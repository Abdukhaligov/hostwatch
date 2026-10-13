// hostwatch agent - reports this PC's health to the hostwatch server every second.
// A normal WinForms tray app (dot icon): Set label / Open dashboard / Change server / Exit.
// Compiled on the machine by install.cmd with the C# compiler that ships with Windows
// (.NET Framework), so there is no script launcher, no hidden PowerShell and no exe to download.
// Collects: hostname, user, OS, MAC/IP/link speed, CPU, memory, disks, uptime and
// keyboard/mouse idle time (GetLastInputInfo - only *when* input happened, never what).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Hostwatch
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idle,
            out System.Runtime.InteropServices.ComTypes.FILETIME kernel,
            out System.Runtime.InteropServices.ComTypes.FILETIME user);

        [DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        [StructLayout(LayoutKind.Sequential)]
        class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                         ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        static extern bool GlobalMemoryStatusEx(MEMORYSTATUSEX m);

        public static uint IdleSeconds()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (!GetLastInputInfo(ref info)) return 0;
            return unchecked((uint)Environment.TickCount - info.dwTime) / 1000;
        }

        public static ulong Uptime() { return GetTickCount64() / 1000; }

        static ulong ToU(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            return ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        public static void CpuTimes(out ulong idle, out ulong busy)
        {
            System.Runtime.InteropServices.ComTypes.FILETIME fi, fk, fu;
            GetSystemTimes(out fi, out fk, out fu);
            idle = ToU(fi);
            busy = ToU(fk) + ToU(fu) - idle;   // kernel time already includes idle
        }

        public static void Memory(out double total, out double used)
        {
            MEMORYSTATUSEX m = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(m)) { total = m.ullTotalPhys; used = m.ullTotalPhys - m.ullAvailPhys; }
            else { total = 0; used = 0; }
        }

        [DllImport("kernel32.dll")]
        static extern uint WTSGetActiveConsoleSessionId();
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSQuerySessionInformation(IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out uint bytes);
        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        static string WtsString(uint session, int infoClass)   // 5 = WTSUserName, 7 = WTSDomainName
        {
            IntPtr buffer; uint bytes;
            if (WTSQuerySessionInformation(IntPtr.Zero, session, infoClass, out buffer, out bytes) && buffer != IntPtr.Zero)
                try { return Marshal.PtrToStringUni(buffer) ?? ""; } finally { WTSFreeMemory(buffer); }
            return "";
        }

        // The person actually logged in at the console, regardless of which account runs the agent.
        public static string ConsoleUser()
        {
            try
            {
                uint session = WTSGetActiveConsoleSessionId();
                if (session != 0xFFFFFFFF)
                {
                    string user = WtsString(session, 5);
                    if (!string.IsNullOrEmpty(user))
                    {
                        string domain = WtsString(session, 7);
                        return (string.IsNullOrEmpty(domain) ? "" : domain + "\\") + user;
                    }
                }
            }
            catch { }
            return Environment.UserDomainName + "\\" + Environment.UserName;
        }
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

    class Config
    {
        public string Server = "";
        public string Label = "";
        public double LabelSetAt = 0;
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
                              ",\n  \"label_set_at\": " + Json.Num(LabelSetAt) + "\n}\n";
                File.WriteAllText(Path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // Slow-changing facts, refreshed once a minute.
    class StaticInfo
    {
        public string Os = "", Model = "", CpuName = "", Ip = "", Mac = "";
        public int Cores = 0, Threads = Environment.ProcessorCount;
        public string NetworksJson = "[]", DisksJson = "[]";
    }

    class Program
    {
        const string Version = "2.1";
        const int IntervalMs = 1000;
        const int StaticEverySec = 60;

        static Config config;
        static readonly object gate = new object();
        static string status = "Starting...";
        static bool ok = true;
        static volatile bool stop = false;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        static StaticInfo stat = new StaticInfo();
        static DateTime statAt = DateTime.MinValue;
        static ulong prevIdle, prevBusy;
        static long prevRx, prevTx;
        static DateTime prevNetAt;

        [STAThread]
        static void Main(string[] args)
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hostwatch");
            Directory.CreateDirectory(dir);
            string target = System.IO.Path.Combine(dir, "hostwatch.exe");
            config = new Config(System.IO.Path.Combine(dir, "config.json"));

            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("-Server", StringComparison.OrdinalIgnoreCase))
                { config.Server = args[i + 1].TrimEnd('/'); config.Save(); }

            if (Array.IndexOf(args, "-uninstall") >= 0) { Uninstall(dir); return; }

            if (string.IsNullOrEmpty(config.Server))
            {
                string answer = Microsoft.VisualBasic.Interaction.InputBox(
                    "hostwatch server address, e.g. http://192.168.31.6:8765", "hostwatch", "http://");
                if (string.IsNullOrEmpty(answer) || answer == "http://") return;
                config.Server = answer.TrimEnd('/');
                config.Save();
            }

            // Run from %LOCALAPPDATA%\Hostwatch and start at every logon. When launched from
            // anywhere else (the downloaded copy), install into place and hand off to it.
            string self = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            bool installed = string.Equals(self, target, StringComparison.OrdinalIgnoreCase);
            SetAutostart(target, config.Server);
            if (!installed)
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("hostwatch"))
                    if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                        try { p.Kill(); p.WaitForExit(3000); } catch { }
                try { File.Copy(self, target, true); } catch { }
                try { System.Diagnostics.Process.Start(target, "-Server \"" + config.Server + "\""); } catch { }
                return;
            }

            bool created;
            Mutex mutex = new Mutex(true, "Local\\HostwatchAgent", out created);
            if (!created) return;

            ServicePointManager.Expect100Continue = false;
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }

            Thread worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Start();

            Application.EnableVisualStyles();
            RunTray();

            stop = true;
            wake.Set();
            worker.Join(4000);
            GC.KeepAlive(mutex);
        }

        // ---------- background worker: collect + send, never touches the UI ----------
        static void WorkerLoop()
        {
            Native.CpuTimes(out prevIdle, out prevBusy);
            SampleNet(out prevRx, out prevTx);
            prevNetAt = DateTime.UtcNow;

            while (!stop)
            {
                DateTime tickAt = DateTime.UtcNow;
                try
                {
                    if ((DateTime.UtcNow - statAt).TotalSeconds >= StaticEverySec || statAt == DateTime.MinValue)
                        CollectStatic();

                    string json = BuildReport();
                    string reply = Post(config.Server + "/api/report", json);

                    // A label changed on the dashboard comes back here.
                    double replyAt = Json.GetNum(reply, "label_set_at", 0);
                    if (replyAt > config.LabelSetAt)
                    {
                        lock (gate)
                        {
                            config.Label = Json.GetStr(reply, "label") ?? "";
                            config.LabelSetAt = replyAt;
                            config.Save();
                        }
                    }
                    bool good = Json.GetBool(reply, "ok");
                    SetStatus(good, good ? "sent " + DateTime.Now.ToString("HH:mm:ss")
                                         : "rejected: " + (Json.GetStr(reply, "error") ?? "?"));
                }
                catch (Exception ex)
                {
                    SetStatus(false, "error: " + ex.Message);
                }

                double left = IntervalMs - (DateTime.UtcNow - tickAt).TotalMilliseconds;
                if (left > 0) wake.WaitOne((int)left);
            }
        }

        static void SetStatus(bool good, string text) { lock (gate) { ok = good; status = text; } }

        static void CollectStatic()
        {
            try { CollectWmi(); } catch { }
            try { CollectNetworks(); } catch { }
            statAt = DateTime.UtcNow;
        }

        static void CollectWmi()
        {
            foreach (ManagementObject o in Query("SELECT Caption,Version FROM Win32_OperatingSystem"))
            { stat.Os = (("" + o["Caption"]) + " " + o["Version"]).Trim(); break; }

            foreach (ManagementObject o in Query("SELECT Manufacturer,Model FROM Win32_ComputerSystem"))
            { stat.Model = (("" + o["Manufacturer"]) + " " + o["Model"]).Trim(); break; }

            int cores = 0, threads = 0; string name = "";
            foreach (ManagementObject o in Query("SELECT Name,NumberOfCores,NumberOfLogicalProcessors FROM Win32_Processor"))
            {
                if (name == "") name = Regex.Replace("" + o["Name"], "\\s+", " ").Trim();
                cores += ToInt(o["NumberOfCores"]);
                threads += ToInt(o["NumberOfLogicalProcessors"]);
            }
            stat.CpuName = name;
            stat.Cores = cores > 0 ? cores : Environment.ProcessorCount;
            stat.Threads = threads > 0 ? threads : Environment.ProcessorCount;

            List<string> disks = new List<string>();
            foreach (ManagementObject o in Query("SELECT DeviceID,VolumeName,FileSystem,Size,FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3"))
            {
                disks.Add("{\"name\":" + Json.Str("" + o["DeviceID"]) +
                          ",\"label\":" + Json.Str("" + o["VolumeName"]) +
                          ",\"fs\":" + Json.Str("" + o["FileSystem"]) +
                          ",\"total\":" + Json.Num(ToDouble(o["Size"])) +
                          ",\"free\":" + Json.Num(ToDouble(o["FreeSpace"])) + "}");
            }
            stat.DisksJson = "[" + string.Join(",", disks.ToArray()) + "]";
        }

        static void CollectNetworks()
        {
            List<string> nets = new List<string>();
            string primaryIp = "", primaryMac = "";
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                string ip = "";
                foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) { ip = ua.Address.ToString(); break; }
                if (ip == "") continue;

                string gw = "";
                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork) { gw = g.Address.ToString(); break; }

                string mac = FormatMac(ni.GetPhysicalAddress());
                bool primary = gw != "";
                double speed = ni.Speed > 0 ? ni.Speed : 0;
                nets.Add("{\"name\":" + Json.Str(ni.Name) +
                         ",\"adapter\":" + Json.Str(ni.Description) +
                         ",\"mac\":" + Json.Str(mac) +
                         ",\"ip\":" + Json.Str(ip) +
                         ",\"gateway\":" + Json.Str(gw) +
                         ",\"speed\":" + (speed > 0 ? Json.Num(speed) : "null") +
                         ",\"primary\":" + (primary ? "true" : "false") + "}");

                if (primary && primaryIp == "") { primaryIp = ip; primaryMac = mac; }
                else if (primaryMac == "") { primaryIp = ip; primaryMac = mac; }
            }
            stat.NetworksJson = "[" + string.Join(",", nets.ToArray()) + "]";
            stat.Ip = primaryIp;
            stat.Mac = primaryMac;
        }

        static void SampleNet(out long rx, out long tx)
        {
            rx = 0; tx = 0;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                try
                {
                    IPv4InterfaceStatistics s = ni.GetIPv4Statistics();
                    rx += s.BytesReceived;
                    tx += s.BytesSent;
                }
                catch { }
            }
        }

        static string BuildReport()
        {
            ulong idle, busy;
            Native.CpuTimes(out idle, out busy);
            ulong idleDelta = idle - prevIdle, busyDelta = busy - prevBusy;
            ulong totalDelta = idleDelta + busyDelta;
            double cpu = totalDelta > 0 ? 100.0 * busyDelta / totalDelta : 0;
            prevIdle = idle; prevBusy = busy;

            double memTotal, memUsed;
            Native.Memory(out memTotal, out memUsed);

            long rx, tx;
            SampleNet(out rx, out tx);
            DateTime now = DateTime.UtcNow;
            double secs = (now - prevNetAt).TotalSeconds;
            double rxBps = secs > 0 ? Math.Max(0, rx - prevRx) / secs : 0;
            double txBps = secs > 0 ? Math.Max(0, tx - prevTx) / secs : 0;
            prevRx = rx; prevTx = tx; prevNetAt = now;

            string label; double labelAt;
            lock (gate) { label = config.Label; labelAt = config.LabelSetAt; }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(Json.Str(stat.Mac));
            sb.Append(",\"agent_version\":").Append(Json.Str(Version));
            sb.Append(",\"hostname\":").Append(Json.Str(Environment.MachineName));
            sb.Append(",\"user\":").Append(Json.Str(Native.ConsoleUser()));
            sb.Append(",\"os\":").Append(Json.Str(stat.Os));
            sb.Append(",\"model\":").Append(Json.Str(stat.Model));
            sb.Append(",\"ip\":").Append(Json.Str(stat.Ip));
            sb.Append(",\"mac\":").Append(Json.Str(stat.Mac));
            sb.Append(",\"networks\":").Append(stat.NetworksJson);
            sb.Append(",\"cpu\":{\"percent\":").Append(Json.Num(cpu))
              .Append(",\"name\":").Append(Json.Str(stat.CpuName))
              .Append(",\"cores\":").Append(stat.Cores)
              .Append(",\"threads\":").Append(stat.Threads).Append('}');
            sb.Append(",\"memory\":{\"total\":").Append(Json.Num(memTotal))
              .Append(",\"used\":").Append(Json.Num(memUsed)).Append('}');
            sb.Append(",\"disks\":").Append(stat.DisksJson);
            sb.Append(",\"net_io\":{\"rx_bps\":").Append(Json.Num(rxBps))
              .Append(",\"tx_bps\":").Append(Json.Num(txBps)).Append('}');
            sb.Append(",\"uptime_seconds\":").Append(Json.Num(Native.Uptime()));
            sb.Append(",\"idle_seconds\":").Append(Json.Num(Native.IdleSeconds()));
            sb.Append(",\"label\":").Append(Json.Str(label));
            sb.Append(",\"label_set_at\":").Append(Json.Num(labelAt));
            sb.Append('}');
            return sb.ToString();
        }

        static string Post(string url, string json)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json; charset=utf-8";
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            req.KeepAlive = false;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            req.ContentLength = bytes.Length;
            using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        // ---------- tray (UI thread) ----------
        static NotifyIcon tray;
        static ToolStripItem miStatus;
        static Icon iconOk, iconErr;
        static bool shownOk = true;
        static string shownText = "";

        static void RunTray()
        {
            iconOk = DotIcon(Color.FromArgb(0, 200, 230));
            iconErr = DotIcon(Color.FromArgb(230, 60, 80));

            tray = new NotifyIcon();
            tray.Icon = iconOk;
            tray.Text = "hostwatch: starting";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            miStatus = menu.Items.Add("Starting...");
            miStatus.Enabled = false;
            menu.Items.Add("Set label...").Click += OnLabel;
            menu.Items.Add("Open dashboard").Click += delegate { try { System.Diagnostics.Process.Start(config.Server); } catch { } };
            menu.Items.Add("Change server...").Click += OnServer;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Uninstall").Click += OnUninstall;
            menu.Items.Add("Exit").Click += delegate { tray.Visible = false; Application.Exit(); };
            menu.Opening += delegate { miStatus.Text = StatusLine(); };
            tray.ContextMenuStrip = menu;

            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { UpdateTray(); };
            timer.Start();

            Application.Run();
            timer.Stop();
            tray.Dispose();
        }

        static string StatusLine()
        {
            string name = string.IsNullOrEmpty(config.Label) ? Environment.MachineName : config.Label;
            string st; lock (gate) { st = status; }
            return name + " - " + st;
        }

        static void UpdateTray()
        {
            string text = "hostwatch: " + StatusLine();
            if (text.Length > 63) text = text.Substring(0, 63);
            bool good; lock (gate) { good = ok; }
            if (text != shownText) { tray.Text = text; shownText = text; }
            if (good != shownOk) { tray.Icon = good ? iconOk : iconErr; shownOk = good; }
        }

        static void OnLabel(object sender, EventArgs e)
        {
            string answer = Microsoft.VisualBasic.Interaction.InputBox(
                "Label for this PC (shown on the dashboard).\nLeave empty to keep the current one.",
                "hostwatch", config.Label);
            if (!string.IsNullOrEmpty(answer))
            {
                lock (gate)
                {
                    config.Label = answer.Trim();
                    config.LabelSetAt = NowUnix();
                    config.Save();
                }
                wake.Set();
            }
        }

        static void OnUninstall(object sender, EventArgs e)
        {
            if (MessageBox.Show("Remove hostwatch from this PC?", "hostwatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hostwatch");
            tray.Visible = false;
            Uninstall(dir);
            Application.Exit();
        }

        static void OnServer(object sender, EventArgs e)
        {
            string answer = Microsoft.VisualBasic.Interaction.InputBox(
                "hostwatch server address", "hostwatch", config.Server);
            if (!string.IsNullOrEmpty(answer))
            {
                lock (gate) { config.Server = answer.TrimEnd('/'); config.Save(); }
                wake.Set();
            }
        }

        static Icon DotIcon(Color color)
        {
            Bitmap bmp = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, 2, 2, 12, 12);
                using (Pen p = new Pen(Color.FromArgb(40, 40, 40))) g.DrawEllipse(p, 2, 2, 12, 12);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ---------- helpers ----------
        static ManagementObjectCollection Query(string wql)
        {
            return new ManagementObjectSearcher(wql).Get();
        }

        static string FormatMac(PhysicalAddress mac)
        {
            byte[] b = mac.GetAddressBytes();
            if (b.Length == 0) return "";
            string[] parts = new string[b.Length];
            for (int i = 0; i < b.Length; i++) parts[i] = b[i].ToString("x2");
            return string.Join(":", parts);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static void SetAutostart(string target, string server)
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (run != null) run.SetValue("Hostwatch", "\"" + target + "\" -Server \"" + server + "\"");
            }
            catch { }
        }

        static void Uninstall(string dir)
        {
            try
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (run != null) run.DeleteValue("Hostwatch", false);
            }
            catch { }
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("hostwatch"))
                if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                    try { p.Kill(); } catch { }
            // Delete the folder after we exit (the running exe can't delete itself directly).
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                    "/c timeout /t 2 >nul & rmdir /s /q \"" + dir + "\"")
                { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, CreateNoWindow = true });
            }
            catch { }
        }

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static double NowUnix() { return (DateTime.UtcNow - Epoch).TotalSeconds; }

        static int ToInt(object o) { int v; return int.TryParse("" + o, out v) ? v : 0; }
        static double ToDouble(object o)
        {
            double v;
            return double.TryParse("" + o, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}
