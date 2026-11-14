using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MacroKit
{
    /// <summary>Оборачивает HANDLE ожидаемого таймера в WaitHandle, чтобы ждать его вместе с событием отмены.</summary>
    internal sealed class RawWaitHandle : WaitHandle
    {
        public RawWaitHandle(IntPtr h)
        {
            SafeWaitHandle = new SafeWaitHandle(h, false);
        }
    }

    /// <summary>
    /// Точная пауза, которую можно прервать.
    ///
    /// Обычный Thread.Sleep(5) реально спит около 15 мс — именно поэтому в родном софте
    /// клавиатуры задержки в макросах не соблюдаются. Здесь используется waitable-таймер
    /// высокого разрешения (точность ~0.5 мс), а последнюю долю миллисекунды добираем
    /// коротким спином. При этом ожидание в любой момент прерывается событием отмены,
    /// так что остановка макроса срабатывает мгновенно.
    /// </summary>
    public sealed class PrecisionWaiter : IDisposable
    {
        const double SpinTailMs = 0.7;

        readonly IntPtr _timer;
        readonly WaitHandle[] _handles;   // [0] — таймер, [1] — отмена
        readonly bool _highRes;
        bool _disposed;

        public PrecisionWaiter(WaitHandle cancel)
        {
            if (cancel == null) throw new ArgumentNullException("cancel");

            try
            {
                _timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null,
                    Native.CREATE_WAITABLE_TIMER_MANUAL_RESET | Native.CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                    Native.TIMER_ALL_ACCESS);

                _highRes = _timer != IntPtr.Zero;

                if (_timer == IntPtr.Zero)
                {
                    // Windows до 10 1803 — обычный таймер, точность хуже
                    _timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null,
                        Native.CREATE_WAITABLE_TIMER_MANUAL_RESET, Native.TIMER_ALL_ACCESS);
                }
            }
            catch (Exception ex)
            {
                // Нет kernel32 — тесты логики под Mono на Linux. Ждём событием отмены.
                if (!(ex is DllNotFoundException) && !(ex is EntryPointNotFoundException)) throw;
                _timer = IntPtr.Zero;
                _highRes = false;
            }

            _handles = _timer != IntPtr.Zero
                ? new WaitHandle[] { new RawWaitHandle(_timer), cancel }
                : new WaitHandle[] { cancel, cancel };
        }

        /// <summary>Ждёт указанное число миллисекунд. Возвращает false, если ожидание прервано отменой.</summary>
        public bool Wait(double ms)
        {
            WaitHandle cancel = _handles[1];

            if (cancel.WaitOne(0)) return false;
            if (ms <= 0.05) return true;

            Stopwatch sw = Stopwatch.StartNew();
            double tail = _highRes ? SpinTailMs : 1.5;
            double sleepMs = ms - tail;

            if (sleepMs > 0.05)
            {
                bool slept = false;

                if (_timer != IntPtr.Zero)
                {
                    long due = (long)(-sleepMs * 10000.0);   // отрицательное = относительное, в 100 нс
                    if (Native.SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        if (WaitHandle.WaitAny(_handles) == 1) return false;
                        slept = true;
                    }
                }

                if (!slept)
                {
                    if (cancel.WaitOne((int)Math.Max(0, sleepMs))) return false;
                }
            }

            // Хвост добираем спином — это даёт точность заметно лучше миллисекунды.
            // Yield отдаёт ядро другому готовому потоку, если такой есть: иначе
            // зажатый спам с паузами по 5 мс заметно отъедал процессор у игры.
            while (sw.Elapsed.TotalMilliseconds < ms)
            {
                if (cancel.WaitOne(0)) return false;
                if (!Thread.Yield()) Thread.SpinWait(20);
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_timer != IntPtr.Zero) Native.CloseHandle(_timer);
        }
    }

    /// <summary>
    /// Пока движок включён, держим системный таймер на 1 мс — иначе планировщик Windows
    /// округляет любые ожидания до ~15.6 мс.
    /// </summary>
    public static class TimerResolution
    {
        static int _refCount;
        static readonly object Gate = new object();

        public static void Acquire()
        {
            lock (Gate)
            {
                if (_refCount++ == 0) { try { Native.timeBeginPeriod(1); } catch (DllNotFoundException) { } }
            }
        }

        public static void Release()
        {
            lock (Gate)
            {
                if (_refCount > 0 && --_refCount == 0) { try { Native.timeEndPeriod(1); } catch (DllNotFoundException) { } }
            }
        }
    }
}
