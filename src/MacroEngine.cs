using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Проигрыватель одного макроса. Живёт на собственном потоке и спит на событии,
    /// пока его не разбудит триггер. Поток создаётся один раз, а не на каждое нажатие.
    ///
    /// Ключевое: методы Trigger и Release вызываются ИЗ КОЛБЭКА ХУКА, поэтому они
    /// не имеют права ничего ждать. Всё, что они делают — взводят флаг и событие.
    /// </summary>
    public sealed class MacroRunner : IDisposable
    {
        readonly MacroDef _def;
        readonly Thread _thread;
        readonly AutoResetEvent _go = new AutoResetEvent(false);
        readonly ManualResetEvent _stop = new ManualResetEvent(false);
        readonly ManualResetEvent _shutdown = new ManualResetEvent(false);
        readonly WaitHandle[] _wake;
        readonly PrecisionWaiter _waiter;

        // Что макрос успел зажать — чтобы отпустить при любой остановке.
        // Без этого прерванный на середине макрос оставляет клавишу залипшей.
        readonly HashSet<int> _keysDown = new HashSet<int>();
        readonly HashSet<MouseBtn> _btnsDown = new HashSet<MouseBtn>();

        int _pendingRestart;
        volatile bool _running;
        volatile bool _held;
        volatile bool _toggle;
        volatile bool _disposing;

        public event Action<MacroRunner> StateChanged;

        public MacroDef Def { get { return _def; } }
        public bool IsRunning { get { return _running; } }

        /// <summary>
        /// Макрос из конфига, из которого раскрыт этот. У макроса на ряд клавиш
        /// проигрыватель свой на каждую клавишу, а интерфейс знает только исходный.
        /// </summary>
        public MacroDef Source { get; private set; }

        /// <summary>Есть ли в макросе слайд: такие играют строго по одному.</summary>
        public bool HasSlide { get; private set; }

        public MacroRunner(MacroDef def) : this(def, def) { }

        public MacroRunner(MacroDef def, MacroDef source)
        {
            _def = def;
            Source = source;
            HasSlide = def.Actions.Exists(x => x.Kind == ActionKind.Slide);
            _waiter = new PrecisionWaiter(_stop);
            _wake = new WaitHandle[] { _go, _shutdown };

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "Macro:" + def.Name;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
        }

        // ---------- вызывается из хука, должно возвращаться мгновенно ----------

        public void Trigger()
        {
            switch (_def.Repeat)
            {
                case RepeatMode.WhileHeld:
                    _held = true;
                    if (!_running) _go.Set();
                    break;

                case RepeatMode.Toggle:
                    if (_toggle)
                    {
                        _toggle = false;
                        _stop.Set();
                    }
                    else
                    {
                        _toggle = true;
                        if (!_running) _go.Set();
                    }
                    break;

                default:
                    if (_running)
                    {
                        switch (_def.OnRetrigger)
                        {
                            case Retrigger.Ignore:
                                return;
                            case Retrigger.Stop:
                                _stop.Set();
                                return;
                            default:
                                Interlocked.Exchange(ref _pendingRestart, 1);
                                _stop.Set();
                                return;
                        }
                    }
                    _go.Set();
                    break;
            }
        }

        public void Release()
        {
            if (_def.Repeat == RepeatMode.WhileHeld) _held = false;
        }

        public void StopNow()
        {
            _held = false;
            _toggle = false;
            Interlocked.Exchange(ref _pendingRestart, 0);
            _stop.Set();
        }

        // ---------- поток проигрывания ----------

        void Loop()
        {
            while (!_disposing)
            {
                if (WaitHandle.WaitAny(_wake) == 1) break;

                bool again;
                do
                {
                    if (_disposing) break;
                    if (_def.Repeat == RepeatMode.WhileHeld && !_held) break;
                    if (_def.Repeat == RepeatMode.Toggle && !_toggle) break;

                    _stop.Reset();
                    SetRunning(true);

                    try { Play(); }
                    catch { }

                    ReleaseAll();
                    SetRunning(false);   // порядок важен: сначала снимаем флаг, потом читаем restart

                    again = Interlocked.Exchange(ref _pendingRestart, 0) == 1 && !_disposing;
                }
                while (again);
            }

            ReleaseAll();
        }

        void Play()
        {
            switch (_def.Repeat)
            {
                case RepeatMode.Once:
                    PlayOnce();
                    break;

                case RepeatMode.Count:
                    for (int i = 0; i < _def.RepeatCount; i++)
                    {
                        if (!PlayOnce()) return;
                        if (i < _def.RepeatCount - 1 && _def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return;
                    }
                    break;

                case RepeatMode.WhileHeld:
                    while (_held && !_disposing)
                    {
                        if (!PlayOnce()) return;
                        if (!_held) break;
                        if (_def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return;
                    }
                    break;

                case RepeatMode.Toggle:
                    while (_toggle && !_disposing)
                    {
                        if (!PlayOnce()) return;
                        if (!_toggle) break;
                        if (_def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return;
                    }
                    break;
            }
        }

        /// <summary>Один проход по списку действий. false — прервано.</summary>
        bool PlayOnce()
        {
            var actions = _def.Actions;

            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];

                switch (a.Kind)
                {
                    case ActionKind.Delay:
                        if (!_waiter.Wait(a.Ms)) return false;
                        break;

                    case ActionKind.KeyDown:
                        InputSender.Key(a.Vk, true);
                        _keysDown.Add(a.Vk);
                        break;

                    case ActionKind.KeyUp:
                        InputSender.Key(a.Vk, false);
                        _keysDown.Remove(a.Vk);
                        break;

                    case ActionKind.KeyPress:
                        InputSender.Key(a.Vk, true);
                        _keysDown.Add(a.Vk);
                        if (a.Ms > 0 && !_waiter.Wait(a.Ms)) return false;
                        InputSender.Key(a.Vk, false);
                        _keysDown.Remove(a.Vk);
                        break;

                    case ActionKind.MouseDown:
                        InputSender.Mouse(a.Button, true);
                        _btnsDown.Add(a.Button);
                        break;

                    case ActionKind.MouseUp:
                        InputSender.Mouse(a.Button, false);
                        _btnsDown.Remove(a.Button);
                        break;

                    case ActionKind.MouseClick:
                        InputSender.Mouse(a.Button, true);
                        _btnsDown.Add(a.Button);
                        if (a.Ms > 0 && !_waiter.Wait(a.Ms)) return false;
                        InputSender.Mouse(a.Button, false);
                        _btnsDown.Remove(a.Button);
                        break;

                    case ActionKind.Slide:
                        if (!PlaySlide(a)) return false;
                        break;

                    case ActionKind.WaitLog:
                        if (!WaitLog(a)) return false;
                        break;
                }
            }

            return true;
        }

        /// <summary>
        /// Слайд: скилл, пауза на каст, бег с прыжком для отмены анимации.
        ///
        /// Раскрывается в примитивы прямо здесь, а не хранится семью шагами в
        /// макросе: скелет у всех слайдов одинаковый, а править тайминги хочется
        /// в одном месте. Механика пауз и учёт зажатых клавиш те же, что у
        /// обычных действий, поэтому обрыв посреди слайда ничего не оставляет
        /// зажатым.
        /// </summary>
        bool PlaySlide(MacroAction a)
        {
            if (a.Mode == SlideMode.Air) return PlayAirSlide(a);

            // Физически зажатый модификатор-триггер (Shift+3 и подобное) иначе
            // приклеится ко всему, что мы шлём дальше: в игру уйдёт Shift+W.
            //
            // ОСТОРОЖНО: отпуская чужую зажатую клавишу, мы переписываем состояние
            // клавиатуры в системе — она станет «отпущенной», пока пользователь
            // физически её держит, и следующие сочетания с ней перестанут
            // подходить под макросы. Включать только если Shift+W или Alt+W
            // реально мешают движению в игре.
            if (a.FreeMods)
            {
                if (!a.SkillCtrl && (Native.GetAsyncKeyState(0x11) & 0x8000) != 0) InputSender.Key(0x11, false);
                if (!a.SkillAlt && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0) InputSender.Key(0x12, false);
                if (!a.SkillShift && (Native.GetAsyncKeyState(0x10) & 0x8000) != 0) InputSender.Key(0x10, false);
            }

            // Разбег до скилла. Прыжок отменяет анимацию только у бегущего
            // персонажа: с места он подпрыгивает стоя и отмены не выходит.
            bool preRunning = false;
            if (a.PreRunMs > 0)
            {
                InputSender.Key(a.MoveVk, true);
                _keysDown.Add(a.MoveVk);
                preRunning = true;
                if (!_waiter.Wait(a.PreRunMs))
                {
                    InputSender.Key(a.MoveVk, false);
                    _keysDown.Remove(a.MoveVk);
                    return false;
                }
            }

            // Автоатака перед скиллом: в бою слайд с места срабатывает только так
            if (!TapAuto(a))
            {
                if (preRunning) { InputSender.Key(a.MoveVk, false); _keysDown.Remove(a.MoveVk); }
                return false;
            }

            if (!PressSkillTaps(a)) return false;

            // Пауза на каст: движение прервало бы его. При разбеге клавиша уже
            // зажата — тогда пауза просто течёт на бегу.
            if (a.CastMs > 0 && !_waiter.Wait(a.CastMs))
            {
                if (preRunning) { InputSender.Key(a.MoveVk, false); _keysDown.Remove(a.MoveVk); }
                return false;
            }

            if (!preRunning)
            {
                InputSender.Key(a.MoveVk, true);
                _keysDown.Add(a.MoveVk);
            }

            bool ok = true;
            if (a.Jump)
            {
                int before = Math.Max(0, Math.Min(a.JumpAfterMs, a.RunMs));
                if (before > 0) ok = _waiter.Wait(before);

                if (ok)
                {
                    InputSender.Key(0x20, true);
                    _keysDown.Add(0x20);
                    ok = MacroAction.JumpHoldMs <= 0 || _waiter.Wait(MacroAction.JumpHoldMs);
                    InputSender.Key(0x20, false);
                    _keysDown.Remove(0x20);
                }

                if (ok)
                {
                    int rest = a.RunMs - before - MacroAction.JumpHoldMs;
                    if (rest > 0) ok = _waiter.Wait(rest);
                }
            }
            else if (a.RunMs > 0)
            {
                ok = _waiter.Wait(a.RunMs);
            }

            // Клавишу движения отпускаем всегда, даже если макрос прервали:
            // иначе персонаж убежит.
            InputSender.Key(a.MoveVk, false);
            _keysDown.Remove(a.MoveVk);

            return ok;
        }

        /// <summary>
        /// Слайд в прыжке (jumpshot): прыжок, движение отпущено, после верхней
        /// точки автоатака и скилл, ждём приземления и только потом бежим.
        ///
        ///   [W↓ разбег]  Пробел ↓↑  [W↑]  →  пик  →  автоатака  →  скилл  →  приземление  →  [бег]
        ///
        /// Движение в воздухе срывает выстрел, поэтому W отпускается сразу после
        /// прыжка и возвращается только после приземления.
        /// </summary>
        bool PlayAirSlide(MacroAction a)
        {
            if (a.FreeMods)
            {
                if (!a.SkillCtrl && (Native.GetAsyncKeyState(0x11) & 0x8000) != 0) InputSender.Key(0x11, false);
                if (!a.SkillAlt && (Native.GetAsyncKeyState(0x12) & 0x8000) != 0) InputSender.Key(0x12, false);
                if (!a.SkillShift && (Native.GetAsyncKeyState(0x10) & 0x8000) != 0) InputSender.Key(0x10, false);
            }

            // Разбег: прыжок получается в сторону движения, а не на месте
            if (a.PreRunMs > 0)
            {
                InputSender.Key(a.MoveVk, true);
                _keysDown.Add(a.MoveVk);
                if (!_waiter.Wait(a.PreRunMs)) return false;
            }

            InputSender.Key(0x20, true);
            _keysDown.Add(0x20);
            bool ok = _waiter.Wait(MacroAction.JumpHoldMs);
            InputSender.Key(0x20, false);
            _keysDown.Remove(0x20);

            if (a.PreRunMs > 0)
            {
                InputSender.Key(a.MoveVk, false);
                _keysDown.Remove(a.MoveVk);
            }

            if (!ok || (a.ApexMs > 0 && !_waiter.Wait(a.ApexMs))) return false;

            if (!TapAuto(a)) return false;

            if (!PressSkillTaps(a)) return false;

            if (a.LandMs > 0 && !_waiter.Wait(a.LandMs)) return false;

            if (a.RunMs > 0)
            {
                InputSender.Key(a.MoveVk, true);
                _keysDown.Add(a.MoveVk);
                ok = _waiter.Wait(a.RunMs);
                InputSender.Key(a.MoveVk, false);
                _keysDown.Remove(a.MoveVk);
            }

            return ok;
        }

        /// <summary>
        /// Ждёт в Chat.log новую строку по шаблону. Не дождались — макрос
        /// останавливается: слайд без подтверждённой автоатаки не нужен.
        /// Каждый исход пишется в журнал задержек.
        /// </summary>
        bool WaitLog(MacroAction a)
        {
            var log = ChatLog.Current;
            if (log == null || string.IsNullOrEmpty(a.Pattern)) return false;

            System.Text.RegularExpressions.Regex rx;
            try { rx = new System.Text.RegularExpressions.Regex(a.Pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant); }
            catch { return false; }

            var sw = Stopwatch.StartNew();
            string hit = log.WaitFor(rx, Math.Max(1, a.Ms), _stop);
            double ms = sw.Elapsed.TotalMilliseconds;

            if (hit != null)
            {
                ChatLog.Journal(_def.Name + ": строка через " + ms.ToString("F0") + " мс — " + hit);
                return true;
            }

            ChatLog.Journal(_def.Name + ": " + (_stop.WaitOne(0) ? "прервано" : "не дождались за " + a.Ms + " мс") + ", слайда не было");
            return false;
        }

        /// <summary>
        /// Сколько держать свой модификатор до и после клавиши скилла.
        ///
        /// Игра опрашивает клавиатуру раз в кадр (~16 мс при 60 FPS). Если
        /// Alt↓ 1↓ 1↑ Alt↑ уходят подряд, к опросу Alt уже отпущен и игра видит
        /// голую «1» — так и было в бою. Чуть больше кадра — и сочетание доходит.
        /// </summary>
        public const int ModLeadMs = 20;

        /// <summary>
        /// Нажатие скилла слайда вместе с модификатором, на котором он висит в
        /// игре (Alt+1 и подобное).
        ///
        /// Зажимаем ТОЛЬКО то, чего ещё нет. Если пользователь держит Alt сам,
        /// лезть туда нельзя: наше отпускание перепишет состояние клавиатуры в
        /// системе, Alt станет «отпущенным» при физически зажатой клавише, и
        /// следующее Alt+1 не подойдёт уже ни под один макрос — клавиша
        /// провалится в игру голой. Отпускаем ровно то, что зажали сами.
        /// При обрыве зажатое отпустит ReleaseAll — всё лежит в _keysDown.
        /// </summary>
        bool PressSkill(MacroAction a)
        {
            bool ownCtrl = a.SkillCtrl && (Native.GetAsyncKeyState(0x11) & 0x8000) == 0;
            bool ownAlt = a.SkillAlt && (Native.GetAsyncKeyState(0x12) & 0x8000) == 0;
            bool ownShift = a.SkillShift && (Native.GetAsyncKeyState(0x10) & 0x8000) == 0;
            bool own = ownCtrl || ownAlt || ownShift;

            if (ownCtrl) { InputSender.Key(0x11, true); _keysDown.Add(0x11); }
            if (ownAlt) { InputSender.Key(0x12, true); _keysDown.Add(0x12); }
            if (ownShift) { InputSender.Key(0x10, true); _keysDown.Add(0x10); }

            if (own && !_waiter.Wait(ModLeadMs)) return false;

            InputSender.Key(a.Vk, true);
            _keysDown.Add(a.Vk);
            int hold = own ? Math.Max(a.Ms, ModLeadMs) : a.Ms;
            if (hold > 0 && !_waiter.Wait(hold)) return false;
            InputSender.Key(a.Vk, false);
            _keysDown.Remove(a.Vk);

            if (own && !_waiter.Wait(ModLeadMs)) return false;

            if (ownShift) { InputSender.Key(0x10, false); _keysDown.Remove(0x10); }
            if (ownAlt) { InputSender.Key(0x12, false); _keysDown.Remove(0x12); }
            if (ownCtrl) { InputSender.Key(0x11, false); _keysDown.Remove(0x11); }
            return true;
        }

        /// <summary>Скилл SkillTaps раз подряд, с паузой TapGapMs между нажатиями.</summary>
        bool PressSkillTaps(MacroAction a)
        {
            int taps = Math.Max(1, a.SkillTaps);
            for (int i = 0; i < taps; i++)
            {
                if (!PressSkill(a)) return false;
                if (i < taps - 1 && a.TapGapMs > 0 && !_waiter.Wait(a.TapGapMs)) return false;
            }
            return true;
        }

        /// <summary>Автоатака слайда и пауза до скилла. false — прервано.</summary>
        bool TapAuto(MacroAction a)
        {
            if (a.AutoVk == 0) return true;

            InputSender.Key(a.AutoVk, true);
            InputSender.Key(a.AutoVk, false);
            return a.AutoGapMs <= 0 || _waiter.Wait(a.AutoGapMs);
        }

        void ReleaseAll()
        {
            if (_keysDown.Count > 0)
            {
                foreach (int vk in _keysDown) InputSender.Key(vk, false);
                _keysDown.Clear();
            }

            if (_btnsDown.Count > 0)
            {
                foreach (var b in _btnsDown) InputSender.Mouse(b, false);
                _btnsDown.Clear();
            }
        }

        void SetRunning(bool value)
        {
            _running = value;
            var h = StateChanged;
            if (h != null) h(this);
        }

        public void Dispose()
        {
            if (_disposing) return;
            _disposing = true;

            _held = false;
            _toggle = false;
            _stop.Set();
            _shutdown.Set();

            _thread.Join(2000);

            ReleaseAll();
            _waiter.Dispose();
            _go.Close();
            _stop.Close();
            _shutdown.Close();
        }
    }

    /// <summary>Запись реальных нажатий с таймингами.</summary>
    public sealed class Recorder
    {
        readonly List<MacroAction> _raw = new List<MacroAction>();
        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly object _gate = new object();
        double _last;

        public int Count { get { lock (_gate) return _raw.Count; } }

        public void Add(int vk, bool down)
        {
            lock (_gate)
            {
                double now = _sw.Elapsed.TotalMilliseconds;

                if (_raw.Count > 0)
                {
                    int gap = (int)Math.Round(now - _last);
                    if (gap > 0) _raw.Add(new MacroAction { Kind = ActionKind.Delay, Ms = gap });
                }

                _raw.Add(new MacroAction
                {
                    Kind = down ? ActionKind.KeyDown : ActionKind.KeyUp,
                    Vk = vk,
                    Ms = 0
                });

                _last = now;
            }
        }

        /// <summary>Собирает итоговый список. Пары "зажать → пауза → отпустить" схлопывает в одно нажатие.</summary>
        public List<MacroAction> Build(bool collapse)
        {
            lock (_gate)
            {
                if (!collapse) return new List<MacroAction>(_raw);

                var result = new List<MacroAction>();
                int i = 0;

                while (i < _raw.Count)
                {
                    var a = _raw[i];

                    // KeyDown, [Delay], KeyUp того же vk  ->  KeyPress
                    if (a.Kind == ActionKind.KeyDown)
                    {
                        if (i + 1 < _raw.Count && _raw[i + 1].Kind == ActionKind.KeyUp && _raw[i + 1].Vk == a.Vk)
                        {
                            result.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = a.Vk, Ms = 10 });
                            i += 2;
                            continue;
                        }

                        if (i + 2 < _raw.Count
                            && _raw[i + 1].Kind == ActionKind.Delay
                            && _raw[i + 2].Kind == ActionKind.KeyUp
                            && _raw[i + 2].Vk == a.Vk)
                        {
                            result.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = a.Vk, Ms = _raw[i + 1].Ms });
                            i += 3;
                            continue;
                        }
                    }

                    result.Add(a.Clone());
                    i++;
                }

                // Ведущая и хвостовая паузы смысла не имеют
                while (result.Count > 0 && result[0].Kind == ActionKind.Delay) result.RemoveAt(0);
                while (result.Count > 0 && result[result.Count - 1].Kind == ActionKind.Delay) result.RemoveAt(result.Count - 1);

                return result;
            }
        }
    }

    /// <summary>
    /// Связывает хук, привязки и проигрыватели.
    /// </summary>
    public sealed class MacroEngine : IDisposable
    {
        sealed class Binding
        {
            public MacroDef Def;
            public MacroRunner Runner;
            public bool Held;
        }

        readonly KeyboardHook _hook;
        readonly object _gate = new object();

        Binding[][] _map = new Binding[256][];
        MacroRunner[] _runners = new MacroRunner[0];
        MacroRunner[] _slideRunners = new MacroRunner[0];
        MacroRunner _testRunner;

        // ---- клавиши-слои; всё ниже, кроме _isLayer, трогает только поток хука ----

        bool[] _isLayer = new bool[256];
        readonly bool[] _layerHeld = new bool[256];
        int _layersDown;

        // Модификаторы, проглоченные под слоем: игра их не видит, а движок помнит.
        // Иначе Alt, зажатый для Alt-скилла, приклеился бы к бегу и прыжку слайда.
        bool _vCtrl, _vAlt, _vShift;

        // Боковые кнопки, на которых висят слайды: под ними модификаторы тоже глотаются
        volatile bool _slideOnM4, _slideOnM5;

        volatile bool _enabled;
        volatile Recorder _recorder;
        volatile Action _recordStop;
        volatile Action<int> _capture;
        bool _started;
        bool _disposed;

        /// <summary>Поднимается, когда какой-то макрос начал или закончил играть.</summary>
        public event Action Changed;

        public bool HookOk { get { return _hook.IsRunning; } }
        public int HookError { get { return _hook.LastError; } }
        public bool IsRecording { get { return _recorder != null; } }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) StopAll();
                RaiseChanged();
            }
        }

        public MacroEngine()
        {
            _hook = new KeyboardHook(Filter);
        }

        public bool Start()
        {
            if (_started) return _hook.IsRunning;
            _started = true;
            TimerResolution.Acquire();
            return _hook.Start();
        }

        // ---------- конфигурация ----------

        public void Apply(AppConfig cfg)
        {
            var newMap = new Binding[256][];
            var newRunners = new List<MacroRunner>();
            var slideRunners = new List<MacroRunner>();
            var buckets = new Dictionary<int, List<Binding>>();
            var isLayer = new bool[256];
            bool slideOnM4 = false, slideOnM5 = false;

            foreach (var def in cfg.Macros)
            {
                if (!def.Enabled || !def.HasTrigger || def.Actions.Count == 0) continue;

                if (def.LayerVk != 0) isLayer[def.LayerVk & 0xFF] = true;

                bool slide = def.Actions.Exists(a => a.Kind == ActionKind.Slide);
                if (slide && def.Mouse4) slideOnM4 = true;
                if (slide && def.Mouse5) slideOnM5 = true;

                // Макрос на ряд клавиш — проигрыватель на каждую: спам по двум
                // кнопкам сразу должен идти параллельно, как у отдельных макросов.
                var keys = def.AllKeys;
                bool expand = keys.Count > 1 || def.Actions.Exists(a => a.Vk == MacroAction.TriggerKey);

                foreach (int key in keys)
                {
                    var play = expand ? def.ExpandFor(key) : def;

                    var runner = new MacroRunner(play, def);
                    runner.StateChanged += OnRunnerStateChanged;
                    newRunners.Add(runner);
                    if (runner.HasSlide) slideRunners.Add(runner);

                    int vk = key & 0xFF;
                    List<Binding> list;
                    if (!buckets.TryGetValue(vk, out list))
                    {
                        list = new List<Binding>();
                        buckets[vk] = list;
                    }
                    list.Add(new Binding { Def = play, Runner = runner });
                }
            }

            foreach (var kv in buckets) newMap[kv.Key] = kv.Value.ToArray();

            MacroRunner[] old;
            lock (_gate)
            {
                old = _runners;
                _runners = newRunners.ToArray();
                Volatile.Write(ref _slideRunners, slideRunners.ToArray());
                Volatile.Write(ref _isLayer, isLayer);
                _slideOnM4 = slideOnM4;
                _slideOnM5 = slideOnM5;
                Volatile.Write(ref _map, newMap);
            }

            foreach (var r in old)
            {
                r.StateChanged -= OnRunnerStateChanged;
                r.Dispose();
            }

            // Лог читаем, только пока он нужен хоть одному включённому макросу
            bool needLog = false;
            foreach (var def in cfg.Macros)
                if (def.Enabled && def.Actions.Exists(a => a.Kind == ActionKind.WaitLog)) needLog = true;
            ChatLog.Use(needLog ? cfg.ChatLogPath : null);

            _enabled = cfg.EngineEnabled;
            RaiseChanged();
        }

        public void StopAll()
        {
            MacroRunner[] snapshot;
            lock (_gate) snapshot = _runners;
            foreach (var r in snapshot) r.StopNow();

            var t = _testRunner;
            if (t != null) t.StopNow();
        }

        public bool AnyRunning
        {
            get
            {
                MacroRunner[] snapshot;
                lock (_gate) snapshot = _runners;
                foreach (var r in snapshot) if (r.IsRunning) return true;
                return false;
            }
        }

        /// <summary>Проиграть макрос один раз прямо сейчас — кнопка «Тест».</summary>
        public void PlayOnce(MacroDef def)
        {
            var copy = def.Clone();
            copy.Repeat = RepeatMode.Once;

            var old = _testRunner;
            _testRunner = new MacroRunner(copy);
            if (old != null) old.Dispose();

            _testRunner.Trigger();
        }

        // ---------- захват клавиши для назначения триггера ----------

        public void BeginCapture(Action<int> onCaptured) { _capture = onCaptured; }
        public void CancelCapture() { _capture = null; }

        // ---------- запись ----------

        /// <summary>Начать запись. onStopKey вызывается из потока хука при нажатии Esc.</summary>
        public Recorder BeginRecord(Action onStopKey)
        {
            var r = new Recorder();
            _recordStop = onStopKey;
            _recorder = r;
            return r;
        }

        public void EndRecord()
        {
            _recorder = null;
            _recordStop = null;
        }

        /// <summary>Играет ли сейчас макрос с таким определением.</summary>
        public bool IsMacroRunning(MacroDef def)
        {
            MacroRunner[] snapshot;
            lock (_gate) snapshot = _runners;

            foreach (var r in snapshot)
                if (r.Source == def && r.IsRunning) return true;

            return false;
        }

        // ---------- фильтр хука ----------

        bool Filter(int vk, bool down, bool injected)
        {
            // Свои же события пропускаем — иначе макрос затриггерит сам себя.
            if (injected) return false;

            var cap = _capture;
            if (cap != null)
            {
                if (down && !KeyNames.IsModifier(vk))
                {
                    _capture = null;
                    cap(vk == 0x1B ? 0 : vk);   // Esc = отмена
                }
                return true;   // пока ловим клавишу, глушим весь ввод
            }

            var rec = _recorder;
            if (rec != null)
            {
                if (down && vk == 0x1B)
                {
                    var stop = _recordStop;
                    _recorder = null;
                    _recordStop = null;
                    if (stop != null) stop();
                    return true;
                }

                rec.Add(vk, down);
                return false;
            }

            // Отпускание проглоченного модификатора глушим всегда, даже если движок
            // успели выключить: игра не видела нажатия, не должна видеть и отпускания.
            if (!down && SwallowedModUp(vk)) return true;

            if (!_enabled)
            {
                // Выключили посреди зажатого слоя — его отпускание мы уже не
                // поймаем, иначе после включения обычные макросы молчали бы.
                if (_layersDown != 0)
                {
                    Array.Clear(_layerHeld, 0, _layerHeld.Length);
                    _layersDown = 0;
                }
                return false;
            }

            // Клавиша-слой: в игру не уходит никогда, состояние помним сами
            int k = vk & 0xFF;
            if (Volatile.Read(ref _isLayer)[k])
            {
                if (down)
                {
                    if (!_layerHeld[k]) { _layerHeld[k] = true; _layersDown++; }
                }
                else if (_layerHeld[k])
                {
                    _layerHeld[k] = false;
                    _layersDown--;
                }
                return true;
            }

            // Ctrl / Alt / Shift, нажатые ПОСЛЕ слоя, глотаем: игра их не увидит,
            // а под макросы они подойдут. Нажатые до слоя уже ушли в систему —
            // их не трогаем, отпускать чужую зажатую клавишу нельзя (см. README).
            // Боковая кнопка мыши, на которой висит слайд, — такой же слой.
            if (down && KeyNames.IsModifier(vk) && InLayer() && SwallowModDown(vk)) return true;

            var map = Volatile.Read(ref _map);
            var list = map[k];
            if (list == null) return false;

            bool suppress = false;

            if (!down)
            {
                // На отпускании модификаторы не проверяем: пользователь мог отпустить
                // Ctrl раньше самой клавиши, и тогда макрос завис бы в состоянии «зажат».
                for (int i = 0; i < list.Length; i++)
                {
                    var b = list[i];
                    if (b.Held)
                    {
                        b.Held = false;
                        b.Runner.Release();
                    }
                    if (b.Def.Suppress) suppress = true;
                }
                return suppress;
            }

            bool ctrl = _vCtrl || (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = _vAlt || (Native.GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shift = _vShift || (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;

            // Боковые кнопки мыши читаются тем же способом, что и модификаторы
            // клавиатуры: мышиный хук ставить не нужно. VK_XBUTTON1 / VK_XBUTTON2.
            bool m4 = (Native.GetAsyncKeyState(0x05) & 0x8000) != 0;
            bool m5 = (Native.GetAsyncKeyState(0x06) & 0x8000) != 0;

            for (int i = 0; i < list.Length; i++)
            {
                var b = list[i];
                var d = b.Def;

                // Сравнение точное, а не «содержит»: поэтому на одной клавише спокойно
                // живут два макроса — обычный и с зажатой боковой кнопкой, и они
                // никогда не срабатывают вместе.
                if (d.Ctrl != ctrl || d.Alt != alt || d.Shift != shift) continue;
                if (d.Mouse4 != m4 || d.Mouse5 != m5) continue;

                // Слой — тоже точно: без слоя макрос молчит, пока зажат любой слой
                if (d.LayerVk == 0 ? _layersDown != 0 : !_layerHeld[d.LayerVk & 0xFF]) continue;

                if (!b.Held)
                {
                    b.Held = true;

                    // Слайды играют строго по одному: два сразу дрались бы за W и пробел
                    if (b.Runner.HasSlide)
                    {
                        var slides = Volatile.Read(ref _slideRunners);
                        for (int j = 0; j < slides.Length; j++)
                            if (slides[j] != b.Runner && slides[j].IsRunning) slides[j].StopNow();
                    }

                    b.Runner.Trigger();
                }
                // иначе это автоповтор от удержания — игнорируем

                if (d.Suppress) suppress = true;
            }

            return suppress;
        }

        /// <summary>Зажат ли слой: клавиша-слой или боковая кнопка, на которой висит слайд.</summary>
        bool InLayer()
        {
            if (_layersDown > 0) return true;
            if (_slideOnM4 && (Native.GetAsyncKeyState(0x05) & 0x8000) != 0) return true;
            if (_slideOnM5 && (Native.GetAsyncKeyState(0x06) & 0x8000) != 0) return true;
            return false;
        }

        /// <summary>Глотает нажатие модификатора под слоем. true — проглочено.</summary>
        bool SwallowModDown(int vk)
        {
            switch (vk)
            {
                case 0xA2: case 0xA3: case 0x11:
                    if (_vCtrl) return true;   // автоповтор уже проглоченного
                    if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0) return false;
                    _vCtrl = true;
                    return true;
                case 0xA4: case 0xA5: case 0x12:
                    if (_vAlt) return true;
                    if ((Native.GetAsyncKeyState(0x12) & 0x8000) != 0) return false;
                    _vAlt = true;
                    return true;
                case 0xA0: case 0xA1: case 0x10:
                    if (_vShift) return true;
                    if ((Native.GetAsyncKeyState(0x10) & 0x8000) != 0) return false;
                    _vShift = true;
                    return true;
            }
            return false;
        }

        /// <summary>Отпускание модификатора, нажатие которого проглотили. true — глушить.</summary>
        bool SwallowedModUp(int vk)
        {
            switch (vk)
            {
                case 0xA2: case 0xA3: case 0x11:
                    if (!_vCtrl) return false;
                    _vCtrl = false;
                    return true;
                case 0xA4: case 0xA5: case 0x12:
                    if (!_vAlt) return false;
                    _vAlt = false;
                    return true;
                case 0xA0: case 0xA1: case 0x10:
                    if (!_vShift) return false;
                    _vShift = false;
                    return true;
            }
            return false;
        }

        void OnRunnerStateChanged(MacroRunner r) { RaiseChanged(); }

        void RaiseChanged()
        {
            var h = Changed;
            if (h != null) h();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _hook.Dispose();

            MacroRunner[] snapshot;
            lock (_gate)
            {
                snapshot = _runners;
                _runners = new MacroRunner[0];
            }
            foreach (var r in snapshot) r.Dispose();

            if (_testRunner != null) _testRunner.Dispose();

            ChatLog.Use(null);
            if (_started) TimerResolution.Release();
        }
    }
}
