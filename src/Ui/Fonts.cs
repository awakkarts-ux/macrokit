using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace MacroKit.Ui
{
    /// <summary>
    /// Вложенные шрифты: Onest (интерфейс) и JetBrains Mono (номера шагов).
    ///
    /// Шрифт регистрируется дважды. PrivateFontCollection даёт FontFamily для
    /// System.Drawing, но TextRenderer рисует через GDI, а GDI про эту коллекцию
    /// не знает — поэтому те же байты ещё и AddFontMemResourceEx. Без второго
    /// вызова GDI молча подставляет системный шрифт.
    ///
    /// Onest взят статическими начертаниями, а не variable-файлом: GDI оси
    /// не понимает и показывает только одно начертание по умолчанию.
    /// </summary>
    public static class Fonts
    {
        static readonly PrivateFontCollection _pfc = new PrivateFontCollection();
        static readonly Dictionary<string, FontFamily> _families = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase);

        // Буферы отданы GDI и GDI+ и должны жить до конца процесса.
        static readonly List<IntPtr> _pinned = new List<IntPtr>();

        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

        static Fonts()
        {
            var asm = typeof(Fonts).Assembly;

            foreach (string file in new[] { "Onest-Regular.ttf", "Onest-Medium.ttf", "Onest-SemiBold.ttf", "JetBrainsMono.ttf" })
            {
                try
                {
                    using (var s = asm.GetManifestResourceStream("MacroKit.Fonts." + file))
                    {
                        if (s == null) continue;

                        var bytes = new byte[s.Length];
                        int read = 0;
                        while (read < bytes.Length) read += s.Read(bytes, read, bytes.Length - read);

                        IntPtr p = Marshal.AllocCoTaskMem(bytes.Length);
                        Marshal.Copy(bytes, 0, p, bytes.Length);
                        _pinned.Add(p);

                        _pfc.AddMemoryFont(p, bytes.Length);
                        uint count = 0;
                        AddFontMemResourceEx(p, (uint)bytes.Length, IntPtr.Zero, ref count);
                    }
                }
                catch
                {
                    // Нет шрифта — будет системный, программа от этого не ломается.
                }
            }

            foreach (var f in _pfc.Families) _families[f.Name] = f;
        }

        /// <summary>Семейство по имени из вложенных, иначе системный запасной.</summary>
        public static FontFamily Family(string name, string fallback)
        {
            FontFamily f;
            if (_families.TryGetValue(name, out f)) return f;
            return new FontFamily(fallback);
        }

        /// <summary>Шрифт интерфейса. Размер — в пикселях, как в макете.</summary>
        public static Font Ui(string weight, float px)
        {
            // В статических файлах у каждого начертания своё имя семейства.
            string name = weight == "Regular" ? "Onest" : "Onest " + weight;
            return new Font(Family(name, "Segoe UI"), px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        public static Font Mono(float px)
        {
            return new Font(Family("JetBrains Mono", "Consolas"), px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        /// <summary>Какие семейства реально подхватились — для проверки сборки.</summary>
        public static IEnumerable<string> Loaded { get { return _families.Keys; } }
    }
}
