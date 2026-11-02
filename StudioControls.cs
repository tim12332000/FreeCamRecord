using System.Drawing.Drawing2D;

namespace FreeCamRecord;

internal enum ButtonTone { Quiet, Secondary, Accent, Recording, Danger }

// A single rendering surface for tools, actions and window controls. These stay
// real WinForms buttons so keyboard, accessibility and existing clicks work.
internal sealed class StudioButton : Button
{
    private bool hovered, pressed;
    private ButtonTone tone;
    internal ButtonTone Tone { get => tone; set { tone = value; Invalidate(); } }

    internal StudioButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = false; Invalidate(); } base.OnKeyUp(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) hovered = pressed = false; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(BackColor);
        Color fill = Tone switch
        {
            ButtonTone.Accent => FrameTheme.Accent,
            ButtonTone.Recording => FrameTheme.RecordButton,
            ButtonTone.Secondary => FrameTheme.SurfaceRaised,
            _ => BackColor
        };
        if (Enabled && (hovered || pressed))
            fill = Tone switch
            {
                ButtonTone.Accent => pressed ? Color.FromArgb(92, 192, 161) : Color.FromArgb(150, 235, 205),
                ButtonTone.Recording => pressed ? Color.FromArgb(178, 55, 67) : Color.FromArgb(239, 105, 114),
                ButtonTone.Danger => Color.FromArgb(170, 55, 66),
                _ => pressed ? FrameTheme.Pressed : FrameTheme.Hover
            };
        if (!Enabled) fill = BackColor;
        var bounds = new RectangleF(.5f, .5f, Width - 1, Height - 1);
        using var path = FrameTheme.RoundedRectangle(bounds, 7);
        using (var brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
        if (Tone == ButtonTone.Secondary || (Focused && ShowFocusCues))
        {
            using var pen = new Pen(Focused && ShowFocusCues ? FrameTheme.Accent : FrameTheme.Border);
            graphics.DrawPath(pen, path);
        }
        Color text = !Enabled ? FrameTheme.MutedText : Tone == ButtonTone.Accent ? FrameTheme.AccentText : ForeColor;
        int iconWidth = Image?.Width ?? 0;
        int textWidth = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int gap = iconWidth > 0 && textWidth > 0 ? 6 : 0;
        int x = Math.Max(4, (Width - iconWidth - gap - textWidth) / 2);
        if (Image != null)
        {
            var location = new Point(x, (Height - Image.Height) / 2);
            if (Enabled) graphics.DrawImageUnscaled(Image, location);
            else ControlPaint.DrawImageDisabled(graphics, Image, location.X, location.Y, BackColor);
            x += iconWidth + gap;
        }
        if (textWidth > 0)
            TextRenderer.DrawText(graphics, Text, Font, new Rectangle(x, 0, Math.Max(0, Width - x - 4), Height), text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal sealed class StudioPanel : Panel
{
    internal Color FillColor { get; set; } = FrameTheme.SurfaceRaised;
    internal Color BorderColor { get; set; } = FrameTheme.Border;
    internal int CornerRadius { get; set; } = 12;

    internal StudioPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = FrameTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), CornerRadius);
        using var fill = new SolidBrush(FillColor);
        using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
    }
}

internal sealed class StudioDialog : Form
{
    private readonly Button closeButton = new StudioButton();
    private readonly Bitmap closeIcon;

    internal StudioDialog(string title, Size size)
    {
        Text = title; ClientSize = size;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = FrameTheme.Toolbar; ForeColor = FrameTheme.Text;
        Font = FrameTheme.NewFont(); DoubleBuffered = true;
        using var shape = FrameTheme.RoundedRectangle(ClientRectangle, 12);
        Region = new Region(shape);
        var heading = new Label { Text = title, Font = FrameTheme.NewFont(11, FontStyle.Bold), ForeColor = FrameTheme.Text, Bounds = new Rectangle(24, 20, Width - 80, 28) };
        closeIcon = ArtIcons.Create(ArtIcon.Close, 18, FrameTheme.MutedText);
        FrameTheme.ApplyWindowButton(closeButton, close: true);
        closeButton.Image = closeIcon;
        closeButton.AccessibleName = UiText.T("關閉對話框", "Close dialog");
        closeButton.SetBounds(Width - 44, 16, 28, 28);
        closeButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        foreach (Control surface in new Control[] { this, heading })
            surface.MouseDown += (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                Native.ReleaseCapture(); Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
            };
        Controls.AddRange(new Control[] { heading, closeButton });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = FrameTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var pen = new Pen(FrameTheme.Border); e.Graphics.DrawPath(pen, shape);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) closeIcon.Dispose();
        base.Dispose(disposing);
    }
}

// Retain NumericUpDown's validation, keyboard and wheel behavior, while covering
// its OS-colored arrow surface with matching accessible buttons.
internal sealed class StudioSpinButtons : Panel
{
    private readonly NumericUpDown input;
    private readonly Button increase = new StudioButton(), decrease = new StudioButton();
    private readonly Bitmap up, down;

    internal StudioSpinButtons(NumericUpDown input)
    {
        this.input = input;
        BackColor = FrameTheme.SurfaceRaised;
        up = ArtIcons.Create(ArtIcon.ChevronUp, 12, FrameTheme.MutedText);
        down = ArtIcons.Create(ArtIcon.ChevronDown, 12, FrameTheme.MutedText);
        foreach (var button in new[] { increase, decrease })
        {
            FrameTheme.ApplyButton(button); button.BackColor = BackColor; button.TabStop = false;
        }
        increase.Image = up; decrease.Image = down;
        increase.AccessibleName = UiText.T("增加", "Increase ") + input.AccessibleName;
        decrease.AccessibleName = UiText.T("減少", "Decrease ") + input.AccessibleName;
        increase.Click += (_, _) => { input.Focus(); input.UpButton(); };
        decrease.Click += (_, _) => { input.Focus(); input.DownButton(); };
        Controls.AddRange(new Control[] { increase, decrease });
        input.SizeChanged += SyncBounds;
        SyncBounds(this, EventArgs.Empty);
    }

    private void SyncBounds(object? sender, EventArgs e)
    {
        Bounds = new Rectangle(input.Width - 20, 0, 20, input.Height);
        int half = Height / 2;
        increase.SetBounds(0, 0, Width, half);
        decrease.SetBounds(0, half, Width, Height - half);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            input.SizeChanged -= SyncBounds;
            up.Dispose(); down.Dispose();
        }
        base.Dispose(disposing);
    }
}
