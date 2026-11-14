using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Следит за Chat.log игры и будит тех, кто ждёт строку по шаблону.
    ///
    /// Aion пишет лог в Windows-1251, строки разделены CR или CRLF, время в
    /// начале строки — с точностью до секунды. Поэтому момент события берём не
    /// из строки, а по тому, когда она появилась в файле: опрашиваем размер раз
    /// в 2 мс. FileSystemWatcher не годится — он склеивает изменения и
    /// приходит с непредсказуемой задержкой.
    ///
    /// Это экспериментальная часть: строка с уроном появляется, когда сервер
    /// уже подтвердил попадание, — для отмены анимации это может быть поздно.
    /// Журнал задержек (Journal) нужен как раз, чтобы это измерить.
    /// </summary>
    public sealed class ChatLog : IDisposable
    {
        internal sealed class Waiter
        {
            public Regex Pattern;
            public long AfterLine;
            public string Hit;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        static readonly Encoding Cp1251 = Encoding.GetEncoding(1251);

        static ChatLog _current;

        /// <summary>Работающий наблюдатель или null, если ни одному макросу лог не нужен.</summary>
        public static ChatLog Current { get { return Volatile.Read(ref _current); } }

        /// <summary>Куда пишутся замеры задержек: рядом с конфигом.</summary>
        public static string JournalPath = Path.Combine(ConfigStore.Dir, "chatlog-timing.txt");

        readonly string _path;
        readonly Thread _thread;
        readonly List<Waiter> _waiters = new List<Waiter>();
        readonly object _gate = new object();
        readonly StringBuilder _partial = new StringBuilder();
        volatile bool _stop;
        long _lines;

        public string FilePath { get { return _path; } }

        ChatLog(string path)
        {
            _path = path;
            _thread = new Thread(Loop) { IsBackground = true, Name = "MacroKit.ChatLog" };
            _thread.Start();
        }

        /// <summary>Запустить, перезапустить на другой файл или остановить (path = null).</summary>
        public static void Use(string path)
        {
            var old = Current;
            if (old != null && path != null && string.Equals(old._path, path, StringComparison.OrdinalIgnoreCase)) return;

            var next = string.IsNullOrEmpty(path) ? null : new ChatLog(path);
            Volatile.Write(ref _current, next);
            if (old != null) old.Dispose();
        }

        /// <summary>
        /// Ожидание строки, заведённое заранее — ДО нажатия, после которого она
        /// придёт: так строка не проскочит между нажатием и началом ожидания.
        /// </summary>
        public sealed class Expectation : IDisposable
        {
            internal Waiter W;
            internal ChatLog Log;

            public void Dispose()
            {
                var log = Log;
                if (log == null) return;
                Log = null;
                lock (log._gate) log._waiters.Remove(W);
                W.Done.Close();
            }
        }

        /// <summary>Начать ждать НОВУЮ строку по шаблону.</summary>
        public Expectation Expect(Regex pattern)
        {
            var w = new Waiter { Pattern = pattern };
            lock (_gate)
            {
                w.AfterLine = _lines;
                _waiters.Add(w);
            }
            return new Expectation { W = w, Log = this };
        }

        /// <summary>Дождаться строки: она сама или null — вышло время или отмена (cancel).</summary>
        public string Wait(Expectation e, int timeoutMs, WaitHandle cancel)
        {
            if (e.Log == null) return null;
            WaitHandle.WaitAny(new[] { e.W.Done, cancel }, timeoutMs);
            return e.W.Hit;
        }

        /// <summary>
        /// Ждёт НОВУЮ строку, подходящую под шаблон. Возвращает её или null —
        /// если вышло время или макрос остановили (cancel).
        /// </summary>
        public string WaitFor(Regex pattern, int timeoutMs, WaitHandle cancel)
        {
            using (var e = Expect(pattern))
                return Wait(e, timeoutMs, cancel);
        }

        /// <summary>Строка в журнал задержек. Пишется в фоне, чтобы не тормозить макрос.</summary>
        public static void Journal(string line)
        {
            MacroKit.Journal.Append(Path.GetFileName(JournalPath), line);
        }

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Patterns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>();

        /// <summary>
        /// Шаблон строки лога, разобранный один раз на всю жизнь программы.
        /// null — шаблон с ошибкой; Error расскажет, с какой.
        /// </summary>
        public static Regex Compile(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            Regex rx;
            if (Patterns.TryGetValue(pattern, out rx)) return rx;

            if (Error(pattern) != null) return null;

            rx = new Regex(pattern, RegexOptions.CultureInvariant);
            Patterns[pattern] = rx;
            return rx;
        }

        /// <summary>Текст ошибки в шаблоне или null, если шаблон годный.</summary>
        public static string Error(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return "шаблон пустой";
            try
            {
                new Regex(pattern, RegexOptions.CultureInvariant);
                return null;
            }
            catch (ArgumentException ex)
            {
                return ex.Message;
            }
        }

        void Loop()
        {
            long pos = -1;
            var buf = new byte[64 * 1024];

            while (!_stop)
            {
                try
                {
                    using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        // Старт — с конца: старые строки нам не нужны
                        if (pos < 0 || pos > fs.Length) pos = fs.Length;

                        while (!_stop)
                        {
                            long len = fs.Length;
                            if (len < pos) { pos = 0; _partial.Length = 0; }   // игра пересоздала файл

                            if (len > pos)
                            {
                                fs.Position = pos;
                                int n;
                                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                                {
                                    pos += n;
                                    Feed(Cp1251.GetString(buf, 0, n));
                                }
                            }

                            Thread.Sleep(2);
                        }
                    }
                }
                catch
                {
                    // Файла ещё нет или он занят — пробуем снова чуть позже
                    Thread.Sleep(500);
                }
            }
        }

        void Feed(string chunk)
        {
            foreach (char c in chunk)
            {
                if (c == '\r' || c == '\n')
                {
                    if (_partial.Length > 0)
                    {
                        Line(_partial.ToString());
                        _partial.Length = 0;
                    }
                }
                else _partial.Append(c);
            }
        }

        void Line(string line)
        {
            // Сначала учёт кд: кто проснётся от строки ниже, уже увидит его
            SkillTimers.OnLine(line);

            lock (_gate)
            {
                _lines++;
                foreach (var w in _waiters)
                {
                    if (w.Hit != null || _lines <= w.AfterLine) continue;
                    if (w.Pattern.IsMatch(line))
                    {
                        w.Hit = line;
                        w.Done.Set();
                    }
                }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }
}
