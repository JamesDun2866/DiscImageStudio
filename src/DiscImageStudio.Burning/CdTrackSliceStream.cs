using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DiscImageStudio.Burning;

/// <summary>
/// One track (or the special pregap) of a multi-track DAO image: a forward-only window over the
/// single sequentially generated content stream. IMAPI reads a DAO image from the lead-in to the
/// lead-out, so the windows are consumed in order and share the source's position. A window only
/// reads when the shared position is exactly where it continues; anything else is reported as
/// out-of-order access instead of silently writing bytes to the wrong sectors.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class CdTrackSliceStream : IStream
{
    private const int StgTypeStream = 2;
    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int ENotImpl = unchecked((int)0x80004001);

    private readonly SharedSource _source;
    private readonly long _start;
    private readonly long _length;
    private long _position;
    private bool _hasRead;

    internal CdTrackSliceStream(SharedSource source, long start, long length)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        _source = source;
        _start = start;
        _length = length;
    }

    internal long Start => _start;

    internal long Length => _length;

    /// <summary>The generated content and the position all of its windows have read up to.</summary>
    internal sealed class SharedSource(IStream stream)
    {
        internal IStream Stream { get; } = stream ?? throw new ArgumentNullException(nameof(stream));

        internal long Position { get; set; }
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        ArgumentNullException.ThrowIfNull(pv);
        if (cb < 0 || cb > pv.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(cb));
        }

        int count = (int)Math.Min(cb, _length - _position);
        if (count > 0)
        {
            long expected = _start + _position;
            if (_source.Position != expected)
            {
                throw new COMException(
                    $"CD track window at content byte {_start} was read out of order: the shared "
                    + $"stream is at byte {_source.Position}, this window continues at byte {expected}.",
                    StgEInvalidFunction);
            }

            ReadExactly(pv, count);
            _position += count;
            _source.Position += count;
            _hasRead = true;
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt32(pcbRead, count);
        }
    }

    private void ReadExactly(byte[] destination, int count)
    {
        IntPtr read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            int total = 0;
            byte[]? scratch = null;
            while (total < count)
            {
                byte[] target = total == 0 ? destination : scratch ??= new byte[count];
                Marshal.WriteInt32(read, 0);
                _source.Stream.Read(target, count - total, read);
                int got = Marshal.ReadInt32(read);
                if (got <= 0)
                {
                    throw new COMException(
                        $"CD content ended at byte {_source.Position + total}; the track window needs "
                        + $"{count - total} more bytes.", StgEInvalidFunction);
                }

                if (total != 0)
                {
                    Buffer.BlockCopy(target, 0, destination, total, got);
                }

                total += got;
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
        }
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten)
        => throw new COMException("CD track windows are read-only.", StgEInvalidFunction);

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        long requested = dwOrigin switch
        {
            0 => dlibMove,
            1 => checked(_position + dlibMove),
            2 => checked(_length + dlibMove),
            _ => -1,
        };
        // Like the generated stream: IMAPI may seek to the end and back while it inspects the
        // size, which is harmless before the first read. Afterwards only the current position is
        // a valid target.
        bool canSeekBeforeReading = !_hasRead && (requested == 0 || requested == _length);
        if (requested < 0 || (requested != _position && !canSeekBeforeReading))
        {
            throw new COMException("CD track windows are forward-only.", StgEInvalidFunction);
        }

        _position = requested;
        if (plibNewPosition != IntPtr.Zero)
        {
            Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void SetSize(long libNewSize)
        => throw new COMException("CD track windows are read-only.", StgEInvalidFunction);

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        ArgumentNullException.ThrowIfNull(pstm);
        byte[] buffer = new byte[64 * 1024];
        long remaining = Math.Min(cb, _length - _position);
        long total = 0;
        while (remaining > 0)
        {
            int count = (int)Math.Min(buffer.Length, remaining);
            Read(buffer, count, IntPtr.Zero);
            pstm.Write(buffer, count, IntPtr.Zero);
            remaining -= count;
            total += count;
        }

        if (pcbRead != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbRead, total);
        }

        if (pcbWritten != IntPtr.Zero)
        {
            Marshal.WriteInt64(pcbWritten, total);
        }
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert() => throw new COMException("Not implemented for CD track windows.", ENotImpl);

    public void LockRegion(long libOffset, long cb, int dwLockType)
        => throw new COMException("Not implemented for CD track windows.", ENotImpl);

    public void UnlockRegion(long libOffset, long cb, int dwLockType)
        => throw new COMException("Not implemented for CD track windows.", ENotImpl);

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            type = StgTypeStream,
            cbSize = _length,
            grfMode = 0,
        };
    }

    public void Clone(out IStream ppstm)
    {
        ppstm = null!;
        throw new COMException("Not implemented for CD track windows.", ENotImpl);
    }
}
