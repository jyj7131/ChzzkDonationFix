using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ChzzkDonationFix
{
    // XSplit.Core.exe 위치 찾기: 저장해 둔 경로 → 레지스트리(설치 정보) → 기본 경로 → 직접 선택
    static class XSplitLocator
    {
        const string ExeName = "XSplit.Core.exe";
        static readonly string SavedPathFile = Path.Combine(Log.Dir, "xsplit-path.txt");

        public static string Find(Form owner)
        {
            string path = FromSaved() ?? FromRegistry() ?? FromDefault();
            if (path == null)
            {
                path = (string)owner.Invoke(new Func<string>(() => AskUser(owner)));
                if (path == null) return null;
            }
            try { File.WriteAllText(SavedPathFile, path); } catch { }
            Log.Write($"XSplit 위치: {path}");
            return path;
        }

        static string FromSaved()
        {
            try
            {
                var p = File.Exists(SavedPathFile) ? File.ReadAllText(SavedPathFile).Trim() : null;
                return p != null && File.Exists(p) ? p : null;
            }
            catch { return null; }
        }

        static string FromRegistry()
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (uninstall == null) continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using (var k = uninstall.OpenSubKey(name))
                        {
                            if (!((k?.GetValue("DisplayName") as string) ?? "").StartsWith("XSplit Broadcaster")) continue;
                            var dir = k.GetValue("InstallLocation") as string;
                            if (string.IsNullOrEmpty(dir)) continue;
                            var p = Path.Combine(dir, ExeName);
                            if (File.Exists(p)) return p;
                        }
                    }
                }
            }
            return null;
        }

        static string FromDefault()
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"XSplit\Broadcaster", ExeName);
            return File.Exists(p) ? p : null;
        }

        static string AskUser(Form owner)
        {
            MessageBox.Show(owner, "XSplit 설치 위치를 찾지 못했어요.\n다음 창에서 XSplit.Core.exe 파일을 선택해 주세요.",
                Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            using (var dlg = new OpenFileDialog { Title = "XSplit.Core.exe 선택", Filter = "XSplit|XSplit.Core.exe|실행 파일|*.exe" })
                return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
        }
    }
}
