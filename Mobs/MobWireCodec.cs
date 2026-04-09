using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace DeadCellsMultiplayerMod;

/// <summary>Wire encoder for mob sync protocol lines (in-process only).</summary>
internal static class MobWireCodec
{
    private const char EntrySep = ';';
    private const char EventSep = '\u00A7';

    private static readonly ThreadLocal<StringBuilder> MobLineBuilder = new(() => new StringBuilder(4096));

    public static string BuildMobStatesLine(IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        var sb = MobLineBuilder.Value!;
        sb.Clear();
        sb.Append("MOBSTATE|");
        AppendJoinedStates(sb, states);
        sb.Append('\n');
        return sb.ToString();
    }

    public static string BuildMobMovesLine(IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {
        var sb = MobLineBuilder.Value!;
        sb.Clear();
        sb.Append("MOBMOVE|");
        if (moves != null)
        {
            for (int i = 0; i < moves.Count; i++)
            {
                if (i > 0)
                    sb.Append(EntrySep);

                var m = moves[i];
                sb.Append(m.Index.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(m.X.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(m.Y.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(m.Dir.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(m.AnimPayload ?? string.Empty);
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    public static string BuildMobChargesLine(IReadOnlyList<NetNode.MobChargeSnapshot> charges)
    {
        var sb = MobLineBuilder.Value!;
        sb.Clear();
        sb.Append("MOBCHARGE|");
        if (charges != null)
        {
            for (int i = 0; i < charges.Count; i++)
            {
                if (i > 0)
                    sb.Append(EntrySep);

                var c = charges[i];
                sb.Append(c.Index.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(c.SkillId ?? string.Empty);
                sb.Append(',');
                sb.Append(c.Ratio.ToString(CultureInfo.InvariantCulture));
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    public static string BuildMobAttackLine(NetNode.MobAttack attack)
    {
        string encodedSkill;
        try
        {
            encodedSkill = System.Uri.EscapeDataString(attack.SkillId ?? string.Empty);
        }
        catch
        {
            encodedSkill = attack.SkillId ?? string.Empty;
        }

        var hasData = attack.Data.HasValue ? "1" : "0";
        var data = attack.Data.HasValue
            ? attack.Data.Value.ToString(CultureInfo.InvariantCulture)
            : "0";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBATTACK|{attack.Index},{encodedSkill},{(attack.RequiresTargetInArea ? 1 : 0)},{hasData},{data},{attack.X},{attack.Y},{attack.TargetUserId},{attack.Dir}\n");
    }

    public static string BuildMobEventsLine(IReadOnlyList<NetNode.MobEventUpdate> updates)
    {
        var sb = MobLineBuilder.Value!;
        sb.Clear();
        sb.Append("MOBEVENT|");
        if (updates != null)
        {
            for (int i = 0; i < updates.Count; i++)
            {
                if (i > 0)
                    sb.Append(EntrySep);

                var u = updates[i];
                sb.Append(u.Index.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(u.X.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(u.Y.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(u.Dir.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(u.Type))
                {
                    sb.Append(',');
                    sb.Append(u.Type);
                }

                if (u.Events != null && u.Events.Count > 0)
                {
                    for (int j = 0; j < u.Events.Count; j++)
                    {
                        sb.Append(EventSep);
                        sb.Append(u.Events[j] ?? string.Empty);
                    }
                }
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    public static string BuildMobDrawLine(int userId, int mobIndex, bool isOutOfGame, bool isOnScreen)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDRAW|{userId}|{mobIndex}|{(isOutOfGame ? 1 : 0)}|{(isOnScreen ? 1 : 0)}\n");
    }

    public static string BuildMobDrawLine(IReadOnlyList<NetNode.MobDraw> draws)
    {
        var sb = MobLineBuilder.Value!;
        sb.Clear();
        sb.Append("MOBDRAW|");
        if (draws != null)
        {
            for (int i = 0; i < draws.Count; i++)
            {
                if (i > 0)
                    sb.Append(EntrySep);

                var d = draws[i];
                sb.Append(d.UserId.ToString(CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(d.MobIndex.ToString(CultureInfo.InvariantCulture));
                sb.Append('|');
                sb.Append(d.IsOutOfGame ? '1' : '0');
                sb.Append('|');
                sb.Append(d.IsOnScreen ? '1' : '0');
            }
        }
        sb.Append('\n');
        return sb.ToString();
    }

    public static string BuildMobDieLine(NetNode.MobDie die)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"MOBDIE|{die.UserId}|{die.MobIndex}|{die.X}|{die.Y}\n");
    }

    private static void AppendJoinedStates(StringBuilder sb, IReadOnlyList<NetNode.MobStateSnapshot>? states)
    {
        if (states == null)
            return;

        for (int i = 0; i < states.Count; i++)
        {
            if (i > 0)
                sb.Append(EntrySep);

            var s = states[i];
            sb.Append(s.Index.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.X.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.Y.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.Dir.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.Life.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.MaxLife.ToString(CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(s.AnimPayload ?? string.Empty);
            sb.Append(',');
            sb.Append(s.Type ?? string.Empty);
            sb.Append(',');
            sb.Append(s.StatePayload ?? string.Empty);
        }
    }
}

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
            if (n > ushort.MaxValue)
                n = ushort.MaxValue;

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
