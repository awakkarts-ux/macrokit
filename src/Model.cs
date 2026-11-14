using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace MacroKit
{
    public enum ActionKind
    {
        KeyPress,   // нажать и отпустить
        KeyDown,    // только нажать
        KeyUp,      // только отпустить
        Delay,      // пауза
        MouseClick,
        MouseDown,
        MouseUp,
        Slide,      // скилл + отмена анимации бегом с прыжком, см. MacroAction
        WaitLog     // ждать строку в Chat.log игры, см. ChatLog; Ms — сколько ждать максимум
    }

    public enum MouseBtn { Left, Right, Middle, X1, X2 }

    /// <summary>
    /// Два вида слайда.
    ///
    /// Standing — «с места»: скилл, затем бег с прыжком срывает анимацию.
    /// Air — «в прыжке» (jumpshot): сначала прыжок, движение отпущено, после
    /// верхней точки автоатака и скилл; анимация натяжения не играется.
    /// Нужен в основном кастуемым скиллам.
    /// </summary>
    public enum SlideMode { Standing, Air }

    public enum RepeatMode
    {
        Once,        // один раз за нажатие
        WhileHeld,   // пока держишь клавишу
        Toggle,      // нажал — пошло, нажал ещё раз — встало
        Count        // ровно N повторов
    }

    public enum Retrigger
    {
        Restart,   // начать заново
        Ignore,    // не трогать текущий проигрыш
        Stop       // остановить
    }

    public sealed class MacroAction
    {
        public ActionKind Kind = ActionKind.KeyPress;
        public int Vk;
        public MouseBtn Button = MouseBtn.Left;
        public int Ms = 30;   // Delay — длительность паузы; KeyPress/MouseClick/Slide — удержание клавиши

        /// <summary>WaitLog: регулярное выражение для строки лога. Не дождались за Ms — макрос останавливается.</summary>
        public string Pattern = "";

        /// <summary>
        /// Vk-заглушка «клавиша, которой запустили макрос». Макрос с несколькими
        /// клавишами-триггерами раскрывается движком в копию на каждую клавишу,
        /// и заглушка заменяется настоящей. Так один макрос «спам» или «слайд»
        /// обслуживает весь ряд скиллов. 0xFF Windows не использует.
        /// </summary>
        public const int TriggerKey = 0xFF;

        // ---- параметры Slide ----
        //
        // Слайд — это всегда один и тот же скелет: нажать скилл, переждать каст
        // (движение его прервёт), побежать, прыгнуть, добежать, отпустить.
        // Держать его семью отдельными шагами накладно: у каждого слайдового
        // скилла он повторяется, и правка тайминга превращается в правку везде.

        /// <summary>Модификатор, на котором скилл висит в игре: Alt+1 и подобное.
        /// Позволяет не заводить дубль скилла на свободный слот панели.</summary>
        public bool SkillCtrl, SkillAlt, SkillShift;

        /// <summary>Пауза после скилла до начала движения. Для инстантов ~20 мс,
        /// для кастуемых — время каста, иначе движение его прервёт.</summary>
        public int CastMs = 20;

        /// <summary>Клавиша движения и сколько её держать суммарно.</summary>
        public int MoveVk = 0x57;   // W
        public int RunMs = 420;

        /// <summary>
        /// Разбег ДО скилла. Прыжок отменяет анимацию только если персонаж уже
        /// бежит: с места он подпрыгивает стоя, и отмены не происходит. Этот
        /// разбег и приводит персонажа в то состояние, в котором слайд работает.
        /// Ноль — прежнее поведение: скилл первым.
        /// </summary>
        public int PreRunMs;

        /// <summary>Прыжок отменяет анимацию. Через сколько после начала бега.</summary>
        public bool Jump = true;
        public int JumpAfterMs = 20;

        /// <summary>Отпустить физически зажатые Ctrl/Alt/Shift перед началом.
        /// Без этого модификатор-триггер приклеивается ко всем инъекциям макроса
        /// и в игру уходит Shift+W вместо W.</summary>
        public bool FreeMods = true;

        /// <summary>Удержание клавиши прыжка — величина некритичная, поэтому не в настройках.</summary>
        public const int JumpHoldMs = 20;

        /// <summary>Клавиша прыжка в игре. По умолчанию пробел.</summary>
        public int JumpVk = 0x20;

        /// <summary>
        /// Учитывать клавишу движения, которую пользователь держит пальцем.
        ///
        /// Лучник кайтит с зажатой W. Без этой галочки слайд в прыжке не может
        /// отпустить W в воздухе (она чужая), и выстрел срывается, а слайд с места
        /// в конце отпускает W — и персонаж встаёт, хотя палец на клавише.
        /// С галочкой слайд забирает W себе на время проигрыша: отпускает её где
        /// нужно, глушит автоповтор с клавиатуры, а в конце возвращает в то
        /// состояние, в котором её держит палец.
        ///
        /// Новые слайды создаются с ней. У слайдов из старых конфигов поле
        /// отсутствует и читается как false — их поведение не меняется.
        /// </summary>
        public bool RespectHeldMove = true;

        public SlideMode Mode = SlideMode.Standing;

        /// <summary>
        /// Автоатака перед скиллом, в обоих видах слайда. 0 — не жать.
        ///
        /// В слайде с места она и делает отмену рабочей: проверено в бою —
        /// «автоатака → скилл → бег с прыжком» срабатывает, без автоатаки нет.
        /// По умолчанию выключена, чтобы старые слайды не поменяли поведение.
        /// </summary>
        public int AutoVk;

        // ---- слайд в прыжке (Mode = Air) ----

        /// <summary>Сколько ждать после прыжка до скилла: скилл жмётся после верхней точки.</summary>
        public int ApexMs = 300;

        /// <summary>Пауза между автоатакой и скиллом.</summary>
        public int AutoGapMs = 20;

        /// <summary>Сколько ждать после скилла до приземления: движение раньше срока сорвёт выстрел.</summary>
        public int LandMs = 350;

        /// <summary>
        /// Сколько раз нажать скилл. Два — второй вид слайда: при убранном
        /// оружии первое нажатие его достаёт, второе — сам скилл, и сразу прыжок.
        /// </summary>
        public int SkillTaps = 1;

        /// <summary>Пауза между нажатиями скилла, если их несколько.</summary>
        public int TapGapMs = 40;

        int ExtraTapsMs { get { return Math.Max(0, SkillTaps - 1) * (TapGapMs + Ms); } }

        public MacroAction Clone()
        {
            return new MacroAction
            {
                Kind = Kind, Vk = Vk, Button = Button, Ms = Ms, Pattern = Pattern,
                SkillCtrl = SkillCtrl, SkillAlt = SkillAlt, SkillShift = SkillShift,
                CastMs = CastMs, MoveVk = MoveVk, RunMs = RunMs, PreRunMs = PreRunMs,
                Jump = Jump, JumpAfterMs = JumpAfterMs, FreeMods = FreeMods,
                Mode = Mode, AutoVk = AutoVk, ApexMs = ApexMs, AutoGapMs = AutoGapMs, LandMs = LandMs,
                SkillTaps = SkillTaps, TapGapMs = TapGapMs,
                JumpVk = JumpVk, RespectHeldMove = RespectHeldMove
            };
        }

        public bool IsKey
        {
            get { return Kind == ActionKind.KeyPress || Kind == ActionKind.KeyDown || Kind == ActionKind.KeyUp; }
        }

        public bool IsSlide { get { return Kind == ActionKind.Slide; } }

        /// <summary>Как скилл записан в игре: «Alt + 1».</summary>
        public string SkillText
        {
            get
            {
                var sb = new StringBuilder();
                if (SkillCtrl) sb.Append("Ctrl + ");
                if (SkillAlt) sb.Append("Alt + ");
                if (SkillShift) sb.Append("Shift + ");
                sb.Append(Vk == TriggerKey ? "клавиша триггера" : KeyNames.Get(Vk));
                return sb.ToString();
            }
        }

        public bool IsMouse
        {
            get { return Kind == ActionKind.MouseClick || Kind == ActionKind.MouseDown || Kind == ActionKind.MouseUp; }
        }

        /// <summary>Код иконки из шрифта Segoe MDL2 Assets.</summary>
        public string Glyph
        {
            get
            {
                if (IsSlide) return ((char)0xE72A).ToString();   // стрелка вперёд
                if (Kind == ActionKind.Delay) return "\uE916";   // секундомер
                if (Kind == ActionKind.WaitLog) return "\uE8BD"; // сообщение
                if (IsMouse) return "\uE962";                    // мышь
                return "\uE765";                                 // клавиатура
            }
        }

        public string Title
        {
            get
            {
                if (Kind == ActionKind.Delay) return "Пауза";
                if (Kind == ActionKind.WaitLog) return "Ждать строку в логе";
                if (IsSlide) return (Mode == SlideMode.Air ? "Слайд в прыжке · " : "Слайд · ")
                                  + (SkillTaps > 1 ? SkillTaps + "× " : "") + SkillText;
                if (IsMouse) return "Мышь · " + MouseName(Button);
                return Vk == TriggerKey ? "Клавиша триггера" : KeyNames.Get(Vk);
            }
        }

        public string Detail
        {
            get
            {
                switch (Kind)
                {
                    case ActionKind.Delay: return Ms + " мс";
                    case ActionKind.WaitLog: return "«" + Pattern + "» · не дольше " + Ms + " мс";
                    case ActionKind.KeyPress: return "нажать · удержание " + Ms + " мс";
                    case ActionKind.KeyDown: return "зажать";
                    case ActionKind.KeyUp: return "отпустить";
                    case ActionKind.MouseClick: return "клик · удержание " + Ms + " мс";
                    case ActionKind.MouseDown: return "зажать";
                    case ActionKind.MouseUp: return "отпустить";
                    case ActionKind.Slide:
                        if (Mode == SlideMode.Air)
                            return (PreRunMs > 0 ? "разбег " + PreRunMs + " мс · " : "")
                                 + "пик " + ApexMs + " мс · "
                                 + (AutoVk != 0 ? "автоатака " + KeyNames.Get(AutoVk) + " · " : "")
                                 + "приземление " + LandMs + " мс"
                                 + (RunMs > 0 ? " · бег " + RunMs + " мс" : "")
                                 + "   всего " + SlideTotalMs + " мс";
                        return (PreRunMs > 0 ? "разбег " + PreRunMs + " мс · " : "")
                             + (AutoVk != 0 ? "автоатака " + KeyNames.Get(AutoVk) + " · " : "")
                             + "каст " + CastMs + " мс · бег " + KeyNames.Get(MoveVk) + " " + RunMs + " мс"
                             + (Jump ? " · прыжок" : "") + "   всего " + SlideTotalMs + " мс";
                }
                return "";
            }
        }

        /// <summary>Полная длительность слайда: разбег, скилл, каст и бег.</summary>
        public int SlideTotalMs
        {
            get
            {
                if (Mode == SlideMode.Air)
                    return PreRunMs + JumpHoldMs + ApexMs + (AutoVk != 0 ? AutoGapMs : 0) + Ms + ExtraTapsMs + LandMs + RunMs;
                return PreRunMs + (AutoVk != 0 ? AutoGapMs : 0) + Ms + ExtraTapsMs + CastMs + RunMs;
            }
        }

        public static string MouseName(MouseBtn b)
        {
            switch (b)
            {
                case MouseBtn.Left: return "левая";
                case MouseBtn.Right: return "правая";
                case MouseBtn.Middle: return "средняя";
                case MouseBtn.X1: return "боковая 1";
                case MouseBtn.X2: return "боковая 2";
            }
            return "?";
        }
    }

    public sealed class MacroDef
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Новый макрос";
        public bool Enabled = true;

        public int TriggerVk;
        public bool Ctrl, Alt, Shift;

        /// <summary>
        /// Дополнительные клавиши-триггеры. Движок раскрывает макрос в копию на
        /// каждую клавишу, а MacroAction.TriggerKey в действиях подменяет нажатой
        /// клавишей. Один «спам» или «слайд» на весь ряд скиллов.
        /// </summary>
        public List<int> ExtraKeys = new List<int>();

        /// <summary>
        /// Клавиша-слой: работает как модификатор, но в игру не уходит никогда —
        /// движок её глушит и сам помнит, зажата ли она. Поэтому, в отличие от
        /// Shift, она не подмешивается в нажатия макроса, а в отличие от боковых
        /// кнопок мыши, не долетает до игры. 0 — без слоя.
        ///
        /// Сравнение точное: макрос без слоя не срабатывает, пока зажат любой слой.
        /// </summary>
        public int LayerVk;

        /// <summary>
        /// Боковые кнопки мыши как модификаторы (XBUTTON1 и XBUTTON2).
        ///
        /// Главное их достоинство перед Ctrl/Alt/Shift — они не клавиши клавиатуры,
        /// поэтому физически не могут подмешаться в инъектируемые макросом нажатия.
        /// С Shift такое бывает: он остаётся зажат, и весь макрос уходит в игру
        /// как Shift+W, Shift+Пробел и так далее.
        /// </summary>
        public bool Mouse4, Mouse5;

        /// <summary>Гасить ли исходную функцию клавиши-триггера.</summary>
        public bool Suppress = true;

        public RepeatMode Repeat = RepeatMode.Once;
        public int RepeatCount = 3;
        public int GapMs;
        public Retrigger OnRetrigger = Retrigger.Restart;

        public List<MacroAction> Actions = new List<MacroAction>();

        public bool HasTrigger { get { return TriggerVk != 0; } }

        /// <summary>Все клавиши-триггеры: основная и дополнительные, без повторов.</summary>
        public List<int> AllKeys
        {
            get
            {
                var list = new List<int>();
                if (TriggerVk != 0) list.Add(TriggerVk);
                foreach (int k in ExtraKeys)
                    if (k != 0 && !list.Contains(k)) list.Add(k);
                return list;
            }
        }

        /// <summary>
        /// Копия под одну клавишу: триггер — эта клавиша, заглушка в действиях
        /// заменена на неё. Так движок превращает макрос на ряд клавиш в обычные
        /// однокнопочные. Id сохраняется — по нему интерфейс узнаёт свой макрос.
        /// </summary>
        public MacroDef ExpandFor(int vk)
        {
            var m = Clone();
            m.Id = Id;
            m.TriggerVk = vk;
            m.ExtraKeys.Clear();
            foreach (var a in m.Actions)
                if (a.Vk == MacroAction.TriggerKey) a.Vk = vk;
            return m;
        }

        /// <summary>
        /// Один и тот же триггер: клавиша и все модификаторы совпадают.
        ///
        /// Движок сравнивает модификаторы точно, поэтому два таких макроса
        /// сработают ОБА и одновременно. Обычно это ошибка: спам и слайд на одной
        /// клавише должны отличаться хотя бы боковой кнопкой мыши.
        /// </summary>
        public bool SameTriggerAs(MacroDef other)
        {
            if (TriggerVk == 0 || other.TriggerVk == 0) return false;
            if (Ctrl != other.Ctrl || Alt != other.Alt || Shift != other.Shift) return false;
            if (Mouse4 != other.Mouse4 || Mouse5 != other.Mouse5 || LayerVk != other.LayerVk) return false;

            var theirs = other.AllKeys;
            foreach (int k in AllKeys)
                if (theirs.Contains(k)) return true;
            return false;
        }

        public string TriggerText
        {
            get
            {
                if (TriggerVk == 0) return "не назначен";
                var sb = new StringBuilder();
                if (LayerVk != 0) sb.Append(KeyNames.Get(LayerVk)).Append(" + ");
                if (Ctrl) sb.Append("Ctrl + ");
                if (Alt) sb.Append("Alt + ");
                if (Shift) sb.Append("Shift + ");
                if (Mouse4) sb.Append("Бок. 1 + ");
                if (Mouse5) sb.Append("Бок. 2 + ");
                sb.Append(KeyNames.Get(TriggerVk));
                int extra = AllKeys.Count - 1;
                if (extra > 0) sb.Append(" и ещё ").Append(extra);
                return sb.ToString();
            }
        }

        public string RepeatText
        {
            get
            {
                switch (Repeat)
                {
                    case RepeatMode.Once: return "один раз";
                    case RepeatMode.WhileHeld: return "пока держу";
                    case RepeatMode.Toggle: return "тумблер";
                    case RepeatMode.Count: return RepeatCount + " повторов";
                }
                return "";
            }
        }

        /// <summary>Суммарная длительность одного прохода, мс.</summary>
        public int EstimatedMs
        {
            get
            {
                int total = 0;
                foreach (var a in Actions)
                {
                    if (a.Kind == ActionKind.Slide) total += a.SlideTotalMs;
                    else if (a.Kind == ActionKind.Delay || a.Kind == ActionKind.WaitLog || a.Kind == ActionKind.KeyPress || a.Kind == ActionKind.MouseClick)
                        total += a.Ms;
                }
                return total;
            }
        }

        public MacroDef Clone()
        {
            var m = new MacroDef
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = Name,
                Enabled = Enabled,
                TriggerVk = TriggerVk,
                Ctrl = Ctrl,
                Alt = Alt,
                Shift = Shift,
                Mouse4 = Mouse4,
                Mouse5 = Mouse5,
                ExtraKeys = new List<int>(ExtraKeys),
                LayerVk = LayerVk,
                Suppress = Suppress,
                Repeat = Repeat,
                RepeatCount = RepeatCount,
                GapMs = GapMs,
                OnRetrigger = OnRetrigger,
                Actions = new List<MacroAction>()
            };
            foreach (var a in Actions) m.Actions.Add(a.Clone());
            return m;
        }
    }

    /// <summary>
    /// Независимый набор макросов. Профили не пересекаются: правка в одном
    /// не может задеть другой — за это платим тем, что общие макросы копируются.
    /// </summary>
    public sealed class Profile
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name = "Профиль";
        public List<MacroDef> Macros = new List<MacroDef>();

        /// <summary>Одна-две буквы для значка в левой полосе.</summary>
        public string Initials
        {
            get
            {
                // Слова — только те, что начинаются с буквы или цифры: иначе в
                // «Лучник · слайды» разделитель «·» становился второй буквой значка.
                var words = new List<string>();
                foreach (var w in (Name ?? "").Split(new[] { ' ', '-', '_', '/' }, StringSplitOptions.RemoveEmptyEntries))
                    if (char.IsLetterOrDigit(w[0])) words.Add(w);

                if (words.Count == 0) return "?";
                if (words.Count == 1) return words[0].Substring(0, 1).ToUpperInvariant();
                return (words[0].Substring(0, 1) + words[1].Substring(0, 1)).ToUpperInvariant();
            }
        }

        /// <summary>
        /// Спорит ли макрос за триггер с другим включённым. Выключенные не в счёт:
        /// они не срабатывают, значит и мешать не могут.
        /// </summary>
        public bool HasTriggerConflict(MacroDef m)
        {
            if (m == null || !m.Enabled || !m.HasTrigger) return false;

            foreach (var other in Macros)
            {
                if (ReferenceEquals(other, m) || !other.Enabled) continue;
                if (m.SameTriggerAs(other)) return true;
            }
            return false;
        }

        public Profile Clone()
        {
            var p = new Profile { Id = Guid.NewGuid().ToString("N"), Name = Name };
            foreach (var m in Macros) p.Macros.Add(m.Clone());
            return p;
        }
    }

    public sealed class AppConfig
    {
        public bool EngineEnabled = true;
        public List<Profile> Profiles = new List<Profile>();
        public string ActiveProfileId;

        /// <summary>Chat.log игры — для действия «Ждать строку в логе».</summary>
        public string ChatLogPath = @"C:\Games\Aion Empire\classic\Chat.log";

        /// <summary>
        /// Макросы работают, только пока на переднем плане окно этого процесса.
        /// Иначе «4», набранная в Discord или браузере, уходит слайдом.
        /// </summary>
        public bool OnlyInGame;
        public string GameProcess = "aion.bin";

        /// <summary>
        /// «Режим чата»: Enter ставит макросы на паузу до следующего Enter или Esc.
        /// Эвристика — программа не видит, открыт ли чат в игре, — но она
        /// закрывает почти все случаи «набрал сообщение — ушли скиллы».
        /// </summary>
        public bool ChatPause;

        /// <summary>Клавиша, которая включает и выключает движок. 0 — нет. По умолчанию Pause.</summary>
        public int ToggleVk = 0x13;

        /// <summary>FPS игры — чтобы показывать тайминги ещё и в кадрах. 0 — не показывать.</summary>
        public int GameFps;

        /// <summary>Писать ленту событий каждого слайда в slides-timing.txt.</summary>
        public bool SlideJournal;

        /// <summary>Выполненные одноразовые переделки конфига (см. ConfigStore.Migrate).</summary>
        public List<string> Migrations = new List<string>();

        /// <summary>
        /// Активный профиль. Никогда не возвращает null: пустой список профилей
        /// чинится на месте, иначе любой битый конфиг ронял бы интерфейс.
        /// </summary>
        public Profile Active
        {
            get
            {
                if (Profiles.Count == 0) Profiles.Add(new Profile { Name = "Основной" });

                for (int i = 0; i < Profiles.Count; i++)
                    if (Profiles[i].Id == ActiveProfileId) return Profiles[i];

                ActiveProfileId = Profiles[0].Id;
                return Profiles[0];
            }
        }

        /// <summary>
        /// Макросы активного профиля. Свойство, а не поле, специально: так весь
        /// остальной код (движок, интерфейс) не заметил появления профилей вообще.
        /// </summary>
        public List<MacroDef> Macros { get { return Active.Macros; } }
    }

    /// <summary>Человекочитаемые имена клавиш.</summary>
    public static class KeyNames
    {
        static readonly Dictionary<int, string> Special = new Dictionary<int, string>
        {
            { 0x08, "Backspace" }, { 0x09, "Tab" },      { 0x0D, "Enter" },   { 0x13, "Pause" },
            { 0x14, "CapsLock" }, { 0x1B, "Esc" },       { 0x20, "Пробел" },  { 0x21, "PageUp" },
            { 0x22, "PageDown" }, { 0x23, "End" },       { 0x24, "Home" },
            { 0x25, "←" },   { 0x26, "↑" },    { 0x27, "→" },  { 0x28, "↓" },
            { 0x2C, "PrintScr" }, { 0x2D, "Insert" },    { 0x2E, "Delete" },
            { 0x5B, "Win" },      { 0x5C, "Win (пр.)" }, { 0x5D, "Menu" },
            { 0x90, "NumLock" },  { 0x91, "ScrollLock" },
            { 0x10, "Shift" },    { 0x11, "Ctrl" },      { 0x12, "Alt" },
            { 0xA0, "Shift" },    { 0xA1, "Shift (пр.)" },
            { 0xA2, "Ctrl" },     { 0xA3, "Ctrl (пр.)" },
            { 0xA4, "Alt" },      { 0xA5, "Alt (пр.)" },
            { 0xBA, ";" },  { 0xBB, "=" },  { 0xBC, "," },  { 0xBD, "-" },
            { 0xBE, "." },  { 0xBF, "/" },  { 0xC0, "`" },
            { 0xDB, "[" },  { 0xDC, "\\" }, { 0xDD, "]" },  { 0xDE, "'" },
            { 0x6A, "Num *" }, { 0x6B, "Num +" }, { 0x6D, "Num -" },
            { 0x6E, "Num ." }, { 0x6F, "Num /" },
            { MacroAction.TriggerKey, "клавиша триггера" }
        };

        public static string Get(int vk)
        {
            if (vk == 0) return "—";

            string s;
            if (Special.TryGetValue(vk, out s)) return s;

            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();              // 0-9
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();              // A-Z
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);               // Num 0-9
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);                  // F1-F24

            return ((Keys)vk).ToString();
        }

        public static bool IsModifier(int vk)
        {
            return vk == 0xA0 || vk == 0xA1 || vk == 0xA2 || vk == 0xA3 || vk == 0xA4 || vk == 0xA5
                || vk == 0x10 || vk == 0x11 || vk == 0x12;
        }
    }
}
