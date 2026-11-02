using System.Drawing.Drawing2D;

namespace FreeCamRecord;

// A temporary, local-only selection surface. The desktop image is never uploaded or used as a recording preview.
internal sealed class RegionSelector : Form
{
    private readonly Bitmap desktop;
    private readonly Point origin;
    private readonly Size minimum;
    private readonly bool screenshotMode;
    private readonly VisualRegionDetector detector;
    private readonly Panel actions = new StudioPanel();
    private readonly Label heading = new(), instructions = new();
    private readonly Button confirm = new StudioButton(), retry = new StudioButton(), cancel = new StudioButton();
    private readonly System.Windows.Forms.Timer hover = new() { Interval = 80 };
    private IReadOnlyList<Rectangle> candidates = Array.Empty<Rectangle>();
    private Rectangle selection, beforeDrag;
    private Point pointer, dragStart, lastDetection = new(int.MinValue, int.MinValue);
    private int candidateIndex, dragMode;
    private bool locked, dragging;

    internal Rectangle SelectedArea => new(selection.X + origin.X, selection.Y + origin.Y, selection.Width, selection.Height);

    internal RegionSelector(Bitmap snapshot, Point origin, Size minimum, bool screenshotMode = false)
    {
        desktop = (Bitmap)snapshot.Clone(); this.origin = origin; this.minimum = minimum; this.screenshotMode = screenshotMode;
        detector = new VisualRegionDetector(desktop);
        Text = screenshotMode ? UiText.T("選取截圖區域", "Select screenshot area") : UiText.T("選取錄製區域", "Select recording area");
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; Bounds = new Rectangle(origin, desktop.Size);
        TopMost = true; ShowInTaskbar = false; DoubleBuffered = true; KeyPreview = true;
        Cursor = Cursors.Cross; Font = FrameTheme.NewFont();
        BackColor = FrameTheme.Toolbar; ForeColor = FrameTheme.Text;
        actions.Size = new Size(Math.Min(680, desktop.Width - 24), 130);
        heading.SetBounds(20, 14, actions.Width - 40, 24);
        heading.Text = screenshotMode ? UiText.T("剪取截圖", "SNIP SCREENSHOT") : UiText.T("選取錄影範圍", "RECORDING AREA");
        heading.Font = FrameTheme.NewFont(10, FontStyle.Bold);
        heading.TextAlign = ContentAlignment.MiddleCenter;
        heading.ForeColor = FrameTheme.Text;
        heading.BackColor = FrameTheme.SurfaceRaised;
        instructions.SetBounds(16, 43, actions.Width - 32, 22);
        instructions.TextAlign = ContentAlignment.MiddleCenter; instructions.ForeColor = FrameTheme.MutedText;
        instructions.BackColor = FrameTheme.SurfaceRaised;
        confirm.Text = UiText.T("確定範圍", "Use area"); confirm.AccessibleName = UiText.T("確定錄製區域", "Use recording area");
        if (screenshotMode) { confirm.Text = UiText.T("預覽截圖", "Preview screenshot"); confirm.AccessibleName = confirm.Text; }
        retry.Text = UiText.T("重新選取", "Reselect"); cancel.Text = UiText.T("取消", "Cancel");
        FrameTheme.ApplyButton(confirm, true); FrameTheme.ApplySecondaryButton(retry); FrameTheme.ApplyButton(cancel);
        foreach (var button in new[] { confirm, retry, cancel }) button.BackColor = FrameTheme.SurfaceRaised;
        int buttonsLeft = Math.Max(12, (actions.Width - 355) / 2);
        confirm.SetBounds(buttonsLeft, 80, 135, 34); retry.SetBounds(buttonsLeft + 145, 80, 115, 34); cancel.SetBounds(buttonsLeft + 270, 80, 85, 34);
        confirm.Click += (_, _) => AcceptSelection();
        retry.Click += (_, _) => { locked = false; selection = Rectangle.Empty; candidates = Array.Empty<Rectangle>(); lastDetection = new(int.MinValue, int.MinValue); RefreshInstructions(); Invalidate(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.AddRange(new Control[] { heading, instructions, confirm, retry, cancel }); Controls.Add(actions);
        AcceptButton = confirm; CancelButton = cancel;
        hover.Tick += (_, _) => DetectAtPointer(); hover.Start();
        Shown += (_, _) => { pointer = PointToClient(System.Windows.Forms.Cursor.Position); PlaceActions(); DetectAtPointer(); Activate(); };
        RefreshInstructions();
    }

    private void PlaceActions()
    {
        // Screen information only positions controls. No window or application bounds choose the recording area.
        var screen = Screen.FromPoint(new Point(pointer.X + origin.X, pointer.Y + origin.Y)).WorkingArea;
        int x = screen.Left - origin.X + (screen.Width - actions.Width) / 2;
        int y = screen.Bottom - origin.Y - actions.Height - 20;
        actions.Location = new Point(Math.Clamp(x, 0, Math.Max(0, Width - actions.Width)), Math.Clamp(y, 0, Math.Max(0, Height - actions.Height)));
    }

    private void DetectAtPointer()
    {
        if (locked || dragging || actions.Bounds.Contains(pointer)) return;
        if (lastDetection != new Point(int.MinValue, int.MinValue) && Math.Abs(pointer.X - lastDetection.X) + Math.Abs(pointer.Y - lastDetection.Y) < 8) return;
        lastDetection = pointer;
        var found = detector.FindCandidates(pointer, minimum);
        if (found.Count > 0) { candidates = found; candidateIndex = 0; selection = found[0]; }
        // Keep a good proposal while moving across an ambiguous gutter toward Confirm.
        PlaceActions(); RefreshInstructions(); Invalidate();
    }

    private void RefreshInstructions()
    {
        confirm.Enabled = selection.Width >= minimum.Width && selection.Height >= minimum.Height;
        instructions.Text = locked ? UiText.T("拖曳邊緣微調範圍　·　Enter 確認　·　Esc 取消", "Drag edges to refine  ·  Enter to confirm  ·  Esc to cancel") :
            selection.IsEmpty ? UiText.T("拖曳框選，或移動滑鼠預選範圍　·　Esc 取消", "Drag a region, or hover to find an area  ·  Esc to cancel") : UiText.T("點一下固定範圍　·　滾輪切換　·　也可拖曳框選", "Click to lock  ·  Scroll for options  ·  Or drag a region");
        if (screenshotMode) instructions.Text = UiText.T("拖曳框選，放開即可預覽　·　Esc 取消", "Drag to select, release to preview  ·  Esc to cancel");
    }

    private void AcceptSelection()
    {
        if (!confirm.Enabled) return;
        DialogResult = DialogResult.OK; Close();
    }

    private int HitEdges(Point point)
    {
        if (!locked || selection.IsEmpty) return 0;
        const int reach = 8;
        var hit = selection; hit.Inflate(reach, reach);
        if (!hit.Contains(point)) return 0;
        return (Math.Abs(point.X - selection.Left) <= reach ? 1 : Math.Abs(point.X - selection.Right) <= reach ? 2 : 0)
            | (Math.Abs(point.Y - selection.Top) <= reach ? 4 : Math.Abs(point.Y - selection.Bottom) <= reach ? 8 : 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
        pointer = e.Location; dragStart = e.Location; beforeDrag = selection;
        dragMode = HitEdges(e.Location);
        if (dragMode == 0) dragMode = locked && selection.Contains(e.Location) ? 16 : 32;
        dragging = true; Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointer = e.Location;
        if (dragging)
        {
            int dx = pointer.X - dragStart.X, dy = pointer.Y - dragStart.Y;
            if (dragMode == 32)
                selection = Rectangle.FromLTRB(Math.Min(dragStart.X, pointer.X), Math.Min(dragStart.Y, pointer.Y), Math.Max(dragStart.X, pointer.X), Math.Max(dragStart.Y, pointer.Y));
            else if (dragMode == 16)
                selection = new Rectangle(Math.Clamp(beforeDrag.X + dx, 0, Width - beforeDrag.Width), Math.Clamp(beforeDrag.Y + dy, 0, Height - beforeDrag.Height), beforeDrag.Width, beforeDrag.Height);
            else
            {
                int l = beforeDrag.Left, r = beforeDrag.Right, t = beforeDrag.Top, b = beforeDrag.Bottom;
                if ((dragMode & 1) != 0) l = Math.Clamp(l + dx, 0, r - minimum.Width);
                if ((dragMode & 2) != 0) r = Math.Clamp(r + dx, l + minimum.Width, Width);
                if ((dragMode & 4) != 0) t = Math.Clamp(t + dy, 0, b - minimum.Height);
                if ((dragMode & 8) != 0) b = Math.Clamp(b + dy, t + minimum.Height, Height);
                selection = Rectangle.FromLTRB(l, t, r, b);
            }
            selection.Intersect(new Rectangle(Point.Empty, desktop.Size));
            RefreshInstructions();
        }
        else
        {
            Cursor = HitEdges(pointer) switch { 5 or 10 => Cursors.SizeNWSE, 6 or 9 => Cursors.SizeNESW, 1 or 2 => Cursors.SizeWE, 4 or 8 => Cursors.SizeNS, _ => locked && selection.Contains(pointer) ? Cursors.SizeAll : Cursors.Cross };
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !dragging) return;
        int distance = Math.Abs(e.X - dragStart.X) + Math.Abs(e.Y - dragStart.Y);
        if (dragMode == 32 && distance < 5 && (!screenshotMode || selection.IsEmpty)) selection = beforeDrag;
        dragging = false; Capture = false;
        locked = !selection.IsEmpty;
        RefreshInstructions(); Invalidate();
        if (screenshotMode && confirm.Enabled) AcceptSelection();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e); if (!Capture) dragging = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (locked || candidates.Count < 2) return;
        candidateIndex = (candidateIndex + (e.Delta < 0 ? 1 : candidates.Count - 1)) % candidates.Count;
        selection = candidates[candidateIndex]; RefreshInstructions(); Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(desktop, new Rectangle(Point.Empty, desktop.Size), 0, 0, desktop.Width, desktop.Height, GraphicsUnit.Pixel);
        using var shade = new SolidBrush(Color.FromArgb(155, 8, 11, 13));
        using var outside = new Region(new Rectangle(Point.Empty, desktop.Size));
        if (!selection.IsEmpty) outside.Exclude(selection);
        g.FillRegion(shade, outside);
        using var border = new Pen(confirm.Enabled ? FrameTheme.Accent : FrameTheme.MutedText, 1);
        if (!selection.IsEmpty)
        {
            g.DrawRectangle(border, selection.X, selection.Y, Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
            if (locked)
            {
                using var handle = new SolidBrush(FrameTheme.Accent);
                foreach (int x in new[] { selection.Left, selection.Left + selection.Width / 2, selection.Right })
                    foreach (int y in new[] { selection.Top, selection.Top + selection.Height / 2, selection.Bottom })
                        if (x != selection.Left + selection.Width / 2 || y != selection.Top + selection.Height / 2) g.FillEllipse(handle, x - 3, y - 3, 6, 6);
            }
        }
        if (actions.Bounds.Contains(pointer)) return;
        using var cross = new Pen(Color.FromArgb(60, FrameTheme.Accent));
        g.DrawLine(cross, pointer.X, 0, pointer.X, Height); g.DrawLine(cross, 0, pointer.Y, Width, pointer.Y);
        DrawMagnifier(g);
    }

    private void DrawMagnifier(Graphics graphics)
    {
        const int diameter = 112, sample = 20;
        int x = Math.Clamp(pointer.X + 28, 0, Math.Max(0, Width - diameter - 8));
        int y = Math.Clamp(pointer.Y + 28, 0, Math.Max(0, Height - diameter - 42));
        var lens = new Rectangle(x, y, diameter, diameter);
        var source = new Rectangle(Math.Clamp(pointer.X - sample / 2, 0, desktop.Width - sample), Math.Clamp(pointer.Y - sample / 2, 0, desktop.Height - sample), sample, sample);
        var state = graphics.Save();
        using var circle = new GraphicsPath(); circle.AddEllipse(lens); graphics.SetClip(circle);
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.DrawImage(desktop, lens, source, GraphicsUnit.Pixel);
        graphics.Restore(state);
        using var pen = new Pen(FrameTheme.Border, 2);
        graphics.DrawEllipse(pen, lens); graphics.DrawLine(pen, x + diameter / 2, y, x + diameter / 2, y + diameter); graphics.DrawLine(pen, x, y + diameter / 2, x + diameter, y + diameter / 2);
        string text = selection.IsEmpty ? UiText.T("拖曳框選", "Drag to select") : $"{selection.Width} × {selection.Height}";
        var label = new Rectangle(x - 8, y + diameter + 6, diameter + 16, 27);
        using var labelShape = FrameTheme.RoundedRectangle(label, 6);
        using var labelFill = new SolidBrush(FrameTheme.Toolbar);
        graphics.FillPath(labelFill, labelShape);
        TextRenderer.DrawText(graphics, text, Font, label, FrameTheme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hover.Dispose(); detector.Dispose(); desktop.Dispose(); }
        base.Dispose(disposing);
    }
}
