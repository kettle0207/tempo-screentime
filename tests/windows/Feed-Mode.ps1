# Restart the loopback update feed in the given mode (normal|truncate|badsig|corrupt|offline).
param([string]$Mode, [string]$Feed)
$pidFile = Join-Path $Feed "server.pid"
if (Test-Path $pidFile) { Stop-Process -Id (Get-Content $pidFile) -ErrorAction SilentlyContinue; Remove-Item $pidFile }
if ($Mode -eq "offline") { "feed stopped (offline)"; return }
$py = "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe"
$p = Start-Process $py -ArgumentList '-I', "`"$PSScriptRoot\update_server.py`"", "`"$Feed`"", '8765', $Mode -WindowStyle Hidden -PassThru
Set-Content $pidFile $p.Id
Start-Sleep -Milliseconds 700
"feed mode $Mode (pid $($p.Id))"
