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
        public MacroDef Source { get; internal set; }

        /// <summary>Отпечаток определения (ConfigStore.Fingerprint): по нему движок переиспользует проигрыватель.</summary>
        public string Fingerprint { get; private set; }

        /// <summary>Писать каждый проигрыш в журнал macros-timing.txt (AppConfig.MacroJournal).</summary>
        public static volatile bool JournalOn;

        public const string JournalFile = "macros-timing.txt";

        // Почему проигрыш оборвали — для журнала. Первая причина побеждает.
        volatile string _stopReason;
        int _passes;

        // Слайд закончился, оставив клавишу движения зажатой под палец пользователя
        bool _moveLeftDown;

        // Модификаторы пальца, которые слайд отпустил, чтобы бег и прыжок ушли
        // в игру чистыми. В конце слайда они возвращаются, если палец их держит.
        readonly List<int> _restoreMods = new List<int>();

        // Сколько свой модификатор скилла держался ПОСЛЕ клавиши скилла (хвост
        // ModLeadMs). Этот хвост засчитывается в паузу на каст, иначе слайд с
        // Alt-скиллом бежал бы на 40 мс позже обычного.
        int _skillTail;

        // Серия: с какого умения искать следующее доступное. Между нажатиями не
        // сбрасывается: серию жмут частыми нажатиями, и каждое — отдельный
        // запуск; со сбросом она жала бы всегда первое доступное, а не по кругу.
        int _seriesPos;

        /// <summary>Есть ли в макросе слайд: такие играют строго по одному.</summary>
        public bool HasSlide { get; private set; }

        public MacroRunner(MacroDef def) : this(def, def, null) { }

        public MacroRunner(MacroDef def, MacroDef source) : this(def, source, null) { }

        public MacroRunner(MacroDef def, MacroDef source, string fingerprint)
        {
            _def = def;
            Source = source;
            Fingerprint = fingerprint;
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
                        Stop("выключен повторным нажатием");
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
                                Stop("остановлен повторным нажатием");
                                return;
                            default:
                                Interlocked.Exchange(ref _pendingRestart, 1);
                                Stop("перезапущен повторным нажатием");
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
            StopNow("остановлен");
        }

        /// <summary>Остановить с причиной — она попадёт в журнал.</summary>
        public void StopNow(string reason)
        {
            _held = false;
            _toggle = false;
            Interlocked.Exchange(ref _pendingRestart, 0);
            Stop(reason);
        }

        void Stop(string reason)
        {
            if (_running && _stopReason == null) _stopReason = reason;
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
                    _stopReason = null;
                    _passes = 0;

                    bool journal = JournalOn;
                    bool repeating = _def.Repeat == RepeatMode.WhileHeld || _def.Repeat == RepeatMode.Toggle;
                    var clock = Stopwatch.StartNew();
                    if (journal && !repeating) InputSender.BeginTrace();

                    SetRunning(true);

                    bool ok;
                    try { ok = Play(); }
                    catch (Exception ex) { ok = false; _stopReason = "ошибка: " + ex.Message; }

                    ReleaseAll();
                    if (journal) WriteJournal(ok, repeating, clock.Elapsed.TotalMilliseconds);
                    SetRunning(false);   // порядок важен: сначала снимаем флаг, потом читаем restart

                    again = Interlocked.Exchange(ref _pendingRestart, 0) == 1 && !_disposing;
                }
                while (again);
            }

            ReleaseAll();
        }

        /// <summary>Весь проигрыш по режиму повтора. false — оборван.</summary>
        bool Play()
        {
            switch (_def.Repeat)
            {
                case RepeatMode.Once:
                    return PlayOnce();

                case RepeatMode.Count:
                    for (int i = 0; i < _def.RepeatCount; i++)
                    {
                        if (!PlayOnce()) return false;
                        if (i < _def.RepeatCount - 1 && _def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return false;
                    }
                    return true;

                case RepeatMode.WhileHeld:
                    while (_held && !_disposing)
                    {
                        if (!PlayOnce()) return false;
                        if (!_held) break;
                        if (_def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return false;
                    }
                    return true;

                case RepeatMode.Toggle:
                    while (_toggle && !_disposing)
                    {
                        if (!PlayOnce()) return false;
                        if (!_toggle) break;
                        if (_def.GapMs > 0 && !_waiter.Wait(_def.GapMs)) return false;
                    }
                    return true;
            }
            return true;
        }

        /// <summary>
        /// Строка журнала. Разовые макросы — лента событий целиком: что ушло в
        /// игру и через сколько миллисекунд. Повторяющиеся («пока держу»,
        /// тумблер) — одна строка-итог при остановке: лента спама в 30 нажатий в
        /// секунду журнал бы просто утопила.
        /// </summary>
        void WriteJournal(bool ok, bool repeating, double totalMs)
        {
            string reason = _stopReason;
            var sb = new System.Text.StringBuilder();
            sb.Append(_def.Name).Append(" [").Append(_def.RepeatText).Append(']');

            if (repeating)
            {
                sb.Append(":  ").Append(_passes).Append(" прох. за ").Append(totalMs.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(" мс");
                if (_passes > 0)
                    sb.Append(", в среднем ").Append((totalMs / _passes).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(" мс");
                if (!ok || (reason != null && reason != "выключен повторным нажатием"))
                    sb.Append("  — ").Append(reason ?? "оборван");
            }
            else
            {
                if (!ok) sb.Append(" ПРЕРВАН (").Append(reason ?? "без причины").Append(')');
                sb.Append(":  ").Append(InputSender.EndTrace());
            }

            Journal.Append(JournalFile, sb.ToString());
        }

        /// <summary>Один проход по списку действий. false — прервано.</summary>
        bool PlayOnce()
        {
            _passes++;
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

                    case ActionKind.Series:
                        if (!PlaySeriesStep(a)) return false;
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
        ///
        /// С RespectHeldMove слайд на время проигрыша «владеет» клавишей движения
        /// (KeyState.Own): события её с клавиатуры не доходят до игры, а в конце
        /// она возвращается в то состояние, в котором её держит палец.
        /// </summary>
        bool PlaySlide(MacroAction a)
        {
            bool own = a.RespectHeldMove && a.MoveVk > 0;

            if (own) KeyState.Own(a.MoveVk);
            _moveLeftDown = false;
            _restoreMods.Clear();
            _skillTail = 0;
            InputSender.TraceNote("[слайд" + (a.Mode == SlideMode.Air ? " в прыжке" : "") + (own ? ", W рукой" : "") + "]");

            try
            {
                return a.Mode == SlideMode.Air ? PlayAirSlide(a, own) : PlayStandingSlide(a, own);
            }
            finally
            {
                if (own) ReturnMove(a);
                RestoreModifiers();
            }
        }

        static int JumpKey(MacroAction a)
        {
            return a.JumpVk > 0 ? a.JumpVk : 0x20;
        }

        bool PlayStandingSlide(MacroAction a, bool own)
        {
            // Физически зажатый модификатор-триггер (Shift+3 и подобное) иначе
            // приклеится ко всему, что мы шлём дальше: в игру уйдёт Shift+W.
            //
            // ОСТОРОЖНО: отпуская чужую зажатую клавишу, мы переписываем состояние
            // клавиатуры в системе — она станет «отпущенной», пока пользователь
            // физически её держит, и следующие сочетания с ней перестанут
            // подходить под макросы. Включать только если Shift+W или Alt+W
            // реально мешают движению в игре.
            if (a.FreeMods) FreeModifiers(a);

            int mv = a.MoveVk;
            bool userHeld = own && KeyState.IsPhysicallyDown(mv);

            // Разбег до скилла. Прыжок отменяет анимацию только у бегущего
            // персонажа: с места он подпрыгивает стоя и отмены не выходит.
            bool preRunning = false;
            if (a.PreRunMs > 0)
            {
                MoveDown(mv);
                preRunning = true;
                if (!_waiter.Wait(a.PreRunMs)) return false;
            }
            else if (userHeld)
            {
                // Пользователь бежит с зажатой W — слайд «с места» подобран под
                // стоящего персонажа, поэтому сначала останавливаемся.
                InputSender.Key(mv, false);
            }

            // Автоатака перед скиллом: в бою слайд с места срабатывает только так
            if (!TapAuto(a)) return false;

            if (!PressSkillTaps(a)) return false;

            // Модификатор пальца (Alt+2 — триггер, скилл тоже на Alt) иначе
            // приклеится к бегу и прыжку: в игру уйдут Alt+W и Alt+Пробел
            int spent = ReleaseModsBeforeMove(a);

            // Пауза на каст: движение прервало бы его. При разбеге клавиша уже
            // зажата — тогда пауза просто течёт на бегу. Хвост модификатора после
            // скилла уже прошёл — он засчитывается в паузу: от скилла до W столько
            // же, сколько у слайда без модификатора.
            int cast = Math.Max(0, a.CastMs - spent);
            if (cast > 0 && !_waiter.Wait(cast)) return false;

            if (!preRunning) MoveDown(mv);

            bool ok = true;
            if (a.Jump)
            {
                int before = Math.Max(0, Math.Min(a.JumpAfterMs, a.RunMs));
                if (before > 0) ok = _waiter.Wait(before);

                if (ok) ok = TapJump(a);

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
            // иначе персонаж убежит. Если её держит палец — оставляем.
            FinishMove(a, own);

            return ok;
        }

        /// <summary>
        /// Слайд в прыжке (jumpshot): прыжок, движение отпущено, после верхней
        /// точки автоатака и скилл, ждём приземления и только потом бежим.
        ///
        ///   [W↓ разбег]  Пробел ↓↑  [W↑]  →  пик  →  автоатака  →  скилл  →  приземление  →  [бег]
        ///
        /// Движение в воздухе срывает выстрел, поэтому W отпускается сразу после
        /// прыжка и возвращается только после приземления. Игра должна «увидеть
        /// остановку» в воздухе — поэтому с RespectHeldMove отпускается и W,
        /// которую держит палец: без этого джампшот на кайте не срабатывал.
        /// </summary>
        bool PlayAirSlide(MacroAction a, bool own)
        {
            if (a.FreeMods) FreeModifiers(a);

            int mv = a.MoveVk;
            bool userHeld = own && KeyState.IsPhysicallyDown(mv);

            // Разбег: прыжок получается в сторону движения, а не на месте.
            // Если пользователь уже бежит, разбег — это его бег.
            if (a.PreRunMs > 0)
            {
                if (!userHeld) MoveDown(mv);
                if (!_waiter.Wait(a.PreRunMs)) return false;
            }

            bool ok = TapJump(a);

            if (a.PreRunMs > 0 || userHeld)
            {
                InputSender.Key(mv, false);
                _keysDown.Remove(mv);
            }

            if (!ok || (a.ApexMs > 0 && !_waiter.Wait(a.ApexMs))) return false;

            if (!TapAuto(a)) return false;

            if (!PressSkillTaps(a)) return false;

            if (a.LandMs > 0 && !_waiter.Wait(a.LandMs)) return false;

            if (a.RunMs > 0)
            {
                MoveDown(mv);
                ok = _waiter.Wait(a.RunMs);
                FinishMove(a, own);
            }

            return ok;
        }

        /// <summary>
        /// Отпустить модификаторы, которые не нужны скиллу (Shift от триггера
        /// Shift+1 и т. п.). Отпущенные запоминаются и в конце слайда
        /// возвращаются, если палец их держит, — иначе система считала бы их
        /// отпущенными до следующего нажатия.
        /// </summary>
        void FreeModifiers(MacroAction a)
        {
            if (!a.SkillCtrl) ReleaseMod(0x11);
            if (!a.SkillAlt) ReleaseMod(0x12);
            if (!a.SkillShift) ReleaseMod(0x10);
        }

        bool ReleaseMod(int vk)
        {
            if (!KeyState.AsyncDown(vk)) return false;
            InputSender.Key(vk, false);
            if (!_restoreMods.Contains(vk)) _restoreMods.Add(vk);
            return true;
        }

        /// <summary>
        /// После скилла: модификаторы, ещё зажатые в системе (палец держит Alt от
        /// триггера Alt+2), отпустить до бега. Перед отпусканием — хвост
        /// ModLeadMs, чтобы игра успела увидеть скилл вместе с модификатором.
        /// Возвращает, сколько миллисекунд модификатор уже держался после скилла
        /// (его хвост или свой хвост PressSkill), — это засчитывается в каст.
        /// </summary>
        int ReleaseModsBeforeMove(MacroAction a)
        {
            int spent = _skillTail;
            if (!a.FreeMods) return spent;

            bool any = KeyState.AsyncDown(0x11) || KeyState.AsyncDown(0x12) || KeyState.AsyncDown(0x10);
            if (!any) return spent;

            if (spent < ModLeadMs)
            {
                if (!_waiter.Wait(ModLeadMs - spent)) return ModLeadMs;
                spent = ModLeadMs;
            }

            ReleaseMod(0x11);
            ReleaseMod(0x12);
            ReleaseMod(0x10);
            return spent;
        }

        /// <summary>
        /// Вернуть отпущенные модификаторы, которые палец всё ещё держит.
        ///
        /// Кроме Alt: возвращённый Alt, отпущенный пальцем без другой клавиши
        /// между ними, Windows понимает как одиночное нажатие Alt и может
        /// активировать меню окна — ввод в игру «залипает» до следующей клавиши.
        /// Макросам возврат не нужен: сочетания сравниваются по пальцу, а Alt для
        /// скилла слайд нажимает сам.
        /// </summary>
        void RestoreModifiers()
        {
            foreach (int vk in _restoreMods)
                if (vk != 0x12 && KeyState.ModPhysicallyDown(vk) && !KeyState.AsyncDown(vk))
                    InputSender.Key(vk, true);
            _restoreMods.Clear();
        }

        bool TapJump(MacroAction a)
        {
            int jv = JumpKey(a);
            InputSender.Key(jv, true);
            _keysDown.Add(jv);
            bool ok = MacroAction.JumpHoldMs <= 0 || _waiter.Wait(MacroAction.JumpHoldMs);
            InputSender.Key(jv, false);
            _keysDown.Remove(jv);
            return ok;
        }

        void MoveDown(int vk)
        {
            InputSender.Key(vk, true);
            _keysDown.Add(vk);
        }

        /// <summary>
        /// Конец бега слайда. Если слайд владеет клавишей и палец её держит,
        /// клавиша остаётся зажатой — персонаж бежит дальше без запинки.
        /// </summary>
        void FinishMove(MacroAction a, bool own)
        {
            int mv = a.MoveVk;
            if (own && KeyState.IsPhysicallyDown(mv))
            {
                _keysDown.Remove(mv);   // теперь это клавиша пользователя, ReleaseAll её не тронет
                _moveLeftDown = true;
                return;
            }

            InputSender.Key(mv, false);
            _keysDown.Remove(mv);
        }

        /// <summary>
        /// Слайд отдаёт клавишу движения. Последний владелец приводит её в
        /// соответствие с пальцем: держит — нажать, не держит — отпущена.
        /// Срабатывает и при обрыве слайда.
        /// </summary>
        void ReturnMove(MacroAction a)
        {
            int mv = a.MoveVk;

            if (_keysDown.Contains(mv))
            {
                InputSender.Key(mv, false);
                _keysDown.Remove(mv);
            }

            bool free = KeyState.Disown(mv);
            if (free && !_moveLeftDown && KeyState.IsPhysicallyDown(mv))
                InputSender.Key(mv, true);
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

            var rx = ChatLog.Compile(a.Pattern);
            if (rx == null)
            {
                ChatLog.Journal(_def.Name + ": шаблон «" + a.Pattern + "» не разбирается, слайда не было");
                return false;
            }

            var sw = Stopwatch.StartNew();
            string hit = log.WaitFor(rx, Math.Max(1, a.Ms), _stop);
            double ms = sw.Elapsed.TotalMilliseconds;

            if (hit != null)
            {
                ChatLog.Journal(_def.Name + ": строка через " + ms.ToString("F0") + " мс — " + hit);
                return true;
            }

            ChatLog.Journal(_def.Name + ": " + (_stop.WaitOne(0) ? "прервано" : "не дождались за " + a.Ms + " мс") + ", слайда не было");
            if (_stopReason == null) _stopReason = "нет строки в логе за " + a.Ms + " мс";
            return false;
        }

        /// <summary>Пауза прохода, когда в серии всё в кд: иначе «пока держу» крутился бы вхолостую.</summary>
        public const int SeriesIdleMs = 15;

        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex> ConfirmPatterns =
            new System.Collections.Concurrent.ConcurrentDictionary<string, System.Text.RegularExpressions.Regex>();

        /// <summary>Строка «умение прошло» или «не прошло» — чем раньше придёт любая.</summary>
        static System.Text.RegularExpressions.Regex ConfirmPattern(string name)
        {
            return ConfirmPatterns.GetOrAdd(name, n => new System.Text.RegularExpressions.Regex(
                "(" + SkillTimers.PatternFor(n) + ")|(" + SkillTimers.Failure + ")",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }

        /// <summary>Ключ учёта кд. У умения без названия — свой на каждый шаг макроса.</summary>
        string SeriesKey(MacroAction a, int i)
        {
            var s = a.Series[i];
            string k = SkillTimers.KeyOf(s.Name);
            return k.Length > 0 ? k : "#" + _def.Id + ":" + _def.Actions.IndexOf(a) + ":" + i;
        }

        /// <summary>
        /// Можно ли сейчас жать умение серии. Кд считается от строки в
        /// Chat.log (или от нажатия, если лог выключен). Продолжение (Chain)
        /// ещё и живёт только ChainWindowMs после предыдущего умения и один
        /// раз на каждое его применение.
        /// </summary>
        bool SeriesReady(MacroAction a, int i, long now)
        {
            var s = a.Series[i];
            if (s.Vk == 0) return false;

            long last = SkillTimers.LastSeen(SeriesKey(a, i));
            if (last >= 0 && now - last < s.CooldownMs) return false;

            if (s.Chain && i > 0)
            {
                long parent = SkillTimers.LastSeen(SeriesKey(a, i - 1));
                if (parent < 0 || now - parent > a.ChainWindowMs) return false;
                if (last >= parent) return false;   // после этого применения уже было
            }
            return true;
        }

        /// <summary>
        /// Один проход серии: следующее по кругу умение не в кд. Прошло (есть
        /// строка в логе) — дальше ищем со следующего; не прошло (стан, далеко,
        /// не дождались) — на следующем проходе пробуем его же. Всё в кд —
        /// ничего не жмём.
        /// </summary>
        bool PlaySeriesStep(MacroAction a)
        {
            int n = a.Series.Count;
            long now = SkillTimers.Now;

            int pick = -1;
            for (int k = 0; k < n; k++)
            {
                int i = (_seriesPos + k) % n;
                if (SeriesReady(a, i, now)) { pick = i; break; }
            }
            if (pick < 0) return _waiter.Wait(SeriesIdleMs);

            var s = a.Series[pick];
            string key = SeriesKey(a, pick);
            var log = a.SeriesUseLog && s.Name.Trim().Length > 0 ? ChatLog.Current : null;

            var press = new MacroAction { Vk = s.Vk, SkillCtrl = s.Ctrl, SkillAlt = s.Alt, SkillShift = s.Shift, Ms = a.Ms };

            if (log == null)
            {
                long at = SkillTimers.Now;
                if (!PressSkill(press)) return false;
                SkillTimers.Mark(key, at);
                _seriesPos = (pick + 1) % n;
                SeriesJournal(s, "нажато (без лога)");
                return s.WaitMs <= 0 || _waiter.Wait(s.WaitMs);
            }

            using (var expect = log.Expect(ConfirmPattern(s.Name)))
            {
                var clock = Stopwatch.StartNew();
                if (!PressSkill(press)) return false;

                string hit = log.Wait(expect, Math.Max(1, s.WaitMs), _stop);
                if (_stop.WaitOne(0)) return false;

                double ms = clock.Elapsed.TotalMilliseconds;
                if (hit != null && !SkillTimers.Failure.IsMatch(hit))
                {
                    _seriesPos = (pick + 1) % n;
                    SeriesJournal(s, "прошло через " + ms.ToString("F0") + " мс");
                }
                else
                {
                    _seriesPos = pick;
                    SeriesJournal(s, hit != null ? "не прошло: " + hit : "нет строки за " + s.WaitMs + " мс, повторю");
                }
            }
            return true;
        }

        void SeriesJournal(SeriesSkill s, string what)
        {
            if (!JournalOn) return;
            ChatLog.Journal(_def.Name + ": серия — " + (s.Name.Length > 0 ? s.Name : s.KeyText) + " (" + s.KeyText + "): " + what);
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
            bool ownCtrl = a.SkillCtrl && !KeyState.AsyncDown(0x11);
            bool ownAlt = a.SkillAlt && !KeyState.AsyncDown(0x12);
            bool ownShift = a.SkillShift && !KeyState.AsyncDown(0x10);
            bool own = ownCtrl || ownAlt || ownShift;

            if (ownCtrl) { InputSender.Key(0x11, true); _keysDown.Add(0x11); }
            if (ownAlt) { InputSender.Key(0x12, true); _keysDown.Add(0x12); }
            if (ownShift) { InputSender.Key(0x10, true); _keysDown.Add(0x10); }

            if (own && !_waiter.Wait(ModLeadMs)) return false;

            InputSender.Key(a.Vk, true);
            _keysDown.Add(a.Vk);
            // Саму клавишу держим столько же, сколько без модификатора: игра
            // принимает нажатие очередью сообщений (удержание 0 работает), а
            // модификатор она смотрит, когда разбирает нажатие, — для этого и
            // хвост ModLeadMs ниже.
            if (a.Ms > 0 && !_waiter.Wait(a.Ms)) return false;
            InputSender.Key(a.Vk, false);
            _keysDown.Remove(a.Vk);

            if (own && !_waiter.Wait(ModLeadMs)) return false;
            _skillTail = own ? ModLeadMs : 0;

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

            // Свой модификатор скилла зажимается за ModLeadMs до клавиши — это
            // время засчитывается в паузу «до скилла», чтобы от автоатаки до
            // скилла было ровно AutoGapMs, как у скилла без модификатора
            int gap = a.AutoGapMs - (NeedsOwnMod(a) ? ModLeadMs : 0);
            return gap <= 0 || _waiter.Wait(gap);
        }

        static bool NeedsOwnMod(MacroAction a)
        {
            return (a.SkillCtrl && !KeyState.AsyncDown(0x11)) ||
                   (a.SkillAlt && !KeyState.AsyncDown(0x12)) ||
                   (a.SkillShift && !KeyState.AsyncDown(0x10));
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
            Stop("макросы пересобраны (правка или смена профиля)");
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

        // ---- настройки из AppConfig, читает поток хука ----
        volatile int _toggleVk;
        volatile bool _chatPauseOn;

        bool _toggleHeld;             // только поток хука: автоповтор клавиши-выключателя
        volatile bool _chatPaused;    // Enter открыл чат — макросы молчат
        volatile bool _gameActive = true;

        // ---- сторож хука ----
        int _probeSentAt;
        bool _probePending;
        int _lastProbe;

        /// <summary>Сколько раз сторож переставлял пропавший хук.</summary>
        public int HookRestarts { get; private set; }

        volatile Recorder _recorder;
        volatile Action _recordStop;
        volatile Action<int> _capture;
        bool _started;
        bool _disposed;

        /// <summary>Поднимается, когда какой-то макрос начал или закончил играть.</summary>
        public event Action Changed;

        public bool HookOk { get { return _hook.IsRunning; } }

        /// <summary>Текущие проигрыватели — для тестов.</summary>
        internal MacroRunner[] Runners { get { lock (_gate) return _runners; } }
        public int HookError { get { return _hook.LastError; } }
        public bool IsRecording { get { return _recorder != null; } }

        /// <summary>Макросы на паузе, потому что Enter открыл чат.</summary>
        public bool ChatPaused { get { return _chatPauseOn && _chatPaused; } }

        /// <summary>На переднем плане окно игры (или ограничение по окну выключено).</summary>
        public bool GameActive { get { return _gameActive; } }

        /// <summary>
        /// Сообщить, активно ли окно игры. Вызывается интерфейсом по таймеру.
        /// При уходе из игры всё играющее останавливается: слайд не должен
        /// дожимать W в браузере.
        /// </summary>
        public void SetGameActive(bool active)
        {
            if (_gameActive == active) return;
            _gameActive = active;
            if (!active)
            {
                _chatPaused = false;
                StopAll("ушёл из окна игры");
            }
            RaiseChanged();
        }

        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!value) StopAll("движок выключен");
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

        /// <summary>
        /// Пересобрать привязки под конфиг. Вызывается на каждую правку в окне,
        /// поэтому проигрыватели не пересоздаются целиком: у макроса, который не
        /// менялся (тот же отпечаток), остаётся прежний поток. Раньше любая галочка
        /// пересоздавала десятки потоков и обрывала всё, что играло.
        /// </summary>
        public void Apply(AppConfig cfg)
        {
            var newMap = new Binding[256][];
            var newRunners = new List<MacroRunner>();
            var slideRunners = new List<MacroRunner>();
            var buckets = new Dictionary<int, List<Binding>>();
            var isLayer = new bool[256];
            bool slideOnM4 = false, slideOnM5 = false;

            MacroRunner[] old;
            Binding[][] oldMap;
            lock (_gate)
            {
                old = _runners;
                oldMap = _map;
            }

            // Свободные старые проигрыватели по отпечаткам
            var spare = new Dictionary<string, List<MacroRunner>>();
            foreach (var r in old)
            {
                if (r.Fingerprint == null) continue;
                List<MacroRunner> list;
                if (!spare.TryGetValue(r.Fingerprint, out list)) { list = new List<MacroRunner>(); spare[r.Fingerprint] = list; }
                list.Add(r);
            }

            // Какие проигрыватели сейчас «зажаты» — чтобы автоповтор после
            // пересборки не принялся за перезапуск
            var heldRunners = new HashSet<MacroRunner>();
            foreach (var bucket in oldMap)
                if (bucket != null)
                    foreach (var b in bucket)
                        if (b.Held) heldRunners.Add(b.Runner);

            foreach (var def in cfg.Macros)
            {
                if (!def.Enabled || !def.HasTrigger || def.Actions.Count == 0) continue;

                if (def.LayerVk != 0) isLayer[def.LayerVk & 0xFF] = true;

                bool slide = def.Actions.Exists(a => a.Kind == ActionKind.Slide);
                if (slide && def.Mouse4) slideOnM4 = true;
                if (slide && def.Mouse5) slideOnM5 = true;

                // Макрос на ряд клавиш — проигрыватель на каждую: спам по двум
                // кнопкам сразу должен идти параллельно, как у отдельных макросов.
                // Играет всегда копия: интерфейс правит определение на месте, и
                // проигрыватель не должен видеть полуизменённый макрос.
                foreach (int key in def.AllKeys)
                {
                    var play = def.ExpandFor(key);
                    string fp = ConfigStore.Fingerprint(play);

                    MacroRunner runner = null;
                    List<MacroRunner> candidates;
                    if (spare.TryGetValue(fp, out candidates) && candidates.Count > 0)
                    {
                        runner = candidates[candidates.Count - 1];
                        candidates.RemoveAt(candidates.Count - 1);
                        runner.Source = def;
                    }
                    else
                    {
                        runner = new MacroRunner(play, def, fp);
                        runner.StateChanged += OnRunnerStateChanged;
                    }

                    newRunners.Add(runner);
                    if (runner.HasSlide) slideRunners.Add(runner);

                    int vk = key & 0xFF;
                    List<Binding> list;
                    if (!buckets.TryGetValue(vk, out list))
                    {
                        list = new List<Binding>();
                        buckets[vk] = list;
                    }
                    list.Add(new Binding { Def = runner.Def, Runner = runner, Held = heldRunners.Contains(runner) });
                }
            }

            foreach (var kv in buckets) newMap[kv.Key] = kv.Value.ToArray();

            lock (_gate)
            {
                _runners = newRunners.ToArray();
                Volatile.Write(ref _slideRunners, slideRunners.ToArray());
                Volatile.Write(ref _isLayer, isLayer);
                _slideOnM4 = slideOnM4;
                _slideOnM5 = slideOnM5;
                Volatile.Write(ref _map, newMap);
            }

            // Всё, что не пригодилось, — в утиль; Dispose отпускает зажатое
            foreach (var list in spare.Values)
                foreach (var r in list)
                {
                    r.StateChanged -= OnRunnerStateChanged;
                    r.Dispose();
                }
            foreach (var r in old)
            {
                if (r.Fingerprint != null) continue;
                r.StateChanged -= OnRunnerStateChanged;
                r.Dispose();
            }

            // Лог читаем, только пока он нужен хоть одному включённому макросу
            bool needLog = false;
            var skillNames = new List<string>();
            foreach (var def in cfg.Macros)
            {
                if (!def.Enabled) continue;
                foreach (var a in def.Actions)
                {
                    if (a.Kind == ActionKind.WaitLog) needLog = true;
                    if (a.Kind == ActionKind.Series && a.SeriesUseLog)
                    {
                        needLog = true;
                        foreach (var s in a.Series) skillNames.Add(s.Name);
                    }
                }
            }
            SkillTimers.WatchNames(skillNames);
            ChatLog.Use(needLog ? cfg.ChatLogPath : null);

            _toggleVk = cfg.ToggleVk;
            _chatPauseOn = cfg.ChatPause;
            if (!cfg.ChatPause) _chatPaused = false;
            MacroRunner.JournalOn = cfg.MacroJournal;

            _enabled = cfg.EngineEnabled;
            RaiseChanged();
        }

        // ---------- сторож хука ----------

        /// <summary>
        /// Жив ли хук. Windows молча снимает низкоуровневый хук, если колбэк хоть
        /// раз не уложился в LowLevelHooksTimeout (подвисание системы, загрузка
        /// игры), — и макросы перестают работать до перезапуска программы.
        ///
        /// Вызывается интерфейсом раз в пару секунд. Если система видела ввод,
        /// а хук давно молчит, шлём «зонд» — отпускание неназначенной клавиши
        /// 0xE8 со своей подписью — и ждём, что хук его увидит. Не увидел за
        /// секунду — хук переставляется. Возвращает true, если хук пришлось
        /// переставить.
        /// </summary>
        public bool CheckHook()
        {
            if (!_started) return false;

            int now = Environment.TickCount;
            int lastHook = _hook.LastEventTick;

            if (_probePending)
            {
                if (unchecked(lastHook - _probeSentAt) >= 0)
                {
                    _probePending = false;
                    return false;
                }
                if (unchecked(now - _probeSentAt) < 1000) return false;

                _probePending = false;
                _hook.Stop();
                KeyState.ResetPhysical();
                _hook.Start();
                HookRestarts++;
                RaiseChanged();
                return true;
            }

            // Хук недавно что-то видел — жив
            if (unchecked(now - lastHook) < 3000) return false;

            // Ввода в системе после последнего события хука не было — проверять нечего
            int lastInput = Native.LastInputTick();
            if (lastInput == 0 || unchecked(lastInput - lastHook) <= 0) return false;
            if (unchecked(now - _lastProbe) < 15000) return false;

            _lastProbe = now;
            _probeSentAt = now;
            _probePending = true;
            InputSender.Key(0xE8, false);
            return false;
        }

        public void StopAll()
        {
            StopAll("остановлены все");
        }

        public void StopAll(string reason)
        {
            MacroRunner[] snapshot;
            lock (_gate) snapshot = _runners;
            foreach (var r in snapshot) r.StopNow(reason);

            var t = _testRunner;
            if (t != null) t.StopNow(reason);
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

        internal bool Filter(int vk, bool down, bool injected)
        {
            // Физическое состояние ведём сами: GetAsyncKeyState смешивает палец
            // пользователя с нашими инъекциями (см. KeyState)
            if (!injected) KeyState.OnPhysical(vk, down);

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

            // Клавишей движения сейчас управляет слайд: события с клавиатуры, в том
            // числе автоповтор, в игру не пускаем. Палец запомнен в KeyState, и
            // слайд в конце вернёт клавишу в его состояние.
            if (KeyState.IsOwned(vk)) return true;

            // Отпускание проглоченного модификатора глушим всегда, даже если движок
            // успели выключить: игра не видела нажатия, не должна видеть и отпускания.
            if (!down && SwallowedModUp(vk)) return true;

            // Клавиша-выключатель движка работает и при выключенном движке
            int toggle = _toggleVk;
            if (toggle != 0 && vk == toggle)
            {
                if (down && !_toggleHeld)
                {
                    _toggleHeld = true;
                    _enabled = !_enabled;
                    if (!_enabled) StopAll("движок выключен клавишей");
                    RaiseChanged();
                }
                else if (!down)
                {
                    _toggleHeld = false;
                }
                return true;
            }

            if (!_enabled)
            {
                // Выключили посреди зажатого слоя — его отпускание мы уже не
                // поймаем, иначе после включения обычные макросы молчали бы.
                if (_layersDown != 0)
                {
                    Array.Clear(_layerHeld, 0, _layerHeld.Length);
                    _layersDown = 0;
                }
                if (!down) ReleaseBindings(vk);
                return false;
            }

            // Окно игры не активно — ни слоёв, ни макросов
            if (!_gameActive)
            {
                if (!down) ReleaseBindings(vk);
                return false;
            }

            // Режим чата: Enter открывает и закрывает чат, Esc закрывает.
            // Сами клавиши до игры доходят — мы только запоминаем состояние.
            if (_chatPauseOn)
            {
                if (down && vk == 0x0D)
                {
                    _chatPaused = !_chatPaused;
                    if (_chatPaused) StopAll("пауза на чат (Enter)");
                    RaiseChanged();
                    return false;
                }
                if (down && vk == 0x1B && _chatPaused)
                {
                    _chatPaused = false;
                    RaiseChanged();
                    return false;
                }
                if (_chatPaused)
                {
                    if (!down) ReleaseBindings(vk);
                    return false;
                }
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

            if (!down) return ReleaseBindings(vk);

            // Модификаторы — по пальцу, а не по состоянию системы. Слайд отпускает
            // Shift триггера Shift+1, чтобы бег ушёл чистым; система после этого
            // считает Shift отпущенным, и раньше следующее нажатие при всё ещё
            // зажатом Shift не подходило под макрос — в игру уходила голая «1».
            bool ctrl = _vCtrl || KeyState.ModPhysicallyDown(0x11);
            bool alt = _vAlt || KeyState.ModPhysicallyDown(0x12);
            bool shift = _vShift || KeyState.ModPhysicallyDown(0x10);

            // Боковые кнопки мыши читаются тем же способом, что и модификаторы
            // клавиатуры: мышиный хук ставить не нужно. VK_XBUTTON1 / VK_XBUTTON2.
            bool m4 = KeyState.AsyncDown(0x05);
            bool m5 = KeyState.AsyncDown(0x06);

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
                            if (slides[j] != b.Runner && slides[j].IsRunning) slides[j].StopNow("начался другой слайд: " + d.Name);
                    }

                    b.Runner.Trigger();
                }
                // иначе это автоповтор от удержания — игнорируем

                if (d.Suppress) suppress = true;
            }

            return suppress;
        }

        /// <summary>
        /// Отпускание клавиши: снять пометку «зажата» со всех её привязок.
        /// true — отпускание надо проглотить (триггер глушит исходную функцию).
        ///
        /// Модификаторы не проверяем: пользователь мог отпустить Ctrl раньше самой
        /// клавиши, и тогда макрос завис бы в состоянии «зажат». Release зовём
        /// всем, а не только помеченным Held: после пересборки привязок пометка
        /// могла потеряться, и «пока держу» крутился бы вечно. Зовётся и на паузе
        /// (чат, чужое окно, движок выключен) — иначе первое нажатие после паузы
        /// сочлось бы автоповтором и пропало.
        /// </summary>
        bool ReleaseBindings(int vk)
        {
            var list = Volatile.Read(ref _map)[vk & 0xFF];
            if (list == null) return false;

            bool suppress = false;
            for (int i = 0; i < list.Length; i++)
            {
                var b = list[i];
                b.Held = false;
                b.Runner.Release();
                if (b.Def.Suppress) suppress = true;
            }
            return suppress;
        }

        /// <summary>Зажат ли слой: клавиша-слой или боковая кнопка, на которой висит слайд.</summary>
        bool InLayer()
        {
            if (_layersDown > 0) return true;
            if (_slideOnM4 && KeyState.AsyncDown(0x05)) return true;
            if (_slideOnM5 && KeyState.AsyncDown(0x06)) return true;
            return false;
        }

        /// <summary>Глотает нажатие модификатора под слоем. true — проглочено.</summary>
        bool SwallowModDown(int vk)
        {
            switch (vk)
            {
                case 0xA2: case 0xA3: case 0x11:
                    if (_vCtrl) return true;   // автоповтор уже проглоченного
                    if (KeyState.AsyncDown(0x11)) return false;
                    _vCtrl = true;
                    return true;
                case 0xA4: case 0xA5: case 0x12:
                    if (_vAlt) return true;
                    if (KeyState.AsyncDown(0x12)) return false;
                    _vAlt = true;
                    return true;
                case 0xA0: case 0xA1: case 0x10:
                    if (_vShift) return true;
                    if (KeyState.AsyncDown(0x10)) return false;
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
