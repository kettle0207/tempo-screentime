// In-app update: signature, integrity, interrupted downloads and offline behaviour against a loopback feed.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Tempo;

internal static class UpdateTests
{
    internal static byte[] Sign(RSA key, string version, byte[] installer, string notes = "- 新しい画面\n- アプリ内更新", string file = "Tempo-Setup-x64.exe", string minimum = "")
    {
        var body = $"format=tempo-update/1\nversion={version}\nfile={file}\nsize={installer.Length}\nsha256={UpdateClient.Hex(SHA256.HashData(installer))}\ndate=2026-11-27\n" +
                   (minimum.Length > 0 ? $"minimum={minimum}\n" : "") + "\n" + notes + "\n";
        var data = new UTF8Encoding(false).GetBytes(body);
        var sig = key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Encoding.ASCII.GetBytes(Convert.ToBase64String(sig) + "\n").Concat(data).ToArray();
    }

    sealed class Feed : IDisposable
    {
        readonly HttpListener listener = new();
        public readonly string Base;
        public byte[] Manifest = Array.Empty<byte>(), Installer = Array.Empty<byte>();
        public bool Truncate;
        // Runs after the first megabyte of the installer is sent and before the rest, so a test can act mid-download
        // regardless of how fast the machine or the loopback connection is.
        public Action? MidDownload;
        public readonly List<string> Requests = new();
        public Feed()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Base = $"http://localhost:{port}/releases/";
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();
            Task.Run(Serve);
        }
        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext c;
                try { c = await listener.GetContextAsync(); } catch { return; }
                var path = c.Request.Url!.AbsolutePath;
                lock (Requests) Requests.Add(c.Request.HttpMethod + " " + c.Request.Url.PathAndQuery + " UA=" + c.Request.UserAgent + " Cookie=" + (c.Request.Headers["Cookie"] ?? ""));
                byte[]? body = path.EndsWith("/latest/download/tempo-update.txt") ? Manifest : path.Contains("/download/v") ? Installer : null;
                try
                {
                    if (body == null) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                    c.Response.ContentLength64 = body.Length;
                    if (Truncate && body == Installer)
                    {
                        await c.Response.OutputStream.WriteAsync(body.AsMemory(0, body.Length / 2));
                        await c.Response.OutputStream.FlushAsync();
                        c.Response.Abort();
                        continue;
                    }
                    if (MidDownload != null && body == Installer && body.Length > 1_000_000)
                    {
                        await c.Response.OutputStream.WriteAsync(body.AsMemory(0, 1_000_000));
                        await c.Response.OutputStream.FlushAsync();
                        MidDownload();
                        await c.Response.OutputStream.WriteAsync(body.AsMemory(1_000_000));
                        c.Response.Close();
                        continue;
                    }
                    await c.Response.OutputStream.WriteAsync(body);
                    c.Response.Close();
                }
                catch { }
            }
        }
        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    static void Expect(bool ok, string message) => Tests.Check(ok, message);
    static Exception? Catch(Action a) { try { a(); return null; } catch (Exception e) { return e; } }

    public static void Run(string dir)
    {
        using var key = RSA.Create(3072);
        using var other = RSA.Create(3072);
        var pub = key.ExportParameters(false);
        var installer = new byte[3_000_000]; new Random(7).NextBytes(installer);
        var folder = Path.Combine(dir, "Updates");

        Expect(UpdateManifest.Compare("1.10.0", "1.9.9") > 0 && UpdateManifest.Compare("1.2.0", "1.2.0") == 0, "semantic version ordering");
        var info = UpdateManifest.Parse(Sign(key, "1.3.0", installer), pub);
        Expect(info.version == "1.3.0" && info.size == installer.Length && info.notes.Contains("アプリ内更新"), "signed manifest parses version, size and notes");
        Expect(Catch(() => UpdateManifest.Parse(Sign(other, "1.3.0", installer), pub)) is UpdateException e1 && e1.Message.Contains("署名"), "manifest signed by another key is rejected");
        var tampered = Sign(key, "1.3.0", installer); tampered[^3] ^= 1;
        Expect(Catch(() => UpdateManifest.Parse(tampered, pub)) is UpdateException, "edited manifest body is rejected");
        Expect(Catch(() => UpdateManifest.Parse(Sign(key, "1.3.0", installer, file: "../evil.exe"), pub)) is UpdateException, "path traversal in file name is rejected");
        Expect(Catch(() => UpdateManifest.Parse(Encoding.ASCII.GetBytes("no signature"), pub)) is UpdateException, "unsigned manifest is rejected");
        Expect(Catch(() => new UpdateClient("http://example.com/", "1.2.0", folder, pub)) is UpdateException, "plain HTTP feed outside loopback is refused");
        Expect(UpdateManifest.PublicKey.Modulus!.Length == 384, "release public key is embedded (RSA-3072)");

        using (var feed = new Feed())
        {
            feed.Manifest = Sign(key, "1.3.0", installer); feed.Installer = installer;
            var client = new UpdateClient(feed.Base, "1.2.0", folder, pub);
            var found = client.Check(CancellationToken.None, out _);
            Expect(found?.version == "1.3.0", "newer release is offered");
            Expect(new UpdateClient(feed.Base, "1.3.0", folder, pub).Check(CancellationToken.None, out var latest) == null && latest.version == "1.3.0", "current release reports up to date");
            Expect(new UpdateClient(feed.Base, "1.4.0", folder, pub).Check(CancellationToken.None, out _) == null, "older release is never offered (no downgrade)");
            long last = 0;
            var path = client.Download(found!, (r, t) => last = r, CancellationToken.None);
            Expect(File.ReadAllBytes(path).SequenceEqual(installer) && last == installer.Length, "installer downloads and verifies");
            using (var locked = UpdateClient.OpenVerified(path, found!))
                Expect(Catch(() => File.WriteAllBytes(path, new byte[1])) != null, "verified installer is write-locked until launch");

            File.Delete(path);
            feed.Truncate = true;
            Expect(Catch(() => client.Download(found!, null, CancellationToken.None)) is UpdateException ue && ue.Message.Contains("現在の版"), "interrupted download fails with keep-current message");
            Expect(!Directory.EnumerateFiles(folder).Any(), "interrupted download leaves no file");
            feed.Truncate = false;

            var corrupt = (byte[])installer.Clone(); corrupt[1000] ^= 0xFF; feed.Installer = corrupt;
            Expect(Catch(() => client.Download(found!, null, CancellationToken.None)) is UpdateException ce && ce.Message.Contains("破損"), "installer that differs from the signed hash is rejected");
            Expect(!Directory.EnumerateFiles(folder).Any(), "rejected installer is deleted");
            feed.Installer = installer;

            feed.Manifest = Sign(other, "9.9.9", installer);
            Expect(Catch(() => client.Check(CancellationToken.None, out _)) is UpdateException se && se.Message.Contains("署名"), "feed with foreign signature offers nothing");
            feed.Manifest = Sign(key, "1.3.0", installer, minimum: "1.2.5");
            Expect(Catch(() => client.Check(CancellationToken.None, out _)) is UpdateException, "minimum version gate is honoured");

            using var cancel = new CancellationTokenSource();
            var big = new byte[60_000_000]; feed.Installer = big;
            var bigInfo = UpdateManifest.Parse(Sign(key, "1.3.0", big), pub);
            // Cancel from the feed side after 1 MB: progress callbacks are throttled to 150 ms, and a fast machine can
            // finish the whole loopback download before the first one fires.
            feed.MidDownload = () => cancel.Cancel();
            Expect(Catch(() => client.Download(bigInfo, null, cancel.Token)) is OperationCanceledException, "download can be cancelled");
            feed.MidDownload = null;
            Expect(!Directory.EnumerateFiles(folder).Any(), "cancelled download leaves no file");

            lock (feed.Requests)
            {
                Expect(feed.Requests.All(r => !r.Contains('?') && r.Contains("UA=Tempo-Updater ") && r.EndsWith("Cookie=")), "requests carry no query, cookie or identifier beyond a generic user agent");
                Expect(feed.Requests.All(r => r.Contains("/latest/download/tempo-update.txt") || r.Contains("/download/v1.3.0/Tempo-Setup-x64.exe")), "only the manifest and installer are requested");
            }
        }
        // Nothing listens on this port: behaves like a disconnected network.
        var down = new UpdateClient("http://localhost:9/releases/", "1.2.0", folder, pub) { Timeout = 3000 };
        Expect(Catch(() => down.Check(CancellationToken.None, out _)) is UpdateException oe && oe.Message.Contains("接続"), "offline check reports a connection error");
    }
}
