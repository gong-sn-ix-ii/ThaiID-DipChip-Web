using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ThaiIdBridge
{
    internal sealed class HttpRequest
    {
        private const int MaxHeaderBytes = 16 * 1024;

        public string Method;
        public string Path;
        public string Query;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public bool HeaderHasToken(string name, string token)
        {
            string value = Header(name);
            if (value == null) return false;
            foreach (string part in value.Split(','))
            {
                if (string.Equals(part.Trim(), token, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Reads the request line and headers. Returns null if the client closed the connection.</summary>
        public static HttpRequest Read(Stream input)
        {
            int total = 0;
            string requestLine = ReadLine(input, ref total);
            if (string.IsNullOrEmpty(requestLine)) return null;

            string[] parts = requestLine.Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new IOException("Malformed HTTP request line");

            var request = new HttpRequest();
            request.Method = parts[0].ToUpperInvariant();
            string target = parts[1];
            int q = target.IndexOf('?');
            request.Path = q >= 0 ? target.Substring(0, q) : target;
            request.Query = q >= 0 ? target.Substring(q + 1) : "";

            while (true)
            {
                string line = ReadLine(input, ref total);
                if (line == null) return null;
                if (line.Length == 0) break;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                string existing;
                request.Headers[name] = request.Headers.TryGetValue(name, out existing) ? existing + ", " + value : value;
            }
            return request;
        }

        public void DiscardBody(Stream input)
        {
            string header = Header("Content-Length");
            int remaining;
            if (header == null || !int.TryParse(header, NumberStyles.Integer, CultureInfo.InvariantCulture, out remaining) || remaining <= 0) return;
            if (remaining > 1024 * 1024) throw new IOException("Request body too large");
            var buffer = new byte[4096];
            while (remaining > 0)
            {
                int read = input.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read <= 0) return;
                remaining -= read;
            }
        }

        private static string ReadLine(Stream input, ref int total)
        {
            var bytes = new List<byte>(128);
            while (true)
            {
                int b = input.ReadByte();
                if (b < 0) return null;
                if (++total > MaxHeaderBytes) throw new IOException("HTTP header too large");
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    internal static class Http
    {
        public static void Send(Stream output, int status, string reason, string contentType, byte[] body, string extraHeaders, bool headOnly)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append((body == null ? 0 : body.Length).ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (extraHeaders != null) sb.Append(extraHeaders);
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            output.Write(head, 0, head.Length);
            if (!headOnly && body != null && body.Length > 0) output.Write(body, 0, body.Length);
            output.Flush();
        }

        public static void SendText(Stream output, int status, string reason, string text, string extraHeaders)
        {
            Send(output, status, reason, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), extraHeaders, false);
        }
    }

    /// <summary>Minimal RFC 6455 server-side WebSocket (text messages, ping/pong, close).</summary>
    internal sealed class WebSocketConnection
    {
        private const string AcceptGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const int MaxMessageBytes = 1024 * 1024;

        private readonly TcpClient _client;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly object _writeLock = new object();
        private volatile bool _closed;

        private WebSocketConnection(TcpClient client, Stream input, Stream output)
        {
            _client = client;
            _input = input;
            _output = output;
        }

        public static WebSocketConnection Accept(TcpClient client, Stream input, Stream output, string key)
        {
            string accept;
            using (SHA1 sha1 = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + AcceptGuid)));
            }
            string response =
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(response);
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return new WebSocketConnection(client, input, output);
        }

        /// <summary>Blocks until a complete text message arrives; returns null once the connection is closed.</summary>
        public string ReceiveText()
        {
            var message = new MemoryStream();
            int messageType = -1;
            while (!_closed)
            {
                int b0 = _input.ReadByte();
                if (b0 < 0) return null;
                int b1 = ReadByteOrThrow();
                bool fin = (b0 & 0x80) != 0;
                int opcode = b0 & 0x0F;
                bool masked = (b1 & 0x80) != 0;
                long length = b1 & 0x7F;
                if (length == 126)
                {
                    length = (ReadByteOrThrow() << 8) | ReadByteOrThrow();
                }
                else if (length == 127)
                {
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) + ReadByteOrThrow();
                }
                if (!masked) { Close(1002); return null; }                       // clients must mask (RFC 6455 5.1)
                if (length < 0 || length > MaxMessageBytes) { Close(1009); return null; }

                byte[] mask = ReadExactly(4);
                byte[] payload = ReadExactly((int)length);
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                if (opcode >= 0x8)
                {
                    if (opcode == 0x8) { CloseReply(payload); return null; }
                    if (opcode == 0x9) SendFrame(0xA, payload);
                    continue;
                }
                if (opcode == 0x1 || opcode == 0x2)
                {
                    if (messageType != -1) { Close(1002); return null; }
                    messageType = opcode;
                }
                else if (opcode != 0x0 || messageType == -1)
                {
                    Close(1002);
                    return null;
                }

                if (message.Length + payload.Length > MaxMessageBytes) { Close(1009); return null; }
                message.Write(payload, 0, payload.Length);
                if (!fin) continue;
                if (messageType == 0x2) { Close(1003); return null; }              // binary messages are not used
                return Encoding.UTF8.GetString(message.ToArray());
            }
            return null;
        }

        public void SendText(string text)
        {
            SendFrame(0x1, Encoding.UTF8.GetBytes(text));
        }

        public void Close(int code)
        {
            try { SendFrame(0x8, new[] { (byte)(code >> 8), (byte)code }); } catch (Exception) { }
            _closed = true;
        }

        public void Abort()
        {
            _closed = true;
            try { _client.Close(); } catch (Exception) { }
        }

        private void CloseReply(byte[] payload)
        {
            byte[] code = payload.Length >= 2 ? new[] { payload[0], payload[1] } : new byte[0];
            try { SendFrame(0x8, code); } catch (Exception) { }
            _closed = true;
        }

        private void SendFrame(int opcode, byte[] payload)
        {
            int headerLength = payload.Length < 126 ? 2 : (payload.Length <= 0xFFFF ? 4 : 10);
            var frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | opcode);
            if (headerLength == 2)
            {
                frame[1] = (byte)payload.Length;
            }
            else if (headerLength == 4)
            {
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 127;
                long len = payload.Length;
                for (int i = 0; i < 8; i++) frame[2 + i] = (byte)(len >> (56 - 8 * i));
            }
            Buffer.BlockCopy(payload, 0, frame, headerLength, payload.Length);
            lock (_writeLock)
            {
                if (_closed) return;
                _output.Write(frame, 0, frame.Length);
                _output.Flush();
            }
        }

        private int ReadByteOrThrow()
        {
            int b = _input.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return b;
        }

        private byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int n = _input.Read(buffer, offset, count - offset);
                if (n <= 0) throw new EndOfStreamException();
                offset += n;
            }
            return buffer;
        }
    }
}
