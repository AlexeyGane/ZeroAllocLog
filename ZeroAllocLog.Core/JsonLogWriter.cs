using System.Buffers;
using System.Buffers.Text;
using System.IO.Pipelines;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace ZeroAllocLog.Core;

public static class JsonLogWriter
{
    // Just detection flags (JIT will map Vector<T> to SSE/AVX/AVX2/AVX-512 where available)
    public static readonly bool Avx2Available = Avx2.IsSupported;
    public static readonly bool Avx512Available = Avx512F.IsSupported;

    public static void WriteEvent(PipeWriter writer, in LogEvent evt)
    {
        // We write JSON manually into a pooled buffer, then copy to PipeWriter once.
        var pool = ArrayPool<byte>.Shared;
        // worst case: very rough upper bound
        var buffer = pool.Rent(1024 + (evt.Message.Length * 6));

        try
        {
            var span = buffer.AsSpan();
            int offset = 0;

            span[offset++] = (byte)'{';

            // "Timestamp":"..."
            offset += WritePropertyName(span.Slice(offset), "Timestamp");
            offset += WriteStringValue(span.Slice(offset), evt.Timestamp.ToString("O"));

            span[offset++] = (byte)',';

            // "Level":"..."
            offset += WritePropertyName(span.Slice(offset), "Level");
            offset += WriteStringValue(span.Slice(offset), evt.Level.ToString());

            span[offset++] = (byte)',';

            // "Category":"..."
            offset += WritePropertyName(span.Slice(offset), "Category");
            offset += WriteStringValue(span.Slice(offset), evt.Category);

            span[offset++] = (byte)',';

            // "Message":"..."
            offset += WritePropertyName(span[offset..], "Message");
            offset += WriteStringValue(span[offset..], evt.Message);

            if (evt.Exception is not null)
            {
                span[offset++] = (byte)',';
                offset += WritePropertyName(span[offset..], "Exception");
                offset += WriteStringValue(span[offset..], evt.Exception.ToString() ?? string.Empty);
            }

            if (evt.Properties.Count > 0)
            {
                span[offset++] = (byte)',';
                offset += WriteRaw(span[offset..], "\"Properties\":{");

                bool first = true;
                foreach (var kv in evt.Properties)
                {
                    if (!first)
                    {
                        span[offset++] = (byte)',';
                    }

                    first = false;

                    offset += WritePropertyName(span[offset..], kv.Key);
                    offset += WriteDynamicValue(span[offset..], kv.Value);
                }

                span[offset++] = (byte)'}';
            }

            span[offset++] = (byte)'}';
            span[offset++] = (byte)'\n';

            writer.Write(span[..offset]);
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private static int WriteRaw(Span<byte> dest, string raw)
    {
        return Encoding.UTF8.GetBytes(raw.AsSpan(), dest);
    }

    private static int WritePropertyName(Span<byte> dest, string name)
    {
        int offset = 0;
        dest[offset++] = (byte)'"';
        offset += EscapeStringToUtf8(dest[offset..], name.AsSpan());
        dest[offset++] = (byte)'"';
        dest[offset++] = (byte)':';
        return offset;
    }

    private static int WriteStringValue(Span<byte> dest, string value)
    {
        int offset = 0;
        dest[offset++] = (byte)'"';
        offset += EscapeStringToUtf8(dest[offset..], value.AsSpan());
        dest[offset++] = (byte)'"';
        return offset;
    }

    private static int WriteDynamicValue(Span<byte> dest, object? value)
    {
        if (value is null)
        {
            "null"u8.CopyTo(dest);
            return 4;
        }

        return value switch
        {
            string s => WriteStringValue(dest, s),
            int i => WriteNumber(dest, i),
            long l => WriteNumber(dest, l),
            bool b => WriteBool(dest, b),
            double d => WriteDouble(dest, d),
            _ => WriteStringValue(dest, value.ToString() ?? string.Empty)
        };
    }

    private static int WriteNumber(Span<byte> dest, long value)
    {
        Utf8Formatter.TryFormat(value, dest, out var written);
        return written;
    }

    private static int WriteBool(Span<byte> dest, bool value)
    {
        if (value)
        {
            "true"u8.CopyTo(dest);
            return 4;
        }
        else
        {
            "false"u8.CopyTo(dest);
            return 5;
        }
    }

    private static int WriteDouble(Span<byte> dest, double value)
    {
        Utf8Formatter.TryFormat(value, dest, out var written);
        return written;
    }

    // SIMD-accelerated escaping using Vector<ushort> (JIT maps to SSE/AVX/AVX2/AVX-512)
    private static int EscapeStringToUtf8(Span<byte> dest, ReadOnlySpan<char> src)
    {
        int offset = 0;
        int i = 0;

        if (Vector.IsHardwareAccelerated && src.Length >= Vector<ushort>.Count)
        {
            var vecSize = Vector<ushort>.Count;
            var chars = MemoryMarshal.Cast<char, ushort>(src);
            var controlLimit = new Vector<ushort>(0x20);
            var quote = new Vector<ushort>('\"');
            var slash = new Vector<ushort>('\\');

            while (i <= chars.Length - vecSize)
            {
                var block = new Vector<ushort>(chars.Slice(i, vecSize));

                var isControl = Vector.LessThan(block, controlLimit);
                var isQuote = Vector.Equals(block, quote);
                var isSlash = Vector.Equals(block, slash);

                var any = Vector.BitwiseOr(isControl, Vector.BitwiseOr(isQuote, isSlash));
                if (Vector.EqualsAll(any, Vector<ushort>.Zero))
                {
                    for (int j = 0; j < vecSize; j++)
                    {
                        offset += EncodeCharUtf8(dest[offset..], (char)chars[i + j]);
                    }

                    i += vecSize;
                    continue;
                }

                break;
            }
        }

        for (; i < src.Length; i++)
        {
            var ch = src[i];

            if (ch < 0x20 || ch == '\"' || ch == '\\')
            {
                offset += EscapeChar(dest[offset..], ch);
            }
            else
            {
                offset += EncodeCharUtf8(dest[offset..], ch);
            }
        }

        return offset;
    }

    private static int EncodeCharUtf8(Span<byte> dest, char ch)
    {
        if (ch <= 0x7F)
        {
            dest[0] = (byte)ch;
            return 1;
        }

        Span<char> tmp = stackalloc char[1];
        tmp[0] = ch;
        return Encoding.UTF8.GetBytes(tmp, dest);
    }

    private static int EscapeChar(Span<byte> dest, char ch)
    {
        int offset = 0;
        dest[offset++] = (byte)'\\';

        switch (ch)
        {
            case '\"':
                dest[offset++] = (byte)'"';
                return offset;
            case '\\':
                dest[offset++] = (byte)'\\';
                return offset;
            case '\b':
                dest[offset++] = (byte)'b';
                return offset;
            case '\f':
                dest[offset++] = (byte)'f';
                return offset;
            case '\n':
                dest[offset++] = (byte)'n';
                return offset;
            case '\r':
                dest[offset++] = (byte)'r';
                return offset;
            case '\t':
                dest[offset++] = (byte)'t';
                return offset;
            default:
                return offset + WriteUnicodeEscape(dest[offset..], ch);
        }
    }

    private static int WriteUnicodeEscape(Span<byte> dest, char ch)
    {
        // \u00XX
        dest[0] = (byte)'u';
        dest[1] = (byte)'0';
        dest[2] = (byte)'0';

        int hi = (ch >> 4) & 0xF;
        int lo = ch & 0xF;

        dest[3] = ToHex(hi);
        dest[4] = ToHex(lo);

        return 5;
    }

    private static byte ToHex(int v) => (byte)(v < 10 ? '0' + v : 'A' + (v - 10));
}