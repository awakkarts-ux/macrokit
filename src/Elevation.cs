using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MacroKit
{
    /// <summary>
    /// Права администратора.
    ///
    /// Если игра запущена от администратора, а MacroKit — нет, Windows (UIPI)
    /// не пускает к игре ничего от процесса с меньшими правами: хук не видит
    /// нажатий в её окне, а SendInput в него молча не доходит. Снаружи это
    /// выглядит как «макросы не работают» — без единой ошибки. Поэтому
    /// программа по умолчанию сама перезапускается от администратора
    /// (AppConfig.RunAsAdmin) и замечает, если активное окно выше её по правам.
    /// </summary>
    public static class Elevation
    {
        /// <summary>Аргумент перезапуска: уже пробовали подняться, второй раз UAC не спрашивать.</summary>
        public const string ElevatedArg = "--elevated";

        static bool? _self;

        /// <summary>Запущена ли программа с правами администратора.</summary>
        public static bool IsElevated
        {
            get
            {
                if (_self == null)
                {
                    try
                    {
                        using (var id = WindowsIdentity.GetCurrent())
                            _self = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                    }
                    catch
                    {
                        _self = false;
                    }
                }
                return _self.Value;
            }
        }

        /// <summary>
        /// Запущен ли процесс с правами администратора. null — узнать не удалось.
        /// Если токен процесса не открывается с отказом в доступе, а мы сами без
        /// прав, — процесс почти наверняка выше нас: так UIPI и выглядит снаружи.
        /// </summary>
        public static bool? IsProcessElevated(uint pid)
        {
            if (pid == 0) return null;

            IntPtr proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (proc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() == 5 && !IsElevated ? (bool?)true : null;

            try
            {
                IntPtr token;
                if (!Native.OpenProcessToken(proc, Native.TOKEN_QUERY, out token))
                    return Marshal.GetLastWin32Error() == 5 && !IsElevated ? (bool?)true : null;

                try
                {
                    int elevated, size;
                    if (!Native.GetTokenInformation(token, Native.TokenElevation, out elevated, sizeof(int), out size))
                        return null;
                    return elevated != 0;
                }
                finally
                {
                    Native.CloseHandle(token);
                }
            }
            finally
            {
                Native.CloseHandle(proc);
            }
        }

        /// <summary>
        /// Запустить себя же от администратора. false — пользователь отказал в
        /// окне UAC или запуск не удался; тогда работаем дальше без прав.
        /// </summary>
        public static bool RelaunchElevated()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Process.GetCurrentProcess().MainModule.FileName,
                    Arguments = ElevatedArg,
                    Verb = "runas",
                    UseShellExecute = true
                });
                return true;
            }
            catch (Win32Exception)
            {
                // 1223 — «Нет» в окне UAC
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
