using System;
using System.IO;

namespace ChzzkDonationFix
{
    // %LOCALAPPDATA%\ChzzkDonationFix\log.txt 에 간단한 기록을 남긴다 (문제 생겼을 때 확인용)
    static class Log
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChzzkDonationFix");
        static readonly string FilePath = Path.Combine(Dir, "log.txt");
        static readonly object Lock = new object();

        static Log() => Directory.CreateDirectory(Dir);

        public static void Write(string msg)
        {
            lock (Lock)
            {
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 1024 * 1024)
                    {
                        File.Copy(FilePath, FilePath + ".old", true);
                        File.Delete(FilePath);
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
                }
                catch { }
            }
        }
    }
}
