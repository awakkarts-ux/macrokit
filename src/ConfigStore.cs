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

        /// <summary>Сюда складываются копии конфига перед перезаписью.</summary>
        public static readonly string BackupDir = Path.Combine(Dir, "backups");

        const int BackupsKept = 10;
        static readonly TimeSpan BackupEvery = TimeSpan.FromMinutes(30);
        static DateTime _lastBackup = DateTime.MinValue;

        /// <summary>
        /// Что случилось при последней загрузке, если что-то случилось: интерфейс
        /// показывает это пользователю. null — всё прочиталось.
        /// </summary>
        public static string LoadProblem { get; private set; }

        /// <summary>
        /// Конфиг не прочитался, и сохранить его копию тоже не вышло. Пока так,
        /// запись запрещена: иначе первое же сохранение затрёт единственный
        /// экземпляр настроек пользователя набором по умолчанию.
        /// </summary>
        static bool _saveBlocked;

        // ---------- чтение ----------

        public static AppConfig Load()
        {
            LoadProblem = null;
            _saveBlocked = false;

            if (!File.Exists(FilePath)) return Seed();

            try
            {
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                    throw new InvalidDataException("файл пустой");

                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text) as Dictionary<string, object>;
                if (root == null)
                    throw new InvalidDataException("в файле не объект JSON");

                var cfg = Parse(root);
                Migrate(cfg);
                return cfg;
            }
            catch (Exception ex)
            {
                // Битый конфиг не должен мешать запуску — но и пропасть не должен.
                // Раньше здесь молча возвращался набор по умолчанию, и первое же
                // сохранение затирало настройки. Теперь файл сначала откладывается.
                string kept = KeepBroken();
                LoadProblem = "Не удалось прочитать настройки: " + ex.Message + "\n\n" +
                    (kept != null
                        ? "Файл сохранён как\n" + kept + "\nПрограмма запущена с чистыми настройками."
                        : "Отложить копию файла тоже не вышло, поэтому запись настроек выключена до перезапуска — " +
                          "иначе он был бы затёрт. Файл: " + FilePath);
                if (kept == null) _saveBlocked = true;
                return Seed();
            }
        }

        /// <summary>Копия нечитаемого конфига рядом с ним. null — не получилось.</summary>
        static string KeepBroken()
        {
            try
            {
                string dest = Path.Combine(Dir, "config.broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.Copy(FilePath, dest, true);
                return dest;
            }
            catch
            {
                return null;
            }
        }

        static AppConfig Parse(Dictionary<string, object> root)
        {
            var cfg = new AppConfig();
            cfg.EngineEnabled = B(root, "engineEnabled", true);
            cfg.ChatLogPath = S(root, "chatLogPath", cfg.ChatLogPath);
            cfg.OnlyInGame = B(root, "onlyInGame", false);
            cfg.GameProcess = S(root, "gameProcess", cfg.GameProcess);
            cfg.ChatPause = B(root, "chatPause", false);
            cfg.ToggleVk = I(root, "toggleVk", cfg.ToggleVk);
            cfg.GameFps = Math.Max(0, I(root, "gameFps", 0));
            cfg.SlideJournal = B(root, "slideJournal", false);

            object rawMigrations;
            if (root.TryGetValue("migrations", out rawMigrations) && rawMigrations is object[])
                foreach (var mg in (object[])rawMigrations)
                    if (mg != null) cfg.Migrations.Add(Convert.ToString(mg, CultureInfo.InvariantCulture));

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

        // ---------- одноразовые переделки ----------

        public const string MigrationHeldMove = "held-move-profile";

        /// <summary>
        /// Слайды научились учитывать W, зажатую пальцем (MacroAction.RespectHeldMove).
        /// Старые профили не трогаем — договорённость: правки макросов только в
        /// новом профиле. Поэтому для каждого профиля со слайдами делается копия
        /// «… · W рукой», где у слайдов новое поведение включено. Активным остаётся
        /// прежний профиль: переключиться — решение пользователя.
        /// </summary>
        internal static void Migrate(AppConfig cfg)
        {
            if (cfg.Migrations.Contains(MigrationHeldMove)) return;

            foreach (var p in cfg.Profiles.ToArray())
            {
                bool hasOldSlides = false;
                foreach (var m in p.Macros)
                    foreach (var a in m.Actions)
                        if (a.IsSlide && !a.RespectHeldMove) hasOldSlides = true;
                if (!hasOldSlides) continue;

                var copy = p.Clone();
                copy.Name = p.Name + " · W рукой";
                foreach (var m in copy.Macros)
                    foreach (var a in m.Actions)
                        if (a.IsSlide) a.RespectHeldMove = true;

                cfg.Profiles.Insert(cfg.Profiles.IndexOf(p) + 1, copy);
            }

            cfg.Migrations.Add(MigrationHeldMove);
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
                            TapGapMs = Math.Max(0, I(ad, "tapGapMs", 40)),
                            JumpVk = I(ad, "jumpVk", 0x20),
                            // нет поля — слайд из старого конфига, поведение прежнее
                            RespectHeldMove = B(ad, "respectHeldMove", false)
                        });
                    }
                }
            }

            return m;
        }

        // ---------- запись ----------

        public static void Save(AppConfig cfg)
        {
            if (_saveBlocked)
                throw new IOException("Запись выключена: прежний конфиг не прочитался и его копию сохранить не удалось.");

            Directory.CreateDirectory(Dir);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"engineEnabled\": ").Append(cfg.EngineEnabled ? "true" : "false").Append(",\n");
            sb.Append("  \"activeProfile\": ").Append(Q(cfg.Active.Id)).Append(",\n");
            sb.Append("  \"chatLogPath\": ").Append(Q(cfg.ChatLogPath)).Append(",\n");
            sb.Append("  \"onlyInGame\": ").Append(cfg.OnlyInGame ? "true" : "false").Append(",\n");
            sb.Append("  \"gameProcess\": ").Append(Q(cfg.GameProcess)).Append(",\n");
            sb.Append("  \"chatPause\": ").Append(cfg.ChatPause ? "true" : "false").Append(",\n");
            sb.Append("  \"toggleVk\": ").Append(cfg.ToggleVk).Append(",\n");
            sb.Append("  \"gameFps\": ").Append(cfg.GameFps).Append(",\n");
            sb.Append("  \"slideJournal\": ").Append(cfg.SlideJournal ? "true" : "false").Append(",\n");
            {
                var ms = new List<string>();
                foreach (var mg in cfg.Migrations) ms.Add(Q(mg));
                sb.Append("  \"migrations\": [").Append(string.Join(", ", ms.ToArray())).Append("],\n");
            }
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

            Backup();

            // Пишем через временный файл, чтобы падение на середине не убило конфиг.
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));

            if (!File.Exists(FilePath))
            {
                File.Move(tmp, FilePath);
                return;
            }

            // Replace подменяет файл одной операцией. Прежние Delete + Move
            // оставляли окно, в котором конфига на диске не было вовсе.
            try
            {
                File.Replace(tmp, FilePath, null);
            }
            catch (PlatformNotSupportedException)
            {
                File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
        }

        /// <summary>
        /// Копия текущего конфига в backups — не чаще раза в полчаса и при первой
        /// записи после запуска. Хранятся последние десять. Ошибки молчат:
        /// бэкап не должен мешать самому сохранению.
        /// </summary>
        static void Backup()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                if (DateTime.Now - _lastBackup < BackupEvery) return;

                Directory.CreateDirectory(BackupDir);
                string dest = Path.Combine(BackupDir, "config-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
                File.Copy(FilePath, dest, true);
                _lastBackup = DateTime.Now;

                var old = new DirectoryInfo(BackupDir).GetFiles("config-*.json");
                Array.Sort(old, (a, b) => string.CompareOrdinal(b.Name, a.Name));   // новые первыми
                for (int i = BackupsKept; i < old.Length; i++)
                {
                    try { old[i].Delete(); } catch { }
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Отпечаток макроса — его JSON. Движок по нему понимает, что макрос не
        /// менялся, и оставляет ему прежний проигрыватель вместо пересоздания.
        /// </summary>
        public static string Fingerprint(MacroDef m)
        {
            var sb = new StringBuilder(512);
            WriteMacro(sb, m);
            return sb.ToString();
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
                        if (a.JumpVk != 0x20) sb.Append(", \"jumpVk\": ").Append(a.JumpVk);
                        sb.Append(", \"respectHeldMove\": ").Append(a.RespectHeldMove ? "true" : "false");
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
