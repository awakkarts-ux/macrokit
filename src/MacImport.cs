using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MacroKit
{
    /// <summary>
    /// Импорт файлов .mac из родного софта клавиатуры.
    ///
    /// Формат — JSON вида:
    ///   { "Events": [ { "Action": "down", "Button": 26, "Delay": "20", "Type": 10 } ], "MacroName": "..." }
    ///
    /// Два момента, которые нужно знать про этот формат:
    ///
    /// 1. Button — это НЕ виртуальный код Windows, а HID Usage ID (страница 0x07).
    ///    26 там означает W, а вовсе не «стрелку вниз». Проверено на 13 файлах:
    ///    имя макроса всегда совпадает с расшифровкой кода (slide_kite_Q -> 20 -> Q).
    ///
    /// 2. Delay — пауза ПОСЛЕ события, а не перед ним. Это видно по файлу вида
    ///    «тап скилла, пауза на время каста, потом движение»: при обратном чтении
    ///    пробел получал бы нулевое удержание в самом конце, что бессмысленно.
    ///    Плюс у последнего события Delay всегда 0 — как и положено «паузе после».
    /// </summary>
    public static class MacImport
    {
        const int TypeKeyboard = 10;

        public sealed class Result
        {
            public MacroDef Macro;
            public string Warning;
            public string Error;
        }

        public static Result Load(string path)
        {
            var res = new Result();

            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);

                var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (root == null)
                {
                    res.Error = "не похоже на .mac (ожидался JSON-объект)";
                    return res;
                }

                object rawEvents;
                if (!root.TryGetValue("Events", out rawEvents))
                {
                    res.Error = "в файле нет списка Events";
                    return res;
                }

                var arr = rawEvents as object[];
                if (arr == null || arr.Length == 0)
                {
                    res.Error = "список событий пуст";
                    return res;
                }

                string name = null;
                object rawName;
                if (root.TryGetValue("MacroName", out rawName) && rawName != null)
                    name = Convert.ToString(rawName, CultureInfo.InvariantCulture);

                if (string.IsNullOrWhiteSpace(name))
                    name = Path.GetFileNameWithoutExtension(path);

                var events = new List<Ev>();
                var unknownTypes = new List<int>();
                var unknownButtons = new List<int>();

                foreach (var item in arr)
                {
                    var d = item as Dictionary<string, object>;
                    if (d == null) continue;

                    int type = ReadInt(d, "Type", TypeKeyboard);
                    if (type != TypeKeyboard)
                    {
                        if (!unknownTypes.Contains(type)) unknownTypes.Add(type);
                        continue;
                    }

                    int usage = ReadInt(d, "Button", 0);
                    int vk = HidToVk(usage);
                    if (vk == 0)
                    {
                        if (!unknownButtons.Contains(usage)) unknownButtons.Add(usage);
                        continue;
                    }

                    string action = ReadString(d, "Action", "down");

                    events.Add(new Ev
                    {
                        Vk = vk,
                        Down = string.Equals(action, "down", StringComparison.OrdinalIgnoreCase),
                        DelayAfter = Math.Max(0, ReadInt(d, "Delay", 0))
                    });
                }

                if (events.Count == 0)
                {
                    res.Error = "не удалось распознать ни одного события";
                    return res;
                }

                var macro = new MacroDef
                {
                    Name = name,
                    Repeat = RepeatMode.Once,
                    Actions = ToActions(events)
                };

                // Первая нажатая клавиша — это и есть скилл, ради которого макрос написан,
                // поэтому она же напрашивается на роль триггера. Подставляем как заготовку,
                // но макрос оставляем ВЫКЛЮЧЕННЫМ: пока пользователь сам не включит,
                // поведение клавиши не меняется.
                macro.TriggerVk = FirstKey(macro.Actions);
                macro.Enabled = false;

                res.Macro = macro;

                var warn = new List<string>();
                if (unknownTypes.Count > 0)
                    warn.Add("пропущены события неизвестного типа: " + string.Join(", ", unknownTypes.ConvertAll(x => x.ToString()).ToArray()));
                if (unknownButtons.Count > 0)
                    warn.Add("пропущены нераспознанные клавиши (HID): " + string.Join(", ", unknownButtons.ConvertAll(x => x.ToString()).ToArray()));

                if (warn.Count > 0) res.Warning = string.Join("; ", warn.ToArray());

                return res;
            }
            catch (Exception ex)
            {
                res.Error = ex.Message;
                return res;
            }
        }

        // ---------- преобразование ----------

        sealed class Ev
        {
            public int Vk;
            public bool Down;
            public int DelayAfter;
        }

        /// <summary>
        /// Разворачивает поток событий в список действий, схлопывая пару
        /// «зажать X → отпустить X» в одно нажатие с нужным удержанием.
        /// </summary>
        static List<MacroAction> ToActions(List<Ev> events)
        {
            var actions = new List<MacroAction>();
            int i = 0;

            while (i < events.Count)
            {
                var e = events[i];

                bool pair = e.Down
                    && i + 1 < events.Count
                    && !events[i + 1].Down
                    && events[i + 1].Vk == e.Vk;

                if (pair)
                {
                    var up = events[i + 1];

                    actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = e.Vk, Ms = e.DelayAfter });
                    if (up.DelayAfter > 0) actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = up.DelayAfter });

                    i += 2;
                    continue;
                }

                actions.Add(new MacroAction
                {
                    Kind = e.Down ? ActionKind.KeyDown : ActionKind.KeyUp,
                    Vk = e.Vk,
                    Ms = 0
                });

                if (e.DelayAfter > 0)
                    actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = e.DelayAfter });

                i++;
            }

            return actions;
        }

        static int FirstKey(List<MacroAction> actions)
        {
            foreach (var a in actions)
                if (a.IsKey && a.Vk != 0) return a.Vk;

            return 0;
        }

        // ---------- HID Usage (страница 0x07) -> виртуальный код Windows ----------

        public static int HidToVk(int usage)
        {
            if (usage >= 4 && usage <= 29) return 0x41 + (usage - 4);      // A-Z
            if (usage >= 30 && usage <= 38) return 0x31 + (usage - 30);    // 1-9
            if (usage == 39) return 0x30;                                  // 0
            if (usage >= 58 && usage <= 69) return 0x70 + (usage - 58);    // F1-F12
            if (usage >= 89 && usage <= 97) return 0x61 + (usage - 89);    // Num 1-9
            if (usage >= 104 && usage <= 115) return 0x7C + (usage - 104); // F13-F24

            switch (usage)
            {
                case 40: return 0x0D;   // Enter
                case 41: return 0x1B;   // Esc
                case 42: return 0x08;   // Backspace
                case 43: return 0x09;   // Tab
                case 44: return 0x20;   // Space
                case 45: return 0xBD;   // -
                case 46: return 0xBB;   // =
                case 47: return 0xDB;   // [
                case 48: return 0xDD;   // ]
                case 49: return 0xDC;   // \
                case 50: return 0xDC;   // non-US #
                case 51: return 0xBA;   // ;
                case 52: return 0xDE;   // '
                case 53: return 0xC0;   // `
                case 54: return 0xBC;   // ,
                case 55: return 0xBE;   // .
                case 56: return 0xBF;   // /
                case 57: return 0x14;   // CapsLock
                case 70: return 0x2C;   // PrintScreen
                case 71: return 0x91;   // ScrollLock
                case 72: return 0x13;   // Pause
                case 73: return 0x2D;   // Insert
                case 74: return 0x24;   // Home
                case 75: return 0x21;   // PageUp
                case 76: return 0x2E;   // Delete
                case 77: return 0x23;   // End
                case 78: return 0x22;   // PageDown
                case 79: return 0x27;   // Right
                case 80: return 0x25;   // Left
                case 81: return 0x28;   // Down
                case 82: return 0x26;   // Up
                case 83: return 0x90;   // NumLock
                case 84: return 0x6F;   // Num /
                case 85: return 0x6A;   // Num *
                case 86: return 0x6D;   // Num -
                case 87: return 0x6B;   // Num +
                case 88: return 0x0D;   // Num Enter
                case 98: return 0x60;   // Num 0
                case 99: return 0x6E;   // Num .
                case 101: return 0x5D;  // Menu
                case 224: return 0xA2;  // LCtrl
                case 225: return 0xA0;  // LShift
                case 226: return 0xA4;  // LAlt
                case 227: return 0x5B;  // LWin
                case 228: return 0xA3;  // RCtrl
                case 229: return 0xA1;  // RShift
                case 230: return 0xA5;  // RAlt
                case 231: return 0x5C;  // RWin
            }

            return 0;
        }

        // ---------- помощники ----------

        static int ReadInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;

            // Delay в этом формате хранится строкой, Button и Type — числом
            try { return System.Convert.ToInt32(System.Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        static string ReadString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            return System.Convert.ToString(v, CultureInfo.InvariantCulture);
        }
    }
}
