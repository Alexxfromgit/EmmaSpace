using System.Buffers;
using System.IO.Compression;

namespace EmmaSpace.Core.Snapshots;

/// <summary>
/// Reads Brotli-compressed data that must make up the rest of <c>input</c>. <see cref="BrotliStream"/> can't tell:
/// it reads ahead and quietly drops whatever follows the end of the compressed data. Here <see cref="EnsureEnded"/>
/// checks that the data is complete and that nothing follows it, reading at most one byte past it, so it works on
/// streams that can't seek too. Leaves <c>input</c> open.
/// </summary>
internal sealed class BrotliReader(Stream input) : Stream
{
    private BrotliDecoder _decoder;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _start, _end;
    private bool _done, _truncated;

    public override int Read(Span<byte> destination)
    {
        if (destination.IsEmpty) return 0;
        while (!_done && !_truncated)
        {
            var status = _decoder.Decompress(_buffer.AsSpan(_start, _end - _start), destination, out var consumed, out var written);
            _start += consumed;
            switch (status)
            {
                case OperationStatus.Done:
                    _done = true;
                    return written;
                case OperationStatus.DestinationTooSmall:
                    return written;
                case OperationStatus.InvalidData:
                    throw new InvalidDataException("Snapshot data is damaged.");
            }
            // NeedMoreData: hand over what was decoded, or read on.
            if (written > 0) return written;
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }
            var read = input.Read(_buffer, _end, _buffer.Length - _end);
            if (read == 0) _truncated = true; // the end of the stream before the end of the data
            _end += read;
        }
        return 0;
    }

    /// <summary>
    /// Throws <see cref="InvalidDataException"/> unless the compressed data ends right after what was read from it, and
    /// the stream right after the compressed data.
    /// </summary>
    public void EnsureEnded()
    {
        Span<byte> one = stackalloc byte[1];
        if (Read(one) != 0) throw new InvalidDataException("Snapshot has unexpected data at the end.");
        if (_truncated) throw new InvalidDataException("Snapshot data is damaged.");
        if (_start < _end || input.ReadByte() >= 0) throw new InvalidDataException("Snapshot has unexpected data at the end.");
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    protected override void Dispose(bool disposing)
    {
        if (disposing) _decoder.Dispose();
        base.Dispose(disposing);
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
