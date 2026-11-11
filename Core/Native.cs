using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaReviewer {
    public static class Native {
        public static string RuntimeDirectory { get; private set; }
        public static string SqliteVersion { get { return Utf8(Sql.sqlite3_libversion()); } }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", ExactSpelling=true)] static extern IntPtr GetProcAddress(IntPtr module,string name);
        internal static T Export<T>(string name) where T:class {
            IntPtr address=GetProcAddress(GetModuleHandle(null),name);
            if(address==IntPtr.Zero)throw new EntryPointNotFoundException(name);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(address,typeof(T));
        }
        public static void Initialize(string scratchDirectory) {
            if(!Environment.Is64BitProcess)throw new PlatformNotSupportedException("An x64 Windows process is required.");
            if(SqliteVersion!="3.54.1")throw new InvalidOperationException("Unexpected linked SQLite version: "+SqliteVersion);
            RuntimeDirectory=Path.GetFullPath(scratchDirectory);
        }
        internal static byte[] Text(string text) { return Encoding.UTF8.GetBytes(text+"\0"); }
        internal static string Utf8(IntPtr pointer) {
            if(pointer==IntPtr.Zero) return "";
            int count=0; while(Marshal.ReadByte(pointer,count)!=0) { if(++count>2*1024*1024) throw new InvalidDataException("Native text exceeds bounded limit"); }
            byte[] bytes=new byte[count]; Marshal.Copy(pointer,bytes,0,count); return Encoding.UTF8.GetString(bytes);
        }
    }
    public sealed class DatabaseException: IOException {
        public readonly int Code;
        public bool IsTransient { get { return (Code&255)==5 || (Code&255)==6; } }
        internal DatabaseException(int code,string message):base("SQLite "+code+": "+message) { Code=code; }
    }
    internal static class Sql {

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr sqlite3_libversion_call();
        internal static readonly sqlite3_libversion_call sqlite3_libversion=Native.Export<sqlite3_libversion_call>("sqlite3_libversion");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_open_v2_call(byte[] path,out IntPtr db,int flags,IntPtr vfs);
        internal static readonly sqlite3_open_v2_call sqlite3_open_v2=Native.Export<sqlite3_open_v2_call>("sqlite3_open_v2");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_close_v2_call(IntPtr db);
        internal static readonly sqlite3_close_v2_call sqlite3_close_v2=Native.Export<sqlite3_close_v2_call>("sqlite3_close_v2");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_extended_result_codes_call(IntPtr db,int value);
        internal static readonly sqlite3_extended_result_codes_call sqlite3_extended_result_codes=Native.Export<sqlite3_extended_result_codes_call>("sqlite3_extended_result_codes");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_busy_timeout_call(IntPtr db,int ms);
        internal static readonly sqlite3_busy_timeout_call sqlite3_busy_timeout=Native.Export<sqlite3_busy_timeout_call>("sqlite3_busy_timeout");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr sqlite3_errmsg_call(IntPtr db);
        internal static readonly sqlite3_errmsg_call sqlite3_errmsg=Native.Export<sqlite3_errmsg_call>("sqlite3_errmsg");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_exec_call(IntPtr db,byte[] sql,IntPtr callback,IntPtr data,out IntPtr error);
        internal static readonly sqlite3_exec_call sqlite3_exec=Native.Export<sqlite3_exec_call>("sqlite3_exec");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void sqlite3_free_call(IntPtr ptr);
        internal static readonly sqlite3_free_call sqlite3_free=Native.Export<sqlite3_free_call>("sqlite3_free");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_prepare_v2_call(IntPtr db,byte[] sql,int length,out IntPtr stmt,out IntPtr tail);
        internal static readonly sqlite3_prepare_v2_call sqlite3_prepare_v2=Native.Export<sqlite3_prepare_v2_call>("sqlite3_prepare_v2");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_parameter_count_call(IntPtr stmt);
        internal static readonly sqlite3_bind_parameter_count_call sqlite3_bind_parameter_count=Native.Export<sqlite3_bind_parameter_count_call>("sqlite3_bind_parameter_count");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_null_call(IntPtr stmt,int i);
        internal static readonly sqlite3_bind_null_call sqlite3_bind_null=Native.Export<sqlite3_bind_null_call>("sqlite3_bind_null");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_int64_call(IntPtr stmt,int i,long value);
        internal static readonly sqlite3_bind_int64_call sqlite3_bind_int64=Native.Export<sqlite3_bind_int64_call>("sqlite3_bind_int64");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_double_call(IntPtr stmt,int i,double value);
        internal static readonly sqlite3_bind_double_call sqlite3_bind_double=Native.Export<sqlite3_bind_double_call>("sqlite3_bind_double");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_text_call(IntPtr stmt,int i,byte[] value,int length,IntPtr dispose);
        internal static readonly sqlite3_bind_text_call sqlite3_bind_text=Native.Export<sqlite3_bind_text_call>("sqlite3_bind_text");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_blob_call(IntPtr stmt,int i,byte[] value,int length,IntPtr dispose);
        internal static readonly sqlite3_bind_blob_call sqlite3_bind_blob=Native.Export<sqlite3_bind_blob_call>("sqlite3_bind_blob");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_bind_zeroblob_call(IntPtr stmt,int i,int length);
        internal static readonly sqlite3_bind_zeroblob_call sqlite3_bind_zeroblob=Native.Export<sqlite3_bind_zeroblob_call>("sqlite3_bind_zeroblob");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_step_call(IntPtr stmt);
        internal static readonly sqlite3_step_call sqlite3_step=Native.Export<sqlite3_step_call>("sqlite3_step");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_reset_call(IntPtr stmt);
        internal static readonly sqlite3_reset_call sqlite3_reset=Native.Export<sqlite3_reset_call>("sqlite3_reset");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_clear_bindings_call(IntPtr stmt);
        internal static readonly sqlite3_clear_bindings_call sqlite3_clear_bindings=Native.Export<sqlite3_clear_bindings_call>("sqlite3_clear_bindings");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_finalize_call(IntPtr stmt);
        internal static readonly sqlite3_finalize_call sqlite3_finalize=Native.Export<sqlite3_finalize_call>("sqlite3_finalize");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_column_count_call(IntPtr stmt);
        internal static readonly sqlite3_column_count_call sqlite3_column_count=Native.Export<sqlite3_column_count_call>("sqlite3_column_count");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_column_type_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_type_call sqlite3_column_type=Native.Export<sqlite3_column_type_call>("sqlite3_column_type");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long sqlite3_column_int64_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_int64_call sqlite3_column_int64=Native.Export<sqlite3_column_int64_call>("sqlite3_column_int64");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate double sqlite3_column_double_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_double_call sqlite3_column_double=Native.Export<sqlite3_column_double_call>("sqlite3_column_double");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr sqlite3_column_text_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_text_call sqlite3_column_text=Native.Export<sqlite3_column_text_call>("sqlite3_column_text");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr sqlite3_column_blob_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_blob_call sqlite3_column_blob=Native.Export<sqlite3_column_blob_call>("sqlite3_column_blob");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_column_bytes_call(IntPtr stmt,int i);
        internal static readonly sqlite3_column_bytes_call sqlite3_column_bytes=Native.Export<sqlite3_column_bytes_call>("sqlite3_column_bytes");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_changes_call(IntPtr db);
        internal static readonly sqlite3_changes_call sqlite3_changes=Native.Export<sqlite3_changes_call>("sqlite3_changes");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate long sqlite3_last_insert_rowid_call(IntPtr db);
        internal static readonly sqlite3_last_insert_rowid_call sqlite3_last_insert_rowid=Native.Export<sqlite3_last_insert_rowid_call>("sqlite3_last_insert_rowid");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr sqlite3_backup_init_call(IntPtr target,byte[] targetName,IntPtr source,byte[] sourceName);
        internal static readonly sqlite3_backup_init_call sqlite3_backup_init=Native.Export<sqlite3_backup_init_call>("sqlite3_backup_init");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_backup_step_call(IntPtr backup,int pages);
        internal static readonly sqlite3_backup_step_call sqlite3_backup_step=Native.Export<sqlite3_backup_step_call>("sqlite3_backup_step");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_backup_finish_call(IntPtr backup);
        internal static readonly sqlite3_backup_finish_call sqlite3_backup_finish=Native.Export<sqlite3_backup_finish_call>("sqlite3_backup_finish");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_backup_remaining_call(IntPtr backup);
        internal static readonly sqlite3_backup_remaining_call sqlite3_backup_remaining=Native.Export<sqlite3_backup_remaining_call>("sqlite3_backup_remaining");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_backup_pagecount_call(IntPtr backup);
        internal static readonly sqlite3_backup_pagecount_call sqlite3_backup_pagecount=Native.Export<sqlite3_backup_pagecount_call>("sqlite3_backup_pagecount");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_blob_open_call(IntPtr db,byte[] schema,byte[] table,byte[] column,long rowid,int flags,out IntPtr blob);
        internal static readonly sqlite3_blob_open_call sqlite3_blob_open=Native.Export<sqlite3_blob_open_call>("sqlite3_blob_open");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_blob_read_call(IntPtr blob,IntPtr buffer,int count,int offset);
        internal static readonly sqlite3_blob_read_call sqlite3_blob_read=Native.Export<sqlite3_blob_read_call>("sqlite3_blob_read");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_blob_close_call(IntPtr blob);
        internal static readonly sqlite3_blob_close_call sqlite3_blob_close=Native.Export<sqlite3_blob_close_call>("sqlite3_blob_close");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int sqlite3_blob_bytes_call(IntPtr blob);
        internal static readonly sqlite3_blob_bytes_call sqlite3_blob_bytes=Native.Export<sqlite3_blob_bytes_call>("sqlite3_blob_bytes");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ProgressCallback(IntPtr state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void sqlite3_progress_handler_call(IntPtr db,int instructions,ProgressCallback callback,IntPtr state);
        internal static readonly sqlite3_progress_handler_call sqlite3_progress_handler=Native.Export<sqlite3_progress_handler_call>("sqlite3_progress_handler");
    }
    public sealed class SqliteConnection:IDisposable {
        internal IntPtr Handle;
        public string FilePath { get;private set; }
        readonly int owner;
        public SqliteConnection(string path,bool readOnly) {
            FilePath=Path.GetFullPath(path);
            owner=System.Threading.Thread.CurrentThread.ManagedThreadId;
            int rc=Sql.sqlite3_open_v2(Native.Text(Path.GetFullPath(path)),out Handle,readOnly ? 1 : 2|4,IntPtr.Zero);
            if(rc!=0) { string error=Handle==IntPtr.Zero ? "Cannot open database" : Native.Utf8(Sql.sqlite3_errmsg(Handle)); if(Handle!=IntPtr.Zero) Sql.sqlite3_close_v2(Handle); Handle=IntPtr.Zero; throw new DatabaseException(rc,error); }
            Sql.sqlite3_extended_result_codes(Handle,1); Sql.sqlite3_busy_timeout(Handle,3000);
            // Catalogs travel between machines. With trusted_schema off, SQL stored in an opened file's views and
            // triggers cannot call functions or virtual tables that SQLite does not mark as safe for an untrusted schema.
            try { Script("PRAGMA trusted_schema=OFF;"); } catch { Sql.sqlite3_close_v2(Handle); Handle=IntPtr.Zero; throw; }
        }
        internal void CheckOwner() { if(Handle==IntPtr.Zero) throw new ObjectDisposedException("SQLite connection"); if(owner!=System.Threading.Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("SQLite connection belongs to another worker"); }
        internal void Check(int rc) { if(rc!=0 && rc!=100 && rc!=101) throw new DatabaseException(rc,Native.Utf8(Sql.sqlite3_errmsg(Handle))); }
        public void Script(string sql) {
            CheckOwner(); IntPtr error; int rc=Sql.sqlite3_exec(Handle,Native.Text(sql),IntPtr.Zero,IntPtr.Zero,out error);
            try { if(rc!=0) throw new DatabaseException(rc,error==IntPtr.Zero ? Native.Utf8(Sql.sqlite3_errmsg(Handle)) : Native.Utf8(error)); }
            finally { if(error!=IntPtr.Zero) Sql.sqlite3_free(error); }
        }
        public SqliteStatement Prepare(string sql) { CheckOwner(); return new SqliteStatement(this,sql); }
        public int Execute(string sql,params object[] args) { using(var stmt=Prepare(sql)) { stmt.Bind(args); while(stmt.Step()){} return Sql.sqlite3_changes(Handle); } }
        public object Scalar(string sql,params object[] args) { using(var stmt=Prepare(sql)) { stmt.Bind(args); return stmt.Step() ? stmt.Value(0) : null; } }
        public List<object[]> Query(string sql,object[] args,int maxRows) {
            if(maxRows<1 || maxRows>10000) throw new ArgumentOutOfRangeException("maxRows","A bounded row limit is required");
            var rows=new List<object[]>(); using(var stmt=Prepare(sql)) { stmt.Bind(args); while(stmt.Step()) { if(rows.Count==maxRows) throw new InvalidOperationException("Query exceeded its bounded row limit"); rows.Add(stmt.Row()); } } return rows;
        }
        public long LastId { get { CheckOwner(); return Sql.sqlite3_last_insert_rowid(Handle); } }
        public void Dispose() { if(Handle!=IntPtr.Zero) { CheckOwner(); Sql.sqlite3_close_v2(Handle); Handle=IntPtr.Zero; } }
    }
    public sealed class SqliteStatement:IDisposable {
        readonly SqliteConnection db; IntPtr stmt;
        internal SqliteStatement(SqliteConnection connection,string sql) { db=connection; IntPtr tail; byte[] bytes=Native.Text(sql); db.Check(Sql.sqlite3_prepare_v2(db.Handle,bytes,bytes.Length-1,out stmt,out tail)); if(stmt==IntPtr.Zero) throw new ArgumentException("Empty SQL statement"); }
        public void Bind(params object[] values) {
            db.CheckOwner(); db.Check(Sql.sqlite3_reset(stmt)); db.Check(Sql.sqlite3_clear_bindings(stmt));
            if(values==null) values=new object[0];
            if(values.Length!=Sql.sqlite3_bind_parameter_count(stmt)) throw new ArgumentException("SQL parameter count mismatch");
            for(int i=0;i<values.Length;i++) {
                object value=values[i];var imported=value as ImportCell;if(imported!=null)value=imported.BindingValue;int n=i+1,rc;
                if(value==null || value==DBNull.Value) rc=Sql.sqlite3_bind_null(stmt,n);
                else if(value is byte[]) { byte[] bytes=(byte[])value; rc=bytes.Length==0 ? Sql.sqlite3_bind_zeroblob(stmt,n,0) : Sql.sqlite3_bind_blob(stmt,n,bytes,bytes.Length,new IntPtr(-1)); }
                else if(value is string) { byte[] bytes=Native.Text((string)value); rc=Sql.sqlite3_bind_text(stmt,n,bytes,bytes.Length-1,new IntPtr(-1)); }
                else if(value is double || value is float || value is decimal) rc=Sql.sqlite3_bind_double(stmt,n,Convert.ToDouble(value,System.Globalization.CultureInfo.InvariantCulture));
                else if(value is bool) rc=Sql.sqlite3_bind_int64(stmt,n,(bool)value ? 1 : 0);
                else rc=Sql.sqlite3_bind_int64(stmt,n,Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture));
                db.Check(rc);
            }
        }
        public bool Step() { db.CheckOwner(); int rc=Sql.sqlite3_step(stmt); db.Check(rc); return rc==100; }
        internal int ColumnType(int column){db.CheckOwner();return Sql.sqlite3_column_type(stmt,column);}
        internal int ColumnBytes(int column){db.CheckOwner();return Sql.sqlite3_column_bytes(stmt,column);}
        public object Value(int i) {
            db.CheckOwner(); int type=Sql.sqlite3_column_type(stmt,i);
            if(type==5) return null;
            if(type==1) return Sql.sqlite3_column_int64(stmt,i);
            if(type==2) return Sql.sqlite3_column_double(stmt,i);
            int length=Sql.sqlite3_column_bytes(stmt,i);
            if(length>(type==4 ? 8*1024*1024 : 2*1024*1024)) throw new InvalidDataException("Column exceeds bounded read limit; use incremental reads");
            byte[] bytes=new byte[length]; if(length!=0) Marshal.Copy(type==4 ? Sql.sqlite3_column_blob(stmt,i) : Sql.sqlite3_column_text(stmt,i),bytes,0,length);
            return type==4 ? (object)bytes : Encoding.UTF8.GetString(bytes);
        }
        public object[] Row() { int count=Sql.sqlite3_column_count(stmt); object[] row=new object[count]; for(int i=0;i<count;i++) row[i]=Value(i); return row; }
        public void Dispose() { if(stmt!=IntPtr.Zero) { db.CheckOwner(); Sql.sqlite3_finalize(stmt); stmt=IntPtr.Zero; } }
    }
}
