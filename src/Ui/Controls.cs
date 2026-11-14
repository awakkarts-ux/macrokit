using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Скруглённая панель на фоне родителя — каркас окна и карточки.
    /// BackColor — цвет самой панели: дочерние контролы берут его как фон.
    /// </summary>
    public class RoundPanel : Panel
    {
        public int Radius = Theme.RadiusPanel;

        public RoundPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Surface;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Theme.Base);
            Theme.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Radius, BackColor);
        }
    }

    /// <summary>
    /// Кнопка: светлая «пилюля» — главное действие, тёмная — остальные,
    /// прозрачная с тонкой обводкой — второстепенные. Крупная: 16 / 32.
    /// </summary>
    public class Btn : Control
    {
        /// <summary>Tool — квадратная кнопка-значок на тёмной плашке.</summary>
        public enum Look { Primary, Secondary, Danger, Success, Ghost, Tool }

        public Look Kind = Look.Secondary;
        public int Radius = Theme.Radius;

        bool _hover, _down;

        public Btn()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Button;
            Cursor = Cursors.Hand;
            Size = new Size(120, Theme.ControlHeight);
        }

        /// <summary>Ширина по надписи: текст плюс по 32 с боков.</summary>
        public int FitWidth()
        {
            return TextRenderer.MeasureText(Text, PaintFont, new Size(1000, 100), TextFormatFlags.NoPadding).Width + 64;
        }

        Font PaintFont
        {
            get { return Kind == Look.Primary && Font == Theme.Button ? Theme.ButtonPrimary : Font; }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        // Control.Text сам по себе перерисовку не вызывает, а мы рисуем надпись руками —
        // без этого на кнопке остаётся текст от предыдущего состояния.
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Parent != null ? Parent.BackColor : Theme.Surface;
            g.Clear(bg);

            Color fill, text;
            bool outline;
            ResolveColors(bg, out fill, out text, out outline);

            // Выключенная кнопка — та же, но на 40 % непрозрачности
            if (!Enabled)
            {
                fill = Theme.Mix(bg, fill, 0.4f);
                text = Theme.Mix(bg, text, 0.4f);
            }

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (fill != bg) Theme.FillRound(g, r, Radius, fill);
            if (outline) Theme.DrawRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Radius, Theme.Mix(bg, Theme.Over(bg, Theme.StrokeStrong), Enabled ? 1f : 0.4f), 1f);

            TextRenderer.DrawText(g, Text, PaintFont, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        void ResolveColors(Color bg, out Color fill, out Color text, out bool outline)
        {
            bool on = Enabled && (_hover || _down);
            outline = false;

            switch (Kind)
            {
                case Look.Primary:
                    fill = on ? Theme.AccentHover : Theme.Accent;
                    text = Theme.AccentInk;
                    break;
                case Look.Danger:
                    fill = on ? Theme.DangerHover : Theme.Raised;
                    text = on ? Theme.Red : Theme.Text;
                    break;
                case Look.Ghost:
                    fill = on ? Theme.Hover : bg;
                    text = on ? Theme.TextStrong : Theme.Text;
                    outline = !on;
                    break;
                case Look.Tool:
                    fill = on ? Theme.Hover : Theme.Deep;
                    text = on ? Theme.TextStrong : Theme.TextMuted;
                    break;
                default:
                    fill = on ? Theme.Hover : Theme.Raised;
                    text = on ? Theme.TextStrong : Theme.Text;
                    break;
            }
        }
    }

    /// <summary>
    /// Переключатель. Включённый — с градиентом из значка программы: цвет
    /// в этом оформлении точечный, и здесь он отмечает главное «работает».
    /// </summary>
    public class Toggle : Control
    {
        bool _checked;
        bool _hover;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Toggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(40, 24);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int h = Math.Min(Height, 24);
            int y = (Height - h) / 2;
            var track = new Rectangle(0, y, Width - 1, h - 1);

            if (_checked) Theme.FillHot(g, track, h / 2);
            else Theme.FillRound(g, track, h / 2, _hover ? Theme.Hover : Theme.Track);

            int knob = h - 8;
            int kx = _checked ? Width - knob - 4 - 1 : 4;
            using (var b = new SolidBrush(_checked ? Color.White : (_hover ? Theme.Text : Theme.TextMuted)))
                g.FillEllipse(b, kx, y + 4, knob, knob);
        }
    }

    /// <summary>Галочка: скруглённый квадрат 20, отмеченная — светлая с тёмной галкой.</summary>
    public class Check : Control
    {
        bool _checked;
        bool _hover;

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public Check()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Label;
            Size = new Size(140, 24);
            Cursor = Cursors.Hand;
        }

        /// <summary>Ширина по надписи: квадрат, зазор 12 и текст.</summary>
        public int FitWidth()
        {
            return 20 + 12 + TextRenderer.MeasureText(Text, Font, new Size(1000, 100), TextFormatFlags.NoPadding).Width + 2;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Parent != null ? Parent.BackColor : Theme.Surface;
            g.Clear(bg);

            float k = Enabled ? 1f : 0.4f;
            bool hot = _hover && Enabled;

            int s = 20;
            int y = (Height - s) / 2;
            var box = new Rectangle(0, y, s - 1, s - 1);

            if (_checked)
            {
                Theme.FillRound(g, box, 6, Theme.Mix(bg, Theme.Accent, k));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Theme.Mix(bg, Theme.AccentInk, k), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    g.DrawLines(pen, new[]
                    {
                        new PointF(box.X + 5.5f, box.Y + 10f),
                        new PointF(box.X + 8.5f, box.Y + 13f),
                        new PointF(box.X + 14.5f, box.Y + 6.5f)
                    });
                }
            }
            else
            {
                Theme.FillRound(g, box, 6, Theme.Mix(bg, Theme.Deep, k));
                Theme.DrawRound(g, new Rectangle(1, y + 1, s - 3, s - 3), 5, Theme.Mix(bg, hot ? Theme.CheckBorderHover : Theme.CheckBorder, k), 1.5f);
            }

            var textRect = new Rectangle(s + 12, 0, Width - s - 12, Height);
            Theme.Text1(g, Text, Font, textRect, Theme.Mix(bg, hot ? Theme.Text : Theme.TextMuted, k));
        }
    }

    /// <summary>Поле ввода: тёмная скруглённая подложка с TextBox внутри, при фокусе — светлая обводка.</summary>
    public class Field : Panel
    {
        public readonly TextBox Box = new TextBox();

        public Field()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Height = Theme.ControlHeight;
            Cursor = Cursors.IBeam;

            Box.BorderStyle = BorderStyle.None;
            // Высоту задаём сами: по метрикам Onest TextBox срезает подчёркивание «_»
            Box.AutoSize = false;
            Box.BackColor = Theme.Deep;
            Box.ForeColor = Theme.TextStrong;
            Box.Font = Theme.Body;
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);

            // Клик по подложке мимо текста — тоже в поле
            MouseDown += (s, e) => Box.Focus();
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int h = Box.Font.Height + 6;
            Box.SetBounds(16, (Height - h) / 2 + 1, Math.Max(10, Width - 32), h);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.ForeColor = Enabled ? Theme.TextStrong : Theme.TextFaint;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg = Parent != null ? Parent.BackColor : Theme.Surface;
            e.Graphics.Clear(bg);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(e.Graphics, r, Theme.Radius, Enabled ? Theme.Deep : Theme.Mix(bg, Theme.Deep, 0.4f));
            if (Box.Focused) Theme.DrawRound(e.Graphics, r, Theme.Radius, Theme.Over(Theme.Deep, Theme.Focus), 1f);
            base.OnPaint(e);
        }
    }

    /// <summary>Числовое поле — только цифры, с ограничением диапазона.</summary>
    public class NumField : Field
    {
        public int Min = 0;
        public int Max = 600000;

        public event EventHandler ValueChanged;

        public NumField()
        {
            Box.KeyPress += (s, e) =>
            {
                if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
            Box.TextChanged += (s, e) =>
            {
                var h = ValueChanged;
                if (h != null) h(this, EventArgs.Empty);
            };
            Box.Leave += (s, e) => Value = Value;   // нормализация при уходе фокуса
        }

        public int Value
        {
            get
            {
                int v;
                if (!int.TryParse(Box.Text, out v)) v = Min;
                return Math.Max(Min, Math.Min(Max, v));
            }
            set
            {
                int v = Math.Max(Min, Math.Min(Max, value));
                string s = v.ToString();
                if (Box.Text != s) Box.Text = s;
            }
        }
    }

    /// <summary>
    /// Выпадающий список, нарисованный целиком вручную.
    ///
    /// Системный ComboBox даже в режиме FlatStyle.Flat оставляет светлую рамку
    /// и светлую кнопку со стрелкой — в тёмной теме это выглядит инородно,
    /// поэтому и сам контрол, и всплывающий список рисуем сами.
    /// </summary>
    public class Combo : Control
    {
        public readonly System.Collections.Generic.List<object> Items = new System.Collections.Generic.List<object>();

        const int ItemH = 36;
        const int Inset = 4;

        int _selected = -1;
        bool _hover;
        Form _popup;

        public event EventHandler SelectedIndexChanged;

        public Combo()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.Body;
            Cursor = Cursors.Hand;
            Size = new Size(220, Theme.ControlHeight);
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = value < 0 || value >= Items.Count ? -1 : value;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public object SelectedItem
        {
            get { return _selected >= 0 ? Items[_selected] : null; }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnClick(EventArgs e)
        {
            TogglePopup();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.FillRound(g, r, Theme.Radius, _hover && _popup == null ? Theme.Mix(Theme.Deep, Theme.Hover, 0.5f) : Theme.Deep);
            if (_popup != null) Theme.DrawRound(g, r, Theme.Radius, Theme.Over(Theme.Deep, Theme.Focus), 1f);

            string text = _selected >= 0 ? Items[_selected].ToString() : "";
            Theme.Text1(g, text, Font, new Rectangle(16, 0, Width - 56, Height), Theme.TextStrong);

            // Стрелка — две линии, как в сборщике, а не глиф: так она одинаковой толщины на любом экране
            int cx = Width - 22, cy = Height / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.TextMuted, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (_popup != null) g.DrawLines(pen, new[] { new PointF(cx - 4, cy + 2), new PointF(cx, cy - 2), new PointF(cx + 4, cy + 2) });
                else g.DrawLines(pen, new[] { new PointF(cx - 4, cy - 2), new PointF(cx, cy + 2), new PointF(cx + 4, cy - 2) });
            }
        }

        void TogglePopup()
        {
            if (_popup != null) { ClosePopup(); return; }
            if (Items.Count == 0) return;

            int visible = Math.Min(Items.Count, 10);
            int h = visible * ItemH + Inset * 2;

            int hover = _selected;

            var pop = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                ShowInTaskbar = false,
                BackColor = Theme.Raised,
                Size = new Size(Width, h),
                Location = PointToScreen(new Point(0, Height + 4))
            };

            typeof(Form).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(pop, true, null);

            pop.HandleCreated += (s, ev) => Theme.ApplyWindowChrome(pop, Theme.Raised);

            pop.Paint += (s, ev) =>
            {
                Graphics g = ev.Graphics;
                g.Clear(Theme.Raised);

                for (int i = 0; i < visible; i++)
                {
                    var row = new Rectangle(Inset, Inset + i * ItemH, pop.Width - Inset * 2 - 1, ItemH - 1);

                    if (i == hover) Theme.FillRound(g, row, Theme.RadiusSm, Theme.Hover);
                    else if (i == _selected) Theme.FillRound(g, row, Theme.RadiusSm, Theme.Active);

                    var tr = new Rectangle(row.X + 12, row.Y, row.Width - 20, row.Height);
                    Theme.Text1(g, Items[i].ToString(), Font, tr,
                        i == hover || i == _selected ? Theme.TextStrong : Theme.Text);
                }
            };

            pop.MouseMove += (s, ev) =>
            {
                int i = (ev.Y - Inset) / ItemH;
                if (i < 0 || i >= visible) i = -1;
                if (i != hover) { hover = i; pop.Invalidate(); }
            };

            pop.MouseLeave += (s, ev) => { hover = -1; pop.Invalidate(); };

            pop.MouseClick += (s, ev) =>
            {
                int i = (ev.Y - Inset) / ItemH;
                ClosePopup();
                if (i >= 0 && i < Items.Count) SelectedIndex = i;
            };

            pop.Deactivate += (s, ev) => ClosePopup();

            _popup = pop;
            pop.Show(FindForm());
            pop.Activate();
            Invalidate();
        }

        void ClosePopup()
        {
            var pop = _popup;
            if (pop == null) return;
            _popup = null;

            try { pop.Close(); pop.Dispose(); }
            catch { }

            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ClosePopup();
            base.Dispose(disposing);
        }
    }

    /// <summary>Подпись поля: мелко, капсом, приглушённо.</summary>
    public class SectionLabel : Label
    {
        public SectionLabel(string text)
        {
            Text = text.ToUpperInvariant();
            Font = Theme.Caption;
            ForeColor = Theme.TextFaint;
            AutoSize = false;
            Height = 16;
            TextAlign = ContentAlignment.MiddleLeft;
            UseCompatibleTextRendering = false;
        }
    }

    /// <summary>
    /// Значок профиля в левой панели: скруглённая плашка с инициалами. Как
    /// плашка значка у пункта меню сборщика — тёмная, у выбранного светлая.
    /// </summary>
    public class RailButton : Control
    {
        public const int RailWidth = 76;     // ширина панели профилей
        public const int IconSize = 44;
        public const int RowHeight = 52;

        bool _hover;
        bool _active;
        bool _add;
        bool _badge;

        /// <summary>Значок из Segoe MDL2 вместо букв профиля: кнопка настроек.</summary>
        public bool IsIcon;

        /// <summary>Цветная точка в углу: «есть что посмотреть» — например, обновление.</summary>
        public bool Badge
        {
            get { return _badge; }
            set { if (_badge == value) return; _badge = value; Invalidate(); }
        }

        /// <summary>Кнопка «добавить профиль»: плашка с плюсом.</summary>
        public bool IsAdd
        {
            get { return _add; }
            set { _add = value; Invalidate(); }
        }

        public bool Active
        {
            get { return _active; }
            set { if (_active == value) return; _active = value; Invalidate(); }
        }

        public RailButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Size = new Size(RailWidth, RowHeight);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Surface);

            var icon = new Rectangle((Width - IconSize) / 2, (Height - IconSize) / 2, IconSize - 1, IconSize - 1);

            if (_add)
            {
                Theme.FillRound(g, icon, Theme.Radius, _hover ? Theme.Hover : Theme.Deep);
                Theme.TextCenter(g, Theme.IcoAdd, Theme.IconSmall, icon, _hover ? Theme.TextStrong : Theme.TextMuted);
                return;
            }

            Color bg = _active ? Theme.Accent : (_hover ? Theme.Hover : Theme.Deep);
            Color text = _active ? Theme.AccentInk : (_hover ? Theme.TextStrong : Theme.TextMuted);

            Theme.FillRound(g, icon, Theme.Radius, bg);
            Theme.TextCenter(g, Text, IsIcon ? Theme.Icon : Theme.Strong, icon, text);

            if (_badge)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var dot = new Rectangle(icon.Right - 9, icon.Top - 1, 10, 10);
                using (var ring = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Surface))
                    g.FillEllipse(ring, Rectangle.Inflate(dot, 2, 2));
                using (var b = new SolidBrush(Theme.Hot[0]))
                    g.FillEllipse(b, dot);
            }
        }
    }

    /// <summary>
    /// Вертикальный список с собственной полосой прокрутки — системная в тёмной теме
    /// выглядит светлым инородным телом.
    /// </summary>
    public class ScrollHost : Panel
    {
        public int Gap = 4;
        public int TrackWidth = 6;

        int _offset;
        int _content;
        bool _dragging;
        bool _thumbHover;
        int _dragStartY;
        int _dragStartOffset;

        public ScrollHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            AutoScroll = false;
        }

        public int Offset
        {
            get { return _offset; }
            set
            {
                int max = Math.Max(0, _content - Height);
                int v = Math.Max(0, Math.Min(max, value));
                if (v == _offset) return;
                _offset = v;
                Relayout();
                Invalidate();
            }
        }

        public void Relayout()
        {
            SuspendLayout();

            int y = -_offset;
            int w = Math.Max(10, Width - TrackWidth - 6);

            foreach (Control c in Controls)
            {
                if (!c.Visible) continue;
                c.SetBounds(0, y, w, c.Height);
                y += c.Height + Gap;
            }

            _content = y + _offset;
            ResumeLayout();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int max = Math.Max(0, _content - Height);
            if (_offset > max) _offset = max;
            Relayout();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Offset = _offset - Math.Sign(e.Delta) * 60;
            base.OnMouseWheel(e);
        }

        Rectangle ThumbRect()
        {
            if (_content <= Height || Height <= 0) return Rectangle.Empty;

            int trackH = Height - 8;
            int thumbH = Math.Max(28, (int)((float)Height / _content * trackH));
            int max = _content - Height;
            int pos = max <= 0 ? 0 : (int)((float)_offset / max * (trackH - thumbH));

            return new Rectangle(Width - TrackWidth, 4 + pos, TrackWidth, thumbH);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var t = ThumbRect();
            if (!t.IsEmpty && t.Contains(e.Location))
            {
                _dragging = true;
                _dragStartY = e.Y;
                _dragStartOffset = _offset;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                int trackH = Height - 8;
                var t = ThumbRect();
                int span = Math.Max(1, trackH - t.Height);
                int max = Math.Max(0, _content - Height);
                Offset = _dragStartOffset + (int)((float)(e.Y - _dragStartY) / span * max);
            }
            else
            {
                bool over = ThumbRect().Contains(e.Location);
                if (over != _thumbHover) { _thumbHover = over; Invalidate(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_thumbHover) { _thumbHover = false; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            var t = ThumbRect();
            if (!t.IsEmpty)
                Theme.FillRound(e.Graphics, t, TrackWidth / 2, _thumbHover || _dragging ? Theme.CheckBorder : Theme.Thumb);
        }
    }
}
