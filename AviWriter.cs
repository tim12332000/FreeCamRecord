using System.Text;

namespace FreeCamRecord;

// RIFF AVI: independently decodable MJPEG frames, optionally interleaved PCM audio.
internal sealed class AviWriter : IDisposable
{
    private readonly BinaryWriter writer;
    private readonly List<(string Id, uint Offset, uint Size)> index = new();
    private readonly long mainFrames, videoFrames, audioFrames, movieSize, movieStart;
    private readonly bool audio;
    private uint frames, samples;
    private bool disposed;
    public long Length => writer.BaseStream.Length;

    internal AviWriter(string path, int width, int height, int fps, bool audio)
    {
        this.audio = audio;
        writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read), Encoding.ASCII);
        FourCC("RIFF"); writer.Write(0u); FourCC("AVI ");
        long header = BeginList("hdrl");
        FourCC("avih"); writer.Write(56u);
        writer.Write(1_000_000 / fps); writer.Write(0); writer.Write(0); writer.Write(0x10);
        mainFrames = Position; writer.Write(0u); writer.Write(0); writer.Write(audio ? 2 : 1);
        writer.Write(width * height * 3); writer.Write(width); writer.Write(height);
        for (int i = 0; i < 4; i++) writer.Write(0);
        long video = BeginList("strl");
        FourCC("strh"); writer.Write(56u); FourCC("vids"); FourCC("MJPG");
        writer.Write(0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0);
        writer.Write(1); writer.Write(fps); writer.Write(0);
        videoFrames = Position; writer.Write(0u); writer.Write(width * height * 3);
        writer.Write(-1); writer.Write(0); writer.Write((short)0); writer.Write((short)0);
        writer.Write((short)width); writer.Write((short)height);
        FourCC("strf"); writer.Write(40u); writer.Write(40); writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)24); FourCC("MJPG");
        writer.Write(width * height * 3); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        EndList(video);
        if (audio)
        {
            long stream = BeginList("strl");
            FourCC("strh"); writer.Write(56u); FourCC("auds"); writer.Write(0);
            writer.Write(0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(0);
            writer.Write(4); writer.Write(48000 * 4); writer.Write(0);
            audioFrames = Position; writer.Write(0u); writer.Write(12800); writer.Write(-1); writer.Write(4);
            for (int i = 0; i < 4; i++) writer.Write((short)0);
            FourCC("strf"); writer.Write(16u); writer.Write((ushort)1); writer.Write((ushort)2);
            writer.Write(48000); writer.Write(48000 * 4); writer.Write((ushort)4); writer.Write((ushort)16);
            EndList(stream);
        }
        EndList(header);
        movieSize = BeginList("movi"); movieStart = movieSize + 4;
    }

    private long Position => writer.BaseStream.Position;
    private void FourCC(string value) => writer.Write(Encoding.ASCII.GetBytes(value));
    private long BeginList(string kind) { FourCC("LIST"); long size = Position; writer.Write(0); FourCC(kind); return size; }
    private void Patch(long offset, uint value) { long end = Position; writer.BaseStream.Position = offset; writer.Write(value); writer.BaseStream.Position = end; }
    private void EndList(long size) => Patch(size, checked((uint)(Position - size - 4)));
    private void Chunk(string id, byte[] data)
    {
        index.Add((id, checked((uint)(Position - movieStart)), checked((uint)data.Length)));
        FourCC(id); writer.Write(data.Length); writer.Write(data);
        if ((data.Length & 1) != 0) writer.Write((byte)0);
    }
    internal void WriteVideo(byte[] jpeg, long slot)
    {
        if (slot < frames) throw new ArgumentOutOfRangeException(nameof(slot));
        // Standard AVI null frames preserve elapsed fixed-rate slots without re-encoding.
        while (frames < slot) { Chunk("00dc", Array.Empty<byte>()); frames++; }
        Chunk("00dc", jpeg); frames++;
    }
    internal void ExtendVideoTo(long slots)
    {
        while (frames < slots) { Chunk("00dc", Array.Empty<byte>()); frames++; }
    }
    internal void WriteAudio(byte[] pcm)
    {
        if (!audio || pcm.Length % 4 != 0) throw new ArgumentException(UiText.T("AVI 音訊資料無效。", "The AVI audio data is invalid."), nameof(pcm));
        if (pcm.Length == 0) return;
        Chunk("01wb", pcm); samples += (uint)(pcm.Length / 4);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            EndList(movieSize);
            FourCC("idx1"); writer.Write(checked(index.Count * 16));
            foreach (var entry in index)
            {
                FourCC(entry.Id); writer.Write(entry.Size == 0 ? 0 : 0x10); writer.Write(entry.Offset); writer.Write(entry.Size);
            }
            Patch(mainFrames, frames); Patch(videoFrames, frames);
            if (audio) Patch(audioFrames, samples);
            Patch(4, checked((uint)(Position - 8)));
            writer.Flush();
            ((FileStream)writer.BaseStream).Flush(true);
        }
        finally { writer.Dispose(); }
    }
}
