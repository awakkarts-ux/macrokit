using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MacroKit
{
    /// <summary>
    /// Чтение и запись конфига. Пишем свой JSON, чтобы файл оставался
    /// человекочитаемым (перечисления строками, отступы) и его можно было править руками.
    /// Читаем встроенным парсером из System.Web.Extensions — внешних зависимостей нет.
    /// </summary>
    public static class ConfigStore
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroKit");

        public static readonly string FilePath = Path.Combine(Dir, "config.json");

        // ---------- чтение ----------

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return Seed();

                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text)) return Seed();

                var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                if (root == null) return Seed();

                var cfg = new AppConfig();
                cfg.EngineEnabled = B(root, "engineEnabled", true);
                cfg.ChatLogPath = S(root, "chatLogPath", cfg.ChatLogPath);

                object rawProfiles;
                if (root.TryGetValue("profiles", out rawProfiles) && rawProfiles is object[])
                {
                    foreach (var item in (object[])rawProfiles)
                    {
                        var d = item as Dictionary<string, object>;
                        if (d == null) continue;

                        var p = new Profile();
                        string pid = S(d, "id", null);
                        if (!string.IsNullOrEmpty(pid)) p.Id = pid;
                        p.Name = S(d, "name", "Профиль");
                        ReadMacrosInto(d, p.Macros);
                        cfg.Profiles.Add(p);
                    }
                    cfg.ActiveProfileId = S(root, "activeProfile", null);
                }
                else
                {
                    // Старый формат: макросы лежали в корне. Заворачиваем их в один
                    // профиль, чтобы уже настроенное не потерялось при обновлении.
                    var p = new Profile { Name = "Основной" };
                    ReadMacrosInto(root, p.Macros);
                    cfg.Profiles.Add(p);
                    cfg.ActiveProfileId = p.Id;
                }

                if (cfg.Profiles.Count == 0) cfg.Profiles.Add(new Profile { Name = "Основной" });

                return cfg;
            }
            catch
            {
                // Битый конфиг не должен мешать запуску программы.
                return Seed();
            }
        }

        /// <summary>Читает массив "macros" из узла в готовый список.</summary>
        static void ReadMacrosInto(Dictionary<string, object> node, List<MacroDef> into)
        {
            object raw;
            if (!node.TryGetValue("macros", out raw)) return;

            var arr = raw as object[];
            if (arr == null) return;

            foreach (var item in arr)
            {
                var d = item as Dictionary<string, object>;
                if (d != null) into.Add(ReadMacro(d));
            }
        }

        static MacroDef ReadMacro(Dictionary<string, object> d)
        {
            var m = new MacroDef();

            string id = S(d, "id", null);
            if (!string.IsNullOrEmpty(id)) m.Id = id;

            m.Name = S(d, "name", "Без названия");
            m.Enabled = B(d, "enabled", true);
            m.TriggerVk = I(d, "triggerVk", 0);
            m.LayerVk = I(d, "layerVk", 0);

            object rawKeys;
            if (d.TryGetValue("extraKeys", out rawKeys) && rawKeys is object[])
            {
                foreach (var k in (object[])rawKeys)
                {
                    try { m.ExtraKeys.Add(Convert.ToInt32(k, CultureInfo.InvariantCulture)); }
                    catch { }
                }
            }

            m.Ctrl = B(d, "ctrl", false);
            m.Alt = B(d, "alt", false);
            m.Shift = B(d, "shift", false);
            m.Mouse4 = B(d, "mouse4", false);
            m.Mouse5 = B(d, "mouse5", false);
            m.Suppress = B(d, "suppressTrigger", true);
            m.Repeat = E(d, "repeat", RepeatMode.Once);
            m.RepeatCount = Math.Max(1, I(d, "repeatCount", 3));
            m.GapMs = Math.Max(0, I(d, "gapMs", 0));
            m.OnRetrigger = E(d, "onRetrigger", Retrigger.Restart);

            object rawActions;
            if (d.TryGetValue("actions", out rawActions))
            {
                var arr = rawActions as object[];
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var ad = item as Dictionary<string, object>;
                        if (ad == null) continue;
                        m.Actions.Add(new MacroAction
                        {
                            Kind = E(ad, "kind", ActionKind.KeyPress),
                            Vk = I(ad, "vk", 0),
                            Button = E(ad, "button", MouseBtn.Left),
                            Ms = Math.Max(0, I(ad, "ms", 30)),
                            Pattern = S(ad, "pattern", ""),

                            SkillCtrl = B(ad, "skillCtrl", false),
                            SkillAlt = B(ad, "skillAlt", false),
                            SkillShift = B(ad, "skillShift", false),
                            CastMs = Math.Max(0, I(ad, "castMs", 20)),
                            MoveVk = I(ad, "moveVk", 0x57),
                            RunMs = Math.Max(0, I(ad, "runMs", 420)),
                            PreRunMs = Math.Max(0, I(ad, "preRunMs", 0)),
                            Jump = B(ad, "jump", true),
                            JumpAfterMs = Math.Max(0, I(ad, "jumpAfterMs", 20)),
                            FreeMods = B(ad, "freeMods", true),
                            Mode = E(ad, "mode", SlideMode.Standing),
                            AutoVk = I(ad, "autoVk", 0),
                            ApexMs = Math.Max(0, I(ad, "apexMs", 300)),
                            AutoGapMs = Math.Max(0, I(ad, "autoGapMs", 20)),
                            LandMs = Math.Max(0, I(ad, "landMs", 350)),
                            SkillTaps = Math.Max(1, I(ad, "skillTaps", 1)),
                            TapGapMs = Math.Max(0, I(ad, "tapGapMs", 40))
                        });
                    }
                }
            }

            return m;
        }

        // ---------- запись ----------

        public static void Save(AppConfig cfg)
        {
            Directory.CreateDirectory(Dir);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"engineEnabled\": ").Append(cfg.EngineEnabled ? "true" : "false").Append(",\n");
            sb.Append("  \"activeProfile\": ").Append(Q(cfg.Active.Id)).Append(",\n");
            sb.Append("  \"chatLogPath\": ").Append(Q(cfg.ChatLogPath)).Append(",\n");
            sb.Append("  \"profiles\": [\n");

            for (int p = 0; p < cfg.Profiles.Count; p++)
            {
                var prof = cfg.Profiles[p];

                sb.Append("    {\n");
                sb.Append("      \"id\": ").Append(Q(prof.Id)).Append(",\n");
                sb.Append("      \"name\": ").Append(Q(prof.Name)).Append(",\n");
                sb.Append("      \"macros\": [");

                if (prof.Macros.Count == 0)
                {
                    sb.Append("]\n");
                }
                else
                {
                    sb.Append('\n');
                    for (int i = 0; i < prof.Macros.Count; i++)
                    {
                        WriteMacro(sb, prof.Macros[i]);
                        if (i < prof.Macros.Count - 1) sb.Append(',');
                        sb.Append('\n');
                    }
                    sb.Append("      ]\n");
                }

                sb.Append("    }");
                if (p < cfg.Profiles.Count - 1) sb.Append(',');
                sb.Append('\n');
            }

            sb.Append("  ]\n");
            sb.Append("}\n");

            // Пишем через временный файл, чтобы падение на середине не убило конфиг.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));

            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        static void WriteMacro(StringBuilder sb, MacroDef m)
        {
            sb.Append("        {\n");
            sb.Append("          \"id\": ").Append(Q(m.Id)).Append(",\n");
            sb.Append("          \"name\": ").Append(Q(m.Name)).Append(",\n");
            sb.Append("          \"enabled\": ").Append(m.Enabled ? "true" : "false").Append(",\n");
            sb.Append("          \"triggerVk\": ").Append(m.TriggerVk).Append(",\n");
            if (m.ExtraKeys.Count > 0)
            {
                var keys = new List<string>();
                foreach (int k in m.ExtraKeys) keys.Add(k.ToString(CultureInfo.InvariantCulture));
                sb.Append("          \"extraKeys\": [").Append(string.Join(", ", keys.ToArray())).Append("],\n");
            }
            if (m.LayerVk != 0) sb.Append("          \"layerVk\": ").Append(m.LayerVk).Append(",\n");
            sb.Append("          \"ctrl\": ").Append(m.Ctrl ? "true" : "false").Append(",\n");
            sb.Append("          \"alt\": ").Append(m.Alt ? "true" : "false").Append(",\n");
            sb.Append("          \"shift\": ").Append(m.Shift ? "true" : "false").Append(",\n");
            sb.Append("          \"mouse4\": ").Append(m.Mouse4 ? "true" : "false").Append(",\n");
            sb.Append("          \"mouse5\": ").Append(m.Mouse5 ? "true" : "false").Append(",\n");
            sb.Append("          \"suppressTrigger\": ").Append(m.Suppress ? "true" : "false").Append(",\n");
            sb.Append("          \"repeat\": ").Append(Q(m.Repeat.ToString())).Append(",\n");
            sb.Append("          \"repeatCount\": ").Append(m.RepeatCount).Append(",\n");
            sb.Append("          \"gapMs\": ").Append(m.GapMs).Append(",\n");
            sb.Append("          \"onRetrigger\": ").Append(Q(m.OnRetrigger.ToString())).Append(",\n");
            sb.Append("          \"actions\": [");

            if (m.Actions.Count == 0)
            {
                sb.Append("]\n");
            }
            else
            {
                sb.Append('\n');
                for (int i = 0; i < m.Actions.Count; i++)
                {
                    var a = m.Actions[i];
                    sb.Append("            { \"kind\": ").Append(Q(a.Kind.ToString()));
                    if (a.IsKey || a.IsSlide) sb.Append(", \"vk\": ").Append(a.Vk);
                    if (a.IsMouse) sb.Append(", \"button\": ").Append(Q(a.Button.ToString()));
                    sb.Append(", \"ms\": ").Append(a.Ms);
                    if (a.Kind == ActionKind.WaitLog) sb.Append(", \"pattern\": ").Append(Q(a.Pattern));

                    if (a.IsSlide)
                    {
                        sb.Append(", \"skillCtrl\": ").Append(a.SkillCtrl ? "true" : "false");
                        sb.Append(", \"skillAlt\": ").Append(a.SkillAlt ? "true" : "false");
                        sb.Append(", \"skillShift\": ").Append(a.SkillShift ? "true" : "false");
                        sb.Append(", \"castMs\": ").Append(a.CastMs);
                        sb.Append(", \"moveVk\": ").Append(a.MoveVk);
                        sb.Append(", \"runMs\": ").Append(a.RunMs);
                        sb.Append(", \"preRunMs\": ").Append(a.PreRunMs);
                        sb.Append(", \"jump\": ").Append(a.Jump ? "true" : "false");
                        sb.Append(", \"jumpAfterMs\": ").Append(a.JumpAfterMs);
                        sb.Append(", \"freeMods\": ").Append(a.FreeMods ? "true" : "false");
                        sb.Append(", \"mode\": ").Append(Q(a.Mode.ToString()));
                        sb.Append(", \"autoVk\": ").Append(a.AutoVk);
                        sb.Append(", \"autoGapMs\": ").Append(a.AutoGapMs);
                        if (a.SkillTaps > 1)
                        {
                            sb.Append(", \"skillTaps\": ").Append(a.SkillTaps);
                            sb.Append(", \"tapGapMs\": ").Append(a.TapGapMs);
                        }
                        if (a.Mode == SlideMode.Air)
                        {
                            sb.Append(", \"apexMs\": ").Append(a.ApexMs);
                            sb.Append(", \"landMs\": ").Append(a.LandMs);
                        }
                    }

                    sb.Append(" }");
                    if (i < m.Actions.Count - 1) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append("          ]\n");
            }

            sb.Append("        }");
        }

        static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ---------- помощники разбора ----------

        static string S(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }

        static int I(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
                catch { return def; }
            }
            return def;
        }

        static bool B(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); }
                catch { return def; }
            }
            return def;
        }

        static T E<T>(Dictionary<string, object> d, string key, T def) where T : struct
        {
            object v;
            if (d.TryGetValue(key, out v) && v != null)
            {
                T parsed;
                if (Enum.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), true, out parsed))
                    return parsed;
            }
            return def;
        }

        // ---------- первый запуск ----------

        static AppConfig Seed()
        {
            var cfg = new AppConfig();
            var main = new Profile { Name = "Основной" };
            cfg.Profiles.Add(main);
            cfg.ActiveProfileId = main.Id;

            var demo = new MacroDef
            {
                Name = "Пример — удали или переделай",
                Enabled = false,
                TriggerVk = 0,
                Repeat = RepeatMode.Once
            };
            demo.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x31, Ms = 30 }); // 1
            demo.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 120 });
            demo.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x32, Ms = 30 }); // 2
            demo.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 120 });
            demo.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x33, Ms = 30 }); // 3

            cfg.Macros.Add(demo);
            return cfg;
        }
    }
}
