using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Tempo
{
    // Runs only inside the dashboard process and only when the user presses a button: no polling, no resident updater.
    internal sealed class Updater
    {
        internal static readonly string Current = CurrentVersion();
        internal static readonly string Folder = Path.Combine(Program.Home, "Updates");
        readonly Desktop desktop;
        readonly Action<object> send;
        readonly Action exit;
        CancellationTokenSource work;
        UpdateInfo found;
        string state = "idle";

        public Updater(Desktop desktop, Action<object> send, Action exit) { this.desktop = desktop; this.send = send; this.exit = exit; }

        static string CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor + "." + v.Build;
        }
        // The feed may be redirected for release testing; the embedded public key is still required.
        static string Feed => Environment.GetEnvironmentVariable("TEMPO_UPDATE_FEED") is string f && f.Length > 0 ? f : UpdateClient.DefaultFeed;
        static string InstallDirectory => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        static bool Installed
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\TempoScreenTime"))
                {
                    var location = (k?.GetValue("InstallLocation") as string ?? "").TrimEnd('\\');
                    return string.Equals(location, InstallDirectory, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(InstallDirectory, "Uninstall.exe"));
                }
            }
        }
        bool Busy => state == "checking" || state == "downloading" || state == "verifying" || state == "installing";

        void Publish(string next, object extra = null)
        {
            state = next;
            send(new { update = Snapshot(extra) });
        }
        object Snapshot(object extra)
        {
            var e = extra as Progress;
            return new
            {
                state, current = Current, version = found?.version ?? "", notes = found?.notes ?? "", size = found?.size ?? 0,
                date = found?.date ?? "", portable = !Installed, received = e?.received ?? 0, total = e?.total ?? 0,
                message = extra as string ?? ""
            };
        }
        sealed class Progress { public long received, total; }

        public async void Check()
        {
            if (Busy) return;
            work = new CancellationTokenSource();
            found = null;
            Publish("checking");
            try
            {
                var client = new UpdateClient(Feed, Current, Folder);
                UpdateInfo latest = null;
                var newer = await Task.Run(() => client.Check(work.Token, out latest));
                found = newer;
                CleanDownloads(newer);
                Publish(newer == null ? "latest" : "available");
            }
            catch (UpdateException e) { found = null; Publish("error", e.Message); }
            catch (OperationCanceledException) { Publish("idle"); }
            catch (Exception e) { found = null; Publish("error", "アップデートを確認できませんでした。" + e.Message); }
        }

        public async void Install()
        {
            if (Busy || found == null) return;
            if (!Installed) { Publish("error", "ZIP 版はアプリ内で更新できません。インストーラー版をお使いください。"); return; }
            var info = found;
            work = new CancellationTokenSource();
            Publish("downloading", new Progress { total = info.size });
            string path;
            try
            {
                var client = new UpdateClient(Feed, Current, Folder);
                path = await Task.Run(() => client.Download(info, (r, t) => send(new { update = Snapshot(new Progress { received = r, total = t }) }), work.Token));
                Publish("verifying");
                using (var locked = await Task.Run(() => UpdateClient.OpenVerified(path, info)))
                {
                    Publish("installing");
                    // Save in-memory usage before the installer stops the resident.
                    await Task.Run(() => desktop.Recorder.Flush());
                    desktop.History.Set("updatePending", info.version);
                    desktop.History.Set("updateFrom", Current);
                    var start = new ProcessStartInfo(path, "/S /UPDATE /D=" + InstallDirectory)
                    { UseShellExecute = false, WorkingDirectory = Folder };
                    using (Process.Start(start)) { }
                    // Keep writers blocked until the installer image is mapped.
                    await Task.Delay(1500);
                }
                exit();
            }
            catch (OperationCanceledException) { Publish("available"); }
            catch (UpdateException e) { Publish("error", e.Message); }
            catch (Exception e)
            {
                desktop.History.Set("updatePending", "");
                Publish("error", "更新を開始できませんでした。現在の版を引き続き使用できます。" + e.Message);
            }
        }

        public void Cancel() { if (state == "downloading" || state == "checking") work?.Cancel(); }

        // After a restart, report the outcome of the last in-app update once.
        public string StartupMessage()
        {
            var pending = desktop.History.Get("updatePending", "");
            if (pending.Length == 0) return "";
            desktop.History.Set("updatePending", "");
            var from = desktop.History.Get("updateFrom", "");
            CleanDownloads(null);
            if (UpdateManifest.TryVersion(pending, out _) && UpdateManifest.Compare(Current, pending) >= 0)
                return "Tempo " + Current + " に更新しました。" + (from.Length > 0 ? "（" + from + " から）" : "") + "履歴と設定を引き継いで記録を再開しています。";
            return "Tempo " + pending + " への更新を完了できませんでした。現在の " + Current + " で記録を続けています。";
        }

        static void CleanDownloads(UpdateInfo keep)
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (var f in Directory.GetFiles(Folder))
                    if (keep == null || !string.Equals(Path.GetFileName(f), keep.file, StringComparison.OrdinalIgnoreCase)) UpdateClient.TryDelete(f);
            }
            catch (Exception) { }
        }
    }
}
