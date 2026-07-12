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
