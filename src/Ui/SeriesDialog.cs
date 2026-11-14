using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Редактор серии умений: по строке на умение — название как в Chat.log,
    /// клавиша с модификатором, кд, сколько ждать строку и «продолжение».
    /// Правит копию: без «Готово» в макросе ничего не меняется.
    /// </summary>
    public class SeriesDialog : EditorDialog
    {
        const int MaxSkills = 10;
        const int RowH = Theme.ControlHeight + 8;

        // Колонки строки
        const int XName = 24, WName = 236;
        const int XKey = XName + WName + 8, WKey = 104;
        const int XMod = XKey + WKey + 8, WMod = 104;
        const int XCd = XMod + WMod + 8, WCd = 84;
        const int XWait = XCd + WCd + 8, WWait = 96;
        const int XChain = XWait + WWait + 16;
        const int DialogW = 1040;
        const int XDel = DialogW - 24 - Theme.ControlHeight;
        const int XDown = XDel - 8 - Theme.ControlHeight;
        const int XUp = XDown - 8 - Theme.ControlHeight;

        static readonly string[] Mods = { "—", "Alt", "Shift", "Ctrl" };

        readonly MacroAction _a;
        readonly MacroEngine _engine;
        readonly AppConfig _cfg;
        readonly List<SeriesSkill> _items = new List<SeriesSkill>();

        readonly Panel _rows = new Panel();
        Btn _add, _ok, _cancel;
        Check _chkLog;
        NumField _numWindow, _numHold;
        readonly Field _path = new Field();
        readonly List<Control> _bottom = new List<Control>();
        int _bottomTop;

        public SeriesDialog(MacroAction action, MacroEngine engine, AppConfig cfg)
            : base("Серия умений", DialogW, 600)
        {
            _a = action;
            _engine = engine;
            _cfg = cfg;
            foreach (var s in action.Series) _items.Add(s.Clone());
            if (_items.Count == 0) _items.Add(new SeriesSkill());

            Build();
            RebuildRows();
        }

        void Build()
        {
            const int h = Theme.ControlHeight;
            int y = 24;

            AddHint("Каждое нажатие — следующее по кругу умение, которое не в кд. Умения в кд пропускаются.\n" +
                    "Кд засчитывается, только когда в Chat.log появилась строка об умении: в стане или вне дистанции его нет — умение вернётся на следующем круге.\n" +
                    "«Ждать строку» — сколько ждать эту строку после нажатия: мгновенным 1000, кастуемым — время каста с запасом.\n" +
                    "«Продолжение» — умение, которое в игре появляется на той же клавише после умения строкой выше (Взрывной после Внезапного).",
                24, y, DialogW - 48, 72);
            y += 88;

            AddLabel("Название, как в Chat.log", XName, y);
            AddLabel("Клавиша", XKey, y);
            AddLabel("Модификатор", XMod, y);
            AddLabel("Кд, с", XCd, y);
            AddLabel("Ждать строку, мс", XWait, y);
            y += LabelGap;

            _rows.Location = new Point(0, y);
            _rows.Width = ClientSize.Width;
            _rows.BackColor = Theme.Surface;
            Controls.Add(_rows);

            _add = new Btn { Text = "+ Умение", Kind = Btn.Look.Secondary };
            _add.Width = _add.FitWidth();
            _add.Click += (s, e) =>
            {
                if (_items.Count >= MaxSkills) return;
                _items.Add(new SeriesSkill());
                RebuildRows();
            };
            Controls.Add(_add);

            // ---- общие настройки серии: строятся от нуля, Relayout сдвигает ----
            int b = 0;
            _chkLog = new Check { Text = "Подтверждать по Chat.log" };
            _chkLog.Bounds = new Rectangle(24, b + LabelGap + (h - 24) / 2, _chkLog.FitWidth(), 24);
            _chkLog.Checked = _a.SeriesUseLog;
            _bottom.Add(_chkLog);

            var lblWindow = new SectionLabel("Продолжение доступно, мс".ToUpperInvariant());
            lblWindow.Bounds = new Rectangle(320, b, 220, 16);
            _bottom.Add(lblWindow);
            _numWindow = new NumField { Bounds = new Rectangle(320, b + LabelGap, 120, h), Min = 0, Max = 60000 };
            _numWindow.Value = _a.ChainWindowMs;
            _bottom.Add(_numWindow);

            var lblHold = new SectionLabel("Удержание клавиши, мс".ToUpperInvariant());
            lblHold.Bounds = new Rectangle(560, b, 220, 16);
            _bottom.Add(lblHold);
            _numHold = new NumField { Bounds = new Rectangle(560, b + LabelGap, 120, h), Min = 0, Max = 1000 };
            _numHold.Value = _a.Ms;
            _bottom.Add(_numHold);

            var hintLog = new Label
            {
                Text = "без лога нажатие считается применением,\nа «Ждать строку» — просто пауза после умения",
                Font = Theme.Small, ForeColor = Theme.TextFaint, TextAlign = ContentAlignment.TopLeft,
                Bounds = new Rectangle(24, b + LabelGap + h + 4, 290, 34)
            };
            _bottom.Add(hintLog);

            b += Block + 24;
            var lblPath = new SectionLabel("Chat.log игры (общий для всех макросов)".ToUpperInvariant());
            lblPath.Bounds = new Rectangle(24, b, 400, 16);
            _bottom.Add(lblPath);
            _path.Bounds = new Rectangle(24, b + LabelGap, DialogW - 48, h);
            _path.Box.Text = _cfg.ChatLogPath ?? "";
            _bottom.Add(_path);

            // Видно сразу, читает ли программа тот файл: не тот путь — серия
            // не узнает, что умения прошли, и не будет засчитывать кд
            var status = new Label
            {
                Font = Theme.Small, ForeColor = Theme.TextMuted, TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(24, b + LabelGap + h + 4, DialogW - 48, 20)
            };
            Action refresh = () =>
            {
                string st = SkillTimers.LogStatus(_path.Box.Text.Trim());
                status.Text = st;
                status.ForeColor = st.StartsWith("файл найден") ? Theme.TextMuted : Theme.Red;
            };
            refresh();
            _path.Box.TextChanged += (o, e) => refresh();
            _bottom.Add(status);

            foreach (var c in _bottom) { c.Tag = c.Top; Controls.Add(c); }
            _bottomTop = 0;

            _ok = new Btn { Text = "Готово", Kind = Btn.Look.Primary };
            _ok.Width = _ok.FitWidth();
            _ok.Click += (s, e) => Accept();
            Controls.Add(_ok);

            _cancel = new Btn { Text = "Отмена", Kind = Btn.Look.Secondary };
            _cancel.Width = _cancel.FitWidth();
            _cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);
        }

        void RebuildRows()
        {
            _rows.SuspendLayout();
            var old = new List<Control>();
            foreach (Control c in _rows.Controls) old.Add(c);
            _rows.Controls.Clear();
            foreach (var c in old) c.Dispose();

            for (int i = 0; i < _items.Count; i++) AddRow(i, i * RowH);

            _rows.Height = _items.Count * RowH;
            _rows.ResumeLayout();
            Relayout();
        }

        void AddRow(int i, int y)
        {
            const int h = Theme.ControlHeight;
            var s = _items[i];

            var name = new Field { Bounds = new Rectangle(XName, y, WName, h) };
            name.Box.Text = s.Name;
            name.Box.TextChanged += (o, e) => s.Name = name.Box.Text.Trim();
            _rows.Controls.Add(name);

            var key = new Btn { Text = s.Vk == 0 ? "—" : KeyNames.Get(s.Vk), Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(XKey, y, WKey, h) };
            key.Click += (o, e) =>
            {
                using (var dlg = new CaptureDialog(_engine))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK || dlg.CapturedVk == 0) return;
                    s.Vk = dlg.CapturedVk;
                    key.Text = KeyNames.Get(s.Vk);
                }
            };
            _rows.Controls.Add(key);

            var mod = new Combo { Bounds = new Rectangle(XMod, y, WMod, h) };
            foreach (var m in Mods) mod.Items.Add(m);
            mod.SelectedIndex = s.Alt ? 1 : s.Shift ? 2 : s.Ctrl ? 3 : 0;
            mod.SelectedIndexChanged += (o, e) =>
            {
                s.Alt = mod.SelectedIndex == 1;
                s.Shift = mod.SelectedIndex == 2;
                s.Ctrl = mod.SelectedIndex == 3;
            };
            _rows.Controls.Add(mod);

            var cd = new NumField { Bounds = new Rectangle(XCd, y, WCd, h), Min = 0, Max = 3600 };
            cd.Value = (s.CooldownMs + 500) / 1000;
            cd.ValueChanged += (o, e) => s.CooldownMs = cd.Value * 1000;
            _rows.Controls.Add(cd);

            var wait = new NumField { Bounds = new Rectangle(XWait, y, WWait, h), Min = 0, Max = 10000 };
            wait.Value = s.WaitMs;
            wait.ValueChanged += (o, e) => s.WaitMs = wait.Value;
            _rows.Controls.Add(wait);

            // Продолжение — только у второго и дальше: оно продолжает предыдущее
            var chain = new Check { Text = "продолжение" };
            chain.Bounds = new Rectangle(XChain, y + (h - 24) / 2, chain.FitWidth(), 24);
            chain.Checked = s.Chain && i > 0;
            chain.Enabled = i > 0;
            chain.CheckedChanged += (o, e) =>
            {
                s.Chain = chain.Checked;
                // Продолжение в игре висит на той же клавише, что и умение до него
                if (s.Chain && s.Vk == 0 && i > 0)
                {
                    var p = _items[i - 1];
                    s.Vk = p.Vk; s.Alt = p.Alt; s.Shift = p.Shift; s.Ctrl = p.Ctrl;
                    key.Text = s.Vk == 0 ? "—" : KeyNames.Get(s.Vk);
                    mod.SelectedIndex = s.Alt ? 1 : s.Shift ? 2 : s.Ctrl ? 3 : 0;
                }
            };
            _rows.Controls.Add(chain);

            var up = new Btn { Text = Theme.IcoUp, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Bounds = new Rectangle(XUp, y, h, h), Enabled = i > 0 };
            up.Click += (o, e) => Move(i, -1);
            _rows.Controls.Add(up);

            var down = new Btn { Text = Theme.IcoDown, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Bounds = new Rectangle(XDown, y, h, h), Enabled = i < _items.Count - 1 };
            down.Click += (o, e) => Move(i, 1);
            _rows.Controls.Add(down);

            var del = new Btn { Text = Theme.IcoDelete, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Bounds = new Rectangle(XDel, y, h, h) };
            del.Click += (o, e) =>
            {
                _items.RemoveAt(i);
                if (_items.Count == 0) _items.Add(new SeriesSkill());
                if (_items.Count > 0) _items[0].Chain = false;
                BeginInvoke((Action)RebuildRows);   // не удалять кнопку из её же обработчика
            };
            _rows.Controls.Add(del);
        }

        /// <summary>Умение выше или ниже. Первое в списке не может быть продолжением.</summary>
        void Move(int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= _items.Count) return;
            var t = _items[i];
            _items[i] = _items[j];
            _items[j] = t;
            _items[0].Chain = false;
            BeginInvoke((Action)RebuildRows);   // не пересоздавать кнопку из её же обработчика
        }

        void Relayout()
        {
            int y = _rows.Bottom + 8;
            _add.Location = new Point(24, y);
            _add.Enabled = _items.Count < MaxSkills;
            y = _add.Bottom + 28;

            foreach (var c in _bottom) c.Top = y + (int)c.Tag;
            int bottom = 0;
            foreach (var c in _bottom) bottom = Math.Max(bottom, c.Bottom);

            int okY = bottom + 28;
            ClientSize = new Size(ClientSize.Width, okY + Theme.ControlHeight + 24);
            _ok.Location = new Point(ClientSize.Width - 24 - _ok.Width, okY);
            _cancel.Location = new Point(_ok.Left - _cancel.Width - Theme.Gap, okY);
        }

        void Accept()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var s = _items[i];
                string who = "Умение " + (i + 1) + (s.Name.Length > 0 ? " («" + s.Name + "»)" : "");
                if (s.Vk == 0)
                {
                    Say(who + ": не назначена клавиша.");
                    return;
                }
                if (_chkLog.Checked && s.Name.Length == 0)
                {
                    Say(who + ": впиши название, как оно пишется в Chat.log, — например «Внезапный выстрел». " +
                        "По нему серия узнаёт, что умение прошло.");
                    return;
                }
            }

            _a.Series = _items;
            _a.SeriesUseLog = _chkLog.Checked;
            _a.ChainWindowMs = _numWindow.Value;
            _a.Ms = _numHold.Value;
            _cfg.ChatLogPath = _path.Box.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        }

        void Say(string text)
        {
            MessageBox.Show(this, text, "MacroKit", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
