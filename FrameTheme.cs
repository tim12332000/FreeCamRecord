using System.Drawing;
using System.Windows.Forms;
using System.Drawing.Drawing2D;

namespace FreeCamRecord;

internal static class FrameTheme
{
    internal static Color Canvas => Color.FromArgb(18, 20, 22);
    internal static Color Toolbar => Color.FromArgb(32, 35, 38);
    internal static Color SurfaceRaised => Color.FromArgb(41, 45, 49);
    internal static Color Border => Color.FromArgb(57, 62, 67);
    internal static Color Text => Color.FromArgb(238, 241, 240);
    internal static Color MutedText => Color.FromArgb(155, 163, 169);
    internal static Color Accent => Color.FromArgb(118, 221, 188);
    internal static Color AccentText => Color.FromArgb(17, 44, 36);
    internal static Color IdleBorder => Color.FromArgb(118, 197, 175);
    internal static Color RecordingBorder => Color.FromArgb(240, 114, 124);
    internal static Color RecordButton => Color.FromArgb(208, 69, 83);
    internal static Color Hover => Color.FromArgb(55, 60, 65);
    internal static Color Pressed => Color.FromArgb(65, 71, 76);

    internal static Font NewFont(float size = 9, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    internal static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        if (roundBottom)
        {
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        }
        else
        {
            path.AddLine(rectangle.Right, rectangle.Top + diameter / 2, rectangle.Right, rectangle.Bottom);
            path.AddLine(rectangle.Right, rectangle.Bottom, rectangle.Left, rectangle.Bottom);
            path.AddLine(rectangle.Left, rectangle.Bottom, rectangle.Left, rectangle.Top + diameter / 2);
        }
        path.CloseFigure();
        return path;
    }

    internal static void ApplyButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Toolbar;
        button.ForeColor = primary ? AccentText : Text;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Pressed;
        if (button is StudioButton styled) styled.Tone = primary ? ButtonTone.Accent : ButtonTone.Quiet;
        button.Cursor = Cursors.Hand;
        button.TabStop = true;
        button.Margin = Padding.Empty;
        button.ImageAlign = ContentAlignment.MiddleLeft;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.GotFocus -= ShowFocus;
        button.LostFocus -= HideFocus;
        button.GotFocus += ShowFocus;
        button.LostFocus += HideFocus;
    }

    internal static void ApplySecondaryButton(Button button)
    {
        ApplyButton(button);
        if (button is StudioButton styled) styled.Tone = ButtonTone.Secondary;
    }

    internal static void ApplyRecordingButton(Button button)
    {
        ApplyButton(button);
        if (button is StudioButton styled) styled.Tone = ButtonTone.Recording;
    }

    internal static void ApplyWindowButton(Button button, bool close = false)
    {
        ApplyButton(button);
        button.TabStop = false;
        button.ForeColor = MutedText;
        if (button is StudioButton styled && close) styled.Tone = ButtonTone.Danger;
    }

    internal static void ApplyMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Toolbar;
        menu.ForeColor = Text;
        menu.Renderer = new DarkMenuRenderer();
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
        menu.Font = NewFont(9.5f);
        menu.Padding = new Padding(6);
        menu.DropShadowEnabled = true;
        menu.Opening += (_, _) => StyleMenuItems(menu.Items);
    }

    private static void StyleMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.Padding = item is ToolStripSeparator ? new Padding(3, 4, 3, 4) : new Padding(4, 5, 10, 5);
            if (item is ToolStripMenuItem submenu)
            {
                submenu.DropDown.Font = submenu.Font;
                submenu.DropDown.Padding = new Padding(6);
                submenu.DropDown.Renderer = new DarkMenuRenderer();
                StyleMenuItems(submenu.DropDownItems);
            }
        }
    }

    private static void ShowFocus(object? sender, EventArgs e)
    {
        if (sender is Button button) button.FlatAppearance.BorderColor = IdleBorder;
    }

    private static void HideFocus(object? sender, EventArgs e)
    {
        if (sender is Button button) button.FlatAppearance.BorderColor = button.BackColor;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        internal DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedRectangle(new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), 6);
            using var fill = new SolidBrush(Hover);
            e.Graphics.FillPath(fill, path);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : MutedText;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? Text : MutedText;
            base.OnRenderArrow(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var side = Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height);
            if (side <= 0) return;
            using var check = ArtIcons.Create(ArtIcon.Check, side, e.Item.Enabled ? Text : MutedText);
            e.Graphics.DrawImageUnscaled(check, e.ImageRectangle.Location);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Toolbar;
        public override Color ImageMarginGradientBegin => Toolbar;
        public override Color ImageMarginGradientMiddle => Toolbar;
        public override Color ImageMarginGradientEnd => Toolbar;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color MenuBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Toolbar;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
