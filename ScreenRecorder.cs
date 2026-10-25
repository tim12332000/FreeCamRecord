using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace FreeCamRecord;

internal record RecordingResult(string Path, string? Error, long CapturedFrames = 0, double Seconds = 0, string CaptureBackend = "GDI", long WrittenFrames = 0, long SkippedSlots = 0);

internal sealed class ScreenRecorder
{
    private readonly Rectangle area;
    private readonly string finalPath, temporaryPath, format;
    private readonly int fps;
    private readonly bool systemAudio, microphone;
    private RecordingTimeline? timeline;
    private AudioTimelinePump? audioPump;
    private volatile bool stopping;
    private string? stopReason;
    private AudioCapture? sound;
    private AviWriter? avi;
    private Mp4Writer? mp4;
    private Bitmap? bitmap;
    private DesktopCapture? desktop;
    private DxgiDesktopCapture? gpu;
    private string captureBackend = "GDI";
    private readonly ImageCodecInfo jpegEncoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
    private readonly EncoderParameters jpegParameters = new(1);
    private byte[]? currentJpeg;
    private bool preciseTimer;
    private long captures;
    private double captureMilliseconds;
    private long writtenFrames;
    public Task<RecordingResult> Completion { get; private set; } = null!;
    public TimeSpan Elapsed => TimeSpan.FromTicks(timeline?.Elapsed ?? 0);

    internal ScreenRecorder(Rectangle area, string directory, bool systemAudio, bool microphone, int fps = 30, string format = "mp4")
    {
        if (fps is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(fps));
        if (format is not ("mp4" or "avi")) throw new ArgumentOutOfRangeException(nameof(format));
        this.area = area; this.systemAudio = systemAudio; this.microphone = microphone;
        this.fps = fps; this.format = format;
        finalPath = Path.Combine(directory, $"{UiText.T("錄影", "Recording")}_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}.{format}");
        temporaryPath = finalPath + ".partial";
        jpegParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
    }

    internal Task StartAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
                if (format == "mp4") mp4 = new Mp4Writer(temporaryPath, area.Width, area.Height, fps, systemAudio || microphone);
                else avi = new AviWriter(temporaryPath, area.Width, area.Height, fps, systemAudio || microphone);
                if (Environment.GetEnvironmentVariable("FREECAM_CAPTURE_BACKEND") != "gdi")
                {
                    try { gpu = new DxgiDesktopCapture(area); captureBackend = "DXGI"; }
                    catch (Exception ex) when (ex is COMException or NotSupportedException or System.ComponentModel.Win32Exception) { gpu = null; }
                }
                if (gpu == null) desktop = new DesktopCapture(area);
                if (systemAudio || microphone) { sound = new AudioCapture(systemAudio, microphone); sound.Start(); }
                preciseTimer = Native.timeBeginPeriod(1) == 0;
                for (int attempt = 0; bitmap == null && attempt < 10; attempt++) Capture();
                if (bitmap == null) { gpu?.Dispose(); gpu = null; desktop = new DesktopCapture(area); captureBackend = "GDI fallback"; Capture(); }
                timeline = new RecordingTimeline(fps);
                if (sound != null)
                {
                    sound.SetTimelineOrigin(timeline.Origin);
                    audioPump = new AudioTimelinePump(sound.ReadFramesAt, (pcm, first) =>
                    {
                        if (mp4 != null) mp4.WriteAudio(pcm, first);
                        else avi!.WriteAudio(pcm);
                    });
                }
                if (timeline.TryTakeVideo(out var firstSlot)) WriteVideo(firstSlot);
            }
            catch (Exception ex)
            {
                try { ReleaseResources(); } catch { /* Keep the original startup error. */ }
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { /* Preserve the startup error. */ }
                ready.TrySetException(ex);
                return new RecordingResult(temporaryPath, ex.Message);
            }
            ready.TrySetResult(true);
            return RecordLoop();
        });
        return ready.Task;
    }

    private void Capture()
    {
        long start = Stopwatch.GetTimestamp();
        Bitmap? image;
        if (gpu != null)
        {
            try
            {
                image = gpu.Capture(bitmap == null ? 50 : Math.Clamp(1000 / fps, 1, 16));
                if (image != null) gpu.OverlayCursor(area);
            }
            catch (COMException)
            {
                gpu.Dispose(); gpu = null; captureBackend = "GDI fallback";
                desktop = new DesktopCapture(area); image = desktop.Capture();
            }
        }
        else image = desktop!.Capture();
        double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        captureMilliseconds = captures == 0 ? elapsed : captureMilliseconds * 0.7 + elapsed * 0.3;
        if (image == null) return;
        bitmap = image;
        currentJpeg = null;
        captures++;
    }

    private void WriteVideo(VideoTimeSlot slot)
    {
        if (mp4 != null) mp4.WriteVideo(bitmap!, slot.Timestamp, slot.Duration, slot.Skipped > 0);
        else
        {
            if (currentJpeg == null)
            {
                using var buffer = new MemoryStream();
                bitmap!.Save(buffer, jpegEncoder, jpegParameters);
                currentJpeg = buffer.ToArray();
            }
            avi!.WriteVideo(currentJpeg, slot.Index);
        }
        writtenFrames++;
    }

    private RecordingResult RecordLoop()
    {
        string? error = null;
        try
        {
            while (!stopping)
            {
                if (sound?.Error is string audioError) throw new InvalidOperationException(audioError);
                audioPump?.DrainUntil(Math.Max(0, timeline!.Elapsed - TimeSpan.TicksPerMillisecond * 50));
                double due = timeline!.NextVideoTime / (double)TimeSpan.TicksPerMillisecond;
                // Screen readback may block until the next display refresh. Start that work
                // before the frame deadline instead of sleeping and then waiting another refresh.
                double lead = Math.Min(1000.0 / fps, captureMilliseconds);
                int delay = (int)(due - lead - Elapsed.TotalMilliseconds);
                if (delay > 0) { Thread.Sleep(Math.Min(delay, 10)); continue; }
                Capture();
                while (!stopping && Elapsed.TotalMilliseconds < due)
                    Thread.Sleep(Math.Clamp((int)(due - Elapsed.TotalMilliseconds), 1, 5));
                if (stopping) break;
                // Consult the clock again AFTER capture: acquisition and encoding may have stalled.
                // At most one new picture is submitted; expired slots advance the timestamp instead.
                if (timeline.TryTakeVideo(out var slot)) WriteVideo(slot);
                audioPump?.DrainUntil(Math.Max(0, timeline.Elapsed - TimeSpan.TicksPerMillisecond * 50));
                if (avi?.Length > 1_800_000_000)
                {
                    error = UiText.T("已達第一版單檔 1.8 GB 上限，已自動停止並儲存。", "The 1.8 GB file limit for this version was reached. Recording stopped and was saved.");
                    break;
                }
            }
        }
        catch (Exception ex) { error = UiText.T($"錄影中斷：{ex.Message}", $"Recording interrupted: {ex.Message}"); }
        finally
        {
            timeline!.Stop();
            try
            {
                if (audioPump != null)
                {
                    Thread.Sleep(50); // Let the final timestamped WASAPI packet arrive before draining.
                    audioPump.DrainUntil(timeline.Elapsed, final: true);
                }
                mp4?.CompleteVideoAt(timeline.Elapsed);
                long fullSlots = RecordingTimeline.Scale(timeline.Elapsed, fps, TimeSpan.TicksPerSecond);
                if (timeline.Elapsed % TimeSpan.TicksPerSecond * fps % TimeSpan.TicksPerSecond != 0) fullSlots++;
                avi?.ExtendVideoTo(Math.Max(1, fullSlots));
            }
            catch (Exception ex) { error = UiText.T($"儲存失敗，暫存檔保留：{ex.Message}", $"Save failed; temporary file retained: {ex.Message}"); }
            try { ReleaseResources(); }
            catch (Exception ex) { error = UiText.T($"儲存失敗，暫存檔保留：{ex.Message}", $"Save failed; temporary file retained: {ex.Message}"); }
        }
        if (error?.StartsWith(UiText.T("儲存失敗", "Save failed")) == true) return Result(temporaryPath, error);
        try
        {
            File.Move(temporaryPath, finalPath);
            return Result(finalPath, error ?? stopReason);
        }
        catch (Exception ex) { return Result(temporaryPath, UiText.T($"儲存失敗：{ex.Message}；暫存檔保留於 {temporaryPath}", $"Save failed: {ex.Message}; temporary file retained at {temporaryPath}")); }
    }

    private RecordingResult Result(string path, string? error)
    {
        var result = new RecordingResult(path, error, captures, Elapsed.TotalSeconds, captureBackend, writtenFrames, timeline?.SkippedSlots ?? 0);
        try
        {
            string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeCamRecord", "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, Path.GetFileName(finalPath) + ".timing.json"), JsonSerializer.Serialize(new
            {
                result.Path, result.Error, result.Seconds, result.CaptureBackend, TargetFps = fps,
                result.CapturedFrames, result.WrittenFrames, result.SkippedSlots, AudioFrames = audioPump?.WrittenFrames ?? 0,
                AudioTimestampFallbackPackets = sound?.TimestampFallbackPackets ?? 0, AudioTimingWarning = sound?.TimingWarning
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Diagnostic storage must never invalidate a completed recording. */ }
        return result;
    }

    private void ReleaseResources()
    {
        try { sound?.Dispose(); }
        finally
        {
            try { avi?.Dispose(); mp4?.Dispose(); }
            finally
            {
                gpu?.Dispose(); desktop?.Dispose(); jpegParameters.Dispose();
                if (preciseTimer) { Native.timeEndPeriod(1); preciseTimer = false; }
            }
        }
    }

    internal Task<RecordingResult> StopAsync(string? reason = null) { stopReason ??= reason; timeline?.Stop(); stopping = true; return Completion; }
}
