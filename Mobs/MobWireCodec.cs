using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace DeadCellsMultiplayerMod;

/// <summary>Wire encoder for mob sync protocol lines (in-process only).</summary>
internal static class MobWireCodec
{
    private const char EntrySep = ';';
    private const char EventSep = '\u00A7';

    /// <summary>Hard cap on mob state entries per line (see MobsSync optimization plan).</summary>
    internal const int MaxMobStateSnapshotsPerWireLine = 96;

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
            var limit = moves.Count;
            if (limit > MaxMobStateSnapshotsPerWireLine)
                limit = MaxMobStateSnapshotsPerWireLine;

            for (int i = 0; i < limit; i++)
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
            var limit = charges.Count;
            if (limit > MaxMobStateSnapshotsPerWireLine)
                limit = MaxMobStateSnapshotsPerWireLine;

            for (int i = 0; i < limit; i++)
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
            var limit = updates.Count;
            if (limit > MaxMobStateSnapshotsPerWireLine)
                limit = MaxMobStateSnapshotsPerWireLine;

            for (int i = 0; i < limit; i++)
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
            var limit = draws.Count;
            if (limit > MaxMobStateSnapshotsPerWireLine)
                limit = MaxMobStateSnapshotsPerWireLine;

            for (int i = 0; i < limit; i++)
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

        var limit = states.Count;
        if (limit > MaxMobStateSnapshotsPerWireLine)
            limit = MaxMobStateSnapshotsPerWireLine;

        for (int i = 0; i < limit; i++)
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
