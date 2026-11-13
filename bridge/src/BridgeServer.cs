using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ThaiIdBridge
{
    /// <summary>
    /// Loopback-only HTTP/WebSocket server.
    ///   /ws            WebSocket JSON-RPC (see BridgeSession)
    ///   /api/status    JSON status (CORS for allowed origins)
    ///   /api/simulator/{insert|remove|toggle}   POST, only with --simulate
    ///   /*             static files from the www folder (optional)
    /// </summary>
    internal sealed class BridgeServer
    {
        private const int MaxSessions = 64;

        private readonly Config _config;
        private readonly PcscMonitor _monitor;
        private readonly VirtualThaiIdCard _virtualCard;
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly List<BridgeSession> _sessions = new List<BridgeSession>();
        private readonly object _sessionsLock = new object();
        private int _sessionCounter;

        public BridgeServer(Config config, PcscMonitor monitor, VirtualThaiIdCard virtualCard)
        {
            _config = config;
            _monitor = monitor;
            _virtualCard = virtualCard;
        }

        public void Start()
        {
            Listen(IPAddress.Loopback, true);
            if (Socket.OSSupportsIPv6) Listen(IPAddress.IPv6Loopback, false);
            _monitor.StatusChanged += Broadcast;
        }

        public void Stop()
        {
            foreach (TcpListener listener in _listeners)
            {
                try { listener.Stop(); } catch (Exception) { }
            }
            BridgeSession[] sessions;
            lock (_sessionsLock) sessions = _sessions.ToArray();
            foreach (BridgeSession session in sessions) session.Abort();
        }

        private void Listen(IPAddress address, bool required)
        {
            var listener = new TcpListener(address, _config.Port);
            try
            {
                listener.Start(32);
            }
            catch (SocketException ex)
            {
                if (required) throw;
                Log.Debug("Not listening on " + address + ": " + ex.Message);
                return;
            }
            _listeners.Add(listener);
            var thread = new Thread(() => AcceptLoop(listener));
            thread.IsBackground = true;
            thread.Name = "accept " + address;
            thread.Start();
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }

                var thread = new Thread(() => HandleClient(client));
                thread.IsBackground = true;
                thread.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                NetworkStream net = client.GetStream();
                net.ReadTimeout = 15000;
                net.WriteTimeout = 15000;
                var input = new BufferedStream(net, 8192);
                HttpRequest request = HttpRequest.Read(input);
                if (request == null) return;
                Route(request, client, net, input);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                Log.Error("Unhandled request error: " + ex);
            }
            finally
            {
                try { client.Close(); } catch (Exception) { }
            }
        }

        private void Route(HttpRequest req, TcpClient client, NetworkStream net, Stream input)
        {
            // DNS-rebinding protection: only accept requests addressed to the loopback names we serve.
            string host = req.Header("Host");
            if (!IsLoopbackHost(host))
            {
                Log.Warn("Rejected request for Host '" + host + "' (only 127.0.0.1 / localhost are served)");
                Http.SendText(net, 403, "Forbidden", "Forbidden: invalid Host header", null);
                return;
            }

            string origin = req.Header("Origin");
            bool originAllowed = origin == null || _config.IsOriginAllowed(origin);

            if (req.Method == "OPTIONS")
            {
                if (origin == null || !originAllowed)
                {
                    Http.SendText(net, 403, "Forbidden", "Origin not allowed", null);
                    return;
                }
                Http.Send(net, 204, "No Content", null, null, CorsHeaders(origin, true), false);
                return;
            }

            if (req.Path == "/ws")
            {
                if (!originAllowed)
                {
                    Log.Warn("Rejected WebSocket from origin " + origin + " - add it to allowedOrigins in " + Config.DefaultConfigFile);
                    Http.SendText(net, 403, "Forbidden", "Origin not allowed: " + origin, null);
                    return;
                }
                HandleWebSocket(req, client, net, input, origin);
                return;
            }

            if (req.Path.StartsWith("/api/", StringComparison.Ordinal))
            {
                if (!originAllowed)
                {
                    Http.SendText(net, 403, "Forbidden", "Origin not allowed: " + origin, null);
                    return;
                }
                req.DiscardBody(input);
                HandleApi(req, net, origin == null ? null : CorsHeaders(origin, false));
                return;
            }

            if (req.Method != "GET" && req.Method != "HEAD")
            {
                Http.SendText(net, 405, "Method Not Allowed", "Method not allowed", "Allow: GET, HEAD\r\n");
                return;
            }
            ServeStatic(req, net);
        }

        private void HandleWebSocket(HttpRequest req, TcpClient client, NetworkStream net, Stream input, string origin)
        {
            string key = req.Header("Sec-WebSocket-Key");
            if (req.Method != "GET" || !req.HeaderHasToken("Upgrade", "websocket") || !req.HeaderHasToken("Connection", "Upgrade") || string.IsNullOrEmpty(key))
            {
                Http.SendText(net, 426, "Upgrade Required", "This endpoint only accepts WebSocket connections", "Upgrade: websocket\r\nSec-WebSocket-Version: 13\r\n");
                return;
            }
            if (req.Header("Sec-WebSocket-Version") != "13")
            {
                Http.SendText(net, 426, "Upgrade Required", "Unsupported WebSocket version", "Sec-WebSocket-Version: 13\r\n");
                return;
            }

            int id;
            lock (_sessionsLock)
            {
                if (_sessions.Count >= MaxSessions)
                {
                    Http.SendText(net, 503, "Service Unavailable", "Too many clients", null);
                    return;
                }
                id = ++_sessionCounter;
            }

            WebSocketConnection ws = WebSocketConnection.Accept(client, input, net, key);
            net.ReadTimeout = Timeout.Infinite;
            var session = new BridgeSession(id, ws, _monitor, _virtualCard);
            session.SendHello();
            lock (_monitor.SyncRoot)
            {
                // Snapshot + registration under the publish lock, so no status change can slip in between.
                session.SendRaw(_monitor.StatusEventJson);
                lock (_sessionsLock) _sessions.Add(session);
            }
            Log.Info("Client #" + id + " connected" + (origin != null ? " from " + origin : ""));
            try
            {
                session.Run();
            }
            finally
            {
                lock (_sessionsLock) _sessions.Remove(session);
                session.Dispose();
                Log.Info("Client #" + id + " disconnected");
            }
        }

        private void HandleApi(HttpRequest req, Stream net, string cors)
        {
            if (req.Path == "/api/status")
            {
                if (req.Method != "GET" && req.Method != "HEAD")
                {
                    SendJson(net, 405, "Method Not Allowed", Error("METHOD_NOT_ALLOWED", "Use GET"), cors, false);
                    return;
                }
                int clients;
                lock (_sessionsLock) clients = _sessions.Count;
                var data = new Dictionary<string, object>();
                data["app"] = Program.AppName;
                data["version"] = Program.Version;
                data["port"] = _config.Port;
                data["simulator"] = _virtualCard != null;
                data["clients"] = clients;
                data["status"] = _monitor.Snapshot;
                SendJson(net, 200, "OK", data, cors, req.Method == "HEAD");
                return;
            }

            const string simulatorPrefix = "/api/simulator/";
            if (req.Path.StartsWith(simulatorPrefix, StringComparison.Ordinal))
            {
                if (_virtualCard == null)
                {
                    SendJson(net, 404, "Not Found", Error("SIMULATOR_DISABLED", "Start ThaiIdBridge with --simulate"), cors, false);
                    return;
                }
                if (req.Method != "POST")
                {
                    SendJson(net, 405, "Method Not Allowed", Error("METHOD_NOT_ALLOWED", "Use POST"), cors, false);
                    return;
                }
                string action = req.Path.Substring(simulatorPrefix.Length);
                if (action == "insert") _virtualCard.Insert();
                else if (action == "remove") _virtualCard.Remove();
                else if (action == "toggle") _virtualCard.Toggle();
                else
                {
                    SendJson(net, 404, "Not Found", Error("NOT_FOUND", "Unknown simulator action '" + action + "'"), cors, false);
                    return;
                }
                _monitor.Refresh();
                var result = new Dictionary<string, object>();
                result["inserted"] = _virtualCard.Inserted;
                SendJson(net, 200, "OK", result, cors, false);
                return;
            }

            SendJson(net, 404, "Not Found", Error("NOT_FOUND", "Unknown API " + req.Path), cors, false);
        }

        private void ServeStatic(HttpRequest req, Stream net)
        {
            bool headOnly = req.Method == "HEAD";
            string root = _config.WwwRootFullPath;
            if (root == null)
            {
                if (req.Path == "/" || req.Path == "/index.html")
                    Http.Send(net, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(BuiltInPage()), "Cache-Control: no-store\r\n", headOnly);
                else
                    Http.SendText(net, 404, "Not Found", "Not found", null);
                return;
            }

            string relative;
            string file;
            try
            {
                relative = Uri.UnescapeDataString(req.Path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                file = Path.GetFullPath(Path.Combine(root, relative));
            }
            catch (Exception)
            {
                Http.SendText(net, 400, "Bad Request", "Bad path", null);
                return;
            }
            if (!file.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                !file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Http.SendText(net, 403, "Forbidden", "Forbidden", null);
                return;
            }
            if (Directory.Exists(file)) file = Path.Combine(file, "index.html");
            if (!File.Exists(file))
            {
                // Single-page-app fallback: extension-less routes get index.html.
                if (Path.GetExtension(file).Length == 0) file = Path.Combine(root, "index.html");
                if (!File.Exists(file))
                {
                    Http.SendText(net, 404, "Not Found", "Not found", null);
                    return;
                }
            }
            byte[] body = File.ReadAllBytes(file);
            Http.Send(net, 200, "OK", ContentType(file), body, "Cache-Control: no-cache\r\n", headOnly);
        }

        private void Broadcast(string json)
        {
            BridgeSession[] sessions;
            lock (_sessionsLock) sessions = _sessions.ToArray();
            foreach (BridgeSession session in sessions) session.SendRaw(json);
        }

        private bool IsLoopbackHost(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            string h = host.Trim().ToLowerInvariant();
            string port = ":" + _config.Port;
            return h == "127.0.0.1" + port || h == "localhost" + port || h == "[::1]" + port;
        }

        private static string CorsHeaders(string origin, bool preflight)
        {
            var sb = new StringBuilder();
            sb.Append("Access-Control-Allow-Origin: ").Append(origin).Append("\r\n");
            sb.Append("Vary: Origin\r\n");
            if (preflight)
            {
                sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
                sb.Append("Access-Control-Allow-Headers: Content-Type\r\n");
                sb.Append("Access-Control-Allow-Private-Network: true\r\n");
                sb.Append("Access-Control-Max-Age: 600\r\n");
            }
            return sb.ToString();
        }

        private static Dictionary<string, object> Error(string code, string message)
        {
            var error = new Dictionary<string, object>();
            error["code"] = code;
            error["message"] = message;
            var result = new Dictionary<string, object>();
            result["error"] = error;
            return result;
        }

        private static void SendJson(Stream net, int status, string reason, object payload, string cors, bool headOnly)
        {
            byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(payload));
            Http.Send(net, status, reason, "application/json; charset=utf-8", body, (cors ?? "") + "Cache-Control: no-store\r\n", headOnly);
        }

        private static string ContentType(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js":
                case ".mjs": return "text/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json":
                case ".map": return "application/json; charset=utf-8";
                case ".webmanifest": return "application/manifest+json";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".ico": return "image/x-icon";
                case ".woff": return "font/woff";
                case ".woff2": return "font/woff2";
                case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        private string BuiltInPage()
        {
            return "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Thai ID Bridge</title>" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<style>body{font-family:system-ui,'Segoe UI',Tahoma,sans-serif;margin:2rem;color:#1f2937;line-height:1.5}" +
                "code{background:#f3f4f6;padding:.1rem .35rem;border-radius:4px}li{margin:.35rem 0}</style></head><body>" +
                "<h1>Thai ID Bridge " + Program.Version + " is running</h1>" +
                "<p>WebSocket endpoint: <code>ws://127.0.0.1:" + _config.Port + "/ws</code></p>" +
                "<h2>Readers</h2><ul id=\"readers\"><li>Loading...</li></ul>" +
                "<p>Put the web app build into the <code>www</code> folder next to ThaiIdBridge.exe to serve it here.</p>" +
                "<script>async function tick(){try{const s=await (await fetch('/api/status',{cache:'no-store'})).json();" +
                "const ul=document.getElementById('readers');ul.innerHTML='';const rs=s.status.readers;" +
                "if(!rs.length){ul.innerHTML='<li>No smart card reader found</li>';}" +
                "for(const r of rs){const li=document.createElement('li');" +
                "li.textContent=r.name+' : '+(r.cardPresent?(r.mute?'card not responding':'card present, ATR '+r.atr):'no card');ul.appendChild(li);}" +
                "}catch(e){}}tick();setInterval(tick,1000);</script></body></html>";
        }
    }
}
