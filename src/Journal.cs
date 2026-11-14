using System;
using System.IO;
using System.Text;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Текстовые журналы рядом с конфигом: задержки Chat.log, ленты слайдов.
    /// Пишутся в фоне — макрос не должен ждать диска. Файл больше 2 МБ
    /// переименовывается в .old, чтобы журнал не рос бесконечно.
    /// </summary>
    public static class Journal
    {
        const long MaxBytes = 2 * 1024 * 1024;
        static readonly object Gate = new object();

        /// <summary>Подмена для тестов: (файл, строка) приходят сюда, на диск ничего не пишется.</summary>
        public static Action<string, string> Sink;

        public static void Append(string fileName, string line)
        {
            var sink = Sink;
            if (sink != null) { sink(fileName, line); return; }

            string path = Path.Combine(ConfigStore.Dir, fileName);
            string text = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + line + Environment.NewLine;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (Gate)
                {
                    try
                    {
                        Directory.CreateDirectory(ConfigStore.Dir);
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes)
                        {
                            string old = path + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                        File.AppendAllText(path, text, Encoding.UTF8);
                    }
                    catch
                    {
                    }
                }
            });
        }
    }
}
