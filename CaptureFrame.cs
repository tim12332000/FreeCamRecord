using System.Diagnostics;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Win32;

namespace FreeCamRecord;

internal sealed class Preferences
{
    public string Directory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FreeCamRecord");
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public int Fps { get; set; } = 0;
    public string Format { get; set; } = "mp4";
    public string Language { get; set; } = "zh-TW";
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeCamRecord", "settings.json");
    internal static Preferences Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            if (settings.Fps is not (0 or 15 or 30 or 60 or 120)) settings.Fps = 0;
            settings.Format = settings.Format?.ToLowerInvariant() == "avi" ? "avi" : "mp4";
            settings.Language = settings.Language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "zh-TW";
            if (string.IsNullOrWhiteSpace(settings.Directory)) settings.Directory = new Preferences().Directory;
            return settings;
        }
        catch { return new(); }
    }
    internal void Save()
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

internal sealed class CaptureFrame : Form
{
    internal const int Edge = 4, Toolbar = 44;
    private readonly Button menuButton = MakeButton(""), selectButton = MakeButton(""), sizeButton = MakeButton(""), photoButton = MakeButton(""), snipButton = MakeButton(""), recordButton = MakeButton(""), minimizeButton = MakeButton(""), closeButton = MakeButton("");
    private readonly Dictionary<(ArtIcon Icon, bool Accent), Bitmap> artwork = new();
    private readonly Icon appIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Preferences settings;
    private ScreenRecorder? session;
    private bool busy, closeRequested;
    private DateTime savedUntil;
    private DateTime photoSavedUntil;
    private string? lastSaved;
    private int dragEdges;
    private Point dragOrigin;
    private Rectangle dragBounds;
    private IntPtr recordingTarget;
    private bool selecting, toolbarInsideSelection;
    private bool excludedFromCapture;
    private int CaptureTopInset => toolbarInsideSelection ? Edge : Toolbar;
    private int CaptureVerticalMargin => CaptureTopInset + Edge;
    internal Rectangle CaptureArea => new(Left + Edge, Top + CaptureTopInset, Width - Edge * 2, Height - CaptureVerticalMargin);

    internal CaptureFrame(Preferences? preferences = null)
    {
        settings = preferences ?? Preferences.Load();
        UiText.SetLanguage(settings.Language);
        using (var stream = typeof(CaptureFrame).Assembly.GetManifestResourceStream("FreeCamRecord.Assets.FreeCamRecord.ico")!)
        using (var loaded = new Icon(stream)) appIcon = (Icon)loaded.Clone();
        Icon = appIcon;
        Text = $"FreeCamRecord {Application.ProductVersion.Split('+')[0]}";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(320, 120 + Toolbar + Edge);
        TopMost = true;
        BackColor = FrameTheme.Toolbar;
        ForeColor = FrameTheme.Text;
        Font = FrameTheme.NewFont();
        DoubleBuffered = true;
        Bounds = new Rectangle(100, 100, 477 + Edge * 2, 430 + Toolbar + Edge);
        Controls.AddRange(new Control[] { menuButton, selectButton, sizeButton, photoButton, snipButton, recordButton, minimizeButton, closeButton });
        FrameTheme.ApplyRecordingButton(recordButton);
        FrameTheme.ApplyWindowButton(minimizeButton);
        FrameTheme.ApplyWindowButton(closeButton, close: true);
        menuButton.Image = Artwork(ArtIcon.Menu);
        selectButton.Image = Artwork(ArtIcon.Size);
        closeButton.Image = Artwork(ArtIcon.Close);
        minimizeButton.Image = Artwork(ArtIcon.Minimize);
        minimizeButton.ImageAlign = ContentAlignment.MiddleCenter;
        photoButton.Image = Artwork(ArtIcon.Camera);
        photoButton.ImageAlign = ContentAlignment.MiddleCenter;
        snipButton.Image = Artwork(ArtIcon.Scissors);
        snipButton.ImageAlign = ContentAlignment.MiddleCenter;
        menuButton.ImageAlign = selectButton.ImageAlign = closeButton.ImageAlign = ContentAlignment.MiddleCenter;
        recordButton.Image = Artwork(ArtIcon.Record);
        recordButton.ImageAlign = ContentAlignment.MiddleCenter;
        FrameTheme.ApplyMenu(menu);
        menu.ShowCheckMargin = true;
        menu.ImageScalingSize = new Size(20, 20);
        ApplyLanguage();
        menuButton.Click += (_, _) => ShowMenu();
        selectButton.Click += async (_, _) => await SelectRegion();
        sizeButton.Click += (_, _) => EditSize();
        photoButton.Click += (_, _) => TakePhoto();
        snipButton.Click += async (_, _) => await SnipScreenshot();
        recordButton.Click += async (_, _) => await ToggleRecording();
        closeButton.Click += (_, _) => Close();
        minimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
        timer.Tick += (_, _) => { MaintainTopMost(); UpdateStatus(); };
        timer.Start();
        Resize += (_, _) => UpdateFrame();
        Shown += (_, _) =>
        {
            // Extra protection on Windows 10 2004+; geometry already keeps the frame outside capture.
            excludedFromCapture = Native.SetWindowDisplayAffinity(Handle, 0x11);
            UpdateFrame();
        };
        FormClosing += ClosingFrame;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        UpdateFrame();
    }

    // A floating recorder must not deactivate the application being recorded.
    // Keep receiving mouse clicks without turning the toolbar into the foreground window.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08040000; return parameters; } // NOACTIVATE | APPWINDOW (taskbar restore)
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        base.WndProc(ref message);
    }

    private static Button MakeButton(string text)
    {
        var button = new StudioButton { Text = text };
        FrameTheme.ApplyButton(button);
        return button;
    }

    private void ApplyLanguage()
    {
        menuButton.AccessibleName = UiText.T("設定選單", "Settings menu");
        selectButton.AccessibleName = UiText.T("選取錄製區域", "Select recording area");
        sizeButton.AccessibleName = UiText.T("錄影範圍尺寸", "Recording dimensions");
        photoButton.AccessibleName = UiText.T("拍照", "Take photo");
        snipButton.AccessibleName = UiText.T("剪取截圖", "Snip screenshot");
        recordButton.AccessibleName = session == null ? UiText.T("開始錄影", "Start recording") : UiText.T("停止錄影", "Stop recording");
        closeButton.AccessibleName = UiText.T("關閉", "Close");
        minimizeButton.AccessibleName = UiText.T("最小化", "Minimize");
        recordButton.Text = session == null ? UiText.T("錄影", "REC") : UiText.T("停止", "Stop");
        tips.SetToolTip(menuButton, UiText.T("聲音、儲存位置、開啟資料夾", "Audio, output folder, language and help"));
        tips.SetToolTip(selectButton, UiText.T("滑鼠指向預選區域；也可直接拖曳框選", "Hover to preview an area, or drag to select"));
        tips.SetToolTip(sizeButton, UiText.T("點擊輸入尺寸；拖曳邊角調整範圍", "Click to enter dimensions; drag edges to resize"));
        tips.SetToolTip(photoButton, UiText.T("拍照：將目前框選範圍儲存為 PNG（錄影中也可使用）", "Take photo: save the selected area as PNG (also available while recording)"));
        tips.SetToolTip(snipButton, UiText.T("剪取截圖：框選後預覽，可儲存或複製", "Snip: select an area, then preview, save or copy"));
        tips.SetToolTip(closeButton, UiText.T("關閉；錄影中會先停止並儲存", "Close; an active recording will be stopped and saved first"));
        tips.SetToolTip(minimizeButton, UiText.T("最小化到工作列；錄影會繼續", "Minimize to taskbar; recording continues"));
        UpdateStatus();
    }

    private Bitmap Artwork(ArtIcon icon, bool accent = false)
    {
        var key = (icon, accent);
        if (!artwork.TryGetValue(key, out var image))
        {
            image = ArtIcons.Create(icon, 20, accent ? FrameTheme.RecordingBorder : FrameTheme.Text);
            artwork.Add(key, image);
        }
        return image;
    }

    private void UpdateFrame()
    {
        if (WindowState == FormWindowState.Minimized) return;
        if (Width <= 0 || Height <= Toolbar + Edge) return;
        int barOffset = toolbarInsideSelection ? Edge : 0;
        int barHeight = Toolbar + barOffset;
        using var toolbarShape = FrameTheme.RoundedRectangle(new RectangleF(0, 0, Width, barHeight), 10);
        var shape = new Region(toolbarShape);
        shape.Union(new Rectangle(0, barHeight, Edge, Height - barHeight));
        shape.Union(new Rectangle(Width - Edge, barHeight, Edge, Height - barHeight));
        shape.Union(new Rectangle(0, Height - Edge, Width, Edge));
        var previous = Region;
        Region = shape;
        previous?.Dispose();
        bool showSelector = Width >= 432;
        bool showPhoto = Width >= 356;
        selectButton.Visible = showSelector;
        photoButton.Visible = showPhoto;
        menuButton.SetBounds(3, barOffset + 6, 32, 30);
        selectButton.SetBounds(38, barOffset + 6, 32, 30);
        int sizeLeft = showSelector ? 76 : 38;
        int recordWidth = showSelector ? 87 : 75;
        int recordLeft = Width - 76 - recordWidth;
        int snipLeft = recordLeft - (showPhoto ? 72 : 36);
        sizeButton.SetBounds(sizeLeft, barOffset + 6, Math.Min(118, snipLeft - sizeLeft - 4), 30);
        sizeButton.ForeColor = FrameTheme.MutedText;
        photoButton.SetBounds(recordLeft - 36, barOffset + 6, 32, 30);
        snipButton.SetBounds(snipLeft, barOffset + 6, 32, 30);
        recordButton.SetBounds(recordLeft, barOffset + 6, recordWidth, 30);
        minimizeButton.SetBounds(Width - 72, barOffset + 6, 32, 30);
        closeButton.SetBounds(Width - 36, barOffset + 6, 32, 30);
        savedUntil = DateTime.MinValue;
        UpdateStatus();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = session == null ? FrameTheme.IdleBorder : FrameTheme.RecordingBorder;
        int barHeight = Toolbar + (toolbarInsideSelection ? Edge : 0);
        using var outline = new Pen(FrameTheme.Border);
        using var toolbarShape = FrameTheme.RoundedRectangle(new RectangleF(.5f, .5f, Width - 1, barHeight - 1), 10);
        e.Graphics.DrawPath(outline, toolbarShape);
        using var border = new Pen(color);
        e.Graphics.DrawRectangle(border, Edge - 1, barHeight - 1, Width - Edge * 2 + 1, Height - barHeight - Edge + 1);
        using var corner = new Pen(color, 2);
        foreach (int x in new[] { Edge - 1, Width - Edge })
            foreach (int y in new[] { barHeight - 1, Height - Edge })
            {
                e.Graphics.DrawLine(corner, x, y, x + (x < Width / 2 ? 9 : -9), y);
                e.Graphics.DrawLine(corner, x, y, x, y + (y < Height / 2 ? 9 : -9));
            }
    }

    private int ResizeEdges(Point point)
    {
        return (point.X < Edge ? 1 : point.X >= Width - Edge ? 2 : 0)
            | (point.Y < Edge ? 4 : point.Y >= Height - Edge ? 8 : 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // Client-side dragging avoids Windows Snap changing the selected recording area.
        if (e.Button != MouseButtons.Left || session != null || busy) return;
        dragEdges = ResizeEdges(e.Location);
        if (dragEdges == 0 && e.Y < Toolbar - Edge + (toolbarInsideSelection ? Edge : 0)) dragEdges = 16;
        if (dragEdges != 0)
        {
            dragOrigin = PointToScreen(e.Location);
            dragBounds = Bounds;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (session != null || busy) return;
        if (dragEdges != 0 && Capture)
        {
            var pointer = System.Windows.Forms.Cursor.Position;
            int dx = pointer.X - dragOrigin.X, dy = pointer.Y - dragOrigin.Y;
            int left = dragBounds.Left, right = dragBounds.Right, top = dragBounds.Top, bottom = dragBounds.Bottom;
            if (dragEdges == 16) { Location = new Point(left + dx, top + dy); return; }
            if ((dragEdges & 1) != 0) left = Math.Min(left + dx, right - MinimumSize.Width);
            if ((dragEdges & 2) != 0) right = Math.Max(right + dx, left + MinimumSize.Width);
            if ((dragEdges & 4) != 0) top = Math.Min(top + dy, bottom - MinimumSize.Height);
            if ((dragEdges & 8) != 0) bottom = Math.Max(bottom + dy, top + MinimumSize.Height);
            Bounds = Rectangle.FromLTRB(left, top, right, bottom);
            return;
        }
        int edges = ResizeEdges(e.Location);
        Cursor = edges switch
        {
            5 or 10 => Cursors.SizeNWSE,
            6 or 9 => Cursors.SizeNESW,
            1 or 2 => Cursors.SizeWE,
            4 or 8 => Cursors.SizeNS,
            _ => Cursors.Default
        };
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) { dragEdges = 0; Capture = false; }
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) dragEdges = 0;
    }

    private void UpdateStatus()
    {
        photoButton.Enabled = !busy && !selecting;
        snipButton.Enabled = session == null && !busy && !selecting;
        photoButton.Image = Artwork(DateTime.Now < photoSavedUntil ? ArtIcon.Check : ArtIcon.Camera);
        if (busy) return;
        bool saved = DateTime.Now < savedUntil;
        sizeButton.Text = session != null ? $"{session.Elapsed:hh\\:mm\\:ss}" :
            saved ? UiText.T("已儲存", "Saved") : $"{CaptureArea.Width} × {CaptureArea.Height}";
        sizeButton.Image = session != null ? Artwork(ArtIcon.Record, true) : saved ? Artwork(ArtIcon.Check) : null;
        sizeButton.ForeColor = session != null ? FrameTheme.Text : saved ? FrameTheme.Accent : FrameTheme.MutedText;
    }

    private void MaintainTopMost()
    {
        // Other topmost windows can move above us when activated. Reassert the
        // overlay's Z order without moving it or taking input from the target.
        // Leave our own menus and modal dialogs above the toolbar.
        if (!Visible || IsDisposed || !IsHandleCreated || selecting || menu.Visible ||
            OwnedForms.Any(form => form.Visible) || WindowState == FormWindowState.Minimized) return;
        Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); // HWND_TOPMOST; NOSIZE | NOMOVE | NOACTIVATE
    }

    private void ShowMenu()
    {
        BuildMenu();
        menu.Show(menuButton, new Point(0, menuButton.Height));
    }

    private void BuildMenu()
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        menu.Items.Clear();
        bool editable = session == null && !busy;
        var pick = new ToolStripMenuItem(UiText.T("選取錄製區域…", "Select recording area…")) { Image = Artwork(ArtIcon.Size), Enabled = editable };
        pick.Click += async (_, _) => await SelectRegion();
        menu.Items.Add(pick);
        var photo = new ToolStripMenuItem(UiText.T("拍照", "Take photo")) { Image = Artwork(ArtIcon.Camera), Enabled = !busy && !selecting };
        photo.Click += (_, _) => TakePhoto();
        menu.Items.Add(photo); menu.Items.Add(new ToolStripSeparator());
        var format = new ToolStripMenuItem(UiText.T($"格式：{settings.Format.ToUpperInvariant()}", $"Format: {settings.Format.ToUpperInvariant()}")) { Image = Artwork(ArtIcon.Size), Enabled = editable };
        foreach (string extension in new[] { "mp4", "avi" })
        {
            var choice = new ToolStripMenuItem(extension == "mp4" ? UiText.T("MP4（H.264 / AAC）", "MP4 (H.264 / AAC)") : UiText.T("AVI（MJPEG / PCM）", "AVI (MJPEG / PCM)")) { Checked = settings.Format == extension };
            choice.Click += (_, _) => { settings.Format = extension; SaveSettings(); };
            format.DropDownItems.Add(choice);
        }
        int monitorRate = DisplayRefreshRate.ForArea(CaptureArea);
        var fps = new ToolStripMenuItem(settings.Fps == 0 ? UiText.T($"幀率：自動（{monitorRate} FPS）", $"Frame rate: Auto ({monitorRate} FPS)") : UiText.T($"幀率：{settings.Fps} FPS", $"Frame rate: {settings.Fps} FPS")) { Enabled = editable };
        foreach (int rate in new[] { 0, 15, 30, 60, 120 })
        {
            var choice = new ToolStripMenuItem(rate == 0 ? UiText.T($"自動：跟隨螢幕（{monitorRate} Hz）", $"Auto: match display ({monitorRate} Hz)") : $"{rate} FPS") { Checked = settings.Fps == rate };
            if (rate == 0) choice.ToolTipText = UiText.T("開始錄影時讀取錄影框所在螢幕的目前更新率；偵測失敗時使用 60 FPS。", "Use the recording display's refresh rate at start; fall back to 60 FPS if unavailable.");
            choice.Click += (_, _) => { settings.Fps = rate; SaveSettings(); };
            fps.DropDownItems.Add(choice);
        }
        foreach (var dropdown in new[] { format.DropDown, fps.DropDown })
        {
            dropdown.Renderer = menu.Renderer; dropdown.BackColor = FrameTheme.Toolbar; dropdown.ForeColor = FrameTheme.Text;
        }
        menu.Items.AddRange(new ToolStripItem[] { format, fps, new ToolStripSeparator() });
        var system = new ToolStripMenuItem(UiText.T("系統音效", "System audio")) { Image = Artwork(ArtIcon.Speaker), Checked = settings.SystemAudio, Enabled = editable };
        system.Click += (_, _) => { settings.SystemAudio = !settings.SystemAudio; SaveSettings(); };
        var mic = new ToolStripMenuItem(UiText.T("麥克風", "Microphone")) { Image = Artwork(ArtIcon.Microphone), Checked = settings.Microphone, Enabled = editable };
        mic.Click += (_, _) => { settings.Microphone = !settings.Microphone; SaveSettings(); };
        menu.Items.AddRange(new ToolStripItem[] { system, mic, new ToolStripSeparator() });
        var directory = menu.Items.Add(UiText.T("儲存位置…", "Output folder…"));
        directory.Image = Artwork(ArtIcon.Folder);
        directory.Enabled = session == null && !busy;
        directory.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = settings.Directory, Description = UiText.T("選擇錄影與照片儲存資料夾", "Choose the recording and photo output folder"), UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { settings.Directory = dialog.SelectedPath; SaveSettings(); }
        };
        var open = menu.Items.Add(UiText.T("開啟儲存資料夾", "Open output folder"));
        open.Image = Artwork(ArtIcon.Folder);
        open.Click += (_, _) => OpenFolder();
        menu.Items.Add(new ToolStripSeparator());
        var language = new ToolStripMenuItem("Language / 語言") { Enabled = editable };
        foreach (var option in new[] { (Code: "zh-TW", Name: "繁體中文"), (Code: "en", Name: "English") })
        {
            var choice = new ToolStripMenuItem(option.Name) { Checked = UiText.Language == option.Code };
            choice.Click += (_, _) => { UiText.SetLanguage(option.Code); settings.Language = UiText.Language; SaveSettings(); ApplyLanguage(); };
            language.DropDownItems.Add(choice);
        }
        language.DropDown.Renderer = menu.Renderer; language.DropDown.BackColor = FrameTheme.Toolbar; language.DropDown.ForeColor = FrameTheme.Text;
        menu.Items.Add(language);
        var help = menu.Items.Add(UiText.T($"Help／說明與關於（v{Application.ProductVersion.Split('+')[0]}）", $"Help / About (v{Application.ProductVersion.Split('+')[0]})"));
        help.AccessibleName = UiText.T("說明與版本資訊", "Help and version information");
        help.Enabled = editable;
        help.Click += (_, _) => ShowHelp();
    }

    private void ShowHelp()
    {
        using var dialog = new StudioDialog("FreeCamRecord", new Size(520, 448)) { Icon = appIcon };
        dialog.Controls.Add(new Label { Text = UiText.T("版本 ", "Version ") + Application.ProductVersion.Split('+')[0], ForeColor = FrameTheme.MutedText, Bounds = new Rectangle(24, 54, 450, 24) });
        var sections = new[]
        {
            (Title: UiText.T("錄製畫面", "Record your screen"), Body: UiText.T("選取錄製區域，或拖曳邊框調整。按錄影開始，按停止自動存檔。格式、聲音與儲存位置都在選單內。", "Select an area or adjust the frame edges. REC starts recording; Stop saves it. Format, audio and output folder are in the menu.")),
            (Title: UiText.T("剪取與拍照", "Snip and capture"), Body: UiText.T("剪刀可自由框選截圖，再從預覽儲存或複製。相機會儲存目前框內畫面，錄影中也能拍照。", "Use scissors to snip an area, then Save or Copy from preview. The camera saves the current framed area, including while recording.")),
            (Title: UiText.T("視窗與快捷鍵", "Window and shortcuts"), Body: UiText.T("最小化後可從工作列還原，錄影會繼續。截圖預覽中：Ctrl+S 儲存、Ctrl+C 複製、Esc 關閉。", "Minimize keeps recording running; restore from the taskbar. In screenshot preview: Ctrl+S saves, Ctrl+C copies, Esc closes."))
        };
        for (int index = 0; index < sections.Length; index++)
        {
            int y = 100 + index * 88;
            dialog.Controls.Add(new Label { Text = sections[index].Title, Font = FrameTheme.NewFont(9, FontStyle.Bold), ForeColor = FrameTheme.Text, Bounds = new Rectangle(24, y, 472, 24) });
            dialog.Controls.Add(new Label { Text = sections[index].Body, ForeColor = FrameTheme.MutedText, Bounds = new Rectangle(24, y + 28, 472, 48) });
        }
        var close = MakeButton(UiText.T("知道了", "Got it"));
        FrameTheme.ApplyButton(close, primary: true);
        close.SetBounds(392, 390, 104, 34); close.DialogResult = DialogResult.OK;
        dialog.Controls.Add(close); dialog.AcceptButton = close; dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    private void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) { MessageBox.Show(this, UiText.T($"本次設定已套用，但無法記住設定：{ex.Message}", $"Settings were applied, but could not be saved: {ex.Message}"), Text); }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { settings.Directory }, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.T("無法開啟資料夾", "Cannot open folder")); }
    }

    private void EditSize()
    {
        if (session != null || busy) return;
        if (DateTime.Now < savedUntil && lastSaved != null) { OpenFolder(); return; }
        using var dialog = new StudioDialog(UiText.T("錄影尺寸（像素）", "Recording size (pixels)"), new Size(360, 224)) { Icon = appIcon };
        var desktop = SystemInformation.VirtualScreen;
        int minWidth = MinimumSize.Width - Edge * 2, minHeight = MinimumSize.Height - CaptureVerticalMargin;
        int maxWidth = Math.Max(minWidth, desktop.Width - Edge * 2);
        int maxHeight = Math.Max(minHeight, desktop.Height - CaptureVerticalMargin);
        var width = new NumericUpDown { AccessibleName = UiText.T("寬度", "Width"), BackColor = FrameTheme.SurfaceRaised, ForeColor = FrameTheme.Text, BorderStyle = BorderStyle.FixedSingle, Font = FrameTheme.NewFont(12), Minimum = minWidth, Maximum = maxWidth, Value = Math.Clamp(CaptureArea.Width, minWidth, maxWidth), Bounds = new Rectangle(24, 100, 144, 36) };
        var height = new NumericUpDown { AccessibleName = UiText.T("高度", "Height"), BackColor = FrameTheme.SurfaceRaised, ForeColor = FrameTheme.Text, BorderStyle = BorderStyle.FixedSingle, Font = FrameTheme.NewFont(12), Minimum = minHeight, Maximum = maxHeight, Value = Math.Clamp(CaptureArea.Height, minHeight, maxHeight), Bounds = new Rectangle(192, 100, 144, 36) };
        var confirm = MakeButton(UiText.T("確定", "OK"));
        FrameTheme.ApplyButton(confirm, primary: true);
        using var confirmationIcon = ArtIcons.Create(ArtIcon.Check, 18, FrameTheme.AccentText);
        confirm.Image = confirmationIcon; confirm.DialogResult = DialogResult.OK; confirm.SetBounds(236, 166, 100, 34);
        var cancel = MakeButton(UiText.T("取消", "Cancel")); cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(126, 166, 100, 34);
        dialog.Controls.AddRange(new Control[] { width, height, confirm, cancel,
            new Label { Text = UiText.T("寬度（像素）", "Width (px)"), ForeColor = FrameTheme.MutedText, AutoSize = true, Location = new Point(24, 72) },
            new Label { Text = UiText.T("高度（像素）", "Height (px)"), ForeColor = FrameTheme.MutedText, AutoSize = true, Location = new Point(192, 72) } });
        foreach (var input in new[] { width, height })
        {
            var field = new StudioPanel { Bounds = new Rectangle(input.Left, 96, input.Width, 44), CornerRadius = 7 };
            dialog.Controls.Add(field);
            input.BorderStyle = BorderStyle.None;
            int inputWidth = input.Width - 24;
            field.Controls.Add(input);
            input.SetBounds(12, 10, inputWidth, input.Height);
            foreach (Control child in input.Controls)
                if (child is not TextBox) child.Visible = false;
            var arrows = new StudioSpinButtons(input);
            input.Controls.Add(arrows); arrows.BringToFront();
        }
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Size = new Size((int)width.Value + Edge * 2, (int)height.Value + CaptureVerticalMargin);
        Left = Math.Clamp(Left, desktop.Left, Math.Max(desktop.Left, desktop.Right - Width));
        Top = Math.Clamp(Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - Height));
    }

    private void TakePhoto()
    {
        if (busy || selecting) return;
        var area = CaptureArea;
        if (!SystemInformation.VirtualScreen.Contains(area))
        {
            MessageBox.Show(this, UiText.T("拍照範圍超出桌面，請移回螢幕內再拍照。", "The photo area extends beyond the desktop. Move it onto the display before taking a photo."), UiText.T("請調整範圍", "Adjust capture area"));
            return;
        }
        try
        {
            tips.Hide(this);
            Directory.CreateDirectory(settings.Directory);
            using var capture = new DesktopCapture(area);
            var image = capture.Capture();
            string path = Path.Combine(settings.Directory, $"Photo-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}.png");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                try { image.Save(file, ImageFormat.Png); }
                catch { file.Dispose(); File.Delete(path); throw; }
            }
            lastSaved = path;
            savedUntil = DateTime.Now.AddSeconds(8);
            photoSavedUntil = DateTime.Now.AddSeconds(2);
            tips.SetToolTip(photoButton, UiText.T($"照片已儲存：{path}", $"Photo saved: {path}"));
            tips.SetToolTip(sizeButton, UiText.T($"已儲存：{path}\n點擊開啟資料夾", $"Saved: {path}\nClick to open the folder"));
            UpdateStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiText.T($"無法儲存照片：{ex.Message}", $"Cannot save photo: {ex.Message}"), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ToggleRecording()
    {
        if (busy || selecting) return;
        if (session != null)
        {
            busy = true; recordButton.Enabled = false; sizeButton.Text = UiText.T("儲存中…", "Saving…");
            RestoreTargetFocus();
            await session.StopAsync(); // ObserveCompletion handles the result once.
            return;
        }
        if (!SystemInformation.VirtualScreen.Contains(CaptureArea))
        {
            MessageBox.Show(this, UiText.T("錄影框超出桌面，請移回螢幕內再錄影。", "The recording area extends beyond the desktop. Move it onto the display before recording."), UiText.T("請調整範圍", "Adjust recording area")); return;
        }
        busy = true; recordButton.Enabled = false;
        var area = CaptureArea;
        recordingTarget = Native.GetAncestor(Native.WindowFromPoint(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)), 2);
        RestoreTargetFocus();
        sizeButton.Text = UiText.T("準備中…", "Starting…"); savedUntil = DateTime.MinValue; tips.Hide(this);
        int targetFps = settings.Fps == 0 ? DisplayRefreshRate.ForArea(CaptureArea) : settings.Fps;
        var recording = new ScreenRecorder(CaptureArea, settings.Directory, settings.SystemAudio, settings.Microphone, targetFps, settings.Format);
        try
        {
            await recording.StartAsync();
            session = recording; busy = false;
            recordButton.Enabled = true; recordButton.Text = UiText.T("停止", "Stop"); recordButton.Image = Artwork(ArtIcon.Stop); recordButton.AccessibleName = UiText.T("停止錄影", "Stop recording");
            Invalidate(); UpdateStatus();
            RestoreTargetFocus();
            _ = ObserveCompletion(recording);
            if (closeRequested) await ToggleRecording();
        }
        catch (Exception ex)
        {
            busy = false; recordButton.Enabled = true; sizeButton.Enabled = true; UpdateStatus();
            MessageBox.Show(this, UiText.T($"無法開始錄影：{ex.Message}\n\n若音訊裝置無法使用，可在選單關閉相應聲音後重試。", $"Cannot start recording: {ex.Message}\n\nIf an audio device is unavailable, disable that audio source in the menu and try again."), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (closeRequested) Close();
        }
    }

    private async Task SnipScreenshot()
    {
        if (session != null || busy || selecting) return;
        selecting = true; menu.Close(); tips.Hide(this); Hide();
        try
        {
            await Task.Delay(160);
            var desktop = SystemInformation.VirtualScreen;
            using var image = new Bitmap(desktop.Width, desktop.Height);
            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size);
            using var picker = new RegionSelector(image, desktop.Location, new Size(1, 1), screenshotMode: true);
            if (picker.ShowDialog() != DialogResult.OK) return;
            var crop = picker.SelectedArea;
            crop.Offset(-desktop.Left, -desktop.Top);
            using var screenshot = image.Clone(crop, PixelFormat.Format32bppRgb);
            using var preview = new SnipPreview(screenshot, settings.Directory) { Icon = appIcon };
            preview.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, UiText.T("無法剪取截圖", "Cannot snip screenshot"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { selecting = false; Show(); UpdateFrame(); }
    }

    private async Task SelectRegion()
    {
        if (session != null || busy || selecting) return;
        selecting = true; menu.Close(); Hide();
        try
        {
            await Task.Delay(160); // Let the compositor remove the recorder before taking the selection image.
            var desktop = SystemInformation.VirtualScreen;
            using var image = new Bitmap(desktop.Width, desktop.Height);
            using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size);
            using var picker = new RegionSelector(image, desktop.Location, new Size(312, 120));
            if (picker.ShowDialog() == DialogResult.OK)
            {
                var area = picker.SelectedArea;
                bool inside = area.Top - Screen.FromRectangle(area).Bounds.Top < Toolbar;
                if (inside && !excludedFromCapture)
                    throw new InvalidOperationException(UiText.T("此系統無法隱藏選區內的工具列，請在畫面上方留一點空間後重新選取。", "This system cannot exclude the toolbar from capture. Leave space above the area for the toolbar and select again."));
                toolbarInsideSelection = inside;
                MinimumSize = new Size(320, inside ? 128 : 120 + Toolbar + Edge);
                Bounds = new Rectangle(area.Left - Edge, area.Top - CaptureTopInset, area.Width + Edge * 2, area.Height + CaptureVerticalMargin);
                tips.SetToolTip(sizeButton, UiText.T("已選取範圍；點擊輸入尺寸或拖曳邊角微調", "Area selected. Click to enter dimensions, or drag edges to adjust."));
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, UiText.T("無法選取錄製區域", "Cannot select recording area"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { selecting = false; Show(); UpdateFrame(); }
    }

    private async Task ObserveCompletion(ScreenRecorder recording)
    {
        var result = await recording.Completion;
        session = null; busy = false; sizeButton.Enabled = true; recordButton.Enabled = true;
        recordButton.Text = UiText.T("錄影", "REC"); recordButton.Image = Artwork(ArtIcon.Record); recordButton.AccessibleName = UiText.T("開始錄影", "Start recording");
        lastSaved = result.Path;
        if (result.Error != null) MessageBox.Show(this, UiText.T($"{result.Error}\n\n檔案位置：{result.Path}", $"{result.Error}\n\nFile location: {result.Path}"), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
        {
            savedUntil = DateTime.Now.AddSeconds(8);
            tips.SetToolTip(sizeButton, UiText.T($"已儲存：{result.Path}\n點擊開啟資料夾", $"Saved: {result.Path}\nClick to open the folder"));
        }
        Invalidate(); UpdateStatus();
        if (closeRequested) Close();
    }

    private void RestoreTargetFocus()
    {
        // Re-enabling a WinForms button may activate its form even on a no-activate toolbar.
        // Return input to the application under the transparent selection hole.
        if (recordingTarget != IntPtr.Zero && recordingTarget != Handle)
            Native.SetForegroundWindow(recordingTarget);
    }

    private async void ClosingFrame(object? sender, FormClosingEventArgs e)
    {
        if (session == null && !busy) return;
        e.Cancel = true; closeRequested = true;
        if (!busy) await ToggleRecording();
    }

    private void DisplayChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() =>
        {
            if (session != null)
            {
                busy = true; recordButton.Enabled = false; sizeButton.Text = UiText.T("儲存中…", "Saving…");
                _ = session.StopAsync(UiText.T("螢幕配置已改變，錄影已停止。請重新確認範圍。", "The display configuration changed, so recording stopped. Check the area before recording again."));
            }
        }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= DisplayChanged; timer.Dispose(); menu.Dispose(); tips.Dispose();
            foreach (var image in artwork.Values) image.Dispose();
            appIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
