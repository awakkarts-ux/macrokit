using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    public class MainForm : Form
    {
        readonly MacroEngine _engine = new MacroEngine();
        AppConfig _cfg;
        MacroDef _current;
        bool _loading;

        readonly Timer _saveTimer = new Timer();

        // Раз в 250 мс: активно ли окно игры, не переключили ли движок клавишей,
        // жив ли хук (каждый восьмой тик)
        readonly Timer _watchTimer = new Timer();
        int _watchTicks;

        RailButton _settingsBtn;
        UpdateInfo _update;

        // Активное окно выше нас по правам (игра от администратора, мы — нет):
        // Windows не пускает к нему ни хук, ни SendInput
        bool _foregroundAboveUs;

        // --- левая панель профилей ---
        RoundPanel _rail;
        readonly List<RailButton> _profileButtons = new List<RailButton>();
        RailButton _addProfileBtn;
        ContextMenuStrip _profileMenu;
        Profile _menuTarget;
        readonly ToolTip _tips = new ToolTip();

        // --- панель макросов ---
        RoundPanel _sidebar;
        ScrollHost _macroList;
        Toggle _engineToggle;
        Label _statusLabel;
        Label _countLabel;

        // --- контент ---
        RoundPanel _content;
        Label _emptyLabel;
        Panel _topBar, _settings, _actionsBar;
        RoundPanel _settingsCard;
        ScrollHost _actionsList;

        Field _nameField;
        Btn _btnTest, _btnDup, _btnDelete;
        Btn _triggerBtn, _keysBtn;
        Check _chkCtrl, _chkAlt, _chkShift, _chkMouse4, _chkMouse5, _chkSuppress, _chkEnabled;
        Combo _cbRepeat, _cbRetrigger;
        NumField _numCount, _numGap;
        Label _lblCount, _lblGap, _lblRetrigger, _lblEstimate;
        Btn _btnAddKey, _btnAddDelay, _btnAddMouse, _btnAddSlide, _btnAddSeries, _btnRecord;
        Btn _btnUp, _btnDown, _btnEditAction, _btnDelAction;

        int _selectedAction = -1;
        Recorder _recorder;

        /// <summary>Последняя запись конфига провалилась — правки не на диске.</summary>
        bool _saveError;
        bool _saveErrorShown;

        // Раскладка карточки настроек: три колонки
        const int ColTrigger = 24;
        const int ColRepeat = 376;
        const int ComboWidth = 220;

        public MainForm()
        {
            Text = "MacroKit";
            BackColor = Theme.Base;
            ForeColor = Theme.Text;
            Font = Theme.Body;
            ClientSize = new Size(1360, 860);
            MinimumSize = new Size(1320, 720);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            // Панели «плавают» на фоне окна: зазор 8 по краям и между ними.
            // Сверху зазора нет — шапка окна того же цвета, что и фон.
            Padding = new Padding(Theme.Gap, 0, Theme.Gap, Theme.Gap);

            // Win32-иконка exe отвечает за Проводник и ярлык, но окно и панель задач
            // берут значок из Form.Icon — поэтому кладём ico ещё и в ресурсы сборки.
            LoadAppIcon();

            Theme.Darken(_tips);

            BuildRail();
            BuildSidebar();
            BuildContent();

            // Порядок важен: Fill добавляем первым, затем панели справа налево
            Controls.Add(_content);
            Controls.Add(Spacer());
            Controls.Add(_sidebar);
            Controls.Add(Spacer());
            Controls.Add(_rail);

            _saveTimer.Interval = 600;
            _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); SaveOnly(); };

            _watchTimer.Interval = 250;
            _watchTimer.Tick += (s, e) => OnWatch();

            Load += OnLoaded;
            FormClosing += OnClosing;
        }

        static Panel Spacer()
        {
            return new Panel { Dock = DockStyle.Left, Width = Theme.Gap, BackColor = Theme.Base };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyWindowChrome(this);
        }

        void LoadAppIcon()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("MacroKit.icon.ico"))
                {
                    if (s != null) Icon = new Icon(s);
                }
            }
            catch
            {
                // Без иконки программа работает точно так же.
            }
        }

        // ================= построение интерфейса =================

        void BuildRail()
        {
            _rail = new RoundPanel { Dock = DockStyle.Left, Width = RailButton.RailWidth };

            _rail.Paint += (s, e) =>
            {
                Graphics g = e.Graphics;

                // Значок программы — градиент, как у значка в шапке сборщика
                var logo = new Rectangle((_rail.Width - RailButton.IconSize) / 2, 16, RailButton.IconSize - 1, RailButton.IconSize - 1);
                Theme.FillHot(g, logo, Theme.Radius);
                Theme.TextCenter(g, "MK", Theme.Strong, logo, Color.White);

                using (var p = new Pen(Theme.Over(Theme.Surface, Theme.StrokeStrong)))
                    g.DrawLine(p, 24, 76, _rail.Width - 25, 76);
            };

            _profileMenu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
            _profileMenu.Items.Add("Переименовать", null, (s, e) => RenameProfile());
            _profileMenu.Items.Add("Дублировать", null, (s, e) => DuplicateProfile());
            _profileMenu.Items.Add(new ToolStripSeparator());
            _profileMenu.Items.Add("Удалить", null, (s, e) => DeleteProfile());
            foreach (ToolStripItem it in _profileMenu.Items) it.Padding = new Padding(4, 6, 12, 6);

            // Настройки — внизу полосы, отдельно от профилей
            _settingsBtn = new RailButton { IsIcon = true, Text = Theme.IcoSettings };
            _settingsBtn.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && _settingsBtn.ClientRectangle.Contains(e.Location)) OpenSettings();
            };
            _tips.SetToolTip(_settingsBtn, "Настройки и обновление");
            _rail.Controls.Add(_settingsBtn);
            _rail.Resize += (s, e) => _settingsBtn.Location = new Point(0, _rail.Height - RailButton.RowHeight - 12);
        }

        /// <summary>
        /// Перестраивает значки профилей под логотипом. Кнопки создаются заново
        /// целиком: их единицы, а следить за расхождением списка и контролов дороже.
        /// </summary>
        void RebuildRail()
        {
            foreach (var b in _profileButtons) { _rail.Controls.Remove(b); b.Dispose(); }
            _profileButtons.Clear();

            if (_addProfileBtn != null) { _rail.Controls.Remove(_addProfileBtn); _addProfileBtn.Dispose(); }

            int y = 88;

            foreach (var prof in _cfg.Profiles)
            {
                Profile captured = prof;

                var btn = new RailButton
                {
                    Text = prof.Initials,
                    Active = prof.Id == _cfg.Active.Id,
                    Location = new Point(0, y),
                    Tag = prof
                };

                // Всё вешаем на MouseUp: у Control событие Click для правой кнопки
                // ведёт себя по-разному, и переключение профиля срабатывало бы
                // заодно с вызовом меню.
                btn.MouseUp += (s, e) =>
                {
                    if (!btn.ClientRectangle.Contains(e.Location)) return;

                    if (e.Button == MouseButtons.Right)
                    {
                        _menuTarget = captured;
                        _profileMenu.Show(btn, e.Location);
                    }
                    else if (e.Button == MouseButtons.Left)
                    {
                        SwitchProfile(captured);
                    }
                };

                _tips.SetToolTip(btn, prof.Name + "  ·  макросов: " + prof.Macros.Count);

                _rail.Controls.Add(btn);
                _profileButtons.Add(btn);
                y += RailButton.RowHeight;
            }

            _addProfileBtn = new RailButton { IsAdd = true, Location = new Point(0, y) };
            _addProfileBtn.MouseUp += (s, e) =>
            {
                if (e.Button == MouseButtons.Left && _addProfileBtn.ClientRectangle.Contains(e.Location)) AddProfile();
            };
            _tips.SetToolTip(_addProfileBtn, "Новый профиль");
            _rail.Controls.Add(_addProfileBtn);
        }

        void BuildSidebar()
        {
            _sidebar = new RoundPanel { Dock = DockStyle.Left, Width = 280, Padding = new Padding(16) };

            var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Surface };

            var title = new Label
            {
                Text = "Макросы",
                Font = Theme.Group,
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(8, 0, 120, 44)
            };

            _countLabel = new Label
            {
                Text = "",
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleRight,
                Size = new Size(40, 44)
            };

            var importBtn = new Btn { Text = Theme.IcoImport, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Radius = Theme.RadiusSm, Size = new Size(36, 36) };
            importBtn.Click += (s, e) => ImportMacFiles();

            var addBtn = new Btn { Text = Theme.IcoAdd, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Radius = Theme.RadiusSm, Size = new Size(36, 36) };
            addBtn.Click += (s, e) => AddMacro();

            _tips.SetToolTip(importBtn, "Импортировать .mac из родного софта клавиатуры");
            _tips.SetToolTip(addBtn, "Создать пустой макрос");

            header.Controls.Add(title);
            header.Controls.Add(_countLabel);
            header.Controls.Add(importBtn);
            header.Controls.Add(addBtn);

            header.Resize += (s, e) =>
            {
                addBtn.Location = new Point(header.Width - addBtn.Width, 4);
                importBtn.Location = new Point(addBtn.Left - importBtn.Width - Theme.Gap, 4);
                _countLabel.Location = new Point(importBtn.Left - _countLabel.Width - 12, 0);
            };

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Theme.Surface };

            _statusLabel = new Label
            {
                Text = "…",
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(8, 0)
            };

            _engineToggle = new Toggle { Size = new Size(40, 24) };
            _engineToggle.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _cfg.EngineEnabled = _engineToggle.Checked;
                _engine.Enabled = _engineToggle.Checked;
                UpdateStatus();
                SaveOnly();
            };
            _tips.SetToolTip(_engineToggle, "Включить или выключить все макросы разом");

            footer.Controls.Add(_statusLabel);
            footer.Controls.Add(_engineToggle);

            footer.Resize += (s, e) =>
            {
                _engineToggle.Location = new Point(footer.Width - _engineToggle.Width - 4, (footer.Height - _engineToggle.Height) / 2 + 4);
                _statusLabel.Size = new Size(_engineToggle.Left - 16, footer.Height);
                _statusLabel.Top = 4;
            };

            _macroList = new ScrollHost { Dock = DockStyle.Fill, BackColor = Theme.Surface, Gap = 4 };

            _sidebar.Controls.Add(_macroList);
            _sidebar.Controls.Add(footer);
            _sidebar.Controls.Add(header);
        }

        void BuildContent()
        {
            _content = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(32, 24, 32, 24) };

            _emptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Выбери макрос слева или нажми «+», чтобы создать новый",
                Font = Theme.Body,
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };

            BuildTopBar();
            BuildSettings();
            BuildActionsBar();

            _actionsList = new ScrollHost { Dock = DockStyle.Fill, BackColor = Theme.Surface, Gap = 4, Padding = new Padding(0) };

            _content.Controls.Add(_actionsList);
            _content.Controls.Add(_emptyLabel);
            _content.Controls.Add(_actionsBar);
            _content.Controls.Add(_settings);
            _content.Controls.Add(_topBar);
        }

        void BuildTopBar()
        {
            // 52 — строка, 24 — отступ до карточки настроек
            _topBar = new Panel { Dock = DockStyle.Top, Height = Theme.ControlHeight + 24, BackColor = Theme.Surface };

            _nameField = new Field { Bounds = new Rectangle(0, 0, 340, Theme.ControlHeight) };
            _nameField.Box.Font = Theme.BodyMedium;
            _nameField.Box.TextChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.Name = _nameField.Text;
                RefreshMacroItems();
                _saveTimer.Stop();
                _saveTimer.Start();
            };

            _chkEnabled = new Check { Text = "Включён" };
            _chkEnabled.Bounds = new Rectangle(_nameField.Right + 20, (Theme.ControlHeight - 24) / 2, _chkEnabled.FitWidth(), 24);
            _chkEnabled.CheckedChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.Enabled = _chkEnabled.Checked;
                ApplyAndSave();
                RefreshMacroItems();
            };

            _btnTest = new Btn { Text = "Тест", Kind = Btn.Look.Primary };
            _btnTest.Width = _btnTest.FitWidth();
            _btnTest.Click += (s, e) => { if (_current != null) _engine.PlayOnce(_current); };

            _btnDup = new Btn { Text = "Дублировать", Kind = Btn.Look.Secondary };
            _btnDup.Width = _btnDup.FitWidth();
            _btnDup.Click += (s, e) => DuplicateMacro();

            _btnDelete = new Btn { Text = "Удалить", Kind = Btn.Look.Danger };
            _btnDelete.Width = _btnDelete.FitWidth();
            _btnDelete.Click += (s, e) => DeleteMacro();

            _topBar.Controls.Add(_nameField);
            _topBar.Controls.Add(_chkEnabled);
            _topBar.Controls.Add(_btnTest);
            _topBar.Controls.Add(_btnDup);
            _topBar.Controls.Add(_btnDelete);

            _topBar.Resize += (s, e) =>
            {
                int right = _topBar.Width;
                _btnDelete.Location = new Point(right - _btnDelete.Width, 0);
                _btnDup.Location = new Point(_btnDelete.Left - _btnDup.Width - Theme.Gap, 0);
                _btnTest.Location = new Point(_btnDup.Left - _btnTest.Width - Theme.Gap, 0);

                // На узком окне сжимается поле названия, а не наезжают кнопки
                int room = _btnTest.Left - 24 - _chkEnabled.Width - 20;
                _nameField.Width = Math.Max(160, Math.Min(340, room));
                _chkEnabled.Left = _nameField.Right + 20;
            };
        }

        /// <summary>Галочки в строку: ширина по надписи, зазор 16.</summary>
        static int RowOfChecks(Control parent, int x, int y, params Check[] checks)
        {
            foreach (var c in checks)
            {
                c.Bounds = new Rectangle(x, y, c.FitWidth(), 24);
                parent.Controls.Add(c);
                x = c.Right + 16;
            }
            return x;
        }

        void BuildSettings()
        {
            // Карточка 216 + отступ 24 до строки действий
            _settings = new Panel { Dock = DockStyle.Top, Height = 216 + 24, BackColor = Theme.Surface, Padding = new Padding(0, 0, 0, 24) };
            _settingsCard = new RoundPanel { Dock = DockStyle.Fill, BackColor = Theme.Card, Radius = Theme.Radius };
            _settings.Controls.Add(_settingsCard);
            var card = _settingsCard;

            // ---- колонка 1: триггер ----
            card.Controls.Add(new SectionLabel("Триггер") { Bounds = new Rectangle(ColTrigger, 20, 200, 16) });

            _triggerBtn = new Btn { Text = "не назначен", Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(ColTrigger, 40, 200, Theme.ControlHeight) };
            _triggerBtn.Click += (s, e) => CaptureTrigger();
            card.Controls.Add(_triggerBtn);

            // Ряд клавиш и слой: один макрос на много скиллов
            _keysBtn = new Btn { Text = "Клавиши…", Kind = Btn.Look.Ghost, Bounds = new Rectangle(_triggerBtn.Right + Theme.Gap, 40, 120, Theme.ControlHeight) };
            _keysBtn.Click += (s, e) => EditKeys();
            card.Controls.Add(_keysBtn);

            _chkCtrl = new Check { Text = "Ctrl" };
            _chkAlt = new Check { Text = "Alt" };
            _chkShift = new Check { Text = "Shift" };
            RowOfChecks(card, ColTrigger, 108, _chkCtrl, _chkAlt, _chkShift);

            _chkMouse4 = new Check { Text = "Бок. 1" };
            _chkMouse5 = new Check { Text = "Бок. 2" };
            int after = RowOfChecks(card, ColTrigger, 140, _chkMouse4, _chkMouse5);

            var mouseHint = new Label
            {
                Text = "боковые кнопки мыши",
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(after - 4, 140, 150, 24)
            };
            card.Controls.Add(mouseHint);

            _chkSuppress = new Check { Text = "Блокировать исходную функцию" };
            RowOfChecks(card, ColTrigger, 172, _chkSuppress);

            EventHandler triggerChanged = (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.Ctrl = _chkCtrl.Checked;
                _current.Alt = _chkAlt.Checked;
                _current.Shift = _chkShift.Checked;
                _current.Mouse4 = _chkMouse4.Checked;
                _current.Mouse5 = _chkMouse5.Checked;
                _current.Suppress = _chkSuppress.Checked;
                _triggerBtn.Text = _current.TriggerText;
                ApplyAndSave();
                RefreshMacroItems();
            };

            _chkCtrl.CheckedChanged += triggerChanged;
            _chkAlt.CheckedChanged += triggerChanged;
            _chkShift.CheckedChanged += triggerChanged;
            _chkMouse4.CheckedChanged += triggerChanged;
            _chkMouse5.CheckedChanged += triggerChanged;
            _chkSuppress.CheckedChanged += triggerChanged;

            // ---- колонка 2: повтор ----
            card.Controls.Add(new SectionLabel("Повтор") { Bounds = new Rectangle(ColRepeat, 20, 200, 16) });

            _cbRepeat = new Combo { Bounds = new Rectangle(ColRepeat, 40, ComboWidth, Theme.ControlHeight) };
            _cbRepeat.Items.AddRange(new object[]
            {
                "Один раз за нажатие",
                "Пока держу клавишу",
                "Тумблер: нажал / нажал",
                "Заданное число повторов"
            });
            _cbRepeat.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.Repeat = (RepeatMode)RepeatIndexToMode(_cbRepeat.SelectedIndex);
                UpdateRepeatVisibility();
                ApplyAndSave();
                RefreshMacroItems();
            };
            card.Controls.Add(_cbRepeat);

            _lblCount = new SectionLabel("Повторов") { Bounds = new Rectangle(ColRepeat, 108, 100, 16) };
            _numCount = new NumField { Bounds = new Rectangle(ColRepeat, 128, 96, Theme.ControlHeight), Min = 1, Max = 9999 };
            _numCount.ValueChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.RepeatCount = _numCount.Value;
                ApplyAndSave();
                RefreshMacroItems();
            };

            _lblGap = new SectionLabel("Пауза между, мс") { Bounds = new Rectangle(ColRepeat + 108, 108, 140, 16) };
            _numGap = new NumField { Bounds = new Rectangle(ColRepeat + 108, 128, ComboWidth - 108, Theme.ControlHeight), Min = 0, Max = 600000 };
            _numGap.ValueChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.GapMs = _numGap.Value;
                ApplyAndSave();
            };

            card.Controls.Add(_lblCount);
            card.Controls.Add(_numCount);
            card.Controls.Add(_lblGap);
            card.Controls.Add(_numGap);

            // ---- колонка 3: повторное нажатие ----
            _lblRetrigger = new SectionLabel("Если нажать повторно") { Size = new Size(220, 16) };
            card.Controls.Add(_lblRetrigger);

            _cbRetrigger = new Combo { Size = new Size(ComboWidth, Theme.ControlHeight) };
            _cbRetrigger.Items.AddRange(new object[] { "Начать заново", "Не прерывать", "Остановить" });
            _cbRetrigger.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || _current == null) return;
                _current.OnRetrigger = (Retrigger)_cbRetrigger.SelectedIndex;
                ApplyAndSave();
            };
            card.Controls.Add(_cbRetrigger);

            _lblEstimate = new Label
            {
                Text = "",
                Font = Theme.Small,
                ForeColor = Theme.TextMuted
            };
            card.Controls.Add(_lblEstimate);

            // Третья колонка прижата к правому краю, но не наезжает на вторую
            card.Resize += (s, e) =>
            {
                int right = card.Width - 24;
                int x = Math.Max(ColRepeat + ComboWidth + 32, right - ComboWidth);

                _lblRetrigger.Location = new Point(x, 20);
                _cbRetrigger.SetBounds(x, 40, ComboWidth, Theme.ControlHeight);
                _lblEstimate.SetBounds(x, 108, Math.Max(150, right - x), 64);
            };
        }

        void BuildActionsBar()
        {
            // Две строки: заголовок с правкой выбранного шага, ниже — добавление.
            // В одну строку крупные кнопки не помещаются даже на широком окне.
            const int row2 = Theme.ControlHeight + 16;
            _actionsBar = new Panel { Dock = DockStyle.Top, Height = row2 + Theme.ControlHeight + 16, BackColor = Theme.Surface };

            _actionsBar.Controls.Add(new Label
            {
                Text = "Действия",
                Font = Theme.Title,
                ForeColor = Theme.TextStrong,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(0, 0, 200, Theme.ControlHeight)
            });

            _btnAddKey = new Btn { Text = "+ Клавиша", Kind = Btn.Look.Secondary };
            _btnAddKey.Click += (s, e) => AddAction(ActionKind.KeyPress);

            _btnAddDelay = new Btn { Text = "+ Пауза", Kind = Btn.Look.Secondary };
            _btnAddDelay.Click += (s, e) => AddAction(ActionKind.Delay);

            _btnAddMouse = new Btn { Text = "+ Мышь", Kind = Btn.Look.Secondary };
            _btnAddMouse.Click += (s, e) => AddAction(ActionKind.MouseClick);

            _btnAddSlide = new Btn { Text = "+ Слайд", Kind = Btn.Look.Secondary };
            _btnAddSlide.Click += (s, e) => AddAction(ActionKind.Slide);

            _btnAddSeries = new Btn { Text = "+ Серия", Kind = Btn.Look.Secondary };
            _btnAddSeries.Click += (s, e) => AddAction(ActionKind.Series);

            _btnRecord = new Btn { Text = "Стоп · Esc", Kind = Btn.Look.Ghost };
            int recordWidth = _btnRecord.FitWidth();
            _btnRecord.Text = "Записать";
            _btnRecord.Width = Math.Max(recordWidth, _btnRecord.FitWidth());
            _btnRecord.Click += (s, e) => ToggleRecording();

            int x = 0;
            foreach (var b in new[] { _btnAddKey, _btnAddDelay, _btnAddMouse, _btnAddSlide, _btnAddSeries, _btnRecord })
            {
                if (b != _btnRecord) b.Width = b.FitWidth();
                b.Location = new Point(x, row2);
                x = b.Right + Theme.Gap;
                _actionsBar.Controls.Add(b);
            }

            _btnEditAction = new Btn { Text = "Изменить", Kind = Btn.Look.Ghost };
            _btnEditAction.Width = _btnEditAction.FitWidth();
            _btnEditAction.Click += (s, e) => EditSelectedAction();

            _btnUp = new Btn { Text = Theme.IcoUp, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Size = new Size(Theme.ControlHeight, Theme.ControlHeight) };
            _btnUp.Click += (s, e) => MoveAction(-1);

            _btnDown = new Btn { Text = Theme.IcoDown, Kind = Btn.Look.Tool, Font = Theme.IconSmall, Size = new Size(Theme.ControlHeight, Theme.ControlHeight) };
            _btnDown.Click += (s, e) => MoveAction(1);

            _btnDelAction = new Btn { Text = "Удалить", Kind = Btn.Look.Danger };
            _btnDelAction.Width = _btnDelAction.FitWidth();
            _btnDelAction.Click += (s, e) => DeleteSelectedAction();

            _tips.SetToolTip(_btnUp, "Шаг выше");
            _tips.SetToolTip(_btnDown, "Шаг ниже");

            _actionsBar.Controls.Add(_btnEditAction);
            _actionsBar.Controls.Add(_btnUp);
            _actionsBar.Controls.Add(_btnDown);
            _actionsBar.Controls.Add(_btnDelAction);

            _actionsBar.Resize += (s, e) =>
            {
                int right = _actionsBar.Width;
                _btnDelAction.Location = new Point(right - _btnDelAction.Width, 0);
                _btnDown.Location = new Point(_btnDelAction.Left - _btnDown.Width - Theme.Gap, 0);
                _btnUp.Location = new Point(_btnDown.Left - _btnUp.Width - Theme.Gap, 0);
                _btnEditAction.Location = new Point(_btnUp.Left - _btnEditAction.Width - Theme.Gap, 0);
            };
        }

        // ================= жизненный цикл =================

        void OnLoaded(object sender, EventArgs e)
        {
            _cfg = ConfigStore.Load();

            if (ConfigStore.LoadProblem != null)
            {
                MessageBox.Show(this, ConfigStore.LoadProblem, "MacroKit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (!_engine.Start())
            {
                MessageBox.Show(this,
                    "Не удалось установить хук клавиатуры (код " + _engine.HookError + ").\n" +
                    "Макросы работать не будут.",
                    "MacroKit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _engine.Changed += OnEngineChanged;
            _engine.Apply(_cfg);

            _loading = true;
            _engineToggle.Checked = _cfg.EngineEnabled;
            _loading = false;

            RebuildRail();
            RebuildMacroList();
            SelectMacro(_cfg.Macros.Count > 0 ? _cfg.Macros[0] : null);
            UpdateStatus();

            SaveOnly();   // фиксируем конфиг первого запуска, чтобы файл существовал сразу

            _watchTimer.Start();
            CheckUpdatesInBackground();
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            _watchTimer.Stop();
            _saveTimer.Stop();
            SaveOnly();
            _engine.Changed -= OnEngineChanged;
            _engine.Dispose();
            InputSender.ReleaseEverything();
        }

        // ================= сторож: окно игры, клавиша движка, хук =================

        void OnWatch()
        {
            try
            {
                bool active = !_cfg.OnlyInGame ||
                    ForegroundApp.Matches(ForegroundApp.CurrentFile(), _cfg.GameProcess);
                _engine.SetGameActive(active);

                bool above = ForegroundApp.CurrentAboveUs;
                if (above != _foregroundAboveUs)
                {
                    _foregroundAboveUs = above;
                    UpdateStatus();
                }

                // Движок переключили клавишей — подтягиваем переключатель и конфиг
                if (_engineToggle.Checked != _engine.Enabled)
                {
                    _loading = true;
                    _engineToggle.Checked = _engine.Enabled;
                    _loading = false;
                    _cfg.EngineEnabled = _engine.Enabled;
                    UpdateStatus();
                    SaveOnly();
                }

                // Окно выше по правам не пропускает зонд — хук тут ни при чём
                if (++_watchTicks % 8 == 0 && !_foregroundAboveUs && _engine.CheckHook()) UpdateStatus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ================= настройки и обновление =================

        void OpenSettings()
        {
            using (var dlg = new SettingsDialog(_cfg, _engine, _update))
            {
                var result = dlg.ShowDialog(this);

                if (dlg.FoundUpdate != null) SetUpdate(dlg.FoundUpdate);

                if (result == DialogResult.OK)
                {
                    ApplyAndSave();
                    UpdateStatus();
                }

                if (dlg.RestartElevated)
                {
                    SaveOnly();
                    if (Elevation.RelaunchElevated()) Close();
                    return;
                }

                // Установщик уже запущен: закрываемся, он заменит файлы и
                // запустит новую версию
                if (dlg.QuitForUpdate) Close();
            }
        }

        /// <summary>Тихая проверка при запуске: нашлась новая версия — точка на кнопке настроек.</summary>
        void CheckUpdatesInBackground()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo info;
                try { info = Updater.FetchLatest(); }
                catch { return; }   // нет сети — не повод беспокоить

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke((Action)(() => SetUpdate(info)));
                }
                catch { }
            });
        }

        void SetUpdate(UpdateInfo info)
        {
            _update = info;
            bool newer = Updater.IsNewer(info);
            _settingsBtn.Badge = newer;
            _tips.SetToolTip(_settingsBtn, newer
                ? "Доступна версия " + Updater.Text(info.Version) + " — обновить в настройках"
                : "Настройки и обновление");
        }

        void OnEngineChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)(() =>
                {
                    RefreshRunningState();
                    UpdateStatus();
                }));
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        // ================= список макросов =================

        void RebuildMacroList()
        {
            _macroList.SuspendLayout();

            foreach (Control c in new List<Control>(_macroList.Controls.Cast<Control>()))
            {
                _macroList.Controls.Remove(c);
                c.Dispose();
            }

            foreach (var def in _cfg.Macros)
            {
                var item = new MacroItem(def);
                MacroDef captured = def;
                item.Click += (s, e) => SelectMacro(captured);
                _macroList.Controls.Add(item);
                ApplyConflict(item);
            }

            _macroList.ResumeLayout();
            _macroList.Relayout();
            _macroList.Invalidate();

            _countLabel.Text = _cfg.Macros.Count.ToString();
            RefreshProfileTips();
        }

        /// <summary>
        /// Красная пометка, если на том же триггере сидит другой включённый макрос.
        /// Движок запустит оба разом — почти всегда это не то, чего хотели.
        /// </summary>
        void ApplyConflict(MacroItem item)
        {
            bool bad = _cfg.Active.HasTriggerConflict(item.Def);
            if (item.Conflict == bad) return;

            item.Conflict = bad;
            _tips.SetToolTip(item, bad
                ? "На «" + item.Def.TriggerText + "» уже есть другой включённый макрос.\n" +
                  "Сработают оба сразу. Добавь боковую кнопку мыши или выключи лишний."
                : "");
        }

        /// <summary>Счётчик в подсказке значка профиля — после правок списка макросов.</summary>
        void RefreshProfileTips()
        {
            foreach (var b in _profileButtons)
            {
                var p = b.Tag as Profile;
                if (p != null) _tips.SetToolTip(b, p.Name + "  ·  макросов: " + p.Macros.Count);
            }
        }

        void RefreshMacroItems()
        {
            foreach (Control c in _macroList.Controls)
            {
                var item = c as MacroItem;
                if (item == null) continue;
                item.Selected = item.Def == _current;
                ApplyConflict(item);
                item.Invalidate();
            }
            _countLabel.Text = _cfg.Macros.Count.ToString();
        }

        void RefreshRunningState()
        {
            foreach (Control c in _macroList.Controls)
            {
                var item = c as MacroItem;
                if (item == null) continue;
                bool running = _engine.IsMacroRunning(item.Def);
                if (item.Running != running)
                {
                    item.Running = running;
                    item.Invalidate();
                }
            }
        }

        void SelectMacro(MacroDef def)
        {
            _current = def;
            _selectedAction = -1;

            bool has = def != null;
            _topBar.Visible = has;
            _settings.Visible = has;
            _actionsBar.Visible = has;
            _actionsList.Visible = has;
            _emptyLabel.Visible = !has;

            if (has) LoadEditor();
            RefreshMacroItems();
        }

        // ================= редактор =================

        void LoadEditor()
        {
            _loading = true;

            _nameField.Text = _current.Name;
            _chkEnabled.Checked = _current.Enabled;
            _triggerBtn.Text = _current.TriggerText;
            _chkCtrl.Checked = _current.Ctrl;
            _chkAlt.Checked = _current.Alt;
            _chkShift.Checked = _current.Shift;
            _chkMouse4.Checked = _current.Mouse4;
            _chkMouse5.Checked = _current.Mouse5;
            _chkSuppress.Checked = _current.Suppress;
            _cbRepeat.SelectedIndex = ModeToRepeatIndex(_current.Repeat);
            _cbRetrigger.SelectedIndex = (int)_current.OnRetrigger;
            _numCount.Value = _current.RepeatCount;
            _numGap.Value = _current.GapMs;

            _loading = false;

            UpdateRepeatVisibility();
            RebuildActionList();
        }

        void UpdateRepeatVisibility()
        {
            if (_current == null) return;

            bool showCount = _current.Repeat == RepeatMode.Count;
            bool showGap = _current.Repeat != RepeatMode.Once;

            _lblCount.Visible = showCount;
            _numCount.Visible = showCount;
            _lblGap.Visible = showGap;
            _numGap.Visible = showGap;

            bool showRetrigger = _current.Repeat == RepeatMode.Once || _current.Repeat == RepeatMode.Count;
            _lblRetrigger.Visible = showRetrigger;
            _cbRetrigger.Visible = showRetrigger;

            UpdateEstimate();
        }

        void UpdateEstimate()
        {
            if (_current == null) return;

            int ms = _current.EstimatedMs;
            string s = "Один проход: ~" + ms + " мс";

            if (_current.Repeat == RepeatMode.Count)
            {
                int total = ms * _current.RepeatCount + _current.GapMs * Math.Max(0, _current.RepeatCount - 1);
                s += "\nВсего: ~" + total + " мс";
            }

            s += "\nШагов: " + _current.Actions.Count;
            _lblEstimate.Text = s;
        }

        static int ModeToRepeatIndex(RepeatMode m)
        {
            switch (m)
            {
                case RepeatMode.Once: return 0;
                case RepeatMode.WhileHeld: return 1;
                case RepeatMode.Toggle: return 2;
                default: return 3;
            }
        }

        static RepeatMode RepeatIndexToMode(int i)
        {
            switch (i)
            {
                case 1: return RepeatMode.WhileHeld;
                case 2: return RepeatMode.Toggle;
                case 3: return RepeatMode.Count;
                default: return RepeatMode.Once;
            }
        }

        // ================= действия =================

        void RebuildActionList()
        {
            _actionsList.SuspendLayout();

            foreach (Control c in new List<Control>(_actionsList.Controls.Cast<Control>()))
            {
                _actionsList.Controls.Remove(c);
                c.Dispose();
            }

            if (_current != null)
            {
                for (int i = 0; i < _current.Actions.Count; i++)
                {
                    var item = new ActionItem(_current.Actions[i], i);
                    item.Selected = i == _selectedAction;

                    int index = i;
                    item.Click += (s, e) => { _selectedAction = index; RefreshActionSelection(); };
                    item.DoubleClick += (s, e) => { _selectedAction = index; EditSelectedAction(); };

                    _actionsList.Controls.Add(item);
                }
            }

            _actionsList.ResumeLayout();
            _actionsList.Relayout();
            _actionsList.Invalidate();

            UpdateEstimate();
        }

        void RefreshActionSelection()
        {
            foreach (Control c in _actionsList.Controls)
            {
                var item = c as ActionItem;
                if (item == null) continue;
                bool sel = item.Index == _selectedAction;
                if (item.Selected != sel)
                {
                    item.Selected = sel;
                    item.Invalidate();
                }
            }
        }

        void AddAction(ActionKind kind)
        {
            if (_current == null) return;

            var a = new MacroAction
            {
                Kind = kind,
                // У слайда удержание скилла 0: проверено в бою — 15 мс дают заметно
                // больше промахов, а пауза на каст отсчитывается от отпускания
                // скилла, так что лишнее удержание ещё и сдвигает бег с прыжком
                Ms = kind == ActionKind.Delay ? 100 : (kind == ActionKind.Slide ? 0 : kind == ActionKind.Series ? 10 : 30)
            };

            // Новый слайд — сразу с настройками, проверенными в бою (14.11):
            // удержание 0, W, держать 300, разбег 0, прыжок через 10, каст 20
            if (kind == ActionKind.Slide)
            {
                a.MoveVk = 0x57;
                a.RunMs = 300;
                a.PreRunMs = 0;
                a.Jump = true;
                a.JumpAfterMs = 10;
                a.CastMs = 20;
            }

            if (!EditAction(a)) return;

            int at = _selectedAction >= 0 && _selectedAction < _current.Actions.Count
                ? _selectedAction + 1
                : _current.Actions.Count;

            _current.Actions.Insert(at, a);
            _selectedAction = at;

            RebuildActionList();
            ApplyAndSave();
            RefreshMacroItems();
        }

        void EditSelectedAction()
        {
            if (_current == null || _selectedAction < 0 || _selectedAction >= _current.Actions.Count) return;

            var original = _current.Actions[_selectedAction];
            var copy = original.Clone();

            if (!EditAction(copy)) return;

            _current.Actions[_selectedAction] = copy;
            RebuildActionList();
            ApplyAndSave();
        }

        /// <summary>У слайда свой редактор: параметров вдвое больше обычного действия.</summary>
        bool EditAction(MacroAction a)
        {
            if (a.Kind == ActionKind.Slide)
            {
                using (var dlg = new SlideDialog(a, _engine, _cfg.GameFps))
                    return dlg.ShowDialog(this) == DialogResult.OK;
            }

            if (a.Kind == ActionKind.Series)
            {
                using (var dlg = new SeriesDialog(a, _engine, _cfg))
                    return dlg.ShowDialog(this) == DialogResult.OK;
            }

            if (a.Kind == ActionKind.WaitLog)
            {
                using (var dlg = new WaitLogDialog(a, _cfg))
                    return dlg.ShowDialog(this) == DialogResult.OK;
            }

            using (var dlg = new ActionDialog(a, _engine))
                return dlg.ShowDialog(this) == DialogResult.OK;
        }

        void DeleteSelectedAction()
        {
            if (_current == null || _selectedAction < 0 || _selectedAction >= _current.Actions.Count) return;

            _current.Actions.RemoveAt(_selectedAction);
            if (_selectedAction >= _current.Actions.Count) _selectedAction = _current.Actions.Count - 1;

            RebuildActionList();
            ApplyAndSave();
            RefreshMacroItems();
        }

        void MoveAction(int delta)
        {
            if (_current == null || _selectedAction < 0) return;

            int to = _selectedAction + delta;
            if (to < 0 || to >= _current.Actions.Count) return;

            var tmp = _current.Actions[_selectedAction];
            _current.Actions[_selectedAction] = _current.Actions[to];
            _current.Actions[to] = tmp;
            _selectedAction = to;

            RebuildActionList();
            ApplyAndSave();
        }

        // ================= запись =================

        void ToggleRecording()
        {
            if (_current == null) return;

            if (_recorder != null)
            {
                StopRecording(true);
                return;
            }

            _recorder = _engine.BeginRecord(() =>
            {
                try { BeginInvoke((Action)(() => StopRecording(true))); }
                catch { }
            });

            _btnRecord.Text = "Стоп · Esc";
            _btnRecord.Kind = Btn.Look.Danger;
            _btnRecord.Invalidate();

            SetEditingEnabled(false);
        }

        void StopRecording(bool keep)
        {
            if (_recorder == null) return;

            _engine.EndRecord();

            var captured = _recorder.Build(true);
            _recorder = null;

            _btnRecord.Text = "Записать";
            _btnRecord.Kind = Btn.Look.Ghost;
            _btnRecord.Invalidate();

            SetEditingEnabled(true);

            if (!keep || captured.Count == 0) return;

            var answer = MessageBox.Show(this,
                "Записано шагов: " + captured.Count + "\n\nДобавить их в конец макроса?\n" +
                "«Нет» — заменить всё содержимое макроса.",
                "Запись завершена", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.No) _current.Actions.Clear();

            _current.Actions.AddRange(captured);
            _selectedAction = _current.Actions.Count - 1;

            RebuildActionList();
            ApplyAndSave();
            RefreshMacroItems();
        }

        void SetEditingEnabled(bool on)
        {
            _btnAddKey.Enabled = on;
            _btnAddDelay.Enabled = on;
            _btnAddMouse.Enabled = on;
            _btnEditAction.Enabled = on;
            _btnUp.Enabled = on;
            _btnDown.Enabled = on;
            _btnDelAction.Enabled = on;
            _btnTest.Enabled = on;
            _triggerBtn.Enabled = on;
            _keysBtn.Enabled = on;
        }

        // ================= триггер =================

        void CaptureTrigger()
        {
            if (_current == null) return;

            using (var dlg = new CaptureDialog(_engine))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.CapturedVk == 0) return;

                _current.TriggerVk = dlg.CapturedVk;
            }

            _triggerBtn.Text = _current.TriggerText;
            ApplyAndSave();
            RefreshMacroItems();
        }

        void EditKeys()
        {
            if (_current == null) return;

            using (var dlg = new KeysDialog(_current, _engine))
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _triggerBtn.Text = _current.TriggerText;
            ApplyAndSave();
            RefreshMacroItems();
        }

        // ================= операции над профилями =================

        /// <summary>
        /// Переключение профиля. Движок пересобирает карту привязок целиком и
        /// диспозит старые раннеры, а те в Dispose отпускают зажатые клавиши —
        /// поэтому переключаться безопасно даже посреди работающего макроса.
        /// </summary>
        void SwitchProfile(Profile p)
        {
            if (p == null || p.Id == _cfg.ActiveProfileId) return;

            _cfg.ActiveProfileId = p.Id;

            RebuildRail();
            RebuildMacroList();
            SelectMacro(_cfg.Macros.Count > 0 ? _cfg.Macros[0] : null);
            ApplyAndSave();
            UpdateStatus();
        }

        void AddProfile()
        {
            using (var dlg = new PromptDialog("Новый профиль", "Профиль " + (_cfg.Profiles.Count + 1)))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var p = new Profile { Name = dlg.Value };
                _cfg.Profiles.Add(p);
                SwitchProfile(p);
            }
        }

        void RenameProfile()
        {
            var target = _menuTarget;
            if (target == null) return;

            using (var dlg = new PromptDialog("Имя профиля", target.Name))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                target.Name = dlg.Value;
                RebuildRail();
                SaveOnly();
            }
        }

        void DuplicateProfile()
        {
            var target = _menuTarget;
            if (target == null) return;

            var copy = target.Clone();
            copy.Name = target.Name + " (копия)";

            _cfg.Profiles.Insert(_cfg.Profiles.IndexOf(target) + 1, copy);
            SwitchProfile(copy);
        }

        void DeleteProfile()
        {
            var target = _menuTarget;
            if (target == null) return;

            if (_cfg.Profiles.Count == 1)
            {
                MessageBox.Show(this, "Это единственный профиль — удалить его нельзя.", "MacroKit",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string what = target.Macros.Count == 0
                ? "Удалить пустой профиль «" + target.Name + "»?"
                : "Удалить профиль «" + target.Name + "» вместе с макросами (" + target.Macros.Count + ")?";

            if (MessageBox.Show(this, what, "MacroKit",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int idx = _cfg.Profiles.IndexOf(target);
            bool wasActive = target.Id == _cfg.ActiveProfileId;
            _cfg.Profiles.Remove(target);

            if (wasActive)
            {
                _cfg.ActiveProfileId = _cfg.Profiles[Math.Min(idx, _cfg.Profiles.Count - 1)].Id;
                RebuildRail();
                RebuildMacroList();
                SelectMacro(_cfg.Macros.Count > 0 ? _cfg.Macros[0] : null);
                ApplyAndSave();
                UpdateStatus();
            }
            else
            {
                RebuildRail();
                SaveOnly();
            }
        }

        // ================= операции над макросами =================

        void AddMacro()
        {
            var m = new MacroDef { Name = "Макрос " + (_cfg.Macros.Count + 1) };
            _cfg.Macros.Add(m);

            RebuildMacroList();
            SelectMacro(m);
            ApplyAndSave();

            _nameField.Box.Focus();
            _nameField.Box.SelectAll();
        }

        void ImportMacFiles()
        {
            string[] files;

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Импорт макросов из родного софта";
                dlg.Filter = "Макросы клавиатуры (*.mac)|*.mac|Все файлы (*.*)|*.*";
                dlg.Multiselect = true;
                dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                files = dlg.FileNames;
            }

            var imported = new List<MacroDef>();
            var problems = new List<string>();

            foreach (string path in files)
            {
                var r = MacImport.Load(path);
                string shortName = System.IO.Path.GetFileName(path);

                if (r.Macro == null)
                {
                    problems.Add(shortName + " — " + r.Error);
                    continue;
                }

                if (!string.IsNullOrEmpty(r.Warning))
                    problems.Add(shortName + " — " + r.Warning);

                _cfg.Macros.Add(r.Macro);
                imported.Add(r.Macro);
            }

            if (imported.Count > 0)
            {
                RebuildMacroList();
                SelectMacro(imported[imported.Count - 1]);
                ApplyAndSave();
            }

            var msg = new StringBuilder();
            msg.AppendLine("Импортировано макросов: " + imported.Count);

            if (imported.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine("Триггером подставлена первая клавиша каждого макроса — проверь и поменяй, если нужно.");
                msg.AppendLine("Все импортированные макросы выключены: пока не включишь галочкой «Включён», поведение клавиш не меняется.");
            }

            if (problems.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine("Замечания:");
                foreach (string p in problems) msg.AppendLine("  " + p);
            }

            MessageBox.Show(this, msg.ToString(), "Импорт .mac",
                MessageBoxButtons.OK,
                problems.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        void DuplicateMacro()
        {
            if (_current == null) return;

            var copy = _current.Clone();
            copy.Name = _current.Name + " (копия)";
            copy.TriggerVk = 0;   // два макроса на одной клавише — почти всегда ошибка

            _cfg.Macros.Insert(_cfg.Macros.IndexOf(_current) + 1, copy);

            RebuildMacroList();
            SelectMacro(copy);
            ApplyAndSave();
        }

        void DeleteMacro()
        {
            if (_current == null) return;

            if (MessageBox.Show(this, "Удалить макрос «" + _current.Name + "»?", "MacroKit",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            int idx = _cfg.Macros.IndexOf(_current);
            _cfg.Macros.Remove(_current);

            RebuildMacroList();

            MacroDef next = null;
            if (_cfg.Macros.Count > 0) next = _cfg.Macros[Math.Min(idx, _cfg.Macros.Count - 1)];

            SelectMacro(next);
            ApplyAndSave();
        }

        // ================= сохранение и статус =================

        void ApplyAndSave()
        {
            _engine.Apply(_cfg);
            SaveOnly();
            UpdateEstimate();
        }

        /// <summary>
        /// Записать конфиг. Ошибку прячем громко, а не тихо: раньше она садилась
        /// в строку состояния, которую UpdateStatus затирал следующим тиком.
        /// В результате запись могла падать часами незаметно — правки жили только
        /// в памяти и пропадали при закрытии, выглядя как «набор сбросился сам».
        /// </summary>
        void SaveOnly()
        {
            try
            {
                ConfigStore.Save(_cfg);

                if (_saveError)
                {
                    _saveError = false;
                    UpdateStatus();
                }
            }
            catch (Exception ex)
            {
                _saveError = true;
                UpdateStatus();
                System.Diagnostics.Debug.WriteLine(ex);

                // Первый раз показываем в лицо: молчаливая потеря правок хуже
                // навязчивого окна. Дальше хватает красной строки внизу.
                if (!_saveErrorShown)
                {
                    _saveErrorShown = true;
                    MessageBox.Show(this,
                        "Не удалось записать настройки:\n\n" + ex.Message +
                        "\n\nФайл: " + ConfigStore.FilePath +
                        "\n\nПравки сейчас живут только в памяти и пропадут при закрытии. " +
                        "Проверь, не занят ли файл, и что папка доступна на запись.",
                        "MacroKit", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        void UpdateStatus()
        {
            // Провал записи важнее всего остального: пока он есть, всё, что
            // пользователь настраивает, существует только до закрытия окна.
            if (_saveError)
            {
                _statusLabel.Text = "Конфиг не сохраняется!";
                _statusLabel.ForeColor = Theme.Red;
                return;
            }

            if (!_engine.HookOk)
            {
                _statusLabel.Text = "Хук не установлен";
                _statusLabel.ForeColor = Theme.Red;
                return;
            }

            if (!_engine.Enabled)
            {
                _statusLabel.Text = _cfg.ToggleVk != 0
                    ? "Движок выключен · " + KeyNames.Get(_cfg.ToggleVk)
                    : "Движок выключен";
                _statusLabel.ForeColor = Theme.TextFaint;
                return;
            }

            if (_foregroundAboveUs)
            {
                _statusLabel.Text = "Игра от админа — нужны права";
                _statusLabel.ForeColor = Theme.Red;
                _tips.SetToolTip(_statusLabel,
                    "Активное окно запущено от администратора, а MacroKit — нет.\n" +
                    "Windows не пускает к такому окну ни нажатия, ни макросы.\n" +
                    "Шестерёнка → «Перезапустить от администратора».");
                return;
            }

            if (!_engine.GameActive)
            {
                _statusLabel.Text = "Ждёт окно игры";
                _statusLabel.ForeColor = Theme.TextMuted;
                return;
            }

            if (_engine.ChatPaused)
            {
                _statusLabel.Text = "Пауза: чат (Enter)";
                _statusLabel.ForeColor = Theme.Changed;
                return;
            }

            if (_engine.AnyRunning)
            {
                _statusLabel.Text = "Играет макрос";
                _statusLabel.ForeColor = Theme.Green;
                return;
            }

            int active = 0;
            foreach (var m in _cfg.Macros) if (m.Enabled && m.HasTrigger && m.Actions.Count > 0) active++;

            _statusLabel.Text = "Готов · активных " + active;
            _statusLabel.ForeColor = Theme.TextMuted;

            if (_engine.HookRestarts > 0)
                _tips.SetToolTip(_statusLabel, "Хук клавиатуры пропадал и был восстановлен: " + _engine.HookRestarts + " раз");
        }
    }

}
