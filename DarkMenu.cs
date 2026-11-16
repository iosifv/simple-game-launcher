using WinForms = System.Windows.Forms;

namespace GameLauncher
{
    /// <summary>Colours of the dark tray menu, matching the app's dark theme.</summary>
    static class DarkMenuColors
    {
        public static readonly System.Drawing.Color Back = System.Drawing.Color.FromArgb(0x2B, 0x2B, 0x30);
        public static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3D, 0x3D, 0x44);
        public static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(0x45, 0x45, 0x4D);
        public static readonly System.Drawing.Color Separator = System.Drawing.Color.FromArgb(0x48, 0x48, 0x50);
        public static readonly System.Drawing.Color Text = System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2);
        public static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(0x60, 0xCD, 0xFF);
    }

    /// <summary>Paints the WinForms tray menu dark: flat background, rounded hover highlight, accent check marks.</summary>
    class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new Table()) { RoundedEdges = false; }

        class Table : WinForms.ProfessionalColorTable
        {
            public override System.Drawing.Color ToolStripDropDownBackground => DarkMenuColors.Back;
            public override System.Drawing.Color ImageMarginGradientBegin => DarkMenuColors.Back;
            public override System.Drawing.Color ImageMarginGradientMiddle => DarkMenuColors.Back;
            public override System.Drawing.Color ImageMarginGradientEnd => DarkMenuColors.Back;
            public override System.Drawing.Color MenuBorder => DarkMenuColors.Border;
            public override System.Drawing.Color MenuItemBorder => DarkMenuColors.Hover;
            public override System.Drawing.Color MenuItemSelected => DarkMenuColors.Hover;
            public override System.Drawing.Color MenuItemSelectedGradientBegin => DarkMenuColors.Hover;
            public override System.Drawing.Color MenuItemSelectedGradientEnd => DarkMenuColors.Hover;
            public override System.Drawing.Color SeparatorDark => DarkMenuColors.Separator;
            public override System.Drawing.Color SeparatorLight => DarkMenuColors.Back;
            public override System.Drawing.Color CheckBackground => DarkMenuColors.Back;
            public override System.Drawing.Color CheckSelectedBackground => DarkMenuColors.Hover;
            public override System.Drawing.Color CheckPressedBackground => DarkMenuColors.Hover;
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new System.Drawing.Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using var path = Rounded(r, 4);
            using var brush = new System.Drawing.SolidBrush(DarkMenuColors.Hover);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkMenuColors.Text : System.Drawing.Color.FromArgb(0x80, 0x80, 0x88);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            // A plain accent check mark instead of the boxed light-theme one.
            var r = e.ImageRectangle;
            using var pen = new System.Drawing.Pen(DarkMenuColors.Accent, 2f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new System.Drawing.PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f),
                new System.Drawing.PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f),
                new System.Drawing.PointF(r.Left + r.Width * 0.84f, r.Top + r.Height * 0.28f)
            });
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(DarkMenuColors.Separator);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        static System.Drawing.Drawing2D.GraphicsPath Rounded(System.Drawing.Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
