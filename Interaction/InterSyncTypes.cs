namespace DeadCellsMultiplayerMod.Interaction;

public readonly struct InterDoorEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly string Action;
    public readonly bool Broken;

    public InterDoorEvent(int userId, double x, double y, string action, bool broken)
    {
        UserId = userId;
        X = x;
        Y = y;
        Action = action ?? string.Empty;
        Broken = broken;
    }
}

public readonly struct InterElevatorEvent
{
    public readonly double X;
    public readonly double Y;

    public InterElevatorEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterPressurePlateEvent
{
    public readonly double X;
    public readonly double Y;

    public InterPressurePlateEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterTreasureChestEvent
{
    public readonly double X;
    public readonly double Y;

    public InterTreasureChestEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterVineLadderEvent
{
    public readonly double X;
    public readonly double Y;

    public InterVineLadderEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterTeleportEvent
{
    public readonly double X;
    public readonly double Y;

    public InterTeleportEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct BossHeroTeleportEvent
{
    public readonly int UserId;
    public readonly double X;
    public readonly double Y;
    public readonly int Dir;

    public BossHeroTeleportEvent(int userId, double x, double y, int dir)
    {
        UserId = userId;
        X = x;
        Y = y;
        Dir = dir;
    }
}

public readonly struct InterBreakableGroundEvent
{
    public readonly double X;
    public readonly double Y;

    public InterBreakableGroundEvent(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public readonly struct InterBossRuneUpdateCellsEvent
{
    public readonly double X;
    public readonly double Y;
    public readonly bool Add;

    public InterBossRuneUpdateCellsEvent(double x, double y, bool add)
    {
        X = x;
        Y = y;
        Add = add;
    }
}


public readonly struct InterGenericActivateEvent
{
    public readonly double X;
    public readonly double Y;
    public readonly string TypeName;

    public InterGenericActivateEvent(double x, double y, string typeName)
    {
        X = x;
        Y = y;
        TypeName = typeName ?? string.Empty;
    }
}

public readonly struct InterPortalEvent
{
    public readonly double X;
    public readonly double Y;
    public readonly string Action;

    public InterPortalEvent(double x, double y, string action)
    {
        X = x;
        Y = y;
        Action = action ?? string.Empty;
    }
}

[System.Flags]
public enum WorldObjectSyncFlags
{
    None = 0,
    Consumed = 1,
    Opened = 2,
    Broken = 4,
    Hidden = 8,
    Important = 16
}

public readonly struct WorldObjectState
{
    public readonly string LevelId;
    public readonly string TypeName;
    public readonly double X;
    public readonly double Y;
    public readonly int Flags;

    public WorldObjectState(string levelId, string typeName, double x, double y, int flags)
    {
        LevelId = levelId ?? string.Empty;
        TypeName = typeName ?? string.Empty;
        X = x;
        Y = y;
        Flags = flags;
    }
}
