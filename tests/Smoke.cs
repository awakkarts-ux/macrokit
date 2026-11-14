using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MacroKit.Tests
{
    /// <summary>
    /// Проверка движка без участия человека.
    ///
    /// Хитрость: ставим собственный хук, который ЛОВИТ И ГЛУШИТ наши же инъекции.
    /// Благодаря этому тест видит ровно то, что макрос отправляет в систему,
    /// но при этом ни одна клавиша не долетает ни до одного приложения —
    /// запуск теста безопасен и ничего никуда не печатает.
    /// </summary>
    public static class SmokeTest
    {
        class Ev
        {
            public int Vk;
            public bool Down;
            public double Ms;
        }

        static readonly List<Ev> Log = new List<Ev>();
        static readonly object Gate = new object();
        static Stopwatch Clock;

        static int _failures;

        public static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Не Windows (Mono на Linux): ни хука, ни SendInput. События ловим
            // подменой отправки — так логика слайдов проверяется и без Windows.
            // Тайминги там грубее, а клавиатуры нет вовсе: KeyState.AsyncDown — «ничего не зажато».
            bool sinkMode = Environment.OSVersion.Platform != PlatformID.Win32NT;

            KeyboardHook hook = null;
            if (sinkMode)
            {
                Console.WriteLine("(не Windows: события ловятся подменой InputSender.Sink, без хука)");
                KeyState.AsyncDown = vk => false;
                InputSender.Sink = (vk, down) =>
                {
                    lock (Gate)
                        Log.Add(new Ev { Vk = vk, Down = down, Ms = Clock == null ? 0 : Clock.Elapsed.TotalMilliseconds });
                };
            }
            else
            {
                hook = new KeyboardHook((vk, down, injected) =>
                {
                    if (!injected) return false;

                    // Зонд сторожа хука — служебный, в ленту не попадает
                    if (vk == 0xE8) return true;

                    lock (Gate)
                        Log.Add(new Ev { Vk = vk, Down = down, Ms = Clock == null ? 0 : Clock.Elapsed.TotalMilliseconds });

                    return true;   // глушим — наружу ничего не уходит
                });

                if (!hook.Start())
                {
                    Console.WriteLine("НЕ УДАЛОСЬ поставить хук (код " + hook.LastError + ")");
                    return 2;
                }
            }

            TimerResolution.Acquire();

            try
            {
                TestSequenceAndTiming();
                TestParallelMacros();
                TestNoStuckKeys();
                TestRepeatCount();
                TestMacImport();
                TestSlide();
                TestAirSlide();
                TestMultiKey();
                TestWaitLog();
                TestConflicts();
                TestHeldMove();
                TestJumpKey();
                TestRunnerReuse();
                TestMigrationAndInitials();
                TestJournal();
                TestModifierTriggers();
                TestSeries();
            }
            finally
            {
                TimerResolution.Release();
                if (hook != null) hook.Dispose();
            }

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" : ("ПРОВАЛЕНО ПРОВЕРОК: " + _failures));
            return _failures == 0 ? 0 : 1;
        }

        // ---------- проверки ----------

        static void TestSequenceAndTiming()
        {
            Head("Последовательность и точность задержек");

            var def = new MacroDef { Name = "seq", Repeat = RepeatMode.Once };
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x41, Ms = 30 });  // A
            def.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 100 });
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x42, Ms = 30 });  // B
            def.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 100 });
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x43, Ms = 30 });  // C

            var events = RunAndCollect(def, 3000);

            string got = Describe(events);
            const string want = "A↓ A↑ B↓ B↑ C↓ C↑";
            Check("порядок событий", got == want, want, got);

            if (events.Count == 6)
            {
                double[] expected = { 0, 30, 130, 160, 260, 290 };
                double worst = 0;

                for (int i = 0; i < 6; i++)
                {
                    double drift = Math.Abs((events[i].Ms - events[0].Ms) - expected[i]);
                    if (drift > worst) worst = drift;
                }

                Console.WriteLine("    тайминги: " + Times(events));
                Check("максимальное отклонение < 20 мс", worst < 20,
                    "< 20 мс", worst.ToString("F1") + " мс");
            }
        }

        static void TestParallelMacros()
        {
            Head("Два макроса играют одновременно, не блокируя друг друга");

            var a = new MacroDef { Name = "a", Repeat = RepeatMode.Once };
            var b = new MacroDef { Name = "b", Repeat = RepeatMode.Once };

            for (int i = 0; i < 4; i++)
            {
                a.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x31, Ms = 5 });  // 1
                a.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 60 });
                b.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x32, Ms = 5 });  // 2
                b.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 60 });
            }

            ResetLog();

            var ra = new MacroRunner(a);
            var rb = new MacroRunner(b);

            var sw = Stopwatch.StartNew();
            ra.Trigger();
            rb.Trigger();

            WaitIdle(new[] { ra, rb }, 4000);
            sw.Stop();

            List<Ev> events;
            lock (Gate) events = new List<Ev>(Log);

            ra.Dispose();
            rb.Dispose();

            int ones = events.FindAll(e => e.Vk == 0x31 && e.Down).Count;
            int twos = events.FindAll(e => e.Vk == 0x32 && e.Down).Count;

            Check("первый макрос отыграл все 4 шага", ones == 4, "4", ones.ToString());
            Check("второй макрос отыграл все 4 шага", twos == 4, "4", twos.ToString());

            // Если бы макросы стояли в очереди друг за другом, вышло бы ~520 мс вместо ~260
            Check("шли параллельно, а не по очереди", sw.ElapsedMilliseconds < 400,
                "< 400 мс", sw.ElapsedMilliseconds + " мс");

            bool interleaved = false;
            for (int i = 1; i < events.Count; i++)
                if (events[i].Vk != events[i - 1].Vk) { interleaved = true; break; }

            Check("события перемешаны во времени", interleaved, "да", interleaved ? "да" : "нет");
        }

        static void TestNoStuckKeys()
        {
            Head("Прерванный макрос не оставляет залипших клавиш");

            var def = new MacroDef { Name = "stuck", Repeat = RepeatMode.Once };
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyDown, Vk = 0x5A });   // Z зажали
            def.Actions.Add(new MacroAction { Kind = ActionKind.Delay, Ms = 5000 });     // и надолго ушли
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyUp, Vk = 0x5A });

            ResetLog();

            var r = new MacroRunner(def);
            r.Trigger();

            Thread.Sleep(250);
            r.StopNow();                      // обрываем на середине
            WaitIdle(new[] { r }, 2000);
            Thread.Sleep(100);

            List<Ev> events;
            lock (Gate) events = new List<Ev>(Log);
            r.Dispose();

            bool down = events.Exists(e => e.Vk == 0x5A && e.Down);
            bool up = events.Exists(e => e.Vk == 0x5A && !e.Down);

            Check("клавиша была зажата", down, "да", down ? "да" : "нет");
            Check("и отпущена при остановке", up, "да", up ? "да" : "нет");
        }

        static void TestRepeatCount()
        {
            Head("Режим «заданное число повторов»");

            var def = new MacroDef
            {
                Name = "count",
                Repeat = RepeatMode.Count,
                RepeatCount = 3,
                GapMs = 20
            };
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x51, Ms = 5 });  // Q

            var events = RunAndCollect(def, 3000);

            int presses = events.FindAll(e => e.Vk == 0x51 && e.Down).Count;
            Check("ровно 3 повтора", presses == 3, "3", presses.ToString());
        }

        /// <summary>
        /// Составное действие «слайд» должно давать ровно ту же ленту событий,
        /// что и семь примитивов, написанных руками. Эталон — slide_kite_4:
        /// 4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑, W зажат 420 мс, всего 440 мс.
        /// </summary>
        static void TestSlide()
        {
            Head("Составное действие «слайд»");

            var def = new MacroDef { Name = "slide", Repeat = RepeatMode.Once };
            def.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Vk = 0x34,        // 4
                Ms = 0,
                CastMs = 20,
                MoveVk = 0x57,    // W
                RunMs = 420,
                Jump = true,
                JumpAfterMs = 20
            });

            var events = RunAndCollect(def, 3000);

            Check("порядок событий", Describe(events) == "4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑",
                  "4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑", Describe(events));

            Console.WriteLine("    тайминги: " + Times(events));

            if (events.Count == 6)
            {
                double total = events[5].Ms - events[0].Ms;
                double wHeld = events[5].Ms - events[2].Ms;
                Check("общая длительность ~440 мс", Math.Abs(total - 440) < 25, "440 мс", Math.Round(total) + " мс");
                Check("W удерживается ~420 мс", Math.Abs(wHeld - 420) < 25, "420 мс", Math.Round(wHeld) + " мс");
            }

            // Разбег: движение должно начаться ДО скилла и не прерываться
            var pre = new MacroDef { Name = "slide-prerun", Repeat = RepeatMode.Once };
            pre.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Vk = 0x34,
                Ms = 15,
                PreRunMs = 80,
                CastMs = 20,
                MoveVk = 0x57,
                RunMs = 300,
                Jump = true,
                JumpAfterMs = 20
            });

            var ev3 = RunAndCollect(pre, 3000);
            Check("с разбегом W идёт первым", Describe(ev3) == "W↓ 4↓ 4↑ Пробел↓ Пробел↑ W↑",
                  "W↓ 4↓ 4↑ Пробел↓ Пробел↑ W↑", Describe(ev3));

            if (ev3.Count == 6)
            {
                double skillAt = ev3[1].Ms - ev3[0].Ms;
                double wHeld3 = ev3[5].Ms - ev3[0].Ms;
                Check("скилл через ~80 мс после старта бега", Math.Abs(skillAt - 80) < 25, "80 мс", Math.Round(skillAt) + " мс");
                Check("W зажат всё время ~415 мс", Math.Abs(wHeld3 - 415) < 30, "415 мс", Math.Round(wHeld3) + " мс");
            }

            // Модификатор скилла: Alt должен обнять нажатие клавиши и отпуститься
            var withAlt = new MacroDef { Name = "slide-alt", Repeat = RepeatMode.Once };
            withAlt.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Vk = 0x31,        // 1
                Ms = 15,
                SkillAlt = true,
                CastMs = 20,
                MoveVk = 0x57,
                RunMs = 60,
                Jump = false
            });

            var ev2 = RunAndCollect(withAlt, 2000);
            Check("скилл на Alt: Alt обнимает нажатие", Describe(ev2) == "Alt↓ 1↓ 1↑ Alt↑ W↓ W↑",
                  "Alt↓ 1↓ 1↑ Alt↑ W↓ W↑", Describe(ev2));

            // Alt должен держаться дольше кадра игры, иначе она видит голую «1»
            if (ev2.Count == 6)
            {
                double lead = ev2[1].Ms - ev2[0].Ms;
                double tail = ev2[3].Ms - ev2[2].Ms;
                Console.WriteLine("    Alt до скилла " + lead.ToString("F1") + " мс, после " + tail.ToString("F1") + " мс");
                Check("Alt зажат за ~20 мс до скилла", lead >= MacroRunner.ModLeadMs - 2, ">= 18 мс", lead.ToString("F1") + " мс");
                Check("и отпущен через ~20 мс после", tail >= MacroRunner.ModLeadMs - 2, ">= 18 мс", tail.ToString("F1") + " мс");
            }

            // Автоатака перед скиллом — так слайд с места работает в бою
            var withAuto = new MacroDef { Name = "slide-auto", Repeat = RepeatMode.Once };
            withAuto.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Vk = 0x34,
                Ms = 0,
                AutoVk = 0x43,    // C
                AutoGapMs = 30,
                CastMs = 20,
                MoveVk = 0x57,
                RunMs = 100,
                Jump = true,
                JumpAfterMs = 20
            });

            var ev4 = RunAndCollect(withAuto, 2000);
            const string want4 = "C↓ C↑ 4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑";
            Check("с автоатакой: C, потом скилл", Describe(ev4) == want4, want4, Describe(ev4));
            if (ev4.Count == 8)
            {
                double gap = ev4[2].Ms - ev4[0].Ms;
                Check("скилл через ~30 мс после автоатаки", Math.Abs(gap - 30) < 15, "30 мс", Math.Round(gap) + " мс");
            }

            // Второй вид слайда: скилл дважды, сразу бег с прыжком
            var dbl = new MacroDef { Name = "slide-double", Repeat = RepeatMode.Once };
            dbl.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Vk = 0x32,        // 2
                Ms = 0,
                SkillTaps = 2,
                TapGapMs = 40,
                CastMs = 0,
                MoveVk = 0x57,
                RunMs = 100,
                Jump = true,
                JumpAfterMs = 0
            });

            var ev5 = RunAndCollect(dbl, 2000);
            const string want5 = "2↓ 2↑ 2↓ 2↑ W↓ Пробел↓ Пробел↑ W↑";
            Check("двойное нажатие, сразу прыжок", Describe(ev5) == want5, want5, Describe(ev5));
            if (ev5.Count == 8)
            {
                double gap = ev5[2].Ms - ev5[0].Ms;
                Check("между нажатиями ~40 мс", Math.Abs(gap - 40) < 15, "40 мс", Math.Round(gap) + " мс");
            }

            dbl.Actions[0].SkillAlt = true;
            var ev6 = RunAndCollect(dbl, 2000);
            const string want6 = "Alt↓ 2↓ 2↑ Alt↑ Alt↓ 2↓ 2↑ Alt↑ W↓ Пробел↓ Пробел↑ W↑";
            Check("двойное нажатие Alt-скилла — Alt на каждом", Describe(ev6) == want6, want6, Describe(ev6));

            // Случай «Alt уже зажат физически» здесь не проверить: хук теста
            // глушит все инъекции, поэтому подделать зажатую клавишу нечем —
            // GetAsyncKeyState её просто не увидит. Проверяется только в игре.
        }

        /// <summary>
        /// Слайд в прыжке: прыжок, пауза до верхней точки, автоатака, скилл,
        /// ожидание приземления. Движения в воздухе быть не должно.
        /// </summary>
        static void TestAirSlide()
        {
            Head("Слайд в прыжке");

            var def = new MacroDef { Name = "air", Repeat = RepeatMode.Once };
            def.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Mode = SlideMode.Air,
                Vk = 0x32,        // 2
                Ms = 0,
                AutoVk = 0x43,    // C
                ApexMs = 200,
                AutoGapMs = 20,
                LandMs = 150,
                RunMs = 0
            });

            var events = RunAndCollect(def, 3000);
            const string want = "Пробел↓ Пробел↑ C↓ C↑ 2↓ 2↑";
            Check("порядок событий", Describe(events) == want, want, Describe(events));
            Console.WriteLine("    тайминги: " + Times(events));

            if (events.Count == 6)
            {
                double autoAt = events[2].Ms - events[1].Ms;
                double skillAt = events[4].Ms - events[2].Ms;
                Check("автоатака через ~200 мс после прыжка", Math.Abs(autoAt - 200) < 25, "200 мс", Math.Round(autoAt) + " мс");
                Check("скилл через ~20 мс после автоатаки", Math.Abs(skillAt - 20) < 15, "20 мс", Math.Round(skillAt) + " мс");
            }

            // С разбегом и бегом после приземления; скилл на Alt
            var run = new MacroDef { Name = "air-run", Repeat = RepeatMode.Once };
            run.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Mode = SlideMode.Air,
                Vk = 0x45,        // E
                Ms = 0,
                SkillAlt = true,
                AutoVk = 0,
                PreRunMs = 60,
                ApexMs = 100,
                LandMs = 100,
                RunMs = 80
            });

            var ev2 = RunAndCollect(run, 3000);
            const string want2 = "W↓ Пробел↓ Пробел↑ W↑ Alt↓ E↓ E↑ Alt↑ W↓ W↑";
            Check("разбег, W отпущен в воздухе, Alt-скилл, бег после", Describe(ev2) == want2, want2, Describe(ev2));
        }

        /// <summary>
        /// «Ждать строку в логе»: макрос стоит, пока в файл не допишут строку
        /// по шаблону, и останавливается, если её так и не дописали.
        /// Файл — временный, в той же кодировке и с теми же разделителями, что Chat.log.
        /// </summary>
        static void TestWaitLog()
        {
            Head("Ждать строку в Chat.log");

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mk-smoke-log");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "Chat.log");
            var cp1251 = System.Text.Encoding.GetEncoding(1251);
            System.IO.File.WriteAllText(path, "2026.10.09 04:00:00 : Вы нанесли 1 ед. урона цели Старое.\r\n", cp1251);

            ChatLog.JournalPath = System.IO.Path.Combine(dir, "timing.txt");
            ChatLog.Use(path);
            Thread.Sleep(100);   // наблюдатель встал в конец файла

            try
            {
                var def = new MacroDef { Name = "по логу", Repeat = RepeatMode.Once };
                def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x43, Ms = 0 });   // C
                def.Actions.Add(new MacroAction { Kind = ActionKind.WaitLog, Pattern = "Вы нанесли", Ms = 1000 });
                def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x34, Ms = 0 });   // 4

                // Через 150 мс «игра» дописывает сначала чужой удар, потом свой
                var writer = new Thread(() =>
                {
                    Thread.Sleep(150);
                    System.IO.File.AppendAllText(path, "2026.10.09 04:00:01 : Стражник наносит 5 ед. урона цели Кукла.\r\n", cp1251);
                    Thread.Sleep(50);
                    System.IO.File.AppendAllText(path, "2026.10.09 04:00:01 : Вы нанесли 1 022 ед. урона цели Кукла.\r\n", cp1251);
                });
                writer.Start();

                var events = RunAndCollect(def, 3000);
                writer.Join();

                Check("C, затем 4 после строки", Describe(events) == "C↓ C↑ 4↓ 4↑", "C↓ C↑ 4↓ 4↑", Describe(events));
                if (events.Count == 4)
                {
                    double gap = events[2].Ms - events[0].Ms;
                    Console.WriteLine("    скилл через " + gap.ToString("F0") + " мс после C (строка дописана через ~200)");
                    Check("ждал именно свою строку, а не чужую и не старую", gap > 180 && gap < 300, "180–300 мс", gap.ToString("F0") + " мс");
                }

                // Строка не пришла — дальше макрос не идёт
                var miss = new MacroDef { Name = "мимо", Repeat = RepeatMode.Once };
                miss.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x43, Ms = 0 });
                miss.Actions.Add(new MacroAction { Kind = ActionKind.WaitLog, Pattern = "Вы нанесли", Ms = 200 });
                miss.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x34, Ms = 0 });

                var ev2 = RunAndCollect(miss, 2000);
                Check("нет строки — скилла нет", Describe(ev2) == "C↓ C↑", "C↓ C↑", Describe(ev2));
            }
            finally
            {
                ChatLog.Use(null);
            }
        }

        /// <summary>
        /// Один макрос на ряд клавиш: раскрытие в копию на каждую клавишу,
        /// подмена заглушки «клавиша триггера» и раздельный учёт для интерфейса.
        /// </summary>
        static void TestMultiKey()
        {
            Head("Макрос на ряд клавиш");

            var def = new MacroDef { Name = "спам", TriggerVk = 0x32, Repeat = RepeatMode.Once };
            def.ExtraKeys.Add(0x33);
            def.ExtraKeys.Add(0x52);
            def.ExtraKeys.Add(0x32);   // повтор — не в счёт
            def.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = MacroAction.TriggerKey, Ms = 5 });

            Check("все клавиши без повторов", def.AllKeys.Count == 3, "3", def.AllKeys.Count.ToString());

            var r = def.ExpandFor(0x52);
            Check("копия под R жмёт R", r.Actions[0].Vk == 0x52, "R", KeyNames.Get(r.Actions[0].Vk));
            Check("исходник не тронут", def.Actions[0].Vk == MacroAction.TriggerKey, "заглушка", KeyNames.Get(def.Actions[0].Vk));
            Check("у копии тот же Id", r.Id == def.Id, "да", r.Id == def.Id ? "да" : "нет");

            var events = RunAndCollect(r, 2000);
            Check("в систему уходит R", Describe(events) == "R↓ R↑", "R↓ R↑", Describe(events));

            // Нераскрытая заглушка в систему уйти не должна
            var raw = new MacroDef { Name = "raw", Repeat = RepeatMode.Once };
            raw.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = MacroAction.TriggerKey, Ms = 5 });
            var ev2 = RunAndCollect(raw, 1000);
            Check("заглушка без раскрытия ничего не шлёт", ev2.Count == 0, "0 событий", ev2.Count + " событий");
        }

        /// <summary>
        /// Конфликт триггеров — это ровно тот случай, когда движок запустит два
        /// макроса разом. Различаться они должны хотя бы одним модификатором.
        /// </summary>
        static void TestConflicts()
        {
            Head("Определение конфликта триггеров");

            var prof = new Profile { Name = "тест" };

            var spam = new MacroDef { Name = "спам", TriggerVk = 0x34, Enabled = true };
            var slide = new MacroDef { Name = "слайд", TriggerVk = 0x34, Enabled = true };
            prof.Macros.Add(spam);
            prof.Macros.Add(slide);

            Check("одинаковый триггер — конфликт", prof.HasTriggerConflict(spam), "да",
                  prof.HasTriggerConflict(spam) ? "да" : "нет");

            slide.Mouse5 = true;
            Check("развели боковой кнопкой — конфликта нет", !prof.HasTriggerConflict(spam), "нет",
                  prof.HasTriggerConflict(spam) ? "да" : "нет");

            slide.Mouse5 = false;
            slide.Enabled = false;
            Check("выключенный не мешает", !prof.HasTriggerConflict(spam), "нет",
                  prof.HasTriggerConflict(spam) ? "да" : "нет");

            slide.Enabled = true;
            slide.Shift = true;
            Check("Shift тоже разводит", !prof.HasTriggerConflict(spam), "нет",
                  prof.HasTriggerConflict(spam) ? "да" : "нет");

            var noTrigger1 = new MacroDef { Name = "без триггера 1", TriggerVk = 0 };
            var noTrigger2 = new MacroDef { Name = "без триггера 2", TriggerVk = 0 };
            prof.Macros.Add(noTrigger1);
            prof.Macros.Add(noTrigger2);
            Check("без триггера не конфликтуют", !prof.HasTriggerConflict(noTrigger1), "нет",
                  prof.HasTriggerConflict(noTrigger1) ? "да" : "нет");

            // Ряды клавиш: конфликт, если ряды пересекаются при тех же модификаторах и слое
            var rowA = new MacroDef { Name = "ряд A", TriggerVk = 0x31, Enabled = true };
            rowA.ExtraKeys.Add(0x35);
            var rowB = new MacroDef { Name = "ряд B", TriggerVk = 0x36, Enabled = true };
            rowB.ExtraKeys.Add(0x35);
            var p2 = new Profile { Name = "ряды" };
            p2.Macros.Add(rowA);
            p2.Macros.Add(rowB);
            Check("пересечение рядов — конфликт", p2.HasTriggerConflict(rowA), "да",
                  p2.HasTriggerConflict(rowA) ? "да" : "нет");

            rowB.LayerVk = 0x14;   // CapsLock
            Check("слой разводит ряды", !p2.HasTriggerConflict(rowA), "нет",
                  p2.HasTriggerConflict(rowA) ? "да" : "нет");
        }

        static void TestMacImport()
        {
            Head("Импорт .mac из родного софта");

            string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string path = System.IO.Path.Combine(dir, "slide_kite_4.mac");

            if (!System.IO.File.Exists(path))
            {
                Console.WriteLine("    пропущено: файл не найден — " + path);
                return;
            }

            var res = MacImport.Load(path);

            if (res.Macro == null)
            {
                Check("файл разобран", false, "макрос", res.Error);
                return;
            }

            Check("имя взято из файла", res.Macro.Name == "slide_kite_4", "slide_kite_4", res.Macro.Name);

            var steps = new List<string>();
            foreach (var a in res.Macro.Actions) steps.Add(a.Title + ":" + a.Kind + ":" + a.Ms);

            Console.WriteLine("    шаги: " + string.Join(" | ", steps.ToArray()));

            // HID 33 -> «4», 26 -> W, 44 -> Пробел
            bool keysOk = res.Macro.Actions.Exists(a => a.Vk == 0x34)
                       && res.Macro.Actions.Exists(a => a.Vk == 0x57)
                       && res.Macro.Actions.Exists(a => a.Vk == 0x20);

            Check("HID-коды распознаны как 4 / W / Пробел", keysOk, "да", keysOk ? "да" : "нет");

            // Прогоняем через движок и смотрим, что реально уходит в систему
            var events = RunAndCollect(res.Macro, 3000);

            string got = Describe(events);
            const string want = "4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑";
            Check("порядок событий на выходе", got == want, want, got);

            if (events.Count == 6)
            {
                double t0 = events[0].Ms;
                double wHold = events[5].Ms - events[2].Ms;
                double spaceHold = events[4].Ms - events[3].Ms;
                double total = events[5].Ms - t0;

                Console.WriteLine("    тайминги: " + Times(events));
                Console.WriteLine("    W зажат " + wHold.ToString("F0") + " мс, пробел " + spaceHold.ToString("F0") + " мс");

                Check("общая длительность ~440 мс", Math.Abs(total - 440) < 20, "440 ± 20 мс", total.ToString("F0") + " мс");
                Check("W удерживается ~420 мс", Math.Abs(wHold - 420) < 20, "420 ± 20 мс", wHold.ToString("F0") + " мс");
            }
        }

        /// <summary>
        /// W под пальцем (MacroAction.RespectHeldMove). Палец имитируется через
        /// KeyState.OnPhysical — так же его видит движок из хука.
        /// </summary>
        static void TestHeldMove()
        {
            Head("W, зажатая рукой (кайт)");

            const int W = 0x57;

            // 1. Джампшот на бегу: W отпускается в воздухе и возвращается после
            KeyState.ResetPhysical();
            KeyState.OnPhysical(W, true);
            var air = SlideDef(SlideMode.Air, true);
            air.Actions[0].RunMs = 0;
            var ev1 = RunAndCollect(air, 3000);
            const string want1 = "Пробел↓ Пробел↑ W↑ 4↓ 4↑ W↓";
            Check("в прыжке: W отпущена в воздухе и возвращена под палец", Describe(ev1) == want1, want1, Describe(ev1));
            Check("после слайда W снова у пальца", !KeyState.IsOwned(W), "не занята", KeyState.IsOwned(W) ? "занята" : "не занята");

            // 2. Тот же слайд со старым поведением: W пальца не трогается — так было
            var oldAir = SlideDef(SlideMode.Air, false);
            oldAir.Actions[0].RunMs = 0;
            var ev2 = RunAndCollect(oldAir, 3000);
            const string want2 = "Пробел↓ Пробел↑ 4↓ 4↑";
            Check("старые слайды ведут себя по-прежнему", Describe(ev2) == want2, want2, Describe(ev2));

            // 3. С места на бегу: остановка, слайд, и бег продолжается без W↑ в конце
            var stand = SlideDef(SlideMode.Standing, true);
            var ev3 = RunAndCollect(stand, 3000);
            const string want3 = "W↑ 4↓ 4↑ W↓ Пробел↓ Пробел↑";
            Check("с места на бегу: стоп, скилл, бег дальше", Describe(ev3) == want3, want3, Describe(ev3));

            // 4. Палец отпустил W посреди слайда — в конце W отпущена
            ResetLog();
            var r4 = new MacroRunner(SlideDef(SlideMode.Standing, true));
            r4.Trigger();
            Thread.Sleep(150);
            KeyState.OnPhysical(W, false);
            WaitIdle(new[] { r4 }, 3000);
            Thread.Sleep(80);
            List<Ev> ev4;
            lock (Gate) ev4 = new List<Ev>(Log);
            r4.Dispose();
            const string want4 = "W↑ 4↓ 4↑ W↓ Пробел↓ Пробел↑ W↑";
            Check("палец отпустил W во время слайда — W отпущена", Describe(ev4) == want4, want4, Describe(ev4));

            // 5. Слайд оборван посреди бега, палец держит W — W остаётся зажатой
            KeyState.OnPhysical(W, true);
            ResetLog();
            var r5 = new MacroRunner(SlideDef(SlideMode.Standing, true));
            r5.Trigger();
            Thread.Sleep(150);
            r5.StopNow();
            WaitIdle(new[] { r5 }, 3000);
            Thread.Sleep(80);
            List<Ev> ev5;
            lock (Gate) ev5 = new List<Ev>(Log);
            r5.Dispose();
            // Важно итоговое состояние W, а не порядок: последнее событие W — нажатие
            bool wDown = false;
            foreach (var e in ev5) if (e.Vk == W) wDown = e.Down;
            Check("обрыв на бегу — W остаётся у пальца зажатой", wDown, "W в итоге зажата", Describe(ev5));
            Check("обрыв отдал владение W", !KeyState.IsOwned(W), "не занята", KeyState.IsOwned(W) ? "занята" : "не занята");

            KeyState.ResetPhysical();
        }

        static MacroDef SlideDef(SlideMode mode, bool respect)
        {
            var def = new MacroDef { Name = "slide", Repeat = RepeatMode.Once };
            def.Actions.Add(new MacroAction
            {
                Kind = ActionKind.Slide,
                Mode = mode,
                Vk = 0x34,
                Ms = 0,
                CastMs = 20,
                MoveVk = 0x57,
                RunMs = 300,
                Jump = true,
                JumpAfterMs = 20,
                ApexMs = 100,
                LandMs = 100,
                RespectHeldMove = respect
            });
            return def;
        }

        static void TestJumpKey()
        {
            Head("Своя клавиша прыжка");

            KeyState.ResetPhysical();
            var def = SlideDef(SlideMode.Standing, true);
            def.Actions[0].JumpVk = 0x56;   // V
            var ev = RunAndCollect(def, 3000);
            const string want = "4↓ 4↑ W↓ V↓ V↑ W↑";
            Check("прыжок на V", Describe(ev) == want, want, Describe(ev));
        }

        /// <summary>
        /// Пересборка привязок не пересоздаёт неизменённые макросы: раньше любая
        /// галочка в окне пересоздавала все потоки и обрывала то, что играло.
        /// </summary>
        static void TestRunnerReuse()
        {
            Head("Пересборка без пересоздания");

            var cfg = new AppConfig();
            var p = new Profile { Name = "т" };
            cfg.Profiles.Add(p);
            cfg.ActiveProfileId = p.Id;

            var a = new MacroDef { Name = "a", TriggerVk = 0x31 };
            a.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x31, Ms = 5 });
            var b = new MacroDef { Name = "b", TriggerVk = 0x32 };
            b.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x32, Ms = 5 });
            p.Macros.Add(a);
            p.Macros.Add(b);

            using (var engine = new MacroEngine())
            {
                engine.Apply(cfg);
                var before = engine.Runners;

                b.Actions[0].Ms = 50;   // правим только b
                engine.Apply(cfg);
                var after = engine.Runners;

                Check("проигрывателей столько же", before.Length == 2 && after.Length == 2, "2 и 2", before.Length + " и " + after.Length);
                if (before.Length == 2 && after.Length == 2)
                {
                    Check("у неизменённого макроса тот же проигрыватель", ReferenceEquals(before[0], after[0]), "тот же", ReferenceEquals(before[0], after[0]) ? "тот же" : "новый");
                    Check("у изменённого — новый", !ReferenceEquals(before[1], after[1]), "новый", ReferenceEquals(before[1], after[1]) ? "тот же" : "новый");
                }
            }
        }

        static void TestMigrationAndInitials()
        {
            Head("Новый профиль для слайдов и значки профилей");

            var cfg = new AppConfig();
            var p = new Profile { Name = "Лучник · слайды" };
            var m = new MacroDef { Name = "слайд", TriggerVk = 0x34 };
            m.Actions.Add(new MacroAction { Kind = ActionKind.Slide, Vk = 0x34, RespectHeldMove = false });
            p.Macros.Add(m);
            var plain = new Profile { Name = "Основной" };
            plain.Macros.Add(new MacroDef { Name = "спам", TriggerVk = 0x31 });
            cfg.Profiles.Add(p);
            cfg.Profiles.Add(plain);
            cfg.ActiveProfileId = p.Id;
            cfg.Migrations.Add(ConfigStore.MigrationSeries);   // серии проверяются отдельно, в TestSeries

            ConfigStore.Migrate(cfg);

            Check("появился ровно один новый профиль", cfg.Profiles.Count == 3, "3 профиля", cfg.Profiles.Count + " профиля");
            if (cfg.Profiles.Count == 3)
            {
                var copy = cfg.Profiles[1];
                Check("он стоит сразу за исходным", copy.Name == "Лучник · слайды · W рукой", "Лучник · слайды · W рукой", copy.Name);
                Check("в копии новое поведение", copy.Macros[0].Actions[0].RespectHeldMove, "да", copy.Macros[0].Actions[0].RespectHeldMove ? "да" : "нет");
                Check("исходный не тронут", !p.Macros[0].Actions[0].RespectHeldMove, "нет", p.Macros[0].Actions[0].RespectHeldMove ? "да" : "нет");
                Check("активный профиль прежний", cfg.Active == p, "прежний", cfg.Active == p ? "прежний" : cfg.Active.Name);
            }

            ConfigStore.Migrate(cfg);
            Check("повторно не срабатывает", cfg.Profiles.Count == 3, "3 профиля", cfg.Profiles.Count + " профиля");

            Check("значок «Лучник · слайды» — ЛС", p.Initials == "ЛС", "ЛС", p.Initials);
        }

        /// <summary>Журнал пишет все макросы: разовые — лентой, повторяющиеся — итогом, с причиной обрыва.</summary>
        static void TestJournal()
        {
            Head("Журнал макросов");

            var lines = new List<string>();
            Journal.Sink = (file, line) => { lock (lines) lines.Add(line); };
            MacroRunner.JournalOn = true;

            try
            {
                // 1. Разовый макрос из обычных шагов — лента целиком
                var once = new MacroDef { Name = "разовый", Repeat = RepeatMode.Once };
                once.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x41, Ms = 5 });
                RunAndCollect(once, 2000);
                string l1 = Last(lines);
                Check("разовый: лента событий", l1.StartsWith("разовый [один раз]:") && l1.Contains("A↓") && l1.Contains("A↑"), "разовый [один раз]: … A↓ … A↑", l1);

                // 2. Слайд оборван — причина и пометка слайда
                KeyState.ResetPhysical();
                ResetLog();
                var r2 = new MacroRunner(SlideDef(SlideMode.Standing, true));
                r2.Trigger();
                Thread.Sleep(120);
                r2.StopNow("начался другой слайд: тест");
                WaitIdle(new[] { r2 }, 3000);
                Thread.Sleep(50);
                r2.Dispose();
                string l2 = Last(lines);
                Check("обрыв: причина в журнале", l2.Contains("ПРЕРВАН (начался другой слайд: тест)"), "ПРЕРВАН (начался другой слайд: тест)", l2);
                Check("пометка слайда в ленте", l2.Contains("[слайд, W рукой]"), "[слайд, W рукой]", l2);

                // 3. Спам «пока держу» — одна строка-итог
                var spam = new MacroDef { Name = "спам", Repeat = RepeatMode.WhileHeld, GapMs = 20 };
                spam.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x32, Ms = 10 });
                int before;
                lock (lines) before = lines.Count;
                var r3 = new MacroRunner(spam);
                r3.Trigger();
                Thread.Sleep(300);
                r3.Release();
                WaitIdle(new[] { r3 }, 2000);
                Thread.Sleep(50);
                r3.Dispose();
                int added;
                lock (lines) added = lines.Count - before;
                string l3 = Last(lines);
                Check("спам: одна строка на весь проигрыш", added == 1, "1", added.ToString());
                Check("спам: итог проходов", l3.StartsWith("спам [пока держу]:") && l3.Contains("прох.") && !l3.Contains("2↓"), "спам [пока держу]: N прох. за …", l3);
            }
            finally
            {
                MacroRunner.JournalOn = false;
                Journal.Sink = null;
            }
        }

        /// <summary>
        /// Триггер на Shift/Alt, скилл на Alt. Палец и состояние системы
        /// имитируются: отправка идёт в подмену (на любой ОС ничего не уходит
        /// наружу), а «система» — массив, который обновляют и палец, и инъекции.
        /// </summary>
        static void TestModifierTriggers()
        {
            Head("Триггер с модификатором, скилл на Alt");

            var sys = new bool[256];
            var oldSink = InputSender.Sink;
            var oldAsync = KeyState.AsyncDown;
            var ev = new List<Ev>();
            Stopwatch clock = null;

            InputSender.Sink = (vk, down) =>
            {
                lock (ev) ev.Add(new Ev { Vk = vk, Down = down, Ms = clock == null ? 0 : clock.Elapsed.TotalMilliseconds });
                sys[vk] = down;
                // Как в Windows: общий Shift/Alt шлётся scan-кодом левого — меняются оба
                if (vk == 0x10) sys[0xA0] = down;
                if (vk == 0x12) sys[0xA4] = down;
            };
            KeyState.AsyncDown = vk =>
            {
                if (vk == 0x10) return sys[0x10] || sys[0xA0];
                if (vk == 0x12) return sys[0x12] || sys[0xA4];
                return sys[vk];
            };

            Func<MacroDef, List<Ev>> run = def =>
            {
                lock (ev) ev.Clear();
                clock = Stopwatch.StartNew();
                var r = new MacroRunner(def);
                r.Trigger();
                WaitIdle(new[] { r }, 3000);
                Thread.Sleep(50);
                r.Dispose();
                lock (ev) return new List<Ev>(ev);
            };

            Func<int, bool, MacroDef> slide = (vk, alt) =>
            {
                var d = new MacroDef { Name = "слайд", Repeat = RepeatMode.Once };
                d.Actions.Add(new MacroAction
                {
                    Kind = ActionKind.Slide, Vk = vk, SkillAlt = alt, Ms = 0, CastMs = 20,
                    MoveVk = 0x57, RunMs = 300, Jump = true, JumpAfterMs = 10, FreeMods = true
                });
                return d;
            };

            try
            {
                // 1. Триггер Shift+1, скилл Alt+1: Shift отпущен на время слайда и возвращён
                KeyState.ResetPhysical();
                Array.Clear(sys, 0, sys.Length);
                KeyState.OnPhysical(0xA0, true);
                sys[0xA0] = true;
                var e1 = run(slide(0x31, true));
                string d1 = Describe(e1);
                Check("Shift+1 → Alt+1: Shift отпущен, Alt-скилл, бег, Shift возвращён",
                      d1 == "Shift↑ Alt↓ 1↓ 1↑ Alt↑ W↓ Пробел↓ Пробел↑ W↑ Shift↓",
                      "Shift↑ Alt↓ 1↓ 1↑ Alt↑ W↓ Пробел↓ Пробел↑ W↑ Shift↓", d1);
                double gap1 = GapBetween(e1, 0x31, true, 0x57, true);
                Check("от скилла до W — как без модификатора (~20 мс)", gap1 > 15 && gap1 < 30, "~20 мс", gap1.ToString("F0") + " мс");

                // 2. Триггер Alt+2, скилл Alt+2: Alt пальца отпущен перед бегом и возвращён
                KeyState.ResetPhysical();
                Array.Clear(sys, 0, sys.Length);
                KeyState.OnPhysical(0xA4, true);
                sys[0xA4] = true;
                var e2 = run(slide(0x32, true));
                string d2 = Describe(e2);
                // Alt в конце не возвращается — см. MacroRunner.RestoreModifiers
                Check("Alt+2 → Alt+2: бег и прыжок без Alt",
                      d2 == "2↓ 2↑ Alt↑ W↓ Пробел↓ Пробел↑ W↑",
                      "2↓ 2↑ Alt↑ W↓ Пробел↓ Пробел↑ W↑", d2);
                double gap2 = GapBetween(e2, 0x32, true, 0x57, true);
                Check("Alt держится после скилла ~20 мс, W сразу за ним", gap2 > 15 && gap2 < 30, "~20 мс", gap2.ToString("F0") + " мс");

                // 3. Сочетание сравнивается по пальцу: Shift в системе отпущен
                //    (его отпустил прошлый слайд), но палец держит — Shift+1 подходит
                KeyState.ResetPhysical();
                Array.Clear(sys, 0, sys.Length);
                var cfg = new AppConfig();
                var p = new Profile { Name = "т" };
                cfg.Profiles.Add(p);
                cfg.ActiveProfileId = p.Id;
                var m = new MacroDef { Name = "Shift+1", TriggerVk = 0x31, Shift = true, Suppress = true };
                m.Actions.Add(new MacroAction { Kind = ActionKind.KeyPress, Vk = 0x33, Ms = 0 });
                p.Macros.Add(m);

                using (var engine = new MacroEngine())
                {
                    engine.Apply(cfg);
                    engine.Filter(0xA0, true, false);          // палец на Shift
                    bool matched = engine.Filter(0x31, true, false);
                    engine.Filter(0x31, false, false);
                    Check("Shift+1 подходит по пальцу, хотя система считает Shift отпущенным", matched, "подошёл", matched ? "подошёл" : "нет — ушла голая 1");
                    engine.Filter(0xA0, false, false);
                }
            }
            finally
            {
                InputSender.Sink = oldSink;
                KeyState.AsyncDown = oldAsync;
                KeyState.ResetPhysical();
            }
        }

        static double GapBetween(List<Ev> evs, int vkA, bool downA, int vkB, bool downB)
        {
            double a = -1, b = -1;
            foreach (var e in evs)
            {
                if (a < 0 && e.Vk == vkA && e.Down == downA) a = e.Ms;
                else if (a >= 0 && b < 0 && e.Vk == vkB && e.Down == downB) b = e.Ms;
            }
            return a < 0 || b < 0 ? -1 : b - a;
        }

        static string Last(List<string> lines)
        {
            lock (lines) return lines.Count == 0 ? "(пусто)" : lines[lines.Count - 1];
        }

        // ---------- серия умений ----------

        /// <summary>
        /// «Игра» для серии: смотрит на нажатия, которые макрос отправил, и через
        /// 30 мс дописывает в Chat.log строку в настоящем формате — если умение
        /// не в кд (по своим часам игры) и персонаж не в стане. Считает нажатия
        /// впустую: в кд, мимо продолжения.
        /// </summary>
        sealed class FakeGame
        {
            public readonly string Path;
            public volatile bool Stunned, TooFar;
            public readonly List<string> Casts = new List<string>();
            public int Wasted;

            readonly Dictionary<string, long> _last = new Dictionary<string, long>();
            readonly Dictionary<string, int> _cd;
            readonly int _chainWindow;
            readonly Stopwatch _clock = Stopwatch.StartNew();
            readonly Thread _thread;
            volatile bool _stop;
            int _seen;
            bool _alt;
            long _baseAt = -1;
            bool _chainUsed;

            public FakeGame(string path, Dictionary<string, int> cd, int chainWindow)
            {
                Path = path;
                _cd = cd;
                _chainWindow = chainWindow;
                lock (Gate) _seen = Log.Count;
                _thread = new Thread(Loop) { IsBackground = true };
                _thread.Start();
            }

            public void Write(string text)
            {
                System.IO.File.AppendAllText(Path, "2026.11.14 07:06:10 : " + text + " \r\n", System.Text.Encoding.GetEncoding(1251));
            }

            bool Ready(string name, long now)
            {
                long t;
                return !_last.TryGetValue(name, out t) || now - t >= _cd[name];
            }

            void Cast(string name, string line, long now)
            {
                _last[name] = now;
                lock (Casts) Casts.Add(name);
                Thread.Sleep(30);
                Write(line);
                if (name == "Внезапный выстрел") Write("Критический удар! Внезапный выстрел IV: Тренировочная кукла оглушается.");
            }

            void Press(int vk)
            {
                long now = _clock.ElapsedMilliseconds;
                if (Stunned) return;
                if (TooFar) { Thread.Sleep(30); Write("Цель находится слишком далеко."); return; }

                if (vk == 0x33 && !_alt)
                {
                    bool chainUp = _baseAt >= 0 && !_chainUsed && now - _baseAt <= _chainWindow;
                    if (chainUp && Ready("Взрывной выстрел", now))
                    {
                        _chainUsed = true;
                        Cast("Взрывной выстрел", "Взрывной выстрел III: Тренировочная кукла получает 1 361 ед. урона.", now);
                    }
                    else if (!chainUp && Ready("Внезапный выстрел", now))
                    {
                        _baseAt = now;
                        _chainUsed = false;
                        Cast("Внезапный выстрел", "Внезапный выстрел IV: Тренировочная кукла получает 3 369 ед. урона.", now);
                    }
                    else Wasted++;
                }
                else if (vk == 0x45 && _alt)
                {
                    if (Ready("Точный выстрел", now)) Cast("Точный выстрел", "Критический удар! Точный выстрел I: Тренировочная кукла получает 3 637 ед. урона.", now);
                    else Wasted++;
                }
                else if (vk == 0x51 && _alt)
                {
                    if (Ready("Стрела грифона", now)) Cast("Стрела грифона", "Критический удар! Стрела грифона I: Тренировочная кукла получает 3 270 ед. урона.", now);
                    else Wasted++;
                }
                else Wasted++;
            }

            void Loop()
            {
                while (!_stop)
                {
                    Ev e = null;
                    lock (Gate) if (_seen < Log.Count) e = Log[_seen++];
                    if (e == null) { Thread.Sleep(1); continue; }
                    if (e.Vk == 0x12) { _alt = e.Down; continue; }
                    if (e.Down) Press(e.Vk);
                }
            }

            public string CastsText()
            {
                lock (Casts) return string.Join(" → ", Casts.ToArray());
            }

            public void Stop() { _stop = true; _thread.Join(500); }
        }

        static MacroDef SeriesMacro(bool useLog)
        {
            var def = new MacroDef { Name = "серия", Repeat = RepeatMode.Once, OnRetrigger = Retrigger.Ignore, TriggerVk = 0x5A };
            var a = new MacroAction { Kind = ActionKind.Series, Ms = 0, SeriesUseLog = useLog, ChainWindowMs = 400 };
            a.Series.Add(new SeriesSkill { Name = "Внезапный выстрел", Vk = 0x33, CooldownMs = 600, WaitMs = 300 });
            a.Series.Add(new SeriesSkill { Name = "Взрывной выстрел", Vk = 0x33, CooldownMs = 1200, WaitMs = 300, Chain = true });
            a.Series.Add(new SeriesSkill { Name = "Точный выстрел", Vk = 0x45, Alt = true, CooldownMs = 5000, WaitMs = 300 });
            a.Series.Add(new SeriesSkill { Name = "Стрела грифона", Vk = 0x51, Alt = true, CooldownMs = 2500, WaitMs = 300 });
            def.Actions.Add(a);
            return def;
        }

        /// <summary>Частые нажатия кнопки серии, как пользователь жмёт её в бою.</summary>
        static void Tap(MacroRunner r, int forMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < forMs)
            {
                r.Trigger();
                Thread.Sleep(40);
            }
            WaitIdle(new[] { r }, 2000);
        }

        static void TestSeries()
        {
            Head("Серия умений: пропуск того, что в кд, по Chat.log");

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mk-smoke-series");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "Chat.log");
            System.IO.File.WriteAllText(path, "", System.Text.Encoding.GetEncoding(1251));

            var cds = new Dictionary<string, int>
            {
                { "Внезапный выстрел", 600 }, { "Взрывной выстрел", 1200 }, { "Точный выстрел", 5000 }, { "Стрела грифона", 2500 }
            };

            // шаблон по строкам из настоящего лога
            bool m1 = SkillTimers.PatternFor("Стрела Грифона").IsMatch("2026.11.14 07:06:19 : Критический удар! Стрела грифона I: Тренировочная кукла получает 3 270 ед. урона. ");
            Check("шаблон: крит, ранг, другой регистр", m1, "подходит", m1 ? "подходит" : "не подходит");
            bool m2 = SkillTimers.PatternFor("Внезапный выстрел").IsMatch("2026.11.14 07:06:24 : [3.Поиск группы] [charname:Pozi]: Внезапный выстрел: ап");
            Check("шаблон: упоминание в чате не считается", !m2, "не подходит", m2 ? "подходит" : "не подходит");
            bool m3 = SkillTimers.PatternFor("Выстрел").IsMatch("2026.11.14 07:06:12 : Прицельный выстрел IV: Тренировочная кукла получает 1 025 ед. урона.");
            Check("шаблон: другое умение не считается", !m3, "не подходит", m3 ? "подходит" : "не подходит");
            bool m4 = SkillTimers.PatternFor("Внезапный выстрел 4").IsMatch("2026.11.14 07:06:10 : Внезапный выстрел IV: Тренировочная кукла получает 3 369 ед. урона.");
            Check("название с рангом цифрой («… 4») находит «… IV»", m4, "подходит", m4 ? "подходит" : "не подходит");
            bool m5 = SkillTimers.KeyOf("Внезапный выстрел IV") == SkillTimers.KeyOf("внезапный выстрел 4") && SkillTimers.KeyOf("Стрела грифона") == "стрела грифона";
            Check("одно умение — один учёт кд, как ни пиши ранг", m5, "да", m5 ? "да" : "нет");

            ChatLog.JournalPath = System.IO.Path.Combine(dir, "timing.txt");
            ChatLog.Use(path);
            var def = SeriesMacro(true);
            var names = new List<string>();
            foreach (var x in def.Actions[0].Series) names.Add(x.Name);
            SkillTimers.WatchNames(names);
            Thread.Sleep(100);

            try
            {
                // 1. Порядок и пропуск кд
                SkillTimers.Clear();
                ResetLog();
                var game = new FakeGame(path, cds, 400);
                var r = new MacroRunner(def);
                Tap(r, 1800);
                game.Stop();
                string casts = game.CastsText();
                Console.WriteLine("    прошло: " + casts);
                Check("первый круг по порядку, с продолжением",
                    casts.StartsWith("Внезапный выстрел → Взрывной выстрел → Точный выстрел → Стрела грифона"),
                    "Внезапный → Взрывной → Точный → Грифон …", casts);
                Check("умения в кд серия не жала", game.Wasted == 0, "0 нажатий впустую", game.Wasted + " впустую");
                int sudden = 0, blast = 0, aimed = 0;
                lock (game.Casts) foreach (var c in game.Casts) { if (c == "Внезапный выстрел") sudden++; if (c == "Взрывной выстрел") blast++; if (c == "Точный выстрел") aimed++; }
                Check("Внезапный — снова, как только вышел кд", sudden >= 2, "≥ 2 раз за 1,8 с при кд 0,6", sudden + " раз");
                Check("Точный с кд 5 с — один раз", aimed == 1, "1", aimed.ToString());
                Check("продолжение не чаще своего кд", blast <= 2, "≤ 2", blast.ToString());
                r.Dispose();

                // 2. Стан: нажатия есть, строк нет — кд не засчитан, и после стана
                // все умения проходят: ни одно не «потерялось» в кд
                SkillTimers.Clear();
                ResetLog();
                game = new FakeGame(path, cds, 400);
                game.Stunned = true;
                r = new MacroRunner(def);
                var t = new Thread(() => { Thread.Sleep(700); game.Stunned = false; });
                t.Start();
                Tap(r, 1500);
                t.Join();
                game.Stop();
                casts = game.CastsText();
                Console.WriteLine("    после стана: " + casts);
                Check("после стана прошли все три основных умения",
                    casts.Contains("Внезапный выстрел") && casts.Contains("Точный выстрел") && casts.Contains("Стрела грифона"),
                    "Внезапный, Точный, Грифон", casts);
                r.Dispose();

                // 3. «Цель находится слишком далеко» — ответ сразу, без ожидания 300 мс
                SkillTimers.Clear();
                ResetLog();
                game = new FakeGame(path, cds, 400);
                game.TooFar = true;
                r = new MacroRunner(def);
                var sw = Stopwatch.StartNew();
                r.Trigger();
                WaitIdle(new[] { r }, 2000);
                double ms = sw.Elapsed.TotalMilliseconds;
                game.Stop();
                Check("«слишком далеко» обрывает ожидание", ms < 200, "< 200 мс", ms.ToString("F0") + " мс");
                bool marked = SkillTimers.LastSeen(SkillTimers.KeyOf("Внезапный выстрел")) >= 0;
                Check("и кд не засчитан", !marked, "не засчитан", marked ? "засчитан" : "не засчитан");
                r.Dispose();

                // 4. Умение, прожатое рукой, тоже уходит в кд
                SkillTimers.Clear();
                ResetLog();
                game = new FakeGame(path, cds, 400);
                game.Write("Критический удар! Точный выстрел I: Тренировочная кукла получает 3 637 ед. урона.");
                Thread.Sleep(50);
                r = new MacroRunner(def);
                Tap(r, 700);
                game.Stop();
                casts = game.CastsText();
                Check("Точный, прожатый рукой, серия пропускает", !casts.Contains("Точный выстрел"), "без Точного", casts);
                r.Dispose();

                // 4б. Строка не находится вовсе (не тот лог) — серия не застревает на первом умении
                SkillTimers.Clear();
                ResetLog();
                var lost = SeriesMacro(true);
                lost.Actions[0].Series[0].Name = "Нет такого умения";
                lost.Actions[0].Series[0].WaitMs = 100;
                game = new FakeGame(path, cds, 400);
                r = new MacroRunner(lost);
                Tap(r, 700);
                game.Stop();
                casts = game.CastsText();
                Console.WriteLine("    первое умение не находится в логе: " + casts);
                Check("дальше первого умения серия всё равно идёт", casts.Contains("Точный выстрел") && casts.Contains("Стрела грифона"), "Точный, Грифон", casts);
                r.Dispose();

                // 5. Всё в кд — ничего не жмём
                ResetLog();
                long now = SkillTimers.Now;
                foreach (var n in names) SkillTimers.Mark(SkillTimers.KeyOf(n), now);
                r = new MacroRunner(def);
                Tap(r, 200);
                List<Ev> evs;
                lock (Gate) evs = new List<Ev>(Log);
                Check("всё в кд — ни одного нажатия", evs.Count == 0, "пусто", Describe(evs));
                r.Dispose();
            }
            finally
            {
                ChatLog.Use(null);
                SkillTimers.WatchNames(new string[0]);
                SkillTimers.Clear();
            }

            // 6. Без лога: нажатие = применение, пауза после умения
            SkillTimers.Clear();
            ResetLog();
            var r2 = new MacroRunner(SeriesMacro(false));
            Tap(r2, 700);
            List<Ev> ev2;
            lock (Gate) ev2 = new List<Ev>(Log);
            Check("без лога: нажатие засчитывается, дальше следующее", Describe(ev2).StartsWith("3↓ 3↑ 3↓ 3↑"), "3↓ 3↑ 3↓ 3↑ …", Describe(ev2));
            r2.Dispose();
            SkillTimers.Clear();

            // 7. Миграция и конфиг
            var cfg = new AppConfig();
            cfg.Profiles.Add(new Profile { Name = "Лучник" });
            cfg.ActiveProfileId = cfg.Profiles[0].Id;
            cfg.Macros.Add(new MacroDef { Name = "спам", TriggerVk = 0x31 });
            cfg.Migrations.Add(ConfigStore.MigrationHeldMove);
            ConfigStore.Migrate(cfg);
            Check("миграция: копия активного профиля «· серия»", cfg.Profiles.Count == 2 && cfg.Profiles[1].Name == "Лучник · серия", "Лучник · серия", cfg.Profiles.Count > 1 ? cfg.Profiles[1].Name : "нет");
            bool okMig = cfg.Profiles.Count == 2 && cfg.Profiles[1].Macros.Count == 2 && cfg.Profiles[0].Macros.Count == 1 && cfg.Active == cfg.Profiles[0];
            Check("в ней готовая серия, активный не тронут", okMig, "да", okMig ? "да" : "нет");
            ConfigStore.Migrate(cfg);
            Check("повторно не срабатывает", cfg.Profiles.Count == 2, "2 профиля", cfg.Profiles.Count + " профиля");

            var ranger = ConfigStore.RangerSeries();
            var back = LoadFromString(SaveToString(ranger));
            var a2 = back.Profiles[0].Macros[0].Actions[0];
            var s0 = ranger.Actions[0].Series;
            bool same = a2.Kind == ActionKind.Series && a2.Series.Count == 4 && a2.ChainWindowMs == 5000 && a2.SeriesUseLog;
            for (int i = 0; same && i < 4; i++)
                same = a2.Series[i].Name == s0[i].Name && a2.Series[i].Vk == s0[i].Vk && a2.Series[i].Alt == s0[i].Alt
                    && a2.Series[i].CooldownMs == s0[i].CooldownMs && a2.Series[i].Chain == s0[i].Chain && a2.Series[i].WaitMs == s0[i].WaitMs;
            Check("сохранение и чтение без потерь", same, "то же самое", same ? "то же самое" : "отличается");
            Check("подпись шага", ranger.Actions[0].Title == "Серия · 4 умения", "Серия · 4 умения", ranger.Actions[0].Title);
            Console.WriteLine("    " + ranger.Actions[0].Detail);
        }

        static string SaveToString(MacroDef macro)
        {
            var m = typeof(ConfigStore).GetMethod("WriteMacro", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var sb = new System.Text.StringBuilder();
            sb.Append("{ \"profiles\": [ { \"name\": \"x\", \"macros\": [\n");
            m.Invoke(null, new object[] { sb, macro });
            sb.Append("\n] } ] }");
            return sb.ToString();
        }

        static AppConfig LoadFromString(string json)
        {
            var root = new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            var m = typeof(ConfigStore).GetMethod("Parse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            return (AppConfig)m.Invoke(null, new object[] { root });
        }

        // ---------- инфраструктура ----------

        static List<Ev> RunAndCollect(MacroDef def, int timeoutMs)
        {
            ResetLog();

            var r = new MacroRunner(def);
            r.Trigger();
            WaitIdle(new[] { r }, timeoutMs);
            Thread.Sleep(80);   // даём хуку долить хвост

            List<Ev> copy;
            lock (Gate) copy = new List<Ev>(Log);

            r.Dispose();
            return copy;
        }

        static void ResetLog()
        {
            lock (Gate) Log.Clear();
            Clock = Stopwatch.StartNew();
        }

        static void WaitIdle(MacroRunner[] runners, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();

            // ждём, пока хотя бы один стартует
            while (sw.ElapsedMilliseconds < 500)
            {
                bool any = false;
                foreach (var r in runners) if (r.IsRunning) any = true;
                if (any) break;
                Thread.Sleep(5);
            }

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                bool any = false;
                foreach (var r in runners) if (r.IsRunning) any = true;
                if (!any) return;
                Thread.Sleep(5);
            }
        }

        static string Describe(List<Ev> events)
        {
            var parts = new List<string>();
            foreach (var e in events) parts.Add(KeyNames.Get(e.Vk) + (e.Down ? "↓" : "↑"));
            return string.Join(" ", parts.ToArray());
        }

        static string Times(List<Ev> events)
        {
            if (events.Count == 0) return "—";
            var parts = new List<string>();
            double t0 = events[0].Ms;
            foreach (var e in events) parts.Add((e.Ms - t0).ToString("F1"));
            return string.Join(" / ", parts.ToArray());
        }

        static void Head(string title)
        {
            Console.WriteLine();
            Console.WriteLine("── " + title);
        }

        static void Check(string what, bool ok, string want, string got)
        {
            if (ok)
            {
                Console.WriteLine("    OK   " + what + "  (" + got + ")");
            }
            else
            {
                _failures++;
                Console.WriteLine("    ФЕЙЛ " + what + "  ожидалось: " + want + ", получено: " + got);
            }
        }
    }
}
