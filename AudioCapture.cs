using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace FreeCamRecord;

/// <summary>Default-device WASAPI audio mixed into 48 kHz stereo PCM16, without packages.</summary>
public sealed class AudioCapture : IDisposable
{
    private readonly bool systemAudio;
    private readonly bool microphone;
    private readonly List<CaptureTrack> tracks = new();
    private bool started;
    private long timelineOriginFrame;
    private long legacyReadFrame;
    private long completedTimestampFallbackPackets;
    public long TimestampFallbackPackets => Interlocked.Read(ref completedTimestampFallbackPackets) + tracks.Sum(track => track.TimestampFallbackPackets);
    public string? TimingWarning => TimestampFallbackPackets == 0 ? null : UiText.T("音訊裝置回報無效時間戳記，部分音訊時間採用連續封包／到達時間近似。", "The audio device reported invalid timestamps. Some audio timing is approximated using consecutive packets or arrival times.");
    public string? Error
    {
        get
        {
            foreach (var track in tracks)
                if (track.Error != null) return track.Error;
            return null;
        }
    }

    public AudioCapture(bool systemAudio, bool microphone)
    {
        this.systemAudio = systemAudio;
        this.microphone = microphone;
    }

    public void Start()
    {
        if (started) throw new InvalidOperationException(UiText.T("音訊已開始錄製。", "Audio recording has already started."));
        try
        {
            Interlocked.Exchange(ref completedTimestampFallbackPackets, 0);
            SetTimelineOrigin(RecordingTimeline.QpcNow());
            if (systemAudio) tracks.Add(new CaptureTrack(true));
            if (microphone) tracks.Add(new CaptureTrack(false));
            foreach (var track in tracks) track.Start();
            started = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public byte[] ReadFrames(int sampleFrames)
    {
        long firstFrame = Interlocked.Read(ref legacyReadFrame);
        byte[] result = ReadFramesAt(firstFrame, sampleFrames);
        Interlocked.Add(ref legacyReadFrame, sampleFrames);
        return result;
    }

    public void SetTimelineOrigin(long qpcTicks100ns)
    {
        Interlocked.Exchange(ref timelineOriginFrame, TimestampedAudioBuffer.Qpc100nsToFrame(qpcTicks100ns));
        Interlocked.Exchange(ref legacyReadFrame, 0);
    }

    public byte[] ReadFramesAt(long firstFrame, int sampleFrames)
    {
        if (firstFrame < 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
        if (sampleFrames < 0 || sampleFrames > 48000 * 10)
            throw new ArgumentOutOfRangeException(nameof(sampleFrames));
        long absoluteFrame = checked(Interlocked.Read(ref timelineOriginFrame) + firstFrame);
        _ = checked(absoluteFrame + sampleFrames);
        var output = new byte[checked(sampleFrames * 4)];
        if (tracks.Count == 0) return output;
        var mixed = new float[sampleFrames * 2];
        foreach (var track in tracks) track.AddSamples(absoluteFrame, mixed);
        for (int i = 0; i < mixed.Length; i++)
        {
            float value = Math.Clamp(mixed[i], -1f, 1f);
            short sample = (short)Math.Round(value * short.MaxValue);
            output[i * 2] = (byte)sample;
            output[i * 2 + 1] = (byte)(sample >> 8);
        }
        return output;
    }

    public void Stop()
    {
        foreach (var track in tracks) track.Dispose();
        Interlocked.Add(ref completedTimestampFallbackPackets, tracks.Sum(track => track.TimestampFallbackPackets));
        tracks.Clear();
        started = false;
    }

    public void Dispose() => Stop();

    private sealed class CaptureTrack : IDisposable
    {
        private readonly bool loopback;
        private readonly TimestampedAudioBuffer buffer = new();
        private readonly TimestampedAudioResampler resampler;
        private readonly ManualResetEventSlim ready = new(false);
        private Thread? thread;
        private volatile bool stop;
        private Exception? startupError;
        private volatile string? error;
        public string? Error => error;
        public long TimestampFallbackPackets => resampler.TimestampFallbackPackets;

        public CaptureTrack(bool loopback)
        {
            this.loopback = loopback;
            resampler = new TimestampedAudioResampler(buffer);
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = loopback ? "System audio" : "Microphone" };
            thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(8)))
                throw new InvalidOperationException(UiText.T("音訊裝置啟動逾時。", "The audio device took too long to start."));
            if (startupError != null)
                throw new InvalidOperationException((loopback ? UiText.T("無法錄製系統音效：", "Unable to record system audio: ") : UiText.T("無法使用麥克風：", "Unable to use the microphone: ")) + startupError.Message, startupError);
        }

        public void AddSamples(long firstFrame, float[] destination) => buffer.AddAt(firstFrame, destination);

        private void Run()
        {
            object? enumeratorObject = null;
            IMMDevice? device = null;
            IAudioClient? client = null;
            IAudioCaptureClient? capture = null;
            IntPtr format = IntPtr.Zero;
            bool initialized = false, running = false, announced = false;
            try
            {
                Check(CoInitializeEx(IntPtr.Zero, 0));
                initialized = true;
                enumeratorObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!);
                var enumerator = (IMMDeviceEnumerator)enumeratorObject!;
                Check(enumerator.GetDefaultAudioEndpoint(loopback ? 0 : 1, 1, out device));
                Guid clientId = typeof(IAudioClient).GUID;
                Check(device.Activate(ref clientId, 23, IntPtr.Zero, out var clientObject));
                client = (IAudioClient)clientObject;
                Check(client.GetMixFormat(out format));
                int tag = (ushort)Marshal.ReadInt16(format, 0);
                int channels = (ushort)Marshal.ReadInt16(format, 2);
                int rate = Marshal.ReadInt32(format, 4);
                int block = (ushort)Marshal.ReadInt16(format, 12);
                int bits = (ushort)Marshal.ReadInt16(format, 14);
                if (tag == 0xfffe)
                {
                    if ((ushort)Marshal.ReadInt16(format, 16) < 22)
                        throw new InvalidOperationException(UiText.T("音訊延伸格式不完整。", "The extended audio format is incomplete."));
                    Guid subFormat = Marshal.PtrToStructure<Guid>(IntPtr.Add(format, 24));
                    if (subFormat == new Guid("00000003-0000-0010-8000-00AA00389B71")) tag = 3;
                    else if (subFormat == new Guid("00000001-0000-0010-8000-00AA00389B71")) tag = 1;
                    else throw new InvalidOperationException(UiText.T("此音訊裝置使用不支援的格式。", "This audio device uses an unsupported format."));
                }
                if (channels < 1 || channels > 32 || rate < 8000 || rate > 384000 ||
                    !((tag == 3 && bits == 32) || (tag == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32))) ||
                    block < channels * (bits / 8))
                    throw new InvalidOperationException(UiText.T("此音訊裝置使用不支援的取樣格式。", "This audio device uses an unsupported sample format."));
                Check(client.Initialize(0, loopback ? 0x00020000u : 0u, 1_000_000, 0, format, IntPtr.Zero));
                Guid captureId = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref captureId, out var captureObject));
                capture = (IAudioCaptureClient)captureObject;
                Check(client.Start());
                running = true;
                announced = true;
                ready.Set();
                byte[] packet = Array.Empty<byte>();
                float[] decoded = Array.Empty<float>();
                while (!stop)
                {
                    Check(capture.GetNextPacketSize(out uint available));
                    while (available > 0 && !stop)
                    {
                        Check(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpcPosition));
                        try
                        {
                            long arrival = RecordingTimeline.QpcNow();
                            int bytes = checked((int)frames * block);
                            bool silent = (flags & 2) != 0;
                            if (!silent)
                            {
                                if (packet.Length < bytes) packet = new byte[bytes];
                                Marshal.Copy(data, packet, 0, bytes);
                            }
                            int sampleCount = checked((int)frames * 2);
                            if (decoded.Length < sampleCount) decoded = new float[sampleCount];
                            for (int frame = 0; frame < frames; frame++)
                            {
                                float left = 0, right = 0;
                                if (!silent)
                                {
                                    int offset = frame * block;
                                    int stride = bits / 8;
                                    left = Sample(packet, offset, tag, bits);
                                    right = channels == 1 ? left : Sample(packet, offset + stride, tag, bits);
                                    // Standard Windows multichannel order: L, R, C, LFE, back/side pairs.
                                    if (channels > 2)
                                    {
                                        float center = Sample(packet, offset + 2 * stride, tag, bits) * 0.7071f;
                                        left += center; right += center;
                                        if (channels > 3)
                                        {
                                            float bass = Sample(packet, offset + 3 * stride, tag, bits) * 0.25f;
                                            left += bass; right += bass;
                                        }
                                        for (int channel = 4; channel < channels; channel++)
                                        {
                                            float surround = Sample(packet, offset + channel * stride, tag, bits) * 0.5f;
                                            if ((channel & 1) == 0) left += surround; else right += surround;
                                        }
                                    }
                                }
                                decoded[frame * 2] = left;
                                decoded[frame * 2 + 1] = right;
                            }
                            resampler.ProcessPacket(decoded.AsSpan(0, sampleCount), rate,
                                qpcPosition <= long.MaxValue ? (long)qpcPosition : 0, flags, arrival);
                        }
                        finally { Check(capture.ReleaseBuffer(frames)); }
                        Check(capture.GetNextPacketSize(out available));
                    }
                    Thread.Sleep(3);
                }
            }
            catch (Exception ex)
            {
                error = (loopback ? UiText.T("系統音效錄製中斷：", "System audio recording interrupted: ") : UiText.T("麥克風錄製中斷：", "Microphone recording interrupted: ")) + ex.Message;
                if (!announced) startupError = ex;
            }
            finally
            {
                if (running && client != null) client.Stop();
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Release(capture); Release(client); Release(device); Release(enumeratorObject);
                ready.Set();
                if (initialized) CoUninitialize();
            }
        }

        private static float Sample(byte[] data, int offset, int tag, int bits)
        {
            if (tag == 3)
            {
                float sample = BitConverter.ToSingle(data, offset);
                return float.IsFinite(sample) ? sample : 0;
            }
            return bits switch
            {
                8 => (data[offset] - 128) / 128f,
                16 => BitConverter.ToInt16(data, offset) / 32768f,
                24 => ((data[offset] << 8 | data[offset + 1] << 16 | data[offset + 2] << 24) >> 8) / 8388608f,
                32 => BitConverter.ToInt32(data, offset) / 2147483648f,
                _ => 0
            };
        }

        public void Dispose()
        {
            stop = true;
            // The worker never invokes UI code; joining prevents releasing live COM state.
            thread?.Join();
            ready.Dispose();
        }
    }

    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out IntPtr collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr activation, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong performanceCounter);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
