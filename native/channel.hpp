#pragma once
#include "protocol.hpp"
constexpr UINT SiteChanged=WM_APP+24;
inline std::wstring pipeName() {
    DWORD session=0;ProcessIdToSessionId(GetCurrentProcessId(),&session);
    return L"\\\\.\\pipe\\Tempo.ScreenTime.Site.1."+std::to_wstring(session);
}
inline std::wstring hostEvent(DWORD pid,const wchar_t* suffix) {
    return L"Local\\Tempo.Browser."+std::to_wstring(pid)+suffix;
}
