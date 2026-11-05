using System.Runtime.InteropServices;

namespace SessionStartupManager;

// WinForms on .NET 8 has no dark mode, so every control is styled by hand. Native parts
// (title bar, scrollbars, combo drop-downs) get Windows' own dark themes through DWM/uxtheme.
internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(32, 32, 32);
    public static readonly Color Surface = Color.FromArgb(43, 43, 43);
    public static readonly Color Raised = Color.FromArgb(58, 58, 58);
    public static readonly Color Hover = Color.FromArgb(72, 72, 72);
    public static readonly Color Border = Color.FromArgb(78, 78, 78);
    public static readonly Color Text = Color.FromArgb(232, 232, 232);
    public static readonly Color Muted = Color.FromArgb(150, 150, 150);
    public static readonly Color Accent = Color.FromArgb(37, 99, 155); // the icon's blue
    public static readonly Color AccentHover = Color.FromArgb(48, 119, 180);
    public static readonly Color Selection = Color.FromArgb(70, 82, 102);

    public const string AccentTag = "accent";

    public static void Apply(Form form)
    {
        form.BackColor = Back;
        form.ForeColor = Text;
        OnHandle(form, h =>
        {
            int on = 1;
            Native.DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
        });
        ApplyTree(form, form);
    }

    static void ApplyTree(Control c, Form form)
    {
        Style(c, form);
        foreach (Control child in c.Controls) ApplyTree(child, form);
        c.ControlAdded += (_, e) => ApplyTree(e.Control, form);
    }

    static void Style(Control c, Form form)
    {
        switch (c)
        {
            case Button b:
                bool accent = Equals(b.Tag, AccentTag) || form.AcceptButton == b;
                b.FlatStyle = FlatStyle.Flat;
                b.UseVisualStyleBackColor = false;
                b.BackColor = accent ? Accent : Raised;
                b.ForeColor = accent ? Color.White : Text;
                b.FlatAppearance.BorderColor = accent ? Accent : Border;
                b.FlatAppearance.MouseOverBackColor = accent ? AccentHover : Hover;
                b.FlatAppearance.MouseDownBackColor = accent ? Accent : Border;
                break;
            case TextBox t:
                t.BackColor = Surface;
                t.ForeColor = Text;
                t.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.FlatStyle = FlatStyle.Flat;
                cb.BackColor = Surface;
                cb.ForeColor = Text;
                OnHandle(cb, h => Native.SetWindowTheme(h, "DarkMode_CFD", null));
                break;
            case NumericUpDown n:
                n.BackColor = Surface;
                n.ForeColor = Text;
                n.BorderStyle = BorderStyle.FixedSingle;
                break;
            case CheckBox chk:
                chk.FlatStyle = FlatStyle.Flat;
                chk.ForeColor = Text;
                chk.FlatAppearance.BorderColor = Muted;
                chk.FlatAppearance.CheckedBackColor = Surface;
                break;
            case ListBox lb: // includes CheckedListBox
                lb.BackColor = Surface;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.FixedSingle;
                OnHandle(lb, h => Native.SetWindowTheme(h, "DarkMode_Explorer", null));
                break;
            case ListView lv:
                StyleListView(lv);
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case ScrollBar sb:
                OnHandle(sb, h => Native.SetWindowTheme(h, "DarkMode_Explorer", null));
                break;
            case SplitContainer split:
                split.BackColor = Border; // shows through as the splitter line
                split.Panel1.BackColor = Back;
                split.Panel2.BackColor = Back;
                break;
            case Label l:
                l.ForeColor = l.ForeColor == SystemColors.GrayText ? Muted : Text;
                break;
        }
    }

    static void StyleGrid(DataGridView g)
    {
        g.EnableHeadersVisualStyles = false;
        g.BackgroundColor = Surface;
        g.GridColor = Border;
        g.BorderStyle = BorderStyle.FixedSingle;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        var header = g.ColumnHeadersDefaultCellStyle;
        header.BackColor = Raised;
        header.ForeColor = Text;
        header.SelectionBackColor = Raised;
        header.SelectionForeColor = Text;
        var cells = g.DefaultCellStyle;
        cells.BackColor = Surface;
        cells.ForeColor = Text;
        cells.SelectionBackColor = Selection;
        cells.SelectionForeColor = Color.White;
        foreach (DataGridViewColumn col in g.Columns)
            if (col is DataGridViewCheckBoxColumn check) check.FlatStyle = FlatStyle.Flat;
    }

    // The column header isn't themable through colors, so it's drawn here; rows draw normally.
    static void StyleListView(ListView lv)
    {
        lv.BackColor = Surface;
        lv.ForeColor = Text;
        lv.BorderStyle = BorderStyle.FixedSingle;
        lv.OwnerDraw = true;
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using var bg = new SolidBrush(Raised);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var line = new Pen(Border);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, r, Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        };
        OnHandle(lv, h => Native.SetWindowTheme(h, "DarkMode_Explorer", null));
    }

    public static void StyleToolTip(ToolTip tip)
    {
        tip.OwnerDraw = true;
        tip.Draw += (_, e) =>
        {
            using var bg = new SolidBrush(Raised);
            e.Graphics.FillRectangle(bg, e.Bounds);
            using var border = new Pen(Border);
            e.Graphics.DrawRectangle(border, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            var r = Rectangle.Inflate(e.Bounds, -6, -4);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, SystemFonts.StatusFont, r, Text, TextFormatFlags.WordBreak | TextFormatFlags.Left);
        };
        tip.Popup += (_, e) =>
        {
            var size = TextRenderer.MeasureText(tip.GetToolTip(e.AssociatedControl), SystemFonts.StatusFont, new Size(500, 0), TextFormatFlags.WordBreak);
            e.ToolTipSize = new Size(size.Width + 14, size.Height + 10);
        };
    }

    public static void StyleMenu(ToolStripDropDown menu)
    {
        menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = false };
        menu.BackColor = Surface;
        menu.ForeColor = Text;
    }

    sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    static void OnHandle(Control c, Action<IntPtr> act)
    {
        if (c.IsHandleCreated) act(c.Handle);
        c.HandleCreated += (_, _) => act(c.Handle);
    }
}
