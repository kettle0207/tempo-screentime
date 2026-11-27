<#
.SYNOPSIS
  Build, sign and publish a Tempo release to GitHub Releases (public repository "<your account>/tempo-screentime").

.DESCRIPTION
  1. Reads your GitHub account from `gh` (run `gh auth login` first).
  2. Writes "<account>/tempo-screentime" into src/Core/UpdateKey.cs (the update source compiled into the app).
  3. Runs the tests, builds the installer and ZIP, signs tempo-update.txt and verifies it.
  4. Creates the public repository on first use, commits and pushes the source.
  5. Creates release v<version> with the installer, ZIP and tempo-update.txt as the latest release.

  Use -DryRun to do steps 1-3 and print what would be published without changing GitHub.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -DryRun
  powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
#>
param(
    [string]$Repo = "tempo-screentime",
    [string]$Key = "",
    [string]$Notes = "",
    [switch]$SkipTests,
    [switch]$DryRun
)
# Native tools (gh, git, dotnet) write progress to stderr; failures are detected by exit code in Run/Need.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
if (-not $Key) { $Key = Join-Path $root "release-keys\update-signing-key.xml" }
if (-not $Notes) { $Notes = Join-Path $root "release-notes.txt" }
function Need($ok, $message) { if (-not $ok) { throw $message } }
function Run($exe) { & $exe @args; if ($LASTEXITCODE -ne 0) { throw "$exe failed ($LASTEXITCODE)" } }

# Toolchain from winget installs (user PATH may not be loaded in this shell yet).
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
if (-not $env:CXX) { $env:CXX = 'g++' }; if (-not $env:WINDRES) { $env:WINDRES = 'windres' }; if (-not $env:DLLTOOL) { $env:DLLTOOL = 'dlltool' }
if (-not $env:MAKENSIS) { $env:MAKENSIS = 'C:\Program Files (x86)\NSIS\makensis.exe' }
$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python -or $python -like '*WindowsApps*') { $python = "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe" }
foreach ($tool in 'gh', 'git', 'dotnet', $env:CXX) { Need (Get-Command $tool -ErrorAction SilentlyContinue) "$tool が見つかりません。RELEASE.md の準備を確認してください。" }
Need (Test-Path $env:MAKENSIS) "NSIS (makensis) が見つかりません。"
Need (Test-Path $Key) "署名鍵 $Key がありません。"
Need (Test-Path $Notes) "変更内容 $Notes がありません。"

# 1. Account
& gh auth status *> $null
if ($LASTEXITCODE -ne 0 -and $DryRun) {
    Write-Host "GitHub 未ログインのため、DryRun は仮の OWNER で続けます。"
    $owner = "OWNER"; $id = "0"
} else {
    Need ($LASTEXITCODE -eq 0) "GitHub にログインしていません。先に 'gh auth login' を実行してください。"
    $owner = (& gh api user --jq .login).Trim()
    $id = (& gh api user --jq .id).Trim()
}
$full = "$owner/$Repo"
$version = ([regex]::Match((Get-Content "$root\src\ScreenTime.csproj" -Raw), '<Version>([^<]+)</Version>')).Groups[1].Value
Write-Host "公開先: https://github.com/$full  バージョン: $version"

# 2. Update source compiled into the app
$keyFile = "$root\src\Core\UpdateKey.cs"
$text = [IO.File]::ReadAllText($keyFile)
$text = [regex]::Replace($text, 'Repository = "[^"]+"', "Repository = `"$full`"")
[IO.File]::WriteAllText($keyFile, $text, (New-Object Text.UTF8Encoding $false))

# 3. Test, build, sign (from a short drive letter: .NET Framework builds hit MAX_PATH in long folders)
$drive = $null
$work = $root
if ($root.Length -gt 150) {
    $drive = @('T', 'U', 'V', 'W', 'X') | Where-Object { -not (Test-Path "${_}:\") } | Select-Object -First 1
    subst "${drive}:" $root
    $work = "${drive}:\"
}
try {
    Push-Location $work
    if (-not $SkipTests) {
        Run dotnet run --project tests/Tempo.Tests.csproj -c Release
        Run $python tests/dashboard.py
        Run node --test tests/extension.test.js
    }
    $env:TEMPO_SIGNING_KEY = (Resolve-Path $Key).Path
    $env:TEMPO_NOTES = (Resolve-Path $Notes).Path
    Run $python (Join-Path $work 'scripts\package.py')
    Run dotnet run --project tools/Release/Tempo.Release.csproj -c Release -- verify dist/tempo-update.txt dist/Tempo-Setup-x64.exe
} finally {
    Pop-Location
    if ($drive) { subst "${drive}:" /D }
}
$assets = "$root\dist\Tempo-Setup-x64.exe", "$root\dist\tempo-update.txt", "$root\dist\Tempo-Windows-x64.zip"
if ($DryRun) {
    Write-Host "DryRun: 公開はしていません。公開されるファイル:"; $assets | ForEach-Object { Write-Host "  $_" }
    return
}

# 4. Repository (public: releases of private repositories need sign-in to download)
Push-Location $root
try {
    if (-not (Test-Path .git)) { Run git init -b main }
    # GitHub's private no-reply address keeps your e-mail out of the public history.
    Run git config user.name $owner
    Run git config user.email "$id+$owner@users.noreply.github.com"
    Run git add -A
    & git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) { Run git commit -m "Tempo $version" }
    & gh repo view $full *> $null
    if ($LASTEXITCODE -ne 0) {
        Run gh repo create $full --public --description "Windows 11 用の軽量スクリーンタイム" --source . --remote origin --push
    } else {
        if (-not (& git remote)) { Run git remote add origin "https://github.com/$full.git" }
        Run git push -u origin main
    }
    # 5. Release
    Run gh release create "v$version" @assets --repo $full --title "Tempo $version" --notes-file $Notes --latest
    $code = & curl.exe -s -o NUL -w "%{http_code}" -L "https://github.com/$full/releases/latest/download/tempo-update.txt"
    Write-Host "公開しました: https://github.com/$full/releases/tag/v$version  (tempo-update.txt HTTP $code)"
} finally { Pop-Location }
