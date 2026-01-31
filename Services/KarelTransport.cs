// KarelTransport.cs
using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwinSync_Gateway.Models;

namespace TwinSync_Gateway.Services
{
    public sealed class KarelTransport : IRobotTransport
    {
        public string Name => "KAREL";

        private TcpClient? _client;
        private NetworkStream? _stream;

        // Read buffering
        private byte[]? _buf;
        private int _bufLen;
        private int _bufPos;

        // Reuse builder to avoid per-line allocations
        private readonly StringBuilder _lineSb = new();

        public async Task ConnectAsync(RobotConfig config, CancellationToken ct)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));

            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(config.IpAddress, config.KarelPort, connectCts.Token).ConfigureAwait(false);

            _stream = _client.GetStream();

            // Allocate read buffer once per connection
            _buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
            _bufLen = 0;
            _bufPos = 0;

            // HELLO handshake (length-framed command -> line response)
            using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            helloCts.CancelAfter(TimeSpan.FromSeconds(5));

            await SendCommandAsync("HELLO", helloCts.Token).ConfigureAwait(false);

            while (true)
            {
                var line = await ReadLineAsync(helloCts.Token).ConfigureAwait(false);
                var s = line.Trim();

                if (string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase))
                    break;

                // Ignore telemetry lines that may arrive early
                if (s.Contains("J=", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.Equals(s, "END", StringComparison.OrdinalIgnoreCase))
                    continue;

                throw new IOException($"Unexpected HELLO response: '{line}'");
            }
        }

        public async Task SendCommandAsync(string cmd, CancellationToken ct)
        {
            var s = _stream;
            var c = _client;
            if (s == null || c == null || !c.Connected)
                throw new IOException("Not connected.");

            await WriteFrameAsync(s, cmd, ct).ConfigureAwait(false);
        }

        public Task<string> ReadLineAsync(CancellationToken ct)
        {
            var s = _stream ?? throw new InvalidOperationException("Not connected.");
            return ReadLineBufferedAsync(s, ct);
        }

        private static async Task WriteFrameAsync(NetworkStream s, string payload, CancellationToken ct)
        {
            var body = Encoding.ASCII.GetBytes(payload);
            if (body.Length > 9999)
                throw new ArgumentOutOfRangeException(nameof(payload), "Payload too long for D4 header.");

            var lenStr = body.Length.ToString("D4");

            var hdr = new byte[4];
            hdr[0] = (byte)lenStr[0];
            hdr[1] = (byte)lenStr[1];
            hdr[2] = (byte)lenStr[2];
            hdr[3] = (byte)lenStr[3];

            await s.WriteAsync(hdr, 0, 4, ct).ConfigureAwait(false);
            await s.WriteAsync(body, 0, body.Length, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }

        private async Task<string> ReadLineBufferedAsync(NetworkStream s, CancellationToken ct)
        {
            if (_buf == null) throw new InvalidOperationException("Not connected.");

            _lineSb.Clear();

            while (true)
            {
                // Refill buffer if empty
                if (_bufPos >= _bufLen)
                {
                    _bufPos = 0;
                    _bufLen = await s.ReadAsync(_buf.AsMemory(0, _buf.Length), ct).ConfigureAwait(false);
                    if (_bufLen == 0) throw new IOException("Socket closed");
                }

                // Consume bytes until CR or LF
                while (_bufPos < _bufLen)
                {
                    byte b = _buf[_bufPos++];

                    if (b == (byte)'\r' || b == (byte)'\n')
                    {
                        // Skip empty line fragments (handles \r\n and repeated delimiters)
                        if (_lineSb.Length == 0) continue;
                        return _lineSb.ToString();
                    }

                    _lineSb.Append((char)b); // ASCII
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _stream?.Close(); } catch { }
            try { _client?.Close(); } catch { }

            try { _stream?.Dispose(); } catch { }
            try { _client?.Dispose(); } catch { }

            _stream = null;
            _client = null;

            if (_buf != null)
            {
                ArrayPool<byte>.Shared.Return(_buf);
                _buf = null;
            }

            _bufLen = 0;
            _bufPos = 0;

            return ValueTask.CompletedTask;
        }
    }
}
