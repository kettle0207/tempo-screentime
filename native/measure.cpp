// Standalone local measurement tool. Never loaded by the recorder.
#include "store.hpp"
#include "protocol.hpp"
#include <tlhelp32.h>
#include <psapi.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <shlobj.h>
#include <set>
#include <vector>
#include <sstream>
#include <iomanip>
#include <iostream>
#include <fstream>
#include <filesystem>
#include <ctime>

namespace fs=std::filesystem;
using IDs=std::set<DWORD>;
constexpr double MB=1000000.0;

std::string utf8(const std::wstring& s) {
    if(s.empty())return {};
    int n=WideCharToMultiByte(CP_UTF8,0,s.data(),int(s.size()),nullptr,0,nullptr,nullptr);
    std::string out(n,0);
    WideCharToMultiByte(CP_UTF8,0,s.data(),int(s.size()),out.data(),n,nullptr,nullptr);
    return out;
}
std::string registry(const wchar_t* key,const wchar_t* value) {
    wchar_t s[256]{};DWORD bytes=sizeof(s);
    if(RegGetValueW(HKEY_LOCAL_MACHINE,key,value,RRF_RT_REG_SZ,nullptr,s,&bytes)!=ERROR_SUCCESS)return "unknown";
    return utf8(s);
}
uint64_t asNumber(FILETIME t) {return (uint64_t(t.dwHighDateTime)<<32)|t.dwLowDateTime;}
struct Counters {
    std::map<DWORD,std::pair<HANDLE,uint64_t>> tracked;
    uint64_t cpu=0,rss=0,wallStart;
    bool sawHost=false,started=false;
    IDs active;
    Counters(){FILETIME ft;GetSystemTimeAsFileTime(&ft);wallStart=asNumber(ft);}
    ~Counters(){for(auto& p:tracked)CloseHandle(p.second.first);}
    void sample() {
        const auto dir=directory(modulePath());
        auto snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
        if(snap==INVALID_HANDLE_VALUE)throw std::runtime_error("process snapshot unavailable");
        PROCESSENTRY32W e{};e.dwSize=sizeof(e);DWORD session=0;
        ProcessIdToSessionId(GetCurrentProcessId(),&session);
        active.clear();rss=0;int residents=0;bool dashboard=false,missing=false;
        if(Process32FirstW(snap,&e))do {
            bool main=equalName(e.szExeFile,L"ScreenTime.exe"),host=equalName(e.szExeFile,L"Tempo.BrowserHost.exe");
            bool view=equalName(e.szExeFile,L"Tempo.Dashboard.exe");
            if(!main && !host && !view)continue;
            DWORD other=0;if(!ProcessIdToSessionId(e.th32ProcessID,&other) || session!=other)continue;
            HANDLE h=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION|PROCESS_VM_READ,FALSE,e.th32ProcessID);
            if(!h)continue;
            wchar_t path[32768]{};DWORD len=32768;
            if(!QueryFullProcessImageNameW(h,0,path,&len) || !equalName(directory(path),dir)){CloseHandle(h);continue;}
            if(view){dashboard=true;CloseHandle(h);continue;}
            if(main)++residents;else sawHost=true;
            FILETIME birth,exit,k,u;PROCESS_MEMORY_COUNTERS mem{};mem.cb=sizeof(mem);
            if(!GetProcessTimes(h,&birth,&exit,&k,&u) || !GetProcessMemoryInfo(h,&mem,sizeof(mem))){
                missing=true;CloseHandle(h);continue;
            }
            active.insert(e.th32ProcessID);rss+=mem.WorkingSetSize;
            if(!tracked.count(e.th32ProcessID))
                tracked[e.th32ProcessID]={h,started && asNumber(birth)>=wallStart?0:asNumber(k)+asNumber(u)};
            else CloseHandle(h);
        }while(Process32NextW(snap,&e));
        CloseHandle(snap);
        if(dashboard)throw std::runtime_error("測定する前に Tempo のダッシュボードを閉じてください。");
        if(residents!=1 || missing)throw std::runtime_error("Tempo を起動し、同じフォルダーの測定ツールを実行してください。");
        for(auto it=tracked.begin();it!=tracked.end();) {
            FILETIME b,x,k,u;
            if(GetProcessTimes(it->second.first,&b,&x,&k,&u)) {
                const auto total=asNumber(k)+asNumber(u);
                cpu+=total-it->second.second;it->second.second=total;
            }
            if(!active.count(it->first)){CloseHandle(it->second.first);it=tracked.erase(it);}
            else ++it;
        }
        started=true;
    }
};
struct GPU {
    PDH_HQUERY q{};PDH_HCOUNTER counter{};
    GPU(){
        if(PdhOpenQueryW(nullptr,0,&q)==ERROR_SUCCESS &&
           PdhAddEnglishCounterW(q,L"\\GPU Engine(*)\\Utilization Percentage",0,&counter)==ERROR_SUCCESS)PdhCollectQueryData(q);
    }
    ~GPU(){if(q)PdhCloseQuery(q);}
    double sample(const IDs& ids) {
        if(!counter || PdhCollectQueryData(q)!=ERROR_SUCCESS)return -1;
        DWORD bytes=0,count=0;
        if(PdhGetFormattedCounterArrayW(counter,PDH_FMT_DOUBLE,&bytes,&count,nullptr)!=static_cast<PDH_STATUS>(PDH_MORE_DATA) || !bytes)return -1;
        std::vector<BYTE> buf(bytes);
        auto list=reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buf.data());
        if(PdhGetFormattedCounterArrayW(counter,PDH_FMT_DOUBLE,&bytes,&count,list)!=ERROR_SUCCESS)return -1;
        bool valid=false;std::map<std::wstring,double> engines;
        for(DWORD i=0;i<count;i++) {
            const auto& item=list[i];
            if(item.FmtValue.CStatus!=PDH_CSTATUS_VALID_DATA && item.FmtValue.CStatus!=PDH_CSTATUS_NEW_DATA)continue;
            std::wstring name=item.szName;
            if(name.rfind(L"pid_",0)!=0)continue;
            valid=true;DWORD pid=wcstoul(name.c_str()+4,nullptr,10);
            auto key=name.find(L"_luid_");if(!ids.count(pid) || key==std::wstring::npos)continue;
            engines[name.substr(key)]+=item.FmtValue.doubleValue;
        }
        double peak=0;for(const auto& e:engines)peak=std::max(peak,e.second);
        return valid?peak:-1;
    }
};
uint64_t bytes(const fs::path& file){std::error_code ec;auto n=fs::file_size(file,ec);return ec?0:n;}
uint64_t footprint(const fs::path& db) {
    return bytes(db)+bytes(db.wstring()+L"-wal")+bytes(db.wstring()+L"-shm");
}
struct TempDB {
    fs::path path;
    TempDB(){wchar_t dir[MAX_PATH],file[MAX_PATH];if(!GetTempPathW(MAX_PATH,dir) || !GetTempFileNameW(dir,L"tpo",0,file))throw std::runtime_error("temp file");path=file;}
    ~TempDB(){std::error_code ec;for(auto suffix:{L"",L"-wal",L"-shm"})fs::remove(path.wstring()+suffix,ec);}
};
std::pair<uint64_t,uint64_t> annual() {
    TempDB tmp;uint64_t live;
    {
        Store db(tmp.path.wstring());
        SYSTEMTIME jan{};jan.wYear=2025;jan.wMonth=1;jan.wDay=1;
        FILETIME epoch;SystemTimeToFileTime(&jan,&epoch);
        for(int d=0;d<365;d++) {
            auto t=asNumber(epoch)+d*864000000000ULL;
            FILETIME ft{DWORD(t),DWORD(t>>32)};SYSTEMTIME day{};FileTimeToSystemTime(&ft,&day);
            char key[16];snprintf(key,sizeof(key),"%04u-%02u-%02u",day.wYear,day.wMonth,day.wDay);
            for(int h=0;h<24;h++)for(int j=0;j<16;j++) {
                std::wstring app=j<8?L"C:\\Program Files\\Sample Application "+std::to_wstring((d+j)%64)+L"\\Program.exe":L"C:\\Program Files\\Browser\\browser.exe";
                auto site=j<8?L"":L"host"+std::to_wstring((d*24*8+h*8+j)%4096)+L".example";
                db.add(key,h,app,site,225000);
            }
            db.flush();
        }
        sqlite3* raw{};check(sqlite3_open16(tmp.path.c_str(),&raw));
        try {
            Statement s(raw,"INSERT INTO apps SELECT value,value,zeroblob(2048),'other' FROM names WHERE value LIKE 'C:%'");
            s.row();
            Statement q(raw,"SELECT count(*),sum(ms) FROM buckets");
            if(!q.row() || q.integer()!=140160)throw std::runtime_error("model row count");
        }catch(...){sqlite3_close(raw);throw;}
        sqlite3_close(raw);live=footprint(tmp.path);
    }
    return {bytes(tmp.path),live};
}
int wmain(int argc,wchar_t** argv) {
    SetConsoleOutputCP(CP_UTF8);
    wchar_t root[MAX_PATH]{};
    if(FAILED(SHGetFolderPathW(nullptr,CSIDL_LOCAL_APPDATA,nullptr,0,root)))return 1;
    fs::path home=fs::path(root)/"Tempo",db=home/"ScreenTime"/"history.sqlite3";
    std::ostringstream report;report<<std::fixed<<std::setprecision(4);
    bool open=true;int seconds=120,rc=0;
    for(int i=1;i<argc;i++) {
        if(equalName(argv[i],L"--no-open"))open=false;
        else if(equalName(argv[i],L"--seconds") && i+1<argc)seconds=_wtoi(argv[++i]);
        else return 2;
    }
    if(seconds<10 || seconds>86400)return 2;
    report<<"Tempo 1.3.0 / ローカル負荷測定\n";
    report<<"実行環境: "<<(GetProcAddress(GetModuleHandleW(L"ntdll"),"wine_get_version")?"Wine（互換環境）":"Windows")<<"\n";
    report<<"Windows build: "<<registry(L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",L"CurrentBuildNumber")<<"\n";
    report<<"CPU: "<<registry(L"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0",L"ProcessorNameString")<<"\n";
    try {
        std::cout<<"常駐本体を "<<seconds<<" 秒間測定します。ダッシュボードを閉じた状態で普段どおり操作してください。\n"<<std::flush;
        Counters p;GPU gpu;p.sample();
        double sumMem=0,peakMem=0,sumGpu=0,peakGpu=0;int samples=0,gpuSamples=0;
        auto start=GetTickCount64();const auto initial=footprint(db);
        do {
            Sleep(2000);p.sample();++samples;sumMem+=p.rss/MB;peakMem=std::max(peakMem,p.rss/MB);
            double g=gpu.sample(p.active);
            if(g>=0){++gpuSamples;sumGpu+=g;peakGpu=std::max(peakGpu,g);}
        }while(GetTickCount64()-start<static_cast<ULONGLONG>(seconds)*1000);
        double elapsed=(GetTickCount64()-start)/1000.0,core=p.cpu/1e5/elapsed;
        auto cores=GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
        report<<"測定秒数: "<<elapsed<<"\n論理プロセッサ数: "<<cores<<"\n";
        report<<"平均 CPU（PC 全体、%）: "<<core/cores<<"\n平均 CPU（1 論理コア換算、%）: "<<core<<"\n";
        report<<"本体・連携ホストの合計メモリ平均（MB）: "<<sumMem/samples<<"\n合計メモリのピーク（MB）: "<<peakMem<<"\n";
        report<<"ブラウザ連携ホストを観測: "<<(p.sawHost?"yes":"no")<<"\n";
        report<<"GPU 有効サンプル数: "<<gpuSamples<<"/"<<samples<<"\n";
        if(gpuSamples)report<<"GPU エンジン使用率の平均 / ピーク（%）: "<<sumGpu/gpuSamples<<" / "<<peakGpu<<"\n";
        else report<<"GPU 使用率: unavailable（取得不能）\n";
        report<<"利用中の履歴容量（DB+WAL+SHM、開始 / 終了バイト）: "<<initial<<" / "<<footprint(db)<<"\n";
        auto size=annual();
        report<<"365 日の模擬使用量: 140,160 行、4,096 ドメイン、65 アプリ\n";
        report<<"模擬履歴の容量（終了後の DB / 使用中の DB+WAL+SHM、MB）: "<<size.first/MB<<" / "<<size.second/MB<<"\n";
        report<<"実際の 365 日間の履歴の増加量: 未測定\n";
    }catch(const std::exception& e){report<<"測定を完了できませんでした: "<<e.what()<<"\n";rc=1;}
    try {
        fs::create_directories(home/"Measurements");SYSTEMTIME time;GetLocalTime(&time);
        wchar_t name[96];swprintf(name,96,L"Tempo-%04u%02u%02u-%02u%02u%02u-%lu.txt",time.wYear,time.wMonth,time.wDay,time.wHour,time.wMinute,time.wSecond,GetCurrentProcessId());
        auto path=home/"Measurements"/name;
        std::ofstream f(path,std::ios::binary);f<<"\xef\xbb\xbf"<<report.str();f.close();if(!f)return 1;
        std::cout<<report.str()<<"保存先: "<<utf8(path.wstring())<<"\n";
        if(open)ShellExecuteW(nullptr,L"open",path.c_str(),nullptr,nullptr,SW_SHOWNORMAL);
    }catch(...){return 1;}
    return rc;
}
