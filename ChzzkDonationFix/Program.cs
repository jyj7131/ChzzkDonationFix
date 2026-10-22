using System;
using System.Threading;
using System.Windows.Forms;

namespace ChzzkDonationFix
{
    static class Program
    {
        public const string AppName = "치지직 영상도네이션 멈춤 방지";

        [STAThread]
        static void Main()
        {
            using (var mutex = new Mutex(true, "ChzzkDonationFix_SingleInstance", out bool created))
            {
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new StatusForm());
            }
        }
    }
}
