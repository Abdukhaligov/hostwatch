// hostwatch setup - installs, reconfigures and removes the hostwatch agent for all users of this PC.
// All the one-time, privileged work lives here, not in the resident agent: elevate, grant the data
// folder write access, trust the server's TLS certificate, enable RDP shadowing (with consent), stop
// any old agent, copy the two exes into ProgramData, write the HKLM autostart and launch the agent.
// Run it from the folder that holds hostwatch-agent.exe (and hostwatch.crt, when the server uses https).
//   hostwatch-setup.exe                      install (asks for the server if not stored yet)
//   hostwatch-setup.exe -Server http://IP    install/reconfigure with that server, no prompt
//   hostwatch-setup.exe -uninstall           remove the agent from this PC
//   hostwatch-setup.exe -debug               same as above, printing every step to a console window
using System;
using System.IO;
using System.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;

namespace Hostwatch
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool debug = Array.IndexOf(args, "-debug") >= 0;
            bool uninstall = Array.IndexOf(args, "-uninstall") >= 0;

            // Everything here needs admin (HKLM, ProgramData ACL, RDP registry): elevate first.
            if (!IsElevated()) { RelaunchElevated(args); return; }

            if (debug && ConsoleWindow.Open("hostwatch-setup " + Build.Version +
                (uninstall ? " uninstall" : " install") + ". Steps print below.")) DebugLog.EchoToConsole();

            try { Directory.CreateDirectory(Paths.DataDir); }
            catch (Exception exception) { DebugLog.Log("creating " + Paths.DataDir + " failed: " + exception); }

            if (uninstall)
            {
                DebugLog.Log("uninstalling");
                RemoveAutostartAndStopAgents();
                RemoveServerCert();
                if (debug) { DebugLog.Log("done. Press Enter to remove the files and close."); Console.ReadLine(); }
                ScheduleFolderDelete();
                return;
            }

            Install(args, debug);
        }

        static void Install(string[] args, bool debug)
        {
            string self = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            string selfDir = System.IO.Path.GetDirectoryName(self);
            string agentSource = System.IO.Path.Combine(selfDir, Paths.AgentExeName);
            DebugLog.Log("installer " + Build.Version + ": " + self);

            if (!File.Exists(agentSource))
            {
                string message = Paths.AgentExeName + " was not found next to the installer.\n" +
                    "Keep hostwatch-agent.exe and hostwatch-setup.exe in the same folder and run the setup again.";
                DebugLog.Log(message);
                MessageBox.Show(message, "hostwatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (debug) { DebugLog.Log("Press Enter to close."); Console.ReadLine(); }
                return;
            }

            // Reuse whatever is already stored (server, label, token) so a reinstall keeps its identity.
            Config config = new Config(Paths.ConfigPath);
            string serverArg = ServerArg(args);
            if (!string.IsNullOrEmpty(serverArg)) config.Server = serverArg.TrimEnd('/');
            // Fall back to the address baked in at build time (see build.cmd) before asking.
            if (string.IsNullOrEmpty(config.Server) && !string.IsNullOrEmpty(BuildConfig.DefaultServer))
                config.Server = BuildConfig.DefaultServer.TrimEnd('/');
            if (string.IsNullOrEmpty(config.Server))
            {
                string answer = Microsoft.VisualBasic.Interaction.InputBox(
                    "hostwatch server address, e.g. http://192.168.31.6:8765", "hostwatch", "http://");
                if (string.IsNullOrEmpty(answer) || answer == "http://")
                {
                    DebugLog.Log("no server entered, nothing installed.");
                    if (debug) { DebugLog.Log("Press Enter to close."); Console.ReadLine(); }
                    return;
                }
                config.Server = answer.TrimEnd('/');
            }
            config.Save();
            DebugLog.Log("server: " + config.Server);

            GrantUsersWrite(Paths.DataDir);   // standard users can save config/label and the offline buffer
            InstallServerCert(selfDir);       // trust the server's cert so the agent can use https
            ConfigureShadow();                // enable RDP shadow (with consent) so an admin can connect

            RemoveAutostartAndStopAgents();   // replace whatever is already installed

            // Copy both exes into ProgramData; the agent is the autostart target, the setup stays for
            // later uninstall/reconfigure from the tray. CopyExe skips a copy onto itself (reconfigure in place).
            CopyExe(agentSource, Paths.AgentTarget);
            CopyExe(self, Paths.SetupTarget);

            SetAutostart(Paths.AgentTarget, config.Server);

            try
            {
                System.Diagnostics.Process agent = System.Diagnostics.Process.Start(
                    Paths.AgentTarget, "-Server \"" + config.Server + "\"");
                DebugLog.Log("started " + Paths.AgentTarget + (agent != null ? " pid " + agent.Id : ""));
            }
            catch (Exception exception) { DebugLog.Log("starting " + Paths.AgentTarget + " failed: " + exception); }

            DebugLog.Log("install finished.");
            if (debug) { DebugLog.Log("Press Enter to close (the agent keeps running)."); Console.ReadLine(); }
        }

        static string ServerArg(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("-Server", StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return "";
        }

        static void CopyExe(string source, string target)
        {
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return;   // reconfigure in place
            try { File.Copy(source, target, true); DebugLog.Log("copied " + source + " -> " + target); }
            catch (Exception exception) { DebugLog.Log("copy " + source + " -> " + target + " failed: " + exception); }
        }

        static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        static void RelaunchElevated(string[] args)
        {
            try
            {
                StringBuilder line = new StringBuilder();
                foreach (string a in args)
                {
                    if (line.Length > 0) line.Append(' ');
                    line.Append(a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a);
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName, line.ToString())
                { Verb = "runas", UseShellExecute = true });
            }
            catch   // user declined the UAC prompt
            {
                MessageBox.Show("hostwatch setup needs administrator rights.\nRun it again and allow the prompt.",
                    "hostwatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // Set up RDP shadowing on this PC so an admin can download SHADOW from the dashboard and
        // connect: enable Remote Desktop and open the firewall. Shadow = 1 means the person at the PC
        // gets a consent prompt and must accept before the admin sees or controls their session.
        // We deliberately do NOT set LocalAccountTokenFilterPolicy (that would weaken UAC filtering for
        // every remote logon) and do NOT use Shadow = 2 (silent control) - the 2GIS SOC flagged both.
        static void ConfigureShadow()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server"))
                    if (k != null) k.SetValue("fDenyTSConnections", 0, RegistryValueKind.DWord);
                using (RegistryKey k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services"))
                    if (k != null) k.SetValue("Shadow", 1, RegistryValueKind.DWord);   // full control, WITH consent
                DebugLog.Log("RDP shadow enabled with consent (Shadow=1)");
            }
            catch (Exception exception) { DebugLog.Log("RDP shadow registry setup failed: " + exception); }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "netsh", "advfirewall firewall set rule group=\"Remote Desktop\" new enable=Yes")
                { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
                DebugLog.Log("netsh: enabling the Remote Desktop firewall rules");
            }
            catch (Exception exception) { DebugLog.Log("netsh failed: " + exception.Message); }
        }

        // Trust the server's self-signed TLS certificate so the agent can POST over https with normal
        // validation. The public cert ships next to the setup as hostwatch.crt (in the download zip);
        // we import it into the machine-wide Trusted Root store and keep a copy so uninstall can remove it.
        static void InstallServerCert(string selfDir)
        {
            string certSource = System.IO.Path.Combine(selfDir, "hostwatch.crt");
            if (!File.Exists(certSource))
            {
                DebugLog.Log("no hostwatch.crt next to the setup - skipping TLS trust (server may be http)");
                return;
            }
            try
            {
                X509Certificate2 certificate = new X509Certificate2(certSource);
                using (X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    store.Add(certificate);
                }
                File.Copy(certSource, Paths.ServerCertPath, true);   // kept so uninstall can find its thumbprint
                DebugLog.Log("trusted server certificate " + certificate.Subject + " (thumbprint " + certificate.Thumbprint + ")");
            }
            catch (Exception exception) { DebugLog.Log("installing server certificate failed: " + exception); }
        }

        static void RemoveServerCert()
        {
            if (!File.Exists(Paths.ServerCertPath)) return;
            try
            {
                X509Certificate2 certificate = new X509Certificate2(Paths.ServerCertPath);
                using (X509Store store = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    foreach (X509Certificate2 found in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false))
                        store.Remove(found);
                }
                DebugLog.Log("removed trusted server certificate " + certificate.Thumbprint);
            }
            catch (Exception exception) { DebugLog.Log("removing server certificate failed: " + exception.Message); }
        }

        static void GrantUsersWrite(string dir)
        {
            try
            {
                DirectoryInfo info = new DirectoryInfo(dir);
                DirectorySecurity security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                    FileSystemRights.Modify | FileSystemRights.Synchronize,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(security);
                DebugLog.Log("granted Users write access to " + dir);
            }
            catch (Exception exception) { DebugLog.Log("granting Users write access to " + dir + " failed: " + exception); }
        }

        static void SetAutostart(string target, string server)
        {
            try
            {
                using (RegistryKey run = Registry.LocalMachine.OpenSubKey(Paths.RunKey, true))
                {
                    if (run != null) run.SetValue(Paths.RunValueName, "\"" + target + "\" -Server \"" + server + "\"");
                    DebugLog.Log(run != null ? "autostart (HKLM): " + target + " -Server " + server
                                             : "autostart: HKLM Run key not found");
                }
            }
            catch (Exception exception) { DebugLog.Log("setting autostart failed: " + exception); }
            // Drop a per-user leftover from an older install so it can't start a second copy.
            try { using (RegistryKey run = Registry.CurrentUser.OpenSubKey(Paths.RunKey, true)) if (run != null) run.DeleteValue(Paths.RunValueName, false); } catch { }
        }

        static void RemoveAutostartAndStopAgents()
        {
            foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
                try { using (RegistryKey run = root.OpenSubKey(Paths.RunKey, true)) if (run != null) run.DeleteValue(Paths.RunValueName, false); }
                catch (Exception exception) { DebugLog.Log("removing autostart failed: " + exception.Message); }

            // Stop every running agent, in any user's session, so its exe is free to overwrite or delete.
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("hostwatch-agent"))
                if (p.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    try
                    {
                        DebugLog.Log("stopping agent pid " + p.Id + " (session " + p.SessionId + ")");
                        p.Kill();
                        DebugLog.Log(p.WaitForExit(3000) ? "pid " + p.Id + " stopped" : "pid " + p.Id + " still running after 3s");
                    }
                    catch (Exception exception) { DebugLog.Log("stopping pid " + p.Id + " failed: " + exception.Message); }
                    finally { try { p.Dispose(); } catch { } }
                }
        }

        // Delete the data folder (both exes, config, buffer) after this process exits - a running exe
        // can't remove its own file, so a short detached cmd waits, then retries until the folder is gone.
        static void ScheduleFolderDelete()
        {
            try
            {
                string dir = Paths.DataDir;
                string cmd = "/c timeout /t 2 >nul & rmdir /s /q \"" + dir + "\"" +
                             " & if exist \"" + dir + "\" (timeout /t 3 >nul & rmdir /s /q \"" + dir + "\")";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmd)
                { WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, CreateNoWindow = true });
                DebugLog.Log("scheduled removal of " + dir);
            }
            catch (Exception exception) { DebugLog.Log("scheduling removal failed: " + exception.Message); }
        }
    }
}
