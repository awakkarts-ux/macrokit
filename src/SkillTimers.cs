using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace MacroKit
{
    /// <summary>
    /// Когда умение последний раз прошло в игре — по Chat.log.
    ///
    /// Свои умения Aion пишет так (время до секунды, ранг римскими цифрами):
    ///   2026.11.14 07:06:10 : Внезапный выстрел IV: Тренировочная кукла получает 3 369 ед. урона.
    ///   2026.11.14 07:06:23 : Критический удар! Внезапный выстрел IV: Тренировочная кукла оглушается.
    /// Строка появляется, когда сервер подтвердил попадание, — значит, умение
    /// точно ушло в кд. Нажатие в стане или вне дистанции строки не даёт.
    ///
    /// Учёт общий для всей программы: умение, нажатое рукой, тоже уходит в кд,
    /// и серия его не жмёт впустую. Время берётся по появлению строки в файле
    /// (ChatLog опрашивает его раз в 2 мс): оно чуть позже нажатия, то есть
    /// кд кончается для программы на долю секунды позже, чем в игре, — в
    /// безопасную сторону.
    /// </summary>
    public static class SkillTimers
    {
        sealed class Watch
        {
            public string Key;
            public Regex Rx;
        }

        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Dictionary<string, long> Seen = new Dictionary<string, long>();
        static readonly object Gate = new object();
        static Watch[] _watches = new Watch[0];

        /// <summary>Чем игра отвечает на умение, которое не прошло.</summary>
        public static readonly Regex Failure = new Regex(
            @"^[\d.]+ [\d:]+ : (Цель находится слишком далеко|Нет цели|Неверная цель|Вы не можете использовать|Нельзя использовать)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Текущее время учёта, мс.</summary>
        public static long Now { get { return Clock.ElapsedMilliseconds; } }

        /// <summary>Ключ учёта — название без регистра и лишних пробелов.</summary>
        public static string KeyOf(string name)
        {
            return Regex.Replace((name ?? "").Trim(), @"\s+", " ").ToLowerInvariant();
        }

        /// <summary>
        /// Шаблон строки о применении умения: после времени — по желанию
        /// «Критический удар! », затем название, ранг и двоеточие. Привязка к
        /// началу строки отсекает упоминания в чате и строки «умение … закончилось».
        /// </summary>
        public static Regex PatternFor(string name)
        {
            string n = Regex.Escape(Regex.Replace((name ?? "").Trim(), @"\s+", " ")).Replace(@"\ ", @"\s+");
            return new Regex(@"^[\d.]+ [\d:]+ : (Критический удар! )?" + n + @"( [IVXL]+)?:",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        /// <summary>Какие умения отслеживать: названия из всех серий активного профиля.</summary>
        public static void WatchNames(IEnumerable<string> names)
        {
            var list = new List<Watch>();
            var keys = new HashSet<string>();
            foreach (var n in names)
            {
                string k = KeyOf(n);
                if (k.Length == 0 || !keys.Add(k)) continue;
                list.Add(new Watch { Key = k, Rx = PatternFor(n) });
            }
            Volatile.Write(ref _watches, list.ToArray());
        }

        /// <summary>Строка лога — из потока ChatLog.</summary>
        public static void OnLine(string line)
        {
            var w = Volatile.Read(ref _watches);
            if (w.Length == 0) return;
            long now = Now;
            for (int i = 0; i < w.Length; i++)
                if (w[i].Rx.IsMatch(line)) Mark(w[i].Key, now);
        }

        public static void Mark(string key, long at)
        {
            lock (Gate) Seen[key] = at;
        }

        /// <summary>Когда умение прошло последний раз, мс по Now; -1 — не видели.</summary>
        public static long LastSeen(string key)
        {
            long t;
            lock (Gate) return Seen.TryGetValue(key, out t) ? t : -1;
        }

        public static void Forget(string key)
        {
            lock (Gate) Seen.Remove(key);
        }

        public static void Clear()
        {
            lock (Gate) Seen.Clear();
        }
    }
}
