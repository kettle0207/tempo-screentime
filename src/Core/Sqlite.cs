using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Tempo
{
    // Windows 11 supplies winsqlite3.dll. Bind values as UTF-8, including non-ASCII executable paths.
    internal sealed class Sqlite : IDisposable
    {
        private const string Lib = "winsqlite3";
        private IntPtr db;
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg(IntPtr db);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr stmt, IntPtr tail);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr stmt, int pos, byte[] value, int len, IntPtr free);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_int64(IntPtr stmt, int pos, long value);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr stmt);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);
        [DllImport(Lib, CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr stmt, int col);

        static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
        public Sqlite(string path)
        {
            int rc = sqlite3_open_v2(Utf8(path), out db, 6, IntPtr.Zero);
            if (rc != 0) { Dispose(); throw new InvalidOperationException("記録データベースを開けません。"); }
            Check(sqlite3_busy_timeout(db, 3000));
        }
        void Check(int rc)
        {
            if (rc != 0) throw new InvalidOperationException("SQLite " + rc + " " + Marshal.PtrToStringAnsi(sqlite3_errmsg(db)));
        }
        public List<string[]> Run(string sql, params object[] values)
        {
            var rows = new List<string[]>();
            Check(sqlite3_prepare_v2(db, Utf8(sql), -1, out var stmt, IntPtr.Zero));
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] is string s)
                    {
                        var bytes = Utf8(s);
                        Check(sqlite3_bind_text(stmt, i+1, bytes, bytes.Length-1, new IntPtr(-1)));
                    }
                    else Check(sqlite3_bind_int64(stmt, i+1, Convert.ToInt64(values[i])));
                }
                int rc;
                while ((rc = sqlite3_step(stmt)) == 100)
                {
                    var row = new string[sqlite3_column_count(stmt)];
                    for (int i = 0; i < row.Length; i++)
                    {
                        var ptr = sqlite3_column_text(stmt, i);
                        var bytes = new byte[sqlite3_column_bytes(stmt, i)];
                        if (bytes.Length > 0) Marshal.Copy(ptr, bytes, 0, bytes.Length);
                        row[i] = Encoding.UTF8.GetString(bytes);
                    }
                    rows.Add(row);
                }
                if (rc != 101) Check(rc);
                return rows;
            }
            finally { sqlite3_finalize(stmt); }
        }
        public void Dispose() { if (db != IntPtr.Zero) { sqlite3_close(db); db = IntPtr.Zero; } }
    }
}
