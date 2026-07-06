using System;
using System.Buffers;
using System.Buffers.Binary;

namespace Manatee.Core.State;

// Little-endian binary writers/readers for the snapshot and whole-netlist
// serialization paths (api.md §14). Deterministic byte layout is load-bearing:
// the serialization laws are stated as byte-equalities, so every primitive is
// written little-endian with a fixed width and no culture/format dependence.
//
// The writer batches into a caller-owned IBufferWriter<byte> in one rented span
// per flush window; the core-side Snapshot claim is 0 B because the destination
// buffer is caller-owned (the writer itself holds only a cursor into the
// caller's span). The reader is a ref struct over a ReadOnlySpan<byte>.

internal ref struct SpanWriter
{
    private readonly IBufferWriter<byte> _dst;
    private Span<byte> _buf;
    private int _pos;

    public SpanWriter(IBufferWriter<byte> dst, int hint = 256)
    {
        _dst = dst;
        _buf = dst.GetSpan(hint);
        _pos = 0;
    }

    private void Ensure(int n)
    {
        if (_pos + n <= _buf.Length) return;
        Flush();
        _buf = _dst.GetSpan(Math.Max(n, 256));
        _pos = 0;
    }

    /// <summary>Commit the cursor to the underlying writer. Call once when done.</summary>
    public void Flush()
    {
        if (_pos > 0) { _dst.Advance(_pos); _pos = 0; _buf = default; }
    }

    public void Byte(byte b) { Ensure(1); _buf[_pos++] = b; }
    public void Bool(bool v) => Byte(v ? (byte)1 : (byte)0);

    public void Int32(int v)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buf.Slice(_pos), v);
        _pos += 4;
    }

    public void UInt32(uint v)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buf.Slice(_pos), v);
        _pos += 4;
    }

    public void Int64(long v)
    {
        Ensure(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buf.Slice(_pos), v);
        _pos += 8;
    }

    public void UInt64(ulong v)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buf.Slice(_pos), v);
        _pos += 8;
    }

    // Doubles are written by their raw IEEE-754 bit pattern so the round-trip is
    // bit-for-bit (law 4): no decimal formatting ever touches a stored double.
    public void Double(double v) => UInt64((ulong)BitConverter.DoubleToInt64Bits(v));

    public void Key(in ExternalKey k) { UInt64(k.Hi); UInt64(k.Lo); }
    public void StateKey(in StateKey k) { UInt64(k.Hi); UInt64(k.Lo); }

    public void Bytes(ReadOnlySpan<byte> src)
    {
        Ensure(src.Length);
        src.CopyTo(_buf.Slice(_pos));
        _pos += src.Length;
    }
}

internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _src;
    private int _pos;

    public SpanReader(ReadOnlySpan<byte> src) { _src = src; _pos = 0; }

    public int Position => _pos;
    public bool AtEnd => _pos >= _src.Length;

    public byte Byte() => _src[_pos++];
    public bool Bool() => Byte() != 0;

    public int Int32()
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(_src.Slice(_pos));
        _pos += 4; return v;
    }

    public uint UInt32()
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_src.Slice(_pos));
        _pos += 4; return v;
    }

    public long Int64()
    {
        var v = BinaryPrimitives.ReadInt64LittleEndian(_src.Slice(_pos));
        _pos += 8; return v;
    }

    public ulong UInt64()
    {
        var v = BinaryPrimitives.ReadUInt64LittleEndian(_src.Slice(_pos));
        _pos += 8; return v;
    }

    public double Double() => BitConverter.Int64BitsToDouble((long)UInt64());

    public ExternalKey Key() => new(UInt64(), UInt64());
    public StateKey StateKey() => new(UInt64(), UInt64());

    public ReadOnlySpan<byte> Bytes(int n)
    {
        var s = _src.Slice(_pos, n);
        _pos += n; return s;
    }
}
