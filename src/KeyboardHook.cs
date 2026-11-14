using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Глобальный низкоуровневый хук клавиатуры на собственном потоке.
    ///
    /// Главное правило: колбэк обязан возвращаться немедленно. Windows отводит ему
    /// LowLevelHooksTimeout (по умолчанию 300 мс) и молча выкидывает хук из обработки,
    /// если тот не уложился. Именно поэтому в родном софте клавиатуры при проигрывании
    /// макроса переставали работать остальные клавиши: макрос играли прямо внутри колбэка,
    /// со Sleep между нажатиями, и весь ввод в системе стоял в очереди.
    ///
    /// Здесь колбэк делает только поиск в массиве и взвод события. Ни аллокаций,
    /// ни блокировок, ни ожиданий.
    /// </summary>
    public sealed class KeyboardHook : IDisposable
    {
        /// <summary>Возвращает true, если событие нужно проглотить.</summary>
        public delegate bool KeyFilter(int vk, bool isDown, bool injected);

        readonly KeyFilter _filter;
        readonly ManualResetEventSlim _ready = new ManualResetEventSlim(false);

        Native.HookProc _proc;    // ссылку надо держать: иначе GC соберёт делегат, и хук упадёт
        IntPtr _hook;
        Thread _thread;
        uint _threadId;
        bool _disposed;

        int _lastEventTick = Environment.TickCount;

        public bool IsRunning { get { return _hook != IntPtr.Zero; } }

        /// <summary>Environment.TickCount последнего события, которое видел хук (свои инъекции тоже).</summary>
        public int LastEventTick { get { return Volatile.Read(ref _lastEventTick); } }
        public int LastError { get; private set; }

        public KeyboardHook(KeyFilter filter)
        {
            if (filter == null) throw new ArgumentNullException("filter");
            _filter = filter;
        }

        public bool Start()
        {
            if (_thread != null) return IsRunning;

            _ready.Reset();
            Volatile.Write(ref _lastEventTick, Environment.TickCount);
            _thread = new Thread(ThreadProc);
            _thread.IsBackground = true;
            _thread.Name = "MacroKit.Hook";
            _thread.Priority = ThreadPriority.Highest;
            _thread.Start();

            _ready.Wait(4000);
            return IsRunning;
        }

        public void Stop()
        {
            if (_thread == null) return;

            if (_threadId != 0)
                Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

            if (!_thread.Join(2000))
            {
                // Поток висит на GetMessage — отпускаем, он фоновый и умрёт с процессом.
            }

            _thread = null;
            _threadId = 0;
        }

        void ThreadProc()
        {
            _threadId = Native.GetCurrentThreadId();
            _proc = HookCallback;

            // Свой хэндл держим в локальной переменной: при перестановке хука
            // сторожем старый поток может закончиться позже, чем стартует новый,
            // и не должен снять чужой хук.
            IntPtr h = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
            LastError = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            _hook = h;

            _ready.Set();
            if (h == IntPtr.Zero) return;

            Native.MSG msg;
            while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            Native.UnhookWindowsHookEx(h);
            if (_hook == h) _hook = IntPtr.Zero;
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == Native.HC_ACTION)
            {
                Volatile.Write(ref _lastEventTick, Environment.TickCount);
                try
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
                    bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;

                    if (down || up)
                    {
                        // Читаем поля структуры напрямую по смещениям — Marshal.PtrToStructure
                        // боксит и аллоцирует на каждое нажатие, а здесь это недопустимо.
                        int vk = Marshal.ReadInt32(lParam, Native.KBD_VKCODE);
                        uint flags = (uint)Marshal.ReadInt32(lParam, Native.KBD_FLAGS);
                        IntPtr extra = Marshal.ReadIntPtr(lParam, Native.KBD_EXTRAINFO);

                        bool ours = extra == InputSender.Signature;
                        bool injected = ours || (flags & Native.LLKHF_INJECTED) != 0;

                        if (_filter(vk, down, injected))
                            return (IntPtr)1;
                    }
                }
                catch
                {
                    // Исключение из колбэка хука роняет процесс — глушим.
                }
            }

            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _ready.Dispose();
        }
    }
}
