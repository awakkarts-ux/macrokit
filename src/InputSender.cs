using System;
using System.Runtime.InteropServices;

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
    /// </summary>
    public static class InputSender
    {
        public static readonly IntPtr Signature = new IntPtr(0x4D4B4954);   // 'MKIT'
        static readonly UIntPtr SigOut = new UIntPtr(0x4D4B4954);

        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        [ThreadStatic] static Native.INPUT[] _buf;

        static Native.INPUT[] Buf
        {
            get
            {
                if (_buf == null) _buf = new Native.INPUT[1];
                return _buf;
            }
        }

        public static void Key(int vk, bool down)
        {
            if (vk <= 0 || vk > 254) return;

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

        public static void Mouse(MouseBtn button, bool down)
        {
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
    }
}
