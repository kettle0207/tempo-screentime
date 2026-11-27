"""Build the native x64 resident and browser host with MinGW-w64."""
import os
import subprocess
from pathlib import Path

def build(root, output):
    work = root / "native"
    compiler = os.environ.get("CXX", "x86_64-w64-mingw32-g++")
    windres = os.environ.get("WINDRES", "x86_64-w64-mingw32-windres")
    dlltool = os.environ.get("DLLTOOL", "x86_64-w64-mingw32-dlltool")
    obj = work / "obj"
    obj.mkdir(exist_ok=True)
    def run(*args):
        subprocess.run(args, cwd=work, check=True)
    run(windres, "-I", ".", "-i", "app.rc", "-o", obj / "app.o", "-O", "coff")
    run(dlltool, "-d", "sqlite.def", "-l", obj / "libwinsqlite.a")
    flags = ["-std=c++17", "-Os", "-s", "-static", "-DUNICODE", "-D_UNICODE",
             "-D_WIN32_WINNT=0x0A00", "-Wall", "-Wextra"]
    extra = os.environ.get("TEMPO_CXXFLAGS", "").split()
    flags += extra
    run(compiler, *flags, "-mwindows", "-municode", "recorder.cpp", "channel.cpp", obj / "app.o",
        obj / "libwinsqlite.a", "-lwtsapi32", "-lshell32", "-ladvapi32", "-o", output / "ScreenTime.exe")
    run(compiler, *flags, "browser.cpp", "-o", output / "Tempo.BrowserHost.exe")
    run(compiler, *flags, "-municode", "measure.cpp", obj / "app.o", obj / "libwinsqlite.a",
        "-lpsapi", "-lpdh", "-lshell32", "-ladvapi32", "-o", output / "Measure-Tempo.exe")
