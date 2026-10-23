// hostwatch agent - reports this PC's health to the hostwatch server every second.
// A plain WinForms tray app (dot icon): Set label / Open dashboard / Change server / Debug / Exit.
// It never installs itself, elevates, writes HKLM, kills processes or deletes anything - that all
// lives in hostwatch-setup.exe. This process only reads its config from ProgramData, collects
// metrics and POSTs them. hostwatch-setup.exe installs it and points autostart here.
// Collects: hostname, user, OS, MAC/IP/link speed, CPU, memory, disks, uptime and
// keyboard/mouse idle time (GetLastInputInfo - only *when* input happened, never what).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.IO.Pipes;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Security.Principal;

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

        // Console session id, for RDP shadowing (mstsc /shadow:<id>). -1 when no one is at the console.
        public static long ConsoleSessionId()
        {
            try { uint s = WTSGetActiveConsoleSessionId(); return s == 0xFFFFFFFF ? -1 : (long)s; }
            catch { return -1; }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

        // UAC split token: an admin's normal (non-elevated) logon token is "limited" and has a
        // linked elevated token. TokenElevationType = 18, TokenElevationTypeLimited = 3.
        public static bool IsLimitedAdminToken(IntPtr token)
        {
            int type, len;
            try { return GetTokenInformation(token, 18, out type, 4, out len) && type == 3; }
            catch { return false; }
        }

        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);

        // Trim process working sets so cached/idle pages are released back to the system.
        // Without admin this covers the current user's own processes; elevated, it covers all.
        public static void ClearMemory()
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
            {
                try { EmptyWorkingSet(p.Handle); }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            GC.Collect();
            GC.WaitForPendingFinalizers();
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
        const int IntervalMs = 1000;
        const int StaticEverySec = 60;
        // Fixed path for fetching this server's report token (same constant lives in server/app.py).
        const string EnrollKey = "b9f3c1a7e24d4f08a1c6e5d20f7b8a3c6d9e4f1028b7c5a3e6f90d2c4b8a1e7f3";

        const double MaxGapSec = 10;   // matches the server; samples within this join one segment

        class Seg { public double From; public double To; public string State; }

        static Config config;
        static string exePath = "";
        static int idleAfter = 120;    // server's idle threshold, learned from the report reply
        static string pendingPath = "";
        static readonly System.Collections.Generic.List<Seg> pending = new System.Collections.Generic.List<Seg>();
        static double lastPendingSave = 0;
        static readonly object gate = new object();
        static string status = "Starting...";
        static bool ok = true;
        static volatile bool stop = false;
        static readonly AutoResetEvent wake = new AutoResetEvent(false);

        static StaticInfo stat = new StaticInfo();
        static DateTime statAt = DateTime.MinValue;
        static ulong prevIdle, prevBusy;
        static long prevRx, prevTx;
        static double ownRxBytes, ownTxBytes;      // our own traffic to the server, excluded from net_io
        static double prevOwnRx, prevOwnTx;
        const int HttpOverhead = 300;              // rough headers/TCP per request, each direction
        static DateTime prevNetAt;

        [STAThread]
        static void Main(string[] args)
        {
            exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

            // -debug just watches the already running agent in its own console; no tray, no second agent.
            if (Array.IndexOf(args, "-debug") >= 0) { RunDebugViewer(); return; }

            try { Directory.CreateDirectory(Paths.DataDir); } catch { }
            pendingPath = Paths.PendingPath;
            config = new Config(Paths.ConfigPath);

            // The saved server is the source of truth: -Server only seeds it when nothing usable is
            // stored yet (first start after install), so a later Change server survives reboots. A
            // loopback address is never reachable from a client, so treat it as "not set".
            if (string.IsNullOrEmpty(config.Server) || IsLoopback(config.Server))
                for (int i = 0; i < args.Length - 1; i++)
                    if (args[i].Equals("-Server", StringComparison.OrdinalIgnoreCase))
                    { config.Server = args[i + 1].TrimEnd('/'); config.Save(); break; }

            // Fall back to the address baked in at build time (see build.cmd) before asking.
            if (string.IsNullOrEmpty(config.Server) && !string.IsNullOrEmpty(BuildConfig.DefaultServer))
            { config.Server = BuildConfig.DefaultServer.TrimEnd('/'); config.Save(); }

            // Normally the setup has already stored the server; prompt only if somehow started without one.
            if (string.IsNullOrEmpty(config.Server))
            {
                string answer = Microsoft.VisualBasic.Interaction.InputBox(
                    "hostwatch server address, e.g. http://192.168.31.6:8765", "hostwatch", "http://");
                if (string.IsNullOrEmpty(answer) || answer == "http://") return;
                config.Server = answer.TrimEnd('/');
                config.Save();
            }

            // One agent per logon session (works for any user, no special privilege), so the tray
            // menu always matches whoever is currently logged in (admin sees actions, user doesn't).
            bool created;
            Mutex mutex = new Mutex(true, "Local\\HostwatchAgent", out created);
            if (!created) return;

            DebugLog.StartServer(DescribeAgent);

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

        static string DescribeAgent()
        {
            string currentStatus; lock (gate) { currentStatus = status; }
            return "hostwatch-agent " + Build.Version + ", exe " + exePath +
                   ", config " + config.Path + ", server " + config.Server + ", label \"" + config.Label + "\"" +
                   ", token " + (string.IsNullOrEmpty(config.Token) ? "none" : "set") + ", idle after " + idleAfter + "s" +
                   ", pending segments " + pending.Count + ", status: " + currentStatus;
        }

        // "hostwatch-agent.exe -debug": prints the running agent's live log in its own console window.
        // Closing the window or Ctrl+C ends only this viewer.
        static void RunDebugViewer()
        {
            if (!ConsoleWindow.Open("hostwatch-agent " + Build.Version +
                " debug. Close this window or press Ctrl+C to stop watching; the agent keeps running.")) return;
            bool waitingShown = false;
            while (true)
            {
                try
                {
                    using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", DebugLog.PipeName, PipeDirection.In))
                    {
                        pipe.Connect(2000);
                        waitingShown = false;
                        using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null) Console.WriteLine(line);
                        }
                    }
                    Console.WriteLine("-- agent disconnected");
                }
                catch (TimeoutException)
                {
                    if (!waitingShown) Console.WriteLine("-- waiting for the agent in this session (" + DebugLog.PipeName + ")...");
                    waitingShown = true;
                }
                catch (Exception exception)
                {
                    Console.WriteLine("-- " + exception.Message);
                    Thread.Sleep(2000);
                }
            }
        }

        // ---------- background worker: collect + send, never touches the UI ----------
        static void WorkerLoop()
        {
            Native.CpuTimes(out prevIdle, out prevBusy);
            SampleNet(out prevRx, out prevTx);
            prevOwnRx = ownRxBytes; prevOwnTx = ownTxBytes;
            prevNetAt = DateTime.UtcNow;
            LoadPending();

            while (!stop)
            {
                DateTime tickAt = DateTime.UtcNow;
                double nowUnix = NowUnix();
                uint inputIdleSeconds = Native.IdleSeconds();
                string actState = inputIdleSeconds >= (uint)idleAfter ? "idle" : "active";
                DebugLog.Log("tick: " + actState + " (no input for " + inputIdleSeconds + "s, idle after " + idleAfter + "s)");
                if (actState == "idle") System.Threading.Interlocked.Increment(ref idleSec);
                else System.Threading.Interlocked.Increment(ref activeSec);
                requestToday = !seededToday;
                bool online = false;
                try
                {
                    if ((DateTime.UtcNow - statAt).TotalSeconds >= StaticEverySec || statAt == DateTime.MinValue)
                        CollectStatic();

                    EnsureToken();
                    string json = BuildReport();
                    string reply = Post(config.Server + "/api/report", json);
                    online = true;

                    // Token rejected (rotated, or the server's config was reset): drop it and re-enroll.
                    if (!Json.GetBool(reply, "ok") && (Json.GetStr(reply, "error") ?? "").Contains("unauthorized"))
                    {
                        DebugLog.Log("token rejected, dropping it to re-enroll");
                        lock (gate) { config.Token = ""; config.Save(); }
                    }

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
                        DebugLog.Log("label changed on the dashboard: \"" + config.Label + "\"");
                    }
                    int replyIdleAfter = (int)Json.GetNum(reply, "idle_after", idleAfter);
                    if (replyIdleAfter != idleAfter) DebugLog.Log("idle threshold from server: " + idleAfter + "s -> " + replyIdleAfter + "s");
                    idleAfter = replyIdleAfter;

                    // Continue today's active/idle from the server instead of counting from zero.
                    if (requestToday)
                    {
                        double ta = Json.GetNum(reply, "today_active", -1), ti = Json.GetNum(reply, "today_idle", -1);
                        if (ta >= 0 && ti >= 0)
                        {
                            System.Threading.Interlocked.Exchange(ref activeSec, (long)ta);
                            System.Threading.Interlocked.Exchange(ref idleSec, (long)ti);
                            DebugLog.Log("today's totals from server: active " + FmtDur((long)ta) + ", idle " + FmtDur((long)ti));
                        }
                        seededToday = true;
                    }

                    // Central retarget: the server can hand out a new address for every agent at once.
                    // Only the saved config changes; the autostart -Server arg is just a first-run seed.
                    string pushed = (Json.GetStr(reply, "server_url") ?? "").TrimEnd('/');
                    if (pushed.Length > 0 && !IsLoopback(pushed) &&
                        !string.Equals(pushed, config.Server, StringComparison.OrdinalIgnoreCase))
                    {
                        DebugLog.Log("server pushed a new address: " + config.Server + " -> " + pushed);
                        lock (gate) { config.Server = pushed; config.Save(); }
                        SetStatus(true, "server -> " + pushed);
                    }

                    if (Json.GetStr(reply, "command") == "clear_cache")
                    {
                        DebugLog.Log("command clear_cache: trimming working sets");
                        Native.ClearMemory();
                    }
                    bool good = Json.GetBool(reply, "ok");
                    SetStatus(good, good ? "sent " + DateTime.Now.ToString("HH:mm:ss")
                                         : "rejected: " + (Json.GetStr(reply, "error") ?? "?"));
                }
                catch (Exception ex)
                {
                    SetStatus(false, "error: " + ex.Message);
                    DebugLog.Log("report failed: " + ex);
                }

                // Offline: keep a local active/idle log and flush it once the server is back.
                if (online)
                {
                    if (pending.Count > 0)
                    {
                        DebugLog.Log("back online, sending " + pending.Count + " buffered segments");
                        if (SendBackfill())
                        {
                            pending.Clear();
                            try { if (File.Exists(pendingPath)) File.Delete(pendingPath); } catch { }
                            DebugLog.Log("backfill accepted, buffer cleared");
                        }
                        else DebugLog.Log("backfill not accepted, will retry next tick");
                    }
                }
                else
                {
                    AppendActivity(nowUnix, actState);
                    DebugLog.Log("offline: buffered " + actState + ", " + pending.Count + " segments pending");
                    if (nowUnix - lastPendingSave >= 10) { SavePending(); lastPendingSave = nowUnix; }
                }

                double left = IntervalMs - (DateTime.UtcNow - tickAt).TotalMilliseconds;
                if (left > 0) wake.WaitOne((int)left);
            }
            if (pending.Count > 0) SavePending();
        }

        static void SetStatus(bool good, string text) { lock (gate) { ok = good; status = text; } }

        // ---------- offline activity buffer ----------
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static void AppendActivity(double ts, string state)
        {
            Seg last = pending.Count > 0 ? pending[pending.Count - 1] : null;
            if (last != null && last.State == state && ts - last.To <= MaxGapSec && ts >= last.To)
                last.To = ts;
            else
                pending.Add(new Seg { From = ts, To = ts, State = state });
            double cutoff = ts - 60 * 86400;   // keep up to ~2 months of offline history
            pending.RemoveAll(seg => seg.To < cutoff);
        }

        static void SavePending()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (Seg s in pending)
                    sb.Append(s.From.ToString(Inv)).Append(',').Append(s.To.ToString(Inv)).Append(',').Append(s.State).Append('\n');
                File.WriteAllText(pendingPath, sb.ToString());
                DebugLog.Log("saved " + pending.Count + " pending segments to " + pendingPath);
            }
            catch (Exception exception) { DebugLog.Log("saving pending segments failed: " + exception); }
        }

        static void LoadPending()
        {
            try
            {
                if (!File.Exists(pendingPath)) return;
                foreach (string line in File.ReadAllLines(pendingPath))
                {
                    string[] parts = line.Split(',');
                    if (parts.Length != 3) continue;
                    double from, to;
                    if (double.TryParse(parts[0], NumberStyles.Any, Inv, out from) &&
                        double.TryParse(parts[1], NumberStyles.Any, Inv, out to))
                        pending.Add(new Seg { From = from, To = to, State = parts[2] });
                }
            }
            catch { }
        }

        static bool SendBackfill()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{\"id\":").Append(Json.Str(stat.Mac)).Append(",\"segments\":[");
                for (int i = 0; i < pending.Count; i++)
                {
                    Seg s = pending[i];
                    if (i > 0) sb.Append(',');
                    sb.Append('[').Append(Json.Num(s.From)).Append(',').Append(Json.Num(s.To)).Append(',').Append(Json.Str(s.State)).Append(']');
                }
                sb.Append("]}");
                return Json.GetBool(Post(config.Server + "/api/report/backfill", sb.ToString()), "ok");
            }
            catch (Exception exception)
            {
                DebugLog.Log("backfill failed: " + exception);
                return false;
            }
        }

        static void CollectStatic()
        {
            try { CollectWmi(); } catch (Exception exception) { DebugLog.Log("WMI collection failed: " + exception); }
            try { CollectNetworks(); } catch (Exception exception) { DebugLog.Log("network collection failed: " + exception); }
            statAt = DateTime.UtcNow;
            DebugLog.Log("static info: " + stat.Os + " | " + stat.Model + " | " + stat.CpuName + " (" + stat.Cores + "c/" +
                         stat.Threads + "t) | ip " + stat.Ip + " | mac " + stat.Mac + " | disks " + stat.DisksJson);
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
            // Subtract our own chatter with the server so the dashboard shows real PC network load.
            double ownRxDelta = ownRxBytes - prevOwnRx, ownTxDelta = ownTxBytes - prevOwnTx;
            double rxBps = secs > 0 ? Math.Max(0, (rx - prevRx) - ownRxDelta) / secs : 0;
            double txBps = secs > 0 ? Math.Max(0, (tx - prevTx) - ownTxDelta) / secs : 0;
            prevRx = rx; prevTx = tx; prevOwnRx = ownRxBytes; prevOwnTx = ownTxBytes; prevNetAt = now;

            string label; double labelAt;
            lock (gate) { label = config.Label; labelAt = config.LabelSetAt; }

            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(Json.Str(stat.Mac));
            sb.Append(",\"agent_version\":").Append(Json.Str(Build.Version));
            sb.Append(",\"hostname\":").Append(Json.Str(Environment.MachineName));
            sb.Append(",\"user\":").Append(Json.Str(Native.ConsoleUser()));
            sb.Append(",\"session_id\":").Append(Native.ConsoleSessionId());
            if (requestToday) sb.Append(",\"want_today\":true");
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
            req.Headers["X-Agent-Token"] = config.Token ?? "";
            req.Timeout = 3000;
            req.ReadWriteTimeout = 3000;
            req.KeepAlive = false;
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            req.ContentLength = bytes.Length;
            ownTxBytes += bytes.Length + HttpOverhead;
            DebugLog.Log("POST " + url + " " + json);
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            using (Stream s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string reply = sr.ReadToEnd();
                ownRxBytes += Encoding.UTF8.GetByteCount(reply) + HttpOverhead;
                DebugLog.Log("<- " + (int)resp.StatusCode + " in " + elapsed.ElapsedMilliseconds + " ms " + reply);
                return reply;
            }
        }

        // Fetch and store this server's report token if we don't have one yet. The enrollment path
        // is a fixed secret baked into the exe, so any install can bootstrap without a password.
        static void EnsureToken()
        {
            if (!string.IsNullOrEmpty(config.Token)) return;
            DebugLog.Log("no token yet, enrolling with " + config.Server);
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(config.Server + "/api/enroll/" + EnrollKey);
                req.Timeout = 3000;
                req.ReadWriteTimeout = 3000;
                req.KeepAlive = false;
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    string tok = Json.GetStr(sr.ReadToEnd(), "token");
                    if (!string.IsNullOrEmpty(tok)) lock (gate) { config.Token = tok; config.Save(); }
                    DebugLog.Log(string.IsNullOrEmpty(tok) ? "enroll: server returned no token" : "enroll: token received");
                }
            }
            catch (Exception exception)   // server down or unreachable: try again next tick
            {
                DebugLog.Log("enroll failed: " + exception.Message);
            }
        }

        // ---------- tray (UI thread) ----------
        static NotifyIcon tray;
        static ToolStripItem miStatus, miTimes;
        static Icon iconOk, iconErr;
        static bool shownOk = true;
        static string shownText = "";

        // Is the logged-in user a member of the local Administrators group (even when not elevated)?
        // id.Groups skips deny-only SIDs, and in a non-elevated admin token (autostart at logon)
        // Administrators is exactly that, so check for the UAC "limited" split token instead.
        static bool UserIsAdmin()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    if (new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)) return true;
                    return Native.IsLimitedAdminToken(id.Token);
                }
            }
            catch { }
            return false;
        }

        static void RunTray()
        {
            iconOk = DotIcon(Color.FromArgb(0, 200, 230));
            iconErr = DotIcon(Color.FromArgb(230, 60, 80));

            tray = new NotifyIcon();
            tray.Icon = iconOk;
            tray.Text = "hostwatch: starting";
            tray.Visible = true;

            bool admin = UserIsAdmin();

            // Info shown to everyone (admins and users), always disabled.
            ContextMenuStrip menu = new ContextMenuStrip();
            miStatus = menu.Items.Add("Starting...");
            miStatus.Enabled = false;
            ToolStripItem miVersion = menu.Items.Add("version " + Build.Version);
            miVersion.Enabled = false;
            miTimes = menu.Items.Add("active - / idle -");
            miTimes.Enabled = false;

            // Actions only for admins.
            if (admin)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Set label...").Click += OnLabel;
                menu.Items.Add("Open dashboard").Click += delegate { try { System.Diagnostics.Process.Start(config.Server); } catch { } };
                menu.Items.Add("Change server...").Click += OnServer;
                menu.Items.Add("Debug console").Click += delegate { try { System.Diagnostics.Process.Start(exePath, "-debug"); } catch { } };
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Uninstall").Click += OnUninstall;
                menu.Items.Add("Exit").Click += delegate { DebugLog.Log("Exit from tray"); tray.Visible = false; Application.Exit(); };
            }
            menu.Opening += delegate { miStatus.Text = StatusLine(); miTimes.Text = TimesLine(); };
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

        static long activeSec, idleSec;   // today's totals shown in the tray (seeded from the server)
        static bool seededToday = false;  // asked the server for today's totals yet?
        static bool requestToday = false; // include "want_today" in the next report

        static string TimesLine()
        {
            return "active " + FmtDur(System.Threading.Interlocked.Read(ref activeSec))
                 + " / idle " + FmtDur(System.Threading.Interlocked.Read(ref idleSec));
        }

        static string FmtDur(long sec)
        {
            if (sec < 60) return sec + "s";
            long h = sec / 3600, m = (sec % 3600) / 60;
            return h > 0 ? h + "h " + m + "m" : m + "m";
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

        // Uninstall is the setup's job (it needs admin and removes files): hand off to it.
        static void OnUninstall(object sender, EventArgs e)
        {
            if (MessageBox.Show("Remove hostwatch from this PC?", "hostwatch",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            DebugLog.Log("Uninstall from tray -> launching the setup");
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Paths.SetupTarget, "-uninstall")
                { Verb = "runas", UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("Could not start " + Paths.SetupExeName + ".\nRun it manually: " +
                    Paths.SetupTarget + " -uninstall", "hostwatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            tray.Visible = false;
            Application.Exit();
        }

        // Change server updates only the saved config; the autostart -Server arg is just a first-run seed.
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

        static bool IsLoopback(string url)
        {
            if (string.IsNullOrEmpty(url)) return true;
            string u = url.ToLowerInvariant();
            return u.Contains("//127.") || u.Contains("//localhost") || u.Contains("//[::1]");
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
