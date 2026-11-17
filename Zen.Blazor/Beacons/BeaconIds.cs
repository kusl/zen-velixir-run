using System.Buffers.Binary;

namespace Zen.Blazor.Beacons;

public static class BeaconIds
{
    public static Guid Floor(DateTimeOffset at)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        Span<byte> stamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(stamp, Math.Max(0, at.ToUnixTimeMilliseconds()));
        stamp[2..].CopyTo(bytes);
        bytes[6] = 0x70;
        bytes[8] = 0x80;
        return new Guid(bytes, bigEndian: true);
    }
}
