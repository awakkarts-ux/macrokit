using System;
using System.Drawing;
using System.Windows.Forms;

namespace MacroKit.Ui
{
    /// <summary>
    /// Строка макроса в левой панели — как пункт меню сборщика: плашка-значок 36,
    /// название и подпись; у выбранного строка светлее, а плашка светлая.
    /// </summary>
    public class MacroItem : Control
    {
        public MacroDef Def;
        public bool Selected;
        public bool Running;

        /// <summary>Другой включённый макрос сидит на том же триггере — оба сработают разом.</summary>
        public bool Conflict;

        bool _hover;

        public MacroItem(MacroDef def)
        {
            Def = def;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 52;
            Cursor = Cursors.Hand;
            BackColor = Theme.Surface;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);

            var r = new Rectangle(0, 0, Math.Max(1, Width - 1), Height - 1);

            if (Selected) Theme.FillRound(g, r, Theme.Radius, Theme.Active);
            else if (_hover) Theme.FillRound(g, r, Theme.Radius, Theme.Hover);

            bool off = !Def.Enabled || !Def.HasTrigger;

            // Плашка-значок: клавиатура, как у шага-клавиши
            var tile = new Rectangle(r.X + 8, r.Y + 8, 35, 35);
            Theme.FillRound(g, tile, Theme.RadiusSm, Selected ? Theme.Accent : Theme.Deep);
            Theme.TextCenter(g, Theme.IcoKeyboard, Theme.IconSmall, tile,
                Selected ? Theme.AccentInk : (off ? Theme.TextFaint : (_hover ? Theme.TextStrong : Theme.TextMuted)));

            Color nameColor = Selected ? Theme.TextStrong : (off ? Theme.TextFaint : (_hover ? Theme.Text : Theme.TextMuted));

            int textX = tile.Right + 12;
            int textW = r.Width - textX - 28;

            Theme.Text1(g, Def.Name, Theme.Nav, new Rectangle(textX, r.Y + 8, textW, 19), nameColor);

            string sub = Def.HasTrigger
                ? Def.TriggerText + "  ·  " + Def.RepeatText
                : "триггер не назначен";

            if (Conflict) sub = Def.TriggerText + "  ·  занята другим макросом";

            Theme.Text1(g, sub, Theme.Small, new Rectangle(textX, r.Y + 27, textW, 17),
                Conflict ? Theme.Red : Theme.TextFaint);

            // Индикатор состояния справа. Приоритет: работает сейчас, потом конфликт,
            // потом выключенность — важнее показать то, что происходит прямо сейчас.
            int cx = r.Right - 16;
            int cy = r.Y + r.Height / 2;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (Running)
            {
                using (var b = new SolidBrush(Theme.Green)) g.FillEllipse(b, cx - 4, cy - 4, 8, 8);
            }
            else if (Conflict)
            {
                using (var b = new SolidBrush(Theme.Red)) g.FillEllipse(b, cx - 4, cy - 4, 8, 8);
            }
            else if (!Def.Enabled)
            {
                using (var b = new SolidBrush(Theme.CheckBorder)) g.FillEllipse(b, cx - 4, cy - 4, 8, 8);
            }
        }
    }

    /// <summary>Строка одного действия внутри макроса — карточка на панели.</summary>
    public class ActionItem : Control
    {
        public MacroAction Action;
        public int Index;
        public bool Selected;

        bool _hover;

        public ActionItem(MacroAction action, int index)
        {
            Action = action;
            Index = index;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 60;
            Cursor = Cursors.Hand;
            BackColor = Theme.Surface;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.Surface);

            var r = new Rectangle(0, 0, Math.Max(1, Width - 1), Height - 1);

            if (Selected) Theme.FillRound(g, r, Theme.Radius, Theme.Active);
            else if (_hover) Theme.FillRound(g, r, Theme.Radius, Theme.Hover);
            else Theme.FillRound(g, r, Theme.Radius, Theme.Card);

            // Плашка-значок: пауза приглушена, чтобы клавиши читались с первого взгляда
            bool delay = Action.Kind == ActionKind.Delay;
            var tile = new Rectangle(r.X + 12, r.Y + 12, 35, 35);
            Theme.FillRound(g, tile, Theme.RadiusSm, Selected ? Theme.Accent : Theme.Deep);
            Theme.TextCenter(g, Action.Glyph, Theme.Icon, tile,
                Selected ? Theme.AccentInk : (delay ? Theme.TextMuted : Theme.TextStrong));

            int textX = tile.Right + 16;
            int textW = r.Width - textX - 60;

            Theme.Text1(g, Action.Title, Theme.Strong, new Rectangle(textX, r.Y + 11, textW, 19), Theme.TextStrong);
            Theme.Text1(g, Action.Detail, Theme.Small, new Rectangle(textX, r.Y + 31, textW, 17), Theme.TextMuted);

            // Номер шага
            var numRect = new Rectangle(r.Right - 56, r.Y, 40, r.Height);
            TextRenderer.DrawText(g, (Index + 1).ToString(), Theme.Mono, numRect, Theme.TextFaint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
