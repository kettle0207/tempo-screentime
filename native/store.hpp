#pragma once
#include <windows.h>
#include <map>
#include <string>
#include <tuple>
#include <stdexcept>
#include <cstdint>

struct sqlite3;
struct sqlite3_stmt;
extern "C" {
int sqlite3_open16(const void*,sqlite3**);
int sqlite3_close(sqlite3*);
int sqlite3_exec(sqlite3*,const char*,void*,void*,char**);
int sqlite3_busy_timeout(sqlite3*,int);
int sqlite3_prepare_v2(sqlite3*,const char*,int,sqlite3_stmt**,const char**);
int sqlite3_finalize(sqlite3_stmt*);
int sqlite3_reset(sqlite3_stmt*);
int sqlite3_clear_bindings(sqlite3_stmt*);
int sqlite3_bind_int64(sqlite3_stmt*,int,int64_t);
int sqlite3_bind_text(sqlite3_stmt*,int,const char*,int,void(*)(void*));
int sqlite3_bind_text16(sqlite3_stmt*,int,const void*,int,void(*)(void*));
int sqlite3_step(sqlite3_stmt*);
int64_t sqlite3_column_int64(sqlite3_stmt*,int);
const void* sqlite3_column_text16(sqlite3_stmt*,int);
}

inline void check(int rc) { if(rc) throw std::runtime_error("SQLite "+std::to_string(rc)); }
class Statement {
    sqlite3_stmt* p{};
public:
    Statement(sqlite3* db,const char* sql) { check(sqlite3_prepare_v2(db,sql,-1,&p,nullptr)); }
    ~Statement() { sqlite3_finalize(p); }
    void reset() { sqlite3_reset(p); sqlite3_clear_bindings(p); }
    void bind(int i,const std::wstring& v) { check(sqlite3_bind_text16(p,i,v.data(),int(v.size()*2),reinterpret_cast<void(*)(void*)>(-1))); }
    void bind(int i,const std::string& v) { check(sqlite3_bind_text(p,i,v.data(),int(v.size()),reinterpret_cast<void(*)(void*)>(-1))); }
    void bind(int i,int64_t v) { check(sqlite3_bind_int64(p,i,v)); }
    bool row() { int rc=sqlite3_step(p); if(rc==100)return true; if(rc!=101)check(rc); return false; }
    int64_t integer() { return sqlite3_column_int64(p,0); }
    std::wstring string() { return static_cast<const wchar_t*>(sqlite3_column_text16(p,0)); }
};
using Bucket=std::tuple<std::string,int,std::wstring,std::wstring>;
class Store {
    sqlite3* db{};
    std::map<Bucket,int64_t> pending;
    void exec(const char* s) { check(sqlite3_exec(db,s,nullptr,nullptr,nullptr)); }
public:
    explicit Store(const std::wstring& path) {
        try {
            check(sqlite3_open16(path.c_str(),&db));
            check(sqlite3_busy_timeout(db,1500));
            exec("PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA cache_size=-512;PRAGMA wal_autocheckpoint=128;PRAGMA journal_size_limit=524288;");
            auto r=FindResourceW(nullptr,MAKEINTRESOURCEW(102),RT_RCDATA);
            if(!r) throw std::runtime_error("schema resource");
            auto ptr=static_cast<const char*>(LockResource(LoadResource(nullptr,r)));
            std::string schema(ptr,SizeofResource(nullptr,r));
            exec(schema.c_str());
        } catch(...) {sqlite3_close(db);throw;}
    }
    ~Store() {sqlite3_close(db);}
    std::wstring setting(const std::wstring& key,const wchar_t* fallback) {
        Statement q(db,"SELECT value FROM settings WHERE id=?");
        q.bind(1,key);return q.row()?q.string():fallback;
    }
    void set(const std::wstring& key,const std::wstring& value) {
        Statement q(db,"INSERT OR REPLACE INTO settings VALUES(?,?)");
        q.bind(1,key);q.bind(2,value);q.row();
    }
    void add(const std::string& day,int hour,const std::wstring& app,const std::wstring& site,int64_t ms) {
        pending[{day,hour,app,site}]+=ms;
    }
    bool dirty() const { return !pending.empty(); }
    void flush() {
        if(pending.empty())return;
        exec("BEGIN IMMEDIATE");
        try {
            Statement insert(db,"INSERT OR IGNORE INTO names(value) VALUES(?)");
            Statement lookup(db,"SELECT id FROM names WHERE value=?");
            Statement save(db,"INSERT INTO buckets VALUES(?,?,?,?,?) ON CONFLICT(day,hour,app,site) DO UPDATE SET ms=buckets.ms+excluded.ms");
            std::map<std::wstring,int64_t> ids;
            auto id=[&](const std::wstring& value) {
                auto it=ids.find(value);if(it!=ids.end())return it->second;
                insert.reset();insert.bind(1,value);insert.row();
                lookup.reset();lookup.bind(1,value);if(!lookup.row())throw std::runtime_error("missing id");
                return ids[value]=lookup.integer();
            };
            for(auto& [key,ms]:pending) {
                const auto& [day,hour,app,site]=key;
                save.reset();save.bind(1,day);save.bind(2,hour);save.bind(3,id(app));save.bind(4,id(site));save.bind(5,ms);save.row();
            }
            // A SELECT paused at SQLITE_ROW holds a read snapshot and can pin the WAL at commit.
            lookup.reset();
            exec("COMMIT");pending.clear();
        } catch(...) {exec("ROLLBACK");throw;}
    }
};
