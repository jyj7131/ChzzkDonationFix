using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ChzzkDonationFix
{
    // 윈도우 기본 SQLite(winsqlite3.dll, Windows 10 이상 내장) 최소 래퍼
    sealed class Sqlite : IDisposable
    {
        const string Dll = "winsqlite3.dll";
        const int SQLITE_OK = 0, SQLITE_ROW = 100, SQLITE_DONE = 101;
        const int OPEN_READONLY = 0x1, OPEN_READWRITE = 0x2;

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare16_v2(IntPtr db, [MarshalAs(UnmanagedType.LPWStr)] string sql, int nByte, out IntPtr stmt, IntPtr tail);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr stmt, int i, long v);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_text16(IntPtr stmt, int i, [MarshalAs(UnmanagedType.LPWStr)] string v, int n, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_blob(IntPtr stmt, int i, byte[] v, int n, IntPtr destructor);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern long sqlite3_column_int64(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_text16(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr stmt, int i);
        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_errmsg16(IntPtr db);

        static readonly IntPtr Transient = new IntPtr(-1);
        IntPtr db;

        public Sqlite(string path, bool readOnly)
        {
            var name = Encoding.UTF8.GetBytes(path + "\0");
            if (sqlite3_open_v2(name, out db, readOnly ? OPEN_READONLY : OPEN_READWRITE, IntPtr.Zero) != SQLITE_OK)
                throw new Exception("쿠키 파일을 열지 못했어요: " + Error());
            sqlite3_busy_timeout(db, 3000);
        }

        string Error() => Marshal.PtrToStringUni(sqlite3_errmsg16(db));

        // 매개변수: long, string, byte[]
        public void Exec(string sql, params object[] args) => Query(sql, args, _ => { });

        public void Query(string sql, object[] args, Action<Row> onRow)
        {
            if (sqlite3_prepare16_v2(db, sql, -1, out var st, IntPtr.Zero) != SQLITE_OK) throw new Exception("SQL 오류: " + Error());
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    int rc;
                    switch (args[i])
                    {
                        case long l: rc = sqlite3_bind_int64(st, i + 1, l); break;
                        case int n: rc = sqlite3_bind_int64(st, i + 1, n); break;
                        case string s: rc = sqlite3_bind_text16(st, i + 1, s, -1, Transient); break;
                        case byte[] b: rc = sqlite3_bind_blob(st, i + 1, b, b.Length, Transient); break;
                        default: throw new ArgumentException("지원하지 않는 매개변수");
                    }
                    if (rc != SQLITE_OK) throw new Exception("SQL 바인딩 오류: " + Error());
                }
                int r;
                while ((r = sqlite3_step(st)) == SQLITE_ROW) onRow(new Row(st));
                if (r != SQLITE_DONE) throw new Exception("SQL 실행 오류: " + Error());
            }
            finally { sqlite3_finalize(st); }
        }

        public struct Row
        {
            readonly IntPtr st;
            public Row(IntPtr st) { this.st = st; }
            public long Long(int i) => sqlite3_column_int64(st, i);
            public string Text(int i) => Marshal.PtrToStringUni(sqlite3_column_text16(st, i));
            public byte[] Blob(int i)
            {
                int n = sqlite3_column_bytes(st, i);
                var b = new byte[n];
                if (n > 0) Marshal.Copy(sqlite3_column_blob(st, i), b, 0, n);
                return b;
            }
        }

        public void Dispose()
        {
            if (db != IntPtr.Zero) { sqlite3_close_v2(db); db = IntPtr.Zero; }
        }
    }

    // 윈도우 기본 암호화(CNG)로 AES-256-GCM. .NET Framework 4.8에는 AesGcm이 없어서 직접 호출한다.
    static class AesGcm
    {
        const string Dll = "bcrypt.dll";

        [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr alg, string algId, string impl, int flags);
        [DllImport(Dll, CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr h, string prop, byte[] input, int size, int flags);
        [DllImport(Dll)] static extern int BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr keyObj, int keyObjSize, byte[] secret, int secretSize, int flags);
        [DllImport(Dll)] static extern int BCryptEncrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo pad, byte[] iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
        [DllImport(Dll)] static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo pad, byte[] iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
        [DllImport(Dll)] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport(Dll)] static extern int BCryptCloseAlgorithmProvider(IntPtr alg, int flags);

        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo
        {
            public int cbSize, dwInfoVersion;
            public IntPtr pbNonce; public int cbNonce;
            public IntPtr pbAuthData; public int cbAuthData;
            public IntPtr pbTag; public int cbTag;
            public IntPtr pbMacContext; public int cbMacContext;
            public int cbAAD; public long cbData; public int dwFlags;
        }

        static T WithKey<T>(byte[] key, Func<IntPtr, T> f)
        {
            Check(BCryptOpenAlgorithmProvider(out var alg, "AES", null, 0));
            try
            {
                Check(BCryptSetProperty(alg, "ChainingMode", Encoding.Unicode.GetBytes("ChainingModeGCM\0"), 32, 0));
                Check(BCryptGenerateSymmetricKey(alg, out var hKey, IntPtr.Zero, 0, key, key.Length, 0));
                try { return f(hKey); } finally { BCryptDestroyKey(hKey); }
            }
            finally { BCryptCloseAlgorithmProvider(alg, 0); }
        }

        // 결과: 암호문 + 태그(16바이트)
        public static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plain) => WithKey(key, hKey =>
        {
            var tag = new byte[16];
            var cipher = new byte[plain.Length];
            var hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            var ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                var info = NewInfo(hn, nonce.Length, ht);
                Check(BCryptEncrypt(hKey, plain, plain.Length, ref info, null, 0, cipher, cipher.Length, out _, 0));
            }
            finally { hn.Free(); ht.Free(); }
            var result = new byte[cipher.Length + 16];
            Buffer.BlockCopy(cipher, 0, result, 0, cipher.Length);
            Buffer.BlockCopy(tag, 0, result, cipher.Length, 16);
            return result;
        });

        // 입력: 암호문 + 태그. 태그가 안 맞으면 null
        public static byte[] Decrypt(byte[] key, byte[] nonce, byte[] cipherAndTag) => WithKey(key, hKey =>
        {
            int n = cipherAndTag.Length - 16;
            if (n < 0) return null;
            var cipher = new byte[n];
            var tag = new byte[16];
            Buffer.BlockCopy(cipherAndTag, 0, cipher, 0, n);
            Buffer.BlockCopy(cipherAndTag, n, tag, 0, 16);
            var plain = new byte[n];
            var hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            var ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                var info = NewInfo(hn, nonce.Length, ht);
                return BCryptDecrypt(hKey, cipher, n, ref info, null, 0, plain, n, out _, 0) == 0 ? plain : null;
            }
            finally { hn.Free(); ht.Free(); }
        });

        static AuthInfo NewInfo(GCHandle nonce, int nonceLen, GCHandle tag) => new AuthInfo
        {
            cbSize = Marshal.SizeOf(typeof(AuthInfo)), dwInfoVersion = 1,
            pbNonce = nonce.AddrOfPinnedObject(), cbNonce = nonceLen,
            pbTag = tag.AddrOfPinnedObject(), cbTag = 16,
        };

        static void Check(int status)
        {
            if (status != 0) throw new Exception($"암호화 오류 (0x{status:X8})");
        }
    }
}
