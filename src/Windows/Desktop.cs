using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Tempo
{
    internal static class Program
    {
        internal static readonly string Home = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tempo", "ScreenTime");
        internal static readonly string Resident = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ScreenTime.exe");

        [STAThread]
        static void Main()
        {
            if (RecorderClient.Window == IntPtr.Zero)
            {
                Process.Start(Resident);
                return;
            }
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var desktop = new Desktop())
                using (var window = new Dashboard(desktop))
                {
                    RecorderClient.GetWindowThreadProcessId(RecorderClient.Window, out int pid);
                    if (pid == 0) return;
                    using (var owner = Process.GetProcessById(pid))
                    {
                        var handle = window.Handle;
                        owner.SynchronizingObject = window;
                        owner.Exited += (s, e) => { if (!window.IsDisposed) window.Close(); };
                        owner.EnableRaisingEvents = true;
                        if (!owner.HasExited) Application.Run(window);
                    }
                }
            }
            catch (Exception e) { MessageBox.Show(e.Message, "Tempo", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    // These commands only cross the current Windows desktop, and carry no browsing history.
    internal sealed class RecorderClient
    {
        const uint First = 0x8000 + 41;
        [DllImport("user32", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32")]
        static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, UIntPtr value, IntPtr unused,
            uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        internal static IntPtr Window => FindWindow("Tempo.ScreenTime.Recorder.1", null);
        static uint Command(uint offset, uint value = 0)
        {
            var hwnd = Window;
            if (hwnd == IntPtr.Zero || SendMessageTimeout(hwnd, First + offset, new UIntPtr(value),
                IntPtr.Zero, 2, 5000, out var result) == IntPtr.Zero)
                throw new InvalidOperationException("常駐本体からの応答がありません。");
            return result.ToUInt32();
        }
        static void Update(uint offset, uint value = 0)
        {
            if (Command(offset, value) != 1) throw new IOException("記録を保存できません。");
        }
        public void Flush() => Update(1);
        public void Pause(bool pause) => Update(2, pause ? 1u : 0u);
        public void SetIdle(int seconds) => Update(3, checked((uint)seconds));
        public uint Status => Command(4);
    }

    internal sealed class Desktop : IDisposable
    {
        internal readonly History History = new History(Path.Combine(Program.Home, "history.sqlite3"));
        internal readonly RecorderClient Recorder = new RecorderClient();
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string StartupCommand => "\"" + Program.Resident + "\" --background";
        internal bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return string.Equals(k?.GetValue("Tempo ScreenTime") as string, StartupCommand, StringComparison.OrdinalIgnoreCase);
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("Tempo ScreenTime", StartupCommand);
                    else k.DeleteValue("Tempo ScreenTime", false);
                }
                History.Set("startupDesired", value ? "true" : "false");
            }
        }
        public void Dispose() => History.Dispose();
    }
}
