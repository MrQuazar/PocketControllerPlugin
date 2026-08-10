using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace PocketController
{
    /// One phone's worth of sensor state. Values update from a background thread;
    /// the getters below are plain reads and safe to call from anywhere, but only
    /// treat this object as valid data source on the main thread (Unity API rules).
    public sealed class PocketPlayerSensors
    {
        public readonly uint SessionId;
        public ulong CapabilityMask { get; internal set; }
        public float LastPacketRealtime { get; internal set; }

        internal readonly ConcurrentDictionary<PocketSensorId, float[]> Latest = new();

        internal PocketPlayerSensors(uint sessionId) => SessionId = sessionId;

        public bool HasSensor(PocketSensorId id) => (CapabilityMask & (1UL << (int)id)) != 0;

        public bool IsStale(float maxAgeSeconds = 5f) =>
            Time.realtimeSinceStartup - LastPacketRealtime > maxAgeSeconds;

        Vector3 GetVec3(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.zero;

        float GetScalar(PocketSensorId id) =>
            Latest.TryGetValue(id, out var v) && v.Length > 0 ? v[0] : 0f;

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

        // Android's TYPE_ROTATION_VECTOR values are [x, y, z, w, headingAccuracy?] -
        // the optional 5th element is ignored here.
        public Quaternion Rotation =>
            Latest.TryGetValue(PocketSensorId.RotationVector, out var v) && v.Length >= 4
                ? new Quaternion(v[0], v[1], v[2], v[3])
                : Quaternion.identity;

        // Gps carries [lat, lon, altitude, accuracy].
        public Vector2 GpsLatLon =>
            Latest.TryGetValue(PocketSensorId.Gps, out var v) && v.Length >= 2
                ? new Vector2(v[0], v[1]) : Vector2.zero;
    }

    /// Unity-side receiver for PocketControllerHost's UDP sensor relay.
    /// Tracks every phone (SessionId) that's sent data - one PocketPlayerSensors each.
    public class PocketControllerSensors : MonoBehaviour
    {
        public static PocketControllerSensors Instance { get; private set; }

        [SerializeField] int listenPort = 5556;

        public event Action<PocketPlayerSensors> OnPlayerJoined;
        public event Action<PocketPlayerSensors, PocketSensorId> OnSensorUpdated;
        public event Action<PocketPlayerSensors> OnCapabilitiesUpdated;

        readonly Dictionary<uint, PocketPlayerSensors> _players = new();
        public IReadOnlyDictionary<uint, PocketPlayerSensors> Players => _players;

        UdpClient _socket;
        Thread _listenThread;
        volatile bool _running;

        // Background thread hands off work items; Update() drains them on the main
        // thread so events can safely touch Unity objects/scripts.
        enum WorkKind { Sensor, Capabilities }
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
        }

        void OnDestroy()
        {
            _running = false;
            _socket?.Close(); // unblocks the pending Receive below
            _listenThread?.Join(200);
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
                        player.CapabilityMask = (ulong)item.payload;
                        OnCapabilitiesUpdated?.Invoke(player);
                        break;
                }
            }
        }

        void ListenLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                byte[] data;
                try { data = _socket.Receive(ref remote); }
                catch (Exception) { break; } // socket closed on shutdown

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
                    if (!PocketCapabilitiesPacketParser.TryParse(data, out var capSession, out var mask)) return;
                    _pending.Enqueue((capSession, WorkKind.Capabilities, mask));
                    break;
            }
        }

        /// Convenience for single-phone testing: the first session seen, or null.
        public PocketPlayerSensors GetFirstPlayer()
        {
            foreach (var p in _players.Values) return p;
            return null;
        }
    }
}