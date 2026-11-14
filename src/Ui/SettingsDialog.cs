using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Настройки программы: где работают макросы, режим чата, клавиша
    /// включения движка, FPS для подсказок в кадрах, журнал слайдов, версия и
    /// обновление.
    ///
    /// Значения применяются к конфигу только по «Готово». Обновление — сразу:
    /// после запуска установщика программа закрывается (QuitForUpdate).
    /// </summary>
    public class SettingsDialog : EditorDialog
    {
        readonly AppConfig _cfg;
        readonly MacroEngine _engine;

        Check _chkOnlyGame, _chkChat, _chkJournal, _chkAdmin;
        Field _proc;
        Btn _pickProc, _toggleBtn, _toggleClear, _updBtn;
        NumField _fps;
        Label _verText, _updText;

        int _toggleVk;
        UpdateInfo _update;
        bool _busy;

        /// <summary>Нажата «Перезапустить от администратора» — главное окно перезапускает программу.</summary>
        public bool RestartElevated { get; private set; }

        /// <summary>Установщик запущен — программу надо закрыть.</summary>
        public bool QuitForUpdate { get; private set; }

        /// <summary>Что нашла проверка обновлений (для точки на кнопке настроек).</summary>
        public UpdateInfo FoundUpdate { get { return _update; } }

        public SettingsDialog(AppConfig cfg, MacroEngine engine, UpdateInfo knownUpdate)
            : base("Настройки", 640, 692)
        {
            _cfg = cfg;
            _engine = engine;
            _update = knownUpdate;
            _toggleVk = cfg.ToggleVk;

            Build();

            Shown += (s, e) =>
            {
                if (_update == null) CheckUpdates(false);
                else ShowUpdateState(null);
            };
        }

        void Build()
        {
            int y = 24;
            const int h = Theme.ControlHeight;
            int right = ClientSize.Width - 24;

            // ---- права ----
            _chkAdmin = new Check { Text = "Запускать от администратора", Checked = _cfg.RunAsAdmin };
            _chkAdmin.Bounds = new Rectangle(24, y + 14, _chkAdmin.FitWidth(), 24);
            Controls.Add(_chkAdmin);

            if (Elevation.IsElevated)
            {
                AddHint("сейчас запущена с правами", _chkAdmin.Right + 16, y, right - _chkAdmin.Right - 16, h);
            }
            else
            {
                var restart = new Btn { Text = "Перезапустить от администратора", Kind = Btn.Look.Primary };
                restart.Width = restart.FitWidth();
                restart.Location = new Point(right - restart.Width, y);
                restart.Click += (s, e) =>
                {
                    ApplyValues();
                    RestartElevated = true;
                    DialogResult = DialogResult.OK;
                    Close();
                };
                Controls.Add(restart);
            }
            y += h + 20;

            // ---- где работают макросы ----
            _chkOnlyGame = new Check { Text = "Только когда активно окно игры", Checked = _cfg.OnlyInGame };
            _chkOnlyGame.Bounds = new Rectangle(24, y, _chkOnlyGame.FitWidth(), 24);
            _chkOnlyGame.CheckedChanged += (s, e) => UpdateEnabled();
            Controls.Add(_chkOnlyGame);
            y += 36;

            AddLabel("Процесс игры", 24, y);
            _proc = new Field { Bounds = new Rectangle(24, y + LabelGap, 220, h) };
            _proc.Box.Text = string.IsNullOrEmpty(_cfg.GameProcess) ? "aion.bin" : _cfg.GameProcess;
            Controls.Add(_proc);

            _pickProc = new Btn { Text = "Из запущенных…", Kind = Btn.Look.Ghost };
            _pickProc.Width = _pickProc.FitWidth();
            _pickProc.Location = new Point(_proc.Right + 16, y + LabelGap);
            _pickProc.Click += (s, e) => PickProcess();
            Controls.Add(_pickProc);

            AddHint("в другом окне клавиши\nработают как обычно", _pickProc.Right + 16, y + LabelGap, right - _pickProc.Right - 16, h);
            y += Block;

            _chkChat = new Check { Text = "Пауза на чат: Enter — пауза, Enter или Esc — снова работают", Checked = _cfg.ChatPause };
            _chkChat.Bounds = new Rectangle(24, y, _chkChat.FitWidth(), 24);
            Controls.Add(_chkChat);
            y += 44;

            // ---- клавиша включения ----
            AddLabel("Клавиша вкл / выкл движка", 24, y);
            _toggleBtn = new Btn { Kind = Btn.Look.Secondary, Font = Theme.BodyMedium, Bounds = new Rectangle(24, y + LabelGap, 160, h) };
            _toggleBtn.Click += (s, e) =>
            {
                using (var dlg = new CaptureDialog(_engine))
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.CapturedVk != 0)
                    {
                        _toggleVk = dlg.CapturedVk;
                        UpdateToggleText();
                    }
                }
            };
            Controls.Add(_toggleBtn);

            _toggleClear = new Btn { Text = "Без клавиши", Kind = Btn.Look.Ghost };
            _toggleClear.Width = _toggleClear.FitWidth();
            _toggleClear.Location = new Point(_toggleBtn.Right + 16, y + LabelGap);
            _toggleClear.Click += (s, e) => { _toggleVk = 0; UpdateToggleText(); };
            Controls.Add(_toggleClear);

            AddHint("до игры не доходит;\nработает и в выключенном", _toggleClear.Right + 16, y + LabelGap, right - _toggleClear.Right - 16, h);
            y += Block;

            // ---- FPS ----
            AddLabel("FPS игры", 24, y);
            _fps = new NumField { Bounds = new Rectangle(24, y + LabelGap, 120, h), Min = 0, Max = 500 };
            _fps.Value = _cfg.GameFps;
            Controls.Add(_fps);
            AddHint("тайминги слайда покажутся ещё и в кадрах — игра\nопрашивает клавиши раз в кадр; 0 — не показывать", 160, y + LabelGap, right - 160, h);
            y += Block;

            // ---- журнал ----
            _chkJournal = new Check { Text = "Журнал макросов: что ушло в игру — в " + MacroRunner.JournalFile, Checked = _cfg.MacroJournal };
            _chkJournal.Bounds = new Rectangle(24, y + 14, _chkJournal.FitWidth(), 24);
            Controls.Add(_chkJournal);

            var folder = new Btn { Text = "Открыть папку", Kind = Btn.Look.Ghost };
            folder.Width = folder.FitWidth();
            folder.Location = new Point(right - folder.Width, y);
            folder.Click += (s, e) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(ConfigStore.Dir);
                    Process.Start(new ProcessStartInfo { FileName = ConfigStore.Dir, UseShellExecute = true });
                }
                catch { }
            };
            Controls.Add(folder);
            y += h + 28;

            // ---- версия ----
            AddLabel("Версия", 24, y);
            _verText = new Label
            {
                Text = "MacroKit " + Updater.CurrentText,
                Font = Theme.H1,
                ForeColor = Theme.TextStrong,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(24, y + LabelGap, 260, 30)
            };
            Controls.Add(_verText);

            _updText = new Label
            {
                Text = "",
                Font = Theme.Small,
                ForeColor = Theme.TextMuted,
                TextAlign = ContentAlignment.TopLeft,
                Bounds = new Rectangle(24, y + LabelGap + 32, 360, 20)
            };
            Controls.Add(_updText);

            _updBtn = new Btn { Text = "Проверить обновления", Kind = Btn.Look.Secondary };
            _updBtn.Width = _updBtn.FitWidth();
            _updBtn.Location = new Point(right - _updBtn.Width, y + LabelGap);
            _updBtn.Click += (s, e) =>
            {
                if (_busy) return;
                if (Updater.IsNewer(_update)) InstallUpdate();
                else CheckUpdates(true);
            };
            Controls.Add(_updBtn);

            AddButtons(() =>
            {
                string proc = _proc.Box.Text.Trim();
                if (_chkOnlyGame.Checked && proc.Length == 0)
                {
                    MessageBox.Show(this, "Впиши имя процесса игры, например aion.bin.", "MacroKit",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                ApplyValues();
                DialogResult = DialogResult.OK;
                Close();
            });

            UpdateToggleText();
            UpdateEnabled();
        }

        void ApplyValues()
        {
            _cfg.RunAsAdmin = _chkAdmin.Checked;
            _cfg.OnlyInGame = _chkOnlyGame.Checked;
            string proc = _proc.Box.Text.Trim();
            if (proc.Length > 0) _cfg.GameProcess = proc;
            _cfg.ChatPause = _chkChat.Checked;
            _cfg.ToggleVk = _toggleVk;
            _cfg.GameFps = _fps.Value;
            _cfg.MacroJournal = _chkJournal.Checked;
        }

        void UpdateEnabled()
        {
            _proc.Enabled = _chkOnlyGame.Checked;
            _pickProc.Enabled = _chkOnlyGame.Checked;
            _pickProc.Invalidate();
        }

        void UpdateToggleText()
        {
            _toggleBtn.Text = _toggleVk == 0 ? "нет" : KeyNames.Get(_toggleVk);
            _toggleBtn.Invalidate();
        }

        /// <summary>Меню из процессов, у которых есть окно: выбрал — имя в поле.</summary>
        void PickProcess()
        {
            var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrEmpty(p.MainWindowTitle)) continue;
                    string file = ForegroundApp.ProcessFile((uint)p.Id);
                    if (file == null || !seen.Add(file)) continue;

                    string title = p.MainWindowTitle.Length > 40 ? p.MainWindowTitle.Substring(0, 40) + "…" : p.MainWindowTitle;
                    string captured = file;
                    var item = menu.Items.Add(file + "   ·   " + title, null, (s, e) => _proc.Box.Text = captured);
                    item.Padding = new Padding(4, 6, 12, 6);
                }
                catch { }
                finally { p.Dispose(); }
            }

            if (menu.Items.Count == 0) menu.Items.Add("Окон не нашлось").Enabled = false;
            menu.Show(_pickProc, new Point(0, _pickProc.Height + 4));
        }

        // ---------- обновление ----------

        void CheckUpdates(bool userAsked)
        {
            _busy = true;
            _updText.Text = "Проверяю…";
            _updText.ForeColor = Theme.TextMuted;
            _updBtn.Enabled = false;
            _updBtn.Invalidate();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo info = null;
                string error = null;
                try { info = Updater.FetchLatest(); }
                catch (Exception ex) { error = ex.Message; }

                Ui(() =>
                {
                    _busy = false;
                    if (info != null) _update = info;
                    ShowUpdateState(error);
                });
            });
        }

        void ShowUpdateState(string error)
        {
            _updBtn.Enabled = true;

            if (error != null)
            {
                _updText.Text = "Не удалось проверить: " + error;
                _updText.ForeColor = Theme.Red;
                _updBtn.Text = "Проверить обновления";
            }
            else if (Updater.IsNewer(_update))
            {
                _updText.Text = "Доступна версия " + Updater.Text(_update.Version);
                _updText.ForeColor = Theme.Changed;
                _updBtn.Text = "Обновить до " + Updater.Text(_update.Version);
                _updBtn.Kind = Btn.Look.Primary;
            }
            else
            {
                _updText.Text = _update == null ? "" : "Установлена последняя версия";
                _updText.ForeColor = Theme.TextMuted;
                _updBtn.Text = "Проверить обновления";
                _updBtn.Kind = Btn.Look.Secondary;
            }

            _updBtn.Width = _updBtn.FitWidth();
            _updBtn.Left = ClientSize.Width - 24 - _updBtn.Width;
            _updBtn.Invalidate();
        }

        void InstallUpdate()
        {
            var info = _update;
            _busy = true;
            _updBtn.Enabled = false;
            _updBtn.Invalidate();
            _updText.Text = "Скачиваю установщик…";
            _updText.ForeColor = Theme.TextMuted;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string path = null, error = null;
                try
                {
                    path = Updater.Download(info, pct => Ui(() => _updText.Text = "Скачиваю установщик… " + pct + "%"));
                }
                catch (Exception ex) { error = ex.Message; }

                Ui(() =>
                {
                    _busy = false;
                    if (error != null)
                    {
                        ShowUpdateState(error);
                        return;
                    }

                    try
                    {
                        Updater.RunInstaller(path);
                    }
                    catch (Exception ex)
                    {
                        ShowUpdateState("установщик не запустился: " + ex.Message);
                        return;
                    }

                    // Настройки, выставленные в окне, не теряем и при обновлении
                    ApplyValues();
                    QuitForUpdate = true;
                    DialogResult = DialogResult.OK;
                    Close();
                });
            });
        }

        void Ui(Action a)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(a);
            }
            catch { }
        }
    }
}
