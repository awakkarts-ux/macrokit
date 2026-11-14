using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Отправка ввода в систему.
    ///
    /// Два принципиальных момента:
    ///
    /// 1. Шлём scan-коды, а не виртуальные клавиши. Приложения и игры, читающие ввод
    ///    через DirectInput или Raw Input, виртуальные коды часто просто не видят.
    ///    Это причина номер один, по которой самописные макросы "не срабатывают".
    ///
    /// 2. Каждое своё событие помечаем подписью в dwExtraInfo, и хук такие события
    ///    пропускает мимо. Без этого макрос, нажимающий свою же клавишу-триггер,
    ///    ушёл бы в бесконечную рекурсию.
    ///
    /// Плюс общий учёт всего, что мы зажали: если программа упадёт посреди слайда,
    /// ReleaseEverything отпустит W, и персонаж не убежит сам.
    /// </summary>
    public static class InputSender
    {
        public static readonly IntPtr Signature = new IntPtr(0x4D4B4954);   // 'MKIT'
        static readonly UIntPtr SigOut = new UIntPtr(0x4D4B4954);

        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        /// <summary>
        /// Подмена отправки — для тестов, где нет ни user32, ни хука: (vk, down).
        /// Мышь в подмене приходит как vk кнопки (VK_LBUTTON = 1 и т. д.).
        /// </summary>
        public static Action<int, bool> Sink;

        // Сколько раз мы зажали клавишу и ещё не отпустили — по всем проигрывателям.
        static readonly int[] HeldKeys = new int[256];
        static readonly int[] HeldButtons = new int[5];

        [ThreadStatic] static Native.INPUT[] _buf;

        static Native.INPUT[] Buf
        {
            get
            {
                if (_buf == null) _buf = new Native.INPUT[1];
                return _buf;
            }
        }

        // ---------- лента событий для журнала слайдов ----------

        [ThreadStatic] static StringBuilder _trace;
        [ThreadStatic] static Stopwatch _traceClock;

        /// <summary>Начать запись ленты событий текущего потока.</summary>
        public static void BeginTrace()
        {
            _trace = new StringBuilder();
            _traceClock = Stopwatch.StartNew();
        }

        /// <summary>Закончить запись и вернуть ленту: «0.0 W↓ · 20.3 Пробел↓ …».</summary>
        public static string EndTrace()
        {
            var t = _trace;
            _trace = null;
            _traceClock = null;
            return t == null ? "" : t.ToString();
        }

        static void Trace(string name, bool down)
        {
            var t = _trace;
            if (t == null) return;
            if (t.Length > 0) t.Append("  ");
            t.Append(_traceClock.Elapsed.TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture))
             .Append(' ').Append(name).Append(down ? "↓" : "↑");
        }

        // ---------- клавиатура ----------

        public static void Key(int vk, bool down)
        {
            if (vk <= 0 || vk > 254) return;

            if (down) Interlocked.Increment(ref HeldKeys[vk]);
            else if (Interlocked.Decrement(ref HeldKeys[vk]) < 0) Interlocked.Exchange(ref HeldKeys[vk], 0);

            Trace(KeyNames.Get(vk), down);

            var sink = Sink;
            if (sink != null) { sink(vk, down); return; }

            ushort scan = (ushort)Native.MapVirtualKey((uint)vk, Native.MAPVK_VK_TO_VSC);
            uint flags = down ? 0u : Native.KEYEVENTF_KEYUP;

            var b = Buf;
            b[0].type = Native.INPUT_KEYBOARD;

            if (scan != 0)
            {
                flags |= Native.KEYEVENTF_SCANCODE;
                if (Native.IsExtendedKey(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;

                b[0].U.ki = new Native.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = SigOut
                };
            }
            else
            {
                // У клавиши нет scan-кода — отправляем виртуальным кодом.
                b[0].U.ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = SigOut
                };
            }

            Native.SendInput(1, b, InputSize);
        }

        // ---------- мышь ----------

        public static void Mouse(MouseBtn button, bool down)
        {
            int idx = (int)button;
            if (idx >= 0 && idx < HeldButtons.Length)
            {
                if (down) Interlocked.Increment(ref HeldButtons[idx]);
                else if (Interlocked.Decrement(ref HeldButtons[idx]) < 0) Interlocked.Exchange(ref HeldButtons[idx], 0);
            }

            Trace(MacroAction.MouseName(button), down);

            var sink = Sink;
            if (sink != null) { sink(MouseVk(button), down); return; }

            uint flags;
            uint data = 0;

            switch (button)
            {
                case MouseBtn.Left:
                    flags = down ? Native.MOUSEEVENTF_LEFTDOWN : Native.MOUSEEVENTF_LEFTUP;
                    break;
                case MouseBtn.Right:
                    flags = down ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseBtn.Middle:
                    flags = down ? Native.MOUSEEVENTF_MIDDLEDOWN : Native.MOUSEEVENTF_MIDDLEUP;
                    break;
                case MouseBtn.X1:
                    flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP;
                    data = Native.XBUTTON1;
                    break;
                case MouseBtn.X2:
                    flags = down ? Native.MOUSEEVENTF_XDOWN : Native.MOUSEEVENTF_XUP;
                    data = Native.XBUTTON2;
                    break;
                default:
                    return;
            }

            var b = Buf;
            b[0].type = Native.INPUT_MOUSE;
            b[0].U.mi = new Native.MOUSEINPUT
            {
                dx = 0,
                dy = 0,
                mouseData = data,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = SigOut
            };

            Native.SendInput(1, b, InputSize);
        }

        static int MouseVk(MouseBtn b)
        {
            switch (b)
            {
                case MouseBtn.Left: return 0x01;
                case MouseBtn.Right: return 0x02;
                case MouseBtn.Middle: return 0x04;
                case MouseBtn.X1: return 0x05;
                case MouseBtn.X2: return 0x06;
            }
            return 0;
        }

        // ---------- аварийное отпускание ----------

        /// <summary>
        /// Отпустить всё, что зажали макросы и ещё не отпустили. Для падения
        /// программы и выхода: проигрыватели могут не успеть сделать это сами.
        /// Безопасно вызывать несколько раз.
        /// </summary>
        public static void ReleaseEverything()
        {
            for (int vk = 1; vk < HeldKeys.Length - 1; vk++)
            {
                if (Interlocked.Exchange(ref HeldKeys[vk], 0) > 0)
                {
                    try { Key(vk, false); } catch { }
                    Interlocked.Exchange(ref HeldKeys[vk], 0);
                }
            }

            for (int i = 0; i < HeldButtons.Length; i++)
            {
                if (Interlocked.Exchange(ref HeldButtons[i], 0) > 0)
                {
                    try { Mouse((MouseBtn)i, false); } catch { }
                    Interlocked.Exchange(ref HeldButtons[i], 0);
                }
            }
        }
    }
}
