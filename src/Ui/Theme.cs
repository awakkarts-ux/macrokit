using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Палитра и типографика — тёмный графит, те же токены, что у «Сборщика
    /// интерфейса Aion» (aion-ui-builder/src/ui/tokens.css). Панели «плавают»
    /// на фоне окна с зазором 8, главное действие — светлая «пилюля», цвет точечно.
    /// </summary>
    public static class Theme
    {
        // Поверхности — от самой глубокой к самой светлой
        public static readonly Color Base = Hex(0x0B0B0C);      // фон окна, на нём лежат панели
        public static readonly Color Surface = Hex(0x151516);   // панели каркаса
        public static readonly Color Card = Hex(0x1A1A1C);      // карточки внутри панели
        public static readonly Color Deep = Hex(0x1D1D1F);      // поля ввода, плашки значков
        public static readonly Color Raised = Hex(0x222224);    // второстепенные кнопки, всплывашки
        public static readonly Color Hover = Hex(0x29292B);
        public static readonly Color Active = Hex(0x2F2F32);    // выбранная строка

        // Текст
        public static readonly Color TextStrong = Hex(0xF5F5F6);
        public static readonly Color Text = Hex(0xD6D6D9);
        public static readonly Color TextMuted = Hex(0x8F8F95);
        public static readonly Color TextFaint = Hex(0x5D5D63);

        // Главный акцент — светлая «пилюля»
        public static readonly Color Accent = Hex(0xEDEDEE);
        public static readonly Color AccentHover = Hex(0xFFFFFF);
        public static readonly Color AccentInk = Hex(0x0B0B0C);

        // Цвет — точечно: состояние, переключатель, значок программы
        public static readonly Color Green = Hex(0x4FD18B);
        public static readonly Color Red = Hex(0xFF5F5F);
        public static readonly Color Changed = Hex(0xFFAD5C);
        public static readonly Color[] Hot = { Hex(0xFF5A3C), Hex(0xE0457B), Hex(0x9B5CFF) };

        // Линии и мелочи контролов
        public static readonly Color Stroke = Color.FromArgb(13, 255, 255, 255);        // 5 %
        public static readonly Color StrokeStrong = Color.FromArgb(23, 255, 255, 255);  // 9 %
        public static readonly Color Focus = Color.FromArgb(56, 255, 255, 255);         // 22 %
        public static readonly Color CheckBorder = Hex(0x3A3A3F);
        public static readonly Color CheckBorderHover = Hex(0x55555B);
        public static readonly Color Track = Hex(0x2D2D31);
        public static readonly Color Thumb = Hex(0x2A2A2D);
        public static readonly Color DangerHover = Mix(Raised, Red, 0.16f);

        // Скругления и зазор каркаса
        public const int RadiusPanel = 20;   // панели каркаса
        public const int Radius = 14;        // карточки, кнопки, поля
        public const int RadiusSm = 10;      // строки списка, плашки значков
        public const int Gap = 8;            // зазор между панелями

        // Высота крупных контролов: 14 px текста + 16 сверху и снизу
        public const int ControlHeight = 52;

        // Шрифты — Onest, размеры в пикселях как в макете
        public static readonly Font Body = Fonts.Ui("Regular", 14f);
        public static readonly Font BodyMedium = Fonts.Ui("Medium", 14f);
        public static readonly Font Strong = Fonts.Ui("SemiBold", 13f);        // строка: заголовок
        public static readonly Font Nav = Fonts.Ui("Medium", 13.5f);           // пункт списка
        public static readonly Font Label = Fonts.Ui("Regular", 13f);          // подписи, галочки
        public static readonly Font Small = Fonts.Ui("Regular", 12f);
        public static readonly Font Group = Fonts.Ui("Medium", 12f);           // заголовок группы в панели
        public static readonly Font Caption = Fonts.Ui("Regular", 11f);        // подпись поля, капсом
        public static readonly Font Button = Fonts.Ui("Medium", 14f);
        public static readonly Font ButtonPrimary = Fonts.Ui("SemiBold", 14f);
        public static readonly Font Title = Fonts.Ui("SemiBold", 15f);         // заголовок раздела
        public static readonly Font H1 = Fonts.Ui("SemiBold", 20f);            // заголовок диалога
        public static readonly Font Mono = Fonts.Mono(11f);

        /// <summary>Segoe MDL2 Assets есть в любой Windows 10/11 — берём иконки оттуда,
        /// чтобы не тащить картинки и не рисковать отсутствующими эмодзи.</summary>
        public static readonly Font Icon = new Font("Segoe MDL2 Assets", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
        public static readonly Font IconSmall = new Font("Segoe MDL2 Assets", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        public const string IcoKeyboard = "";
        public const string IcoTimer = "";
        public const string IcoMouse = "";
        public const string IcoPlay = "";
        public const string IcoAdd = "";
        public const string IcoDelete = "";
        public const string IcoUp = "";
        public const string IcoDown = "";
        public const string IcoCopy = "";
        public const string IcoRecord = "";
        public const string IcoImport = "";

        public static Color Hex(int rgb)
        {
            return Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        /// <summary>Смешать два цвета: t = 0 — первый, t = 1 — второй.</summary>
        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>Полупрозрачный цвет поверх непрозрачного фона — для GDI, который альфу не смешивает.</summary>
        public static Color Over(Color bg, Color fg)
        {
            return Mix(bg, Color.FromArgb(fg.R, fg.G, fg.B), fg.A / 255f);
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath();

            if (radius <= 0 || r.Width <= 0 || r.Height <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void DrawRound(Graphics g, Rectangle r, int radius, Color c, float width)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        /// <summary>Тот же градиент, что у значка программы в шапке сборщика.</summary>
        public static void FillHot(Graphics g, Rectangle r, int radius)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, radius))
            using (var b = new LinearGradientBrush(new Rectangle(r.X - 1, r.Y, r.Width + 2, r.Height), Hot[0], Hot[2], LinearGradientMode.Horizontal))
            {
                b.InterpolationColors = new ColorBlend
                {
                    Colors = Hot,
                    Positions = new[] { 0f, 0.55f, 1f }
                };
                g.FillPath(b, p);
            }
            g.SmoothingMode = old;
        }

        /// <summary>Текст одной строкой: слева, по центру по вертикали, с многоточием.</summary>
        public static void Text1(Graphics g, string text, Font font, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, text, font, r, c,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        /// <summary>Текст по центру прямоугольника.</summary>
        public static void TextCenter(Graphics g, string text, Font font, Rectangle r, Color c)
        {
            TextRenderer.DrawText(g, text, font, r, c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        /// <summary>
        /// Тёмная рамка окна и скруглённые углы (Windows 11). Шапка окна — цвета
        /// фона, как полоса заголовка у сборщика.
        /// </summary>
        public static void ApplyWindowChrome(Form f)
        {
            ApplyWindowChrome(f, Base);
        }

        public static void ApplyWindowChrome(Form f, Color caption)
        {
            try
            {
                int dark = 1;

                // До Windows 10 20H1 атрибут имел номер 19, потом стал 20.
                // Пробуем оба — лишний вызов просто вернёт ошибку.
                Native.DwmSetWindowAttribute(f.Handle, 19, ref dark, sizeof(int));
                Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int corner = 2; // DWMWCP_ROUND
                Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int captionRef = ColorRef(caption);
                Native.DwmSetWindowAttribute(f.Handle, 35 /* DWMWA_CAPTION_COLOR */, ref captionRef, sizeof(int));

                int captionText = ColorRef(Text);
                Native.DwmSetWindowAttribute(f.Handle, 36 /* DWMWA_TEXT_COLOR */, ref captionText, sizeof(int));

                // Рамка чуть светлее шапки — иначе окно сливается с тёмным рабочим столом
                int border = ColorRef(Over(caption, StrokeStrong));
                Native.DwmSetWindowAttribute(f.Handle, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
            }
            catch
            {
                // На старых сборках Windows атрибуты не поддерживаются — не страшно.
            }
        }

        static int ColorRef(Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);   // COLORREF: 0x00BBGGRR
        }

        /// <summary>Тёмные подсказки вместо системных жёлто-белых.</summary>
        public static void Darken(ToolTip tip)
        {
            tip.OwnerDraw = true;
            tip.Popup += (s, e) =>
            {
                var size = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl), Small);
                e.ToolTipSize = new Size(size.Width + 20, size.Height + 14);
            };
            tip.Draw += (s, e) =>
            {
                using (var b = new SolidBrush(Raised)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Over(Raised, StrokeStrong)))
                    e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
                TextRenderer.DrawText(e.Graphics, e.ToolTipText, Small, e.Bounds, Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
        }
    }

    /// <summary>Тёмное контекстное меню: системное светлое в тёмном окне смотрится чужим.</summary>
    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Theme.TextStrong : Theme.Text;
            e.TextFont = Theme.Label;
            base.OnRenderItemText(e);
        }

        class DarkMenuColors : ProfessionalColorTable
        {
            static readonly Color Line = Theme.Over(Theme.Raised, Theme.StrokeStrong);

            public override Color ToolStripDropDownBackground { get { return Theme.Raised; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Theme.Hover; } }
            public override Color MenuItemSelected { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Hover; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Raised; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Raised; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Raised; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Theme.Raised; } }
        }
    }
}
