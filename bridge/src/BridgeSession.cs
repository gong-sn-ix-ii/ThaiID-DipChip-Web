using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace ThaiIdBridge
{
    /// <summary>
    /// One WebSocket client. JSON-RPC style protocol:
    ///   request  {"id":1,"method":"transmit","params":{"handle":"1","apdu":"00A40400..."}}
    ///   response {"id":1,"result":{...}}   or   {"id":1,"error":{"code":"SCARD_W_REMOVED_CARD","message":"..."}}
    ///   events   {"event":"hello","data":{...}}  {"event":"status","data":{"service":"ready","readers":[...]}}
    /// Methods: ping, getStatus, listReaders, connect, transmit, beginTransaction, endTransaction, disconnect, simulator.
    /// Each session owns its own PC/SC context and card handles; everything is released when the socket closes.
    /// </summary>
    internal sealed class BridgeSession : IDisposable
    {
        private sealed class Card
        {
            public string Id;
            public string Reader;
            public IntPtr Handle;
            public int Protocol;
            public bool IsVirtual;
            public bool InTransaction;
        }

        private const int MaxCardsPerSession = 16;

        private readonly int _id;
        private readonly WebSocketConnection _ws;
        private readonly PcscMonitor _monitor;
        private readonly VirtualThaiIdCard _virtualCard;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly Dictionary<string, Card> _cards = new Dictionary<string, Card>();
        private readonly byte[] _receiveBuffer = new byte[65538];
        private IntPtr _context = IntPtr.Zero;
        private int _nextCardId = 1;

        public BridgeSession(int id, WebSocketConnection ws, PcscMonitor monitor, VirtualThaiIdCard virtualCard)
        {
            _id = id;
            _ws = ws;
            _monitor = monitor;
            _virtualCard = virtualCard;
            _json.MaxJsonLength = 8 * 1024 * 1024;
        }

        public void SendRaw(string json)
        {
            try { _ws.SendText(json); }
            catch (Exception) { /* client went away - the read loop notices and cleans up */ }
        }

        public void SendHello()
        {
            var data = new Dictionary<string, object>();
            data["app"] = Program.AppName;
            data["version"] = Program.Version;
            data["protocol"] = 1;
            data["simulator"] = _virtualCard != null;
            var evt = new Dictionary<string, object>();
            evt["event"] = "hello";
            evt["data"] = data;
            SendRaw(_json.Serialize(evt));
        }

        public void Abort()
        {
            _ws.Abort();
        }

        public void Run()
        {
            while (true)
            {
                string text = _ws.ReceiveText();
                if (text == null) return;
                HandleMessage(text);
            }
        }

        private void HandleMessage(string text)
        {
            object id = null;
            string method = null;
            try
            {
                Dictionary<string, object> message;
                try
                {
                    message = _json.DeserializeObject(text) as Dictionary<string, object>;
                }
                catch (ArgumentException ex)
                {
                    throw new BridgeException("BAD_REQUEST", "Invalid JSON: " + ex.Message);
                }
                if (message == null) throw new BridgeException("BAD_REQUEST", "Message must be a JSON object");
                message.TryGetValue("id", out id);
                object m;
                message.TryGetValue("method", out m);
                method = m as string;
                if (string.IsNullOrEmpty(method)) throw new BridgeException("BAD_REQUEST", "Missing 'method'");
                object p;
                message.TryGetValue("params", out p);
                var prms = p as Dictionary<string, object> ?? new Dictionary<string, object>();

                object result = Dispatch(method, prms);
                var response = new Dictionary<string, object>();
                response["id"] = id;
                response["result"] = result;
                SendRaw(_json.Serialize(response));
            }
            catch (PcscException ex)
            {
                Log.Debug("#" + _id + " " + method + " -> " + Pcsc.Name(ex.Rc));
                SendError(id, Pcsc.Name(ex.Rc), ex.Message);
            }
            catch (BridgeException ex)
            {
                Log.Debug("#" + _id + " " + method + " -> " + ex.Code);
                SendError(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("#" + _id + " " + method + " failed: " + ex);
                SendError(id, "INTERNAL_ERROR", ex.Message);
            }
        }

        private void SendError(object id, string code, string message)
        {
            var error = new Dictionary<string, object>();
            error["code"] = code;
            error["message"] = message;
            var response = new Dictionary<string, object>();
            response["id"] = id;
            response["error"] = error;
            SendRaw(_json.Serialize(response));
        }

        private object Dispatch(string method, Dictionary<string, object> p)
        {
            switch (method)
            {
                case "ping": return "pong";
                case "getStatus": return _monitor.Snapshot;
                case "listReaders": return ListReaders();
                case "connect": return Connect(p);
                case "transmit": return Transmit(p);
                case "beginTransaction": return BeginTransaction(p);
                case "endTransaction": return EndTransaction(p);
                case "disconnect": return Disconnect(p);
                case "simulator": return Simulator(p);
                default: throw new BridgeException("UNKNOWN_METHOD", "Unknown method '" + method + "'");
            }
        }

        private object ListReaders()
        {
            var result = new List<object>();
            List<string> names;
            int rc = TryListReaders(out names);
            if (rc == Pcsc.SCARD_S_SUCCESS)
            {
                foreach (string name in names) result.Add(name);
            }
            else if (!Pcsc.IsServiceError(rc))
            {
                throw new PcscException("SCardListReaders", rc);
            }
            if (_virtualCard != null) result.Add(VirtualThaiIdCard.ReaderName);
            return result;
        }

        private object Connect(Dictionary<string, object> p)
        {
            string reader = RequireString(p, "reader");
            int shareMode;
            switch (OptString(p, "shareMode", "shared"))
            {
                case "shared": shareMode = Pcsc.SCARD_SHARE_SHARED; break;
                case "exclusive": shareMode = Pcsc.SCARD_SHARE_EXCLUSIVE; break;
                default: throw new BridgeException("BAD_REQUEST", "shareMode must be 'shared' or 'exclusive'");
            }
            int protocols;
            switch (OptString(p, "protocol", "any"))
            {
                case "T0": protocols = Pcsc.SCARD_PROTOCOL_T0; break;
                case "T1": protocols = Pcsc.SCARD_PROTOCOL_T1; break;
                case "any": protocols = Pcsc.SCARD_PROTOCOL_T0 | Pcsc.SCARD_PROTOCOL_T1; break;
                default: throw new BridgeException("BAD_REQUEST", "protocol must be 'T0', 'T1' or 'any'");
            }
            if (_cards.Count >= MaxCardsPerSession)
                throw new BridgeException("TOO_MANY_HANDLES", "Too many open card connections - call disconnect first");

            var card = new Card();
            card.Id = (_nextCardId++).ToString(CultureInfo.InvariantCulture);
            card.Reader = reader;
            byte[] atr;

            if (_virtualCard != null && reader == VirtualThaiIdCard.ReaderName)
            {
                _virtualCard.Connect();
                card.IsVirtual = true;
                card.Protocol = Pcsc.SCARD_PROTOCOL_T0;
                atr = _virtualCard.GetAtr();
            }
            else
            {
                IntPtr handle = IntPtr.Zero;
                int active = 0;
                IntPtr context;
                int rc = TryGetContext(out context);
                if (rc == Pcsc.SCARD_S_SUCCESS) rc = Pcsc.SCardConnect(context, reader, shareMode, protocols, out handle, out active);
                if (Pcsc.IsServiceError(rc))
                {
                    ResetContext();
                    rc = TryGetContext(out context);
                    if (rc == Pcsc.SCARD_S_SUCCESS) rc = Pcsc.SCardConnect(context, reader, shareMode, protocols, out handle, out active);
                }
                if (rc != Pcsc.SCARD_S_SUCCESS) throw new PcscException("SCardConnect", rc);

                card.Handle = handle;
                card.Protocol = active;
                try
                {
                    atr = Pcsc.GetAtr(handle);
                }
                catch (Exception)
                {
                    Pcsc.SCardDisconnect(handle, Pcsc.SCARD_LEAVE_CARD);
                    throw;
                }
            }

            _cards[card.Id] = card;
            Log.Info("#" + _id + " connected to '" + reader + "' (" + ProtocolName(card.Protocol) + ", ATR " + Hex.Encode(atr) + ")");
            var result = new Dictionary<string, object>();
            result["handle"] = card.Id;
            result["protocol"] = ProtocolName(card.Protocol);
            result["atr"] = Hex.Encode(atr);
            return result;
        }

        private object Transmit(Dictionary<string, object> p)
        {
            Card card = RequireCard(p);
            byte[] apdu = Hex.Decode(RequireString(p, "apdu"));
            if (apdu.Length < 4 || apdu.Length > 65544) throw new BridgeException("BAD_REQUEST", "APDU must be 4..65544 bytes");

            byte[] response;
            if (card.IsVirtual)
            {
                response = _virtualCard.Transmit(apdu);
            }
            else
            {
                var pci = new Pcsc.SCARD_IO_REQUEST();
                pci.dwProtocol = card.Protocol;
                pci.cbPciLength = 8;
                int length = _receiveBuffer.Length;
                int rc = Pcsc.SCardTransmit(card.Handle, ref pci, apdu, apdu.Length, IntPtr.Zero, _receiveBuffer, ref length);
                if (rc != Pcsc.SCARD_S_SUCCESS) throw new PcscException("SCardTransmit", rc);
                response = new byte[length];
                Buffer.BlockCopy(_receiveBuffer, 0, response, 0, length);
            }

            if (Log.VerboseEnabled)
            {
                // Only the command and the status word are logged - never the card's data.
                string sw = response.Length >= 2 ? Hex.Encode(response, response.Length - 2, 2) : "-";
                Log.Debug("#" + _id + " > " + Hex.Encode(apdu) + "   < " + (response.Length - 2) + " bytes, SW " + sw);
            }
            var result = new Dictionary<string, object>();
            result["response"] = Hex.Encode(response);
            return result;
        }

        private object BeginTransaction(Dictionary<string, object> p)
        {
            Card card = RequireCard(p);
            if (!card.IsVirtual)
            {
                int rc = Pcsc.SCardBeginTransaction(card.Handle);
                if (rc != Pcsc.SCARD_S_SUCCESS) throw new PcscException("SCardBeginTransaction", rc);
            }
            card.InTransaction = true;
            return true;
        }

        private object EndTransaction(Dictionary<string, object> p)
        {
            Card card = RequireCard(p);
            int disposition = ParseDisposition(p);
            if (!card.IsVirtual && card.InTransaction)
            {
                int rc = Pcsc.SCardEndTransaction(card.Handle, disposition);
                if (rc != Pcsc.SCARD_S_SUCCESS) throw new PcscException("SCardEndTransaction", rc);
            }
            card.InTransaction = false;
            return true;
        }

        private object Disconnect(Dictionary<string, object> p)
        {
            Card card = RequireCard(p);
            int disposition = ParseDisposition(p);
            _cards.Remove(card.Id);
            if (card.IsVirtual)
            {
                _virtualCard.Disconnect();
                return true;
            }
            if (card.InTransaction) Pcsc.SCardEndTransaction(card.Handle, Pcsc.SCARD_LEAVE_CARD);
            int rc = Pcsc.SCardDisconnect(card.Handle, disposition);
            if (rc != Pcsc.SCARD_S_SUCCESS && rc != Pcsc.SCARD_W_REMOVED_CARD) throw new PcscException("SCardDisconnect", rc);
            return true;
        }

        private object Simulator(Dictionary<string, object> p)
        {
            if (_virtualCard == null)
                throw new BridgeException("SIMULATOR_DISABLED", "Start ThaiIdBridge with --simulate to use the virtual reader");
            switch (OptString(p, "action", "toggle"))
            {
                case "insert": _virtualCard.Insert(); break;
                case "remove": _virtualCard.Remove(); break;
                case "toggle": _virtualCard.Toggle(); break;
                default: throw new BridgeException("BAD_REQUEST", "action must be 'insert', 'remove' or 'toggle'");
            }
            _monitor.Refresh();
            var result = new Dictionary<string, object>();
            result["inserted"] = _virtualCard.Inserted;
            return result;
        }

        private int TryListReaders(out List<string> names)
        {
            names = null;
            IntPtr context;
            int rc = TryGetContext(out context);
            if (rc == Pcsc.SCARD_S_SUCCESS) rc = Pcsc.ListReaders(context, out names);
            if (Pcsc.IsServiceError(rc))
            {
                ResetContext();
                rc = TryGetContext(out context);
                if (rc == Pcsc.SCARD_S_SUCCESS) rc = Pcsc.ListReaders(context, out names);
            }
            return rc;
        }

        private int TryGetContext(out IntPtr context)
        {
            if (_context == IntPtr.Zero)
            {
                IntPtr created;
                int rc = Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out created);
                if (rc != Pcsc.SCARD_S_SUCCESS)
                {
                    context = IntPtr.Zero;
                    return rc;
                }
                _context = created;
            }
            context = _context;
            return Pcsc.SCARD_S_SUCCESS;
        }

        /// <summary>The Smart Card service restarted: old context and card handles are dead.</summary>
        private void ResetContext()
        {
            var dead = new List<string>();
            foreach (KeyValuePair<string, Card> kv in _cards)
            {
                if (!kv.Value.IsVirtual) dead.Add(kv.Key);
            }
            foreach (string key in dead) _cards.Remove(key);
            if (_context != IntPtr.Zero)
            {
                Pcsc.SCardReleaseContext(_context);
                _context = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            foreach (Card card in _cards.Values)
            {
                try
                {
                    if (card.IsVirtual)
                    {
                        _virtualCard.Disconnect();
                        continue;
                    }
                    if (card.InTransaction) Pcsc.SCardEndTransaction(card.Handle, Pcsc.SCARD_LEAVE_CARD);
                    Pcsc.SCardDisconnect(card.Handle, Pcsc.SCARD_LEAVE_CARD);
                }
                catch (Exception) { }
            }
            _cards.Clear();
            if (_context != IntPtr.Zero)
            {
                Pcsc.SCardReleaseContext(_context);
                _context = IntPtr.Zero;
            }
        }

        private Card RequireCard(Dictionary<string, object> p)
        {
            object value;
            p.TryGetValue("handle", out value);
            string id = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            Card card;
            if (string.IsNullOrEmpty(id) || !_cards.TryGetValue(id, out card))
                throw new BridgeException("INVALID_HANDLE", "Unknown card handle '" + id + "' (connect first)");
            return card;
        }

        private static int ParseDisposition(Dictionary<string, object> p)
        {
            switch (OptString(p, "disposition", "leave"))
            {
                case "leave": return Pcsc.SCARD_LEAVE_CARD;
                case "reset": return Pcsc.SCARD_RESET_CARD;
                case "unpower": return Pcsc.SCARD_UNPOWER_CARD;
                case "eject": return Pcsc.SCARD_EJECT_CARD;
                default: throw new BridgeException("BAD_REQUEST", "disposition must be leave, reset, unpower or eject");
            }
        }

        private static string RequireString(Dictionary<string, object> p, string name)
        {
            object value;
            string s = p.TryGetValue(name, out value) ? value as string : null;
            if (string.IsNullOrEmpty(s)) throw new BridgeException("BAD_REQUEST", "Missing string parameter '" + name + "'");
            return s;
        }

        private static string OptString(Dictionary<string, object> p, string name, string fallback)
        {
            object value;
            string s = p.TryGetValue(name, out value) ? value as string : null;
            return string.IsNullOrEmpty(s) ? fallback : s;
        }

        private static string ProtocolName(int protocol)
        {
            if (protocol == Pcsc.SCARD_PROTOCOL_T0) return "T0";
            if (protocol == Pcsc.SCARD_PROTOCOL_T1) return "T1";
            return "0x" + protocol.ToString("X");
        }
    }
}
