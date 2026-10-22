using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ChzzkDonationFix
{
    // XSplit 내장 브라우저(CEF, Chrome 103)의 쿠키 파일을 직접 읽고 쓴다.
    // - 파일: %APPDATA%\XSplit\Broadcaster\cefcache\Network\Cookies (SQLite, 스키마 버전 18)
    // - 값 암호화: "v10" + nonce(12) + AES-256-GCM(값) + 태그(16)
    // - 키: cefcache\LocalPrefs.json 의 os_crypt.encrypted_key ("DPAPI" + 현재 사용자로 보호된 키)
    class CookieStore
    {
        public const int SupportedSchema = 18;

        public static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"XSplit\Broadcaster\cefcache");
        public static string DbPath => Path.Combine(CacheDir, @"Network\Cookies");
        static string PrefsPath => Path.Combine(CacheDir, "LocalPrefs.json");

        public class Cookie
        {
            public string Name, Value, Domain = ".youtube.com", Path = "/";
            public bool HttpOnly;
            public long? Expires; // 유닉스 초
        }

        public static bool Exists => File.Exists(DbPath) && File.Exists(PrefsPath);

        // 쿠키의 만료 시각. 없으면 null, 만료 없는 세션 쿠키면 DateTimeOffset.MaxValue.
        // 만료된 쿠키도 파일에 한동안 남아 있을 수 있어서, 있는지가 아니라 만료 시각을 봐야 한다.
        // 실행 중인 XSplit이 파일을 잠그고 있을 수 있어서, 읽기만 할 때는 복사본으로 본다.
        public static DateTimeOffset? GetExpiry(string host, string name)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "ChzzkDonationFix-cookies-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.Copy(DbPath, tmp, true);
                using (var db = new Sqlite(tmp, true))
                {
                    DateTimeOffset? result = null;
                    db.Query("SELECT has_expires, expires_utc FROM cookies WHERE host_key=? AND name=?", new object[] { host, name }, r =>
                    {
                        var exp = r.Long(0) == 0 ? DateTimeOffset.MaxValue : DateTimeOffset.FromUnixTimeSeconds(r.Long(1) / 1000000L - 11644473600L);
                        if (result == null || exp > result) result = exp;
                    });
                    return result;
                }
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // XSplit이 꺼져 있을 때만 호출해야 한다
        public static void Write(IEnumerable<Cookie> cookies)
        {
            var key = LoadKey();
            using (var db = new Sqlite(DbPath, false))
            {
                int schema = 0;
                db.Query("SELECT value FROM meta WHERE key='version'", new object[0], r => int.TryParse(r.Text(0), out schema));
                if (schema != SupportedSchema)
                    throw new NotSupportedException($"XSplit 쿠키 파일 형식(버전 {schema})이 예상과 달라요.");

                VerifyKey(db, key);

                long now = ChromeTime(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                db.Exec("BEGIN");
                try
                {
                    foreach (var c in cookies)
                    {
                        var enc = Encrypt(key, c.Value);
                        long expires = c.Expires.HasValue ? ChromeTime(c.Expires.Value) : 0;
                        db.Exec("DELETE FROM cookies WHERE host_key=? AND top_frame_site_key='' AND name=? AND path=?", c.Domain, c.Name, c.Path);
                        db.Exec(@"INSERT INTO cookies (creation_utc, host_key, top_frame_site_key, name, value, encrypted_value, path,
                                    expires_utc, is_secure, is_httponly, last_access_utc, has_expires, is_persistent, priority,
                                    samesite, source_scheme, source_port, is_same_party, last_update_utc)
                                  VALUES (?, ?, '', ?, '', ?, ?, ?, 1, ?, ?, ?, ?, 1, 0, 2, 443, 0, ?)",
                            now++, c.Domain, c.Name, enc, c.Path, expires, c.HttpOnly ? 1 : 0, now,
                            c.Expires.HasValue ? 1 : 0, c.Expires.HasValue ? 1 : 0, now);
                        Log.Write($"쿠키 기록: {c.Name}");
                    }
                    db.Exec("COMMIT");
                }
                catch
                {
                    db.Exec("ROLLBACK");
                    throw;
                }
            }
        }

        // Chrome 시간: 1601-01-01 기준 마이크로초
        static long ChromeTime(long unixSeconds) => (unixSeconds + 11644473600L) * 1000000L;

        static byte[] LoadKey()
        {
            var prefs = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(PrefsPath));
            var b64 = ((Dictionary<string, object>)prefs["os_crypt"])["encrypted_key"] as string;
            var raw = Convert.FromBase64String(b64);
            if (Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI") throw new NotSupportedException("암호 키 형식이 예상과 달라요.");
            return ProtectedData.Unprotect(raw.Skip(5).ToArray(), null, DataProtectionScope.CurrentUser);
        }

        static byte[] Encrypt(byte[] key, string value)
        {
            var nonce = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
            var body = AesGcm.Encrypt(key, nonce, Encoding.UTF8.GetBytes(value));
            return Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(body).ToArray();
        }

        // 기존 쿠키 하나를 풀어봐서 키가 맞는지 확인한다 (틀린 키로 쓰면 XSplit이 못 읽는 쿠키가 된다)
        static void VerifyKey(Sqlite db, byte[] key)
        {
            byte[] sample = null;
            db.Query("SELECT encrypted_value FROM cookies WHERE length(encrypted_value) > 31 LIMIT 1", new object[0], r => sample = r.Blob(0));
            if (sample == null) return; // 비교할 쿠키가 없으면 넘어간다
            if (Encoding.ASCII.GetString(sample, 0, 3) != "v10") throw new NotSupportedException("쿠키 암호화 형식이 예상과 달라요.");
            var nonce = sample.Skip(3).Take(12).ToArray();
            if (AesGcm.Decrypt(key, nonce, sample.Skip(15).ToArray()) == null)
                throw new Exception("XSplit 쿠키 암호 키를 확인하지 못했어요.");
        }
    }
}
