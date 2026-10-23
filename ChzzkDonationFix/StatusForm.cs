using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ChzzkDonationFix
{
    // 진행 상황을 보여주는 작은 창. 끝나면 스스로 닫힌다.
    class StatusForm : Form
    {
        readonly Label label;

        public StatusForm()
        {
            Text = Program.AppName;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 90);
            Font = new Font("맑은 고딕", 10f);
            label = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "준비 중..." };
            Controls.Add(label);
            Shown += async (s, e) => await RunAsync();
        }

        void Status(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Status(text))); return; }
            label.Text = text;
        }

        async Task RunAsync()
        {
            Launcher.Result r;
            try
            {
                // XSplit 위치 찾기(선택 창)는 UI 스레드에서 해야 해서 먼저 한다
                var launcher = new Launcher(Status, () => XSplitLocator.Find(this));
                r = await Task.Run(() => launcher.Run());
            }
            catch (Exception e)
            {
                Log.Write($"예외: {e}");
                r = Launcher.Result.Fail("알 수 없는 오류가 발생했어요.\n" + e.Message);
            }

            if (r.Ok)
            {
                Status(r.Message);
                await Task.Delay(1500);
            }
            else
            {
                Hide();
                MessageBox.Show(r.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Close();
        }
    }
}
