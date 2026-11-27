#pragma once
#include <windows.h>
struct SiteMessage {DWORD browser;char domain[254];};
inline bool domainChar(char c) {
    return (c>='a' && c<='z') || (c>='0' && c<='9') || c=='.' || c=='-' || c=='[' || c==']' || c==':' || c=='_';
}
