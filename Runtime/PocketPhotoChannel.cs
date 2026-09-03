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
        // Must match UnityControlPort in the host's Program.cs. Deliberately
        // NOT 5555-5585 - that's adb's own emulator-console scan range, and a
        // listener there gets hit by adb's periodic probes (see host comment).
        const int Port = 58217;
        const int MaxPayloadSize = 8 * 1024 * 1024;

        readonly ConcurrentQueue<PocketPhotoResponse> _pending = new();
        readonly ConcurrentQueue<byte[]> _outgoing = new();
        readonly object _sendLock = new();

        volatile bool _running;
        volatile TcpClient _client;
        Thread _thread;

        // Guards EnsureStarted() against spawning a second ConnectLoop thread -
        // touched only from the main thread (RequestPhoto/OnDestroy), so a plain
        // bool is enough, no lock needed.
        bool _everStarted;

        /// Starts the connect/retry loop if it isn't already running. Safe to
        /// call repeatedly - only the first call actually starts the thread.
        /// Deliberately NOT started automatically: most sessions never touch the
        /// camera, and holding this connection open (and retrying it) for the
        /// whole game for every player was pure overhead - and console noise -
        /// for anyone who never uses the feature.
        public void EnsureStarted()
        {
            if (_everStarted) return;
            _everStarted = true;
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
            EnsureStarted();

            var client = _client;
            if (client == null || !client.Connected)
            {
                // Connection hasn't been established yet (this is likely the very
                // first request since EnsureStarted() just fired) - queue it so
                // ConnectLoop can flush it the moment the socket connects, rather
                // than silently dropping the first photo request of the session.
                _outgoing.Enqueue(payload);
                return;
            }

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

                    // Flush anything queued while we weren't yet connected -
                    // most commonly the request that triggered EnsureStarted()
                    // in the first place.
                    lock (_sendLock)
                    {
                        while (_outgoing.TryDequeue(out var queued))
                            WriteFramed(stream, queued);
                    }

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
