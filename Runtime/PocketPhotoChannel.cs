using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace PocketController
{
    // Dedicated TCP connection to the host for photo snapshots - the UDP
    // sensor path can't carry a JPEG reliably. Framing matches Framing.cs on
    // the host: [int32 LittleEndian Length][Length bytes of payload].
    internal sealed class PocketPhotoChannel
    {
        const int Port = 5557;
        const int MaxPayloadSize = 8 * 1024 * 1024;

        readonly ConcurrentQueue<PocketPhotoResponse> _pending = new();
        readonly object _sendLock = new();

        volatile bool _running;
        volatile TcpClient _client;
        Thread _thread;

        public void Start()
        {
            _running = true;
            _thread = new Thread(ConnectLoop) { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _client?.Close(); } catch { }
        }

        public bool TryDequeue(out PocketPhotoResponse response) => _pending.TryDequeue(out response);

        public void SendRequest(byte[] payload)
        {
            var client = _client;
            if (client == null || !client.Connected) return;

            try
            {
                lock (_sendLock)
                {
                    WriteFramed(client.GetStream(), payload);
                }
            }
            catch { /* connection dropped mid-send - ConnectLoop will notice and retry */ }
        }

        void ConnectLoop()
        {
            while (_running)
            {
                try
                {
                    using var client = new TcpClient();
                    client.Connect("127.0.0.1", Port);
                    client.NoDelay = true;
                    _client = client;

                    var stream = client.GetStream();
                    while (_running)
                    {
                        if (!TryReadFramed(stream, out var payload)) break;
                        if (payload.Length < 2) continue;
                        if (payload[1] != PocketPacketTypes.PhotoResponse) continue;

                        if (PocketPhotoResponseParser.TryParse(payload, out var response))
                            _pending.Enqueue(response);
                    }
                }
                catch { /* host not up yet, or connection dropped - retry below */ }

                _client = null;
                if (_running) Thread.Sleep(1000);
            }
        }

        static void WriteFramed(NetworkStream stream, byte[] payload)
        {
            var lengthBuffer = BitConverter.GetBytes(payload.Length);
            stream.Write(lengthBuffer, 0, lengthBuffer.Length);
            stream.Write(payload, 0, payload.Length);
        }

        static bool TryReadFramed(NetworkStream stream, out byte[] payload)
        {
            payload = Array.Empty<byte>();

            var lengthBuffer = new byte[4];
            if (!ReadExact(stream, lengthBuffer, 4)) return false;

            int length = BitConverter.ToInt32(lengthBuffer, 0);
            if (length <= 0 || length > MaxPayloadSize) return false;

            var buffer = new byte[length];
            if (!ReadExact(stream, buffer, length)) return false;

            payload = buffer;
            return true;
        }

        static bool ReadExact(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }
    }
}
