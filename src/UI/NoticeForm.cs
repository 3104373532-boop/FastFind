using System;
using System.Drawing;
using System.Windows.Forms;

namespace FastFind
{
    /// <summary>
    /// DPI-safe replacement for MessageBox when the text is long: the system dialog
    /// mis-measures wrapped text at 150–200% scaling.
    /// </summary>
    internal sealed class NoticeForm : Form
    {
        private NoticeForm(Theme theme, string title, string message, bool warning)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = theme.Surface;
            ForeColor = theme.TextMain;
            Font = theme.Ui;
            Padding = new Padding(0);

            int pad = theme.S(24);
            int iconSize = warning ? theme.S(28) : theme.S(20);
            int maxTextWidth = theme.S(430);

            var icon = new Label();
            icon.Text = warning ? "!" : "i";
            icon.TextAlign = ContentAlignment.MiddleCenter;
            icon.Font = new Font(theme.Ui.FontFamily, warning ? 16f : 12f, FontStyle.Bold);
            icon.ForeColor = warning ? Color.FromArgb(210, 140, 0) : theme.Accent;
            icon.BackColor = Color.Transparent;
            icon.Bounds = new Rectangle(pad, pad + theme.S(2), iconSize, iconSize);

            var text = new Label();
            text.Text = message;
            text.AutoSize = true;
            text.MaximumSize = new Size(maxTextWidth, 0);
            text.ForeColor = theme.TextMain;
            text.BackColor = Color.Transparent;
            text.Location = new Point(pad + iconSize + theme.S(12), pad);
            text.UseMnemonic = false;

            var ok = new Button();
            ok.Text = "确定";
            ok.FlatStyle = FlatStyle.System;
            ok.Size = new Size(theme.S(96), theme.S(32));
            ok.UseVisualStyleBackColor = true;
            ok.DialogResult = DialogResult.OK;

            Controls.Add(icon);
            Controls.Add(text);
            Controls.Add(ok);

            int width = pad * 2 + iconSize + theme.S(12) + maxTextWidth;
            int height = pad * 2 + Math.Max(iconSize, text.PreferredHeight) + theme.S(20) + ok.Height;
            ClientSize = new Size(width, height);
            ApplyTitleBarTheme(theme);
            ok.Location = new Point((ClientSize.Width - ok.Width) / 2, ClientSize.Height - pad - ok.Height);

            AcceptButton = ok;
            CancelButton = ok;
        }

        private void ApplyTitleBarTheme(Theme theme)
        {
            if (!theme.Dark) return;
            try
            {
                int dark = 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void Show(Form owner, Theme theme, string message, bool warning)
        {
            using (var form = new NoticeForm(theme, "FastFind", message, warning))
            {
                form.ShowDialog(owner);
            }
        }
    }
}





