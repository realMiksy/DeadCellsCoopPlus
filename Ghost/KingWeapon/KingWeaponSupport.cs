using System;
using System.Runtime.CompilerServices;
using dc.en;
using dc.hl.types;
using dc.tool;
using dc.tool.skill;
using HaxeProxy.Runtime;

namespace DeadCellsMultiplayerMod.Ghost;

internal static class KingWeaponSupport
{
    private static readonly ConditionalWeakTable<Weapon, KingSkin> WeaponToSource = new();
    private static readonly ConditionalWeakTable<InventItem, KingSkin> ItemToSource = new();
    private static readonly ConditionalWeakTable<OldSkill, SkillHooks> WrappedSkills = new();

    [ThreadStatic]
    private static int _contextDepth;
    [ThreadStatic]
    private static int _allowLocalHeroDamageDepth;
    [ThreadStatic]
    private static KingSkin? _currentContextSource;

    internal static bool IsInKingContext => _contextDepth > 0;
    internal static bool IsLocalHeroDamageAllowedInKingContext => _allowLocalHeroDamageDepth > 0;
    internal static bool TryGetCurrentContextSource(out KingSkin source)
    {
        source = _currentContextSource!;
        return source != null;
    }

    private sealed class SkillHooks
    {
        public HlAction? DynOnChargeStart;
        public HlAction<double>? DynOnCharging;
        public HlAction? DynOnChargeComplete;
        public HlAction<double>? DynOnExecute;
        public HlAction? DynOnAttackAnim;
        public HlAction? DynOnFxFrame;
        public HlAction<double>? DynOnInterrupt;
    }

    public static Weapon CreateWeapon(Hero owner, InventItem item, KingSkin source)
    {
        Weapon weapon;
        try
        {
            weapon = Weapon.Class.create(owner, item);
        }
        catch
        {
            weapon = new Weapon(owner, item);
        }

        if(weapon == null)
            weapon = new Weapon(owner, item);

        Bind(weapon, source);
        SyncSource(weapon);
        PatchSkills(weapon);
        return weapon;
    }

    public static void Bind(Weapon weapon, KingSkin source)
    {
        if(weapon == null || source == null)
            return;

        WeaponToSource.Remove(weapon);
        WeaponToSource.Add(weapon, source);

        var item = weapon.item;
        if(item != null)
        {
            ItemToSource.Remove(item);
            ItemToSource.Add(item, source);
        }
    }

    public static void Unbind(Weapon weapon)
    {
        if(weapon == null)
            return;

        var item = weapon.item;
        if(item != null)
            ItemToSource.Remove(item);
        WeaponToSource.Remove(weapon);
    }

    public static bool TryGetSource(Weapon weapon, out KingSkin source)
    {
        if(weapon == null)
        {
            source = null!;
            return false;
        }
        return WeaponToSource.TryGetValue(weapon, out source!);
    }

    public static bool TryGetSourceByItem(InventItem? item, out KingSkin source)
    {
        if(item == null)
        {
            source = null!;
            return false;
        }
        return ItemToSource.TryGetValue(item, out source!);
    }

    public static bool IsKingWeapon(Weapon weapon)
    {
        return weapon != null && WeaponToSource.TryGetValue(weapon, out _);
    }

    public static void WithKingContext(Weapon weapon, Action action)
    {
        if(action == null)
            return;

        if(_contextDepth > 0)
        {
            action();
            return;
        }

        if(!TryGetSource(weapon, out var src))
        {
            action();
            return;
        }

        WithKingContextCore(weapon?.owner, src, action);
    }

    public static void WithKingContext(Hero hero, KingSkin source, Action action)
    {
        if(action == null)
            return;

        if(_contextDepth > 0)
        {
            action();
            return;
        }

        WithKingContextCore(hero, source, action);
    }

    public static T WithKingContext<T>(Hero hero, KingSkin source, Func<T> func)
    {
        T result = default!;
        WithKingContext(hero, source, () => { result = func(); });
        return result;
    }

    public static void WithLocalHeroDamageAllowed(Action action)
    {
        if(action == null)
            return;

        _allowLocalHeroDamageDepth++;
        try
        {
            action();
        }
        finally
        {
            _allowLocalHeroDamageDepth--;
        }
    }

    private static void WithKingContextCore(Hero? hero, KingSkin? src, Action action)
    {
        if(hero == null || src == null)
        {
            action();
            return;
        }

        _contextDepth++;
        var previousSource = _currentContextSource;
        _currentContextSource = src;

        var savedSpr = hero.spr;
        var savedLevel = hero._level;
        var savedTeam = hero._team;
        var savedCx = hero.cx;
        var savedCy = hero.cy;
        var savedXr = hero.xr;
        var savedYr = hero.yr;
        var savedDir = hero.dir;
        var savedDx = hero.dx;
        var savedDy = hero.dy;
        try
        {
            if(src.spr != null) hero.spr = src.spr;
            if(src._level != null) hero._level = src._level;
            if(src._team != null) hero._team = src._team;
            hero.cx = src.cx;
            hero.cy = src.cy;
            hero.xr = src.xr;
            hero.yr = src.yr;
            hero.dir = src.dir;
            hero.dx = src.dx;
            hero.dy = src.dy;
            action();
        }
        finally
        {
            hero.spr = savedSpr;
            hero._level = savedLevel;
            hero._team = savedTeam;
            hero.cx = savedCx;
            hero.cy = savedCy;
            hero.xr = savedXr;
            hero.yr = savedYr;
            hero.dir = savedDir;
            hero.dx = savedDx;
            hero.dy = savedDy;
            _currentContextSource = previousSource;
            _contextDepth--;
        }
    }

    public static T WithKingContext<T>(Weapon weapon, Func<T> func)
    {
        T result = default!;
        WithKingContext(weapon, () => { result = func(); });
        return result;
    }

    public static void SyncSource(Weapon weapon)
    {
        if(weapon == null)
            return;

        if(!TryGetSource(weapon, out var source))
            return;

        var arr = weapon.areas;
        if(source == null || arr == null)
            return;

        for(int i = 0; i < arr.length; i++)
        {
            var a = arr.array[i] as Area;
            if(a != null)
                a.setRelativePos(source, a.x, a.y);
        }
    }

    public static void PatchSkills(Weapon weapon)
    {
        if(weapon == null)
            return;

        var arr = weapon.skills;
        if(arr == null)
            return;

        for(int i = 0; i < arr.length; i++)
        {
            var s = arr.array[i] as WeaponSkill;
            if(s == null)
                continue;

            WrapSkillCallbacks(weapon, s);

            s.lockControlsAfterUseS = 0.0;
            s.canMoveDuringCharge = true;
        }
    }

    public static void PatchCurrentSkill(Weapon weapon)
    {
        if(weapon == null)
            return;

        WeaponSkill s;
        try
        {
            s = weapon.get_curSkill();
        }
        catch
        {
            return;
        }

        if(s == null)
            return;

        WrapSkillCallbacks(weapon, s);

        s.lockControlsAfterUseS = 0.0;
        s.canMoveDuringCharge = true;
    }

    private static void WrapSkillCallbacks(Weapon weapon, OldSkill skill)
    {
        if(weapon == null || skill == null)
            return;

        if(WrappedSkills.TryGetValue(skill, out _))
            return;

        var hooks = new SkillHooks
        {
            DynOnChargeStart = skill.dynOnChargeStart,
            DynOnCharging = skill.dynOnCharging,
            DynOnChargeComplete = skill.dynOnChargeComplete,
            DynOnExecute = skill.dynOnExecute,
            DynOnAttackAnim = skill.dynOnAttackAnim,
            DynOnFxFrame = skill.dynOnFxFrame,
            DynOnInterrupt = skill.dynOnInterrupt
        };
        WrappedSkills.Add(skill, hooks);

        if(hooks.DynOnChargeStart != null)
            skill.dynOnChargeStart = () => WithKingContext(weapon, () => hooks.DynOnChargeStart?.Invoke());

        if(hooks.DynOnCharging != null)
            skill.dynOnCharging = r => WithKingContext(weapon, () => hooks.DynOnCharging?.Invoke(r));

        if(hooks.DynOnChargeComplete != null)
            skill.dynOnChargeComplete = () => WithKingContext(weapon, () => hooks.DynOnChargeComplete?.Invoke());

        if(hooks.DynOnExecute != null)
            skill.dynOnExecute = ratio => WithKingContext(weapon, () => hooks.DynOnExecute?.Invoke(ratio));

        if(hooks.DynOnAttackAnim != null)
            skill.dynOnAttackAnim = () => WithKingContext(weapon, () => hooks.DynOnAttackAnim?.Invoke());

        if(hooks.DynOnFxFrame != null)
            skill.dynOnFxFrame = () => WithKingContext(weapon, () => hooks.DynOnFxFrame?.Invoke());

        if(hooks.DynOnInterrupt != null)
            skill.dynOnInterrupt = r => WithKingContext(weapon, () => hooks.DynOnInterrupt?.Invoke(r));
    }
}
