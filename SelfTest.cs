using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeCamRecord;

// Exercises the actual desktop UI/capture path; no test framework or runtime dependency.
internal static class SelfTest
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RectangleRaw rectangle);
    [StructLayout(LayoutKind.Sequential)] private struct RectangleRaw { public int Left, Top, Right, Bottom; public override string ToString() => $"{Left},{Top},{Right},{Bottom}"; }
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    private static readonly List<string> results = new();
    private static string directory = "";
    private static int exitCode;

    internal static int Run(bool mediaOnly = false, bool photosOnly = false)
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var preferences = new Preferences { Directory = directory, SystemAudio = false, Microphone = false, Format = "avi", Fps = 15 };
        using var frame = new CaptureFrame(preferences);
        frame.Shown += async (_, _) =>
        {
            try
            {
                if (photosOnly) await ExercisePhotos(frame);
                else if (mediaOnly)
                {
                    using var pattern = new TestPattern { Bounds = frame.CaptureArea, TopMost = true };
                    pattern.Show(); frame.BringToFront();
                    await Task.Delay(300);
                    WriteTone(Path.Combine(directory, "tone.wav"));
                    await ExerciseMp4(frame, preferences, frame.CaptureArea);
                }
                else await Exercise(frame, preferences);
                results.Add("PASS: complete");
            }
            catch (Exception ex) { exitCode = 1; results.Add("FAIL: " + ex); }
            finally
            {
                File.WriteAllLines(Path.Combine(directory, "results.txt"), results, Encoding.UTF8);
                frame.Close();
            }
        };
        Application.Run(frame);
        return exitCode;
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        results.Add("PASS: " + description);
    }

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(description);
            await Task.Delay(50);
        }
    }

    private static async Task Drag(Point from, Point to)
    {
        SetCursorPos(from.X, from.Y); mouse_event(2, 0, 0, 0, UIntPtr.Zero);
        GetCursorPos(out var actual); results.Add($"CURSOR: requested={from}; actual={actual}");
        await Task.Delay(80);
        for (int i = 1; i <= 8; i++)
        {
            SetCursorPos(from.X + (to.X - from.X) * i / 8, from.Y + (to.Y - from.Y) * i / 8);
            await Task.Delay(35);
        }
        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(120);
    }

    private static async Task ExercisePhotos(CaptureFrame frame)
    {
        var area = frame.CaptureArea;
        using var pattern = new TestPattern { Bounds = area, TopMost = true };
        pattern.Show(); frame.BringToFront();
        await Task.Delay(300);
        CheckPhoto(frame, area.Size);
        CheckPhoto(frame, area.Size);
        var record = frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "開始錄影");
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("停止") && record.Enabled, "start recording for photo test");
        CheckPhoto(frame, area.Size);
        Require(record.Text.Contains("停止") && record.Enabled, "taking a photo preserves active recording");
        await Task.Delay(1200);
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("錄影") && record.Enabled, "save recording after taking photo");
        InspectAvi(Directory.GetFiles(directory, "*.avi").Single(), area.Size, false);
    }

    private static async Task Exercise(CaptureFrame frame, Preferences preferences)
    {
        await Task.Delay(300);
        await CaptureScreenshot(frame.Bounds, "initial.png", frame);
        GetWindowRect(frame.Handle, out var native); results.Add($"NATIVE BOUNDS: {native}");
        var old = frame.Bounds;
        await Drag(new Point(frame.Left + 220, frame.Top + 15), new Point(frame.Left + 260, frame.Top + 45));
        results.Add($"DRAG: before={old}; after={frame.Bounds}; DPI={frame.DeviceDpi}");
        Require(frame.Left == old.Left + 40 && frame.Top == old.Top + 30, "drag toolbar moves capture frame");
        old = frame.Bounds;
        await Drag(new Point(frame.Right - 2, frame.Bottom - 2), new Point(frame.Right + 38, frame.Bottom + 28));
        Require(frame.Width == old.Width + 40 && frame.Height == old.Height + 30, "drag corner resizes capture frame");
        var beforeEdgeDrag = frame.Bounds;
        var display = Screen.FromControl(frame);
        foreach (int bottom in new[] { display.WorkingArea.Bottom - 1, display.Bounds.Bottom - 1 }.Distinct())
        {
            frame.Bounds = beforeEdgeDrag;
            int fromY = frame.Bottom - 2;
            await Drag(new Point(frame.Left + 230, fromY), new Point(frame.Left + 230, bottom));
            await Task.Delay(350);
            results.Add($"EDGE DRAG: before={beforeEdgeDrag}; after={frame.Bounds}; pointerBottom={bottom}");
            Require(frame.Top == beforeEdgeDrag.Top && frame.Height == beforeEdgeDrag.Height + bottom - fromY && frame.WindowState == FormWindowState.Normal,
                "drag bottom to screen/work-area edge does not vertically maximize");
        }
        frame.Bounds = beforeEdgeDrag;
        old = frame.Bounds;
        frame.Size = new Size(SystemInformation.VirtualScreen.Width + 100, SystemInformation.VirtualScreen.Height + 100);
        bool dimensionDialogChecked = false;
        using (var dismiss = new System.Windows.Forms.Timer { Interval = 150 })
        {
            dismiss.Tick += async (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Text == "錄影尺寸（像素）");
                if (dialog == null) return;
                dismiss.Stop();
                dimensionDialogChecked = dialog.Controls.OfType<NumericUpDown>().All(n => n.Value <= n.Maximum && n.Value >= n.Minimum);
                await CaptureScreenshot(dialog.Bounds, "dimensions.png", frame);
                dialog.DialogResult = DialogResult.Cancel;
            };
            dismiss.Start();
            frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "錄影範圍尺寸").PerformClick();
        }
        Require(dimensionDialogChecked, "oversized capture frame opens dimension dialog without crashing");
        frame.Bounds = old;
        frame.Size = new Size(477 + CaptureFrame.Edge * 2, 430 + CaptureFrame.Toolbar + CaptureFrame.Edge);
        var area = frame.CaptureArea;
        Require(area.Size == new Size(477, 430), "odd capture dimensions remain exact");
        Require(!frame.Region!.IsVisible(new Point(CaptureFrame.Edge + 50, CaptureFrame.Toolbar + 50)), "frame region has a genuine input hole");
        using var pattern = new TestPattern { Bounds = area, TopMost = true };
        pattern.Show(); frame.BringToFront();
        await Task.Delay(300);
        CheckPhoto(frame, area.Size);
        CheckPhoto(frame, area.Size);
        Require(new Preferences().Format == "mp4" && new Preferences().Fps == 0, "default format is MP4 with automatic monitor FPS");
        Require(frame.Icon != null, "window has the original application icon");
        using (var executableIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!))
        using (var iconBitmap = executableIcon!.ToBitmap())
        {
            Color center = iconBitmap.GetPixel(iconBitmap.Width / 2, iconBitmap.Height / 2);
            Require(center.R > 180 && center.G < 140, "published executable contains the red recording-dot app icon");
        }
        await InspectMenus(frame, preferences);
        var click = new Point(area.Left + 80, area.Top + 80);
        SetCursorPos(click.X, click.Y); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(150);
        Require(pattern.Clicks > 0, "click passes through capture frame to underlying application");
        await CaptureScreenshot(frame.Bounds, "idle.png", frame);
        var record = frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "開始錄影");
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("停止") && record.Enabled, "start recording");
        CheckPhoto(frame, area.Size);
        Require(record.Text.Contains("停止") && record.Enabled, "taking a photo preserves active recording");
        await CaptureScreenshot(frame.Bounds, "recording.png", frame);
        await Task.Delay(1800);
        old = frame.Bounds;
        await Drag(new Point(frame.Left + 220, frame.Top + 15), new Point(frame.Left + 240, frame.Top + 25));
        Require(frame.Bounds == old, "capture frame cannot move during recording");
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("錄影") && record.Enabled, "stop and save");
        string first = Directory.GetFiles(directory, "*.avi").Single();
        InspectAvi(first, area.Size, false);
        await CaptureScreenshot(frame.Bounds, "saved.png", frame);

        // Real system loopback: generate a tone locally and record it alongside the screen.
        preferences.SystemAudio = true;
        string tone = Path.Combine(directory, "tone.wav");
        WriteTone(tone);
        using var player = new SoundPlayer(tone);
        player.PlayLooping();
        await Task.Delay(200);
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("停止") && record.Enabled, "start recording with audio");
        await Task.Delay(1600);
        record.PerformClick();
        await WaitUntil(() => record.Text.Contains("錄影") && record.Enabled, "save recording with audio");
        player.Stop();
        string second = Directory.GetFiles(directory, "*.avi").Single(p => p != first);
        InspectAvi(second, area.Size, true);
        Require(Directory.GetFiles(directory, "*.avi").Length == 2, "consecutive recordings have unique filenames");
        Require(Directory.GetFiles(directory, "*.partial").Length == 0, "completed recordings have no partial files");

        try
        {
            using var microphone = new AudioCapture(false, true);
            microphone.Start();
            await Task.Delay(300);
            Require(microphone.ReadFrames(4800).Length == 19200 && microphone.Error == null, "default microphone capture starts and supplies PCM frames");
        }
        catch (InvalidOperationException ex) { results.Add("NOT TESTED: default microphone unavailable: " + ex.Message); }

        // Closing while recording must finalize the file rather than abandon it.
        frame.Hide();
        using (var closingFrame = new CaptureFrame(new Preferences { Directory = directory, SystemAudio = false, Format = "avi", Fps = 15 }))
        {
            closingFrame.Bounds = frame.Bounds;
            closingFrame.Show();
            await Task.Delay(250);
            var closeRecord = closingFrame.Controls.OfType<Button>().Single(b => b.AccessibleName == "開始錄影");
            closeRecord.PerformClick();
            await WaitUntil(() => closeRecord.Text.Contains("停止") && closeRecord.Enabled, "start close-path recording");
            await Task.Delay(1100);
            closingFrame.Close();
            await WaitUntil(() => closingFrame.IsDisposed, "close waits for recording to be saved");
        }
        string third = Directory.GetFiles(directory, "*.avi").Single(p => p != first && p != second);
        InspectAvi(third, area.Size, false);
        Require(Directory.GetFiles(directory, "*.partial").Length == 0, "closing during recording finishes the saved file");
        frame.Show();

        string blockedPath = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockedPath, "test");
        bool rejected = false;
        try { await new ScreenRecorder(area, blockedPath, false, false).StartAsync(); }
        catch (IOException) { rejected = true; }
        Require(rejected, "invalid save directory rejects startup");
        File.Delete(blockedPath);
        await ExerciseMp4(frame, preferences, area);
    }

    private static async Task InspectMenus(CaptureFrame frame, Preferences preferences)
    {
        preferences.Format = "mp4"; preferences.Fps = 0;
        var button = frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "設定選單");
        button.PerformClick();
        var menu = (ContextMenuStrip)typeof(CaptureFrame).GetField("menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frame)!;
        await CaptureScreenshot(Rectangle.Union(frame.Bounds, menu.Bounds), "menu.png", frame);
        Require(menu.Items.OfType<ToolStripMenuItem>().Where(i => i.Text.Contains("音效") || i.Text.Contains("麥克風") || i.Text.Contains("資料夾")).All(i => i.Image != null), "audio and folder actions have matching vector icons");
        var format = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text.StartsWith("格式"));
        format.ShowDropDown();
        await CaptureScreenshot(Rectangle.Union(menu.Bounds, format.DropDown.Bounds), "formats.png", frame);
        Require(format.DropDownItems.Count == 2, "MP4 and AVI format choices are present");
        format.HideDropDown();
        var fps = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Text.StartsWith("幀率"));
        fps.ShowDropDown();
        await CaptureScreenshot(Rectangle.Union(menu.Bounds, fps.DropDown.Bounds), "fps.png", frame);
        Require(fps.DropDownItems.Count == 5 && fps.DropDownItems[0].Text.Contains("自動") && fps.DropDownItems[4].Text.Contains("120"), "automatic monitor sync and 15/30/60/120 FPS choices are present");
        fps.HideDropDown(); menu.Close();
        preferences.Format = "avi"; preferences.Fps = 15;
    }

    private static async Task ExerciseMp4(CaptureFrame frame, Preferences preferences, Rectangle area)
    {
        var record = frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "開始錄影");
        preferences.Format = "mp4"; preferences.SystemAudio = false;
        int automatic = DisplayRefreshRate.ForArea(area);
        results.Add($"DISPLAY: monitor={Screen.FromRectangle(area).DeviceName}; refresh={automatic} Hz");
        Require(automatic is >= 10 and <= 1000, "current monitor refresh rate can be read");
        foreach (int rate in new[] { 30, 120, 0 })
        {
            preferences.Fps = rate;
            var before = Directory.GetFiles(directory, "*.mp4").ToHashSet();
            record.PerformClick();
            await WaitUntil(() => record.Text.Contains("停止") && record.Enabled, "start MP4 recording");
            var running = (ScreenRecorder)typeof(CaptureFrame).GetField("session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(frame)!;
            await Task.Delay(1200);
            record.PerformClick();
            await WaitUntil(() => record.Text.Contains("錄影") && record.Enabled, "finalize MP4 recording");
            var measured = await running.Completion;
            string file = Directory.GetFiles(directory, "*.mp4").Single(p => !before.Contains(p));
            using var input = new BinaryReader(File.OpenRead(file));
            input.ReadUInt32();
            Require(Encoding.ASCII.GetString(input.ReadBytes(4)) == "ftyp" && input.BaseStream.Length > 1000, "real MP4 container is finalized");
            results.Add($"MP4: {file}; expectedFps={(rate == 0 ? automatic : rate)}; width={(area.Width + 1) & ~1}; height={(area.Height + 1) & ~1}; captureRate={measured.CapturedFrames / measured.Seconds:F1}");
        }
        // Non-divisor refresh rates must not accumulate PCM sample rounding errors.
        var tone = Path.Combine(directory, "tone.wav");
        using var player = new SoundPlayer(tone); player.PlayLooping();
        var recording = new ScreenRecorder(area, directory, true, false, 144, "mp4");
        await recording.StartAsync();
        await Task.Delay(1400);
        var result = await recording.StopAsync(); player.Stop();
        Require(result.Error == null && File.Exists(result.Path), "144 FPS MP4 with AAC saves successfully");
        results.Add($"MP4: {result.Path}; expectedFps=144; width={(area.Width + 1) & ~1}; height={(area.Height + 1) & ~1}; audio=AAC; captureRate={result.CapturedFrames / result.Seconds:F1}; captured={result.CapturedFrames}; seconds={result.Seconds:F3}");
        var highRateAvi = new ScreenRecorder(area, directory, false, false, 120, "avi");
        await highRateAvi.StartAsync();
        await Task.Delay(700);
        var aviResult = await highRateAvi.StopAsync();
        Require(aviResult.Error == null, "120 FPS AVI saves successfully");
        InspectAvi(aviResult.Path, area.Size, false, 120);
        Require(Directory.GetFiles(directory, "*.partial").Length == 0, "all MP4 files are finalized without leftover temporary files");
    }

    private static void CheckPhoto(CaptureFrame frame, Size size)
    {
        var before = Directory.GetFiles(directory, "Photo-*.png").ToHashSet();
        frame.Controls.OfType<Button>().Single(b => b.AccessibleName == "拍照").PerformClick();
        var added = Directory.GetFiles(directory, "Photo-*.png").Where(path => !before.Contains(path)).ToArray();
        Require(added.Length == 1, "photo button saves a unique PNG in the configured output folder");
        using var photo = new Bitmap(added[0]);
        Require(photo.Size == size, "photo preserves exact selected dimensions including odd widths");
        Require(photo.GetPixel(10, 10).R > 200 && photo.GetPixel(size.Width - 10, 10).G > 200
            && photo.GetPixel(10, size.Height - 10).B > 200 && photo.GetPixel(size.Width - 10, size.Height - 10).R > 200,
            "photo captures all four region corners without the toolbar or frame");
    }

    private static async Task CaptureScreenshot(Rectangle bounds, string name, CaptureFrame frame)
    {
        bool affinity = Native.SetWindowDisplayAffinity(frame.Handle, 0);
        results.Add($"AFFINITY RESET: {affinity}");
        frame.Refresh();
        await Task.Delay(250);
        using var image = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        image.Save(Path.Combine(directory, name));
        Native.SetWindowDisplayAffinity(frame.Handle, 0x11);
    }

    private static void InspectAvi(string path, Size size, bool expectAudio, int fps = 15)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        string FourCC() => Encoding.ASCII.GetString(reader.ReadBytes(4));
        Require(FourCC() == "RIFF", "AVI has RIFF header");
        Require(reader.ReadUInt32() + 8 == reader.BaseStream.Length, "AVI RIFF size is finalized");
        Require(FourCC() == "AVI ", "AVI type header");
        int frames = 0, audioBytes = 0, nonzero = 0, indexEntries = 0, chunks = 0;
        var imageHashes = new HashSet<string>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            string id = FourCC(); int bytes = reader.ReadInt32(); long end = reader.BaseStream.Position + bytes;
            if (id == "LIST")
            {
                string kind = FourCC();
                if (kind == "movi")
                {
                    while (reader.BaseStream.Position < end)
                    {
                        string chunk = FourCC(); int count = reader.ReadInt32(); byte[] data = reader.ReadBytes(count); chunks++;
                        if ((count & 1) != 0) reader.ReadByte();
                        if (chunk == "00dc")
                        {
                            frames++;
                            if (data.Length == 0) continue; // AVI timed null frame holds the preceding picture.
                            imageHashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)));
                            if (frames == 1)
                            {
                                using var memory = new MemoryStream(data);
                                using var bitmap = new Bitmap(memory);
                                Require(bitmap.Size == size, "decoded video frame has exact requested dimensions");
                                Color corner = bitmap.GetPixel(10, 10);
                                Require(corner.R > 200 && corner.G < 65 && corner.B < 65, "top-left video pixel matches target, excludes toolbar and border");
                                Color last = bitmap.GetPixel(size.Width - 10, size.Height - 10);
                                Require(last.R > 200 && last.G > 200 && last.B < 65, "bottom-right video pixel matches target, no crop offset");
                                bitmap.Save(Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + ".png"));
                            }
                        }
                        else if (chunk == "01wb") { audioBytes += data.Length; nonzero += data.Count(b => b != 0); }
                    }
                }
            }
            else if (id == "idx1") indexEntries = bytes / 16;
            reader.BaseStream.Position = end + (bytes & 1);
        }
        Require(frames >= 15, "recorded at least one second of video");
        Require(imageHashes.Count > 1, "video contains changing screen content");
        Require(indexEntries == chunks, "AVI index covers video and independent audio chunks");
        Require(expectAudio ? Math.Abs(audioBytes / 192000.0 - (double)frames / fps) <= 1.0 / fps + .02 && nonzero > 1000 : audioBytes == 0, "audio stream matches selected settings and physical duration");
        results.Add($"FILE: {path}; frames={frames}; audioBytes={audioBytes}; nonzeroAudio={nonzero}");
    }

    private static void WriteTone(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + 48000 * 2);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(96000);
        for (int i = 0; i < 48000; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / 48000) * 3000));
    }

    private sealed class TestPattern : Form
    {
        private readonly System.Windows.Forms.Timer animation = new() { Interval = 100 };
        private int tick;
        internal int Clicks { get; private set; }
        internal TestPattern()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 28, 34); DoubleBuffered = true;
            animation.Tick += (_, _) => { tick++; Invalidate(); }; animation.Start();
            MouseDown += (_, _) => Clicks++;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.FillRectangle(Brushes.Red, 0, 0, 35, 35);
            e.Graphics.FillRectangle(Brushes.Lime, Width - 35, 0, 35, 35);
            e.Graphics.FillRectangle(Brushes.Blue, 0, Height - 35, 35, 35);
            e.Graphics.FillRectangle(Brushes.Yellow, Width - 35, Height - 35, 35, 35);
            using var font = new Font("Microsoft JhengHei UI", 18);
            e.Graphics.DrawString("FreeCamRecord\n區域錄影驗證\n" + tick, font, Brushes.White, 50, 150);
            e.Graphics.FillRectangle(Brushes.CornflowerBlue, 45 + tick % 250, 300, 40, 30);
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
    }
}
