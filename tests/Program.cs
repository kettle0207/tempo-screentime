using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tempo;

internal static class Tests
{
    static int passed;
    internal static void Check(bool ok,string message)=>Expect(ok,message);
    static void Expect(bool ok,string message)
    {
        if(!ok)throw new Exception(message);
        passed++;Console.WriteLine("PASS "+message);
    }
    static Slice Row(string date,int hour,string app,string site,long ms)=>new Slice{ Day=date,Hour=hour,App=app,Site=site,Ms=ms };
    static void Storage(string path)
    {
        var anchor=new DateTime(2024,3,1);
        using(var h=new History(path))
        {
            h.Register("chrome","ブラウザー","","web");h.Register("code","開発ツール","","dev");
            h.Save(new[]{Row("2024-02-25",9,"code","",2000),Row("2024-02-29",23,"code","",3000),Row("2024-03-01",0,"chrome","www.youtube.com",6000),Row("2024-03-01",1,"chrome","www.youtube.com",1000),Row("2024-03-01",1,"code","",1000)});
            var d=h.Read("day",anchor);
            Expect(d.total==8000 && d.apps.Sum(x=>x.ms)==8000 && d.sites.Single().ms==7000,"sites are a subset of PC usage");
            Expect(d.buckets.Length==24 && d.buckets[0]["video"]==6000 && d.previous==3000,"hour chart and previous day");
            var w=h.Read("week",anchor);Expect(w.start=="2024-02-26" && w.total==11000 && w.previous==2000,"Monday week and previous week");
            var m=h.Read("month",anchor);Expect(m.buckets.Length==31 && m.total==8000 && m.previous==5000,"month and prior leap February");
            var yr=h.Read("year",anchor);Expect(yr.buckets.Length==12 && yr.total==13000,"calendar year");
            h.Rule("app","chrome","work");Expect(h.Read("day",anchor).buckets[0]["work"]==6000,"app recategorization applies to history");
            Expect(h.Read("day",anchor).sites.Single().category=="work","site ranking agrees with inherited category");
            h.Rule("site","www.youtube.com","study");Expect(h.Read("day",anchor).buckets[0]["study"]==6000,"domain override takes priority");
            h.Set("theme","dark");
            try {h.Save(new[]{Row("2024-03-01",2,"code","",long.MaxValue),Row("2024-03-01",2,"code","",long.MaxValue)});}catch(OverflowException){}
            Expect(h.Read("day",anchor).total==8000,"failed batch rolls back");
        }
        using(var h=new History(path))
        {
            var d=h.Read("day",anchor);
            Expect(d.total==8000 && d.sites.Single().category=="study" && h.Get("theme","")=="dark","reopen preserves usage, rules and settings");
            h.Rule("site","www.youtube.com","auto");h.Rule("app","chrome","auto");
            Expect(h.Read("day",anchor).buckets[0]["video"]==6000,"automatic category restored");
            Parallel.For(0,30,i=>{if(i%2==0)h.Save(new[]{Row("2024-03-01",2,"code","",100)});else h.Read("day",anchor);});
            Expect(h.Read("day",anchor).total==9500,"simultaneous reads and writes preserve totals");
            Expect(h.Read("day",anchor.AddDays(3)).total==0,"empty history returns zero");
        }
    }
    static void MixedFormats(string path)
    {
        using(var db=new Sqlite(path))
        {
            db.Run("INSERT INTO names VALUES(1,'code'),(2,'example.test')");
            db.Run("INSERT INTO buckets VALUES('2024-03-01',2,1,2,750)");
        }
        using var history=new History(path);
        var day=history.Read("day",new DateTime(2024,3,1));
        Expect(day.total==10250 && day.sites.Any(s=>s.id=="example.test" && s.ms==750),"legacy and compact rows included exactly once");
        history.Rule("site","example.test","study");
        Expect(history.Read("year",new DateTime(2024,1,1)).buckets[2]["study"]==750,"rules apply to compact rows across year reports");
    }
    static void Benchmark(string path)
    {
        var begin=new DateTime(2025,1,1);using var h=new History(path);
        const int apps=8;for(int a=0;a<apps;a++)h.Register("a"+a,"アプリ"+a,"","other");
        var sw=Stopwatch.StartNew();
        for(int day=0;day<365;day++)
        {
            var slices=new List<Slice>();
            for(int hour=8;hour<20;hour++)for(int a=0;a<apps;a++)
                slices.Add(Row(begin.AddDays(day).ToString("yyyy-MM-dd"),hour,"a"+a,"",180000));
            h.Save(slices);
        }
        Console.WriteLine($"BENCH rows={365*12*apps} populate_ms={sw.ElapsedMilliseconds}");
        var ms=new List<double>();for(int j=0;j<15;j++){sw.Restart();var result=h.Read("year",begin);if(result.total!=365L*12*apps*180000)throw new Exception();ms.Add(sw.Elapsed.TotalMilliseconds);}
        ms.Sort();Console.WriteLine($"BENCH annual_median_ms={ms[7]:F1} max_ms={ms.Last():F1} database_bytes={new FileInfo(path).Length}");
    }

    // Test-only driver for exercising the real dashboard against the production SQLite/reporting classes.
    static void UiDriver(string path,bool seed)
    {
        using var h=new History(path);var day=DateTime.Today;
        if(seed){
        var apps=new[]{("code","Visual Studio Code","dev"),("edge","Microsoft Edge","web"),("word","Microsoft Word","work"),("game","Minecraft","games"),("notes","OneNote","study")};
        foreach(var a in apps)h.Register(a.Item1,a.Item2,"",a.Item3);
        var slices=new List<Slice>();
        for(int j=0;j<8;j++)
        {
            var d=day.AddDays(-j).ToString("yyyy-MM-dd");
            for(int i=0;i<10;i++)
            {
                var a=apps[new[]{0,0,1,2,1,0,4,2,3,1}[i]];
                slices.Add(Row(d,8+i,a.Item1,a.Item1=="edge"?(i==4?"www.youtube.com":"github.com"):"",(10+(i*13+j*7)%45)*60000));
            }
        }
        h.Save(slices);}
        bool paused=false,startup=true;int idle=60;string theme="system";var options=new JsonSerializerOptions{IncludeFields=true};
        string line;while((line=Console.ReadLine())!=null)
        {
            var m=JsonSerializer.Deserialize<Dictionary<string,string>>(line);object report=null;
            try
            {
                switch(m["action"])
                {
                    case "report":report=h.Read(m["period"],DateTime.ParseExact(m["date"],"yyyy-MM-dd",CultureInfo.InvariantCulture));break;
                    case "rule":h.Rule(m["kind"],m["id"],m["value"]);break;
                    case "paused":paused=bool.Parse(m["value"]);break;
                    case "startup":startup=bool.Parse(m["value"]);break;
                    case "idle":idle=int.Parse(m["value"]);break;
                    case "theme":theme=m["value"];break;
                }
                Console.WriteLine(JsonSerializer.Serialize(new {request=m["request"],report,ok=true,settings=new{paused,startup,idle,theme,error="",version="1.2.0",message="",data=@"C:\Local\Tempo",extension=@"C:\Tempo\Extension"}},options));
            }
            catch(Exception ex){Console.WriteLine(JsonSerializer.Serialize(new{request=m["request"],error=ex.Message}));}
        }
    }
    static void Main(string[] args)
    {
        if(OperatingSystem.IsLinux())NativeLibrary.SetDllImportResolver(typeof(Tests).Assembly,(name,_,_)=>name=="winsqlite3"?NativeLibrary.Load("libsqlite3.so.0"):IntPtr.Zero);
        var dir=Directory.CreateTempSubdirectory("tempo-test-");
        try
        {
            string db=Path.Combine(dir.FullName,"使用履歴.sqlite3");
            if(args.Contains("--ui")){UiDriver(db,!args.Contains("--empty"));return;}
            Storage(db);MixedFormats(db);UpdateTests.Run(dir.FullName);Benchmark(Path.Combine(dir.FullName,"perf.sqlite3"));Console.WriteLine($"{passed} checks passed");
        }
        finally {dir.Delete(true);}
    }
}
