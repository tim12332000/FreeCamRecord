using System.Diagnostics;
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
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeCamRecord", "settings.json");
    internal static Preferences Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            if (settings.Fps is not (0 or 15 or 30 or 60 or 120)) settings.Fps = 0;
            settings.Format = settings.Format?.ToLowerInvariant() == "avi" ? "avi" : "mp4";
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
    internal const int Edge = 4, Toolbar = 36;
    private readonly Button menuButton = MakeButton(""), sizeButton = MakeButton(""), recordButton = MakeButton("錄影"), closeButton = MakeButton("");
    private readonly Dictionary<(ArtIcon Icon, bool Accent), Bitmap> artwork = new();
    private readonly Icon appIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly ToolTip tips = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Preferences settings;
    private ScreenRecorder? session;
    private bool busy, closeRequested;
    private DateTime savedUntil;
    private string? lastSaved;
    private int dragEdges;
    private Point dragOrigin;
    private Rectangle dragBounds;
    private IntPtr recordingTarget;
    internal Rectangle CaptureArea => new(Left + Edge, Top + Toolbar, Width - Edge * 2, Height - Toolbar - Edge);

    internal CaptureFrame(Preferences? preferences = null)
    {
        settings = preferences ?? Preferences.Load();
        using (var stream = typeof(CaptureFrame).Assembly.GetManifestResourceStream("FreeCamRecord.Assets.FreeCamRecord.ico")!)
        using (var loaded = new Icon(stream)) appIcon = (Icon)loaded.Clone();
        Icon = appIcon;
        Text = "FreeCamRecord";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(320, 160);
        TopMost = true;
        BackColor = FrameTheme.Toolbar;
        ForeColor = FrameTheme.Text;
        Font = new Font("Microsoft JhengHei UI", 9);
        DoubleBuffered = true;
        Bounds = new Rectangle(100, 100, 477 + Edge * 2, 430 + Toolbar + Edge);
        Controls.AddRange(new Control[] { menuButton, sizeButton, recordButton, closeButton });
        FrameTheme.ApplyButton(recordButton, primary: true);
        menuButton.Image = Artwork(ArtIcon.Menu);
        closeButton.Image = Artwork(ArtIcon.Close);
        menuButton.ImageAlign = closeButton.ImageAlign = ContentAlignment.MiddleCenter;
        recordButton.Image = Artwork(ArtIcon.Record);
        recordButton.ImageAlign = ContentAlignment.MiddleCenter;
        FrameTheme.ApplyMenu(menu);
        menu.ShowCheckMargin = true;
        menu.ImageScalingSize = new Size(20, 20);
        menuButton.AccessibleName = "設定選單";
        sizeButton.AccessibleName = "錄影範圍尺寸";
        recordButton.AccessibleName = "開始錄影";
        closeButton.AccessibleName = "關閉";
        tips.SetToolTip(menuButton, "聲音、儲存位置、開啟資料夾");
        tips.SetToolTip(sizeButton, "點擊輸入尺寸；拖曳邊角調整範圍");
        tips.SetToolTip(closeButton, "關閉；錄影中會先停止並儲存");
        menuButton.Click += (_, _) => ShowMenu();
        sizeButton.Click += (_, _) => EditSize();
        recordButton.Click += async (_, _) => await ToggleRecording();
        closeButton.Click += (_, _) => Close();
        timer.Tick += (_, _) => UpdateStatus();
        timer.Start();
        Resize += (_, _) => UpdateFrame();
        Shown += (_, _) =>
        {
            // Extra protection on Windows 10 2004+; geometry already keeps the frame outside capture.
            Native.SetWindowDisplayAffinity(Handle, 0x11);
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
        get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) { message.Result = (IntPtr)3; return; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        base.WndProc(ref message);
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text };
        FrameTheme.ApplyButton(button);
        return button;
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
        if (Width <= 0 || Height <= Toolbar + Edge) return;
        var shape = new Region(new Rectangle(0, 0, Width, Toolbar));
        shape.Union(new Rectangle(0, Toolbar, Edge, Height - Toolbar));
        shape.Union(new Rectangle(Width - Edge, Toolbar, Edge, Height - Toolbar));
        shape.Union(new Rectangle(0, Height - Edge, Width, Edge));
        var previous = Region;
        Region = shape;
        previous?.Dispose();
        menuButton.SetBounds(3, 2, 32, Toolbar - 4);
        sizeButton.SetBounds(38, 2, 118, Toolbar - 4);
        recordButton.SetBounds(Width - 127, 3, 87, Toolbar - 6);
        closeButton.SetBounds(Width - 36, 2, 32, Toolbar - 4);
        savedUntil = DateTime.MinValue;
        UpdateStatus();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var brush = new SolidBrush(session == null ? FrameTheme.IdleBorder : FrameTheme.RecordingBorder);
        e.Graphics.FillRectangle(brush, 0, Toolbar - Edge, Width, Edge);
        e.Graphics.FillRectangle(brush, 0, Toolbar, Edge, Height - Toolbar);
        e.Graphics.FillRectangle(brush, Width - Edge, Toolbar, Edge, Height - Toolbar);
        e.Graphics.FillRectangle(brush, 0, Height - Edge, Width, Edge);
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
        if (dragEdges == 0 && e.Y < Toolbar - Edge) dragEdges = 16;
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
        if (busy) return;
        bool saved = DateTime.Now < savedUntil;
        sizeButton.Text = session != null ? $"{session.Elapsed:hh\\:mm\\:ss}" :
            saved ? "已儲存" : $"{CaptureArea.Width} × {CaptureArea.Height}";
        sizeButton.Image = session != null ? Artwork(ArtIcon.Record, true) : saved ? Artwork(ArtIcon.Check) : null;
    }

    private void ShowMenu()
    {
        foreach (var item in menu.Items.Cast<ToolStripItem>().ToArray()) item.Dispose();
        menu.Items.Clear();
        bool editable = session == null && !busy;
        var format = new ToolStripMenuItem($"格式：{settings.Format.ToUpperInvariant()}") { Image = Artwork(ArtIcon.Size), Enabled = editable };
        foreach (string extension in new[] { "mp4", "avi" })
        {
            var choice = new ToolStripMenuItem(extension == "mp4" ? "MP4（H.264 / AAC）" : "AVI（MJPEG / PCM）") { Checked = settings.Format == extension };
            choice.Click += (_, _) => { settings.Format = extension; SaveSettings(); };
            format.DropDownItems.Add(choice);
        }
        int monitorRate = DisplayRefreshRate.ForArea(CaptureArea);
        var fps = new ToolStripMenuItem(settings.Fps == 0 ? $"幀率：自動（{monitorRate} FPS）" : $"幀率：{settings.Fps} FPS") { Enabled = editable };
        foreach (int rate in new[] { 0, 15, 30, 60, 120 })
        {
            var choice = new ToolStripMenuItem(rate == 0 ? $"自動：跟隨螢幕（{monitorRate} Hz）" : $"{rate} FPS") { Checked = settings.Fps == rate };
            if (rate == 0) choice.ToolTipText = "開始錄影時讀取錄影框所在螢幕的目前更新率；偵測失敗時使用 60 FPS。";
            choice.Click += (_, _) => { settings.Fps = rate; SaveSettings(); };
            fps.DropDownItems.Add(choice);
        }
        foreach (var dropdown in new[] { format.DropDown, fps.DropDown })
        {
            dropdown.Renderer = menu.Renderer; dropdown.BackColor = FrameTheme.Toolbar; dropdown.ForeColor = FrameTheme.Text;
        }
        menu.Items.AddRange(new ToolStripItem[] { format, fps, new ToolStripSeparator() });
        var system = new ToolStripMenuItem("系統音效") { Image = Artwork(ArtIcon.Speaker), Checked = settings.SystemAudio, Enabled = editable };
        system.Click += (_, _) => { settings.SystemAudio = !settings.SystemAudio; SaveSettings(); };
        var mic = new ToolStripMenuItem("麥克風") { Image = Artwork(ArtIcon.Microphone), Checked = settings.Microphone, Enabled = editable };
        mic.Click += (_, _) => { settings.Microphone = !settings.Microphone; SaveSettings(); };
        menu.Items.AddRange(new ToolStripItem[] { system, mic, new ToolStripSeparator() });
        var directory = menu.Items.Add("儲存位置…");
        directory.Image = Artwork(ArtIcon.Folder);
        directory.Enabled = session == null && !busy;
        directory.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = settings.Directory, Description = "選擇錄影儲存資料夾", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { settings.Directory = dialog.SelectedPath; SaveSettings(); }
        };
        var open = menu.Items.Add("開啟儲存資料夾");
        open.Image = Artwork(ArtIcon.Folder);
        open.Click += (_, _) => OpenFolder();
        menu.Show(menuButton, new Point(0, menuButton.Height));
    }

    private void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) { MessageBox.Show(this, $"本次設定已套用，但無法記住設定：{ex.Message}", Text); }
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(settings.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { settings.Directory }, UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "無法開啟資料夾"); }
    }

    private void EditSize()
    {
        if (session != null || busy) return;
        if (DateTime.Now < savedUntil && lastSaved != null) { OpenFolder(); return; }
        using var dialog = new Form { Text = "錄影尺寸（像素）", Icon = appIcon, BackColor = FrameTheme.Toolbar, ForeColor = FrameTheme.Text, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(290, 145), MinimizeBox = false, MaximizeBox = false, Font = Font };
        var desktop = SystemInformation.VirtualScreen;
        int minWidth = MinimumSize.Width - Edge * 2, minHeight = MinimumSize.Height - Toolbar - Edge;
        int maxWidth = Math.Max(minWidth, desktop.Width - Edge * 2);
        int maxHeight = Math.Max(minHeight, desktop.Height - Toolbar - Edge);
        var width = new NumericUpDown { AccessibleName = "寬度", BackColor = FrameTheme.Hover, ForeColor = FrameTheme.Text, BorderStyle = BorderStyle.FixedSingle, Minimum = minWidth, Maximum = maxWidth, Value = Math.Clamp(CaptureArea.Width, minWidth, maxWidth), Bounds = new Rectangle(20, 46, 110, 28) };
        var height = new NumericUpDown { AccessibleName = "高度", BackColor = FrameTheme.Hover, ForeColor = FrameTheme.Text, BorderStyle = BorderStyle.FixedSingle, Minimum = minHeight, Maximum = maxHeight, Value = Math.Clamp(CaptureArea.Height, minHeight, maxHeight), Bounds = new Rectangle(160, 46, 110, 28) };
        var confirm = MakeButton("確定");
        FrameTheme.ApplyButton(confirm, primary: true);
        confirm.Image = Artwork(ArtIcon.Check); confirm.DialogResult = DialogResult.OK; confirm.SetBounds(185, 96, 85, 32);
        var cancel = MakeButton("取消"); cancel.DialogResult = DialogResult.Cancel; cancel.SetBounds(90, 96, 85, 32);
        dialog.Controls.AddRange(new Control[] { width, height, confirm, cancel,
            new Label { Text = "寬度（像素）", ForeColor = FrameTheme.MutedText, AutoSize = true, Location = new Point(20, 18) },
            new Label { Text = "高度（像素）", ForeColor = FrameTheme.MutedText, AutoSize = true, Location = new Point(160, 18) } });
        dialog.AcceptButton = confirm;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Size = new Size((int)width.Value + Edge * 2, (int)height.Value + Toolbar + Edge);
        Left = Math.Clamp(Left, desktop.Left, Math.Max(desktop.Left, desktop.Right - Width));
        Top = Math.Clamp(Top, desktop.Top, Math.Max(desktop.Top, desktop.Bottom - Height));
    }

    private async Task ToggleRecording()
    {
        if (busy) return;
        if (session != null)
        {
            busy = true; recordButton.Enabled = false; sizeButton.Text = "儲存中…";
            RestoreTargetFocus();
            await session.StopAsync(); // ObserveCompletion handles the result once.
            return;
        }
        if (!SystemInformation.VirtualScreen.Contains(CaptureArea))
        {
            MessageBox.Show(this, "錄影框超出桌面，請移回螢幕內再錄影。", "請調整範圍"); return;
        }
        busy = true; recordButton.Enabled = false;
        var area = CaptureArea;
        recordingTarget = Native.GetAncestor(Native.WindowFromPoint(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)), 2);
        RestoreTargetFocus();
        sizeButton.Text = "準備中…"; savedUntil = DateTime.MinValue; tips.Hide(this);
        int targetFps = settings.Fps == 0 ? DisplayRefreshRate.ForArea(CaptureArea) : settings.Fps;
        var recording = new ScreenRecorder(CaptureArea, settings.Directory, settings.SystemAudio, settings.Microphone, targetFps, settings.Format);
        try
        {
            await recording.StartAsync();
            session = recording; busy = false;
            recordButton.Enabled = true; recordButton.Text = "停止"; recordButton.Image = Artwork(ArtIcon.Stop); recordButton.AccessibleName = "停止錄影";
            Invalidate(); UpdateStatus();
            RestoreTargetFocus();
            _ = ObserveCompletion(recording);
            if (closeRequested) await ToggleRecording();
        }
        catch (Exception ex)
        {
            busy = false; recordButton.Enabled = true; sizeButton.Enabled = true; UpdateStatus();
            MessageBox.Show(this, $"無法開始錄影：{ex.Message}\n\n若音訊裝置無法使用，可在選單關閉相應聲音後重試。", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            if (closeRequested) Close();
        }
    }

    private async Task ObserveCompletion(ScreenRecorder recording)
    {
        var result = await recording.Completion;
        session = null; busy = false; sizeButton.Enabled = true; recordButton.Enabled = true;
        recordButton.Text = "錄影"; recordButton.Image = Artwork(ArtIcon.Record); recordButton.AccessibleName = "開始錄影";
        lastSaved = result.Path;
        if (result.Error != null) MessageBox.Show(this, $"{result.Error}\n\n檔案位置：{result.Path}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        else
        {
            savedUntil = DateTime.Now.AddSeconds(8);
            tips.SetToolTip(sizeButton, $"已儲存：{result.Path}\n點擊開啟資料夾");
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
                busy = true; recordButton.Enabled = false; sizeButton.Text = "儲存中…";
                _ = session.StopAsync("螢幕配置已改變，錄影已停止。請重新確認範圍。");
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
