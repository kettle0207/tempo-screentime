# Run the same local measurement executable included in the release.
param(
    [ValidateRange(10,86400)][int]$Seconds = 120,
    [string]$InstallDirectory = ""
)
$ErrorActionPreference = "Stop"
if (-not $InstallDirectory -and (Test-Path (Join-Path $PSScriptRoot "Measure-Tempo.exe"))) {
    $InstallDirectory = $PSScriptRoot
}
if (-not $InstallDirectory) {
    $install = Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TempoScreenTime" -ErrorAction SilentlyContinue
    if ($install) { $InstallDirectory = $install.InstallLocation }
}
if (-not $InstallDirectory) { throw "Specify -InstallDirectory for the portable edition." }
& (Join-Path $InstallDirectory "Measure-Tempo.exe") --seconds $Seconds --no-open
exit $LASTEXITCODE
