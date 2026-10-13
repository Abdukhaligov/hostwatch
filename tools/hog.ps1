# hog.ps1 - hold RAM resident to test the dashboard "free" button.
# Run on the target PC as the same user the agent runs under, then watch that
# PC's MEMORY on the dashboard, click "free", and it should drop within a second.
#   powershell -ExecutionPolicy Bypass -File hog.ps1
$chunks = New-Object System.Collections.Generic.List[byte[]]
$gb = 4                                           # lower this on PCs with little RAM
for ($n = 0; $n -lt $gb * 2; $n++) {
    $b = New-Object byte[] 536870912              # 512 MB
    for ($i = 0; $i -lt $b.Length; $i += 4096) { $b[$i] = 1 }   # touch each page so it stays resident
    $chunks.Add($b)
}
Write-Host "Holding ~$gb GB. Watch the dashboard, click 'free', then press Enter to release."
[void](Read-Host)
