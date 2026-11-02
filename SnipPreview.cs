using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FreeCamRecord;

// Owns a frozen crop: saving and copying never capture the selector or its controls.
internal sealed class SnipPreview : Form
{
    private readonly Bitmap image;
    private readonly PictureBox preview;
    private readonly Button saveButton = new StudioButton(), copyButton = new StudioButton();
    private readonly Button minimizeButton = new StudioButton(), maximizeButton = new StudioButton(), closeButton = new StudioButton();
    private readonly Label status = new();
    private readonly List<Bitmap> artwork = new();
    private readonly Panel canvas;
    private readonly Bitmap maximizeIcon, restoreIcon;
    private readonly string outputDirectory;
    private bool arranging;

    internal SnipPreview(Bitmap snapshot, string outputDirectory)
    {
        image = (Bitmap)snapshot.Clone();
        this.outputDirectory = outputDirectory;
        Text = UiText.T("截圖預覽", "Screenshot preview");
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        Font = FrameTheme.NewFont();
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        DoubleBuffered = true;
        BackColor = FrameTheme.Toolbar;
        ForeColor = FrameTheme.Text;
        TopMost = true;
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(Math.Min(work.Width - 40, Math.Clamp(image.Width + 96, 720, 1200)),
            Math.Min(work.Height - 60, Math.Clamp(image.Height + 170, 480, 860)));
        MinimumSize = new Size(Math.Min(720, Width), Math.Min(480, Height));
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 80, BackColor = FrameTheme.Toolbar, Width = ClientSize.Width - 2 };
        var mark = new PictureBox { Image = Artwork(ArtIcon.Scissors, FrameTheme.Accent), Size = new Size(24, 24), Location = new Point(22, 28), SizeMode = PictureBoxSizeMode.CenterImage };
        var title = new Label { Text = Text, Font = FrameTheme.NewFont(10, FontStyle.Bold), ForeColor = FrameTheme.Text, Location = new Point(58, 20), Height = 23, AutoEllipsis = true };
        var subtitle = new Label { Text = "FreeCamRecord", Font = FrameTheme.NewFont(8), ForeColor = FrameTheme.MutedText, Location = new Point(58, 44), Height = 18, AutoEllipsis = true };
        saveButton.Text = UiText.T("儲存", "Save");
        copyButton.Text = UiText.T("複製", "Copy");
        saveButton.AccessibleName = UiText.T("儲存截圖", "Save screenshot");
        copyButton.AccessibleName = UiText.T("複製截圖", "Copy screenshot");
        FrameTheme.ApplyButton(saveButton, true); FrameTheme.ApplySecondaryButton(copyButton);
        saveButton.Image = Artwork(ArtIcon.Save, FrameTheme.AccentText);
        copyButton.Image = Artwork(ArtIcon.Copy, FrameTheme.Text);
        FrameTheme.ApplyWindowButton(minimizeButton);
        FrameTheme.ApplyWindowButton(maximizeButton);
        FrameTheme.ApplyWindowButton(closeButton, close: true);
        minimizeButton.Image = Artwork(ArtIcon.Minimize, FrameTheme.MutedText);
        maximizeIcon = Artwork(ArtIcon.Maximize, FrameTheme.MutedText);
        restoreIcon = Artwork(ArtIcon.Restore, FrameTheme.MutedText);
        maximizeButton.Image = maximizeIcon;
        closeButton.Image = Artwork(ArtIcon.Close, FrameTheme.MutedText);
        minimizeButton.AccessibleName = UiText.T("最小化預覽", "Minimize preview");
        maximizeButton.AccessibleName = UiText.T("最大化或還原預覽", "Maximize or restore preview");
        closeButton.AccessibleName = UiText.T("關閉預覽", "Close preview");
        toolbar.Controls.AddRange(new Control[] { mark, title, subtitle, saveButton, copyButton, minimizeButton, maximizeButton, closeButton });
        toolbar.Layout += (_, _) =>
        {
            int width = toolbar.ClientSize.Width;
            saveButton.SetBounds(width - 356, 22, 104, 36);
            copyButton.SetBounds(width - 244, 22, 104, 36);
            minimizeButton.SetBounds(width - 116, 24, 32, 32);
            maximizeButton.SetBounds(width - 80, 24, 32, 32);
            closeButton.SetBounds(width - 44, 24, 32, 32);
            title.Width = subtitle.Width = Math.Max(80, saveButton.Left - title.Left - 24);
        };
        toolbar.Paint += (_, e) => { using var pen = new Pen(FrameTheme.Border); e.Graphics.DrawLine(pen, 0, toolbar.Height - 1, toolbar.Width, toolbar.Height - 1); };
        foreach (Control surface in new Control[] { toolbar, mark, title, subtitle }) surface.MouseDown += MoveWindow;

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = FrameTheme.Toolbar, Width = ClientSize.Width - 2 };
        status.Text = $"{image.Width} × {image.Height} px   ·   PNG";
        status.TextAlign = ContentAlignment.MiddleLeft;
        status.AutoEllipsis = true;
        status.ForeColor = FrameTheme.MutedText;
        status.Font = FrameTheme.NewFont(8);
        var shortcuts = new Label { Text = UiText.T("Ctrl+S 儲存  ·  Ctrl+C 複製  ·  Esc 關閉", "Ctrl+S Save  ·  Ctrl+C Copy  ·  Esc Close"), Font = FrameTheme.NewFont(8), ForeColor = FrameTheme.MutedText, TextAlign = ContentAlignment.MiddleRight };
        footer.Controls.AddRange(new Control[] { status, shortcuts });
        footer.Layout += (_, _) =>
        {
            shortcuts.SetBounds(Math.Max(24, footer.Width - 320), 1, 296, footer.Height - 1);
            status.SetBounds(24, 1, Math.Max(40, shortcuts.Left - 40), footer.Height - 1);
        };
        footer.Paint += (_, e) => { using var pen = new Pen(FrameTheme.Border); e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };

        canvas = new PreviewCanvas { Dock = DockStyle.Fill, AutoScroll = true, BackColor = FrameTheme.Canvas };
        preview = new PictureBox { Image = image, SizeMode = PictureBoxSizeMode.AutoSize, AccessibleName = UiText.T("截圖內容", "Screenshot") };
        canvas.Controls.Add(preview);
        canvas.AutoScrollMinSize = new Size(image.Width + 48, image.Height + 48);
        canvas.Layout += (_, _) => ArrangeImage();
        canvas.Scroll += (_, _) => { ArrangeImage(); canvas.Invalidate(); };
        canvas.Paint += (_, e) =>
        {
            var bounds = preview.Bounds;
            using var shadow = new SolidBrush(Color.FromArgb(7, 9, 11));
            e.Graphics.FillRectangle(shadow, bounds.X - 3, bounds.Y + 5, bounds.Width + 6, bounds.Height + 3);
            using var edge = new Pen(FrameTheme.Border);
            e.Graphics.DrawRectangle(edge, bounds.X - 1, bounds.Y - 1, bounds.Width + 1, bounds.Height + 1);
        };
        Controls.Add(canvas); Controls.Add(footer); Controls.Add(toolbar);
        saveButton.Click += (_, _) => Save();
        copyButton.Click += (_, _) => Copy();
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        maximizeButton.Click += (_, _) => ToggleMaximize();
        closeButton.Click += (_, _) => Close();
        Resize += (_, _) => { UpdateChrome(); ArrangeImage(); };
        UpdateChrome();
        toolbar.PerformLayout(); footer.PerformLayout(); ArrangeImage();
    }

    private Bitmap Artwork(ArtIcon icon, Color color)
    {
        var bitmap = ArtIcons.Create(icon, 20, color);
        artwork.Add(bitmap);
        return bitmap;
    }

    private void ArrangeImage()
    {
        if (arranging || IsDisposed || WindowState == FormWindowState.Minimized) return;
        arranging = true;
        try
        {
            var scroll = canvas.AutoScrollPosition;
            preview.Location = new Point(Math.Max(24, (canvas.ClientSize.Width - image.Width) / 2) + scroll.X,
                Math.Max(24, (canvas.ClientSize.Height - image.Height) / 2) + scroll.Y);
            canvas.Invalidate();
        }
        finally { arranging = false; }
    }

    private void MoveWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.Clicks == 2) { ToggleMaximize(); return; }
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
    }

    private void ToggleMaximize()
    {
        MaximizedBounds = Screen.FromHandle(Handle).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    private void UpdateChrome()
    {
        if (WindowState == FormWindowState.Minimized || Width <= 0 || Height <= 0) return;
        maximizeButton.Image = WindowState == FormWindowState.Maximized ? restoreIcon : maximizeIcon;
        using var outline = new GraphicsPath();
        if (WindowState == FormWindowState.Maximized) outline.AddRectangle(ClientRectangle);
        else
        {
            const int diameter = 18;
            outline.AddArc(0, 0, diameter, diameter, 180, 90);
            outline.AddArc(Width - diameter, 0, diameter, diameter, 270, 90);
            outline.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0, 90);
            outline.AddArc(0, Height - diameter, diameter, diameter, 90, 90);
            outline.CloseFigure();
        }
        var old = Region;
        Region = new Region(outline);
        old?.Dispose();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(FrameTheme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg != 0x84 || WindowState != FormWindowState.Normal) return;
        long position = message.LParam.ToInt64();
        var point = PointToClient(new Point(unchecked((short)position), unchecked((short)(position >> 16))));
        const int grip = 7;
        bool left = point.X < grip, right = point.X >= Width - grip;
        bool top = point.Y < grip, bottom = point.Y >= Height - grip;
        int hit = top ? left ? 13 : right ? 14 : 12 : bottom ? left ? 16 : right ? 17 : 15 : left ? 10 : right ? 11 : 1;
        if (hit != 1) message.Result = new IntPtr(hit);
    }

    internal void SaveImage(string path) => image.Save(path, ImageFormat.Png);

    private void Save()
    {
        using var dialog = new SaveFileDialog
        {
            Title = UiText.T("儲存截圖", "Save screenshot"),
            Filter = UiText.T("PNG 圖片 (*.png)|*.png", "PNG image (*.png)|*.png"),
            DefaultExt = "png", AddExtension = true, OverwritePrompt = true,
            FileName = UiText.T("截圖_", "Screenshot_") + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png",
            InitialDirectory = Directory.Exists(outputDirectory) ? outputDirectory : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { SaveImage(dialog.FileName); status.Text = UiText.T("已儲存：", "Saved: ") + Path.GetFileName(dialog.FileName); }
        catch (Exception ex) { ShowError(ex, UiText.T("無法儲存截圖", "Cannot save screenshot")); }
    }

    private void Copy()
    {
        try { Clipboard.SetImage(image); status.Text = UiText.T("已複製到剪貼簿", "Copied to clipboard"); }
        catch (Exception ex) { ShowError(ex, UiText.T("無法複製截圖", "Cannot copy screenshot")); }
    }

    private void ShowError(Exception error, string title) => MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { Save(); return true; }
        if (keyData == (Keys.Control | Keys.C)) { Copy(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { preview.Image = null; image.Dispose(); foreach (var bitmap in artwork) bitmap.Dispose(); }
        base.Dispose(disposing);
    }

    private sealed class PreviewCanvas : Panel
    {
        internal PreviewCanvas() => SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }
}
