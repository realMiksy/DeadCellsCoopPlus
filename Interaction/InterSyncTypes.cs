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
