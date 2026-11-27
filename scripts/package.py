"""Build release files. Requires .NET 8 SDK, Python 3, MinGW-w64, NSIS 3 (makensis on PATH).

Set TEMPO_SIGNING_KEY to the private update key (XML) and TEMPO_NOTES to a UTF-8 notes file
to also write dist/tempo-update.txt, the signed manifest the in-app updater reads.
"""
import os
import re
import shutil
import subprocess
import zipfile
from pathlib import Path

root = Path(os.path.abspath(__file__)).parents[1]
sdk = os.environ.get("DOTNET", "dotnet")
version = os.environ.get("TEMPO_VERSION") or re.search(r"<Version>([^<]+)</Version>",
                                                       (root / "src/ScreenTime.csproj").read_text("utf-8")).group(1)
assert re.fullmatch(r"\d+\.\d+\.\d+", version), version
subprocess.run([sdk, "build", str(root / "src/ScreenTime.csproj"), "-c", "Release",
                "-p:UseSharedCompilation=false", f"-p:Version={version}"], check=True)
output = root / "src/bin/Release/net48"
stage = root / "installer/stage"
stage.mkdir(exist_ok=True)
dist = root / "dist"
dist.mkdir(exist_ok=True)
from native import build
build(root, stage)
assets = ["Tempo.Dashboard.exe", "Tempo.Dashboard.exe.config", "Microsoft.Web.WebView2.Core.dll",
          "Microsoft.Web.WebView2.WinForms.dll", "runtimes/win-x64/native/WebView2Loader.dll"]
for name in assets:
    target = stage / name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(output / name, target)
shutil.copyfile(root / "native/THIRD-PARTY.txt", stage / "MinGW-NOTICES.txt")
shutil.copyfile(root / "tests/windows/Measure-Tempo.ps1", stage / "Measure-Tempo.ps1")
shutil.copyfile(root / "installer/browser-host.json", stage / "browser-host.json")
(stage / "Extension").mkdir(exist_ok=True)
for name in ["manifest.json", "worker.js"]:
    shutil.copyfile(root / "extension" / name, stage / "Extension" / name)
cache = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
package = cache / "microsoft.web.webview2/1.0.3650.58"
for name in ["LICENSE", "NOTICE"]:
    shutil.copyfile(package / (name + ".txt"), stage / ("WebView2-" + name + ".txt"))
with zipfile.ZipFile(dist / "Tempo-Windows-x64.zip", "w", zipfile.ZIP_DEFLATED) as archive:
    for name in assets + ["MinGW-NOTICES.txt", "ScreenTime.exe", "Measure-Tempo.exe", "Measure-Tempo.ps1", "Tempo.BrowserHost.exe", "browser-host.json", "Extension/manifest.json",
                          "Extension/worker.js", "WebView2-LICENSE.txt", "WebView2-NOTICE.txt"]:
        archive.write(stage / name, Path("Tempo") / name)
subprocess.run([os.environ.get("MAKENSIS", "makensis"), f"/DVERSION={version}", "Tempo.nsi"], cwd=root / "installer", check=True)
key = os.environ.get("TEMPO_SIGNING_KEY")
if key:
    notes = os.environ.get("TEMPO_NOTES") or str(root / "release-notes.txt")
    subprocess.run([sdk, "run", "--project", str(root / "tools/Release/Tempo.Release.csproj"), "-c", "Release", "--",
                    "sign", key, str(dist / "Tempo-Setup-x64.exe"), version, notes, str(dist)], check=True)
print("built Tempo", version)
