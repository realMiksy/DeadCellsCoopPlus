using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeadCellsMultiplayerMod;

/// <summary>Optional compact binary wire for MOBSTATE batches (enable DCCM_MOB_WIRE_BINARY=1).</summary>
internal static class MobWireBinary
{
    public const byte WireVersion = 1;

    public static bool UseBinaryWire =>
        string.Equals(Environment.GetEnvironmentVariable("DCCM_MOB_WIRE_BINARY"), "1", StringComparison.Ordinal);

    public static bool TryBuildMobStatesBinary(IReadOnlyList<NetNode.MobStateSnapshot> states, out byte[]? bytes)
    {
        bytes = null;
        if (states == null || states.Count == 0)
            return false;

        try
        {
            var n = states.Count;
            if (n > MobWireCodec.MaxMobStateSnapshotsPerWireLine)
                n = MobWireCodec.MaxMobStateSnapshotsPerWireLine;

            using var ms = new MemoryStream(64 + n * 96);
            using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
            bw.Write(WireVersion);
            bw.Write((ushort)n);
            for (int i = 0; i < n; i++)
            {
                var s = states[i];
                bw.Write(s.Index);
                bw.Write(s.X);
                bw.Write(s.Y);
                bw.Write(s.Dir);
                bw.Write(s.Life);
                bw.Write(s.MaxLife);
                WriteUtf8(bw, s.AnimPayload ?? string.Empty);
                WriteUtf8(bw, s.Type ?? string.Empty);
                WriteUtf8(bw, s.StatePayload ?? string.Empty);
            }

            bytes = ms.ToArray();
            return true;
        }
        catch
        {
            bytes = null;
            return false;
        }
    }

    private static void WriteUtf8(BinaryWriter bw, string s)
    {
        var buf = Encoding.UTF8.GetBytes(s);
        if (buf.Length > ushort.MaxValue)
            throw new InvalidOperationException("Mob wire UTF-8 segment too long.");
        bw.Write((ushort)buf.Length);
        bw.Write(buf);
    }

    public static bool TryParseMobStatesBase64(string base64Payload, List<NetNode.MobStateSnapshot> destination)
    {
        destination.Clear();
        if (string.IsNullOrWhiteSpace(base64Payload))
            return false;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64Payload.Trim());
        }
        catch
        {
            return false;
        }

        return TryParseMobStatesBinary(raw.AsSpan(), destination);
    }

    public static bool TryParseMobStatesBinary(ReadOnlySpan<byte> raw, List<NetNode.MobStateSnapshot> destination)
    {
        destination.Clear();
        if (raw.Length < 3)
            return false;

        var ver = raw[0];
        if (ver != WireVersion)
            return false;

        var count = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(1, 2));
        var offset = 3;
        for (int i = 0; i < count; i++)
        {
            if (offset + 32 > raw.Length)
                return false;

            var index = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(offset, 4));
            offset += 4;
            var x = BitConverter.ToDouble(raw.Slice(offset, 8));
            offset += 8;
            var y = BitConverter.ToDouble(raw.Slice(offset, 8));
            offset += 8;
            var dir = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(offset, 4));
            offset += 4;
            var life = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(offset, 4));
            offset += 4;
            var maxLife = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(offset, 4));
            offset += 4;

            if (!TryReadUtf8Segment(raw, ref offset, out var anim) ||
                !TryReadUtf8Segment(raw, ref offset, out var type) ||
                !TryReadUtf8Segment(raw, ref offset, out var state))
                return false;

            destination.Add(new NetNode.MobStateSnapshot(index, x, y, dir, life, maxLife, anim, type, state));
        }

        return destination.Count > 0;
    }

    private static bool TryReadUtf8Segment(ReadOnlySpan<byte> raw, ref int offset, out string text)
    {
        text = string.Empty;
        if (offset + 2 > raw.Length)
            return false;

        var len = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(offset, 2));
        offset += 2;
        if (offset + len > raw.Length)
            return false;

        text = len == 0 ? string.Empty : Encoding.UTF8.GetString(raw.Slice(offset, len));
        offset += len;
        return true;
    }
}
