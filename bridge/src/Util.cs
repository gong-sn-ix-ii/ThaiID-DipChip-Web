using System;
using System.Text;

namespace ThaiIdBridge
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        public static bool VerboseEnabled;

        public static void Info(string message) { Write("INFO ", message); }
        public static void Warn(string message) { Write("WARN ", message); }
        public static void Error(string message) { Write("ERROR", message); }
        public static void Debug(string message) { if (VerboseEnabled) Write("DEBUG", message); }

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                try
                {
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + message);
                }
                catch (Exception)
                {
                    // No console attached (started hidden) - logging is best effort.
                }
            }
        }
    }

    /// <summary>Error reported to the web client with a stable code.</summary>
    internal sealed class BridgeException : Exception
    {
        public readonly string Code;

        public BridgeException(string code, string message) : base(message)
        {
            Code = code;
        }
    }

    /// <summary>A failed winscard.dll call.</summary>
    internal sealed class PcscException : Exception
    {
        public readonly int Rc;

        public PcscException(string function, int rc) : base(function + " failed: " + Pcsc.Describe(rc))
        {
            Rc = rc;
        }
    }

    internal static class Hex
    {
        private const string Digits = "0123456789ABCDEF";

        public static string Encode(byte[] data)
        {
            return data == null ? "" : Encode(data, 0, data.Length);
        }

        public static string Encode(byte[] data, int offset, int count)
        {
            var chars = new char[count * 2];
            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                chars[2 * i] = Digits[b >> 4];
                chars[2 * i + 1] = Digits[b & 0x0F];
            }
            return new string(chars);
        }

        public static byte[] Decode(string hex)
        {
            if (hex == null) throw new BridgeException("BAD_REQUEST", "Hex string expected");
            var clean = new StringBuilder(hex.Length);
            foreach (char ch in hex)
            {
                if (ch == ' ' || ch == ':' || ch == '-') continue;
                clean.Append(ch);
            }
            if (clean.Length % 2 != 0) throw new BridgeException("BAD_REQUEST", "Hex string must have an even number of digits");
            var result = new byte[clean.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                int hi = Value(clean[2 * i]);
                int lo = Value(clean[2 * i + 1]);
                if (hi < 0 || lo < 0) throw new BridgeException("BAD_REQUEST", "Invalid hex digit");
                result[i] = (byte)((hi << 4) | lo);
            }
            return result;
        }

        private static int Value(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
