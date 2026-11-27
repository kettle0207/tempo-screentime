using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Tempo
{
    internal static class Categories
    {
        public static readonly string[] All = { "dev", "work", "study", "games", "social", "video", "web", "other" };
        public static string App(string exe, string path)
        {
            switch (exe.ToLowerInvariant())
            {
                case "code": case "devenv": case "windowsterminal": case "idea64": case "pycharm64":
                case "rider64": case "wezterm-gui": case "powershell": case "pwsh": return "dev";
                case "winword": case "excel": case "powerpnt": case "outlook": case "ms-teams": case "slack": return "work";
                case "anki": case "onenote": return "study";
                case "discord": case "line": case "telegram": return "social";
                case "vlc": case "mpv": case "netflix": return "video";
                case "chrome": case "msedge": case "firefox": case "brave": case "opera": case "vivaldi": return "web";
                case "steam": case "minecraft.windows": case "fortniteclient-win64-shipping": return "games";
                default: return path.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) >= 0 ? "games" : "other";
            }
        }
        public static string Site(string host)
        {
            if (Matches("youtube.com","youtu.be","netflix.com","twitch.tv","nicovideo.jp","abema.tv")) return "video";
            if (Matches("x.com","twitter.com","instagram.com","facebook.com","reddit.com","discord.com","threads.net")) return "social";
            if (Matches("github.com","gitlab.com","stackoverflow.com","developer.mozilla.org","qiita.com","zenn.dev")) return "dev";
            if (Matches("wikipedia.org","coursera.org","udemy.com","duolingo.com","khanacademy.org")) return "study";
            if (Matches("notion.so","docs.google.com","office.com","slack.com")) return "work";
            return "";
            bool Matches(params string[] roots) => roots.Any(root => host == root || host.EndsWith("." + root, StringComparison.Ordinal));
        }
    }

    internal sealed class Ranking
    {
        public string id, name, icon, category, rule;
        public long ms;
    }
    internal sealed class Report
    {
        public string start, end, first;
        public long total, previous;
        public List<Ranking> apps, sites;
        public Dictionary<string,long>[] buckets;
    }
    internal sealed partial class History : IDisposable
    {
        readonly Sqlite db;
        readonly object gate = new object();
        public History(string path)
        {
            db = new Sqlite(path);
            db.Run("PRAGMA journal_mode=WAL"); db.Run("PRAGMA synchronous=NORMAL");
            using (var reader = new System.IO.StreamReader(typeof(History).Assembly.GetManifestResourceStream("Tempo.Schema")))
                foreach (var statement in reader.ReadToEnd().Split(';'))
                    if (!string.IsNullOrWhiteSpace(statement)) db.Run(statement);

        }
        public void Register(string id, string name, string icon, string category)
        {
            lock (gate) db.Run("INSERT OR IGNORE INTO apps VALUES(?,?,?,?)", id, name, icon, category);
        }
        public string Get(string key, string fallback)
        {
            lock (gate) return db.Run("SELECT value FROM settings WHERE id=?",key).FirstOrDefault()?[0] ?? fallback;
        }
        public void Set(string key, string value)
        {
            lock (gate) db.Run("INSERT OR REPLACE INTO settings VALUES(?,?)",key,value);
        }
        public void Rule(string kind, string id, string category)
        {
            if (kind != "app" && kind != "site") throw new ArgumentException("対象を選んでください。");
            lock (gate)
            {
                if (category == "auto") db.Run("DELETE FROM rules WHERE kind=? AND id=?",kind,id);
                else if (Categories.All.Contains(category)) db.Run("INSERT OR REPLACE INTO rules VALUES(?,?,?)",kind,id,category);
                else throw new ArgumentException("カテゴリを選んでください。");
            }
        }
        static string DateKey(DateTime d) => d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        public string[] MissingApps(string period, DateTime anchor)
        {
            var range=CalendarRange.Get(period,anchor);
            lock(gate) return db.Run(@"SELECT DISTINCT u.app FROM all_usage u LEFT JOIN apps a ON a.id=u.app
                WHERE u.day>=? AND u.day<? AND a.id IS NULL",DateKey(range.start),DateKey(range.end)).Select(r=>r[0]).ToArray();
        }
        public Report Read(string period, DateTime anchor)
        {
            var range = CalendarRange.Get(period, anchor);
            var start = DateKey(range.start); var end = DateKey(range.end);
            string[][] rows, rules, metadata;
            long previous; string first;
            string bucket = period == "day" ? "hour" : period == "year" ? "substr(day,6,2)" : "day";
            lock (gate)
            {
                rows = db.Run("SELECT " + bucket + ",app,site,SUM(ms) FROM all_usage WHERE day>=? AND day<? GROUP BY 1,2,3",start,end).ToArray();
                metadata = db.Run("SELECT id,name,icon,category FROM apps WHERE id IN (SELECT DISTINCT app FROM all_usage WHERE day>=? AND day<?)",start,end).ToArray();
                rules = db.Run("SELECT kind,id,category FROM rules").ToArray();
                previous = long.Parse(db.Run("SELECT COALESCE(SUM(ms),0) FROM all_usage WHERE day>=? AND day<?",DateKey(range.previous),start)[0][0]);
                first = db.Run("SELECT MIN(d) FROM (SELECT MIN(day) d FROM usage UNION ALL SELECT MIN(day) d FROM buckets)")[0][0];
            }
            var meta = metadata.ToDictionary(a=>a[0]);
            var appRules = rules.Where(r=>r[0]=="app").ToDictionary(r=>r[1],r=>r[2]);
            var siteRules = rules.Where(r=>r[0]=="site").ToDictionary(r=>r[1],r=>r[2]);
            var apps = new Dictionary<string,Ranking>(); var sites = new Dictionary<string,Ranking>();
            var siteCategories = new Dictionary<string,Dictionary<string,long>>();
            int count = period == "day" ? 24 : period == "year" ? 12 : (range.end-range.start).Days;
            var bins = Enumerable.Range(0,count).Select(_=>new Dictionary<string,long>()).ToArray();
            long total = 0;
            foreach (var row in rows)
            {
                var app = row[1]; var host = row[2]; long duration = long.Parse(row[3]);
                meta.TryGetValue(app, out var info);
                appRules.TryGetValue(app,out var appRule);
                siteRules.TryGetValue(host,out var siteRule);
                string appCategory = appRule ?? info?[3] ?? "other";
                string byDomain = Categories.Site(host);
                string category = siteRule ?? appRule ?? (byDomain.Length > 0 ? byDomain : appCategory);
                int bin = period == "day" ? int.Parse(row[0]) : period == "year" ? int.Parse(row[0])-1 :
                    (DateTime.ParseExact(row[0],"yyyy-MM-dd",CultureInfo.InvariantCulture)-range.start).Days;
                bins[bin].TryGetValue(category,out long used); bins[bin][category] = used + duration;
                total += duration;
                if (!apps.TryGetValue(app,out var a)) apps[app] = a = new Ranking {id=app,name=info?[1] ?? app,icon=info?[2] ?? "",category=appCategory,rule=appRule ?? "auto"};
                a.ms += duration;
                if (host.Length == 0) continue;
                if (!sites.TryGetValue(host,out var s))
                {
                    sites[host] = s = new Ranking {id=host,name=host,icon="",rule=siteRule ?? "auto"};
                    siteCategories[host] = new Dictionary<string,long>();
                }
                s.ms += duration;
                var weights=siteCategories[host];
                weights.TryGetValue(category,out long weight); weights[category]=weight+duration;
            }
            foreach (var site in sites.Values)
                site.category=siteCategories[site.id].OrderByDescending(pair=>pair.Value).First().Key;
            return new Report {start=start,end=end,first=first,total=total,previous=previous,buckets=bins,
                apps=apps.Values.OrderByDescending(a=>a.ms).ThenBy(a=>a.name).ToList(),
                sites=sites.Values.OrderByDescending(s=>s.ms).ThenBy(s=>s.name).ToList()};
        }
        public void Dispose() { lock (gate) db.Dispose(); }
    }
}
