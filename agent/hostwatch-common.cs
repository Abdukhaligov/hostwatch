// Shared by hostwatch-agent.exe (the resident monitor) and hostwatch-setup.exe (the installer):
// build version, the fixed all-users install layout, minimal JSON, the config file, a console
// window helper and the live debug stream. Compiled into both exes (see build.cmd).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

namespace Hostwatch
{
    static class Build
    {
        public const string Version = "3.0";
    }

    // Fixed, all-users layout: the setup puts both exes in ProgramData and points HKLM autostart at
    // the agent. The agent reads its config and offline buffer from the same folder.
    static class Paths
    {
        public static readonly string DataDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hostwatch");
        public const string AgentExeName = "hostwatch-agent.exe";
        public const string SetupExeName = "hostwatch-setup.exe";
        public static readonly string AgentTarget = System.IO.Path.Combine(DataDir, AgentExeName);
        public static readonly string SetupTarget = System.IO.Path.Combine(DataDir, SetupExeName);
        public static readonly string ConfigPath = System.IO.Path.Combine(DataDir, "config.json");
        public static readonly string PendingPath = System.IO.Path.Combine(DataDir, "pending.txt");
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValueName = "Hostwatch";
    }

    // Minimal JSON writing (report) and reading (config + server reply).
    static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) d = 0;
            return d.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public static string GetStr(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }

        public static double GetNum(string json, string key, double def)
        {
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : def;
        }

        public static bool GetBool(string json, string key)
        {
            return Regex.IsMatch(json, "\"" + key + "\"\\s*:\\s*true");
        }
    }

    // Opens a console window for the -debug modes. The agent's viewer and the setup's debug install
    // both print into one; closing it never touches the agent.
    static class ConsoleWindow
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        public delegate bool CtrlHandler(int controlType);
        [DllImport("kernel32.dll")]
        public static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);

        public static bool Open(string firstLine)
        {
            if (!AllocConsole())
            {
                MessageBox.Show("Could not open a console window (AllocConsole error " +
                    Marshal.GetLastWin32Error() + ").", "hostwatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
            try { Console.Title = "hostwatch debug"; } catch { }
            Console.WriteLine(firstLine);
            return true;
        }
    }

    // Live debug stream. The resident agent serves it over a named pipe (one per logon session); a
    // viewer is a separate process with its own console. The setup, which owns its own console, calls
    // EchoToConsole() and prints straight there instead. While nobody is watching, Log drops everything.
    static class DebugLog
    {
        public static readonly string PipeName = "HostwatchDebug-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
        const int MaxQueuedLines = 5000;

        static readonly List<StreamWriter> viewers = new List<StreamWriter>();
        static readonly Queue<string> queuedLines = new Queue<string>();
        static readonly object queueGate = new object();
        static volatile int viewerCount = 0;
        static volatile bool echoToConsole = false;
        static Func<string> describe;

        public static bool Watching { get { return echoToConsole || viewerCount > 0; } }

        // The setup prints into its own console, so there is no pipe server there.
        public static void EchoToConsole() { echoToConsole = true; }

        public static void StartServer(Func<string> description)
        {
            describe = description;
            StartBackground(AcceptViewers);
            StartBackground(SendQueuedLines);
        }

        static void StartBackground(ThreadStart work)
        {
            Thread thread = new Thread(work);
            thread.IsBackground = true;
            thread.Start();
        }

        // Only queues: a viewer that stops reading (e.g. text selected in its console) must not stall the caller.
        public static void Log(string message)
        {
            if (!Watching) return;
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message;
            if (echoToConsole) { try { Console.WriteLine(line); } catch { } return; }
            lock (queueGate)
            {
                queuedLines.Enqueue(line);
                while (queuedLines.Count > MaxQueuedLines) queuedLines.Dequeue();
                Monitor.Pulse(queueGate);
            }
        }

        static void AcceptViewers()
        {
            while (true)
            {
                try
                {
                    NamedPipeServerStream pipe = new NamedPipeServerStream(PipeName, PipeDirection.Out,
                        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                        PipeOptions.None, 0, 0, OwnerOnlySecurity());
                    pipe.WaitForConnection();
                    StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false));
                    writer.AutoFlush = true;
                    lock (viewers) { viewers.Add(writer); viewerCount = viewers.Count; }
                    Log("viewer connected: " + (describe != null ? describe() : ""));
                }
                catch { Thread.Sleep(1000); }
            }
        }

        static void SendQueuedLines()
        {
            while (true)
            {
                string line;
                lock (queueGate)
                {
                    while (queuedLines.Count == 0) Monitor.Wait(queueGate);
                    line = queuedLines.Dequeue();
                }
                lock (viewers)
                {
                    for (int viewerIndex = viewers.Count - 1; viewerIndex >= 0; viewerIndex--)
                    {
                        try { viewers[viewerIndex].WriteLine(line); }
                        catch
                        {
                            try { viewers[viewerIndex].Dispose(); } catch { }
                            viewers.RemoveAt(viewerIndex);
                        }
                    }
                    viewerCount = viewers.Count;
                }
            }
        }

        // Reports carry user/IP details, so only the agent's own account may read them, and never over the network.
        static PipeSecurity OwnerOnlySecurity()
        {
            PipeSecurity security = new PipeSecurity();
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                security.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }
    }

    class Config
    {
        public string Server = "";
        public string Label = "";
        public double LabelSetAt = 0;
        public string Token = "";
        public string Path;

        public Config(string path)
        {
            Path = path;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    Server = Json.GetStr(json, "server") ?? "";
                    Label = Json.GetStr(json, "label") ?? "";
                    LabelSetAt = Json.GetNum(json, "label_set_at", 0);
                    Token = Json.GetStr(json, "token") ?? "";
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                string json = "{\n  \"server\": " + Json.Str(Server) +
                              ",\n  \"label\": " + Json.Str(Label) +
                              ",\n  \"label_set_at\": " + Json.Num(LabelSetAt) +
                              ",\n  \"token\": " + Json.Str(Token) + "\n}\n";
                File.WriteAllText(Path, json, new UTF8Encoding(false));
                DebugLog.Log("config saved: server " + Server + ", label \"" + Label + "\", token " + (Token == "" ? "none" : "set"));
            }
            catch (Exception exception) { DebugLog.Log("config save failed: " + exception); }
        }
    }
}
