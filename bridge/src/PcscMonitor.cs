using System;
using System.Collections.Generic;
using System.Threading;
using System.Web.Script.Serialization;

namespace ThaiIdBridge
{
    internal sealed class ReaderInfo
    {
        public string Name;
        public int State;
        public int EventCount;
        public byte[] Atr = new byte[0];
        public bool IsVirtual;

        public bool CardPresent { get { return (State & Pcsc.SCARD_STATE_PRESENT) != 0; } }
        public bool Mute { get { return (State & Pcsc.SCARD_STATE_MUTE) != 0; } }

        public Dictionary<string, object> ToJson()
        {
            var states = new List<object>();
            AddFlag(states, Pcsc.SCARD_STATE_EMPTY, "empty");
            AddFlag(states, Pcsc.SCARD_STATE_PRESENT, "present");
            AddFlag(states, Pcsc.SCARD_STATE_INUSE, "inuse");
            AddFlag(states, Pcsc.SCARD_STATE_EXCLUSIVE, "exclusive");
            AddFlag(states, Pcsc.SCARD_STATE_MUTE, "mute");
            AddFlag(states, Pcsc.SCARD_STATE_UNPOWERED, "unpowered");
            AddFlag(states, Pcsc.SCARD_STATE_UNAVAILABLE, "unavailable");

            var json = new Dictionary<string, object>();
            json["name"] = Name;
            json["cardPresent"] = CardPresent;
            json["mute"] = Mute;
            json["states"] = states;
            json["atr"] = CardPresent && Atr.Length > 0 ? Hex.Encode(Atr) : null;
            json["eventCount"] = EventCount;
            json["virtual"] = IsVirtual;
            return json;
        }

        private void AddFlag(List<object> list, int flag, string name)
        {
            if ((State & flag) != 0) list.Add(name);
        }
    }

    /// <summary>
    /// Watches readers and cards with SCardGetStatusChange (incl. the \\?PnP?\Notification pseudo reader for
    /// reader plug/unplug) and publishes a "status" event whenever something visible changes.
    /// </summary>
    internal sealed class PcscMonitor
    {
        private const string PnpNotification = @"\\?PnP?\Notification";

        private readonly object _publishLock = new object();
        private readonly object _contextLock = new object();
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly VirtualThaiIdCard _virtualCard;
        private volatile bool _stopping;
        private IntPtr _context = IntPtr.Zero;
        private Thread _thread;

        private bool _serviceReady;
        private int _serviceError;
        private List<ReaderInfo> _realReaders = new List<ReaderInfo>();
        private Dictionary<string, object> _snapshot;
        private string _eventJson;
        private Dictionary<string, ReaderInfo> _lastByName = new Dictionary<string, ReaderInfo>();

        /// <summary>Raised (under <see cref="SyncRoot"/>) with the serialized status event.</summary>
        public event Action<string> StatusChanged;

        public PcscMonitor(VirtualThaiIdCard virtualCard)
        {
            _virtualCard = virtualCard;
            Publish(false, 0, new List<ReaderInfo>());
        }

        public object SyncRoot { get { return _publishLock; } }

        public Dictionary<string, object> Snapshot
        {
            get { lock (_publishLock) { return _snapshot; } }
        }

        public string StatusEventJson
        {
            get { lock (_publishLock) { return _eventJson; } }
        }

        public void Start()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "pcsc-monitor";
            _thread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            _stopEvent.Set();
            lock (_contextLock)
            {
                if (_context != IntPtr.Zero) Pcsc.SCardCancel(_context);
            }
        }

        /// <summary>Re-publish (used when the virtual card is inserted/removed).</summary>
        public void Refresh()
        {
            lock (_publishLock)
            {
                Publish(_serviceReady, _serviceError, _realReaders);
            }
        }

        private void Run()
        {
            int lastError = 0;
            while (!_stopping)
            {
                IntPtr context;
                int rc = Pcsc.SCardEstablishContext(Pcsc.SCARD_SCOPE_USER, IntPtr.Zero, IntPtr.Zero, out context);
                if (rc != Pcsc.SCARD_S_SUCCESS)
                {
                    // Windows stops the Smart Card service when no reader is plugged in; poll until it is back.
                    if (rc != lastError)
                    {
                        Log.Debug("SCardEstablishContext: " + Pcsc.Describe(rc));
                        lastError = rc;
                    }
                    Publish(false, rc, new List<ReaderInfo>());
                    _stopEvent.WaitOne(1000);
                    continue;
                }

                lastError = 0;
                lock (_contextLock) { _context = context; }
                try
                {
                    MonitorReaders(context);
                }
                catch (Exception ex)
                {
                    Log.Error("Reader monitor error: " + ex.Message);
                    _stopEvent.WaitOne(1000);
                }
                finally
                {
                    lock (_contextLock) { _context = IntPtr.Zero; }
                    Pcsc.SCardReleaseContext(context);
                }
                _stopEvent.WaitOne(300);
            }
        }

        private void MonitorReaders(IntPtr context)
        {
            while (!_stopping)
            {
                List<string> names;
                int rc = Pcsc.ListReaders(context, out names);
                if (rc != Pcsc.SCARD_S_SUCCESS)
                {
                    Log.Debug("SCardListReaders: " + Pcsc.Describe(rc));
                    Publish(false, rc, new List<ReaderInfo>());
                    return; // re-establish the context
                }

                int pnp = names.Count;
                var states = new Pcsc.SCARD_READERSTATE[names.Count + 1];
                try
                {
                    for (int i = 0; i < names.Count; i++) states[i] = Pcsc.NewReaderState(names[i], Pcsc.SCARD_STATE_UNAWARE);
                    // On Windows the high word of the PnP pseudo-reader state carries the reader count.
                    states[pnp] = Pcsc.NewReaderState(PnpNotification, names.Count << 16);

                    int timeout = 0;
                    bool rebuild = false;
                    while (!_stopping && !rebuild)
                    {
                        rc = Pcsc.SCardGetStatusChange(context, timeout, states, states.Length);
                        if (rc == Pcsc.SCARD_E_TIMEOUT)
                        {
                            if (timeout == 0) Publish(true, 0, Collect(states, names));
                            timeout = 2000;
                            continue;
                        }
                        if (rc == Pcsc.SCARD_E_CANCELLED) return;
                        if (rc == Pcsc.SCARD_E_UNKNOWN_READER || rc == Pcsc.SCARD_E_READER_UNAVAILABLE)
                        {
                            rebuild = true;
                            break;
                        }
                        if (rc != Pcsc.SCARD_S_SUCCESS)
                        {
                            Log.Debug("SCardGetStatusChange: " + Pcsc.Describe(rc));
                            Publish(false, rc, new List<ReaderInfo>());
                            return;
                        }

                        timeout = 2000;
                        bool pnpChanged = (states[pnp].dwEventState & Pcsc.SCARD_STATE_CHANGED) != 0;
                        for (int i = 0; i < states.Length; i++)
                        {
                            if (i < pnp && (states[i].dwEventState & (Pcsc.SCARD_STATE_UNKNOWN | Pcsc.SCARD_STATE_IGNORE)) != 0)
                                rebuild = true;
                            states[i].dwCurrentState = states[i].dwEventState & ~Pcsc.SCARD_STATE_CHANGED;
                        }

                        bool changed = Publish(true, 0, Collect(states, names));
                        if (pnpChanged && !rebuild)
                        {
                            List<string> current;
                            if (Pcsc.ListReaders(context, out current) != Pcsc.SCARD_S_SUCCESS || !SameNames(current, names))
                                rebuild = true;
                        }
                        if (!changed && !rebuild) _stopEvent.WaitOne(50); // guard against busy loops on odd drivers
                    }
                }
                finally
                {
                    Pcsc.FreeReaderStates(states);
                }
            }
        }

        private static List<ReaderInfo> Collect(Pcsc.SCARD_READERSTATE[] states, List<string> names)
        {
            var list = new List<ReaderInfo>();
            for (int i = 0; i < names.Count; i++)
            {
                Pcsc.SCARD_READERSTATE s = states[i];
                var info = new ReaderInfo();
                info.Name = names[i];
                info.State = s.dwEventState & 0xFFFF;
                info.EventCount = (s.dwEventState >> 16) & 0xFFFF;
                int n = Math.Max(0, Math.Min(s.cbAtr, s.rgbAtr == null ? 0 : s.rgbAtr.Length));
                info.Atr = new byte[n];
                if (n > 0) Buffer.BlockCopy(s.rgbAtr, 0, info.Atr, 0, n);
                list.Add(info);
            }
            return list;
        }

        private static bool SameNames(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <returns>true when the published status changed.</returns>
        private bool Publish(bool serviceReady, int error, List<ReaderInfo> realReaders)
        {
            lock (_publishLock)
            {
                _serviceReady = serviceReady;
                _serviceError = error;
                _realReaders = realReaders;

                var all = new List<ReaderInfo>(realReaders);
                if (_virtualCard != null) all.Add(_virtualCard.GetReaderInfo());

                var readers = new List<object>();
                foreach (ReaderInfo r in all) readers.Add(r.ToJson());
                var data = new Dictionary<string, object>();
                data["service"] = serviceReady ? "ready" : "unavailable";
                if (!serviceReady && error != 0) data["error"] = Pcsc.Name(error);
                data["readers"] = readers;

                var evt = new Dictionary<string, object>();
                evt["event"] = "status";
                evt["data"] = data;
                string json = _json.Serialize(evt);
                if (json == _eventJson) return false;

                _snapshot = data;
                _eventJson = json;
                LogChanges(all);

                Action<string> handler = StatusChanged;
                if (handler != null)
                {
                    try { handler(json); }
                    catch (Exception ex) { Log.Error("Status broadcast failed: " + ex.Message); }
                }
                return true;
            }
        }

        private void LogChanges(List<ReaderInfo> readers)
        {
            var now = new Dictionary<string, ReaderInfo>();
            foreach (ReaderInfo r in readers) now[r.Name] = r;
            foreach (KeyValuePair<string, ReaderInfo> kv in _lastByName)
            {
                if (!now.ContainsKey(kv.Key)) Log.Info("Reader removed: " + kv.Key);
            }
            foreach (ReaderInfo r in readers)
            {
                ReaderInfo previous;
                if (!_lastByName.TryGetValue(r.Name, out previous))
                {
                    Log.Info("Reader found: " + r.Name + (r.CardPresent ? " (card present)" : ""));
                    continue;
                }
                if (!previous.CardPresent && r.CardPresent)
                    Log.Info("Card inserted: " + r.Name + (r.Mute ? " - card does not respond (inserted the wrong way?)" : ""));
                else if (previous.CardPresent && !r.CardPresent)
                    Log.Info("Card removed: " + r.Name);
            }
            _lastByName = now;
        }
    }
}
