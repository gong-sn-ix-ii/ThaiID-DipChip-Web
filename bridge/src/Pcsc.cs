using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ThaiIdBridge
{
    /// <summary>Thin P/Invoke layer over the Windows PC/SC API (winscard.dll). Works with any CCID reader.</summary>
    internal static class Pcsc
    {
        public const int SCARD_SCOPE_USER = 0;

        public const int SCARD_SHARE_EXCLUSIVE = 1;
        public const int SCARD_SHARE_SHARED = 2;

        public const int SCARD_PROTOCOL_T0 = 0x1;
        public const int SCARD_PROTOCOL_T1 = 0x2;

        public const int SCARD_LEAVE_CARD = 0;
        public const int SCARD_RESET_CARD = 1;
        public const int SCARD_UNPOWER_CARD = 2;
        public const int SCARD_EJECT_CARD = 3;

        public const int SCARD_STATE_UNAWARE = 0x0000;
        public const int SCARD_STATE_IGNORE = 0x0001;
        public const int SCARD_STATE_CHANGED = 0x0002;
        public const int SCARD_STATE_UNKNOWN = 0x0004;
        public const int SCARD_STATE_UNAVAILABLE = 0x0008;
        public const int SCARD_STATE_EMPTY = 0x0010;
        public const int SCARD_STATE_PRESENT = 0x0020;
        public const int SCARD_STATE_EXCLUSIVE = 0x0080;
        public const int SCARD_STATE_INUSE = 0x0100;
        public const int SCARD_STATE_MUTE = 0x0200;
        public const int SCARD_STATE_UNPOWERED = 0x0400;

        public const int SCARD_S_SUCCESS = 0;
        public const int ERROR_INVALID_HANDLE = 6;
        public const int SCARD_E_CANCELLED = unchecked((int)0x80100002);
        public const int SCARD_E_INVALID_HANDLE = unchecked((int)0x80100003);
        public const int SCARD_E_INSUFFICIENT_BUFFER = unchecked((int)0x80100008);
        public const int SCARD_E_UNKNOWN_READER = unchecked((int)0x80100009);
        public const int SCARD_E_TIMEOUT = unchecked((int)0x8010000A);
        public const int SCARD_E_READER_UNAVAILABLE = unchecked((int)0x80100017);
        public const int SCARD_E_NO_SERVICE = unchecked((int)0x8010001D);
        public const int SCARD_E_SERVICE_STOPPED = unchecked((int)0x8010001E);
        public const int SCARD_E_NO_READERS_AVAILABLE = unchecked((int)0x8010002E);
        public const int SCARD_W_REMOVED_CARD = unchecked((int)0x80100069);

        [StructLayout(LayoutKind.Sequential)]
        public struct SCARD_IO_REQUEST
        {
            public int dwProtocol;
            public int cbPciLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SCARD_READERSTATE
        {
            public IntPtr szReader;
            public IntPtr pvUserData;
            public int dwCurrentState;
            public int dwEventState;
            public int cbAtr;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 36)]
            public byte[] rgbAtr;
        }

        [DllImport("winscard.dll")]
        public static extern int SCardEstablishContext(int dwScope, IntPtr pvReserved1, IntPtr pvReserved2, out IntPtr phContext);

        [DllImport("winscard.dll")]
        public static extern int SCardReleaseContext(IntPtr hContext);

        [DllImport("winscard.dll")]
        public static extern int SCardCancel(IntPtr hContext);

        [DllImport("winscard.dll", EntryPoint = "SCardListReadersW", CharSet = CharSet.Unicode)]
        private static extern int SCardListReaders(IntPtr hContext, string mszGroups, char[] mszReaders, ref int pcchReaders);

        [DllImport("winscard.dll", EntryPoint = "SCardGetStatusChangeW")]
        public static extern int SCardGetStatusChange(IntPtr hContext, int dwTimeout, [In, Out] SCARD_READERSTATE[] rgReaderStates, int cReaders);

        [DllImport("winscard.dll", EntryPoint = "SCardConnectW", CharSet = CharSet.Unicode)]
        public static extern int SCardConnect(IntPtr hContext, string szReader, int dwShareMode, int dwPreferredProtocols, out IntPtr phCard, out int pdwActiveProtocol);

        [DllImport("winscard.dll")]
        public static extern int SCardDisconnect(IntPtr hCard, int dwDisposition);

        [DllImport("winscard.dll")]
        public static extern int SCardBeginTransaction(IntPtr hCard);

        [DllImport("winscard.dll")]
        public static extern int SCardEndTransaction(IntPtr hCard, int dwDisposition);

        [DllImport("winscard.dll", EntryPoint = "SCardStatusW", CharSet = CharSet.Unicode)]
        private static extern int SCardStatus(IntPtr hCard, char[] szReaderName, ref int pcchReaderLen, out int pdwState, out int pdwProtocol, byte[] pbAtr, ref int pcbAtrLen);

        [DllImport("winscard.dll")]
        public static extern int SCardTransmit(IntPtr hCard, ref SCARD_IO_REQUEST pioSendPci, byte[] pbSendBuffer, int cbSendLength, IntPtr pioRecvPci, byte[] pbRecvBuffer, ref int pcbRecvLength);

        /// <summary>Lists reader names. "No readers" is reported as success with an empty list.</summary>
        public static int ListReaders(IntPtr context, out List<string> readers)
        {
            readers = new List<string>();
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int length = 0;
                int rc = SCardListReaders(context, null, null, ref length);
                if (rc == SCARD_E_NO_READERS_AVAILABLE) return SCARD_S_SUCCESS;
                if (rc != SCARD_S_SUCCESS) return rc;

                var buffer = new char[length];
                rc = SCardListReaders(context, null, buffer, ref length);
                if (rc == SCARD_E_INSUFFICIENT_BUFFER) continue; // a reader was plugged in between the two calls
                if (rc == SCARD_E_NO_READERS_AVAILABLE) return SCARD_S_SUCCESS;
                if (rc != SCARD_S_SUCCESS) return rc;

                string all = new string(buffer, 0, Math.Max(0, Math.Min(length, buffer.Length)));
                foreach (string name in all.Split('\0'))
                {
                    if (name.Length > 0) readers.Add(name);
                }
                return SCARD_S_SUCCESS;
            }
            return SCARD_E_INSUFFICIENT_BUFFER;
        }

        public static byte[] GetAtr(IntPtr card)
        {
            var name = new char[512];
            int nameLength = name.Length;
            var atr = new byte[64];
            int atrLength = atr.Length;
            int state, protocol;
            int rc = SCardStatus(card, name, ref nameLength, out state, out protocol, atr, ref atrLength);
            if (rc != SCARD_S_SUCCESS) throw new PcscException("SCardStatus", rc);
            var result = new byte[Math.Max(0, Math.Min(atrLength, atr.Length))];
            Buffer.BlockCopy(atr, 0, result, 0, result.Length);
            return result;
        }

        public static SCARD_READERSTATE NewReaderState(string reader, int currentState)
        {
            var state = new SCARD_READERSTATE();
            state.szReader = Marshal.StringToHGlobalUni(reader);
            state.pvUserData = IntPtr.Zero;
            state.dwCurrentState = currentState;
            state.dwEventState = 0;
            state.cbAtr = 0;
            state.rgbAtr = new byte[36];
            return state;
        }

        public static void FreeReaderStates(SCARD_READERSTATE[] states)
        {
            if (states == null) return;
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].szReader != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(states[i].szReader);
                    states[i].szReader = IntPtr.Zero;
                }
            }
        }

        /// <summary>The Smart Card service stopped/restarted (Windows stops it when the last reader is unplugged).</summary>
        public static bool IsServiceError(int rc)
        {
            return rc == SCARD_E_NO_SERVICE || rc == SCARD_E_SERVICE_STOPPED || rc == SCARD_E_INVALID_HANDLE || rc == ERROR_INVALID_HANDLE;
        }

        public static string Name(int rc)
        {
            string name;
            return Names.TryGetValue(rc, out name) ? name : "SCARD_0x" + rc.ToString("X8");
        }

        public static string Describe(int rc)
        {
            string text;
            Texts.TryGetValue(rc, out text);
            return Name(rc) + " (0x" + rc.ToString("X8") + ")" + (text != null ? " " + text : "");
        }

        private static readonly Dictionary<int, string> Names = new Dictionary<int, string>();
        private static readonly Dictionary<int, string> Texts = new Dictionary<int, string>();

        static Pcsc()
        {
            Add(0x80100001, "SCARD_F_INTERNAL_ERROR", "An internal consistency check failed.");
            Add(0x80100002, "SCARD_E_CANCELLED", "The action was cancelled.");
            Add(0x80100003, "SCARD_E_INVALID_HANDLE", "The supplied handle was invalid.");
            Add(0x80100004, "SCARD_E_INVALID_PARAMETER", "One or more parameters could not be interpreted.");
            Add(0x80100006, "SCARD_E_NO_MEMORY", "Not enough memory.");
            Add(0x80100008, "SCARD_E_INSUFFICIENT_BUFFER", "The data buffer is too small.");
            Add(0x80100009, "SCARD_E_UNKNOWN_READER", "The reader name is not recognized.");
            Add(0x8010000A, "SCARD_E_TIMEOUT", "The timeout expired.");
            Add(0x8010000B, "SCARD_E_SHARING_VIOLATION", "The card is in use by another application.");
            Add(0x8010000C, "SCARD_E_NO_SMARTCARD", "No smart card is in the reader.");
            Add(0x8010000D, "SCARD_E_UNKNOWN_CARD", "The card name is not recognized.");
            Add(0x8010000F, "SCARD_E_PROTO_MISMATCH", "The requested protocol is not supported by the card.");
            Add(0x80100010, "SCARD_E_NOT_READY", "The reader or card is not ready.");
            Add(0x80100013, "SCARD_F_COMM_ERROR", "Communication error with the reader.");
            Add(0x80100016, "SCARD_E_NOT_TRANSACTED", "The command could not be completed in a transaction.");
            Add(0x80100017, "SCARD_E_READER_UNAVAILABLE", "The reader is not available (unplugged?).");
            Add(0x8010001D, "SCARD_E_NO_SERVICE", "The Smart Card service is not running.");
            Add(0x8010001E, "SCARD_E_SERVICE_STOPPED", "The Smart Card service stopped.");
            Add(0x8010001F, "SCARD_E_UNEXPECTED", "An unexpected card error occurred.");
            Add(0x8010002E, "SCARD_E_NO_READERS_AVAILABLE", "No smart card reader is connected.");
            Add(0x8010002F, "SCARD_E_COMM_DATA_LOST", "Communication with the card was lost.");
            Add(0x80100065, "SCARD_W_UNSUPPORTED_CARD", "The card ATR is not supported.");
            Add(0x80100066, "SCARD_W_UNRESPONSIVE_CARD", "The card is not responding (inserted the wrong way?).");
            Add(0x80100067, "SCARD_W_UNPOWERED_CARD", "The card is not powered.");
            Add(0x80100068, "SCARD_W_RESET_CARD", "The card was reset by another application.");
            Add(0x80100069, "SCARD_W_REMOVED_CARD", "The card was removed.");
        }

        private static void Add(uint code, string name, string text)
        {
            int key = unchecked((int)code);
            Names[key] = name;
            Texts[key] = text;
        }
    }
}
