using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace ChzzkDonationFix
{
    // 유튜브 기기 이름표 쿠키가 XSplit에 없으면 넣어준 뒤, XSplit을 켜고 끝낸다.
    //
    // 배경: 유튜브는 이름표 쿠키(VISITOR_INFO1_LIVE 등)를 "Partitioned" 속성으로 준다.
    // XSplit 내장 브라우저(Chrome 103)는 이 속성을 처리하지 못해 쿠키를 버리고,
    // 그 결과 영상도네이션 임베드마다 새 기기로 인식되어 프리미엄 동시 재생 제한에 걸린다.
    // 같은 쿠키를 Partitioned 없이 XSplit 쿠키 파일에 넣어 두면 XSplit이 계속 기억한다.
    class Launcher
    {
        public class Result
        {
            public bool Ok;
            public string Message;
            public static Result Success(string m) => new Result { Ok = true, Message = m };
            public static Result Fail(string m) => new Result { Ok = false, Message = m };
        }

        const string Marker = "VISITOR_INFO1_LIVE";
        // 유효기간이 이만큼 안 남았으면 미리 새 이름표로 바꾼다 (방송 도중 만료 방지)
        static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
        static readonly string[] Wanted = { "VISITOR_INFO1_LIVE", "VISITOR_PRIVACY_METADATA", "__Secure-ROLLOUT_TOKEN", "__Secure-YNID" };
        const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/103.0.5060.134 Safari/537.36";

        readonly Action<string> status;
        readonly Func<string> locate;

        public Launcher(Action<string> status, Func<string> locate)
        {
            this.status = status; this.locate = locate;
        }

        static bool XSplitRunning => Process.GetProcessesByName("XSplit.Core").Length > 0;

        public async Task<Result> Run()
        {
            Log.Write("---- 실행");

            if (!CookieStore.Exists)
            {
                // XSplit을 한 번도 안 켠 PC면 쿠키 파일이 아직 없다
                Log.Write("쿠키 파일 없음: " + CookieStore.DbPath);
                if (XSplitRunning) return Result.Fail("XSplit을 종료한 뒤 이 프로그램을 다시 실행해 주세요.");
                var r0 = StartXSplit();
                return r0 ?? Result.Fail("XSplit을 처음 실행해서 아직 설정하지 못했어요.\nXSplit을 한 번 종료한 뒤, 이 프로그램으로 다시 켜주세요.");
            }

            status("유튜브 기기 정보 확인 중...");
            DateTimeOffset? expiry;
            try { expiry = CookieStore.GetExpiry(".youtube.com", Marker); }
            catch (Exception e) { Log.Write($"쿠키 읽기 실패: {e.Message}"); expiry = null; }

            var now = DateTimeOffset.UtcNow;
            Log.Write(expiry == null ? "이름표 쿠키 없음"
                : expiry == DateTimeOffset.MaxValue ? "이름표 쿠키 있음 (만료 없음)"
                : $"이름표 쿠키 만료: {expiry.Value.ToLocalTime():yyyy-MM-dd HH:mm} ({(expiry.Value - now).TotalDays:0}일 남음)");

            bool valid = expiry != null && expiry > now;
            if (valid && expiry - now > RenewBefore)
            {
                if (XSplitRunning) return Result.Success("이미 설정되어 있어요. 그대로 방송하시면 됩니다.");
                return StartXSplit() ?? Result.Success("이미 설정되어 있어요. XSplit을 실행합니다.");
            }

            // 쿠키 파일은 XSplit이 꺼져 있을 때만 고칠 수 있다
            if (XSplitRunning)
            {
                if (valid)
                    return Result.Success($"지금은 정상이에요. 다만 기기 정보 유효기간이 {(expiry.Value - now).TotalDays:0}일 남아서,\n다음에 XSplit을 종료한 뒤 이 프로그램으로 켜주시면 미리 갱신해 둘게요.");
                return Result.Fail("기기 정보 설정이 필요해요.\n\nXSplit을 종료한 뒤 이 프로그램을 다시 실행해 주세요.");
            }
            if (valid) Log.Write("만료가 가까워서 새 이름표로 갱신");

            status("유튜브 기기 정보 설정 중...");
            var cookies = await FetchVisitorCookies();
            if (!cookies.Any(c => c.Name == Marker))
                return Result.Fail("유튜브에서 기기 정보를 받아오지 못했어요.\n인터넷 연결을 확인하고 다시 실행해 주세요.");

            try
            {
                CookieStore.Write(cookies);
            }
            catch (NotSupportedException e)
            {
                // XSplit이 업데이트되어 형식이 바뀐 경우. 최신 브라우저라면 문제 자체가 없을 수 있으니 건드리지 않고 켠다.
                Log.Write($"형식 다름, 건너뜀: {e.Message}");
                StartXSplit();
                return Result.Fail("XSplit 버전이 달라서 설정을 건너뛰었어요.\n" + e.Message);
            }

            var written = CookieStore.GetExpiry(".youtube.com", Marker);
            if (written == null || written - DateTimeOffset.UtcNow <= RenewBefore)
                return Result.Fail("유튜브 기기 정보를 저장하지 못했어요.\n기록 폴더의 log.txt를 확인해 주세요:\n" + Log.Dir);

            Log.Write("이름표 쿠키 설정 완료");
            return StartXSplit() ?? Result.Success("설정 완료! XSplit을 실행합니다.");
        }

        // 실패하면 Result, 성공하면 null
        Result StartXSplit()
        {
            status("XSplit 실행 중...");
            var exe = locate();
            if (exe == null) return Result.Fail("XSplit 위치를 지정하지 않아서 실행하지 못했어요.");
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
                Log.Write("XSplit 실행");
                return null;
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                return Result.Fail("XSplit 실행이 취소됐어요.");
            }
        }

        // 쿠키 없이 youtube.com에 접속하면 새 이름표 쿠키 세트를 발급해 준다
        static async Task<List<CookieStore.Cookie>> FetchVisitorCookies()
        {
            var handler = new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            {
                var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
                req.Headers.TryAddWithoutValidation("User-Agent", Ua);
                req.Headers.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9");
                try
                {
                    using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead))
                    {
                        var lines = res.Headers.TryGetValues("Set-Cookie", out var v) ? v.ToList() : new List<string>();
                        Log.Write($"유튜브 응답 {(int)res.StatusCode}, 쿠키 {lines.Count}개");
                        return lines.Select(Parse).Where(c => c != null && Wanted.Contains(c.Name)).ToList();
                    }
                }
                catch (Exception e)
                {
                    Log.Write($"유튜브 접속 실패: {e.Message}");
                    return new List<CookieStore.Cookie>();
                }
            }
        }

        static CookieStore.Cookie Parse(string line)
        {
            var parts = line.Split(';').Select(s => s.Trim()).ToArray();
            int eq = parts[0].IndexOf('=');
            if (eq <= 0) return null;
            var c = new CookieStore.Cookie { Name = parts[0].Substring(0, eq), Value = parts[0].Substring(eq + 1) };
            foreach (var a in parts.Skip(1))
            {
                int i = a.IndexOf('=');
                var key = (i < 0 ? a : a.Substring(0, i)).ToLowerInvariant();
                var val = i < 0 ? "" : a.Substring(i + 1);
                if (key == "domain") c.Domain = val.StartsWith(".") ? val : "." + val;
                else if (key == "path") c.Path = val;
                else if (key == "httponly") c.HttpOnly = true;
                else if (key == "expires" && DateTimeOffset.TryParse(val, out var dt)) c.Expires = dt.ToUnixTimeSeconds();
                else if (key == "max-age" && long.TryParse(val, out var sec)) c.Expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + sec;
            }
            return c;
        }
    }
}
