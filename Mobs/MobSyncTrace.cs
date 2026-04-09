using System;
using System.Collections.Generic;
using DeadCellsMultiplayerMod;
using Serilog;

namespace DeadCellsMultiplayerMod.Mobs.MobsSynchronization;

/// <summary>Opt-in verbose tracing for mob sync (env DCCM_MOB_SYNC_TRACE=1 or debug settings).</summary>
internal static class MobSyncTrace
{
    private static readonly bool EnvTraceEnabled = string.Equals(
        Environment.GetEnvironmentVariable("DCCM_MOB_SYNC_TRACE"),
        "1",
        StringComparison.Ordinal);

    public static bool Enabled => EnvTraceEnabled || MultiplayerSettingsStorage.DebugMobsSyncTrace;

    public static void LogSendStatesBatch(string role, IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (!Enabled || states == null || states.Count == 0)
            return;

        MinMaxSyncId(states, static s => s.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] → SEND states {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            states.Count,
            minId,
            maxId);
    }

    public static void LogSendDrawBatch(string role, IReadOnlyList<NetNode.MobDraw> draws)
    {
        if (!Enabled || draws == null || draws.Count == 0)
            return;

        MinMaxSyncId(draws, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] → SEND draws {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            draws.Count,
            minId,
            maxId);
    }

    public static void LogSendMovesBatch(string role, IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {
        if (!Enabled || moves == null || moves.Count == 0)
            return;

        MinMaxSyncId(moves, static m => m.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] в†’ SEND moves {Role} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            role,
            moves.Count,
            minId,
            maxId);
    }

    public static void LogRecvStates(string context, IReadOnlyList<NetNode.MobStateSnapshot> states)
    {
        if (!Enabled || states == null || states.Count == 0)
            return;

        MinMaxSyncId(states, static s => s.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV states {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            states.Count,
            minId,
            maxId);
    }

    public static void LogRecvAttacks(string context, IReadOnlyList<NetNode.MobAttack> attacks)
    {
        if (!Enabled || attacks == null || attacks.Count == 0)
            return;

        MinMaxSyncId(attacks, static a => a.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV attacks {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            attacks.Count,
            minId,
            maxId);
    }

    public static void LogRecvMoves(string context, IReadOnlyList<NetNode.MobMoveSnapshot> moves)
    {
        if (!Enabled || moves == null || moves.Count == 0)
            return;

        MinMaxSyncId(moves, static m => m.Index, out var minId, out var maxId);
        Log.Information(
            "[MobSync] в†ђ RECV moves {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            moves.Count,
            minId,
            maxId);
    }

    public static void LogRecvDraws(string context, IReadOnlyList<NetNode.MobDraw> draws)
    {
        if (!Enabled || draws == null || draws.Count == 0)
            return;

        MinMaxSyncId(draws, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV draws {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            draws.Count,
            minId,
            maxId);
    }

    public static void LogRecvHits(string context, IReadOnlyList<NetNode.MobHit> hits)
    {
        if (!Enabled || hits == null || hits.Count == 0)
            return;

        MinMaxSyncId(hits, static h => h.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV hits {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            hits.Count,
            minId,
            maxId);
    }

    public static void LogRecvDies(string context, IReadOnlyList<NetNode.MobDie> dies)
    {
        if (!Enabled || dies == null || dies.Count == 0)
            return;

        MinMaxSyncId(dies, static d => d.MobIndex, out var minId, out var maxId);
        Log.Information(
            "[MobSync] ← RECV dies {Context} count={Count} syncIdMin={SyncIdMin} syncIdMax={SyncIdMax}",
            context,
            dies.Count,
            minId,
            maxId);
    }

    public static void LogSendMobEvents(string role, IReadOnlyList<NetNode.MobEventUpdate> updates)
    {
        if (!Enabled || updates == null || updates.Count == 0)
            return;

        for (var i = 0; i < updates.Count; i++)
        {
            var u = updates[i];
            var events = u.Events;
            var summary = SummarizeEvents(events);
            Log.Information(
                "[MobSync] → SEND mobEvent {Role} syncId={SyncId} x={X} y={Y} dir={Dir} type={MobType} events={EventSummary}",
                role,
                u.Index,
                u.X,
                u.Y,
                u.Dir,
                u.Type ?? string.Empty,
                summary);
        }
    }

    public static void LogRegisterTracked(string role, int syncId, int localIndex, string mobType)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ REGISTER mob tracked {Role} syncId={SyncId} localIndex={LocalIndex} type={MobType}",
            role,
            syncId,
            localIndex,
            mobType ?? string.Empty);
    }

    public static void LogBindSyncId(string reason, int syncId, string mobType, double x, double y)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ BIND syncId reason={Reason} syncId={SyncId} type={MobType} x={X} y={Y}",
            reason,
            syncId,
            mobType ?? string.Empty,
            x,
            y);
    }

    public static void LogIncomingHitApply(
        int syncId,
        int hp,
        int userId,
        bool replaySpecial,
        bool forceDie)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ APPLY hit syncId={SyncId} hp={Hp} userId={UserId} replaySpecial={ReplaySpecial} forceDie={ForceDie}",
            syncId,
            hp,
            userId,
            replaySpecial,
            forceDie);
    }

    public static void LogClientAttackRoute(string route, int syncId, string skillId)
    {
        if (!Enabled)
            return;

        Log.Information(
            "[MobSync] ◎ CLIENT attack route={Route} syncId={SyncId} skillId={SkillId}",
            route,
            syncId,
            skillId ?? string.Empty);
    }

    private static void MinMaxSyncId<T>(IReadOnlyList<T> items, Func<T, int> getIndex, out int minId, out int maxId)
    {
        minId = int.MaxValue;
        maxId = int.MinValue;
        for (var i = 0; i < items.Count; i++)
        {
            var id = getIndex(items[i]);
            if (id < minId)
                minId = id;
            if (id > maxId)
                maxId = id;
        }

        if (minId == int.MaxValue)
        {
            minId = -1;
            maxId = -1;
        }
    }

    private static string SummarizeEvents(IReadOnlyList<string>? events)
    {
        if (events == null || events.Count == 0)
            return string.Empty;

        var parts = new List<string>(events.Count);
        for (var i = 0; i < events.Count; i++)
        {
            parts.Add(SummarizeOneEvent(events[i]));
        }

        return string.Join("; ", parts);
    }

    private static string SummarizeOneEvent(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "?";

        var pipe = raw.IndexOf('|', StringComparison.Ordinal);
        var head = pipe >= 0 ? raw[..pipe] : raw;
        if (string.Equals(head, "hit", StringComparison.Ordinal))
        {
            var lifePart = pipe >= 0 && pipe + 1 < raw.Length ? raw[(pipe + 1)..] : string.Empty;
            return $"hit life={lifePart}";
        }

        if (!string.Equals(head, "attack", StringComparison.Ordinal))
            return raw.Length <= 120 ? raw : raw[..117] + "...";

        var rest = pipe >= 0 && pipe + 1 < raw.Length ? raw[(pipe + 1)..] : string.Empty;
        var seg = rest.Split('|', 8);
        var skillEnc = seg.Length > 0 ? seg[0] : string.Empty;
        var skill = skillEnc;
        try
        {
            skill = Uri.UnescapeDataString(skillEnc);
        }
        catch
        {
        }

        return $"attack skill={skill}";
    }
}
