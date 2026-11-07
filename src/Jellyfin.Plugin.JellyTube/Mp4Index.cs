using System.Buffers.Binary;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Finds the ISO-BMFF sidx and initialization byte ranges without downloading the media payload.</summary>
public static class Mp4Index
{
    public static (long IndexStart, long IndexEnd, long InitEnd) Read(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset + 8 <= bytes.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            var header = 8;
            if (size == 1)
            {
                if (offset + 16 > bytes.Length) break;
                var large = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(offset + 8, 8));
                if (large > int.MaxValue) break; size = (long)large; header = 16;
            }
            if (size < header || size > bytes.Length - offset) break;
            if (bytes.Slice(offset + 4, 4).SequenceEqual("sidx"u8))
            {
                if (offset == 0 || size < 32) throw new ProviderException("The stream has an invalid segment index.");
                return (offset, offset + size - 1, offset - 1);
            }
            offset += (int)size;
        }
        throw new ProviderException("This format has no supported seek index. Use Download for a local copy.");
    }
    public static SegmentIndex Segments(ReadOnlySpan<byte> bytes)
    {
        var range = Read(bytes); var start = (int)range.IndexStart; var end = (int)range.IndexEnd;
        var header = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(start, 4)) == 1 ? 16 : 8;
        var version = bytes[start + header]; var pos = start + header + 8;
        var scale = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4)); pos += 4;
        if (scale == 0 || version > 1) throw new ProviderException("Unsupported segment index timing.");
        ulong presentation, offset;
        if (version == 0)
        {
            if (pos + 12 > end + 1) throw new ProviderException("Truncated segment index.");
            presentation = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4)); offset = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4)); pos += 8;
        }
        else
        {
            if (pos + 20 > end + 1) throw new ProviderException("Truncated segment index.");
            presentation = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(pos, 8)); offset = BinaryPrimitives.ReadUInt64BigEndian(bytes.Slice(pos + 8, 8)); pos += 16;
        }
        pos += 2; var count = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(pos, 2)); pos += 2;
        if (count == 0 || count > 20000 || pos + count * 12 > end + 1 || offset > (ulong)(long.MaxValue - end - 1)) throw new ProviderException("Invalid segment index references.");
        var next = checked(end + 1L + (long)offset); var segments = new List<MediaSegment>(count);
        for (var i = 0; i < count; i++, pos += 12)
        {
            var reference = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos, 4));
            var duration = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(pos + 4, 4));
            if ((reference & 0x80000000) != 0 || (reference & 0x7fffffff) == 0 || duration == 0) throw new ProviderException("Unsupported nested or empty media segment.");
            var size = reference & 0x7fffffff; segments.Add(new(next, checked(next + size - 1), duration)); next = checked(next + size);
        }
        return new(range.IndexStart, range.IndexEnd, range.InitEnd, scale, presentation, segments);
    }
}
