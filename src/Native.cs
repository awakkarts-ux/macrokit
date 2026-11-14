using System;
using System.Runtime.InteropServices;

namespace MacroKit
{
    /// <summary>
    /// Тонкая обёртка над WinAPI. Ничего умного тут нет — только объявления.
    /// </summary>
    internal static class Native
    {
        // ---------- Низкоуровневый хук клавиатуры ----------

        public const int WH_KEYBOARD_LL = 13;
        public const int HC_ACTION = 0;

        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint WM_QUIT = 0x0012;

        public const uint LLKHF_INJECTED = 0x00000010;

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // Смещения полей KBDLLHOOKSTRUCT — читаем напрямую, чтобы в колбэке
        // хука не было ни одной аллокации (Marshal.PtrToStructure боксит).
        public const int KBD_VKCODE = 0;
        public const int KBD_SCANCODE = 4;
        public const int KBD_FLAGS = 8;
        public const int KBD_TIME = 12;
        public const int KBD_EXTRAINFO = 16;

        // ---------- Цикл сообщений для потока хука ----------

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll")]
        public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        // ---------- SendInput ----------

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint KEYEVENTF_SCANCODE = 0x0008;

        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        public const uint MOUSEEVENTF_XDOWN = 0x0080;
        public const uint MOUSEEVENTF_XUP = 0x0100;

        public const uint XBUTTON1 = 0x0001;
        public const uint XBUTTON2 = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public const uint MAPVK_VK_TO_VSC = 0;
        public const uint MAPVK_VSC_TO_VK_EX = 3;

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        // ---------- Таймер высокого разрешения ----------

        public const uint CREATE_WAITABLE_TIMER_MANUAL_RESET = 0x00000001;
        public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
        public const uint TIMER_ALL_ACCESS = 0x001F0003;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string lpTimerName,
            uint dwFlags, uint dwDesiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetWaitableTimer(IntPtr hTimer, ref long pDueTime, int lPeriod,
            IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint uPeriod);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint uPeriod);

        // ---------- Активное окно и его процесс ----------

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags,
            System.Text.StringBuilder exeName, ref uint size);

        // ---------- Права процесса ----------

        public const uint TOKEN_QUERY = 0x0008;
        public const int TokenElevation = 20;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

        // ---------- Последний ввод в системе ----------

        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        /// <summary>TickCount последнего ввода с клавиатуры или мыши; 0 — не узнать.</summary>
        public static int LastInputTick()
        {
            try
            {
                var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
                return GetLastInputInfo(ref li) ? unchecked((int)li.dwTime) : 0;
            }
            catch
            {
                return 0;
            }
        }

        // ---------- Окно ----------

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 0x2;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

        /// <summary>
        /// Клавиши, у которых scan-код требует флага "extended".
        /// Без него игры и часть приложений принимают, например, стрелку вправо за цифру 6 на нумпаде.
        /// </summary>
        public static bool IsExtendedKey(int vk)
        {
            switch (vk)
            {
                case 0xA3: // RControl
                case 0xA5: // RMenu (правый Alt)
                case 0x2D: // Insert
                case 0x2E: // Delete
                case 0x24: // Home
                case 0x23: // End
                case 0x21: // PageUp
                case 0x22: // PageDown
                case 0x25: // Left
                case 0x26: // Up
                case 0x27: // Right
                case 0x28: // Down
                case 0x90: // NumLock
                case 0x6F: // Divide
                case 0x2C: // PrintScreen
                case 0x5B: // LWin
                case 0x5C: // RWin
                case 0x5D: // Apps
                    return true;
                default:
                    return false;
            }
        }
    }
}
