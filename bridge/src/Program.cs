using System;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("Thai ID Bridge")]
[assembly: AssemblyDescription("Local PC/SC to WebSocket bridge so web apps can read smart cards (Thai national ID).")]
[assembly: AssemblyProduct("Thai ID Bridge")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace ThaiIdBridge
{
    internal static class Program
    {
        public const string AppName = "ThaiIdBridge";
        public const string Version = "1.0.0";

        private static int Main(string[] args)
        {
            try { Console.Title = "Thai ID Bridge " + Version; } catch (Exception) { }

            Config config;
            try
            {
                config = Config.Load(args);
            }
            catch (Exception ex)
            {
                Log.Error("Configuration error: " + ex.Message);
                return 2;
            }

            if (config.ShowHelp)
            {
                Console.WriteLine(Config.HelpText);
                return 0;
            }

            Log.VerboseEnabled = config.Verbose;

            VirtualThaiIdCard virtualCard = config.Simulate ? new VirtualThaiIdCard() : null;
            var monitor = new PcscMonitor(virtualCard);
            var server = new BridgeServer(config, monitor, virtualCard);
            try
            {
                server.Start();
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
                {
                    Log.Error("Port " + config.Port + " is already in use - is ThaiIdBridge already running? (use --port to pick another port)");
                }
                else
                {
                    Log.Error("Cannot listen on 127.0.0.1:" + config.Port + ": " + ex.Message);
                }
                return 1;
            }

            monitor.Start();
            PrintBanner(config);

            var exit = new ManualResetEvent(false);
            Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                exit.Set();
            };
            exit.WaitOne();

            Log.Info("Stopping...");
            monitor.Stop();
            server.Stop();
            return 0;
        }

        private static void PrintBanner(Config config)
        {
            string baseUrl = "http://127.0.0.1:" + config.Port;
            var sb = new StringBuilder();
            sb.AppendLine("================================================================");
            sb.AppendLine(" Thai ID Bridge " + Version + "   (PC/SC smart card -> WebSocket)");
            sb.AppendLine("----------------------------------------------------------------");
            sb.AppendLine(" WebSocket : ws://127.0.0.1:" + config.Port + "/ws");
            sb.AppendLine(" Status    : " + baseUrl + "/api/status");
            sb.AppendLine(" Web page  : " + baseUrl + "/" + (config.WwwRootFullPath != null ? "  (" + config.WwwRootFullPath + ")" : "  (built-in status page)"));
            sb.AppendLine(" Origins   : " + config.DescribeOrigins());
            if (config.Simulate)
                sb.AppendLine(" Simulator : ON - virtual reader with a FAKE Thai ID card");
            if (config.ConfigPath != null)
                sb.AppendLine(" Config    : " + config.ConfigPath);
            sb.AppendLine(" Press Ctrl+C to stop.");
            sb.Append("================================================================");
            Console.WriteLine(sb.ToString());
            if (config.AllowsAnyOrigin)
                Log.Warn("allowedOrigins contains \"*\" - ANY website could read an inserted ID card. Use only for testing!");
            else if (config.AllowedOrigins.Count == 0)
                Log.Info("Only localhost pages are allowed. Add your web app origin to allowedOrigins in " + Config.DefaultConfigFile + " for production.");
        }
    }
}
