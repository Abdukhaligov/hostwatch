# hostwatch agent - reports this PC's health to the hostwatch server every second.
# Runs hidden with a tray icon: Set label / Open dashboard / Change server / Exit.
# Collects: hostname, user, OS, MAC/IP/link speed, CPU, memory, disks, uptime and
# keyboard/mouse idle time (GetLastInputInfo - only *when* input happened, never what).
param([string]$Server = '')

$ErrorActionPreference = 'Stop'
$Version = '1.2'
$IntervalMs = 1000          # live counters (CPU, memory, network, idle)
$StaticEverySec = 60        # slow-changing facts (model, OS, adapters, disks)
$AppDir = Join-Path $env:LOCALAPPDATA 'Hostwatch'
$ConfigPath = Join-Path $AppDir 'config.json'
New-Item -ItemType Directory -Force -Path $AppDir | Out-Null

# One instance per user session.
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Local\HostwatchAgent', [ref]$created)
if (-not $created) { exit }

Add-Type -AssemblyName System.Windows.Forms, System.Drawing, Microsoft.VisualBasic
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HostwatchIdle {
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    public static uint Seconds() {
        LASTINPUTINFO info = new LASTINPUTINFO();
        info.cbSize = (uint)Marshal.SizeOf(info);
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000;
    }
}
'@

# ---------- config ----------
$config = @{ server = ''; label = ''; label_set_at = 0 }
if (Test-Path $ConfigPath) {
    try {
        $saved = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($key in @('server', 'label', 'label_set_at')) {
            if ($null -ne $saved.$key) { $config[$key] = $saved.$key }
        }
    } catch { }
}
function Save-Config { $config | ConvertTo-Json | Set-Content -Path $ConfigPath -Encoding UTF8 }
function Now-Unix { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 }

if ($Server) { $config.server = $Server.TrimEnd('/'); Save-Config }
if (-not $config.server) {
    $answer = [Microsoft.VisualBasic.Interaction]::InputBox(
        'hostwatch server address, e.g. http://192.168.31.6:8765', 'hostwatch', 'http://')
    if (-not $answer -or $answer -eq 'http://') { exit }
    $config.server = $answer.TrimEnd('/'); Save-Config
}

# ---------- collect ----------
$script:static = $null
$script:staticAt = [datetime]::MinValue

# Model, OS, adapters, disks: refreshed once a minute, they barely change.
function Collect-Static {
    $os = Get-CimInstance Win32_OperatingSystem
    $cs = Get-CimInstance Win32_ComputerSystem
    $cpus = @(Get-CimInstance Win32_Processor)

    $nics = @(Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' | Where-Object { $_.MACAddress })
    $networks = foreach ($nic in $nics) {
        $adapter = Get-CimInstance Win32_NetworkAdapter -Filter "Index=$($nic.Index)"
        [ordered]@{
            name    = if ($adapter.NetConnectionID) { $adapter.NetConnectionID } else { $nic.Description }
            adapter = $nic.Description
            mac     = ($nic.MACAddress -replace '-', ':').ToLower()
            ip      = @($nic.IPAddress | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+$' }) -join ', '
            gateway = @($nic.DefaultIPGateway) -join ', '
            speed   = if ($adapter.Speed) { [double]$adapter.Speed } else { $null }
            primary = [bool]$nic.DefaultIPGateway
        }
    }
    $primary = @($networks | Where-Object { $_.primary }) + @($networks) | Select-Object -First 1

    $disks = foreach ($d in @(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3')) {
        [ordered]@{
            name  = $d.DeviceID
            label = $d.VolumeName
            fs    = $d.FileSystem
            total = [double]$d.Size
            free  = [double]$d.FreeSpace
        }
    }

    $script:static = @{
        primary  = $primary
        networks = @($networks)
        disks    = @($disks)
        os       = "$($os.Caption) $($os.Version)"
        model    = "$($cs.Manufacturer) $($cs.Model)".Trim()
        boot     = $os.LastBootUpTime
        cpu_name = ($cpus[0].Name -replace '\s+', ' ').Trim()
        cores    = ($cpus | Measure-Object NumberOfCores -Sum).Sum
        threads  = ($cpus | Measure-Object NumberOfLogicalProcessors -Sum).Sum
    }
    $script:staticAt = Get-Date
}

# CPU, memory, network and idle: cheap counters, read every second.
function Collect {
    if (((Get-Date) - $script:staticAt).TotalSeconds -ge $StaticEverySec) { Collect-Static }
    $st = $script:static
    $load = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
    $mem = Get-CimInstance Win32_OperatingSystem -Property TotalVisibleMemorySize, FreePhysicalMemory
    $io = @(Get-CimInstance Win32_PerfFormattedData_Tcpip_NetworkInterface |
        Where-Object { $_.Name -notmatch 'Loopback|isatap|Teredo|Pseudo' })

    [ordered]@{
        id             = $st.primary.mac
        agent_version  = $Version
        hostname       = $env:COMPUTERNAME
        user           = "$env:USERDOMAIN\$env:USERNAME"
        os             = $st.os
        model          = $st.model
        ip             = $st.primary.ip
        mac            = $st.primary.mac
        networks       = $st.networks
        cpu            = [ordered]@{
            percent = [double]$load
            name    = $st.cpu_name
            cores   = $st.cores
            threads = $st.threads
        }
        memory         = [ordered]@{
            total = [double]$mem.TotalVisibleMemorySize * 1024
            used  = ([double]$mem.TotalVisibleMemorySize - [double]$mem.FreePhysicalMemory) * 1024
        }
        disks          = $st.disks
        net_io         = [ordered]@{
            rx_bps = [double](($io | Measure-Object BytesReceivedPersec -Sum).Sum)
            tx_bps = [double](($io | Measure-Object BytesSentPersec -Sum).Sum)
        }
        uptime_seconds = [math]::Round(((Get-Date) - $st.boot).TotalSeconds)
        idle_seconds   = [HostwatchIdle]::Seconds()
    }
}

# ---------- tray ----------
function New-DotIcon([System.Drawing.Color]$color) {
    $bmp = New-Object System.Drawing.Bitmap 16, 16
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $color), 2, 2, 12, 12)
    $g.DrawEllipse((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(40, 40, 40))), 2, 2, 12, 12)
    $g.Dispose()
    [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}
$iconOk = New-DotIcon ([System.Drawing.Color]::FromArgb(0, 200, 230))
$iconErr = New-DotIcon ([System.Drawing.Color]::FromArgb(230, 60, 80))

$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = $iconOk
$tray.Text = 'hostwatch: starting'
$tray.Visible = $true

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$miStatus = $menu.Items.Add('Starting...')
$miStatus.Enabled = $false
$miLabel = $menu.Items.Add('Set label...')
$miOpen = $menu.Items.Add('Open dashboard')
$miServer = $menu.Items.Add('Change server...')
[void]$menu.Items.Add('-')
$miExit = $menu.Items.Add('Exit')
$tray.ContextMenuStrip = $menu

$script:lastStatus = 'Starting...'
$script:lastOk = $true

function Update-Tray {
    $name = if ($config.label) { $config.label } else { $env:COMPUTERNAME }
    $text = "hostwatch: $name - $script:lastStatus"
    $tray.Text = if ($text.Length -gt 63) { $text.Substring(0, 63) } else { $text }
    $tray.Icon = if ($script:lastOk) { $iconOk } else { $iconErr }
    $miStatus.Text = "$name - $script:lastStatus"
}

function Send-Report {
    try {
        $report = Collect
        $report.label = $config.label
        $report.label_set_at = $config.label_set_at
        $json = $report | ConvertTo-Json -Depth 6 -Compress
        $body = [System.Text.Encoding]::UTF8.GetBytes($json)
        $resp = Invoke-RestMethod -Uri "$($config.server)/api/report" -Method Post -Body $body `
            -ContentType 'application/json; charset=utf-8' -TimeoutSec 3 -UseBasicParsing
        # A label changed on the dashboard comes back here
        if ($resp.label_set_at -gt $config.label_set_at) {
            $config.label = if ($resp.label) { $resp.label } else { '' }
            $config.label_set_at = $resp.label_set_at
            Save-Config
        }
        $script:lastOk = [bool]$resp.ok
        $script:lastStatus = if ($resp.ok) { "sent $(Get-Date -Format 'HH:mm:ss')" } else { "rejected: $($resp.error)" }
    } catch {
        $script:lastOk = $false
        $script:lastStatus = "error: $($_.Exception.Message)"
    }
    Update-Tray
}

$miLabel.add_Click({
    $answer = [Microsoft.VisualBasic.Interaction]::InputBox(
        "Label for this PC (shown on the dashboard).`nLeave empty to keep the current one.", 'hostwatch', $config.label)
    if ($answer) {
        $config.label = $answer.Trim()
        $config.label_set_at = Now-Unix
        Save-Config
        Send-Report
    }
})
$miOpen.add_Click({ Start-Process $config.server })
$miServer.add_Click({
    $answer = [Microsoft.VisualBasic.Interaction]::InputBox('hostwatch server address', 'hostwatch', $config.server)
    if ($answer) { $config.server = $answer.TrimEnd('/'); Save-Config; Send-Report }
})
$miExit.add_Click({
    $tray.Visible = $false
    [System.Windows.Forms.Application]::Exit()
})

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $IntervalMs
$timer.add_Tick({ Send-Report })
$timer.Start()
Send-Report

[System.Windows.Forms.Application]::Run()
$tray.Dispose()
$mutex.ReleaseMutex()
