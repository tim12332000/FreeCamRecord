using System.Drawing.Drawing2D;

namespace FreeCamRecord;

// A temporary, local-only selection surface. The desktop image is never uploaded or used as a recording preview.
internal sealed class RegionSelector : Form
{
    private readonly Bitmap desktop;
    private readonly Point origin;
    private readonly Size minimum;
    private readonly VisualRegionDetector detector;
    private readonly Panel actions = new();
    private readonly Label instructions = new();
    private readonly Button confirm = new(), retry = new(), cancel = new();
    private readonly System.Windows.Forms.Timer hover = new() { Interval = 80 };
    private IReadOnlyList<Rectangle> candidates = Array.Empty<Rectangle>();
    private Rectangle selection, beforeDrag;
    private Point pointer, dragStart, lastDetection = new(int.MinValue, int.MinValue);
    private int candidateIndex, dragMode;
    private bool locked, dragging;

    internal Rectangle SelectedArea => new(selection.X + origin.X, selection.Y + origin.Y, selection.Width, selection.Height);

    internal RegionSelector(Bitmap snapshot, Point origin, Size minimum)
    {
        desktop = (Bitmap)snapshot.Clone(); this.origin = origin; this.minimum = minimum;
        detector = new VisualRegionDetector(desktop);
        Text = UiText.T("選取錄製區域", "Select recording area");
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None; Bounds = new Rectangle(origin, desktop.Size);
        TopMost = true; ShowInTaskbar = false; DoubleBuffered = true; KeyPreview = true;
        Cursor = Cursors.Cross; Font = new Font("Microsoft JhengHei UI", 9);
        BackColor = FrameTheme.Toolbar; ForeColor = FrameTheme.Text;
        actions.BackColor = FrameTheme.Toolbar; actions.Size = new Size(Math.Min(680, desktop.Width - 24), 92);
        instructions.SetBounds(12, 8, actions.Width - 24, 26);
        instructions.TextAlign = ContentAlignment.MiddleCenter; instructions.ForeColor = FrameTheme.Text;
        confirm.Text = UiText.T("確定範圍", "Use area"); confirm.AccessibleName = UiText.T("確定錄製區域", "Use recording area");
        retry.Text = UiText.T("重新選取", "Reselect"); cancel.Text = UiText.T("取消", "Cancel");
        FrameTheme.ApplyButton(confirm, true); FrameTheme.ApplyButton(retry); FrameTheme.ApplyButton(cancel);
        int buttonsLeft = Math.Max(12, (actions.Width - 355) / 2);
        confirm.SetBounds(buttonsLeft, 45, 135, 34); retry.SetBounds(buttonsLeft + 145, 45, 115, 34); cancel.SetBounds(buttonsLeft + 270, 45, 85, 34);
        confirm.Click += (_, _) => AcceptSelection();
        retry.Click += (_, _) => { locked = false; selection = Rectangle.Empty; candidates = Array.Empty<Rectangle>(); lastDetection = new(int.MinValue, int.MinValue); RefreshInstructions(); Invalidate(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        actions.Controls.AddRange(new Control[] { instructions, confirm, retry, cancel }); Controls.Add(actions);
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
        instructions.Text = locked ? UiText.T("範圍已固定，可拖邊調整 · Enter 確定 · Esc 取消", "Area locked; drag edges to resize · Enter to use · Esc to cancel") :
            selection.IsEmpty ? UiText.T("移動滑鼠預選；也可直接拖曳框選 · Esc 取消", "Hover to preview or drag to select · Esc to cancel") : UiText.T("滑過即預選 · 點一下固定／直接拖曳框選 · 滾輪換範圍", "Hover to preview · Click to lock or drag to select · Scroll for options");
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
        if (dragMode == 32 && distance < 5) selection = beforeDrag;
        dragging = false; Capture = false;
        locked = !selection.IsEmpty;
        RefreshInstructions(); Invalidate();
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
        using var shade = new SolidBrush(Color.FromArgb(135, Color.Black));
        using var outside = new Region(new Rectangle(Point.Empty, desktop.Size));
        if (!selection.IsEmpty) outside.Exclude(selection);
        g.FillRegion(shade, outside);
        using var border = new Pen(confirm.Enabled ? FrameTheme.IdleBorder : FrameTheme.RecordingBorder, 2);
        if (!selection.IsEmpty)
        {
            g.DrawRectangle(border, selection.X, selection.Y, Math.Max(0, selection.Width - 1), Math.Max(0, selection.Height - 1));
            if (locked)
            {
                using var handle = new SolidBrush(FrameTheme.Text);
                foreach (int x in new[] { selection.Left, selection.Left + selection.Width / 2, selection.Right })
                    foreach (int y in new[] { selection.Top, selection.Top + selection.Height / 2, selection.Bottom })
                        if (x != selection.Left + selection.Width / 2 || y != selection.Top + selection.Height / 2) g.FillRectangle(handle, x - 3, y - 3, 6, 6);
            }
        }
        if (actions.Bounds.Contains(pointer)) return;
        using var cross = new Pen(Color.FromArgb(140, FrameTheme.IdleBorder));
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
        using var pen = new Pen(FrameTheme.IdleBorder, 2);
        graphics.DrawEllipse(pen, lens); graphics.DrawLine(pen, x + diameter / 2, y, x + diameter / 2, y + diameter); graphics.DrawLine(pen, x, y + diameter / 2, x + diameter, y + diameter / 2);
        string text = selection.IsEmpty ? UiText.T("拖曳框選", "Drag to select") : $"{selection.Width} × {selection.Height}";
        var label = new Rectangle(x - 8, y + diameter + 6, diameter + 16, 27);
        graphics.FillRectangle(Brushes.Black, label);
        TextRenderer.DrawText(graphics, text, Font, label, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { hover.Dispose(); detector.Dispose(); desktop.Dispose(); }
        base.Dispose(disposing);
    }
}
