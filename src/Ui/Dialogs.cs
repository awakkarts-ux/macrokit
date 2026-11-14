using System;
using System.Drawing;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Ловит одну клавишу. Пока окно открыто, движок глушит весь ввод и отдаёт
    /// сюда первую не-модификаторную клавишу. Esc — отмена.
    /// </summary>
    public class CaptureDialog : Form
    {
        public int CapturedVk;

        readonly MacroEngine _engine;

        public CaptureDialog(MacroEngine engine)
        {
            _engine = engine;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 208);
            BackColor = Theme.Surface;
            ShowInTaskbar = false;
            DoubleBuffered = true;

            var cancel = new Btn { Text = "Отмена", Kind = Btn.Look.Secondary };
            cancel.Width = cancel.FitWidth();
            cancel.Location = new Point((ClientSize.Width - cancel.Width) / 2, 128);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            Paint += OnPaintDialog;

            Shown += (s, e) => _engine.BeginCapture(vk =>
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        CapturedVk = vk;
                        DialogResult = vk == 0 ? DialogResult.Cancel : DialogResult.OK;
                        Close();
                    }));
                }
                catch { }
            });

            FormClosed += (s, e) => _engine.CancelCapture();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this, Theme.Surface);
        }

        void OnPaintDialog(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);

            Theme.TextCenter(g, "Нажми клавишу", Theme.H1, new Rectangle(0, 36, Width, 32), Theme.TextStrong);
            Theme.TextCenter(g, "она станет триггером этого макроса   ·   Esc — отмена", Theme.Small,
                new Rectangle(0, 74, Width, 22), Theme.TextMuted);
        }
    }

    /// <summary>Ввод одной строки: имя профиля при создании и переименовании.</summary>
    public class PromptDialog : Form
    {
        readonly Field _field = new Field();
        readonly string _caption;

        public string Value { get { return _field.Box.Text.Trim(); } }

        public PromptDialog(string caption, string initial)
        {
            _caption = caption;

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 228);
            BackColor = Theme.Surface;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            KeyPreview = true;

            _field.Bounds = new Rectangle(28, 76, 364, Theme.ControlHeight);
            _field.Box.Text = initial ?? "";
            Controls.Add(_field);

            var ok = new Btn { Text = "Готово", Kind = Btn.Look.Primary };
            ok.Width = ok.FitWidth();
            ok.Location = new Point(_field.Right - ok.Width, 152);
            ok.Click += (s, e) => Accept();
            Controls.Add(ok);

            var cancel = new Btn { Text = "Отмена", Kind = Btn.Look.Secondary };
            cancel.Width = cancel.FitWidth();
            cancel.Location = new Point(ok.Left - cancel.Width - Theme.Gap, 152);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            // Enter подтверждает, Esc отменяет. Обрабатываем на форме, потому что
            // фокус живёт внутри TextBox'а поля.
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            _field.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Accept(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            Shown += (s, e) => { _field.Box.Focus(); _field.Box.SelectAll(); };
            Paint += OnPaintDialog;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this, Theme.Surface);
        }

        void Accept()
        {
            if (Value.Length == 0) { _field.Box.Focus(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        void OnPaintDialog(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);
            Theme.Text1(g, _caption, Theme.H1, new Rectangle(28, 24, Width - 56, 32), Theme.TextStrong);
        }
    }

    /// <summary>Общая часть диалогов-редакторов: окно, подписи, кнопки внизу.</summary>
    public class EditorDialog : Form
    {
        // Блок «подпись + поле»: подпись 16, зазор 4, поле 52, до следующего блока 20
        public const int LabelGap = 20;
        public const int Block = LabelGap + Theme.ControlHeight + 20;

        protected EditorDialog(string title, int width, int height)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(width, height);
            BackColor = Theme.Surface;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ShowInTaskbar = false;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this, Theme.Surface);
        }

        /// <summary>Подпись поля; ширина по тексту, чтобы не закрывать соседнюю колонку.</summary>
        protected SectionLabel AddLabel(string text, int x, int y, int w = 0)
        {
            var l = new SectionLabel(text);
            if (w <= 0) w = TextRenderer.MeasureText(l.Text, l.Font).Width + 4;
            l.Bounds = new Rectangle(x, y, w, 16);
            Controls.Add(l);
            return l;
        }

        protected Label AddHint(string text, int x, int y, int w, int h)
        {
            var l = new Label
            {
                Text = text,
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(x, y, w, h)
            };
            Controls.Add(l);
            return l;
        }

        /// <summary>«Готово» и «Отмена» в правом нижнем углу.</summary>
        protected Btn AddButtons(Action onOk)
        {
            var ok = new Btn { Text = "Готово", Kind = Btn.Look.Primary };
            ok.Width = ok.FitWidth();
            ok.Location = new Point(ClientSize.Width - 24 - ok.Width, ClientSize.Height - 24 - ok.Height);
            ok.Click += (s, e) => onOk();
            Controls.Add(ok);

            var cancel = new Btn { Text = "Отмена", Kind = Btn.Look.Secondary };
            cancel.Width = cancel.FitWidth();
            cancel.Location = new Point(ok.Left - cancel.Width - Theme.Gap, ok.Top);
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(cancel);

            return ok;
        }
    }

    /// <summary>
    /// Редактор слайда — скилл плюс отмена анимации.
    ///
    /// Два вида: «с места» (скилл, потом бег с прыжком) и «в прыжке» (прыжок,
    /// в воздухе автоатака и скилл). Строки 3 и 5 у них свои и стоят на одном
    /// месте — видна только та, что относится к выбранному виду.
    ///
    /// Отдельно от ActionDialog: параметров вдвое больше, и мешать их с обычными
    /// действиями значило бы сделать нечитаемым и то, и другое.
    /// </summary>
    public class SlideDialog : EditorDialog
    {
        readonly MacroAction _a;
        readonly MacroEngine _engine;

        Btn _skillBtn, _moveBtn, _autoBtn, _autoClear, _jumpKeyBtn;
        Check _chkCtrl, _chkAlt, _chkShift, _chkJump, _chkFreeMods, _chkFromTrigger, _chkHeldMove;
        readonly int _fps;
        NumField _numHold, _numCast, _numRun, _numPreRun, _numJumpAfter, _numAutoGap, _numTaps, _numTapGap;
        SectionLabel _lblRun, _lblPreRun;
        Label _lblTotal, _hintPreRun;


        bool _loading;

        public SlideDialog(MacroAction action, MacroEngine engine, int fps = 0)
            : base("Слайд", 640, 800)
        {
            _a = action;
            _engine = engine;
            _fps = fps;

            // Вид «в прыжке» (джампшот) убран: в игре он не сработал ни при каком
            // тайминге, а обычный слайд работает и с места, и в прыжке. Слайд
            // старого вида при правке становится обычным (окно правит копию —
            // без «Готово» ничего не меняется).
            _a.Mode = SlideMode.Standing;

            Build();
            LoadValues();
        }

        void Build()
        {
            int y = 24;
            const int h = Theme.ControlHeight;

            // ---- скилл и вид ----
            AddLabel("Клавиша скилла", 24, y);
            _skillBtn = new Btn { Text = "—", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 220, h) };
            _skillBtn.Click += (s, e) => PickKey(_skillBtn);
            Controls.Add(_skillBtn);

            AddLabel("Удержание, мс", 260, y);
            _numHold = new NumField { Bounds = new Rectangle(260, y + LabelGap, 120, h), Min = 0, Max = 5000 };
            _numHold.ValueChanged += (s, e) => { _a.Ms = _numHold.Value; UpdateTotal(); };
            Controls.Add(_numHold);

            y += Block;

            // ---- модификатор и клавиша из триггера ----
            AddLabel("Модификатор скилла в игре", 24, y);
            _chkCtrl = new Check { Text = "Ctrl" };
            _chkAlt = new Check { Text = "Alt" };
            _chkShift = new Check { Text = "Shift" };
            int x = 24;
            foreach (var c in new[] { _chkCtrl, _chkAlt, _chkShift })
            {
                c.Bounds = new Rectangle(x, y + LabelGap + 4, c.FitWidth(), 24);
                Controls.Add(c);
                x = c.Right + 16;
            }
            _chkCtrl.CheckedChanged += (s, e) => { _a.SkillCtrl = _chkCtrl.Checked; UpdateSkillText(); };
            _chkAlt.CheckedChanged += (s, e) => { _a.SkillAlt = _chkAlt.Checked; UpdateSkillText(); };
            _chkShift.CheckedChanged += (s, e) => { _a.SkillShift = _chkShift.Checked; UpdateSkillText(); };

            // Один слайд на ряд клавиш: скилл — та клавиша, которой запустили макрос
            _chkFromTrigger = new Check { Text = "Скилл = клавиша триггера" };
            _chkFromTrigger.Bounds = new Rectangle(x + 16, y + LabelGap + 4, _chkFromTrigger.FitWidth(), 24);
            _chkFromTrigger.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _a.Vk = _chkFromTrigger.Checked ? MacroAction.TriggerKey : 0;
                UpdateSkillText();
            };
            Controls.Add(_chkFromTrigger);

            y += LabelGap + 24 + 28;

            // ---- строка 3: автоатака перед скиллом, у обоих видов ----
            AddLabel("Автоатака", 24, y);
            _autoBtn = new Btn { Text = "нет", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 120, h) };
            _autoBtn.Click += (s, e) => PickKey(_autoBtn);
            Controls.Add(_autoBtn);

            _autoClear = new Btn { Text = "Без неё", Kind = Btn.Look.Ghost };
            _autoClear.Width = _autoClear.FitWidth();
            _autoClear.Location = new Point(152, y + LabelGap);
            _autoClear.Click += (s, e) => { _a.AutoVk = 0; UpdateAuto(); UpdateTotal(); };
            Controls.Add(_autoClear);

            int gapX = _autoClear.Right + 16;
            AddLabel("До скилла, мс", gapX, y);
            _numAutoGap = new NumField { Bounds = new Rectangle(gapX, y + LabelGap, 120, h), Min = 0, Max = 2000 };
            _numAutoGap.ValueChanged += (s, e) => { _a.AutoGapMs = _numAutoGap.Value; UpdateTotal(); };
            Controls.Add(_numAutoGap);
            AddHint("с места без неё отмена\nв бою не срабатывает", gapX + 136, y + LabelGap, 616 - gapX - 136, h);

            y += Block;

            // ---- бег: общий для обоих, подписи разные ----
            AddLabel("Клавиша движения", 24, y);
            _moveBtn = new Btn { Text = "W", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 120, h) };
            _moveBtn.Click += (s, e) => PickKey(_moveBtn);
            Controls.Add(_moveBtn);

            _lblRun = AddLabel("Держать, мс", 160, y, 130);
            _numRun = new NumField { Bounds = new Rectangle(160, y + LabelGap, 120, h), Min = 0, Max = 10000 };
            _numRun.ValueChanged += (s, e) => { _a.RunMs = _numRun.Value; UpdateTotal(); };
            Controls.Add(_numRun);

            _lblPreRun = AddLabel("Разбег до скилла, мс", 296, y, 150);
            _numPreRun = new NumField { Bounds = new Rectangle(296, y + LabelGap, 120, h), Min = 0, Max = 5000 };
            _numPreRun.ValueChanged += (s, e) => { _a.PreRunMs = _numPreRun.Value; UpdateTotal(); };
            Controls.Add(_numPreRun);

            _hintPreRun = AddHint("", 432, y + LabelGap, 184, h);

            y += Block;

            // ---- строка 5, с места: прыжок ----
            _chkJump = new Check { Text = "Прыжок" };
            _chkJump.Bounds = new Rectangle(24, y + LabelGap + (h - 24) / 2, 120, 24);
            _chkJump.CheckedChanged += (s, e) => { _a.Jump = _chkJump.Checked; _numJumpAfter.Enabled = _chkJump.Checked; };
            Controls.Add(_chkJump);

            AddLabel("Через, мс", 160, y);
            _numJumpAfter = new NumField { Bounds = new Rectangle(160, y + LabelGap, 120, h), Min = 0, Max = 10000 };
            _numJumpAfter.ValueChanged += (s, e) => { _a.JumpAfterMs = _numJumpAfter.Value; };
            Controls.Add(_numJumpAfter);

            AddLabel("Пауза на каст, мс", 296, y);
            _numCast = new NumField { Bounds = new Rectangle(296, y + LabelGap, 120, h), Min = 0, Max = 10000 };
            _numCast.ValueChanged += (s, e) => { _a.CastMs = _numCast.Value; UpdateTotal(); };
            Controls.Add(_numCast);
            AddHint("инстанты 20 мс, кастуемые —\nвремя каста", 432, y + LabelGap, 184, h);

            y += Block;

            // ---- строка 6: сколько раз жать скилл, у обоих видов ----
            AddLabel("Нажатий скилла", 24, y);
            _numTaps = new NumField { Bounds = new Rectangle(24, y + LabelGap, 120, h), Min = 1, Max = 5 };
            _numTaps.ValueChanged += (s, e) => { _a.SkillTaps = _numTaps.Value; _numTapGap.Enabled = _a.SkillTaps > 1; UpdateTotal(); };
            Controls.Add(_numTaps);

            AddLabel("Между нажатиями, мс", 160, y);
            _numTapGap = new NumField { Bounds = new Rectangle(160, y + LabelGap, 120, h), Min = 0, Max = 2000 };
            _numTapGap.ValueChanged += (s, e) => { _a.TapGapMs = _numTapGap.Value; UpdateTotal(); };
            Controls.Add(_numTapGap);

            AddHint("2 — слайд при убранном оружии:\nпервое нажатие достаёт оружие", 296, y + LabelGap, 320, h);

            y += Block;

            // ---- строка 7: прыжок и W под пальцем ----
            AddLabel("Клавиша прыжка", 24, y);
            _jumpKeyBtn = new Btn { Text = "Пробел", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 120, h) };
            _jumpKeyBtn.Click += (s, e) => PickKey(_jumpKeyBtn);
            Controls.Add(_jumpKeyBtn);

            _chkHeldMove = new Check { Text = "Учитывать W, зажатую рукой" };
            _chkHeldMove.Bounds = new Rectangle(160, y + LabelGap + (h - 24) / 2, _chkHeldMove.FitWidth(), 24);
            _chkHeldMove.CheckedChanged += (s, e) => { _a.RespectHeldMove = _chkHeldMove.Checked; };
            Controls.Add(_chkHeldMove);

            AddHint("для кайта: на бегу слайд сам\nостановит, в конце — бег дальше", _chkHeldMove.Right + 16, y + LabelGap,
                616 - _chkHeldMove.Right - 16, h);

            y += Block;

            _chkFreeMods = new Check { Text = "Отпускать зажатые Ctrl / Alt / Shift на время слайда" };
            _chkFreeMods.Bounds = new Rectangle(24, y, _chkFreeMods.FitWidth(), 24);
            _chkFreeMods.CheckedChanged += (s, e) => { _a.FreeMods = _chkFreeMods.Checked; };
            Controls.Add(_chkFreeMods);

            _lblTotal = new Label
            {
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(_lblTotal);

            var ok = AddButtons(() =>
            {
                if (_a.Vk == 0)
                {
                    MessageBox.Show(this, "Выбери клавишу скилла или отметь «Скилл = клавиша триггера».", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            });
            _lblTotal.Bounds = new Rectangle(24, ok.Top, 330, ok.Height);
        }

        void LoadValues()
        {
            _loading = true;

            _chkCtrl.Checked = _a.SkillCtrl;
            _chkAlt.Checked = _a.SkillAlt;
            _chkShift.Checked = _a.SkillShift;
            _chkFromTrigger.Checked = _a.Vk == MacroAction.TriggerKey;
            _numHold.Value = _a.Ms;
            _numCast.Value = _a.CastMs;
            _numRun.Value = _a.RunMs;
            _numPreRun.Value = _a.PreRunMs;
            _chkJump.Checked = _a.Jump;
            _numJumpAfter.Value = _a.JumpAfterMs;
            _numJumpAfter.Enabled = _a.Jump;
            _numAutoGap.Value = _a.AutoGapMs;
            _numTaps.Value = _a.SkillTaps;
            _numTapGap.Value = _a.TapGapMs;
            _numTapGap.Enabled = _a.SkillTaps > 1;
            _chkFreeMods.Checked = _a.FreeMods;
            _chkHeldMove.Checked = _a.RespectHeldMove;
            _moveBtn.Text = KeyNames.Get(_a.MoveVk);
            _jumpKeyBtn.Text = KeyNames.Get(_a.JumpVk > 0 ? _a.JumpVk : 0x20);

            _loading = false;

            UpdateSkillText();
            UpdateAuto();
            UpdateMode();
        }

        void UpdateMode()
        {
            _lblRun.Text = "Держать, мс";
            _lblPreRun.Text = "Разбег до скилла, мс";
            _hintPreRun.Text = "проверено в бою: 0";
            _chkFreeMods.Top = _jumpKeyBtn.Bottom + 20;
            UpdateTotal();
        }

        void UpdateSkillText()
        {
            bool fromTrigger = _a.Vk == MacroAction.TriggerKey;
            _skillBtn.Enabled = !fromTrigger;
            _skillBtn.Text = _a.Vk == 0 ? "не выбрана" : _a.SkillText;
            _skillBtn.Invalidate();
        }

        void UpdateAuto()
        {
            _autoBtn.Text = _a.AutoVk == 0 ? "нет" : KeyNames.Get(_a.AutoVk);
            _numAutoGap.Enabled = _a.AutoVk != 0;
            _autoBtn.Invalidate();
        }

        void UpdateTotal()
        {
            int ms = _a.SlideTotalMs;
            _lblTotal.Text = "Полная длительность: " + ms + " мс" +
                (_fps > 0 ? "  ·  ≈ " + Math.Round(ms * _fps / 1000.0) + " кадр. при " + _fps + " FPS" : "");
        }

        void PickKey(Btn target)
        {
            using (var dlg = new CaptureDialog(_engine))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.CapturedVk == 0) return;

                if (target == _skillBtn) { _a.Vk = dlg.CapturedVk; UpdateSkillText(); }
                else if (target == _autoBtn) { _a.AutoVk = dlg.CapturedVk; UpdateAuto(); UpdateTotal(); }
                else if (target == _jumpKeyBtn) { _a.JumpVk = dlg.CapturedVk; _jumpKeyBtn.Text = KeyNames.Get(_a.JumpVk); }
                else { _a.MoveVk = dlg.CapturedVk; _moveBtn.Text = KeyNames.Get(_a.MoveVk); }
            }
        }
    }

    /// <summary>
    /// Клавиши-триггеры и клавиша-слой макроса. Основной триггер задаётся в
    /// карточке; здесь — весь ряд, которым один макрос обслуживает много скиллов.
    /// </summary>
    public class KeysDialog : EditorDialog
    {
        readonly MacroDef _def;
        readonly MacroEngine _engine;
        readonly System.Collections.Generic.List<int> _keys;
        int _layer;

        Label _keysText;
        Btn _layerBtn;

        public KeysDialog(MacroDef def, MacroEngine engine)
            : base("Клавиши и слой", 560, 452)
        {
            _def = def;
            _engine = engine;
            _keys = def.AllKeys;
            _layer = def.LayerVk;

            Build();
            UpdateTexts();
        }

        void Build()
        {
            int y = 24;
            const int h = Theme.ControlHeight;

            AddLabel("Клавиши-триггеры", 24, y);
            _keysText = new Label
            {
                Font = Theme.BodyMedium,
                ForeColor = Theme.TextStrong,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(24, y + LabelGap, 512, h)
            };
            Controls.Add(_keysText);

            y += LabelGap + h + 8;

            var add = new Btn { Text = "+ Клавиша", Kind = Btn.Look.Secondary };
            add.Width = add.FitWidth();
            add.Location = new Point(24, y);
            add.Click += (s, e) =>
            {
                int vk = AskKey();
                if (vk != 0 && !_keys.Contains(vk)) { _keys.Add(vk); UpdateTexts(); }
            };
            Controls.Add(add);

            var undo = new Btn { Text = "Убрать последнюю", Kind = Btn.Look.Ghost };
            undo.Width = undo.FitWidth();
            undo.Location = new Point(add.Right + Theme.Gap, y);
            undo.Click += (s, e) => { if (_keys.Count > 0) { _keys.RemoveAt(_keys.Count - 1); UpdateTexts(); } };
            Controls.Add(undo);

            AddHint("в действиях выбери «Скилл = клавиша триггера» —\nмакрос сработает на каждой клавише ряда",
                24, y + h + 4, 512, 40);

            y += h + 4 + 40 + 24;

            AddLabel("Клавиша-слой", 24, y);
            _layerBtn = new Btn { Text = "нет", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 160, h) };
            _layerBtn.Click += (s, e) =>
            {
                int vk = AskKey();
                if (vk != 0) { _layer = vk; UpdateTexts(); }
            };
            Controls.Add(_layerBtn);

            var noLayer = new Btn { Text = "Без слоя", Kind = Btn.Look.Ghost };
            noLayer.Width = noLayer.FitWidth();
            noLayer.Location = new Point(_layerBtn.Right + Theme.Gap, y + LabelGap);
            noLayer.Click += (s, e) => { _layer = 0; UpdateTexts(); };
            Controls.Add(noLayer);

            AddHint("держится как модификатор, в игру не уходит.\nCtrl / Alt / Shift после слоя игра тоже не видит —\nсначала слой, потом Alt",
                noLayer.Right + 16, y + LabelGap - 6, 536 - noLayer.Right - 16, h + 12);

            AddButtons(() =>
            {
                if (_keys.Count == 0)
                {
                    MessageBox.Show(this, "Нужна хотя бы одна клавиша.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (_keys.Contains(_layer))
                {
                    MessageBox.Show(this, "Слой не может быть одной из клавиш-триггеров.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _def.TriggerVk = _keys[0];
                _def.ExtraKeys = _keys.GetRange(1, _keys.Count - 1);
                _def.LayerVk = _layer;
                DialogResult = DialogResult.OK;
                Close();
            });
        }

        int AskKey()
        {
            using (var dlg = new CaptureDialog(_engine))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return 0;
                return dlg.CapturedVk;
            }
        }

        void UpdateTexts()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (int k in _keys) names.Add(KeyNames.Get(k));
            _keysText.Text = names.Count == 0 ? "нет" : string.Join("  ·  ", names.ToArray());

            _layerBtn.Text = _layer == 0 ? "нет" : KeyNames.Get(_layer);
            _layerBtn.Invalidate();
        }
    }

    /// <summary>Редактор одного действия макроса.</summary>
    public class ActionDialog : EditorDialog
    {
        readonly MacroAction _action;
        readonly MacroEngine _engine;

        Combo _cbKind;
        Combo _cbButton;
        Btn _keyBtn;
        NumField _numMs;
        Label _lblKey, _lblButton, _lblMs;

        readonly bool _isDelay;
        readonly bool _isMouse;

        public ActionDialog(MacroAction action, MacroEngine engine)
            : base(TitleFor(action), 460, action.Kind == ActionKind.Delay ? 208 : 392)
        {
            _action = action;
            _engine = engine;

            _isDelay = action.Kind == ActionKind.Delay;
            _isMouse = action.IsMouse;

            Build();
            LoadValues();
        }

        static string TitleFor(MacroAction a)
        {
            if (a.Kind == ActionKind.Delay) return "Пауза";
            return a.IsMouse ? "Действие мышью" : "Действие клавишей";
        }

        void Build()
        {
            int y = 24;
            const int h = Theme.ControlHeight;

            if (!_isDelay)
            {
                AddLabel(_isMouse ? "Что сделать" : "Что сделать с клавишей", 24, y);

                _cbKind = new Combo { Bounds = new Rectangle(24, y + LabelGap, 260, h) };

                if (_isMouse)
                    _cbKind.Items.AddRange(new object[] { "Клик", "Зажать", "Отпустить" });
                else
                    _cbKind.Items.AddRange(new object[] { "Нажать и отпустить", "Зажать", "Отпустить" });

                _cbKind.SelectedIndexChanged += (s, e) => { ApplyKind(); UpdateVisibility(); };
                Controls.Add(_cbKind);

                y += Block;

                if (_isMouse)
                {
                    _lblButton = AddLabel("Кнопка", 24, y);
                    _cbButton = new Combo { Bounds = new Rectangle(24, y + LabelGap, 260, h) };
                    _cbButton.Items.AddRange(new object[] { "Левая", "Правая", "Средняя", "Боковая 1", "Боковая 2" });
                    _cbButton.SelectedIndexChanged += (s, e) => { _action.Button = (MouseBtn)_cbButton.SelectedIndex; };
                    Controls.Add(_cbButton);
                }
                else
                {
                    _lblKey = AddLabel("Клавиша", 24, y);
                    _keyBtn = new Btn
                    {
                        Text = "—",
                        Kind = Btn.Look.Secondary,
                        Font = Theme.BodyMedium,
                        Bounds = new Rectangle(24, y + LabelGap, 260, h)
                    };
                    _keyBtn.Click += (s, e) => PickKey();
                    Controls.Add(_keyBtn);
                }

                y += Block;
            }

            _lblMs = AddLabel(_isDelay ? "Длительность паузы, мс" : "Удержание, мс", 24, y);

            _numMs = new NumField { Bounds = new Rectangle(24, y + LabelGap, 140, h), Min = 0, Max = 600000 };
            _numMs.ValueChanged += (s, e) => { _action.Ms = _numMs.Value; };
            Controls.Add(_numMs);

            AddButtons(() =>
            {
                _action.Ms = _numMs.Value;
                if (!_isDelay && !_isMouse && _action.Vk == 0)
                {
                    MessageBox.Show(this, "Сначала выбери клавишу.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            });
        }

        void LoadValues()
        {
            if (!_isDelay)
            {
                _cbKind.SelectedIndex = KindToIndex(_action.Kind);
                if (_isMouse) _cbButton.SelectedIndex = (int)_action.Button;
                else _keyBtn.Text = KeyNames.Get(_action.Vk);
            }

            _numMs.Value = _action.Ms;
            UpdateVisibility();
        }

        void ApplyKind()
        {
            int i = _cbKind.SelectedIndex;

            if (_isMouse)
                _action.Kind = i == 0 ? ActionKind.MouseClick : (i == 1 ? ActionKind.MouseDown : ActionKind.MouseUp);
            else
                _action.Kind = i == 0 ? ActionKind.KeyPress : (i == 1 ? ActionKind.KeyDown : ActionKind.KeyUp);
        }

        static int KindToIndex(ActionKind k)
        {
            switch (k)
            {
                case ActionKind.KeyPress:
                case ActionKind.MouseClick: return 0;
                case ActionKind.KeyDown:
                case ActionKind.MouseDown: return 1;
                default: return 2;
            }
        }

        void UpdateVisibility()
        {
            // Удержание имеет смысл только для «нажать и отпустить» и для паузы
            bool showMs = _isDelay
                || _action.Kind == ActionKind.KeyPress
                || _action.Kind == ActionKind.MouseClick;

            _lblMs.Visible = showMs;
            _numMs.Visible = showMs;
        }

        void PickKey()
        {
            using (var dlg = new CaptureDialog(_engine))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.CapturedVk == 0) return;

                _action.Vk = dlg.CapturedVk;
                _keyBtn.Text = KeyNames.Get(_action.Vk);
            }
        }
    }

    /// <summary>
    /// «Ждать строку в логе»: шаблон, сколько ждать и где лежит Chat.log.
    /// Путь к логу общий для всей программы, а не для действия.
    /// </summary>
    public class WaitLogDialog : EditorDialog
    {
        readonly MacroAction _a;
        readonly AppConfig _cfg;

        readonly Field _pattern = new Field();
        readonly Field _path = new Field();
        NumField _numMs;

        public WaitLogDialog(MacroAction action, AppConfig cfg)
            : base("Ждать строку в логе", 560, 452)
        {
            _a = action;
            _cfg = cfg;
            Build();
        }

        void Build()
        {
            int y = 24;
            const int h = Theme.ControlHeight;

            AddLabel("Строка содержит (регулярное выражение)", 24, y);
            _pattern.Bounds = new Rectangle(24, y + LabelGap, 512, h);
            _pattern.Box.Text = _a.Pattern ?? "";
            Controls.Add(_pattern);

            y += Block;

            AddLabel("Ждать не дольше, мс", 24, y);
            _numMs = new NumField { Bounds = new Rectangle(24, y + LabelGap, 140, h), Min = 1, Max = 60000 };
            _numMs.Value = _a.Ms;
            Controls.Add(_numMs);
            AddHint("не дождались — макрос останавливается\nи дальше не идёт", 180, y + LabelGap, 356, h);

            y += Block;

            AddLabel("Chat.log игры (общий для всех макросов)", 24, y);
            _path.Bounds = new Rectangle(24, y + LabelGap, 512, h);
            _path.Box.Text = _cfg.ChatLogPath ?? "";
            Controls.Add(_path);

            AddHint("задержки пишутся в " + ChatLog.JournalPath, 24, y + LabelGap + h + 4, 512, 20);

            AddButtons(() =>
            {
                string p = _pattern.Box.Text.Trim();
                try { new System.Text.RegularExpressions.Regex(p); }
                catch (ArgumentException ex)
                {
                    MessageBox.Show(this, "Шаблон не разбирается:\n" + ex.Message, "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                if (p.Length == 0)
                {
                    MessageBox.Show(this, "Впиши, что искать в строке лога.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _a.Pattern = p;
                _a.Ms = _numMs.Value;
                _cfg.ChatLogPath = _path.Box.Text.Trim();
                DialogResult = DialogResult.OK;
                Close();
            });
        }
    }
}
