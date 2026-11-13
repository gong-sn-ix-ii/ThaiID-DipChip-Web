using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ThaiIdBridge
{
    /// <summary>Settings from ThaiIdBridge.config.json (next to the exe) overridden by command-line options.</summary>
    internal sealed class Config
    {
        public const string DefaultConfigFile = "ThaiIdBridge.config.json";

        public const string HelpText =
            "Thai ID Bridge - lets web apps read smart cards (Thai national ID) through a local WebSocket.\r\n" +
            "\r\n" +
            "Usage: ThaiIdBridge.exe [options]\r\n" +
            "\r\n" +
            "  --port <n>              TCP port on 127.0.0.1 (default 8424)\r\n" +
            "  --allow-origin <url>    Allow a web origin, e.g. https://app.example.com (repeatable;\r\n" +
            "                          wildcard sub-domains allowed: https://*.example.com)\r\n" +
            "  --no-localhost-origins  Do not automatically allow pages from http(s)://localhost or 127.0.0.1\r\n" +
            "  --www <dir>             Folder with a web app to serve at http://127.0.0.1:<port>/ (default: www)\r\n" +
            "  --simulate              Add a virtual reader with a FAKE Thai ID card (for development)\r\n" +
            "  --verbose               Log every command APDU (card responses are never logged)\r\n" +
            "  --config <file>         Config file (default: ThaiIdBridge.config.json next to the exe)\r\n" +
            "  --help                  Show this help\r\n";

        public int Port = 8424;
        public readonly List<string> AllowedOrigins = new List<string>();
        public bool AllowLocalhostOrigins = true;
        public string WwwRoot = "www";
        public bool Simulate;
        public bool Verbose;
        public bool ShowHelp;
        public string ConfigPath;
        public string WwwRootFullPath;

        public bool AllowsAnyOrigin
        {
            get { return AllowedOrigins.Contains("*"); }
        }

        public static Config Load(string[] args)
        {
            var config = new Config();
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;

            string configPath = Path.Combine(exeDir, DefaultConfigFile);
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--config") configPath = Path.GetFullPath(args[i + 1]);
            }
            if (File.Exists(configPath))
            {
                config.ApplyJson(File.ReadAllText(configPath, Encoding.UTF8));
                config.ConfigPath = configPath;
            }

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--port": config.Port = ParsePort(NextArg(args, ref i)); break;
                    case "--allow-origin": config.AllowedOrigins.Add(NormalizeOrigin(NextArg(args, ref i))); break;
                    case "--no-localhost-origins": config.AllowLocalhostOrigins = false; break;
                    case "--www": config.WwwRoot = NextArg(args, ref i); break;
                    case "--simulate": config.Simulate = true; break;
                    case "--verbose":
                    case "-v": config.Verbose = true; break;
                    case "--config": i++; break;
                    case "--help":
                    case "-h":
                    case "/?": config.ShowHelp = true; break;
                    default: throw new ArgumentException("Unknown option '" + arg + "' (see --help)");
                }
            }

            if (!string.IsNullOrEmpty(config.WwwRoot))
            {
                string full = Path.IsPathRooted(config.WwwRoot) ? config.WwwRoot : Path.Combine(exeDir, config.WwwRoot);
                full = Path.GetFullPath(full).TrimEnd('\\', '/');
                if (Directory.Exists(full)) config.WwwRootFullPath = full;
            }
            return config;
        }

        /// <summary>
        /// Browsers always send Origin on WebSocket handshakes, so this is what stops a random website
        /// from reading the citizen's card while it sits in the reader.
        /// </summary>
        public bool IsOriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return false;
            string o = origin.Trim().TrimEnd('/').ToLowerInvariant();
            if (o == "null") return false;

            foreach (string allowed in AllowedOrigins)
            {
                if (allowed == "*" || allowed == o) return true;
                int wildcard = allowed.IndexOf("://*.", StringComparison.Ordinal);
                if (wildcard > 0)
                {
                    string scheme = allowed.Substring(0, wildcard + 3);   // "https://"
                    string suffix = allowed.Substring(wildcard + 4);      // ".example.com[:port]"
                    if (o.StartsWith(scheme, StringComparison.Ordinal) && o.EndsWith(suffix, StringComparison.Ordinal) &&
                        o.Length > scheme.Length + suffix.Length)
                    {
                        string label = o.Substring(scheme.Length, o.Length - scheme.Length - suffix.Length);
                        if (label.IndexOfAny(new[] { '/', ':', '@', '\\' }) < 0) return true;
                    }
                }
            }

            if (AllowLocalhostOrigins)
            {
                Uri uri;
                if (Uri.TryCreate(o, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                {
                    string host = uri.Host;
                    if (host == "localhost" || host == "127.0.0.1" || host == "[::1]" || host == "::1" ||
                        host.EndsWith(".localhost", StringComparison.Ordinal))
                        return true;
                }
            }
            return false;
        }

        public string DescribeOrigins()
        {
            var parts = new List<string>();
            if (AllowLocalhostOrigins) parts.Add("localhost / 127.0.0.1 (any port)");
            parts.AddRange(AllowedOrigins);
            return parts.Count == 0 ? "(none - every web page will be rejected)" : string.Join(", ", parts);
        }

        private void ApplyJson(string text)
        {
            var obj = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
            if (obj == null) throw new ArgumentException(DefaultConfigFile + " must contain a JSON object");

            object v;
            if (obj.TryGetValue("port", out v) && v != null) Port = ParsePort(Convert.ToString(v, CultureInfo.InvariantCulture));
            if (obj.TryGetValue("allowedOrigins", out v) && v != null)
            {
                var list = v as IEnumerable;
                if (list == null || v is string) throw new ArgumentException("allowedOrigins must be an array of strings");
                foreach (object item in list)
                {
                    var s = item as string;
                    if (!string.IsNullOrWhiteSpace(s)) AllowedOrigins.Add(NormalizeOrigin(s));
                }
            }
            if (obj.TryGetValue("allowLocalhostOrigins", out v) && v is bool) AllowLocalhostOrigins = (bool)v;
            if (obj.TryGetValue("wwwRoot", out v)) WwwRoot = v as string;
            if (obj.TryGetValue("simulate", out v) && v is bool) Simulate = (bool)v;
            if (obj.TryGetValue("verbose", out v) && v is bool) Verbose = (bool)v;
        }

        private static string NextArg(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException("Option " + args[i] + " needs a value");
            i++;
            return args[i];
        }

        private static int ParsePort(string value)
        {
            int port;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port < 1 || port > 65535)
                throw new ArgumentException("Invalid port '" + value + "'");
            return port;
        }

        private static string NormalizeOrigin(string origin)
        {
            string o = origin.Trim().TrimEnd('/').ToLowerInvariant();
            if (o != "*" && o.IndexOf("://", StringComparison.Ordinal) <= 0)
                throw new ArgumentException("Origin '" + origin + "' must look like https://app.example.com");
            return o;
        }
    }
}
