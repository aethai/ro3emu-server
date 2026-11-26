using System.IO.Compression;

namespace Ro3Emu.Server.Net;

/// <summary>
/// Mirror of Phonest.Rpc.Convertor: one persistent zlib stream per direction (sync-flushed after every frame)
/// wrapped in RC4. Inbound = RC4 decrypt, then inflate; outbound = deflate + sync flush, then RC4 encrypt.
/// </summary>
public sealed class StreamConvertor
{
    private readonly Rc4 _decrypt;
    private readonly Rc4 _encrypt;
    private readonly ChunkReadStream _compressedIn = new();
    private readonly ZLibStream _inflater;
    // Outbound: the runtime's native zlib (Flush = Z_SYNC_FLUSH) into a reused buffer so per-frame flushes allocate
    // almost nothing.
    private readonly MemoryStream _packed = new();
    private readonly ZLibStream _deflater;
    // Input runs on the connection reader, output on its writer task, concurrently: each direction has its own state.
    private readonly byte[] _inScratch = new byte[16 * 1024];
    private byte[] _inCipher = [];

    public StreamConvertor(byte[] sessionKey)
    {
        _decrypt = new Rc4(sessionKey);
        _encrypt = new Rc4(sessionKey);
        _inflater = new ZLibStream(_compressedIn, CompressionMode.Decompress, leaveOpen: true);
        _deflater = new ZLibStream(_packed, CompressionLevel.Optimal, leaveOpen: true);
    }

    /// <summary>Decrypts and inflates a chunk of received bytes; returns the plain frame bytes it yields (possibly none).</summary>
    public byte[] ConvertInput(ReadOnlySpan<byte> received)
    {
        BeginInput(received);
        using var plain = new MemoryStream();
        while (ReadInput() is { IsEmpty: false } step) plain.Write(step);
        return plain.ToArray();
    }

    /// <summary>Decrypts a chunk of received bytes for <see cref="ReadInput"/>, which must have run it dry before the next one.</summary>
    public void BeginInput(ReadOnlySpan<byte> received)
    {
        if (received.Length == 0)
        {
            _compressedIn.Feed([], 0);
            return;
        }
        if (_inCipher.Length < received.Length) _inCipher = new byte[received.Length];
        received.CopyTo(_inCipher);
        _decrypt.Process(_inCipher.AsSpan(0, received.Length));
        _compressedIn.Feed(_inCipher, received.Length);
    }

    /// <summary>The next plain bytes of the chunk, at most 16 KiB and valid until the next call; empty when it has run dry.</summary>
    public ReadOnlySpan<byte> ReadInput()
    {
        int n = _inflater.Read(_inScratch, 0, _inScratch.Length);
        return n > 0 ? _inScratch.AsSpan(0, n) : default;
    }

    /// <summary>Deflates one or more whole frames with a sync flush and encrypts the result.</summary>
    public byte[] ConvertOutput(ReadOnlySpan<byte> frames)
    {
        var output = new MemoryStream();
        ConvertOutput(frames, output);
        return output.ToArray();
    }

    /// <summary>Deflates, sync-flushes and encrypts <paramref name="frames"/>, appending the wire bytes to
    /// <paramref name="destination"/> without an intermediate array.</summary>
    public void ConvertOutput(ReadOnlySpan<byte> frames, Stream destination)
    {
        _packed.SetLength(0);
        _deflater.Write(frames);
        _deflater.Flush();
        var wire = _packed.GetBuffer().AsSpan(0, (int)_packed.Length);
        _encrypt.Process(wire);
        destination.Write(wire);
    }

    private sealed class ChunkReadStream : Stream
    {
        private byte[] _buffer = [];
        private int _offset;
        private int _remaining;

        internal void Feed(byte[] buffer, int count)
        {
            _buffer = buffer;
            _offset = 0;
            _remaining = count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0 || buffer.IsEmpty) return 0;
            int take = Math.Min(_remaining, buffer.Length);
            _buffer.AsSpan(_offset, take).CopyTo(buffer);
            _offset += take;
            _remaining -= take;
            return take;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
