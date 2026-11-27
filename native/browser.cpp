// Console native-messaging host. It has no windows, rendering engine or managed runtime.
#include <windows.h>
#include <tlhelp32.h>
#include <cwchar>
#include <cstdio>
#include <cstring>
#include <cctype>
#include "site_message.hpp"

namespace {
SiteMessage state{};
SRWLOCK lock=SRWLOCK_INIT;
HANDLE pipe=INVALID_HANDLE_VALUE;
wchar_t name[128]{};
HANDLE events[3]{};

DWORD ancestor() {
    auto snap=CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS,0);
    if(snap==INVALID_HANDLE_VALUE)return 0;
    DWORD pid=GetCurrentProcessId(),result=0;
    PROCESSENTRY32W entry{};entry.dwSize=sizeof(entry);
    for(int depth=0;depth<8 && pid;depth++) {
        DWORD parent=0;
        if(Process32FirstW(snap,&entry))do {
            if(entry.th32ProcessID!=pid)continue;
            if(!_wcsicmp(entry.szExeFile,L"chrome.exe") || !_wcsicmp(entry.szExeFile,L"msedge.exe"))result=pid;
            parent=entry.th32ParentProcessID;break;
        }while(Process32NextW(snap,&entry));
        if(result)break;
        pid=parent;
    }
    CloseHandle(snap);return result;
}
bool readAll(HANDLE in,void* dest,DWORD length) {
    auto out=static_cast<char*>(dest);
    while(length) {
        DWORD n=0;
        if(!ReadFile(in,out,length,&n,nullptr) || !n)return false;
        out+=n;length-=n;
    }
    return true;
}
bool token(const char*& pos,const char* text) {
    while(*pos && std::isspace(static_cast<unsigned char>(*pos)))++pos;
    size_t n=strlen(text);if(strncmp(pos,text,n))return false;
    pos+=n;return true;
}
bool parse(const char* pos,char (&host)[254]) {
    if(!token(pos,"{") || !token(pos,"\"domain\"") || !token(pos,":") || !token(pos,"\""))return false;
    const char* start=pos;while(domainChar(*pos))++pos;
    size_t n=pos-start;
    if(n>=sizeof(host) || *pos++!='"' || !token(pos,"}") || !token(pos,"") || *pos)return false;
    memcpy(host,start,n);host[n]=0;return true;
}
void publish() {
    for(int attempt=0;attempt<2;++attempt) {
        if(pipe==INVALID_HANDLE_VALUE) {
            if(!WaitNamedPipeW(name,500))return;
            pipe=CreateFileW(name,GENERIC_WRITE,0,nullptr,OPEN_EXISTING,0,nullptr);
            if(pipe==INVALID_HANDLE_VALUE)continue;
        }
        DWORD n=0;
        if(WriteFile(pipe,&state,sizeof(state),&n,nullptr) && n==sizeof(state))return;
        CloseHandle(pipe);pipe=INVALID_HANDLE_VALUE;
    }
}
DWORD WINAPI watch(void*) {
    for(;;) {
        auto w=WaitForMultipleObjects(3,events,FALSE,INFINITE);
        if(w==WAIT_OBJECT_0)return 0; // Browser closed stdin.
        if(w==WAIT_OBJECT_0+1)ExitProcess(0); // Upgrade or uninstall.
        if(w!=WAIT_OBJECT_0+2)return 1;
        AcquireSRWLockExclusive(&lock);publish();ReleaseSRWLockExclusive(&lock);
    }
}
}
int main() {
    state.browser=ancestor();if(!state.browser)return 0;
    DWORD session;ProcessIdToSessionId(GetCurrentProcessId(),&session);
    swprintf(name,128,L"\\\\.\\pipe\\Tempo.ScreenTime.Site.1.%lu",session);
    events[0]=CreateEventW(nullptr,FALSE,FALSE,nullptr);
    wchar_t key[96];
    swprintf(key,96,L"Local\\Tempo.Browser.%lu.stop",GetCurrentProcessId());
    events[1]=CreateEventW(nullptr,FALSE,FALSE,key);
    swprintf(key,96,L"Local\\Tempo.Browser.%lu.refresh",GetCurrentProcessId());
    events[2]=CreateEventW(nullptr,FALSE,FALSE,key);
    if(!events[0] || !events[1] || !events[2])return 1;
    HANDLE worker=CreateThread(nullptr,65536,watch,nullptr,STACK_SIZE_PARAM_IS_A_RESERVATION,nullptr);
    if(!worker)return 1;
    HANDLE in=GetStdHandle(STD_INPUT_HANDLE);
    DWORD length;char data[2049];
    while(readAll(in,&length,sizeof(length)) && length && length<sizeof(data)) {
        if(!readAll(in,data,length))break;
        data[length]=0;char domain[254]{};
        if(!memchr(data,0,length))parse(data,domain);
        AcquireSRWLockExclusive(&lock);
        if(strcmp(state.domain,domain) || pipe==INVALID_HANDLE_VALUE){
            strcpy(state.domain,domain);publish();
        }
        ReleaseSRWLockExclusive(&lock);
    }
    SetEvent(events[0]);WaitForSingleObject(worker,INFINITE);
    if(pipe!=INVALID_HANDLE_VALUE)CloseHandle(pipe);
    for(auto h:events)CloseHandle(h);
    CloseHandle(worker);return 0;
}
