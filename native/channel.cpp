#include "channel.hpp"
#include <deque>
#include <new>
#include <tlhelp32.h>
#include <memory>

// A single IO completion port serves all profiles, including clients blocked on a read.
struct Delivery {DWORD client;SiteMessage msg;};
static SRWLOCK gate=SRWLOCK_INIT;
static std::deque<Delivery> queue;
static void notify(HWND window,DWORD client,const SiteMessage& msg) {
    AcquireSRWLockExclusive(&gate);
    queue.push_back({client,msg});
    ReleaseSRWLockExclusive(&gate);
    PostMessageW(window,SiteChanged,0,0);
}
bool nextDomain(DWORD& client,SiteMessage& msg) {
    AcquireSRWLockExclusive(&gate);
    bool available=!queue.empty();
    if(available){client=queue.front().client;msg=queue.front().msg;queue.pop_front();}
    ReleaseSRWLockExclusive(&gate);return available;
}
void signalHosts(const wchar_t* suffix,bool wait) {
    HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snapshot==INVALID_HANDLE_VALUE)return;
    PROCESSENTRY32W e{};e.dwSize=sizeof(e);
    if(Process32FirstW(snapshot,&e))do {
        if(!equalName(e.szExeFile,L"Tempo.BrowserHost.exe"))continue;
        HANDLE ev=OpenEventW(EVENT_MODIFY_STATE,FALSE,hostEvent(e.th32ProcessID,suffix).c_str());
        if(!ev)continue;
        HANDLE process=wait?OpenProcess(SYNCHRONIZE|PROCESS_QUERY_LIMITED_INFORMATION,FALSE,e.th32ProcessID):nullptr;
        if(wait) {
            wchar_t path[32768]{};DWORD len=32768;
            if(!process || !QueryFullProcessImageNameW(process,0,path,&len) ||
               !equalName(path,directory(modulePath())+L"Tempo.BrowserHost.exe")) {
                if(process)CloseHandle(process);
                CloseHandle(ev);continue;
            }
        }
        SetEvent(ev);CloseHandle(ev);
        if(process){WaitForSingleObject(process,5000);CloseHandle(process);}
    }while(Process32NextW(snapshot,&e));
    CloseHandle(snapshot);
}
struct Peer {
    OVERLAPPED io{};
    HANDLE pipe=INVALID_HANDLE_VALUE;
    DWORD source=0;
    SiteMessage message{};
    ~Peer(){if(pipe!=INVALID_HANDLE_VALUE)CloseHandle(pipe);}
};
static bool acceptNext(HANDLE port,const std::wstring& name,bool first) {
    auto p=std::make_unique<Peer>();
    p->pipe=CreateNamedPipeW(name.c_str(),PIPE_ACCESS_INBOUND|FILE_FLAG_OVERLAPPED|(first?FILE_FLAG_FIRST_PIPE_INSTANCE:0),
        PIPE_TYPE_MESSAGE|PIPE_READMODE_MESSAGE|PIPE_WAIT|PIPE_REJECT_REMOTE_CLIENTS,
        PIPE_UNLIMITED_INSTANCES,512,512,1000,nullptr);
    if(p->pipe==INVALID_HANDLE_VALUE || !CreateIoCompletionPort(p->pipe,port,reinterpret_cast<ULONG_PTR>(p.get()),0))return false;
    if(!ConnectNamedPipe(p->pipe,&p->io)){
        auto err=GetLastError();
        if(err==ERROR_PIPE_CONNECTED) {
            if(!PostQueuedCompletionStatus(port,0,reinterpret_cast<ULONG_PTR>(p.get()),&p->io))return false;
        } else if(err!=ERROR_IO_PENDING)return false;
    }
    p.release();return true;
}
static bool trusted(HANDLE pipe,const std::wstring& expected) {
    ULONG pid=0;if(!GetNamedPipeClientProcessId(pipe,&pid))return false;
    HANDLE owner=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,pid);
    if(!owner)return false;
    wchar_t path[32768]{};DWORD size=32768;
    bool match=QueryFullProcessImageNameW(owner,0,path,&size) && equalName(path,expected);
    CloseHandle(owner);return match;
}
DWORD WINAPI listenSites(void* target) {
    HWND window=static_cast<HWND>(target);
    const auto name=pipeName(),expected=directory(modulePath())+L"Tempo.BrowserHost.exe";
    HANDLE port=CreateIoCompletionPort(INVALID_HANDLE_VALUE,nullptr,0,1);
    if(!port)return 1;
    bool listening=acceptNext(port,name,true);
    if(!listening){CloseHandle(port);return 1;}
    signalHosts(L".refresh",false);
    DWORD seq=0;
    for(;;) {
        DWORD n=0;ULONG_PTR key=0;OVERLAPPED* io=nullptr;
        bool ok=GetQueuedCompletionStatus(port,&n,&key,&io,listening?INFINITE:1000);
        if(!listening)listening=acceptNext(port,name,false);
        if(!io)continue;
        std::unique_ptr<Peer> p(reinterpret_cast<Peer*>(key));
        if(!p->source) {
            listening=acceptNext(port,name,false);
            if(!ok || !trusted(p->pipe,expected))continue;
            p->source=++seq;
        } else if(ok && n==sizeof(p->message)) {
            notify(window,p->source,p->message);
        } else {
            notify(window,p->source,SiteMessage{});continue;
        }
        p->io={};
        if(ReadFile(p->pipe,&p->message,sizeof(p->message),nullptr,&p->io) || GetLastError()==ERROR_IO_PENDING)p.release();
        else notify(window,p->source,SiteMessage{});
    }
}
