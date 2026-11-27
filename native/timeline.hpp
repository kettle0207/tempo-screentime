#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdlib>
// End the old foreground interval at the notification, even if the next app has the same executable.
template<typename Charge>
void activeParts(int64_t from,int64_t to,int64_t oldInput,int64_t newInput,int64_t limit,Charge charge) {
    if(to<=from)return;
    const int64_t end=std::clamp(oldInput+limit,from,to);
    if(end>from)charge(from,end);
    // The deadline timer samples input before the known active window expires.
    // After idle, only the interval following an observed new input is attributable.
    if(newInput>oldInput) {
        const auto start=std::max(end,std::max(from,newInput));
        const auto stop=std::min(to,newInput+limit);
        if(stop>start)charge(start,stop);
    }
}

inline bool continuousInterval(int64_t elapsed,int64_t wallElapsed,int64_t awakeElapsed) {
    return elapsed>=0 && elapsed<=65000 && std::abs(wallElapsed-elapsed)<1500 && std::abs(awakeElapsed-elapsed)<1000;
}
