using System;
using System.Collections.Generic;

namespace PocketController
{
    // Mirrors SensorId in PacketFormat.cs on the host - byte values MUST match.
    // Append new sensors at the end only. 20 (SignificantMotion) is deliberately
    // skipped, same as on the host/Android side - it's a trigger-type Android
    // sensor that doesn't fit this continuous-reading model.
    public enum PocketSensorId : byte
    {
        Gyroscope = 0,
        Accelerometer = 1,
        Light = 2,
        Gravity = 3,
        LinearAcceleration = 4,
        RotationVector = 5,
        Magnetometer = 6,
        Proximity = 7,
        Gps = 8,
        Pressure = 9,
        Humidity = 10,
        AmbientTemperature = 11,
        StepCounter = 12,
        Touchscreen = 13,
        GameRotationVector = 14,
        GeomagneticRotationVector = 15,
        GyroscopeUncalibrated = 16,
        AccelerometerUncalibrated = 17,
        MagnetometerUncalibrated = 18,
        StepDetector = 19,
        HeartRate = 21
    }

    // Mirrors DeviceCapability in PacketTypes.cs - features that aren't
    // continuous float sensors, each with its own packet type.
    public enum PocketDeviceCapability : byte
    {
        Camera = 0,
        Microphone = 1,
        Nfc = 2,
        // Not a hardware/toggle capability like the others - reflects which
        // transport (WiFi/UDP vs USB/TCP) this phone's session is using.
        UsbConnection = 3
    }

    static class PocketPacketTypes
    {
        public const byte Input = 0;
        public const byte Hello = 1;
        public const byte Ack = 2;
        public const byte Sensor = 3;
        public const byte Capabilities = 4;
        public const byte PhotoRequest = 5;
        public const byte PhotoResponse = 6;
        public const byte AudioRequest = 7;
        public const byte AudioResponse = 8;
        public const byte NfcTagDetected = 9;
    }

    public readonly struct PocketSensorReading
    {
        public readonly PocketSensorId Id;
        public readonly float[] Values;
        public PocketSensorReading(PocketSensorId id, float[] values) { Id = id; Values = values; }
    }

    static class PocketSensorPacketParser
    {
        const int HeaderSize = 11;

        public static bool TryParse(byte[] data, out uint sessionId, out List<PocketSensorReading> readings)
        {
            sessionId = 0;
            readings = null;

            if (data.Length < HeaderSize) return false;

            sessionId = BitConverter.ToUInt32(data, 2);
            byte readingCount = data[10];

            var result = new List<PocketSensorReading>(readingCount);
            int offset = HeaderSize;

            for (int i = 0; i < readingCount; i++)
            {
                if (offset + 2 > data.Length) return false;

                var id = (PocketSensorId)data[offset];
                byte valueCount = data[offset + 1];
                offset += 2;

                int bytesNeeded = valueCount * 4;
                if (offset + bytesNeeded > data.Length) return false;

                var values = new float[valueCount];
                for (int v = 0; v < valueCount; v++)
                {
                    values[v] = BitConverter.ToSingle(data, offset);
                    offset += 4;
                }

                result.Add(new PocketSensorReading(id, values));
            }

            readings = result;
            return true;
        }
    }

    // Capabilities packet grew from 14 to 22 bytes - added ExtendedCapabilityMask.
    static class PocketCapabilitiesPacketParser
    {
        const int Size = 22;

        public static bool TryParse(byte[] data, out uint sessionId, out ulong sensorMask, out ulong extendedCapabilityMask)
        {
            sessionId = 0;
            sensorMask = 0;
            extendedCapabilityMask = 0;

            if (data.Length < Size) return false;

            sessionId = BitConverter.ToUInt32(data, 2);
            sensorMask = BitConverter.ToUInt64(data, 6);
            extendedCapabilityMask = BitConverter.ToUInt64(data, 14);
            return true;
        }
    }

    // Builds an outgoing PhotoRequest - rides the dedicated photo TCP channel
    // (PocketPhotoChannel), not the UDP path. Mirrors PhotoRequestPacket on
    // the host - layout MUST match.
    //
    // byte    Version
    // byte    PacketType (= PhotoRequest)
    // uint32  SessionId    - which phone should capture
    // uint32  RequestId    - echoed back in the matching PhotoResponse
    // byte    CameraFacing (0 = back, 1 = front)
    static class PocketPhotoRequestBuilder
    {
        public const int Size = 11;

        public static byte[] Build(uint sessionId, uint requestId, byte cameraFacing)
        {
            var data = new byte[Size];
            data[0] = 1;
            data[1] = PocketPacketTypes.PhotoRequest;
            BitConverter.GetBytes(sessionId).CopyTo(data, 2);
            BitConverter.GetBytes(requestId).CopyTo(data, 6);
            data[10] = cameraFacing;
            return data;
        }
    }

    public readonly struct PocketPhotoResponse
    {
        public readonly uint SessionId;
        public readonly uint RequestId;
        public readonly byte[] ImageBytes;

        public PocketPhotoResponse(uint sessionId, uint requestId, byte[] imageBytes)
        {
            SessionId = sessionId;
            RequestId = requestId;
            ImageBytes = imageBytes;
        }
    }

    // Parses an incoming PhotoResponse - variable length, no internal length
    // field for the image since the photo channel's framing already delimits
    // the whole payload. Mirrors PhotoResponsePacket on the host.
    static class PocketPhotoResponseParser
    {
        const int HeaderSize = 10;

        public static bool TryParse(byte[] data, out PocketPhotoResponse response)
        {
            response = default;
            if (data.Length < HeaderSize) return false;

            uint sessionId = BitConverter.ToUInt32(data, 2);
            uint requestId = BitConverter.ToUInt32(data, 6);

            var imageBytes = new byte[data.Length - HeaderSize];
            Array.Copy(data, HeaderSize, imageBytes, 0, imageBytes.Length);

            response = new PocketPhotoResponse(sessionId, requestId, imageBytes);
            return true;
        }
    }

    // Parses an unsolicited NfcTagDetected packet - variable length, tag UID only.
    static class PocketNfcTagPacketParser
    {
        const int HeaderSize = 7;

        public static bool TryParse(byte[] data, out uint sessionId, out byte[] tagId)
        {
            sessionId = 0;
            tagId = null;

            if (data.Length < HeaderSize) return false;

            sessionId = BitConverter.ToUInt32(data, 2);
            byte tagIdLength = data[6];

            if (data.Length < HeaderSize + tagIdLength) return false;

            tagId = new byte[tagIdLength];
            Array.Copy(data, HeaderSize, tagId, 0, tagIdLength);
            return true;
        }
    }
}