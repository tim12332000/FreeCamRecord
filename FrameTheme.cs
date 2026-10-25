using System.Drawing;
using System.Windows.Forms;

namespace FreeCamRecord;

internal static class FrameTheme
{
    internal static Color Toolbar => Color.FromArgb(23, 34, 53);
    internal static Color Text => Color.FromArgb(237, 243, 250);
    internal static Color IdleBorder => Color.FromArgb(88, 161, 211);
    internal static Color RecordingBorder => Color.FromArgb(246, 77, 91);
    internal static Color RecordButton => Color.FromArgb(174, 39, 55);
    internal static Color MutedText => Color.FromArgb(164, 179, 197);
    internal static Color Hover => Color.FromArgb(44, 62, 84);

    internal static void ApplyButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? RecordButton : Toolbar;
        button.ForeColor = Text;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = button.Focused ? IdleBorder : button.BackColor;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(201, 48, 65) : Hover;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(145, 30, 44) : Color.FromArgb(56, 78, 102);
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

    internal static void ApplyMenu(ContextMenuStrip menu)
    {
        menu.BackColor = Toolbar;
        menu.ForeColor = Text;
        menu.Renderer = new DarkMenuRenderer();
        menu.ShowImageMargin = true;
        menu.ShowCheckMargin = false;
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
        public override Color MenuItemBorder => IdleBorder;
        public override Color MenuBorder => Hover;
        public override Color SeparatorDark => Hover;
        public override Color SeparatorLight => Toolbar;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
