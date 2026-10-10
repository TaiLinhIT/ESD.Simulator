using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ESD.Protocol;

public static class FrameCodec
{
    public static byte[] Encode(EsdFrame f, ReadOnlySpan<byte> key)
    {
        if (f.Payload.Length > ProtocolConstants.MaxPayload)
            throw new ArgumentOutOfRangeException(nameof(f.Payload));

        int total = 20 + f.Payload.Length + 16 + 2;
        var b = new byte[total];
        int p = 0;
        b[p++] = 0xAA; b[p++] = 0x55;
        b[p++] = f.Version; b[p++] = (byte)f.Flags;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), f.DeviceId);   p += 4;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p, 2), (ushort)f.Command); p += 2;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), f.Sequence);   p += 4;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p, 4), f.Timestamp);  p += 4;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p, 2), (ushort)f.Payload.Length); p += 2;
        f.Payload.CopyTo(b, p); p += f.Payload.Length;
        using var hm = new HMACSHA256(key.ToArray());
        var tag = hm.ComputeHash(b, 2, p - 2)[..16];
        tag.CopyTo(b, p); p += 16;
        b[p++] = 0x0D; b[p] = 0x0A;
        return b;
    }

    public static bool TryDecode(ReadOnlySpan<byte> d, ReadOnlySpan<byte> key,
        out EsdFrame? f, out ErrorCode e)
    {
        f = null; e = ErrorCode.Ok;
        if (d.Length < 38 || d[0] != 0xAA || d[1] != 0x55 || d[^2] != 0x0D || d[^1] != 0x0A)
            { e = ErrorCode.BadLength; return false; }
        int p = 2;
        byte v = d[p++]; if (v != 1) { e = ErrorCode.BadVersion; return false; }
        var fl = (FrameFlags)d[p++];
        uint id = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p, 4)); p += 4;
        var c  = (Command)BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p, 2)); p += 2;
        uint s = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p, 4)); p += 4;
        uint t = BinaryPrimitives.ReadUInt32BigEndian(d.Slice(p, 4)); p += 4;
        ushort n = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(p, 2)); p += 2;
        if (n > 256 || d.Length != 38 + n) { e = ErrorCode.BadLength; return false; }
        var payload = d.Slice(p, n).ToArray(); p += n;
        var tag = d.Slice(p, 16);
        using var hm = new HMACSHA256(key.ToArray());
        var exp = hm.ComputeHash(d.Slice(2, p - 2).ToArray())[..16];
        if (!CryptographicOperations.FixedTimeEquals(tag, exp)) { e = ErrorCode.BadMac; return false; }
        f = new EsdFrame(v, fl, id, c, s, t, payload, tag.ToArray());
        return true;
    }

    public static string ToHex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
    public static byte[] FromHex(string s) =>
        Convert.FromHexString(new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()));
}
