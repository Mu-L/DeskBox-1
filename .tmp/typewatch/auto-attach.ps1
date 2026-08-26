# Auto-attach loop:
#  - watches for a DeskBox.exe under a wingezi worktree
#  - on a NEW pid, starts typewatch.exe sampling it every 5s to auto-trace.csv
#  - when that DeskBox exits, waits for the next one
# Marker lines "### NEW PROCESS PID=..." delimit sample blocks.

$watchCsv = "D:\project\wingezi\.tmp\typewatch\auto-trace.csv"
$twExe    = "D:\project\wingezi\.tmp\typewatch\bin\Debug\net10.0\typewatch.exe"
$currentPid = -1
$twProc    = $null

while ($true) {
    $p = Get-Process DeskBox -ErrorAction SilentlyContinue |
         Where-Object { $_.Path -and $_.Path -like "*wingezi*" } |
         Select-Object -First 1

    if ($null -ne $p) {
        if ($p.Id -ne $currentPid) {
            # new instance -> (re)start sampler
            if ($twProc -and -not $twProc.HasExited) { Stop-Process -Id $twProc.Id -Force -ErrorAction SilentlyContinue }
            $currentPid = $p.Id
            Add-Content -Path $watchCsv -Value ("### NEW PROCESS PID=" + $currentPid + " at " + (Get-Date -Format HH:mm:ss)) -Encoding utf8
            $twProc = Start-Process -FilePath $twExe -ArgumentList ($currentPid.ToString(), "5000", $watchCsv) -WindowStyle Hidden -PassThru
            Write-Output ("attached typewatch to PID=" + $currentPid + " ws=" + [math]::Round($p.WorkingSet64/1MB) + "MB")
        }
        $p.Dispose()
    } else {
        if ($currentPid -ne -1) {
            Write-Output ("DeskBox PID=" + $currentPid + " exited at " + (Get-Date -Format HH:mm:ss))
            Add-Content -Path $watchCsv -Value ("### PROCESS EXITED PID=" + $currentPid + " at " + (Get-Date -Format HH:mm:ss)) -Encoding utf8
            if ($twProc -and -not $twProc.HasExited) { Stop-Process -Id $twProc.Id -Force -ErrorAction SilentlyContinue }
            $currentPid = -1
        }
    }
    Start-Sleep -Seconds 2
}