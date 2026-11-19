using System;
using System.Drawing;
using System.Drawing.Text;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>Central place for colors, fonts and DPI aware metrics.</summary>
    internal sealed class Theme
    {
        public bool Dark;

        public Color Bg;
        public Color Surface;
        public Color SurfaceAlt;
        public Color Line;
        public Color TextMain;
        public Color TextSub;
        public Color TextFaint;
        public Color Accent;
        public Color AccentText;
        public Color SelectBg;
        public Color Hover;
        public Color Danger;

        public float Scale = 1f;
        public Font Ui;
        public Font UiSmall;
        public Font SearchFont;
        public Font RowFont;
        public Font RowFontBold;
        public Font IconFont;

        public Theme(bool dark, int dpi)
        {
            Dark = dark;
            Scale = dpi <= 0 ? 1f : dpi / 96f;
            if (Scale < 0.75f) Scale = 0.75f;
            if (Scale > 3f) Scale = 3f;
            ApplyPalette();
            BuildFonts();
        }

        /// <summary>Switch palette in place; every control paints from this instance, so a repaint applies it live.</summary>
        public void SetDark(bool dark)
        {
            if (Dark == dark) return;
            Dark = dark;
            ApplyPalette();
        }

        private void ApplyPalette()
        {
            if (Dark)
            {
                Bg = Color.FromArgb(0x1E, 0x1F, 0x22);
                Surface = Color.FromArgb(0x26, 0x28, 0x2C);
                SurfaceAlt = Color.FromArgb(0x2B, 0x2E, 0x33);
                Line = Color.FromArgb(0x34, 0x37, 0x3C);
                TextMain = Color.FromArgb(0xE6, 0xE8, 0xEA);
                TextSub = Color.FromArgb(0x9A, 0xA1, 0xAA);
                TextFaint = Color.FromArgb(0x6E, 0x76, 0x80);
                Accent = Color.FromArgb(0x4C, 0x9A, 0xFF);
                AccentText = Color.FromArgb(0xE8, 0xF1, 0xFE);
                SelectBg = Color.FromArgb(0x1F, 0x3A, 0x5F);
                Hover = Color.FromArgb(0x2B, 0x2E, 0x33);
                Danger = Color.FromArgb(0xFF, 0x7B, 0x72);
            }
            else
            {
                Bg = Color.FromArgb(0xF7, 0xF8, 0xFA);
                Surface = Color.White;
                SurfaceAlt = Color.FromArgb(0xF4, 0xF6, 0xF9);
                Line = Color.FromArgb(0xE3, 0xE6, 0xEA);
                TextMain = Color.FromArgb(0x1B, 0x1F, 0x24);
                TextSub = Color.FromArgb(0x5C, 0x65, 0x70);
                TextFaint = Color.FromArgb(0x8A, 0x93, 0x9E);
                Accent = Color.FromArgb(0x0B, 0x6C, 0xD6);
                AccentText = Color.FromArgb(0x08, 0x4C, 0x99);
                SelectBg = Color.FromArgb(0xE8, 0xF1, 0xFE);
                Hover = Color.FromArgb(0xF0, 0xF2, 0xF5);
                Danger = Color.FromArgb(0xD9, 0x30, 0x25);
            }
        }

        private void BuildFonts()
        {
            string family = PickFamily();
            Ui = new Font(family, 9f, FontStyle.Regular, GraphicsUnit.Point);
            UiSmall = new Font(family, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
            SearchFont = new Font(family, 14f, FontStyle.Regular, GraphicsUnit.Point);
            RowFont = new Font(family, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            RowFontBold = new Font(family, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
            IconFont = new Font("Segoe MDL2 Assets", 10f, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static string PickFamily()
        {
            string[] wanted = { "Segoe UI Variable Text", "Segoe UI", "Microsoft YaHei UI", "Tahoma" };
            foreach (string name in wanted)
            {
                try
                {
                    var f = new FontFamily(name);
                    if (f != null) return name;
                }
                catch (Exception) { }
            }
            return "Arial";
        }

        public int S(int value)
        {
            int scaled = (int)Math.Round(value * Scale);
            return scaled < 1 ? 1 : scaled;
        }

        public float SF(float value)
        {
            return value * Scale;
        }

        public void Dispose()
        {
            if (Ui != null) Ui.Dispose();
            if (UiSmall != null) UiSmall.Dispose();
            if (SearchFont != null) SearchFont.Dispose();
            if (RowFont != null) RowFont.Dispose();
            if (RowFontBold != null) RowFontBold.Dispose();
            if (IconFont != null) IconFont.Dispose();
        }
    }
}