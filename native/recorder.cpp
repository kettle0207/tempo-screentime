#include "channel.hpp"
#include "store.hpp"
#include "timeline.hpp"
#include <shellapi.h>
#include <shlobj.h>
#include <wtsapi32.h>
#include <sddl.h>
#include <memory>
#include <vector>
#include <cstdio>

DWORD WINAPI listenSites(void*);
bool nextDomain(DWORD&,SiteMessage&);
void signalHosts(const wchar_t*,bool);

namespace {
using Time=int64_t;
constexpr Time Checkpoint=60000;
constexpr UINT Tray=WM_APP+20;
constexpr UINT_PTR Timer=1;
// Away-detection choices offered in Settings: 1, 3, 5, 30 and 60 minutes.
bool allowedIdle(int64_t s){return s==60 || s==180 || s==300 || s==1800 || s==3600;}
Time tickNow(){return static_cast<Time>(GetTickCount64());}
Time utcNow() {
    FILETIME ft;GetSystemTimeAsFileTime(&ft);
    ULARGE_INTEGER v{};v.LowPart=ft.dwLowDateTime;v.HighPart=ft.dwHighDateTime;
    return v.QuadPart/10000;
}
Time awakeNow() {ULONGLONG v=0;QueryUnbiasedInterruptTime(&v);return v/10000;}
Time expandTick(DWORD low,Time now) {
    return now-static_cast<DWORD>(static_cast<DWORD>(now)-low);
}
std::wstring dataPath() {
    wchar_t root[MAX_PATH];
    if(FAILED(SHGetFolderPathW(nullptr,CSIDL_LOCAL_APPDATA,nullptr,0,root)))throw std::runtime_error("LocalAppData");
    auto dir=std::wstring(root)+L"\\Tempo\\ScreenTime";
    int rc=SHCreateDirectoryExW(nullptr,dir.c_str(),nullptr);
    if(rc!=ERROR_SUCCESS && rc!=ERROR_ALREADY_EXISTS && rc!=ERROR_FILE_EXISTS)throw std::runtime_error("directory");
    return dir+L"\\history.sqlite3";
}
std::wstring processPath(DWORD pid) {
    HANDLE h=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    if(!h)return {};
    wchar_t path[32768];DWORD length=32768;
    bool ok=QueryFullProcessImageNameW(h,0,path,&length);CloseHandle(h);
    return ok ? std::wstring(path,length) : std::wstring();
}
bool interactive() {
    auto desk=OpenInputDesktop(0,FALSE,DESKTOP_READOBJECTS);
    if(!desk)return false;
    wchar_t name[64]{};DWORD bytes;
    bool ok=GetUserObjectInformationW(desk,UOI_NAME,name,sizeof(name),&bytes);
    CloseDesktop(desk);return ok && equalName(name,L"Default");
}
bool activate(HWND h,DWORD pid) {
    DWORD owner=0;GetWindowThreadProcessId(h,&owner);
    if(owner!=pid || !IsWindowVisible(h) || GetWindow(h,GW_OWNER))return false;
    if(IsIconic(h))ShowWindow(h,SW_RESTORE);
    SetForegroundWindow(h);return true;
}
struct Foreground {
    HWND window{};DWORD owner{},pid{};std::wstring app;
    void select(HWND next) {
        DWORD candidate=0;if(next)GetWindowThreadProcessId(next,&candidate);
        if(next==window && candidate==owner)return;
        window=next;owner=pid=candidate;app.clear();
        if(!candidate)return;
        auto path=processPath(candidate);
        if(equalName(path.substr(path.find_last_of(L"\\/")+1),L"ApplicationFrameHost.exe")) {
            EnumChildWindows(next,[](HWND child,LPARAM arg)->BOOL{
                auto self=reinterpret_cast<Foreground*>(arg);
                wchar_t cls[80]{};GetClassNameW(child,cls,80);
                if(!equalName(cls,L"Windows.UI.Core.CoreWindow"))return TRUE;
                GetWindowThreadProcessId(child,&self->pid);return FALSE;
            },reinterpret_cast<LPARAM>(this));
            if(pid!=owner){auto child=processPath(pid);if(!child.empty())path=child;}
        }
        if(path.empty()){app=L"取得できないアプリ";return;}
        CharLowerBuffW(path.data(),static_cast<DWORD>(path.size()));
        auto name=path.substr(path.find_last_of(L'\\')+1);
        if(name==L"lockapp.exe" || name==L"logonui.exe" || (name.size()>4 && name.substr(name.size()-4)==L".scr"))return;
        app=std::move(path);
    }
};

class Recorder {
    Store db;
    HWND wnd;
    HWINEVENTHOOK hook{};
    NOTIFYICONDATAW tray{};
    Foreground front;
    struct Site {DWORD pid;std::wstring domain;uint64_t order;};
    std::map<DWORD,Site> sites;
    std::wstring domain;
    uint64_t sequence=0;
    bool paused,locked=false,asleep=false,valid=false,failed=false;
    Time last=0,wall=0,input=0,awake=0,nextWrite=0,limit;
    HANDLE dashboard{};
    DWORD dashboardPid{};
    static Recorder* instance;
    static void CALLBACK changed(HWINEVENTHOOK,DWORD,HWND h,LONG,LONG,DWORD,DWORD stamp) {
        if(!instance || instance->stopped())return;
        try {instance->observe(h,expandTick(stamp,tickNow()));}
        catch(...) {instance->failed=true;}
    }
    bool stopped() const {return paused || locked || asleep;}
    void updateHook() {
        if(stopped()) {
            if(hook)UnhookWinEvent(hook);
            hook=nullptr;
        } else if(!hook) {
            hook=SetWinEventHook(EVENT_SYSTEM_FOREGROUND,EVENT_SYSTEM_FOREGROUND,nullptr,changed,0,0,WINEVENT_OUTOFCONTEXT);
        }
    }
    void chooseSite() {
        domain.clear();uint64_t best=0;
        for(auto it=sites.begin();it!=sites.end();) {
            const auto& s=it->second;
            if(s.pid==front.pid && s.order>best) {domain=s.domain;best=s.order;}
            ++it;
        }
    }
    void add(Time utc,Time duration) {
        while(duration>0) {
            ULARGE_INTEGER v{};v.QuadPart=utc*10000;
            FILETIME ft{v.LowPart,v.HighPart};SYSTEMTIME universal{},local{};
            if(!FileTimeToSystemTime(&ft,&universal) || !SystemTimeToTzSpecificLocalTime(nullptr,&universal,&local))return;
            char day[16];snprintf(day,sizeof(day),"%04u-%02u-%02u",local.wYear,local.wMonth,local.wDay);
            Time beforeHour=3600000-(local.wMinute*60000+local.wSecond*1000+local.wMilliseconds);
            Time part=std::min(duration,beforeHour);
            db.add(day,local.wHour,front.app,domain,part);duration-=part;utc+=part;
        }
    }
    void schedule(Time now) {
        KillTimer(wnd,Timer);
        if(stopped() && !db.dirty())return;
        Time delay=std::max<Time>(100,nextWrite-now);
        if(!stopped()) {
            Time untilIdle=input+limit-now;
            delay=std::min(delay,untilIdle>0 ? untilIdle : 3000);
            if(!hook)delay=std::min<Time>(delay,2000);
        }
        SetTimer(wnd,Timer,static_cast<UINT>(std::clamp<Time>(delay,100,Checkpoint)),nullptr);
    }
    void configureStartup() {
        const wchar_t* key=L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        HKEY k{};
        if(RegCreateKeyExW(HKEY_CURRENT_USER,key,0,nullptr,0,KEY_QUERY_VALUE|KEY_SET_VALUE,nullptr,&k,nullptr)!=ERROR_SUCCESS)
            throw std::runtime_error("startup registry");
        DWORD size=0;
        bool existing=RegQueryValueExW(k,L"Tempo ScreenTime",nullptr,nullptr,nullptr,&size)==ERROR_SUCCESS;
        bool enabled=db.setting(L"startupDesired",db.setting(L"initialized",L"").empty() || existing ? L"true":L"false")==L"true";
        if(enabled) {
            auto value=L"\""+modulePath()+L"\" --background";
            auto rc=RegSetValueExW(k,L"Tempo ScreenTime",0,REG_SZ,reinterpret_cast<const BYTE*>(value.c_str()),(value.size()+1)*2);
            if(rc!=ERROR_SUCCESS){RegCloseKey(k);throw std::runtime_error("startup registry");}
        }
        RegCloseKey(k);
        if(db.setting(L"initialized",L"").empty())db.set(L"initialized",L"1");
        if(db.setting(L"startupDesired",L"").empty())db.set(L"startupDesired",enabled?L"true":L"false");
    }
public:
    Recorder(HWND w):db(dataPath()),wnd(w),paused(db.setting(L"paused",L"false")==L"true") {
        limit=_wtoi(db.setting(L"idle",L"60").c_str())*1000LL;
        if(!allowedIdle(limit/1000))limit=60000;
        configureStartup();
        tray.cbSize=sizeof(tray);tray.hWnd=wnd;tray.uID=1;tray.uFlags=NIF_MESSAGE|NIF_ICON|NIF_TIP;
        tray.uCallbackMessage=Tray;tray.hIcon=LoadIconW(GetModuleHandleW(nullptr),MAKEINTRESOURCEW(1));
        wcscpy(tray.szTip,L"Tempo · スクリーンタイム");
        addTray();
        WTSRegisterSessionNotification(wnd,NOTIFY_FOR_THIS_SESSION);
        instance=this;
        updateHook();
        nextWrite=tickNow()+Checkpoint;
        observe(GetForegroundWindow(),tickNow());
    }
    void addTray(){Shell_NotifyIconW(NIM_ADD,&tray);}
    ~Recorder() {
        instance=nullptr;if(hook)UnhookWinEvent(hook);
        WTSUnRegisterSessionNotification(wnd);KillTimer(wnd,Timer);
        Shell_NotifyIconW(NIM_DELETE,&tray);
        if(dashboard)CloseHandle(dashboard);
    }
    void observe(HWND next,Time stamp,bool closeKnown=false) {
        if(stopped())return;
        Time now=tickNow(),end=std::clamp(stamp,last,now);
        Time lag=now-end,utc=utcNow()-lag,currentAwake=awakeNow()-lag;
        LASTINPUTINFO li{sizeof(li),0};
        bool usable=interactive() && GetLastInputInfo(&li);
        Time recent=usable?expandTick(li.dwTime,now):(closeKnown && valid?input:end-limit);
        // A queued notification can predate the latest input. Keep that input for the next interval.
        if(recent>end)recent=valid?input:end;
        auto elapsed=end-last;
        bool continuous=valid && (usable || closeKnown) && continuousInterval(elapsed,utc-wall,currentAwake-awake);
        if(continuous && !front.app.empty()) {
            activeParts(last,end,input,recent,limit,[&](Time a,Time b){add(wall+a-last,b-a);});
        }
        last=end;wall=utc;input=recent;awake=currentAwake;valid=usable;
        front.select(usable?next:nullptr);chooseSite();
        if(now>=nextWrite)save();
        schedule(now);
    }
    bool save() {
        try {db.flush();failed=false;}
        catch(...) {failed=true;}
        nextWrite=tickNow()+(failed?10000:Checkpoint);
        return !failed;
    }
    bool flushNow() {observe(GetForegroundWindow(),tickNow());bool ok=save();schedule(tickNow());return ok;}
    void timer() {
        if(stopped()){if(db.dirty())save();schedule(tickNow());}
        else observe(GetForegroundWindow(),tickNow());
    }
    void interrupt(bool& flag,bool value) {
        if(flag==value)return;
        if(!stopped())observe(GetForegroundWindow(),tickNow(),true);
        flag=value;updateHook();valid=false;front.select(nullptr);save();
        if(!stopped())observe(GetForegroundWindow(),tickNow());else schedule(tickNow());
    }
    void lock(bool v){interrupt(locked,v);}
    void sleep(bool v){interrupt(asleep,v);}
    bool pause(bool v) {
        db.set(L"paused",v?L"true":L"false");interrupt(paused,v);
        if(dashboard && WaitForSingleObject(dashboard,0)==WAIT_TIMEOUT)
            EnumWindows([](HWND h,LPARAM p)->BOOL{
                DWORD id=0;GetWindowThreadProcessId(h,&id);
                if(id==DWORD(p) && IsWindowVisible(h))PostMessageW(h,RegisterWindowMessageW(L"Tempo.SettingsChanged.1"),0,0);
                return TRUE;
            },dashboardPid);
        return !failed;
    }
    bool idle(int seconds) {
        if(!allowedIdle(seconds))return false;
        observe(GetForegroundWindow(),tickNow());db.set(L"idle",std::to_wstring(seconds));
        limit=seconds*1000;schedule(tickNow());return true;
    }
    int status()const{return (paused?1:0)|(failed?2:0);}
    void site() {
        DWORD host; SiteMessage msg{};
        while(nextDomain(host,msg)) {
            const char* end=static_cast<const char*>(memchr(msg.domain,0,sizeof(msg.domain)));
            if(!end || !std::all_of(static_cast<const char*>(msg.domain),end,domainChar))continue;
            observe(GetForegroundWindow(),tickNow());
            if(end==msg.domain)sites.erase(host);
            else sites[host]={msg.browser,std::wstring(static_cast<const char*>(msg.domain),end),++sequence};
            chooseSite();
        }
    }
    void show() {
        if(dashboard && WaitForSingleObject(dashboard,0)==WAIT_TIMEOUT) {
            EnumWindows([](HWND h,LPARAM p)->BOOL{return !activate(h,static_cast<DWORD>(p));},dashboardPid);
            return;
        }
        if(dashboard){CloseHandle(dashboard);dashboard=nullptr;}
        flushNow();
        auto exe=directory(modulePath())+L"Tempo.Dashboard.exe";
        auto cmd=L"\""+exe+L"\"";
        STARTUPINFOW si{};si.cb=sizeof(si);PROCESS_INFORMATION pi{};
        if(!CreateProcessW(exe.c_str(),cmd.data(),nullptr,nullptr,FALSE,0,nullptr,nullptr,&si,&pi)){
            MessageBoxW(nullptr,L"Tempo.Dashboard.exe を開けません。",L"Tempo",MB_ICONERROR);return;
        }
        CloseHandle(pi.hThread);dashboard=pi.hProcess;dashboardPid=pi.dwProcessId;
    }
    void closeView() {
        if(dashboard && WaitForSingleObject(dashboard,0)==WAIT_TIMEOUT)
            EnumWindows([](HWND h,LPARAM p)->BOOL{DWORD id=0;GetWindowThreadProcessId(h,&id);if(id==DWORD(p) && IsWindowVisible(h))PostMessageW(h,WM_CLOSE,0,0);return TRUE;},dashboardPid);
    }
    void menu() {
        HMENU m=CreatePopupMenu();
        AppendMenuW(m,MF_STRING,1,L"ダッシュボードを開く");
        AppendMenuW(m,MF_STRING,2,paused?L"記録を再開":L"記録を一時停止");
        AppendMenuW(m,MF_SEPARATOR,0,nullptr);AppendMenuW(m,MF_STRING,3,L"Tempo を終了");
        POINT pt;GetCursorPos(&pt);SetForegroundWindow(wnd);
        int cmd=TrackPopupMenu(m,TPM_RETURNCMD|TPM_RIGHTBUTTON,pt.x,pt.y,0,wnd,nullptr);
        DestroyMenu(m);PostMessageW(wnd,WM_NULL,0,0);
        if(cmd==1)show();
        if(cmd==2)pause(!paused);
        if(cmd==3)PostMessageW(wnd,WM_CLOSE,0,0);
    }
};
Recorder* Recorder::instance=nullptr;
std::unique_ptr<Recorder> recorder;
UINT taskbar;
LRESULT CALLBACK proc(HWND wnd,UINT msg,WPARAM w,LPARAM l) {
    try {
        if(recorder) {
            switch(msg) {
            case WM_TIMER:recorder->timer();return 0;
            case SiteChanged:recorder->site();return 0;
            case Show:recorder->show();return 1;
            case Flush:return recorder->flushNow();
            case Pause:return recorder->pause(w!=0);
            case IdleLimit:return recorder->idle(static_cast<int>(w));
            case Status:return recorder->status();
            case Tray:
                if(l==WM_LBUTTONDBLCLK)recorder->show();
                if(l==WM_RBUTTONUP)recorder->menu();
                return 0;
            case WM_WTSSESSION_CHANGE:
                if(w==WTS_SESSION_LOCK || w==WTS_CONSOLE_DISCONNECT || w==WTS_REMOTE_DISCONNECT)recorder->lock(true);
                if(w==WTS_SESSION_UNLOCK || w==WTS_CONSOLE_CONNECT || w==WTS_REMOTE_CONNECT)recorder->lock(false);
                return 0;
            case WM_POWERBROADCAST:
                if(w==PBT_APMSUSPEND)recorder->sleep(true);
                if(w==PBT_APMRESUMEAUTOMATIC || w==PBT_APMRESUMESUSPEND)recorder->sleep(false);
                return TRUE;
            case WM_QUERYENDSESSION:recorder->flushNow();return TRUE;
            case WM_ENDSESSION:if(w){recorder->flushNow();recorder->closeView();DestroyWindow(wnd);}return 0;
            case WM_CLOSE:
                if(!recorder->flushNow()){MessageBoxW(wnd,L"履歴を保存できません。空き容量を確認してください。",L"Tempo",MB_ICONERROR);return 0;}
                recorder->closeView();DestroyWindow(wnd);return 0;
            }
            if(msg==taskbar){recorder->addTray();return 0;}
        }
        if(msg==WM_DESTROY){PostQuitMessage(0);return 0;}
    } catch(...) {return 0;}
    return DefWindowProcW(wnd,msg,w,l);
}
}
namespace {
std::wstring mutexName() {
    HANDLE token=nullptr;
    if(!OpenProcessToken(GetCurrentProcess(),TOKEN_QUERY,&token))throw std::runtime_error("token");
    DWORD size=0;GetTokenInformation(token,TokenUser,nullptr,0,&size);
    std::vector<BYTE> data(size);
    bool ok=GetTokenInformation(token,TokenUser,data.data(),size,&size);CloseHandle(token);
    LPWSTR sid=nullptr;
    if(!ok || !ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(data.data())->User.Sid,&sid))throw std::runtime_error("SID");
    auto name=std::wstring(L"Local\\Tempo.ScreenTime.")+sid+L".instance";LocalFree(sid);
    return name;
}
int maintenance() {
    // Wait for our installed IDE-free payload to release its images before the installer replaces them.
    signalHosts(L".stop",true);
    auto dir=directory(modulePath());
    std::vector<HANDLE> waits;
    struct Context { const std::wstring& dir;std::vector<HANDLE>& waits; } context{dir,waits};
    EnumWindows([](HWND h,LPARAM l)->BOOL{
        auto& c=*reinterpret_cast<Context*>(l);
        wchar_t cls[80]{};GetClassNameW(h,cls,80);
        const bool resident=equalName(cls,RecorderClass);
        if(!resident && !IsWindowVisible(h))return TRUE;
        DWORD pid=0;GetWindowThreadProcessId(h,&pid);
        auto path=processPath(pid);
        auto expected=c.dir+(resident?L"ScreenTime.exe":L"Tempo.Dashboard.exe");
        if(!equalName(path,expected))return TRUE;
        HANDLE p=OpenProcess(SYNCHRONIZE,FALSE,pid);if(p)c.waits.push_back(p);
        PostMessageW(h,WM_CLOSE,0,0);return TRUE;
    },reinterpret_cast<LPARAM>(&context));
    int result=0;auto limit=tickNow()+10000;
    for(HANDLE h:waits){if(WaitForSingleObject(h,DWORD(std::max<Time>(0,limit-tickNow())))!=WAIT_OBJECT_0)result=1;CloseHandle(h);}
    return result;
}
}
int WINAPI wWinMain(HINSTANCE inst,HINSTANCE,LPWSTR command,int) {
    try {
        if(equalName(command,L"--shutdown"))return maintenance();
        bool background=equalName(command,L"--background");
        HANDLE mutex=CreateMutexW(nullptr,TRUE,mutexName().c_str());
        if(!mutex)return 1;
        if(GetLastError()==ERROR_ALREADY_EXISTS) {
            if(!background)for(int i=0;i<30;++i) {
                HWND h=FindWindowW(RecorderClass,nullptr);
                if(h){PostMessageW(h,Show,0,0);break;}Sleep(100);
            }
            CloseHandle(mutex);return 0;
        }
        WNDCLASSW wc{};wc.lpfnWndProc=proc;wc.hInstance=inst;wc.lpszClassName=RecorderClass;
        if(!RegisterClassW(&wc))return 1;
        HWND wnd=CreateWindowW(RecorderClass,L"Tempo Recorder",WS_POPUP,0,0,0,0,nullptr,nullptr,inst,nullptr);
        if(!wnd)return 1;
        taskbar=RegisterWindowMessageW(L"TaskbarCreated");
        recorder=std::make_unique<Recorder>(wnd);
        HANDLE listener=CreateThread(nullptr,0,listenSites,wnd,0,nullptr);
        if(listener)CloseHandle(listener);
        if(!background)PostMessageW(wnd,Show,0,0);
        MSG m;while(GetMessageW(&m,nullptr,0,0)>0){TranslateMessage(&m);DispatchMessageW(&m);}
        recorder.reset();
        // OS shutdown closes blocked IPC reads before the C++ runtime tears down shared queues.
        ReleaseMutex(mutex);CloseHandle(mutex);ExitProcess(0);
    } catch(const std::exception& e) {
        std::string s=e.what();
        auto text=std::wstring(L"Tempo を起動できません。\n")+std::wstring(s.begin(),s.end());
        MessageBoxW(nullptr,text.c_str(),L"Tempo",MB_ICONERROR);return 1;
    }
}
