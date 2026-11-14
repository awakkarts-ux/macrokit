using System;
using System.Threading;
using System.Windows.Forms;

namespace MacroKit
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Два экземпляра поставят два хука и будут дублировать нажатия.
            bool isNew;
            using (var mutex = new Mutex(true, "MacroKit.SingleInstance", out isNew))
            {
                if (!isNew)
                {
                    MessageBox.Show("MacroKit уже запущен.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.ThreadException += (s, e) => Report(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Report(e.ExceptionObject as Exception);

                Application.Run(new Ui.MainForm());
            }
        }

        static void Report(Exception ex)
        {
            if (ex == null) return;

            MessageBox.Show("Непредвиденная ошибка:\n\n" + ex.Message + "\n\n" + ex.StackTrace,
                "MacroKit", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
