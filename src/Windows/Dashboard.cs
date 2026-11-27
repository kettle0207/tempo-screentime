using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Tempo
{
    internal sealed class Dashboard : Form
    {
        const string Origin="https://tempo.invalid/";
        readonly Desktop desktop;
        readonly WebView2 view=new WebView2 {Dock=DockStyle.Fill};
        readonly Updater updater;
        readonly string startupMessage;
        readonly JavaScriptSerializer json=new JavaScriptSerializer {MaxJsonLength=int.MaxValue};
        public Dashboard(Desktop desktop)
        {
            this.desktop=desktop;Text="Tempo — スクリーンタイム";
            updater=new Updater(desktop,Post,()=>{if(!IsDisposed && IsHandleCreated)BeginInvoke((Action)Close);});
            try{startupMessage=updater.StartupMessage();}catch(Exception){startupMessage="";}
            var dark=Theme(desktop)=="dark";
            view.DefaultBackgroundColor=dark?Color.FromArgb(31,31,33):Color.White;BackColor=view.DefaultBackgroundColor;Icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode=AutoScaleMode.Dpi;MinimumSize=new Size(610,520);Size=new Size(1100,820);StartPosition=FormStartPosition.CenterScreen;
            Controls.Add(view);Shown+=async(s,e)=>await Initialize();
        }
        async Task Initialize()
        {
            try
            {
                var opts=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-sync --no-first-run --disable-quic --metrics-recording-only --proxy-server=127.0.0.1:9 --proxy-bypass-list=<-loopback>");
                opts.IsCustomCrashReportingEnabled=true;
                var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Program.Home,"WebView"),opts);
                if(IsDisposed)return;
                await view.EnsureCoreWebView2Async(env);
                if(IsDisposed)return;
                var core=view.CoreWebView2;
                core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
                core.Settings.AreHostObjectsAllowed=false;core.Settings.IsStatusBarEnabled=false;
                core.Settings.IsGeneralAutofillEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;
                core.Settings.IsBuiltInErrorPageEnabled=false;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(s,e)=>{
                    string url=e.Request.Uri;
                    string file=url==Origin?"index.html":url==Origin+"app.js"?"app.js":url==Origin+"style.css"?"style.css":null;
                    if(file==null) {e.Response=env.CreateWebResourceResponse(null,403,"Forbidden","");return;}
                    var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("UI."+file);
                    string mime=file.EndsWith("html")?"text/html":file.EndsWith("js")?"text/javascript":"text/css";
                    e.Response=env.CreateWebResourceResponse(stream,200,"OK","Content-Type: "+mime+"; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src data:; connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'");
                };
                core.NavigationStarting+=(s,e)=>{if(e.Uri!=Origin)e.Cancel=true;};
                core.NewWindowRequested+=(s,e)=>e.Handled=true;
                core.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
                core.WebMessageReceived+=Message;
                core.Navigate(Origin);
            }
            catch(Exception e)
            {
                if(IsDisposed)return;
                view.Visible=false;
                var detail=e is WebView2RuntimeNotFoundException ? "ダッシュボードには Microsoft Edge WebView2 Runtime が必要です。" : e.Message;
                Controls.Add(new Label{Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Text="ダッシュボードを表示できません。\n"+detail+"\n\n記録はタスクトレイで継続します。"});
            }
        }
        [System.Runtime.InteropServices.DllImport("user32",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int RegisterWindowMessage(string name);
        static readonly int SettingsChanged=RegisterWindowMessage("Tempo.SettingsChanged.1");
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if(message.Msg==SettingsChanged && view?.CoreWebView2!=null) Send(new {changed=true});
        }
        static string Theme(Desktop desktop)
        {
            var t=desktop.History.Get("theme","system");
            if(t!="system")return t;
            using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k?.GetValue("AppsUseLightTheme") is int light && light==0 ? "dark" : "light";
        }
        void Post(object obj)
        {
            if(IsDisposed)return;
            if(InvokeRequired){try{BeginInvoke((Action)(()=>Send(obj)));}catch(InvalidOperationException){}}
            else Send(obj);
        }
        void Send(object obj) {if(!IsDisposed && view.CoreWebView2!=null)view.CoreWebView2.PostWebMessageAsJson(json.Serialize(obj));}
        async void Message(object sender,CoreWebView2WebMessageReceivedEventArgs e)
        {
            if(e.Source!=Origin)return;
            string request="";
            try
            {
                var m=json.Deserialize<Dictionary<string,string>>(e.WebMessageAsJson);
                request=m["request"]; string action=m["action"];
                if(action=="report")
                {
                    var date=DateTime.ParseExact(m["date"],"yyyy-MM-dd",CultureInfo.InvariantCulture);
                    if(date.Year<1971 || date.Year>9997)throw new ArgumentException("日付の範囲が無効です。");
                    var report=await Task.Run(()=>{desktop.Recorder.Flush();
                        foreach(var id in desktop.History.MissingApps(m["period"],date)) AppMetadata.Register(desktop.History,id);
                        return desktop.History.Read(m["period"],date);});
                    Send(new {request,report,settings=Settings()});return;
                }
                switch(action)
                {
                    case "theme":
                        if(m["value"]!="system" && m["value"]!="light" && m["value"]!="dark")throw new ArgumentException();
                        desktop.History.Set("theme",m["value"]);break;
                    case "idle": desktop.Recorder.SetIdle(int.Parse(m["value"]));break;
                    case "paused": desktop.Recorder.Pause(bool.Parse(m["value"]));break;
                    case "startup":desktop.Autostart=bool.Parse(m["value"]);break;
                    case "rule":desktop.History.Rule(m["kind"],m["id"],m["value"]);break;
                    case "update-check":updater.Check();break;
                    case "update-install":updater.Install();break;
                    case "update-cancel":updater.Cancel();break;
                    case "extension":
                        string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Extension");
                        if(!Directory.Exists(path))throw new DirectoryNotFoundException("拡張機能フォルダーが見つかりません。");
                        string manifest=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"browser-host.json");
                        if(!File.Exists(manifest))throw new FileNotFoundException("ブラウザ連携用ファイルが見つかりません。");
                        foreach(var key in new[]{@"Software\Google\Chrome\NativeMessagingHosts\local.tempo.screentime",
                            @"Software\Microsoft\Edge\NativeMessagingHosts\local.tempo.screentime"})
                            using(var k=Registry.CurrentUser.CreateSubKey(key))k.SetValue("",manifest);
                        Process.Start("explorer.exe","\""+path+"\"");break;
                    default:throw new ArgumentException("操作が無効です。");
                }
                Send(new {request,ok=true,settings=Settings()});
            }
            catch(Exception ex) {if(!IsDisposed)Send(new {request,error=ex.Message});}
        }
        object Settings()
        {
            var status=desktop.Recorder.Status;
            return new {theme=desktop.History.Get("theme","system"),idle=int.Parse(desktop.History.Get("idle","60")),
                paused=(status&1)!=0,startup=desktop.Autostart,error=(status&2)!=0?"履歴を保存できません。":"",
                data=Program.Home,version=Updater.Current,message=startupMessage,extension=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Extension")};
        }
    }
}
