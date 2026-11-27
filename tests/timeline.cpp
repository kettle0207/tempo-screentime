#include "../native/timeline.hpp"
#include <cassert>
#include <random>
#include <iostream>
int64_t sum(int64_t a,int64_t b,int64_t last,int64_t recent,int64_t limit=60000) {
    int64_t total=0,previous=a;
    activeParts(a,b,last,recent,limit,[&](auto from,auto to){
        assert(from>=previous && to>from && to<=b);
        total+=to-from;previous=to;
    });
    return total;
}
int main() {
    assert(sum(100,170,100,100)==70);
    assert(sum(170,200,100,100)==30); // Both sub-second foreground events are retained.
    assert(sum(59000,62000,0,0)==1000);
    assert(sum(61000,67000,0,0)==0);
    assert(sum(61000,67000,0,66500)==500);
    assert(sum(0,60000,0,59000)==60000);
    assert(sum(0,63000,0,61500)==61500);
    // 30-minute and 1-hour away limits: time keeps counting until the limit, then stops.
    constexpr int64_t M=60000;
    assert(sum(29*M,30*M,0,0,30*M)==M);           // still inside 30 min of the last input
    assert(sum(30*M,31*M,0,0,30*M)==0);           // past 30 min: excluded
    assert(sum(30*M,31*M,0,30*M+20000,30*M)==40000); // input after the limit resumes recording
    assert(sum(59*M,60*M,0,0,60*M)==M && sum(60*M,61*M,0,0,60*M)==0);
    assert(continuousInterval(60000,60000,60000));
    assert(!continuousInterval(60000,60000,30000));
    assert(!continuousInterval(3000,8000,3000));
    assert(!continuousInterval(3600000,3600000,3600000));
    std::mt19937 rng(178);
    for(int i=0;i<10000;i++){
        int64_t end=100000+rng()%65000, old=rng()%100001, fresh=std::max<int64_t>(old,rng()%(end+1));
        const auto n=sum(100000,end,old,fresh);
        assert(n>=0 && n<=end-100000);
    }
    std::cout<<"PASS event boundaries, 30 min/1 h away limits, idle cutoff/resume, suspend/clock gaps and 10000 non-overlap cases\n";
}
