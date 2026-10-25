using System.Drawing.Imaging;
using System.Text.Json;

namespace FreeCamRecord;

// Pure generated pictures/PCM and an injected clock: no desktop, browser, input or audio device.
internal static class TimingSelfTest
{
    private static int checks;
    private static readonly List<object> files = new();
    private static string output = "";

    internal static int Run()
    {
        output = Path.Combine(Environment.CurrentDirectory, ".omx", "verification", "timing-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        try
        {
            CheckSlots(); CheckAudioPump(); CheckAudioBuffer();
            MakeVideo("clock-165.mp4", 165, 3, true, false);
            MakeVideo("clock-60.mp4", 60, 3, true, false);
            MakeVideo("long-gap-120.mp4", 120, 4, true, false);
            MakeVideo("clock-60.avi", 60, 3, true, false);
            MakeVideo("single-picture.mp4", 165, 5, false, true);
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Files = files }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
            return 1;
        }
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++;
    }

    private static long CeilSlot(int index, int fps) => ((long)index * TimeSpan.TicksPerSecond + fps - 1) / fps;

    private static void CheckSlots()
    {
        foreach (int fps in new[] { 15, 30, 60, 120, 144, 165 })
        {
            long now = 123 * TimeSpan.TicksPerSecond;
            var clock = new RecordingTimeline(fps, () => now);
            Require(clock.TryTakeVideo(out var first) && first.Timestamp == 0 && first.Index == 0, "first picture begins at origin");
            long previous = 0, written = 1;
            foreach (int milliseconds in new[] { 100, 600, 2100, 2200, 2700 })
            {
                now = clock.Origin + milliseconds * TimeSpan.TicksPerMillisecond;
                Require(clock.TryTakeVideo(out var slot), "a late clock produces a current slot");
                Require(slot.Index > previous && slot.Timestamp <= clock.Elapsed, "timestamps remain chronological");
                Require(clock.Elapsed - slot.Timestamp < TimeSpan.TicksPerSecond / fps + 1, "PTS follows elapsed time within one cadence slot");
                Require(!clock.TryTakeVideo(out _), "one clock instant cannot emit a catch-up burst");
                previous = slot.Index; written++;
                Require(clock.SkippedSlots + written == previous + 1, "time slots and written pictures are separate counts");
            }
            clock.Stop(); long stopped = clock.Elapsed; now += 10 * TimeSpan.TicksPerSecond;
            clock.Stop();
            Require(clock.Elapsed == stopped && !clock.TryTakeVideo(out _), "stop time does not include encoder finalization");
        }
        long timestamp = 0;
        var delayedFirst = new RecordingTimeline(165, () => timestamp);
        timestamp = TimeSpan.TicksPerSecond;
        Require(delayedFirst.TryTakeVideo(out var initial) && initial.Index == 0, "known initial image retains origin when submission is delayed");
        Require(delayedFirst.TryTakeVideo(out var late) && late.Index == 165, "next picture skips to current physical time");
        Require(RecordingTimeline.AudioFramesAt(24 * TimeSpan.TicksPerHour) == 24L * 3600 * 48000, "long-duration integer conversion does not overflow");
    }

    private static void CheckAudioPump()
    {
        var ranges = new List<(long First, int Count)>();
        var pump = new AudioTimelinePump((first, count) => new byte[count * 4], (pcm, first) => ranges.Add((first, pcm.Length / 4)));
        pump.DrainUntil(TimeSpan.TicksPerMillisecond * 23);
        Require(pump.WrittenFrames == 960, "audio emits complete 10ms blocks");
        pump.DrainUntil(TimeSpan.TicksPerSecond * 3 + 123, true);
        Require(pump.WrittenFrames == RecordingTimeline.AudioFramesAt(TimeSpan.TicksPerSecond * 3 + 123), "audio duration follows clock despite sparse video");
        Require(ranges.Zip(ranges.Skip(1)).All(pair => pair.First.First + pair.First.Count == pair.Second.First), "audio positions stay contiguous");
        long before = pump.WrittenFrames; pump.DrainUntil(0, true);
        Require(pump.WrittenFrames == before, "audio does not move backwards");
    }

    private static void CheckAudioBuffer()
    {
        var buffer = new TimestampedAudioBuffer(8);
        buffer.WriteAt(100, Enumerable.Repeat(.25f, 16).ToArray());
        buffer.WriteAt(112, Enumerable.Repeat(.75f, 16).ToArray());
        var missing = new float[16]; buffer.AddAt(100, missing);
        Require(missing.All(x => x == 0), "expired audio is silence rather than retimed newer sound");
        var retained = new float[16]; buffer.AddAt(112, retained);
        Require(retained.All(x => x == .75f), "retained audio keeps its absolute position");
        var gap = new TimestampedAudioBuffer(48000);
        gap.WriteAt(500, Enumerable.Repeat(.5f, 20).ToArray());
        var samples = new float[40]; gap.AddAt(490, samples);
        Require(samples.Take(20).All(x => x == 0) && samples.Skip(20).All(x => x == .5f), "a missing interval is not compressed out of audio");
        var resampled = new TimestampedAudioBuffer();
        var converter = new TimestampedAudioResampler(resampled);
        var source = Enumerable.Repeat(.25f, 4410 * 2).ToArray();
        long packetOrigin = 100 * TimeSpan.TicksPerSecond;
        converter.ProcessPacket(source, 44100, packetOrigin, 0, packetOrigin + TimeSpan.TicksPerSecond / 10);
        converter.ProcessPacket(source, 44100, packetOrigin + TimeSpan.TicksPerSecond / 10, 0, packetOrigin + TimeSpan.TicksPerSecond / 5);
        var recovered = new float[9500 * 2];
        resampled.AddAt(TimestampedAudioBuffer.Qpc100nsToFrame(packetOrigin), recovered);
        Require(recovered.All(value => Math.Abs(value - .25f) < .0001f), "44.1kHz packets share a continuous 48k physical clock");
        converter.ProcessPacket(source, 44100, 0, 4, packetOrigin + TimeSpan.TicksPerSecond * 3 / 10);
        Require(converter.TimestampFallbackPackets == 1, "invalid device timestamps are reported rather than silently ignored");
    }

    private static void MakeVideo(string name, int fps, int seconds, bool audio, bool single)
    {
        string path = Path.Combine(output, name);
        bool isAvi = name.EndsWith(".avi", StringComparison.Ordinal);
        bool longGap = name.StartsWith("long-gap", StringComparison.Ordinal);
        using var image = new Bitmap(64, 48, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(image);
        using var mp4 = isAvi ? null : new Mp4Writer(path, image.Width, image.Height, fps, audio, allowHardware: false);
        using var avi = isAvi ? new AviWriter(path, image.Width, image.Height, fps, audio) : null;
        long now = 100 * TimeSpan.TicksPerSecond;
        var clock = new RecordingTimeline(fps, () => now);
        var timestamps = new List<long>();
        var audioPump = audio ? new AudioTimelinePump(Tone, (pcm, first) => { if (mp4 != null) mp4.WriteAudio(pcm, first); else avi!.WriteAudio(pcm); }) : null;
        for (int index = 0; index < (single ? 1 : fps * seconds); index++)
        {
            double time = (double)index / fps;
            // Artificial producer/encoder pauses of 100ms, 500ms and a later 400ms gap.
            if (!single && (longGap ? time >= .5 && time < 2 : (time >= .4 && time < .9) || (time >= 1.6 && time < 1.7) || (time >= 2.4 && time < 2.8))) continue;
            now = clock.Origin + CeilSlot(index, fps);
            Require(clock.TryTakeVideo(out var slot), "generated picture has an elapsed-clock slot");
            graphics.Clear(time < .9 ? Color.Red : time < 1.7 ? Color.Lime : time < 2.8 ? Color.Blue : Color.Yellow);
            if (mp4 != null) mp4.WriteVideo(image, slot.Timestamp, slot.Duration, slot.Skipped != 0);
            else
            {
                using var bytes = new MemoryStream(); image.Save(bytes, ImageFormat.Jpeg);
                avi!.WriteVideo(bytes.ToArray(), slot.Index);
            }
            timestamps.Add(slot.Timestamp);
            audioPump?.DrainUntil(Math.Max(0, clock.Elapsed - TimeSpan.TicksPerMillisecond * 50));
        }
        now = clock.Origin + seconds * TimeSpan.TicksPerSecond; clock.Stop();
        audioPump?.DrainUntil(clock.Elapsed, true);
        mp4?.CompleteVideoAt(clock.Elapsed);
        avi?.ExtendVideoTo(fps * seconds);
        Require(!audio || audioPump!.WrittenFrames == seconds * 48000, "file audio has exactly the physical duration of PCM input");
        files.Add(new { Name = name, Fps = fps, Seconds = seconds, Audio = audio, InputPictures = timestamps.Count,
            SkippedSlots = clock.SkippedSlots, Timestamps = timestamps, AudioInputFrames = audioPump?.WrittenFrames ?? 0,
            ColorChecks = single ? new[] { new { Time = 4.5, Color = "red" } } : new[] { new { Time = .7, Color = "red" }, new { Time = 1.65, Color = longGap ? "red" : "green" }, new { Time = 2.6, Color = "blue" }, new { Time = 2.9, Color = "yellow" } } });
    }

    private static byte[] Tone(long firstFrame, int frames)
    {
        var pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            double time = (double)(firstFrame + i) / 48000;
            // The first beep falls INSIDE the video gap, proving audio is independently clocked.
            short sample = time is >= .6 and < .7 || time is >= 1.6 and < 1.7 || time is >= 2.6 and < 2.7
                ? (short)(Math.Sin(time * Math.PI * 2 * 880) * 6000) : (short)0;
            pcm[i * 4] = pcm[i * 4 + 2] = (byte)sample;
            pcm[i * 4 + 1] = pcm[i * 4 + 3] = (byte)(sample >> 8);
        }
        return pcm;
    }
}
