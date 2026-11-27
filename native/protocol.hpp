#pragma once
#include <windows.h>
#include <string>
#include <algorithm>
#include <cwctype>
constexpr wchar_t RecorderClass[]=L"Tempo.ScreenTime.Recorder.1";
enum Command : UINT { Show=WM_APP+41, Flush, Pause, IdleLimit, Status };
#include "site_message.hpp"
inline std::wstring modulePath() {
    wchar_t path[32768]{};
    GetModuleFileNameW(nullptr,path,32768);
    return path;
}
inline std::wstring directory(const std::wstring& s){return s.substr(0,s.find_last_of(L"\\/")+1);}
inline bool equalName(const std::wstring& a,const std::wstring& b){return !_wcsicmp(a.c_str(),b.c_str());}
