using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace PocketController
{
    // Position is normalized 0..1, origin top-left, Y down - raw Android
    // touch convention, not flipped to Unity's. Flip Y yourself (1f - y) to
    // match Input.mousePosition.
    public readonly struct PocketTouchPoint
    {
        public readonly Vector2 Position;
        public readonly float Pressure;
        public PocketTouchPoint(Vector2 position, float pressure) { Position = position; Pressure = pressure; }
    }

    // Uncalibrated IMU sensors carry both the raw reading and the device's own
    // estimated bias - Android provides both, most games only need Raw.
    public readonly struct PocketUncalibratedReading
    {
        public readonly Vector3 Raw;
        public readonly Vector3 EstimatedBias;
        public PocketUncalibratedReading(Vector3 raw, Vector3 bias) { Raw = raw; EstimatedBias = bias; }
    }

    public sealed class PocketPlayerSensors
    {
        public readonly uint SessionId;
        public ulong CapabilityMask { get; internal set; }
        public ulong ExtendedCapabilityMask { get; internal set; }
        public float LastPacketRealtime { get; internal set; }

        internal readonly ConcurrentDictionary<PocketSensorId, float[]> Latest = new();

        internal PocketPlayerSensors(uint sessionId) => SessionId = sessionId;

        public bool HasSensor(PocketSensorId id) => (CapabilityMask & (1UL << (int)id)) != 0;
        public bool HasCapability(PocketDeviceCapability capability) => (ExtendedCapabilityMask & (1UL << (int)capability)) != 0;

        public bool IsStale(float maxAgeSeconds = 5f) =>
            Time.realtimeSinceStartup - LastPacketRealtime > maxAgeSeconds;

        Vector3 GetVec3(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;

        float GetScalar(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length > 0 ? v[0] : 0f;

        Quaternion GetQuaternion(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length >= 4 ? new Quaternion(v[0], v[1], v[2], v[3]) : Quaternion.identity;

        PocketUncalibratedReading GetUncalibrated(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length >= 6
                ? new PocketUncalibratedReading(new Vector3(v[0], v[1], v[2]), new Vector3(v[3], v[4], v[5]))
                : new PocketUncalibratedReading(Vector3.zero, Vector3.zero);

        public Vector3 Gyroscope => GetVec3(PocketSensorId.Gyroscope);
        public Vector3 Accelerometer => GetVec3(PocketSensorId.Accelerometer);
        public Vector3 Gravity => GetVec3(PocketSensorId.Gravity);
        public Vector3 LinearAcceleration => GetVec3(PocketSensorId.LinearAcceleration);
        public Vector3 Magnetometer => GetVec3(PocketSensorId.Magnetometer);
        public float Light => GetScalar(PocketSensorId.Light);
        public float Proximity => GetScalar(PocketSensorId.Proximity);
        public float Pressure => GetScalar(PocketSensorId.Pressure);
        public float Humidity => GetScalar(PocketSensorId.Humidity);
        public float AmbientTemperature => GetScalar(PocketSensorId.AmbientTemperature);
        public float StepCounter => GetScalar(PocketSensorId.StepCounter);
        public float HeartRate => GetScalar(PocketSensorId.HeartRate);

        public Quaternion Rotation => GetQuaternion(PocketSensorId.RotationVector);
        public Quaternion GameRotation => GetQuaternion(PocketSensorId.GameRotationVector);
        public Quaternion GeomagneticRotation => GetQuaternion(PocketSensorId.GeomagneticRotationVector);

        public PocketUncalibratedReading GyroscopeUncalibrated => GetUncalibrated(PocketSensorId.GyroscopeUncalibrated);
        public PocketUncalibratedReading AccelerometerUncalibrated => GetUncalibrated(PocketSensorId.AccelerometerUncalibrated);
        public PocketUncalibratedReading MagnetometerUncalibrated => GetUncalibrated(PocketSensorId.MagnetometerUncalibrated);

        // True once at least one step has ever fired - NOT "a step just happened
        // this frame". For the actual moment of each step, use OnSensorUpdated
        // and check for id == PocketSensorId.StepDetector instead of polling this.
        public bool StepDetectorFired => Latest.ContainsKey(PocketSensorId.StepDetector);

        public Vector2 GpsLatLon =>
            Latest.TryGetValue(PocketSensorId.Gps, out var v) && v.Length >= 2
                ? new Vector2(v[0], v[1]) : Vector2.zero;

        // Populated only while the phone's Touchscreen toggle is on (up to 10
        // fingers). Check HasSensor(PocketSensorId.Touchscreen) to tell
        // "pad inactive" apart from "pad active, no fingers down".
        public IReadOnlyList<PocketTouchPoint> TouchPoints
        {
            get
            {
                if (!Latest.TryGetValue(PocketSensorId.Touchscreen, out var v) || v.Length < 1)
                    return Array.Empty<PocketTouchPoint>();

                int count = Mathf.RoundToInt(v[0]);
                var points = new List<PocketTouchPoint>(count);
                for (int i = 0; i < count; i++)
                {
                    int offset = 1 + i * 3;
                    if (offset + 2 >= v.Length) break;
                    points.Add(new PocketTouchPoint(new Vector2(v[offset], v[offset + 1]), v[offset + 2]));
                }
                return points;
            }
        }
    }

    public class PocketControllerSensors : MonoBehaviour
    {
        public static PocketControllerSensors Instance { get; private set; }

        [SerializeField] int listenPort = 5556;

        public event Action<PocketPlayerSensors> OnPlayerJoined;
        public event Action<PocketPlayerSensors, PocketSensorId> OnSensorUpdated;
        public event Action<PocketPlayerSensors> OnCapabilitiesUpdated;
        public event Action<PocketPlayerSensors, byte[]> OnNfcTagDetected;
        public event Action<PocketPlayerSensors, Texture2D> OnPhotoReceived;

        readonly Dictionary<uint, PocketPlayerSensors> _players = new();
        public IReadOnlyDictionary<uint, PocketPlayerSensors> Players => _players;

        UdpClient _socket;
        Thread _listenThread;
        volatile bool _running;

        // Photo request/response rides a separate TCP channel, not the UDP
        // socket above - see PocketPhotoChannel. Only touched from the main
        // thread, so no locking needed.
        readonly PocketPhotoChannel _photoChannel = new();
        readonly Dictionary<uint, Action<Texture2D>> _pendingPhotoCallbacks = new();
        uint _nextPhotoRequestId = 1;

        enum WorkKind { Sensor, Capabilities, NfcTag }
        readonly ConcurrentQueue<(uint sessionId, WorkKind kind, object payload)> _pending = new();

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            _socket = new UdpClient(listenPort);
            _running = true;
            _listenThread = new Thread(ListenLoop) { IsBackground = true };
            _listenThread.Start();

            _photoChannel.Start();
        }

        void OnDestroy()
        {
            _running = false;
            _socket?.Close();
            _listenThread?.Join(200);
            _photoChannel.Stop();
        }

        /// <summary>
        /// Requests a JPEG snapshot from the given player's camera. Fires
        /// onReceived with the decoded Texture2D once the phone responds, or
        /// null if the phone declined (camera off, no permission) or the
        /// image failed to decode. Caller should check
        /// player.HasCapability(PocketDeviceCapability.Camera) first.
        /// </summary>
        public void RequestPhoto(PocketPlayerSensors player, Action<Texture2D> onReceived, byte cameraFacing = 0)
        {
            uint requestId = _nextPhotoRequestId++;
            if (onReceived != null)
                _pendingPhotoCallbacks[requestId] = onReceived;

            _photoChannel.SendRequest(PocketPhotoRequestBuilder.Build(player.SessionId, requestId, cameraFacing));
        }

        void Update()
        {
            while (_pending.TryDequeue(out var item))
            {
                if (!_players.TryGetValue(item.sessionId, out var player))
                {
                    player = new PocketPlayerSensors(item.sessionId);
                    _players[item.sessionId] = player;
                    OnPlayerJoined?.Invoke(player);
                }

                player.LastPacketRealtime = Time.realtimeSinceStartup;

                switch (item.kind)
                {
                    case WorkKind.Sensor:
                        var reading = (PocketSensorReading)item.payload;
                        player.Latest[reading.Id] = reading.Values;
                        OnSensorUpdated?.Invoke(player, reading.Id);
                        break;

                    case WorkKind.Capabilities:
                        var (sensorMask, extendedMask) = ((ulong, ulong))item.payload;
                        player.CapabilityMask = sensorMask;
                        player.ExtendedCapabilityMask = extendedMask;
                        OnCapabilitiesUpdated?.Invoke(player);
                        break;

                    case WorkKind.NfcTag:
                        Debug.Log($"[PocketController] Dispatching OnNfcTagDetected for session {item.sessionId}, {(OnNfcTagDetected?.GetInvocationList().Length ?? 0)} subscriber(s)");
                        OnNfcTagDetected?.Invoke(player, (byte[])item.payload);
                        break;
                }
            }

            while (_photoChannel.TryDequeue(out var response))
            {
                Texture2D texture = null;
                if (response.ImageBytes.Length > 0)
                {
                    texture = new Texture2D(2, 2);
                    if (!ImageConversion.LoadImage(texture, response.ImageBytes))
                        texture = null;
                }

                if (_pendingPhotoCallbacks.TryGetValue(response.RequestId, out var callback))
                {
                    _pendingPhotoCallbacks.Remove(response.RequestId);
                    callback?.Invoke(texture);
                }

                if (texture != null && _players.TryGetValue(response.SessionId, out var photoPlayer))
                    OnPhotoReceived?.Invoke(photoPlayer, texture);
            }
        }

        void ListenLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _socket.Receive(ref remote); }
                catch (Exception) { break; }

                try { Parse(data); }
                catch (Exception e) { Debug.LogWarning($"PocketController: malformed packet ({e.Message})"); }
            }
        }

        void Parse(byte[] data)
        {
            if (data.Length < 2) return;

            switch (data[1])
            {
                case PocketPacketTypes.Sensor:
                    if (!PocketSensorPacketParser.TryParse(data, out var sessionId, out var readings)) return;
                    foreach (var reading in readings)
                        _pending.Enqueue((sessionId, WorkKind.Sensor, reading));
                    break;

                case PocketPacketTypes.Capabilities:
                    if (!PocketCapabilitiesPacketParser.TryParse(data, out var capSession, out var mask, out var extendedMask)) return;
                    _pending.Enqueue((capSession, WorkKind.Capabilities, (mask, extendedMask)));
                    break;

                case PocketPacketTypes.NfcTagDetected:
                    if (!PocketNfcTagPacketParser.TryParse(data, out var nfcSession, out var tagId))
                    {
                        Debug.LogWarning($"[PocketController] Failed to parse NfcTagDetected packet ({data.Length} bytes) - malformed?");
                        return;
                    }
                    Debug.Log($"[PocketController] NfcTagDetected received on UDP listener: session={nfcSession} tagIdLength={tagId.Length}");
                    _pending.Enqueue((nfcSession, WorkKind.NfcTag, tagId));
                    break;
            }
        }

        public PocketPlayerSensors GetFirstPlayer()
        {
            foreach (var p in _players.Values) return p;
            return null;
        }
    }
}