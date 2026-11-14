using System;
using System.IO;
using System.Text;

namespace MacroKit
{
    /// <summary>
    /// Какой процесс владеет активным окном — для режима «только в игре».
    ///
    /// Имя берётся через QueryFullProcessImageName с правами
    /// PROCESS_QUERY_LIMITED_INFORMATION: так оно читается и у игры, запущенной
    /// от администратора, когда сама программа запущена без прав
    /// (Process.MainModule в этом случае падает с отказом в доступе).
    /// </summary>
    public static class ForegroundApp
    {
        static IntPtr _lastHwnd;
        static string _lastFile;

        /// <summary>Имя exe активного окна («aion.bin») или null.</summary>
        public static string CurrentFile()
        {
            IntPtr hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            if (hwnd == _lastHwnd) return _lastFile;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);

            _lastHwnd = hwnd;
            _lastFile = ProcessFile(pid);
            return _lastFile;
        }

        /// <summary>Имя exe процесса по его id или null.</summary>
        public static string ProcessFile(uint pid)
        {
            if (pid == 0) return null;

            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;

            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!Native.QueryFullProcessImageName(h, 0, sb, ref size)) return null;
                return Path.GetFileName(sb.ToString());
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        /// <summary>
        /// Подходит ли файл под то, что вписал пользователь: «aion.bin»,
        /// «aion» или «game» для game.exe. Регистр не важен.
        /// </summary>
        public static bool Matches(string file, string wanted)
        {
            if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(wanted)) return false;

            wanted = wanted.Trim();
            if (string.Equals(file, wanted, StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(Path.GetFileNameWithoutExtension(file), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
