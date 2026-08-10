using System;
using System.Collections.Generic;

namespace PocketController
{
    // Mirrors SensorId in PacketFormat.cs on the host - byte values MUST match.
    // Append new sensors at the end only.
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
        StepCounter = 12
    }

    static class PocketPacketTypes
    {
        public const byte Input = 0;
        public const byte Hello = 1;
        public const byte Ack = 2;
        public const byte Sensor = 3;
        public const byte Capabilities = 4;
    }

    public readonly struct PocketSensorReading
    {
        public readonly PocketSensorId Id;
        public readonly float[] Values;
        public PocketSensorReading(PocketSensorId id, float[] values) { Id = id; Values = values; }
    }

    // Parses a Sensor packet (PacketTypes.Sensor) body.
    static class PocketSensorPacketParser
    {
        const int HeaderSize = 11; // version + type + sessionId(4) + sequence(4) + count

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

    // Parses a Capabilities packet (PacketTypes.Capabilities) body.
    static class PocketCapabilitiesPacketParser
    {
        const int Size = 14;

        public static bool TryParse(byte[] data, out uint sessionId, out ulong sensorMask)
        {
            sessionId = 0;
            sensorMask = 0;

            if (data.Length < Size) return false;

            sessionId = BitConverter.ToUInt32(data, 2);
            sensorMask = BitConverter.ToUInt64(data, 6);
            return true;
        }
    }
}