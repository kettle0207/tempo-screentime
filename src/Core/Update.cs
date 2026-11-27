using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Tempo
{
    internal sealed class UpdateException : Exception
    {
        public UpdateException(string message, Exception inner = null) : base(message, inner) { }
    }

    internal sealed class UpdateInfo
    {
        public string version, notes, file, sha256, date, minimum;
        public long size;
    }

    // Release manifest: line 1 is a base64 RSA-PSS/SHA-256 signature of every byte after the first LF.
    // The signed body is "key=value" header lines, a blank line, then the release notes.
    internal static class UpdateManifest
    {
        internal const string Name = "tempo-update.txt";
        internal const string Format = "tempo-update/1";
        const int MaxBytes = 64 * 1024;
        internal const long MaxInstaller = 200L * 1024 * 1024;

        internal static RSAParameters PublicKey => new RSAParameters
        {
            Modulus = Convert.FromBase64String(UpdateKey.Modulus),
            Exponent = Convert.FromBase64String(UpdateKey.Exponent)
        };

        internal static RSA NewRsa()
        {
#if NETFRAMEWORK
            return new RSACng();
#else
            return RSA.Create();
#endif
        }

        public static UpdateInfo Parse(byte[] data, RSAParameters key)
        {
            if (data == null || data.Length == 0 || data.Length > MaxBytes) throw new UpdateException("更新情報の形式が正しくありません。");
            int lf = Array.IndexOf(data, (byte)'\n');
            if (lf <= 0) throw new UpdateException("更新情報に署名がありません。");
            byte[] signature;
            try { signature = Convert.FromBase64String(Encoding.ASCII.GetString(data, 0, lf).Trim()); }
            catch (FormatException) { throw new UpdateException("更新情報の署名を読み取れません。"); }
            var body = new byte[data.Length - lf - 1];
            Buffer.BlockCopy(data, lf + 1, body, 0, body.Length);
            bool valid;
            using (var rsa = NewRsa())
            {
                rsa.ImportParameters(key);
                try { valid = rsa.VerifyData(body, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss); }
                catch (CryptographicException) { valid = false; }
            }
            if (!valid) throw new UpdateException("更新情報の署名が正規の配布元と一致しません。更新を中止しました。");

            string text;
            try { text = new UTF8Encoding(false, true).GetString(body); }
            catch (ArgumentException) { throw new UpdateException("更新情報の文字コードが正しくありません。"); }
            text = text.Replace("\r\n", "\n");
            int split = text.IndexOf("\n\n", StringComparison.Ordinal);
            var head = split < 0 ? text : text.Substring(0, split);
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in head.Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                fields[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            string Field(string name) => fields.TryGetValue(name, out var v) && v.Length > 0 ? v : throw new UpdateException("更新情報に " + name + " がありません。");
            if (Field("format") != Format) throw new UpdateException("この版では読み取れない更新情報です。");
            var info = new UpdateInfo
            {
                version = Field("version"), file = Field("file"), sha256 = Field("sha256").ToLowerInvariant(),
                notes = split < 0 ? "" : text.Substring(split + 2).Trim(),
                date = fields.TryGetValue("date", out var d) ? d : "",
                minimum = fields.TryGetValue("minimum", out var m) ? m : ""
            };
            if (!TryVersion(info.version, out _)) throw new UpdateException("更新情報のバージョンが正しくありません。");
            if (!Regex.IsMatch(info.file, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,80}\.exe$")) throw new UpdateException("更新ファイル名が正しくありません。");
            if (!Regex.IsMatch(info.sha256, "^[0-9a-f]{64}$")) throw new UpdateException("更新情報のハッシュが正しくありません。");
            if (!long.TryParse(Field("size"), NumberStyles.None, CultureInfo.InvariantCulture, out info.size) || info.size <= 0 || info.size > MaxInstaller)
                throw new UpdateException("更新ファイルのサイズが正しくありません。");
            return info;
        }

        public static bool TryVersion(string s, out Version v)
        {
            v = null;
            return s != null && Regex.IsMatch(s, @"^\d{1,5}\.\d{1,5}\.\d{1,5}$") && Version.TryParse(s, out v);
        }
        public static int Compare(string a, string b)
        {
            if (!TryVersion(a, out var x) || !TryVersion(b, out var y)) throw new ArgumentException("version");
            return x.CompareTo(y);
        }
    }

    // Fetches only the signed manifest and the installer. No cookies, identifiers or history are sent.
    internal sealed class UpdateClient
    {
        internal const string DefaultFeed = "https://github.com/" + UpdateKey.Repository + "/releases/";
        readonly Uri feed;
        readonly string current, folder;
        readonly RSAParameters key;
        public int Timeout = 20000;

        public UpdateClient(string feedBase, string currentVersion, string downloadFolder, RSAParameters? publicKey = null)
        {
            if (!Uri.TryCreate(feedBase.EndsWith("/") ? feedBase : feedBase + "/", UriKind.Absolute, out feed) || !Allowed(feed))
                throw new UpdateException("更新の配布先が正しくありません。");
            current = currentVersion; folder = downloadFolder;
            key = publicKey ?? UpdateManifest.PublicKey;
        }

        static bool Allowed(Uri u) => u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
        public Uri ManifestUri => new Uri(feed, "latest/download/" + UpdateManifest.Name);
        public Uri InstallerUri(UpdateInfo info) => new Uri(feed, "download/v" + info.version + "/" + info.file);

        HttpWebResponse Open(Uri uri, CancellationToken cancel)
        {
#if NETFRAMEWORK
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
#pragma warning disable SYSLIB0014
            var request = (HttpWebRequest)WebRequest.Create(uri);
#pragma warning restore SYSLIB0014
            request.Method = "GET";
            request.UserAgent = "Tempo-Updater";
            request.Accept = "application/octet-stream";
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;
            request.Timeout = Timeout;
            request.ReadWriteTimeout = Timeout;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.KeepAlive = false;
            using (cancel.Register(request.Abort))
            {
                try
                {
                    var response = (HttpWebResponse)request.GetResponse();
                    if (!Allowed(response.ResponseUri)) { response.Dispose(); throw new UpdateException("更新の配布先が安全な接続ではありません。"); }
                    return response;
                }
                catch (WebException e) when (cancel.IsCancellationRequested) { throw new OperationCanceledException("cancel", e); }
                catch (WebException e) when (e.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound)
                { throw new UpdateException("配布先に更新情報が見つかりません。", e); }
                catch (WebException e) { throw new UpdateException("更新サーバーに接続できません。インターネット接続を確認してください。", e); }
            }
        }

        // Returns the newer release, or null when the installed version is current.
        public UpdateInfo Check(CancellationToken cancel, out UpdateInfo latest)
        {
            byte[] data;
            using (var response = Open(ManifestUri, cancel))
            using (var stream = response.GetResponseStream())
            using (var buffer = new MemoryStream())
            {
                try
                {
                    var chunk = new byte[8192]; int n;
                    while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        buffer.Write(chunk, 0, n);
                        if (buffer.Length > 64 * 1024) throw new UpdateException("更新情報が大きすぎます。");
                    }
                }
                catch (IOException e) { throw new UpdateException("更新情報の取得が中断されました。", e); }
                catch (WebException e) { throw new UpdateException("更新情報の取得が中断されました。", e); }
                data = buffer.ToArray();
            }
            latest = UpdateManifest.Parse(data, key);
            if (latest.minimum.Length > 0 && UpdateManifest.TryVersion(latest.minimum, out _) && UpdateManifest.Compare(current, latest.minimum) < 0)
                throw new UpdateException("この版からは自動更新できません。Tempo " + latest.version + " のインストーラーを直接実行してください。");
            return UpdateManifest.Compare(latest.version, current) > 0 ? latest : null;
        }

        // Downloads to a .partial file, then verifies size and SHA-256 before exposing the installer.
        public string Download(UpdateInfo info, Action<long, long> progress, CancellationToken cancel)
        {
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, info.file), partial = target + ".partial";
            try
            {
                using (var response = Open(InstallerUri(info), cancel))
                using (var input = response.GetResponseStream())
                using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                using (cancel.Register(() => { try { response.Close(); } catch (Exception) { } }))
                {
                    if (response.ContentLength >= 0 && response.ContentLength != info.size)
                        throw new UpdateException("更新ファイルのサイズが更新情報と一致しません。");
                    var chunk = new byte[81920]; long received = 0; int n;
                    var lastReport = Environment.TickCount;
                    while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        received += n;
                        if (received > info.size) throw new UpdateException("更新ファイルが想定より大きいため中止しました。");
                        sha.TransformBlock(chunk, 0, n, null, 0);
                        output.Write(chunk, 0, n);
                        if (Environment.TickCount - lastReport > 150) { progress?.Invoke(received, info.size); lastReport = Environment.TickCount; }
                    }
                    cancel.ThrowIfCancellationRequested();
                    if (received != info.size) throw new UpdateException("ダウンロードが途中で切断されました。現在の版を引き続き使用できます。");
                    sha.TransformFinalBlock(chunk, 0, 0);
                    if (Hex(sha.Hash) != info.sha256) throw new UpdateException("更新ファイルが破損しているか、署名された内容と一致しません。更新を中止しました。");
                    output.Flush(true);
                    progress?.Invoke(received, info.size);
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(partial, target);
                return target;
            }
            catch (Exception e)
            {
                TryDelete(partial);
                if (e is UpdateException || e is OperationCanceledException) throw;
                if (cancel.IsCancellationRequested) throw new OperationCanceledException("cancel", e);
                throw new UpdateException("ダウンロードが途中で切断されました。現在の版を引き続き使用できます。", e);
            }
        }

        // Re-hash an already verified installer through a handle that blocks writers until the caller releases it.
        public static FileStream OpenVerified(string path, UpdateInfo info)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                if (stream.Length != info.size) throw new UpdateException("更新ファイルのサイズが一致しません。");
                using (var sha = SHA256.Create())
                    if (Hex(sha.ComputeHash(stream)) != info.sha256) throw new UpdateException("更新ファイルが変更されています。更新を中止しました。");
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
        internal static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }
    }
}
