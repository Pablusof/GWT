using System;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

// ─────────────────────────────────────────────────────────────────────────────
//  Bot combat AI — class-based tactics + perception model.
//
//  A bot's fighting behaviour is NOT hard-coded per vehicle. On spawn we read the
//  tank's real stats (class, HP, speed, view range, gun alpha, reload) and derive a
//  CombatProfile from them. All of the movement/engagement logic in RoomServer then
//  reads the profile instead of magic constants, so a heavy brawls up close, a TD
//  snipes and holds a stand-off, a light kites at the edge of its (superior) vision,
//  and a medium plays hit-and-run — emergently, from the data.
//
//  Class comes from TankEntry.Type (confirmed against the client table):
//     1 = Light, 2 = Medium, 3 = Heavy, 4 = Tank-Destroyer.
//  VehicleType: 1 = turretless (casemate), 2 = turreted.
// ─────────────────────────────────────────────────────────────────────────────

public enum TankClass { Light = 1, Medium = 2, Heavy = 3, TD = 4 }

// Tactical front corridor assigned at spawn (-1=Left flank, 0=Center, +1=Right flank).
public enum BotFront { Left = -1, Center = 0, Right = 1 }

// High-level tactical state for human-like peek/cover/brawl decisions.
public enum BotTacticalState { AdvanceFront, PeekOut, FireHold, ReloadCover, FlankManoeuvre, Retreat }

// How a bot picks WHICH spotted enemy to fight.
public enum TargetPriority { Nearest, Weakest, HighValue }

// Flavour of movement (used by RoomServer for wander/kite amount).
public enum MovementStyle { Steady, HitAndRun, Evasive, Static }

public readonly struct CombatProfile
{
    public readonly TankClass      Class;
    public readonly bool           Turreted;
    public readonly float          DetectRange;    // vision radius, world units
    public readonly float          PreferredRange; // ideal distance to open fire
    public readonly float          StandoffRange;  // never willingly get closer than this
    public readonly float          RetreatHpFrac;  // fall back below this HP fraction
    public readonly float          Aggression;     // 0..1, willingness to push forward
    public readonly float          SpeedScale;     // caution multiplier on move speed
    public readonly bool           UseHullDown;    // prefers ridges / peek positions
    public readonly TargetPriority Target;
    public readonly MovementStyle  Move;

    public CombatProfile(TankClass cls, bool turreted, float detect, float preferred,
        float standoff, float retreatHp, float aggression, float speedScale,
        bool hullDown, TargetPriority target, MovementStyle move)
    {
        Class = cls; Turreted = turreted; DetectRange = detect; PreferredRange = preferred;
        StandoffRange = standoff; RetreatHpFrac = retreatHp; Aggression = aggression;
        SpeedScale = speedScale; UseHullDown = hullDown; Target = target; Move = move;
    }

    // Safe fallback when a bot has no known tank entry (keeps old behaviour).
    public static CombatProfile Default =>
        new(TankClass.Medium, true, 260f, 140f, 40f, 0.25f, 0.65f, 0.9f,
            false, TargetPriority.Nearest, MovementStyle.Steady);
}

public static class BotCombatProfiles
{
    // Global knobs so the whole bot roster can be tuned without touching per-class
    // numbers. VisionScale multiplies every view range (raise if bots feel blind on a
    // big map, lower to make them shorter-sighted / easier).
    public const float VisionScale = 1.0f;
    public const float MinDetect   = 120f;   // floor so nobody is functionally blind

    public static CombatProfile Build(TankEntry? e, GameTablesService gt)
    {
        if (e == null) return CombatProfile.Default;

        var  cls      = ToClass(e.Type);
        bool turreted = e.VehicleType != 1;
        float view    = (e.ViewRange > 0 ? e.ViewRange : 250f) * VisionScale;
        if (view < MinDetect) view = MinDetect;
        float speed   = e.Speed > 0 ? e.Speed : 35f;   // km/h

        // Gun burst character: high alpha + slow reload ⇒ peek-and-hide sniper.
        float alpha = 0f, reload = 8f;
        var gun = gt.GetGun(e.MainGunId);
        if (gun != null)
        {
            var sh = gt.GetShell(gun.Ammo1Id);
            if (sh != null) alpha = sh.Damage;
        }
        try
        {
            var attrs = gt.BuildTankAttributes(e, e.MainGunId, 0, e.MaxHp);
            if (attrs != null && attrs.Length > 24 && attrs[24] > 0.1f) reload = attrs[24];
        }
        catch { /* keep default reload */ }
        bool burstGun = alpha >= 500f && reload >= 10f;

        // ── class baselines ────────────────────────────────────────────────────
        float detect, preferred, standoff, retreatHp, aggr, spd;
        bool  hullDown;
        TargetPriority tgt;
        MovementStyle  move;
        switch (cls)
        {
            case TankClass.Light:   // scout: best eyes, stays at arm's length, kites
                detect = view * 1.15f; preferred = 165f; standoff = 85f;
                retreatHp = 0.45f; aggr = 0.35f; spd = 1.00f; hullDown = false;
                tgt = TargetPriority.Weakest; move = MovementStyle.Evasive; break;

            case TankClass.Heavy:   // brawler: pushes in, tanks damage, holds ground
                detect = view * 0.95f; preferred = 90f; standoff = 0f;
                retreatHp = 0.15f; aggr = 1.00f; spd = 0.85f; hullDown = true;
                tgt = TargetPriority.Nearest; move = MovementStyle.Steady; break;

            case TankClass.TD:      // sniper: second line, long range, big stand-off
                detect = view * 1.00f; preferred = 200f; standoff = 130f;
                retreatHp = 0.40f; aggr = 0.25f; spd = 0.70f; hullDown = true;
                tgt = TargetPriority.HighValue; move = MovementStyle.Static; break;

            default:                // Medium: flexible mid-range hit-and-run
                detect = view * 1.00f; preferred = 125f; standoff = 45f;
                retreatHp = 0.28f; aggr = 0.65f; spd = 0.95f; hullDown = false;
                tgt = TargetPriority.Weakest; move = MovementStyle.HitAndRun; break;
        }

        // ── stat modulation (so two same-class tanks still differ) ──────────────
        // Speed: quicker tanks push harder and sit a touch closer; sluggish ones hang
        // back. Centered on ~45 km/h.
        float sf = Clamp((speed - 45f) / 45f, -0.6f, 0.7f);
        aggr     = Clamp01(aggr + sf * 0.12f);
        standoff = MathF.Max(0f, standoff - sf * 18f);

        // Peek-a-boom guns want more distance and a bigger stand-off buffer.
        if (burstGun) { preferred += 25f; standoff += 25f; }

        return new CombatProfile(cls, turreted, detect, preferred, standoff,
            retreatHp, aggr, spd, hullDown, tgt, move);
    }

    private static TankClass ToClass(int type) => type switch
    {
        1 => TankClass.Light,
        3 => TankClass.Heavy,
        4 => TankClass.TD,
        _ => TankClass.Medium,
    };

    /// <summary>
    /// Assigns a tactical front corridor (-1 Left, 0 Center, +1 Right) by tank class
    /// and spawn slot so the team fans out naturally across flanks instead of blobbing.
    /// </summary>
    public static BotFront AssignFront(TankClass cls, int teamIndex)
    {
        int idx = Math.Abs(teamIndex);
        return cls switch
        {
            TankClass.Heavy => (idx % 4) switch
            {
                0 or 3 => BotFront.Center,
                1      => BotFront.Left,
                _      => BotFront.Right,
            },
            TankClass.TD => (idx % 3) switch
            {
                0 => BotFront.Left,
                1 => BotFront.Right,
                _ => BotFront.Center,
            },
            _ => (idx % 5) switch // Light & Medium: predominantly flanks with occasional center support
            {
                0 or 2 => BotFront.Left,
                1 or 3 => BotFront.Right,
                _      => BotFront.Center,
            },
        };
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
    private static float Clamp01(float v) => Clamp(v, 0f, 1f);
}

// ─────────────────────────────────────────────────────────────────────────────
//  Phase 5: per-bot SKILL (difficulty). One float 0..1 rolled at spawn drives how
//  well a bot shoots: hit chance, reaction time, how well it leads a moving target,
//  how often it finds a weak spot, and how far it spots. Raise/lower GlobalBias to
//  shift the whole roster from "recruits" to "aces".
// ─────────────────────────────────────────────────────────────────────────────
public static class BotSkill
{
    // Added to every rolled skill. -0.35 = much easier bots, +0.35 = much harder.
    public const float GlobalBias = 0.0f;

    public static float Roll(Random rng)
    {
        // Average of two uniforms → bell-ish spread centered ~0.5 (few recruits, few aces).
        float s = (float)((rng.NextDouble() + rng.NextDouble()) * 0.5);
        float v = s + GlobalBias;
        return v < 0.05f ? 0.05f : (v > 1f ? 1f : v);
    }

    public static float HitChance(float skill)    => 0.45f + skill * 0.45f;  // 45% .. 90%
    public static float ReactionSec(float skill)  => 1.40f - skill * 1.05f;  // 1.40s .. 0.35s
    public static float LeadQuality(float skill)  => 0.35f + skill * 0.65f;  // 0.35 .. 1.0 of true lead
    public static float ViewMult(float skill)     => 0.85f + skill * 0.30f;  // 0.85x .. 1.15x view range
    public static float WeakSpotBias(float skill) => skill;                   // 0 (random zones) .. 1 (aims weak)
}
