using System;
using System.Threading;
using System.Windows.Forms;

namespace TinyHwBar
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;

            using (Mutex mutex = new Mutex(true, @"Local\TinyHwBar.Singleton", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "TinyHwBar 已在运行。请先从系统托盘菜单选择“退出”，再启动新版本。",
                        "TinyHwBar",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                using (MonitorForm form = new MonitorForm())
                {
                    Application.Run(form);
                }
            }
        }
    }
}
