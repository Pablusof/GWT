using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using GwtEmulator.Protocol;
using GwtEmulator.Server.Database;
using GwtEmulator.Server.Database.Models;
using GwtEmulator.Server.Database.Repositories;
using GwtEmulator.Server.GameTables;

namespace GwtEmulator.Server;

// ──────────────────────────── enums ─────────────────────────────────────────

/// <summary>Maps to EBattleMode in the decompiled client (TK/EBattleMode.cs).</summary>
public enum BattleGameMode : byte
{
    BaseCapture = 0,   // WarType 0 – destroy all / capture flag, no respawn
    Respawn     = 1,   // WarType 1 – 1 750 pts to win, 15 min, respawn (Relive)
    Defense     = 4,   // WarType 4 – PVE wave defense, 12 waves, ≤5 pass-through, respawn
    Commander   = 2,   // WarType 2 – Generals: one general per side (x3 HP), kill the enemy general to win
    Simplified  = 6,   // WarType 6 – every hit deals damage, destroy all / flag, no respawn
}

/// <summary>Role assigned to each bot at spawn; drives its tactical behaviour.</summary>
public enum BotRole { Aggressive, Flanker, Defensive }

// v76: РЕАЛЬНЫЙ enum типов снарядов из клиента (decfilesgame Assembly-CSharp.cs,
// enum ShellType). Значения 1-based, а не 0-based — проверено по названиям снарядов
// в client_game_tables.json (Type=2 → «Sprg»/«UOF» = ОФ, Type=4 → явно «HEAT» в имени
// и т.д.). Раньше в ResolveHit/HandleShellHit использовалась ВЫДУМАННАЯ 0-based схема
// (0=AP,1=APCR,2=HEAT,3=HE,4=HEF), из-за чего фугасные снаряды (type==2, например у
// Штурмтигра) никогда не распознавались как HE и урон по ним не проходил вовсе.
public enum ShellKind { Ap = 1, He = 2, ApCr = 3, Heat = 4, Rocket = 5, Laser = 6 }

// ─────────────────────────── RoomServer ────────────────────────────────────

public class RoomServer : GameServer
{
    private readonly DatabaseService _db;
    private readonly TankRepository _tankRepo;
    private readonly CrewRepository? _crewRepo;
    private readonly TankEquipmentRepository? _equipRepo;
    private readonly BattleStatsRepository _battleStatsRepo;
    private readonly ModeStatsRepository? _modeStatsRepo;
    private readonly GameTablesService _gameTables;
    private readonly AccountRepository? _accountRepo;
    private readonly ConcurrentDictionary<long, BattleRoom>  _rooms   = new();
    private readonly ConcurrentDictionary<long, RoomPlayer>  _players = new();
    private long _waveBotSeq = -1000; // unique GUIDs for freshly-spawned wave bots
    private int  _attackerSpawnIdx = -1; // round-robin index over a map's AttackerSpawns
    private readonly Random _random = new();

    // ── per-map configuration ─────────────────────────────────────────────────
    //
    // Each MapConfig stores:
    //  • Terrain bounds (XMin/XMax, ZMin/ZMax)
    //  • Team spawn zones: center X±halfX, Z spread
    //  • Ground Y override (maps have different terrain heights; 0 = auto-detect from player)
    //  • Obstacle boxes for bot collision avoidance (approximate building/wall areas)
    //
    // Spawn XCenter/ZCenter below were re-derived from the minimap's own
    // red/green "A" birth-point markers (screenshots dated 2026-06-30), not
    // guessed: each minimap is a 10x10 A–J / 0–9 grid over the map's full
    // XMin..XMax / ZMin..ZMax bounds, so a marker's grid cell converts to
    // world units as worldX = XMin + (XMax-XMin)*(colIndex+0.5)/10 and
    // worldZ = ZMax - (ZMax-ZMin)*(rowLabel+0.5)/10 (column A=0..J=9; row
    // label 0 is the TOP of the minimap, i.e. the map's max Z, since the
    // client's own MiniMapViewNgui draws row 0 at maximum world Z). The
    // client's real per-map width/length table isn't available to the
    // server (it lives in the asset bundles, not client_game_tables.json),
    // so treat these as "now matches the marker's grid cell", not
    // millimetre-exact — XMin/XMax/ZMin/ZMax are still the same 0–800
    // (50–750 for Caucasus) assumption used everywhere else in this file.
    //
    // Each map's real spawn layout turned out to be different, confirmed by
    // re-checking with the colours swapped (the player landed on the other
    // team in a second match and the SAME two physical markers showed up
    // with red/green flipped):
    //   1 = Москва     — spawns are in diagonally opposite corners
    //   2 = Берлин     — spawns share the SAME west edge, split north/south
    //   3 = Закавказье — spawns split east/west at a similar height (this
    //                    one barely moved from the previous guess)
    //   4 = Сталинград — PVE/Defense map, not re-checked this pass (no
    //                    Defense-mode screenshots this round)
    //   9 = Кан        — spawns share ONE column, split north/south, same
    //                    as Berlin's topology despite being a different map
    //
    // A second round of calibration logs (after the GetSpawnZ/Y team-blind
    // bug was fixed, so each team's OWN coordinates were finally actually
    // reachable) showed a pattern the minimap-grid math alone couldn't see:
    // a corner that's close to TWO bounding-box edges at once tends to fall
    // outside the real playable terrain, even though a point close to only
    // ONE edge is usually fine. Moscow's Team0 (X=40, Z=760 — 40 units from
    // both the X=0 and Z=800 edges) sat frozen at the exact same coordinate
    // across three separate move attempts — never moved at all — while its
    // Team1 (X=730, Z=40 — only one dimension close to an edge) drove
    // around normally with a sane Y. Берлин's two spawns have the SAME
    // double-edge shape as the broken Moscow corner (and never sent a
    // single movement packet in two full matches), so they get the same
    // treatment. Spawn radius tightened to ~5m per spec (was an arbitrary
    // 15m guess) — world units appear to already be metres, given
    // CurrentSpeedMps is applied directly as world-units/second elsewhere
    // in this file.
    private readonly record struct SpawnZone(float XCenter, float XHalfSpread,
                                              float ZCenter, float ZHalfSpread, float Y);
    internal readonly record struct ObstacleBox(float X1, float Z1, float X2, float Z2,
                                                float YBot = -1000f, float YTop = 1000f);

    private static readonly (float Z, float XLeft, float XRight)[] MidBridgeSlicesMap9 =
    {
        (400f, 440f, 494f),
        (438f, 450f, 484f),
        (470f, 458f, 486f),
        (502f, 464f, 492f),
        (542f, 452f, 484f),
        (574f, 446f, 474f),
        (610f, 440f, 468f),
    };

    private static bool TrySampleMidBridgeSliceMap9(float z, out float xLeft, out float xRight)
    {
        var pts = MidBridgeSlicesMap9;
        if (z < pts[0].Z || z > pts[^1].Z) { xLeft = xRight = 0f; return false; }
        for (int i = 0; i < pts.Length - 1; i++)
        {
            if (z <= pts[i + 1].Z)
            {
                float denom = MathF.Max(0.01f, pts[i + 1].Z - pts[i].Z);
                float t = (z - pts[i].Z) / denom;
                xLeft  = pts[i].XLeft  + (pts[i + 1].XLeft  - pts[i].XLeft)  * t;
                xRight = pts[i].XRight + (pts[i + 1].XRight - pts[i].XRight) * t;
                return true;
            }
        }
        xLeft = pts[^1].XLeft; xRight = pts[^1].XRight;
        return true;
    }

    private static bool TrySampleBridgeDeck(byte mapId, float x, float z, out float deckY)
    {
        switch (mapId)
        {
            case 9: // Caen bridges spanning hm=14 canal
                if (x >= 286f && x <= 336f && z >= 402f && z <= 600f)
                {
                    deckY = 30.3f;
                    return true;
                }
                if (TrySampleMidBridgeSliceMap9(z, out float xl, out float xr) && x >= xl && x <= xr)
                {
                    float u = (z - 505f) / 105f;
                    deckY = 30.0f + 1.4f * MathF.Max(0f, 1f - u * u);
                    return true;
                }
                break;

            case 2: // Caucasia gorge bridge
                if (x >= 279f && x <= 295f && z >= 410f && z <= 534f)
                {
                    deckY = z <= 520f ? 26.4f : 26.4f + ((z - 520f) / 14f) * 4.5f;
                    return true;
                }
                break;

            case 74: // SmallLion canal bridges
                if (((x >= 369f && x <= 395f) || (x >= 503f && x <= 531f)) && z >= 580f && z <= 618f)
                {
                    float t = (z - 580f) / 38f;
                    deckY = 69.5f - 3.0f * t;
                    return true;
                }
                break;
        }
        deckY = 0f;
        return false;
    }

    private static void PostProcessBridgeObstacles(byte mapId, List<ObstacleBox> list)
    {
        if (mapId == 9)
        {
            // Trim overhead truss crossbars that span across WestBridge roadway (287..335)
            // while keeping both side truss walls (<=287 and >=335) solid.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var b = list[i];
                if (b.X1 >= 280f && b.X2 <= 342f && b.Z1 >= 408f && b.Z2 <= 596f && (b.X2 - b.X1) > 12f)
                {
                    list.RemoveAt(i);
                    if (b.X1 < 287f)
                        list.Add(new ObstacleBox(b.X1, b.Z1, 287f, b.Z2, b.YBot, b.YTop));
                    if (b.X2 > 335f)
                        list.Add(new ObstacleBox(335f, b.Z1, b.X2, b.Z2, b.YBot, b.YTop));
                }
            }
            // Seal 2m rasterization gaps along MidBridge's curved stone parapets so shots and
            // tanks cannot slip sideways through the bridge walls.
            for (float z = 400f; z < 610f; z += 4f)
            {
                if (TrySampleMidBridgeSliceMap9(z + 2f, out float xl, out float xr))
                {
                    list.Add(new ObstacleBox(xl - 3.5f, z, xl, z + 4f, 14f, 40f));
                    list.Add(new ObstacleBox(xr, z, xr + 3.5f, z + 4f, 14f, 40f));
                }
            }
        }
        else if (mapId == 2)
        {
            // Remove flat roadway deck/pier boxes spanning across GorgeBridge (279..295) and
            // seal bridge side walls along the gorge edge.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var b = list[i];
                if (b.X1 >= 274f && b.X2 <= 298f && b.Z1 >= 412f && b.Z2 <= 532f && (b.X2 - b.X1) > 6f && b.YTop <= 31.5f)
                    list.RemoveAt(i);
            }
            list.Add(new ObstacleBox(276.5f, 414f, 279f, 528f, 0f, 32f));
            list.Add(new ObstacleBox(295f, 414f, 297.5f, 528f, 0f, 32f));
        }
        else if (mapId == 74)
        {
            // Keep SmallLion canal bridges traversable on top of piers while sealing bridge sides.
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var b = list[i];
                if (b.Z1 >= 582f && b.Z2 <= 616f
                    && ((b.X1 >= 366f && b.X2 <= 398f) || (b.X1 >= 500f && b.X2 <= 534f))
                    && (b.X2 - b.X1) > 6f && b.YTop <= 71.5f)
                    list.RemoveAt(i);
            }
            list.Add(new ObstacleBox(366.5f, 584f, 369f, 614f, 54f, 72f));
            list.Add(new ObstacleBox(395f, 584f, 397.5f, 614f, 54f, 72f));
            list.Add(new ObstacleBox(500.5f, 584f, 503f, 614f, 54f, 72f));
            list.Add(new ObstacleBox(531f, 584f, 533.5f, 614f, 54f, 72f));
        }
    }

    private record MapConfig(
        byte MapId, string Name,
        float XMin, float XMax, float ZMin, float ZMax,
        SpawnZone Team0, SpawnZone Team1,
        ObstacleBox[] Obstacles,
        (float X, float Z)[]? BotSpawns = null,
        (float X, float Z)[]? AttackerSpawns = null);

    // Static map table — one entry per map
    private static readonly Dictionary<byte, MapConfig> Maps = new()
    {
        // ── Moscow ────────────────────────────────────────────────────────────
        // Both corners fine-tuned per direct feedback this round (small,
        // few-metre nudges — the big 70-unit corner pulls were already
        // validated as the right ballpark, this is just precision on top):
        // Team0 moved another ~7m further from the corner (was still
        // reading as spawning against a wall), Team1 moved 5m toward map
        // centre.
        [1] = new(1, "Москва",
            XMin: 0, XMax: 800, ZMin: 0, ZMax: 800,
            // Group 0 = A_base (SE ~655,149), Group 1 = B_base (NW ~164,652)
            Team0: new(XCenter: 642f, XHalfSpread: 5f, ZCenter: 140f, ZHalfSpread: 5f, Y: 6f),
            Team1: new(XCenter: 160f, XHalfSpread: 5f, ZCenter: 649f, ZHalfSpread: 5f, Y: 6f),
            // Obstacle box removed: it was one huge rectangle (180,80)-(620,720)
            // covering most of the 800x800 map, so both spawns sat right on its
            // edge and bots hit an invisible wall the moment they moved — while
            // real buildings elsewhere had no box, so bots drove through those.
            // Building collision needs real per-building boxes (from the map
            // bundle), not this catch-all; until then, no fake wall.
            Obstacles: Array.Empty<ObstacleBox>()),

        // ── Berlin ────────────────────────────────────────────────────────────
        // Both spawns were double-edge corners (X=40 AND Z=760/Z=40) — the
        // same shape that froze Moscow's Team0 off-map, and consistent with
        // zero movement packets ever arriving in two full Berlin matches
        // this round. Pulled in by the same ~70-unit margin, keeping the
        // shared-column / north-south topology the minimap showed.
        // X/Z confirmed correct this round ("точка спавна правильная").
        // Y corrected from a blind defensive 150 to real numbers: this
        // round's log shows the corrected corners actually settle at
        // Y≈33-36 (Team0, confirmed across a long drive) and Y≈61 (Team1,
        // two readings) once gravity pulls the tank down — but several
        // OTHER Berlin matches this round sent zero movement packets at
        // all (the under-the-map symptom), which a ~90-117 unit fall from
        // Y=150 is a plausible cause of (falling fast enough in one engine
        // tick to tunnel through thin floor geometry instead of landing on
        // it is a known failure mode). Spawning much closer to the real
        // settled height shrinks that fall to ~10-20 units, which should
        // be safe either way.
        // ── network map id 2 ──────────────────────────────────────────────────
        // Spawns set from the player's OWN driven log (PlayerPos map=2):
        //   Team0 spawned at old (110,690) and drove to (635,789) Y=35
        //   Team1 spawned at old (110,110) and drove to (645,170) Y=35
        // Both teams sit on the X~640 side, split north/south by Z (789 vs 170).
        // NOTE ON NAMING: this entry was labelled "Берлин", but the game loads
        // the map the player calls ЗАКАВКАЗЬЕ as network id 2 (that's what the
        // server.log recorded while driving here). Editing the map=3 entry did
        // nothing because this map isn't map 3 — THIS (id 2) is the one that
        // controls its spawns. Label kept as a comment to avoid guessing wrong.
        [2] = new(2, "Закавказье (network id 2)",
            // terrain New Terrain_6 is 1000×1000 (verified from player Y); bounds
            // widened from 800 so bots use the full map and reach the edge structures.
            XMin: 0, XMax: 1000, ZMin: 0, ZMax: 1000,
            // Group 0 = A_base (south ~629,171), Group 1 = B_base (north ~626,799)
            Team0: new(XCenter: 645f, XHalfSpread: 5f, ZCenter: 170f, ZHalfSpread: 5f, Y: 35f),
            Team1: new(XCenter: 635f, XHalfSpread: 5f, ZCenter: 789f, ZHalfSpread: 5f, Y: 35f),
            // Building footprints extracted from MapData_22 (the map on New Terrain_6)
            // combined with the client's collision meshes: solid structures — houses
            // (fang), tower (ta), bunker (diaobao) and wall segments (qiang, id 8011).
            // Trees/shrubs are decor with no collision mesh and are correctly omitted.
            // Stops bots driving and shooting through the Caucasus buildings and walls.
            Obstacles: new ObstacleBox[]
            {
                new(402.5f, 719.3f, 420.7f, 736.2f),
                new(428.5f, 801.5f, 445.0f, 819.7f),
                new(428.9f, 412.9f, 435.0f, 415.9f),
                new(429.0f, 414.7f, 431.9f, 420.8f),
                new(430.5f, 419.7f, 433.4f, 425.8f),
                new(431.9f, 424.6f, 434.8f, 430.7f),
                new(432.6f, 408.8f, 448.7f, 422.8f),
                new(433.7f, 427.6f, 439.7f, 430.5f),
                new(438.6f, 426.2f, 444.7f, 429.1f),
                new(445.5f, 407.5f, 451.6f, 410.5f),
                new(450.0f, 407.9f, 453.0f, 414.0f),
                new(465.7f, 753.4f, 468.7f, 759.4f),
                new(465.8f, 749.3f, 471.6f, 753.3f),
                new(466.8f, 524.4f, 470.5f, 530.3f),
                new(467.1f, 522.3f, 473.0f, 525.9f),
                new(468.1f, 731.5f, 484.5f, 752.8f),
                new(469.0f, 525.6f, 484.3f, 542.3f),
                new(470.7f, 872.1f, 473.2f, 878.2f),
                new(473.5f, 862.0f, 476.3f, 868.1f),
                new(474.2f, 867.1f, 490.2f, 880.8f),
                new(474.8f, 539.9f, 478.6f, 545.8f),
                new(477.3f, 541.8f, 483.2f, 545.5f),
                new(482.0f, 539.6f, 487.8f, 543.3f),
                new(483.5f, 711.9f, 485.5f, 718.1f),
                new(486.3f, 528.1f, 490.0f, 534.0f),
                new(486.6f, 537.4f, 492.5f, 541.0f),
                new(487.7f, 392.5f, 511.5f, 413.3f),
                new(488.5f, 532.7f, 492.2f, 538.6f),
                new(490.1f, 875.0f, 492.9f, 881.2f),
                new(491.4f, 870.1f, 494.3f, 876.2f),
                new(524.9f, 776.3f, 536.7f, 791.0f),
                new(583.9f, 440.2f, 599.3f, 453.2f),
                new(695.5f, 269.4f, 701.3f, 275.0f),
                new(771.8f, 162.8f, 776.4f, 168.1f),
                new(804.9f, 254.8f, 818.8f, 261.4f),
                new(890.1f, 254.9f, 894.2f, 260.6f),
            }),

        // ── Caucasus / Закавказье ─────────────────────────────────────────────
        // Team1 corrected from a direct screenshot this round: the player
        // drove away from the marker-derived (715,365) and parked next to
        // a small structure, which measured out to ≈(582,289) by the same
        // pixel-to-world conversion used everywhere else in this table —
        // pulled back ~130 units in X and ~75 in Z. Rounded to (580,290).
        // This is a bigger pull than the edge-corner fixes on the other
        // maps, but the same underlying pattern: the literal marker
        // position isn't always the best spawn point, even when (like
        // here) the ground right at the marker tested as valid terrain.
        // Team0 (85,365) wasn't re-checked this round — still only backed
        // by the earlier real Y=0 log confirmation, not a fresh screenshot.
        // ── map 3 = Берлин ──────────────────────────────────────────────────
        // Раньше здесь был дубликат Закавказья (настоящее Закавказье — map 2).
        // Клиент шлёт mapId=3 для Берлина; при чужом конфиге (Y=0) танки спавнились
        // под картой. Террейн Берлина ~30, спавн Y=33. Коллизия из obstacles/map_3.txt.
        // Спавны — ОЦЕНКА (нет driven-данных), уточнить заездом ([RS] PlayerPos map=3).
        // map 3 = Берлин. Driven-калибровка: X-сплит, Team0 восток (869,453),
        // Team1 запад (146,435), Y=30 (surface). Границы по seen X[144,870] Z[429,789].
        [3] = new(3, "Берлин",
            XMin: 60, XMax: 960, ZMin: 60, ZMax: 910,
            // Group 0 = A_base (west ~155,463), Group 1 = B_base (east ~859,488)
            Team0: new(XCenter: 146f, XHalfSpread: 8f, ZCenter: 435f, ZHalfSpread: 8f, Y: 33f),
            Team1: new(XCenter: 869f, XHalfSpread: 8f, ZCenter: 453f, ZHalfSpread: 8f, Y: 33f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // ── Stalingrad ────────────────────────────────────────────────────────
        // Not re-tested this round (no Defense-mode data); spawn radius
        // tightened to match the new spec, coordinates otherwise untouched.
        [4] = new(4, "Сталинград",
            XMin: 0, XMax: 800, ZMin: 0, ZMax: 800,
            Team0: new(XCenter: 70f,  XHalfSpread: 5f, ZCenter: 400f, ZHalfSpread: 5f, Y: 5f),
            Team1: new(XCenter: 730f, XHalfSpread: 5f, ZCenter: 400f, ZHalfSpread: 5f, Y: 5f),
            Obstacles: Array.Empty<ObstacleBox>()), // Defense mode: attackers march forward, no obstacles needed

        // ── Сталинград (MapId=10 per EnMapName.Stalingrad=10) ───────────────
        // Primary Defense/PVE map
        // Terrain-verified from Stalingrad.asset (Unity heightmap, 600x600, maxH=300).
        // Old spawn (70,400) sat in the low river channel (real surface Y=10) and
        // Team1 X=730 was OFF the 600-wide map entirely — that's the Image-1 float.
        // These two spots are on the flat main ground (surface exactly Y=30, zero
        // spread across a 25u radius), north/south, ~360u apart. Spawn Y = 33
        // (surface 30 + ~3 tank clearance) so the client's ±5 ground-snap catches it.
        // Bounds tightened to the real terrain size (600) so bots can't wander into
        // the void past the map edge.
        [10] = new(10, "Город в пустыне",
            XMin: 150, XMax: 485, ZMin: 120, ZMax: 775,
            Team0: new(XCenter: 438f, XHalfSpread: 8f, ZCenter: 154f, ZHalfSpread: 8f, Y: 35f),
            Team1: new(XCenter: 442f, XHalfSpread: 8f, ZCenter: 745f, ZHalfSpread: 8f, Y: 35f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // ── Кан (Caen, MapId=9) ───────────────────────────────────────────────
        // Team0 (X=520,Z=680) confirmed "almost correct" — left untouched.
        // Team1 only has Z close to an edge (X=520 is already central, no
        // double-edge problem, which matches it actually working — real log
        // data shows it driving normally with sane Y, just explicitly asked
        // to sit further from the Z=0 edge): moved +70 on Z, within the
        // requested +50..+80 range.
        // Terrain-verified from Normandy.asset (1000x1000, maxH=300). Spawns already
        // correct: Team0 (142,152) surface 27 -> tank Y30 (matches log), Team1
        // (153,867) surface 28 -> Y31. Only the bounds were wrong: map is 1000 wide
        // but bounds said 800, so Team1's Z=867 fell outside and bots got clamped
        // off their own spawn side. Bounds raised to the real 1000.
        [9] = new(9, "Кан",
            // Было 0..1000 — боты уезжали ЗА красную линию в непроходимую окраину и зависали.
            // Реальная зона по коллизии: X[88,935] Z[73,924].
            XMin: 80, XMax: 940, ZMin: 70, ZMax: 930,
            // Group 0 = A_base (north ~161,850), Group 1 = B_base (south ~166,165)
            Team0: new(XCenter: 153f, XHalfSpread: 5f, ZCenter: 867f, ZHalfSpread: 5f, Y: 31f),
            Team1: new(XCenter: 142f, XHalfSpread: 5f, ZCenter: 152f, ZHalfSpread: 5f, Y: 30f),
            Obstacles: Array.Empty<ObstacleBox>()),
        // ── Нормандия / приморская битва (network MapId 29) ───────────────────
        // Coastal map (MapData_16), no heightmap for this id — server uses a flat Y.
        // Spawns set from the player's OWN driven log (PlayerPos map=29): both spots
        // sit on solid ground at real Y≈28, on opposite corners:
        //   Team0 (170,171) Y28,  Team1 (799,835) Y28.
        // Flat Y=30 (2 m above ground) so the tank lands cleanly; the old forest/sea
        // spawns are gone. Note: the inland forest is higher ground, so bots that
        // wander there may clip low — the main playable diagonal is ~Y28. Boxes:
        // obstacles/map_29.txt (also embedded).
        [29] = new(29, "Нормандия",
            XMin: 50, XMax: 1000, ZMin: 50, ZMax: 950,
            Team0: new(XCenter: 170f, XHalfSpread: 5f, ZCenter: 171f, ZHalfSpread: 5f, Y: 30f),
            Team1: new(XCenter: 799f, XHalfSpread: 5f, ZCenter: 835f, ZHalfSpread: 5f, Y: 30f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // ── Сталинград (network MapId 30) — ТОЛЬКО РЕЖИМ ОБОРОНЫ ─────────────
        // Точки из driven PlayerPos. Защитники (игрок + союзники) — линия обороны на
        // Z≈145: игрок в центре (290,145), союзники по краям (132,147)/(438,142).
        // Враги (волны) — линия атаки на Z≈482: (145,482)/(297,480)/(455,484).
        [30] = new(30, "Сталинград",
            XMin: 20, XMax: 580, ZMin: 125, ZMax: 580,
            Team0: new(XCenter: 290f, XHalfSpread: 20f, ZCenter: 145f, ZHalfSpread: 12f, Y: 33f),
            Team1: new(XCenter: 297f, XHalfSpread: 30f, ZCenter: 482f, ZHalfSpread: 12f, Y: 33f),
            Obstacles: Array.Empty<ObstacleBox>(),
            // Чистые позиции у ворот и перед линией стен (не за глухим забором с трубами):
            BotSpawns:      new (float, float)[] { (180f, 185f), (275f, 185f), (375f, 185f), (205f, 145f), (365f, 145f) },
            AttackerSpawns: new (float, float)[] { (150f, 475f), (270f, 475f), (455f, 475f) }),

        // ── Горящие пески / alm (network MapId 11) ────────────────────────────
        // ИСПРАВЛЕНО: этот id грузит НЕ Берлин, а пустынную карту «Горящие пески»
        // (сцена alm = MapData_007). Подтверждено:
        //   1) твоим driven-логом  [RS] PlayerPos map=11 (координаты ниже);
        //   2) коллизией MapData_007 — в ней две деревни РОВНО в точках спавна
        //      (191 бокс на Z≈100-200, X-медиана 439; 209 боксов на Z≈700-800,
        //       X-медиана 455 — юг и север).
        // Команды разнесены ЮГ/СЕВЕР по Z (НЕ по X, как было в берлинской заглушке),
        // обе в центре по X≈440 (южная и северная деревни):
        //   Team0 (юг):   X=438 Z=154   (seen X:157..438 Z:129..531)
        //   Team1 (север):X=442 Z=745   (seen X:157..475 Z:129..764)
        // Y=35 — из лога (Y-диапазон сцены [-20..53], 35 в норме).
        // Границы: X 120..660 покрывают обе деревни (структуры X≈284..597) + запас;
        // Z 5..900 — весь коридор юг↔север. Если боты упираются на востоке — сузь XMax.
        // Коллизия: obstacles/map_11.txt (90 боксов — ТОЛЬКО дома+скалы, мелочь
        // отфильтрована, чтобы боты не застревали как на Сталинграде).
        // Старая берлинская коллизия сохранена в map_11_BERLIN_backup.txt — перевесь
        // её на настоящий сетевой id Берлина (см. MAPPING.md), когда определишь пробой.
        [11] = new(11, "Горящие пески",
            XMin: 150, XMax: 485, ZMin: 120, ZMax: 775,
            Team0: new(XCenter: 438f, XHalfSpread: 8f, ZCenter: 154f, ZHalfSpread: 8f, Y: 35f),
            Team1: new(XCenter: 442f, XHalfSpread: 8f, ZCenter: 745f, ZHalfSpread: 8f, Y: 35f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // ── НОВЫЕ КАРТЫ (коллизия из файлов obstacles/map_NN.txt). Спавны/границы —
        // ОЦЕНКА из геометрии бандла (driven-данных пока нет), уточнить заездом:
        // прогони карту и пришли [RS] PlayerPos map=NN. Спавн Y = высота террейна+3
        // (раньше карт не было в конфиге → брался дефолт Y и танки спавнились ПОД картой).

        // map 43 = Ледяной мир (iceworld). Driven: диагональ, Team0 SW (195,137),
        // Team1 NE (626,648), surface Y=0. 265 боксов (нефтебаки/стены/дома).
        [43] = new(43, "Ледяной мир",
            XMin: 40, XMax: 740, ZMin: 50, ZMax: 750,
            Team0: new(XCenter: 195f, XHalfSpread: 8f, ZCenter: 137f, ZHalfSpread: 8f, Y: 3f),
            Team1: new(XCenter: 626f, XHalfSpread: 8f, ZCenter: 648f, ZHalfSpread: 8f, Y: 3f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // map 74 = Лион (Lion). Реальный runtime-ID = 74 (клиент шлёт 74; раньше стоял 70 →
        // спавн Y=68. Коллизия зданий — из obstacles/map_74.txt (mesh-footprint, т.к.
        // у зданий нет коллайдеров). Спавны/границы — ОЦЕНКА, уточнить [RS] PlayerPos map=74.
        [74] = new(74, "Лион",
            XMin: 128, XMax: 772, ZMin: 300, ZMax: 700,
            // Границы расширены по Z: в узкой полосе Z[418,562] дома глушили проезд полностью,
            // и мне приходилось «прорезать» коллизию (из-за чего боты ехали и стреляли СКВОЗЬ дома).
            // Теперь дома целые, а объезд есть за счёт более широкой зоны.
            // Driven-калибровка ([RS] PlayerPos map=74): X-сплит, узкая полоса Z~450.
            // Team0 запад (137,454), Team1 восток (766,447), Y=66.
            Team0: new(XCenter: 137f, XHalfSpread: 8f, ZCenter: 454f, ZHalfSpread: 8f, Y: 66f),
            Team1: new(XCenter: 766f, XHalfSpread: 8f, ZCenter: 447f, ZHalfSpread: 8f, Y: 66f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // map 78 = Склад (Factory). Террейн ~25. 272 бокса (стены/ангары/сараи/скалы).
        [78] = new(78, "Склад",
            XMin: 113, XMax: 904, ZMin: 220, ZMax: 800,
            // Group 0 = A_base (south ~604,206), Group 1 = B_base (north ~568,818)
            Team0: new(XCenter: 578f, XHalfSpread: 8f, ZCenter: 233f, ZHalfSpread: 8f, Y: 28f),
            Team1: new(XCenter: 551f, XHalfSpread: 8f, ZCenter: 784f, ZHalfSpread: 8f, Y: 28f),
            Obstacles: Array.Empty<ObstacleBox>()),

        // MapId=5 = TrainingGround in client — excluded from all arrays
    };

    // ── map-aware helpers ─────────────────────────────────────────────────────

    private MapConfig GetMapConfig(byte mapId) =>
        Maps.TryGetValue(mapId, out var cfg) ? cfg : Maps[1];

    // v.fix (камуфляж по карте, не по ангару): в клиенте (Player.SetBattleMapId,
    // decfilesgame/Player.cs) сезон карты определяет, какой ИЗ ТРЁХ купленных слотов
    // камуфляжа (0=снег, 1=поле/лето, 2=пустыня — Table_Cloth.suit_maptype) показывается
    // в бою — НЕЗАВИСИМО от того, что "надето" в ангаре (CurTankCoatId). Соответствует
    // decfilesgame/Table_Battlemap.cs: exterior_type (1=снег,2=поле,3=пустыня).
    //
    // У эмулятора нет исходных данных Table_Battlemap для реальных MapId — заполните
    // словарь ниже по своим картам (посмотрите exterior_type в клиентских данных).
    // Пока карта не указана здесь — поведение НЕ меняется (используется камуфляж из
    // ангара, как раньше), так что заполнять можно постепенно, без риска регресса.
    //
    // Пример: если MapId=5 ("Winter Field") — снежная карта, добавьте: { 5, 1 },
    // MapId=1 ("Desert Storm") — пустынная: { 1, 3 }, обычная летняя/полевая карта: { x, 2 }.
    private static readonly Dictionary<byte, int> MapExteriorType = new()
    {
        // { mapId, exteriorType },где exteriorType: 1=снег, 2=поле/лето, 3=пустыня
        { 1, 1 },
        { 2, 2 },
        { 3, 2 },
        { 9, 2 },
        { 10, 3 },
        { 11, 3 },
        { 29, 2 },
        { 43, 1 },
        { 74, 2 },
        { 78, 2 },
        { 30, 1 },
    };

    /// <summary>
    /// Сезонный камуфляж: если для карты room.MapId задан exterior_type в
    /// MapExteriorType, подбирает подходящий купленный слот камуфляжа танка
    /// (TankCoatRepository), иначе не трогает переданное значение (fallback —
    /// камуфляж, "надетый" в ангаре, старое поведение).
    /// </summary>
    private int ResolveSeasonalCoatId(Tank? tank, byte mapId, int fallbackCoatId)
    {
        if (tank == null || !MapExteriorType.TryGetValue(mapId, out int exteriorType))
            return fallbackCoatId;

        int slotIndex = exteriorType - 1; // 1=снег->0, 2=поле->1, 3=пустыня->2
        var coatRepo  = new TankCoatRepository(_db);
        var slots     = coatRepo.GetCoats(tank.TankGUID);
        if (slotIndex < 0 || slotIndex >= slots.Length || slots[slotIndex].IsEmpty)
            return 0; // как в оригинале: нет купленного камуфляжа под сезон — камуфляжа нет

        return slots[slotIndex].CoatTableId;
    }

    // GetSpawnX/Z/Y all take the team's `group` (0 or 1) and read THAT team's
    // own SpawnZone. GetSpawnZ and GetSpawnY used to ignore `group` entirely
    // and always return Team0's Z/Y regardless of which team was actually
    // spawning — harmless back when every map had Team0.Z == Team1.Z (and
    // Team0.Y == Team1.Y), which was true for every map until the spawn
    // coordinates were corrected to match each map's real minimap markers.
    // Once Berlin/Kan/Moscow got genuinely different Z per team, this silently
    // collapsed BOTH teams onto Team0's Z every single spawn and respawn —
    // exactly the "everyone lands in one spot" symptom.
    // v13 (баг 6): точка спавна, разнесённая от живых союзников минимум на 8 м.
    // Пробуем сетку смещений вокруг центра зоны (спираль 0, ±9, ±18, ±27 по X и Z),
    // берём первую точку дальше 8 м от каждого живого одногруппника; если все заняты —
    // широкий случайный разброс. Убирает спавн «вплотную» из отчёта.
    private static readonly (float dx, float dz)[] SpawnSpiralOffsets = BuildSpawnSpiralOffsets();

    private static (float dx, float dz)[] BuildSpawnSpiralOffsets()
    {
        float[] offs = { 0f, 9f, -9f, 18f, -18f, 27f, -27f };
        var list = new List<(float dx, float dz)>(offs.Length * offs.Length);
        foreach (var dz in offs)
            foreach (var dx in offs)
                list.Add((dx, dz));
        list.Sort((a, b) => (a.dx * a.dx + a.dz * a.dz).CompareTo(b.dx * b.dx + b.dz * b.dz));
        return list.ToArray();
    }

    /// <summary>
    /// Azimuth in degrees (0=+Z North, 90=+X East) facing from <paramref name="group"/>'s
    /// spawn zone directly toward the opposing team's spawn zone. Prevents tanks from spawning
    /// facing backwards into the map border or sideways into walls.
    /// </summary>
    private float GetSpawnYaw(byte mapId, byte group)
    {
        var cfg   = GetMapConfig(mapId);
        var own   = group == 0 ? cfg.Team0 : cfg.Team1;
        var enemy = group == 0 ? cfg.Team1 : cfg.Team0;
        float dx  = enemy.XCenter - own.XCenter;
        float dz  = enemy.ZCenter - own.ZCenter;
        if (dx * dx + dz * dz < 1f) return group == 0 ? 0f : 180f;
        return ((MathF.Atan2(dx, dz) * (180f / MathF.PI)) % 360f + 360f) % 360f;
    }

    private bool IsSpawnSpotFlatAndClear(BattleRoom room, float cx, float cz, float x, float z, ObstacleBox[] obs)
    {
        if (InsideObstacle(x, z, obs, BotHullRadius + 1.5f, SampleHeight(room, x, z))) return false;
        if (IsNavHazard(room, x, z)) return false;
        if (TerrainHeightmaps.ByMap.ContainsKey(room.MapId))
        {
            float c = GroundAt(room, x, z, out bool ok);
            if (ok)
            {
                float gx1 = GroundAt(room, x + 3.5f, z, out bool ok1);
                float gx2 = GroundAt(room, x - 3.5f, z, out bool ok2);
                float gz1 = GroundAt(room, x, z + 3.5f, out bool ok3);
                float gz2 = GroundAt(room, x, z - 3.5f, out bool ok4);
                float maxSlope = MathF.Max(
                    MathF.Max(ok1 ? MathF.Abs(gx1 - c) / 3.5f : 0f, ok2 ? MathF.Abs(gx2 - c) / 3.5f : 0f),
                    MathF.Max(ok3 ? MathF.Abs(gz1 - c) / 3.5f : 0f, ok4 ? MathF.Abs(gz2 - c) / 3.5f : 0f));
                if (maxSlope > MaxSlopeGrade) return false;
                if (TooSteep(room, cx, cz, x, z)) return false;
            }
        }
        return true;
    }

    private (float x, float z) PickSpawnSpot(BattleRoom room, byte group)
    {
        var cfg  = GetMapConfig(room.MapId);
        var zone = group == 0 ? cfg.Team0 : cfg.Team1;
        return PickSpawnSpotNear(room, zone.XCenter, zone.ZCenter);
    }

    /// <summary>
    /// Picks a free, obstacle-clear, navigable spawn spot around (<paramref name="anchorX"/>, <paramref name="anchorZ"/>),
    /// maintaining at least 10 m separation from existing tanks so bots never spawn inside each other or obstacles.
    /// </summary>
    private (float x, float z) PickSpawnSpotNear(BattleRoom room, float anchorX, float anchorZ)
    {
        var cfg = GetMapConfig(room.MapId);
        var spawnObs = (room.FileObstacles != null && room.FileObstacles.Length > 0)
            ? room.FileObstacles : cfg.Obstacles;
        EnsureNavGrid(room);

        bool HasSpace(float x, float z, float minDist)
        {
            foreach (var q in room.Players.Values)
                if (Distance2D(q.PosX, q.PosZ, x, z) < minDist)
                    return false;
            return true;
        }

        // Pass 1: clean navigable cell + >= 10m tank separation + clear obstacles/slopes
        foreach (var (dx, dz) in SpawnSpiralOffsets)
        {
            float x = Math.Clamp(anchorX + dx + _random.Next(-1, 2), cfg.XMin + 12f, cfg.XMax - 12f);
            float z = Math.Clamp(anchorZ + dz + _random.Next(-1, 2), cfg.ZMin + 12f, cfg.ZMax - 12f);
            if (!HasSpace(x, z, 10f)) continue;
            if (IsNavCellBlocked(room, x, z)) continue;
            if (IsSpawnSpotFlatAndClear(room, anchorX, anchorZ, x, z, spawnObs))
                return (x, z);
        }

        // Pass 2: wide jitter around anchor (preferring nav-free, else any clear >= 9m spot)
        for (int attempt = 0; attempt < 60; attempt++)
        {
            float rx = Math.Clamp(anchorX + _random.Next(-35, 36), cfg.XMin + 12f, cfg.XMax - 12f);
            float rz = Math.Clamp(anchorZ + _random.Next(-35, 36), cfg.ZMin + 12f, cfg.ZMax - 12f);
            if (!HasSpace(rx, rz, 9f)) continue;
            if (attempt < 35 && IsNavCellBlocked(room, rx, rz)) continue;
            if (IsSpawnSpotFlatAndClear(room, anchorX, anchorZ, rx, rz, spawnObs))
                return (rx, rz);
        }

        return (Math.Clamp(anchorX, cfg.XMin + 12f, cfg.XMax - 12f),
                Math.Clamp(anchorZ, cfg.ZMin + 12f, cfg.ZMax - 12f));
    }

    private float GetSpawnX(byte group, byte mapId = 1)
    {
        var cfg  = GetMapConfig(mapId);
        var zone = group == 0 ? cfg.Team0 : cfg.Team1;
        return Math.Clamp(
            zone.XCenter + _random.Next(-(int)zone.XHalfSpread, (int)zone.XHalfSpread + 1),
            cfg.XMin + 10f, cfg.XMax - 10f);
    }
    private float GetSpawnZ(byte group, byte mapId = 1)
    {
        var cfg  = GetMapConfig(mapId);
        var zone = group == 0 ? cfg.Team0 : cfg.Team1;
        return Math.Clamp(
            zone.ZCenter + _random.Next(-(int)zone.ZHalfSpread, (int)zone.ZHalfSpread + 1),
            cfg.ZMin + 20f, cfg.ZMax - 20f);
    }
    private float GetSpawnY(byte group, byte mapId = 1)
    {
        var cfg = GetMapConfig(mapId);
        return group == 0 ? cfg.Team0.Y : cfg.Team1.Y;
    }

    private static ushort GetSpawnCoordId(byte mapId, byte group) => mapId switch
    {
        1  => group == 0 ? (ushort)510  : (ushort)525,
        2  => group == 0 ? (ushort)205  : (ushort)221,
        3  => group == 0 ? (ushort)144  : (ushort)159,
        4  => group == 0 ? (ushort)189  : (ushort)174,
        9  => group == 0 ? (ushort)189  : (ushort)174,
        10 => group == 0 ? (ushort)756  : (ushort)786,
        11 => group == 0 ? (ushort)756  : (ushort)786,
        29 => group == 0 ? (ushort)583  : (ushort)613,
        30 => (ushort)646,
        43 => group == 0 ? (ushort)946  : (ushort)976,
        74 => group == 0 ? (ushort)1481 : (ushort)1511,
        78 => group == 0 ? (ushort)1607 : (ushort)1637,
        _  => (ushort)(group + 1),
    };

    // ─────────────────────────── crew & equipment battle bonuses ───────────────
    // Equipment effects (оснащение). The client shows these as % modifiers in the
    // garage; we apply them server-side to the battle attribute array so they
    // actually affect HP, reload, aim time, etc. in combat.
    // Key = equipmentId, value = list of (attrIndex, multiplier). Multiplier < 1
    // reduces the stat (good for reload/aim), > 1 increases it (good for HP/speed).
    // Real equipment IDs observed in EquipmentOperate logs: 96, 105, 108, 120,
    // 144, 153, 162. These map to the equipment the player installed on Object 279
    // (rammer, drive, springs, CO2 tanks, wet ammo rack, optics, stabiliser).
    // Effects expressed as (attrIndex, multiplier). mult<1 reduces (reload/aim),
    // mult>1 increases (HP/speed/view). Indices: 2/3=fwd/rev speed, 12=turret rot,
    // 19=aim time, 20=view, 24=reload, 29/30=penetration, 31=ammo HP, 32=fuel HP,
    // 37/38=hull/turret HP buff, 39=total HP.
    // Оснащение по скринам ангара. Формат: id → (название, эффекты, толькоОборона).
    // idx: 24=перезарядка, 19=сведение, 39=множитель HP, 999=ПЛОСКОЕ +HP.
    // Группировка id из лога: 48/51/69/72/78 = «Для Обороны» (низкие id),
    // 96/105/108/120/144/153/162 = «Универсальное»+«Тактика» (высокие id).
    // Название логируется рядом с id — сверь его с тем, что видишь в ангаре, и
    // скажи, если какой-то id даёт не тот предмет (внутри группы порядок мог сбиться).
    // id оснащения «Стимуляторы» (продлевает энергетик на 30%). Точный id уточни в логе.
    private const int StimulantEquipId = -1; // -1 = не задан (продление отключено)
    private static readonly Dictionary<int, (string name, (int idx, float val)[] fx, bool defenseOnly)> EquipmentEffects = new()
    {
        // IDs verified against the real client Table_Equipment (purchase-order log matched
        // to the item tooltips). Effects/values unchanged — only the id→item mapping fixed.
        // ── Универсальное (действует в любом бою) ───────────────────────────
        [51]  = ("Усиленный привод сред.III (сведение −15%)",         new[] { (19, 0.85f) },              false),
        [48]  = ("Гаубичный досылатель сред.III (перезарядка −20%)",  new[] { (24, 0.80f) },              false),
        [72]  = ("Бронезащита башни сред.III (прочность +300)",       new[] { (999, 300f) },              false),
        [69]  = ("Бронезащита корпуса сред.III (прочность +400)",     new[] { (999, 400f) },              false),
        [78]  = ("Буфер от снарядов III (−5% урона ББ)",              System.Array.Empty<(int, float)>(), false), // спец-механика (флаг)

        // ── Тактика (игровые механики, не боевые статы) ─────────────────────
        [153] = ("Передовой транспортёр III (смена снаряда без перезарядки)", System.Array.Empty<(int, float)>(), false),
        [144] = ("Тактическая карта III (карта, клавиша Q)",          System.Array.Empty<(int, float)>(), false),

        // ── Для Обороны (эффект ТОЛЬКО в режиме обороны) ────────────────────
        [162] = ("Передовой привод наводки III Об (сведение −30%)",   new[] { (19, 0.70f) },              true),
        [96]  = ("Гаубичный досылатель ОБ сред.III (перезарядка −30%)", new[] { (24, 0.70f) },            true),
        [120] = ("Крупная защитная сетка III (защита +10%)",          System.Array.Empty<(int, float)>(), true), // спец-механика (флаг)
        [108] = ("Направляющий локатор III (бомбардировки +10%)",     System.Array.Empty<(int, float)>(), true), // спец-механика (флаг)
        [105] = ("Система арт.корректировки III (Катюши +10%)",       System.Array.Empty<(int, float)>(), true), // спец-механика (флаг)
    };

    /// <summary>
    /// Apply equipment bonuses to a freshly-built battle attribute array, in place.
    /// Returns (hpMult, flatHp): callers scale maxHp by hpMult and then add flatHp
    /// (armor equipment gives flat +HP, e.g. hull armor +400, turret armor +300).
    /// Crew level is deliberately NOT applied here. The crew-level effect on the six
    /// characteristics it strengthens (dispersion/accuracy, aim time, reload, vision,
    /// turret rotation, hull rotation) is computed entirely CLIENT-SIDE from
    /// Table_Fight_level (client tables ComputeNewAttribute / GetDeltaForCurretTk),
    /// driven only by the CurLv we send — the server has no lever on those percentages
    /// and must not substitute an HP or mobility bonus in their place. Defense-only
    /// combat effects live in ApplyDefenseEquipmentFlags.
    /// </summary>
    private (float hpMult, float flatHp) ApplyCrewAndEquipmentBonuses(float[] attrs, long playerGuid, long tankGuid, bool isDefenseMode)
    {
        float hpMult = 1f;
        float flatHp = 0f;

        // ── Crew level → reload (2.4775% per level, 29.73% at level 12) ────────
        // The BATTLE tank is built from THIS attribute array (sent as
        // RoomMainTankAttribute), and the client does NOT apply the crew effect in
        // battle — it only does so in the hangar (ComputeNewAttribute). So the crew
        // reload speed-up has to be baked in here, or it never applies in a fight.
        // attrs[24] = EReloadTime. Same formula as the hangar (DLL) + Hall paths.
        if (_crewRepo != null && tankGuid != 0 && attrs.Length > 24 && attrs[24] > 0.001f)
        {
            try
            {
                var crew = _crewRepo.GetPlayerCrew(playerGuid)
                                    .FirstOrDefault(c => c.TankGuid == tankGuid);
                int crewLv = crew?.CurLv ?? 0;
                if (crewLv > 0)
                {
                    float rate = Math.Min(crewLv * (0.2291667f / 12f), 0.9f);
                    attrs[24] *= (1f - rate);
                    Console.WriteLine($"[RS] Crew reload: lvl={crewLv} → -{rate*100:F2}% (reload now {attrs[24]:F2}s)");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[RS] Crew reload error: {ex.Message}"); }
        }

        // ── Equipment effects ──────────────────────────────────────────────
        if (_equipRepo != null && tankGuid != 0)
        {
            try
            {
                var installed = _equipRepo.GetTankEquipmentIds(tankGuid);
                float[] baseAttrs = (float[])attrs.Clone(); // base for percentage (changetype 1) effects
                foreach (int equipId in installed)
                {
                    // Prefer the REAL per-id effect from Table_Equipment (correct id->effect
                    // mapping; no dependence on the hand-labelled fallback which had ids swapped).
                    var eq = _gameTables.GetEquipment(equipId);
                    if (eq != null)
                    {
                        if (eq.DefenseOnly && !isDefenseMode)
                        {
                            Console.WriteLine($"[RS] Equip {equipId}: оборонное — в этом бою не действует");
                            continue;
                        }
                        int applied = 0;
                        applied += ApplyEqStat(attrs, baseAttrs, eq.Attribute1, eq.Value1, eq.ChangeType1, ref hpMult, ref flatHp);
                        applied += ApplyEqStat(attrs, baseAttrs, eq.Attribute2, eq.Value2, eq.ChangeType2, ref hpMult, ref flatHp);
                        applied += ApplyEqStat(attrs, baseAttrs, eq.Attribute3, eq.Value3, eq.ChangeType3, ref hpMult, ref flatHp);
                        applied += ApplyEqStat(attrs, baseAttrs, eq.Attribute4, eq.Value4, eq.ChangeType4, ref hpMult, ref flatHp);
                        applied += ApplyEqStat(attrs, baseAttrs, eq.Attribute5, eq.Value5, eq.ChangeType5, ref hpMult, ref flatHp);
                        Console.WriteLine($"[RS] Equip {equipId} (Table_Equipment): применено {applied} эффект(ов)");
                        continue;
                    }
                    // Fallback: hardcoded table (used until Table_Equipment is in the JSON).
                    if (!EquipmentEffects.TryGetValue(equipId, out var e))
                    {
                        Console.WriteLine($"[RS] Equip {equipId}: НЕИЗВЕСТЕН (нет в EquipmentEffects)");
                        continue;
                    }
                    // "Для Обороны" оснащение действует только в режиме обороны.
                    if (e.defenseOnly && !isDefenseMode)
                    {
                        Console.WriteLine($"[RS] Equip {equipId} = {e.name}: оборонное — в этом бою не действует");
                        continue;
                    }
                    foreach (var (idx, val) in e.fx)
                    {
                        if (idx == 39)  { hpMult *= val; continue; }  // HP multiplier
                        if (idx == 999) { flatHp += val; continue; }  // flat +HP (armor)
                        if (idx >= 0 && idx < attrs.Length) attrs[idx] *= val;
                    }
                    Console.WriteLine($"[RS] Equip {equipId} = {e.name}: применено {e.fx.Length} эффект(ов)");
                }
            }
            catch (Exception ex) { Console.WriteLine($"[RS] Equip error: {ex.Message}"); }
        }

        // Apply HP multiplier + flat HP to the attr array's HP buff too
        attrs[39] = attrs[39] * hpMult + flatHp;
        return (hpMult, flatHp);
    }

    // Apply one Table_Equipment stat effect (mirrors client ComputeNewAttribute):
    //   changeType 0 -> flat  +value
    //   changeType 1 -> percent of base (+base*value)
    // attr 0 or >=1000 = scripted/special (handled elsewhere), not a stat change.
    // HP attributes (37 hull, 38 turret, 39 total) are routed to the hpMult/flatHp
    // accumulators so the tank's maxHp (finalized once) stays consistent.
    private static int ApplyEqStat(float[] attrs, float[] baseAttrs, int attr, float value, int changeType, ref float hpMult, ref float flatHp)
    {
        if (attr <= 0 || attr >= 1000) return 0;
        if (attr == 37 || attr == 38 || attr == 39)
        {
            if (changeType == 0) flatHp += value;
            else                 hpMult *= 1f + value;
            return 1;
        }
        if (attr >= attrs.Length) return 0;
        if (changeType == 0) attrs[attr] += value;
        else                 attrs[attr] += baseAttrs[attr] * value;
        return 1;
    }

    // ── "Для Обороны" combat equipment (effect ONLY in defense mode) ──────────
    // These three can't be expressed as a simple attribute multiplier — they change
    // how incoming/outgoing damage is resolved at runtime — so they live as flags on
    // RoomPlayer instead of in the EquipmentEffects attr table.
    private const int DefenseNetEquipId       = 120; // Крупная защитная сетка: защита +10% + малый шанс неуязвимости
    private const int DefenseLocatorEquipId   = 108; // Направляющий локатор: урон от бомбардировок +10%
    private const int DefenseArtCorrectEquipId = 105; // Система арт.корректировки: урон Катюши +10%
    private const int BufferEquipId           = 78;  // Буфер от снарядов: входящий урон ББ −5% (универсальное)
    private const float BufferDamageTakenMult     = 0.95f; // -5% incoming (AP) damage, any mode
    private const float DefenseNetDamageTakenMult = 0.90f; // -10% incoming damage
    private const float DefenseNetInvulnChance    = 0.05f; // "малый шанс неуязвимости" (5%)
    private const float DefenseBombDamageMult     = 1.10f; // +10% bombardment damage
    private const float DefenseKatyushaDamageMult = 1.10f; // +10% Katyusha damage

    /// <summary>
    /// Populate a player's combat/scripted equipment & passive consumable flags from its
    /// installed equipment (Table_Equipment) and equipped consumables (Table_Consumable).
    /// Handles both universal special attributes and Defense-mode-only equipment.
    /// </summary>
    private void ApplyEquipmentCombatEffects(RoomPlayer player, long tankGuid, bool isDefenseMode, float[]? attrs = null)
    {
        // Reset to neutral so a respawn/hot-join never inherits stale buffs.
        player.DamageTakenMult       = 1f;
        player.ApDamageTakenMult     = 1f;
        player.HalveDamageChance     = 0f;
        player.InvulnChance          = 0f;
        player.BombDamageMult        = 1f;
        player.KatyushaDamageMult    = 1f;
        player.ShellDamageMult       = 1f;
        player.StimulantDurMult      = 1f;
        player.RepairCdMult          = 1f;
        player.PveSpBonusMult        = 1f;
        player.TrackRepairSpeedBonus = 0f;
        player.TrackImmunitySeconds  = 0f;
        player.TrackImmuneUntil      = DateTime.MinValue;
        player.FastChangeShell       = false;
        player.IgnoreWeatherVision   = false;
        player.AmmoBayNoDmgMult      = 1f;
        player.FuelTankNoDmgMult     = 1f;
        player.ConsumableRepairMult  = 1f;
        player.FireDamageMult        = 1f;
        player.HasAutoExtinguisher   = false;
        player.HideFactor            = attrs != null && attrs.Length > 21 ? Math.Clamp(attrs[21], 0f, 0.7f) : 0f;

        if (_equipRepo != null && tankGuid != 0)
        {
            try
            {
                var installed = _equipRepo.GetTankEquipmentIds(tankGuid);
                foreach (int equipId in installed)
                {
                    var eq = _gameTables.GetEquipment(equipId);
                    if (eq != null)
                    {
                        bool allowDefense = !eq.DefenseOnly || isDefenseMode;
                        ApplySingleEquipmentCombatAttr(player, eq.Attribute1, eq.Value1, allowDefense);
                        ApplySingleEquipmentCombatAttr(player, eq.Attribute2, eq.Value2, allowDefense);
                        ApplySingleEquipmentCombatAttr(player, eq.Attribute3, eq.Value3, allowDefense);
                        ApplySingleEquipmentCombatAttr(player, eq.Attribute4, eq.Value4, allowDefense);
                        ApplySingleEquipmentCombatAttr(player, eq.Attribute5, eq.Value5, allowDefense);
                        continue;
                    }

                    // Legacy fallback if JSON row is absent
                    if (equipId == BufferEquipId) player.ApDamageTakenMult *= BufferDamageTakenMult;
                    else if (isDefenseMode)
                    {
                        switch (equipId)
                        {
                            case DefenseNetEquipId:
                                player.DamageTakenMult *= DefenseNetDamageTakenMult;
                                player.InvulnChance = Math.Max(player.InvulnChance, DefenseNetInvulnChance);
                                break;
                            case DefenseLocatorEquipId:
                                player.BombDamageMult *= DefenseBombDamageMult;
                                break;
                            case DefenseArtCorrectEquipId:
                                player.KatyushaDamageMult *= DefenseKatyushaDamageMult;
                                break;
                        }
                    }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[RS] Equip combat flags error: {ex.Message}"); }
        }

        // Passive bonuses from equipped Table_Consumable (Gold/Elite repair kit & extinguisher)
        foreach (int pid in player.Provisions)
        {
            if (pid <= 0) continue;
            var con = _gameTables.GetConsumable(pid);
            if (con == null) continue;
            if (con.Id == 4 || con.Id == 5)
                player.HasAutoExtinguisher = true;
            if (!string.IsNullOrWhiteSpace(con.AddEffect) && !string.IsNullOrWhiteSpace(con.AddEffectCount))
            {
                var fxIds  = con.AddEffect.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var fxVals = con.AddEffectCount.Split('_', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < Math.Min(fxIds.Length, fxVals.Length); i++)
                {
                    if (!int.TryParse(fxIds[i], out int fxId) || !float.TryParse(fxVals[i], out float pct)) continue;
                    if (fxId == 1) player.ConsumableRepairMult = Math.Min(player.ConsumableRepairMult, Math.Max(0.2f, 1f - pct / 100f));
                    if (fxId == 2) player.FireDamageMult       = Math.Min(player.FireDamageMult,       Math.Max(0.2f, 1f - pct / 100f));
                }
            }
        }
    }

    private static void ApplySingleEquipmentCombatAttr(RoomPlayer player, int attr, float val, bool allowMode)
    {
        if (attr <= 0) return;
        switch (attr)
        {
            case 44: // EAmmobayNodamgePercentBuff (Core Protection Plate: reduces module hit chance by 25%)
                player.AmmoBayNoDmgMult *= Math.Max(0.1f, 1f - val);
                break;
            case 45: // EFueltankNodamgePercentBuff
                player.FuelTankNoDmgMult *= Math.Max(0.1f, 1f - val);
                break;
            case 1001: // Track repair speed +val (10%)
                if (allowMode) player.TrackRepairSpeedBonus += val;
                break;
            case 1002: // Defense +val (Large Defense Net: -10% incoming damage)
                if (allowMode) player.DamageTakenMult *= Math.Max(0.1f, 1f - val);
                break;
            case 1003: // Fiberscope: ignore weather/night vision penalty
                player.IgnoreWeatherVision = true;
                break;
            case 1006: // Ammo Buffer: chance (0.01) to halve incoming damage
                player.HalveDamageChance = Math.Max(player.HalveDamageChance, val);
                break;
            case 1007: // Track Maintenance Device: track immunity seconds after repair (10s)
                player.TrackImmunitySeconds = Math.Max(player.TrackImmunitySeconds, val);
                break;
            case 1008: // Large Defense Net: chance to evade hit completely (0.05)
                if (allowMode) player.InvulnChance = Math.Max(player.InvulnChance, val);
                break;
            case 1009: // Defense/Honor Flag: SP earn multiplier (+10% / +20%)
                if (allowMode) player.PveSpBonusMult += val;
                break;
            case 1010: // Dense Point Locator: Katyusha damage (+10%)
                if (allowMode) player.KatyushaDamageMult *= (1f + val);
                break;
            case 1011: // Fall Point Locator: Bomber damage (+10%)
                if (allowMode) player.BombDamageMult *= (1f + val);
                break;
            case 1012: // Excitement Booster: Stimulant duration (+30%)
                if (allowMode) player.StimulantDurMult *= (1f + val);
                break;
            case 1013: // Almighty Repair Device: PVE repair cooldown (-30%)
                if (allowMode) player.RepairCdMult *= Math.Max(0.2f, 1f + val);
                break;
            case 1019: // Ammo Buffer: -5% AP damage taken (val = -0.05)
                player.ApDamageTakenMult *= Math.Max(0.1f, 1f + val);
                break;
            case 1020: // Shell Damage modifier (+5%/+10% Bore Pressure, -20%/-10% Def GLD)
                if (allowMode) player.ShellDamageMult *= Math.Max(0.1f, 1f + val);
                break;
            case 1021: // Transporter: switch shells in battle without reloading
                player.FastChangeShell = true;
                break;
        }
    }


    /// <summary>
    /// Ground the height grid near both team spawn zones only, using each
    /// zone's calibrated Y, so bots standing at spawn are correct from tick
    /// one. Deliberately does NOT pre-fill the rest of the map.
    ///
    /// Root cause this fixes: the previous version filled EVERY cell across
    /// the whole map with one flat Y. That meant SampleHeight's "exact cell"
    /// lookup below always succeeded — even for cells the player never drove
    /// through — so the spiral search for the *nearest real sample* never
    /// actually ran; every bot more than a few steps from the exact spot the
    /// player walked got the flat seed value regardless of real terrain.
    /// On a sloped map (Berlin's spawn-to-centre hill is calibrated at
    /// Y=50→0 over ~80 units) that put bots ~30-50 units off the real ground
    /// — exactly the floating/sinking-through-geometry look in the reports.
    /// Leaving unexplored cells empty lets the spiral search do its job: as
    /// soon as the player drives anywhere near a bot, that real sample wins.
    /// </summary>
    private void SeedHeightGrid(BattleRoom room)
    {
        var cfg = GetMapConfig(room.MapId);
        int cell = (int)BattleRoom.HeightCellSize;

        // Load any terrain learned in previous battles on this map FIRST, then seed
        // the calibrated spawn-zone heights on top so spawns keep their exact Y.
        LoadHeightGrid(room);

        // Load building collision boxes for this map from obstacles/map_N.txt (if
        // present), so new maps just need a dropped-in box file — no code changes.
        room.FileObstacles = LoadMapObstacles(room.MapId);
        // Bot route graph (нарисованные маршруты из routes/map_N_routes.txt). Пусто → патруль.
        room.RouteGraph    = LoadMapRoutes(room.MapId);

        void SeedZone(SpawnZone zone)
        {
            float x0 = zone.XCenter - zone.XHalfSpread - cell;
            float x1 = zone.XCenter + zone.XHalfSpread + cell;
            float z0 = zone.ZCenter - zone.ZHalfSpread - cell;
            float z1 = zone.ZCenter + zone.ZHalfSpread + cell;
            for (float x = x0; x <= x1; x += cell)
            for (float z = z0; z <= z1; z += cell)
            {
                int gx = (int)MathF.Floor(x / BattleRoom.HeightCellSize);
                int gz = (int)MathF.Floor(z / BattleRoom.HeightCellSize);
                room.HeightGrid[(gx, gz)] = zone.Y;
            }
        }
        SeedZone(cfg.Team0);
        SeedZone(cfg.Team1);

        // Global fallback for cells with no real sample anywhere nearby yet.
        room.KnownGroundY = cfg.Team0.Y;
        room.LastPlayerY  = cfg.Team0.Y;
        Console.WriteLine($"[RS] Seeded height grid for map {room.MapId} " +
            $"(Team0 Y={cfg.Team0.Y}, Team1 Y={cfg.Team1.Y}; rest learned from player movement)");
    }

    /// <summary>
    /// Sample terrain height at (x, z) from the player-built height grid.
    /// Searches the nearest grid cells in a spiral; falls back to KnownGroundY,
    /// then spawn Y. This lets bots follow the real terrain the player walked on.
    /// </summary>

    // Vertical offset added to the sampled terrain height when placing bots. This
    // equals the Y the (grounded) player reports at the same spot, so it is the
    // correct "on the ground" value. NOTE: bots can still look like they hover even
    // with the right Y, because the CLIENT only ground-clamps the LOCAL player (via
    // Hang.DoUpdate's raycast) — other tanks are drawn at exactly this server Y with
    // no client-side snap. The real cure for hovering is the client patch to
    // OtherTankPosition (see OtherTankPosition_ЗЕМЛЯ.cs); this constant only shifts
    // where the server thinks the ground is.
    private const float BotGroundOffsetY = 0.0f;

    private static bool TrySampleLiveElevated(BattleRoom room, float x, float z, float hmY, out float elevatedY)
    {
        if (!room.LiveElevatedGrid.IsEmpty)
        {
            int ex = (int)MathF.Floor(x / BattleRoom.ElevatedCellSize);
            int ez = (int)MathF.Floor(z / BattleRoom.ElevatedCellSize);
            if (room.LiveElevatedGrid.TryGetValue((ex, ez), out float ly) && ly > hmY + 2.0f)
            {
                elevatedY = ly;
                return true;
            }
        }
        elevatedY = 0f;
        return false;
    }

    private float SampleHeight(BattleRoom room, float x, float z)
    {
        // Real terrain first, overlaid with bridge decks and any live-observed
        // elevated structures (bridges/ramps above the terrain mesh).
        if (TerrainHeightmaps.ByMap.TryGetValue(room.MapId, out var hm))
        {
            float y = hm.SurfaceBilinear(x, z);
            if (TrySampleBridgeDeck(room.MapId, x, z, out float deckY) && deckY > y)
                y = deckY;
            if (TrySampleLiveElevated(room, x, z, y, out float liveY))
                y = liveY;
            return y + BotGroundOffsetY;
        }

        if (TrySampleBridgeDeck(room.MapId, x, z, out float knownDeck))
            return knownDeck + BotGroundOffsetY;

        // Learned grid, smooth path: bilinearly blend the 4 surrounding known cells.
        // The grid stores one Y per coarse 40u cell (treated as the cell centre's
        // height); interpolating across neighbours turns the old blocky staircase
        // into a continuous surface, which is the single biggest cause of the
        // "floating a few metres above the slope" look on maps without a heightmap.
        if (TryBilinearGrid(room, x, z, out float bl)) return bl;

        int gx = (int)MathF.Floor(x / BattleRoom.HeightCellSize);
        int gz = (int)MathF.Floor(z / BattleRoom.HeightCellSize);

        // Exact cell (used when only some of the 4 bilinear corners are known)
        if (room.HeightGrid.TryGetValue((gx, gz), out float h)) return h;

        // Spiral outward up to 8 cells looking for the nearest sampled height.
        // Wider search than before so bots far from the player still find a
        // plausible ground height instead of falling back to a flat value.
        for (int radius = 1; radius <= 8; radius++)
        {
            float bestDist = float.MaxValue;
            float bestH    = float.NaN;
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                if (Math.Abs(dx) != radius && Math.Abs(dz) != radius) continue; // ring only
                if (room.HeightGrid.TryGetValue((gx + dx, gz + dz), out float nh))
                {
                    float d = dx * dx + dz * dz;
                    if (d < bestDist) { bestDist = d; bestH = nh; }
                }
            }
            if (!float.IsNaN(bestH)) return bestH;
        }

        // No sample anywhere nearby. Use the player's most recent Y as the best
        // available guess (better than a stale flat spawn Y on hilly maps).
        if (room.LastPlayerY != 0f) return room.LastPlayerY;
        // Not team-specific here — just a generic "any ground level on this
        // map" fallback when nothing else is known at all, so group=0 is fine.
        return room.KnownGroundY ?? GetSpawnY(0, room.MapId);
    }

    /// <summary>
    /// Bilinear interpolation over the learned height grid. Each cell's stored Y is
    /// treated as the height at that cell's centre; we blend the four cell centres
    /// surrounding (x,z). Succeeds only when all four are known, so partially-mapped
    /// regions fall back to the nearest-cell / spiral logic instead of inventing a
    /// value from a single corner.
    /// </summary>
    private static bool TryBilinearGrid(BattleRoom room, float x, float z, out float y)
    {
        float cell = BattleRoom.HeightCellSize;
        float cx = x / cell - 0.5f;   // cell-centre coordinate space
        float cz = z / cell - 0.5f;
        int gx0 = (int)MathF.Floor(cx);
        int gz0 = (int)MathF.Floor(cz);
        float fx = cx - gx0;
        float fz = cz - gz0;
        if (room.HeightGrid.TryGetValue((gx0,     gz0),     out float h00) &&
            room.HeightGrid.TryGetValue((gx0 + 1, gz0),     out float h10) &&
            room.HeightGrid.TryGetValue((gx0,     gz0 + 1), out float h01) &&
            room.HeightGrid.TryGetValue((gx0 + 1, gz0 + 1), out float h11))
        {
            float a = h00 + (h10 - h00) * fx;
            float b = h01 + (h11 - h01) * fx;
            y = a + (b - a) * fz;
            return true;
        }
        y = 0f;
        return false;
    }

    /// <summary>
    /// Raw terrain surface height at (x,z) with NO tank clearance added, for
    /// line-of-sight. Sets <paramref name="known"/> false when the map has no real
    /// heightmap and no learned data near (x,z) — callers must fail OPEN in that case
    /// so bots never freeze over unmapped ground.
    /// </summary>
    private float GroundAt(BattleRoom room, float x, float z, out bool known)
    {
        if (TerrainHeightmaps.ByMap.TryGetValue(room.MapId, out var hm))
        {
            float y = hm.SurfaceBilinear(x, z);
            if (TrySampleBridgeDeck(room.MapId, x, z, out float deckY) && deckY > y)
                y = deckY;
            if (TrySampleLiveElevated(room, x, z, y, out float liveY))
                y = liveY;
            known = true;
            return y;
        }
        if (TrySampleBridgeDeck(room.MapId, x, z, out float knownDeck))
        { known = true; return knownDeck; }
        if (TryBilinearGrid(room, x, z, out float bl)) { known = true; return bl; }
        int gx = (int)MathF.Floor(x / BattleRoom.HeightCellSize);
        int gz = (int)MathF.Floor(z / BattleRoom.HeightCellSize);
        if (room.HeightGrid.TryGetValue((gx, gz), out float h)) { known = true; return h; }
        known = false; return float.NaN;
    }

    /// <summary>
    /// Terrain pitch/roll (degrees) at (x,z) for a hull heading <paramref name="yawDeg"/>,
    /// from the heightmap gradient. Bots are broadcast with a flat hull (RotX=RotZ=0)
    /// otherwise, so on a slope the downhill end of the hull hangs in the air and the
    /// tank reads as "flying" / not descending evenly. Tilting the hull to the local
    /// surface fixes that. Clamped so a bad sample at a wall base can't spin the hull.
    /// NOTE: if bots end up tilted the WRONG way on slopes, flip the sign of pitch and
    /// roll below — the exact sign depends on the client's Euler axis convention and
    /// can't be verified from here.
    /// </summary>
    private (float pitch, float roll) TerrainTilt(BattleRoom room, float x, float z, float yawDeg)
    {
        // v30 (п.2): НАКЛОН ТОЛЬКО ТАМ, ГДЕ ЕСТЬ НАСТОЯЩИЙ HEIGHTMAP.
        //
        // Это и есть причина «шатающихся ботов». TerrainTilt берёт градиент высоты в
        // четырёх точках опоры, но на картах без выгруженного рельефа (Сталинград =
        // карта 30 — её в TerrainHeightmaps.ByMap НЕТ) SampleHeight возвращает
        // значения из разреженной «выученной» сетки: она засеяна только в зонах
        // спавна, между ячейками идёт интерполяция и спиральный поиск ближайшей
        // известной клетки. Разница между четырьмя опорами там — не рельеф, а шум
        // данных, и он менялся на каждом тике: корпус дёргался туда-сюда на ровной
        // городской площади (см. скриншот с Е-50 у здания).
        //
        // Настоящий рельеф есть у карт 1, 2, 9, 10 — там наклон физически верный и
        // нужен (иначе танк на склоне висит краем в воздухе). Везде остальном честнее
        // ехать с ровным корпусом, чем шататься от шума.
        return TerrainTiltWithSupport(room, x, z, yawDeg).tilt;
    }

    private ((float pitch, float roll) tilt, float supportY) TerrainTiltWithSupport(
        BattleRoom room, float x, float z, float yawDeg)
    {
        if (!TerrainHeightmaps.ByMap.ContainsKey(room.MapId)) return ((0f, 0f), float.NaN);
        // Мосты имеют ровный горизонтальный настил — не наклоняем корпус на въезде/краю.
        if (TrySampleBridgeDeck(room.MapId, x, z, out _)) return ((0f, 0f), float.NaN);

        float yaw = yawDeg * (MathF.PI / 180f);
        float fX = MathF.Sin(yaw), fZ = MathF.Cos(yaw);   // hull forward (XZ)
        float rX = MathF.Cos(yaw), rZ = -MathF.Sin(yaw);  // hull right   (XZ)
        const float hl = 3.8f;  // half track length
        const float hw = 1.7f;  // half track width
        float FR = SampleHeight(room, x + fX * hl + rX * hw, z + fZ * hl + rZ * hw);
        float FL = SampleHeight(room, x + fX * hl - rX * hw, z + fZ * hl - rZ * hw);
        float BR = SampleHeight(room, x - fX * hl + rX * hw, z - fZ * hl + rZ * hw);
        float BL = SampleHeight(room, x - fX * hl - rX * hw, z - fZ * hl - rZ * hw);
        float supportY   = 0.25f * (FR + FL + BR + BL);
        float slopeFwd   = ((FR + FL) - (BR + BL)) / (4f * hl);   // rise per unit forward
        float slopeRight = ((FR + BR) - (FL + BL)) / (4f * hw);   // rise per unit right
        float pitch = -MathF.Atan(slopeFwd)   * (180f / MathF.PI); // nose down when descending
        float roll  =  MathF.Atan(slopeRight) * (180f / MathF.PI);
        // Порог 7.5 м отсекает только резкие обрывы/стены (~45°+), но НЕ сбрасывает наклон
        // на реальных песчаных барханах 18–30° (раньше порог 3.0 м обнулял pitch/roll и
        // задняя часть танка тонула в склоне).
        float spread = MathF.Max(MathF.Max(FR, FL), MathF.Max(BR, BL))
                     - MathF.Min(MathF.Min(FR, FL), MathF.Min(BR, BL));
        if (spread > 7.5f) return ((0f, 0f), float.NaN);
        if (MathF.Abs(pitch) < 1.2f) pitch = 0f;
        if (MathF.Abs(roll)  < 1.2f) roll  = 0f;
        pitch = Math.Clamp(pitch, -28f, 28f);
        roll  = Math.Clamp(roll,  -28f, 28f);
        return ((pitch, roll), supportY);
    }

    /// <summary>
    /// True if terrain (a hill/mountain, from the real heightmap or the learned grid)
    /// rises above the straight gun→target line and would physically stop the shell.
    /// Only samples where the ground is actually KNOWN block the shot, so on maps
    /// without a heightmap this never falsely blocks over unexplored ground — it just
    /// doesn't help there until the grid fills in (or a heightmap is added). This is
    /// what stops bots sniping straight through mountains on maps like Закавказье.
    /// </summary>
    private bool TerrainBlocksShot(BattleRoom room,
        float x1, float z1, float y1, float x2, float z2, float y2)
    {
        float dx = x2 - x1, dz = z2 - z1;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return false;
        int steps = Math.Max(4, (int)(len / 3f));   // fine terrain ray sample every ~3 units
        const float clearance = 0.65f;              // tight crest slack with 257x257 heightmap
        for (int i = 1; i < steps; i++)
        {
            float t     = (float)i / steps;
            float sx    = x1 + dx * t;
            float sz    = z1 + dz * t;
            float lineY = y1 + (y2 - y1) * t;        // straight line of fire
            float g     = GroundAt(room, sx, sz, out bool known);
            if (known && g > lineY + clearance) return true;
        }
        return false;
    }

    // ── learned height-grid persistence ─────────────────────────────────────────
    // The grid is built from real player positions as they drive (each reported
    // position is a ground-truth terrain sample). Persisting it per map means every
    // session you and Фёдор play makes the next battle on that map start with denser
    // terrain knowledge — so "flying" bots fade map-by-map even on maps that have no
    // extracted Unity heightmap yet. Best-effort: any I/O error is swallowed so it
    // can never affect a live battle.
    private static string HeightGridDir =>
        Path.Combine(AppContext.BaseDirectory, "heightgrids");

    private static string HeightGridPath(byte mapId) =>
        Path.Combine(HeightGridDir, $"map_{mapId}.hg");

    private static void LoadHeightGrid(BattleRoom room)
    {
        try
        {
            string path = HeightGridPath(room.MapId);
            if (!File.Exists(path)) return;
            int n = 0;
            foreach (var line in File.ReadLines(path))
            {
                var parts = line.Split(' ');
                if (parts.Length != 3) continue;
                if (int.TryParse(parts[0], out int gx) &&
                    int.TryParse(parts[1], out int gz) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out float y))
                {
                    room.HeightGrid[(gx, gz)] = y;
                    n++;
                }
            }
            if (n > 0)
                Console.WriteLine($"[RS] Loaded {n} persisted terrain cells for map {room.MapId}");
        }
        catch { /* best-effort */ }
    }

    private static void SaveHeightGrid(BattleRoom room)
    {
        try
        {
            if (room.HeightGrid.Count == 0) return;
            Directory.CreateDirectory(HeightGridDir);
            var sb = new System.Text.StringBuilder(room.HeightGrid.Count * 12);
            foreach (var kv in room.HeightGrid)
                sb.Append(kv.Key.gx).Append(' ').Append(kv.Key.gz).Append(' ')
                  .Append(kv.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                  .Append('\n');
            File.WriteAllText(HeightGridPath(room.MapId), sb.ToString());
        }
        catch { /* best-effort */ }
    }

    // Load building collision boxes for a map from  obstacles/map_{mapId}.txt  next to
    // the server binary. One box per line: "x1 z1 x2 z2" (world X/Z rectangle). This
    // lets new maps get collision by dropping in a generated file — no recompile. If
    // the file is missing/empty, returns null and the map's hard-coded Obstacles are
    // used instead. Best-effort: any error just falls back to the built-in list.
    // Candidate locations for the obstacles/ folder, tried in order: next to the
    // binary (bin/.../net8.0/), the current working dir, and the project root a few
    // levels up. Whichever contains map_N.txt wins — so it works no matter where the
    // folder was dropped.
    // Result of the obstacles/ folder search, cached so we only scan the tree once.
    private static string? _obstaclesDir;
    private static bool     _obstaclesDirSearched;

    // Fast candidate locations for the obstacles/ folder: next to the binary, the
    // current working dir, and every ancestor up to 8 levels (covers the solution
    // root and any project folder the folder might have been dropped into).
    private static IEnumerable<string> FastObstacleDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "obstacles");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "obstacles");
        string p = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            p = Path.GetFullPath(Path.Combine(p, ".."));
            yield return Path.Combine(p, "obstacles");
        }
    }

    // Fallback: walk up to the solution/repo root (a *.sln or the GwtEmulator.*
    // project folders), then recursively search that tree for an obstacles/ folder
    // that actually contains map_*.txt. Bounded to the user's project tree, so even
    // if the folder was left in the WRONG project's bin it is still found.
    private static string? FindObstaclesDirDeep()
    {
        try
        {
            string? root = null;
            var cur = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 12 && cur != null; i++)
            {
                if (cur.GetFiles("*.sln").Length > 0
                    || Directory.Exists(Path.Combine(cur.FullName, "GwtEmulator.Server"))
                    || Directory.Exists(Path.Combine(cur.FullName, "GwtEmulator.App")))
                { root = cur.FullName; break; }
                cur = cur.Parent;
            }
            root ??= new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
            if (root == null) return null;
            foreach (var d in Directory.EnumerateDirectories(root, "obstacles", SearchOption.AllDirectories))
                if (Directory.EnumerateFiles(d, "map_*.txt").Any()) return d;
        }
        catch { /* best-effort */ }
        return null;
    }

    private static string? ResolveObstaclesDir()
    {
        if (_obstaclesDirSearched) return _obstaclesDir;
        _obstaclesDirSearched = true;
        foreach (var d in FastObstacleDirs())
        {
            try { if (Directory.Exists(d) && Directory.EnumerateFiles(d, "map_*.txt").Any()) { _obstaclesDir = d; return d; } }
            catch { }
        }
        _obstaclesDir = FindObstaclesDirDeep();
        return _obstaclesDir;
    }

    // Load the bot route graph for a map from routes/map_{id}_routes.txt, which sits next
    // to the obstacles/ folder (or inside it). Empty graph if the file is missing — bots
    // then fall back to the old random patrol.
    private static RouteGraph LoadMapRoutes(int mapId)
    {
        try
        {
            string? od = ResolveObstaclesDir();
            if (od != null)
            {
                // routes/ рядом с obstacles/
                string routesDir = Path.Combine(Path.GetDirectoryName(od.TrimEnd('/', '\\'))!, "routes");
                string f = Path.Combine(routesDir, $"map_{mapId}_routes.txt");
                if (File.Exists(f)) { var g = RouteGraph.LoadOrEmpty(f); Console.WriteLine($"[RS] Loaded route graph map={mapId}: {g.NodeCount} nodes from {f}"); return g; }
                // либо прямо в obstacles/ рядом с боксами
                string f2 = Path.Combine(od, $"map_{mapId}_routes.txt");
                if (File.Exists(f2)) { var g = RouteGraph.LoadOrEmpty(f2); Console.WriteLine($"[RS] Loaded route graph map={mapId}: {g.NodeCount} nodes from {f2}"); return g; }
            }
        }
        catch { }
        return RouteGraph.LoadOrEmpty("");
    }

    // Load building collision boxes for a map. Priority:
    //   1) obstacles/map_{id}.txt found anywhere in the project tree (lets you tune
    //      or replace boxes WITHOUT recompiling — drop the folder in and restart).
    //   2) the boxes embedded in ObstacleBoxes.cs (so collision ALWAYS works, even
    //      with no files at all).
    // Only if BOTH are missing does a map fall back to its hard-coded Obstacles.
    private static ObstacleBox[]? LoadMapObstacles(byte mapId)
    {
        // 1) external file (override)
        try
        {
            string? dir = ResolveObstaclesDir();
            if (dir != null)
            {
                string path = Path.Combine(dir, $"map_{mapId}.txt");
                if (File.Exists(path))
                {
                    var list = new List<ObstacleBox>();
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    foreach (var raw in File.ReadLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#")) continue;
                        var pr = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (pr.Length < 4) continue;
                        if (float.TryParse(pr[0], System.Globalization.NumberStyles.Float, inv, out float x1) &&
                            float.TryParse(pr[1], System.Globalization.NumberStyles.Float, inv, out float z1) &&
                            float.TryParse(pr[2], System.Globalization.NumberStyles.Float, inv, out float x2) &&
                            float.TryParse(pr[3], System.Globalization.NumberStyles.Float, inv, out float z2))
                        {
                            float yBot = -1000f, yTop = 1000f;
                            if (pr.Length >= 6 &&
                                float.TryParse(pr[4], System.Globalization.NumberStyles.Float, inv, out float ya) &&
                                float.TryParse(pr[5], System.Globalization.NumberStyles.Float, inv, out float yb))
                            {
                                yBot = MathF.Min(ya, yb);
                                yTop = MathF.Max(ya, yb);
                            }
                            list.Add(new ObstacleBox(MathF.Min(x1, x2), MathF.Min(z1, z2),
                                                     MathF.Max(x1, x2), MathF.Max(z1, z2),
                                                     yBot, yTop));
                        }
                    }
                    if (list.Count > 0)
                    {
                        PostProcessBridgeObstacles(mapId, list);
                        Console.WriteLine($"[RS] Loaded {list.Count} obstacle boxes for map {mapId} from {path}");
                        return list.ToArray();
                    }
                }
            }
        }
        catch { /* fall through to embedded */ }

        // 2) embedded fallback (no files needed)
        try
        {
            var packed = ObstacleBoxes.Decode(mapId);
            if (packed != null && packed.Count > 0)
            {
                var list = new List<ObstacleBox>(packed.Count);
                foreach (var (x1, z1, x2, z2) in packed)
                    list.Add(new ObstacleBox(MathF.Min(x1, x2), MathF.Min(z1, z2),
                                             MathF.Max(x1, x2), MathF.Max(z1, z2)));
                PostProcessBridgeObstacles(mapId, list);
                Console.WriteLine($"[RS] Loaded {list.Count} obstacle boxes for map {mapId} from EMBEDDED data (no map_{mapId}.txt found)");
                return list.ToArray();
            }
        }
        catch { }

        Console.WriteLine($"[RS] No obstacle boxes for map {mapId} (no file and none embedded).");
        return null;
    }

    // Maps in rotation. Склад removed (terrain issues). Кан (Caen, id=5) added back.
    // MapId 5 = TrainingGround (client-confirmed) — excluded
    // MapId 9 = Кан/Normandy test (based on EnMapName.Normandy=9)
    private static readonly byte[] NormalMapIds  = { 1, 2, 3, 9, 10, 11, 29, 43, 74, 78 }; // +Берлин(3), Ледяной мир(43), Лион(74), Склад(78). Сталинград(30) — только в обороне.
    // MapId=10 turned out NOT to be Stalingrad — the client loads a desert city
    // ("Город в пустыне") for it, so the Stalingrad-terrain spawn I computed was on
    // the wrong map. Defense is back on map 4 (the one the player could actually
    // drive at ground level). Stalingrad exists as a scene, but its network MapId is
    // unknown — use GWT_FORCE_MAP below to find it.
    // Defense now runs on Кан (map 9) — same MapId as Generals/Respawn, so it
    // automatically uses the same spawn coordinates (MapConfig is keyed by MapId).
    private static readonly byte[] DefenseMapIds = { 30 }; // Сталинград — ТОЛЬКО в режиме обороны

    // Diagnostic: set env var GWT_FORCE_MAP=<id> to make EVERY battle load that exact
    // MapId, so you can try ids and see which scene each one is (that's how we pin
    // down which id is really Stalingrad, Berlin, etc.). Unset/invalid = normal random.
    //
    // MAP PROBE: set env var GWT_MAP_PROBE=1 to make each battle load the NEXT id from
    // MapProbeIds in order (battle #1 -> id 1, battle #2 -> id 2, ...), printing a big
    // banner with the id. Play battles back-to-back and write down which id loads
    // Berlin and which loads Stalingrad. Set GWT_MAP_PROBE=0 (or unset) for normal play.
    // Berlin's network id is still unknown (past attempts confused it with Caucasus
    // on ids 2/3). Normandy=29 and Stalingrad=30 were found high, so Berlin is likely
    // nearby — this list probes the unexplored high range. Add/adjust freely.
    private static readonly byte[] MapProbeIds =
        { 27, 28, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 26, 25, 24 };
    private static int _mapProbeIndex = -1;

    private byte PickRandomMap(BattleGameMode mode)
    {
        var forced = Environment.GetEnvironmentVariable("GWT_FORCE_MAP");
        if (byte.TryParse(forced, out var fm) && fm > 0)
        {
            Console.WriteLine($"[RS] GWT_FORCE_MAP active -> loading MapId={fm}");
            return fm;
        }

        var probe = Environment.GetEnvironmentVariable("GWT_MAP_PROBE");
        if (!string.IsNullOrEmpty(probe) && probe != "0")
        {
            int n = Interlocked.Increment(ref _mapProbeIndex);
            byte id = MapProbeIds[((n % MapProbeIds.Length) + MapProbeIds.Length) % MapProbeIds.Length];
            Console.WriteLine("");
            Console.WriteLine("╔════════════════════════════════════════════════════════╗");
            Console.WriteLine($"║  [ПОДБОР КАРТ]  Бой #{n + 1,-3}   ->   MapId = {id,-3}              ║");
            Console.WriteLine($"║  Запиши: этот бой = MapId {id,-3}. Что за карта загрузилась? ║");
            Console.WriteLine($"║  (Берлин? Сталинград? Известные: 1=Москва 2/3=Кавказ    ║");
            Console.WriteLine($"║   9=Кан/Нормандия 10=пустыня — их можно пропускать.)    ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════╝");
            Console.WriteLine("");
            return id;
        }

        return mode == BattleGameMode.Defense
            ? DefenseMapIds[_random.Next(DefenseMapIds.Length)]
            : NormalMapIds[_random.Next(NormalMapIds.Length)];
    }

    // ── obstacle avoidance (hard position rejection) ────────────────────────────
    private const float ObstacleAvoidRadius   = 60f;   // repulsion steering radius
    private const float ObstacleAvoidStrength = 90f;   // max steering deflection
    // If yes, try axis-aligned sliding (X-only or Z-only move); if both blocked, stay put.
    // This is far more reliable than force-based repulsion for high-speed bots.
    //
    private const float BotHullRadius = 1.6f;

    private static bool BlocksTankAtY(in ObstacleBox b, float tankY)
        => b.YTop > tankY + 0.55f && b.YBot < tankY + 2.8f;

    private static bool InsideObstacle(float x, float z, ObstacleBox[] obstacles, float pad = 0f, float? tankY = null)
    {
        foreach (var o in obstacles)
        {
            if (tankY.HasValue && !BlocksTankAtY(in o, tankY.Value)) continue;
            if (x >= o.X1 - pad && x <= o.X2 + pad && z >= o.Z1 - pad && z <= o.Z2 + pad) return true;
        }
        return false;
    }

    // Exact segment-vs-AABB test (Liang–Barsky). Used so a fast bot can't tunnel
    // THROUGH a thin box in one tick: we test the whole swept path old→new, not
    // just the endpoint. A wall segment is ~0.5 units thick, so endpoint-only
    // checks let bots step clean over it — this is why bots drove through walls.
    private static bool ClipSlab(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0f) return q >= 0f;          // parallel to slab: inside only if q>=0
        float r = q / p;
        if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
        else        { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    private static bool SegmentHitsBox(float x1, float z1, float x2, float z2, in ObstacleBox b, float pad = 0f)
    {
        float dx = x2 - x1, dz = z2 - z1;
        float t0 = 0f, t1 = 1f;
        if (!ClipSlab(-dx, x1 - (b.X1 - pad), ref t0, ref t1)) return false;
        if (!ClipSlab( dx, (b.X2 + pad) - x1, ref t0, ref t1)) return false;
        if (!ClipSlab(-dz, z1 - (b.Z1 - pad), ref t0, ref t1)) return false;
        if (!ClipSlab( dz, (b.Z2 + pad) - z1, ref t0, ref t1)) return false;
        return true;
    }

    private static bool SegmentHitsBox3D(
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        in ObstacleBox b)
    {
        float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
        float t0 = 0f, t1 = 1f;
        if (!ClipSlab(-dx, x1 - b.X1,   ref t0, ref t1)) return false;
        if (!ClipSlab( dx, b.X2 - x1,   ref t0, ref t1)) return false;
        if (!ClipSlab(-dy, y1 - b.YBot, ref t0, ref t1)) return false;
        if (!ClipSlab( dy, b.YTop - y1, ref t0, ref t1)) return false;
        if (!ClipSlab(-dz, z1 - b.Z1,   ref t0, ref t1)) return false;
        if (!ClipSlab( dz, b.Z2 - z1,   ref t0, ref t1)) return false;
        return true;
    }

    private static bool SegmentHitsAnyObstacle(float x1, float z1, float x2, float z2, ObstacleBox[] obstacles, float pad = 0f, float? tankY = null)
    {
        foreach (var b in obstacles)
        {
            if (tankY.HasValue && !BlocksTankAtY(in b, tankY.Value)) continue;
            if (SegmentHitsBox(x1, z1, x2, z2, in b, pad)) return true;
        }
        return false;
    }

    // 3D Line-of-sight: checks horizontal bounds and vertical [YBot, YTop] so bridge
    // parapets/walls block horizontal shots while shots can cleanly clear submerged ledges.
    private static bool SegmentBlocked3D(float x1, float y1, float z1, float x2, float y2, float z2, ObstacleBox[] obstacles)
    {
        if (obstacles.Length == 0) return false;
        foreach (var b in obstacles)
            if (SegmentHitsBox3D(x1, y1, z1, x2, y2, z2, in b)) return true;
        return false;
    }

    // Push a point out of any overlapping obstacle box (including tank hull radius)
    // along the shortest exit axis so a tank can never remain clipped or disable collision.
    private static (float x, float z) DepenetrateFromObstacles(
        float x, float z, ObstacleBox[] obstacles, float pad = BotHullRadius, float? tankY = null)
    {
        for (int iter = 0; iter < 3; iter++)
        {
            bool moved = false;
            foreach (var b in obstacles)
            {
                if (tankY.HasValue && !BlocksTankAtY(in b, tankY.Value)) continue;
                float bx1 = b.X1 - pad, bx2 = b.X2 + pad;
                float bz1 = b.Z1 - pad, bz2 = b.Z2 + pad;
                if (x >= bx1 && x <= bx2 && z >= bz1 && z <= bz2)
                {
                    float dLeft  = x - bx1;
                    float dRight = bx2 - x;
                    float dDown  = z - bz1;
                    float dUp    = bz2 - z;
                    float minD   = dLeft;
                    float nx = bx1 - 0.1f, nz = z;
                    if (dRight < minD) { minD = dRight; nx = bx2 + 0.1f; nz = z; }
                    if (dDown  < minD) { minD = dDown;  nx = x; nz = bz1 - 0.1f; }
                    if (dUp    < minD) {                nx = x; nz = bz2 + 0.1f; }
                    x = nx; z = nz;
                    moved = true;
                    break;
                }
            }
            if (!moved) break;
        }
        return (x, z);
    }

    private static (float x, float z) RejectObstacles(
        float curX, float curZ, float newX, float newZ, ObstacleBox[] obstacles, float? tankY = null)
    {
        if (obstacles.Length == 0) return (newX, newZ);
        if (InsideObstacle(curX, curZ, obstacles, BotHullRadius, tankY))
            (curX, curZ) = DepenetrateFromObstacles(curX, curZ, obstacles, BotHullRadius, tankY);

        // Swept hull-padded check: block if the path crosses any box (no wall clipping).
        if (!SegmentHitsAnyObstacle(curX, curZ, newX, newZ, obstacles, BotHullRadius, tankY)) return (newX, newZ);
        // Try sliding along X only
        if (!SegmentHitsAnyObstacle(curX, curZ, newX, curZ, obstacles, BotHullRadius, tankY)) return (newX, curZ);
        // Try sliding along Z only
        if (!SegmentHitsAnyObstacle(curX, curZ, curX, newZ, obstacles, BotHullRadius, tankY)) return (curX, newZ);
        // Fully blocked — stay put at depenetrated position
        return (curX, curZ);
    }

    // Maximum elevation a bot may climb/drop in a single 0.12s tick before the
    // move is treated as blocked, like running into a wall. The hand-drawn
    // ObstacleBox rectangles only cover buildings someone outlined by hand;
    // this catches ANY steep step the height grid has learned about — a real
    // wall base, a cliff edge, the foot of a building — even where no box was
    // drawn, using the same X-only/Z-only slide-or-stop pattern as
    // RejectObstacles so bots skirt the edge instead of snapping back to a
    // single point. Only has data where the player has actually driven
    // nearby (see SampleHeight); elsewhere it's a no-op, same as before.
    // A bot may drive up any slope up to this grade (rise/run). Only terrain
    // STEEPER than this (a near-vertical cliff face) is treated as an impassable
    // wall — everything else, including the mountain slopes, is drivable, so bots
    // climb and drive ON the hills/mountains instead of being penned to the floor.
    // 2.0 ≈ 63°; the hull tilts to match (see TerrainTilt). Lower it only if bots
    // start scaling walls they shouldn't.
    // Ограничение уклона (rise/run) по физике клиента (TankMoveControlNoRigibody: подъём
    // блокируется около 30° = tan 0.577). Крутые склоны гор (> MaxSlopeGrade) непроходимы для
    // ботов как при планировании пути (EnsureNavGrid), так и при шаге вперёд. Для спуска
    // уже оказавшегося на крутом склоне танка разрешается съехать вниз до MaxDropGrade (~51°).
    private const float MaxSlopeGrade = 0.58f;
    private const float MaxDropGrade  = 1.25f;
    private const float MaxClimbPerTick = 18f; // (kept for reference; unused)

    private bool TooSteep(BattleRoom room, float fromX, float fromZ, float toX, float toZ, bool allowDownhillEscape = false)
    {
        float run = MathF.Sqrt((toX - fromX) * (toX - fromX) + (toZ - fromZ) * (toZ - fromZ));
        if (run < 0.01f) return false;
        float dropLimit = allowDownhillEscape ? MaxDropGrade : MaxSlopeGrade;
        bool IsStepSteep(float dy, float stepRun)
            => dy > 0f ? (dy / stepRun) > MaxSlopeGrade : (-dy / stepRun) > dropLimit;

        if (run <= 4.5f)
        {
            float dy = SampleHeight(room, toX, toZ) - SampleHeight(room, fromX, fromZ);
            return IsStepSteep(dy, run);
        }
        int steps = Math.Max(2, (int)MathF.Ceiling(run / 4f));
        float px = fromX, pz = fromZ;
        float py = SampleHeight(room, px, pz);
        float segLen = run / steps;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float nx = fromX + (toX - fromX) * t;
            float nz = fromZ + (toZ - fromZ) * t;
            float ny = SampleHeight(room, nx, nz);
            if (IsStepSteep(ny - py, segLen)) return true;
            px = nx; pz = nz; py = ny;
        }
        return false;
    }

    private const float SolidTankSpacing  = 5.0f;
    private const float CasemateMaxArcDeg = 12f;

    private static float NormalizeAngle180(float deg)
    {
        deg %= 360f;
        if (deg > 180f) deg -= 360f;
        if (deg < -180f) deg += 360f;
        return deg;
    }

    private static bool SegmentHitsCircle(float x1, float z1, float x2, float z2, float cx, float cz, float radius)
    {
        float dx = x2 - x1, dz = z2 - z1;
        float len2 = dx * dx + dz * dz;
        float t = len2 < 1e-6f ? 0f : Math.Clamp(((cx - x1) * dx + (cz - z1) * dz) / len2, 0f, 1f);
        float px = x1 + dx * t - cx;
        float pz = z1 + dz * t - cz;
        return (px * px + pz * pz) < radius * radius;
    }

    private static bool StepHitsTankOrWreck(
        BattleRoom room, RoomPlayer bot,
        float curX, float curZ, float nextX, float nextZ,
        float minDist = SolidTankSpacing)
    {
        float r2 = minDist * minDist;
        foreach (var other in room.Players.Values)
        {
            if (ReferenceEquals(other, bot)) continue;
            if (MathF.Abs(other.PosY - bot.PosY) > 4.0f) continue;
            float ox = other.PosX, oz = other.PosZ;
            float curD2 = (curX - ox) * (curX - ox) + (curZ - oz) * (curZ - oz);
            float nextD2 = (nextX - ox) * (nextX - ox) + (nextZ - oz) * (nextZ - oz);
            if (curD2 >= r2)
            {
                if (SegmentHitsCircle(curX, curZ, nextX, nextZ, ox, oz, minDist))
                    return true;
            }
            else if (nextD2 < curD2)
            {
                // Already touching/overlapping: only block moves that press further in.
                return true;
            }
        }
        return false;
    }

    private (float x, float z) RejectObstaclesAndSteps(
        BattleRoom room, RoomPlayer bot, float curX, float curZ, float curY,
        float newX, float newZ, ObstacleBox[] obstacles, MapConfig cfg)
    {
        (newX, newZ) = RejectObstacles(curX, curZ, newX, newZ, obstacles, curY);
        bool curInHazard = IsNavHazard(room, curX, curZ);

        bool CanMoveTo(float tx, float tz)
        {
            if (tx < cfg.XMin + 5f || tx > cfg.XMax - 5f || tz < cfg.ZMin + 5f || tz > cfg.ZMax - 5f)
                return false;
            if (!curInHazard && IsNavHazard(room, tx, tz))
                return false;
            if (obstacles.Length != 0 && SegmentHitsAnyObstacle(curX, curZ, tx, tz, obstacles, BotHullRadius, curY))
                return false;
            if (TooSteep(room, curX, curZ, tx, tz, allowDownhillEscape: true))
                return false;
            if (StepHitsTankOrWreck(room, bot, curX, curZ, tx, tz))
                return false;
            return true;
        }

        if (CanMoveTo(newX, newZ)) return (newX, newZ);
        if (CanMoveTo(newX, curZ)) return (newX, curZ);
        if (CanMoveTo(curX, newZ)) return (curX, newZ);

        // Try gentle +/-45 deg tangential slide so tanks flow smoothly around allies/wrecks.
        float stepX = newX - curX, stepZ = newZ - curZ;
        float stepLen = MathF.Sqrt(stepX * stepX + stepZ * stepZ);
        if (stepLen > 0.01f)
        {
            float ux = stepX / stepLen, uz = stepZ / stepLen;
            const float c45 = 0.7071f, s45 = 0.7071f;
            float rx1 = (ux * c45 - uz * s45) * (stepLen * 0.7f);
            float rz1 = (ux * s45 + uz * c45) * (stepLen * 0.7f);
            if (CanMoveTo(curX + rx1, curZ + rz1)) return (curX + rx1, curZ + rz1);
            float rx2 = (ux * c45 + uz * s45) * (stepLen * 0.7f);
            float rz2 = (-ux * s45 + uz * c45) * (stepLen * 0.7f);
            if (CanMoveTo(curX + rx2, curZ + rz2)) return (curX + rx2, curZ + rz2);
        }
        return (curX, curZ);
    }

    // Steer AROUND obstacles / steep terrain. Tries the straight heading first,
    // then fans out left/right in increasing angles until it finds a direction that
    // is neither blocked by a box (swept) nor too steep.
    private static readonly float[] SteerAngles =
        { 0f, 20f, -20f, 40f, -40f, 60f, -60f, 90f, -90f, 130f, -130f, 160f, -160f };

    private (float x, float z) SteerAround(
        BattleRoom room, float curX, float curZ,
        float dirX, float dirZ, float step,
        float xMin, float xMax, float zMin, float zMax,
        ObstacleBox[] obstacles)
    {
        if (step < 0.0001f) return (curX, curZ);
        float tankY = SampleHeight(room, curX, curZ);
        if (obstacles.Length != 0 && InsideObstacle(curX, curZ, obstacles, BotHullRadius, tankY))
            (curX, curZ) = DepenetrateFromObstacles(curX, curZ, obstacles, BotHullRadius, tankY);

        foreach (float deg in SteerAngles)
        {
            float a  = deg * (MathF.PI / 180f);
            float ca = MathF.Cos(a), sa = MathF.Sin(a);
            float rx = dirX * ca - dirZ * sa;
            float rz = dirX * sa + dirZ * ca;
            float nx = Math.Clamp(curX + rx * step, xMin, xMax);
            float nz = Math.Clamp(curZ + rz * step, zMin, zMax);
            bool boxBlocked = obstacles.Length != 0
                              && SegmentHitsAnyObstacle(curX, curZ, nx, nz, obstacles, BotHullRadius, tankY);
            if (!boxBlocked && !TooSteep(room, curX, curZ, nx, nz))
                return (nx, nz);
        }
        return (curX, curZ);
    }

    // Cast forward feeler rays around desiredYaw with short-term side hysteresis so tanks
    // turn smoothly around upcoming corners, allies, dead wrecks, and hazards without oscillating.
    private float ChooseSteerYawOffset(
        BattleRoom room, RoomPlayer bot, float baseYawDeg, float lookDist, ObstacleBox[] obstacles, MapConfig cfg)
    {
        bool curInHazard = IsNavHazard(room, bot.PosX, bot.PosZ);

        bool ClearAt(float degOffset)
        {
            float rad = (baseYawDeg + degOffset) * (MathF.PI / 180f);
            float px = bot.PosX + MathF.Sin(rad) * lookDist;
            float pz = bot.PosZ + MathF.Cos(rad) * lookDist;
            if (px < cfg.XMin + 5f || px > cfg.XMax - 5f || pz < cfg.ZMin + 5f || pz > cfg.ZMax - 5f)
                return false;
            if (!curInHazard && IsNavHazard(room, px, pz))
                return false;
            if (obstacles.Length != 0 && SegmentHitsAnyObstacle(bot.PosX, bot.PosZ, px, pz, obstacles, BotHullRadius, bot.PosY))
                return false;
            if (SegmentTooSteep(room, bot.PosX, bot.PosZ, px, pz))
                return false;
            if (StepHitsTankOrWreck(room, bot, bot.PosX, bot.PosZ, px, pz, SolidTankSpacing - 0.3f))
                return false;
            return true;
        }

        if (ClearAt(0f))
            return 0f;

        int preferredSide = (DateTime.UtcNow < bot.SteerSideUntil && bot.SteerSideBias != 0)
            ? bot.SteerSideBias
            : ((bot.PlayerGUID % 2 == 0) ? 1 : -1);

        float[] mags = { 25f, 45f, 70f, 95f };
        foreach (float mag in mags)
        {
            float first = preferredSide * mag;
            if (ClearAt(first))
            {
                bot.SteerSideBias = preferredSide;
                bot.SteerSideUntil = DateTime.UtcNow.AddSeconds(1.5);
                return first;
            }
            float second = -preferredSide * mag;
            if (ClearAt(second))
            {
                bot.SteerSideBias = -preferredSide;
                bot.SteerSideUntil = DateTime.UtcNow.AddSeconds(1.5);
                return second;
            }
        }
        return 0f;
    }

    // Solid hard-body separation against living tanks AND dead wrecks so tanks never stack
    // or drive through one another; tracked tanks stay pinned and never get pushed into hazards.
    private void SeparateFromTanks(BattleRoom room, RoomPlayer bot, ObstacleBox[] obstacles, MapConfig cfg)
    {
        if (bot.TrackImmobilized) return;
        float pushX = 0f, pushZ = 0f;
        foreach (var other in room.Players.Values)
        {
            if (ReferenceEquals(other, bot)) continue;
            if (MathF.Abs(other.PosY - bot.PosY) > 4.0f) continue;
            float dx = bot.PosX - other.PosX;
            float dz = bot.PosZ - other.PosZ;
            float d2 = dx * dx + dz * dz;
            if (d2 >= SolidTankSpacing * SolidTankSpacing) continue;
            float d = MathF.Sqrt(d2);
            float overlap = SolidTankSpacing - d;
            float scale = other.IsDead || !other.IsBot || other.TrackImmobilized ? 1.0f : 0.55f;
            if (d < 0.05f)
            {
                float angle = (bot.PlayerGUID % 16) * (MathF.PI / 8f);
                pushX += MathF.Sin(angle) * overlap * scale;
                pushZ += MathF.Cos(angle) * overlap * scale;
            }
            else
            {
                float amt = Math.Min(overlap * scale, 1.4f);
                pushX += (dx / d) * amt;
                pushZ += (dz / d) * amt;
            }
        }
        if (pushX == 0f && pushZ == 0f) return;
        float nx = Math.Clamp(bot.PosX + pushX, cfg.XMin + 5f, cfg.XMax - 5f);
        float nz = Math.Clamp(bot.PosZ + pushZ, cfg.ZMin + 5f, cfg.ZMax - 5f);
        bool curInHazard = IsNavHazard(room, bot.PosX, bot.PosZ);
        if ((curInHazard || !IsNavHazard(room, nx, nz))
            && !SegmentHitsAnyObstacle(bot.PosX, bot.PosZ, nx, nz, obstacles, BotHullRadius, bot.PosY)
            && !InsideObstacle(nx, nz, obstacles, BotHullRadius, bot.PosY)
            && !TooSteep(room, bot.PosX, bot.PosZ, nx, nz, allowDownhillEscape: true))
        {
            bot.PosX = nx;
            bot.PosZ = nz;
        }
    }

    // Finds the earliest obstacle/terrain impact point along a shot ray when LOS became blocked in flight.
    private (float x, float y, float z) FindFirstImpactPoint(
        BattleRoom room, float x1, float y1, float z1, float x2, float y2, float z2, ObstacleBox[] obstacles)
    {
        float bestT = 1f;
        float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
        foreach (var b in obstacles)
        {
            float t0 = 0f, t1 = 1f;
            if (ClipSlab(-dx, x1 - b.X1,   ref t0, ref t1)
                && ClipSlab(dx, b.X2 - x1,   ref t0, ref t1)
                && ClipSlab(-dy, y1 - b.YBot, ref t0, ref t1)
                && ClipSlab(dy, b.YTop - y1, ref t0, ref t1)
                && ClipSlab(-dz, z1 - b.Z1,   ref t0, ref t1)
                && ClipSlab(dz, b.Z2 - z1,   ref t0, ref t1)
                && t0 < bestT)
            {
                bestT = Math.Max(0.02f, t0);
            }
        }
        float len = MathF.Sqrt(dx * dx + dz * dz);
        int steps = Math.Max(4, (int)(len / 3f));
        for (int i = 1; i < steps; i++)
        {
            float t = (float)i / steps;
            if (t >= bestT) break;
            float sx = x1 + dx * t;
            float sz = z1 + dz * t;
            float sy = y1 + (y2 - y1) * t;
            float g = GroundAt(room, sx, sz, out bool known);
            if (known && g > sy + 0.65f)
            {
                bestT = t;
                break;
            }
        }
        return (x1 + dx * bestT, y1 + (y2 - y1) * bestT, z1 + dz * bestT);
    }

    // Scan 16 directions around a point and return a unit heading toward the most
    // OPEN space — the direction whose straight path stays clear of boxes/steep
    // terrain for the longest distance. Used to break a wedged bot out of a pocket
    // (it drives toward daylight instead of grinding into the wall it's stuck on).
    // Tie-breaks are randomised so a cluster of stuck bots fans out instead of all
    // picking the same escape lane. Falls back to a random heading if nothing is open.
    private (float x, float z) FindOpenDirection(BattleRoom room, RoomPlayer bot, float x, float z, ObstacleBox[] obstacles, MapConfig cfg)
    {
        const int probes = 16;
        const float reach = 45f, stepLen = 5f;
        float startY = SampleHeight(room, x, z);
        bool curInHazard = IsNavHazard(room, x, z);
        float bestClear = -1f, bestX = 0f, bestZ = 0f;
        int start = _random.Next(probes);
        for (int k = 0; k < probes; k++)
        {
            int idx = (start + k) % probes;
            float a = idx * (2f * MathF.PI / probes);
            float dxu = MathF.Sin(a), dzu = MathF.Cos(a);
            float clear = reach;
            for (float d = stepLen; d <= reach; d += stepLen)
            {
                float px = x + dxu * d, pz = z + dzu * d;
                if (px < cfg.XMin + 5f || px > cfg.XMax - 5f || pz < cfg.ZMin + 5f || pz > cfg.ZMax - 5f
                    || (!curInHazard && IsNavHazard(room, px, pz))
                    || (obstacles.Length != 0 && SegmentHitsAnyObstacle(x, z, px, pz, obstacles, BotHullRadius, startY))
                    || TooSteep(room, x, z, px, pz, allowDownhillEscape: true)
                    || StepHitsTankOrWreck(room, bot, x, z, px, pz))
                { clear = d - stepLen; break; }
            }
            if (clear > bestClear) { bestClear = clear; bestX = dxu; bestZ = dzu; }
        }
        if (bestClear <= 0f)
        {
            float cx = (cfg.XMin + cfg.XMax) * 0.5f - x;
            float cz = (cfg.ZMin + cfg.ZMax) * 0.5f - z;
            float cl = MathF.Sqrt(cx * cx + cz * cz);
            if (cl > 1f) return (cx / cl, cz / cl);
            float a = (float)(_random.NextDouble() * 2.0 * Math.PI);
            return (MathF.Sin(a), MathF.Cos(a));
        }
        return (bestX, bestZ);
    }

    private (float dx, float dz) ComputeObstacleAvoidance(
        float posX, float posZ, ObstacleBox[] obstacles, float moveDx, float moveDz)
    {
        // Keep a soft repulsion for steering direction, but the hard rejection below
        // is what actually prevents wall clipping.
        float repX = 0, repZ = 0;
        foreach (var obs in obstacles)
        {
            float cx = Math.Clamp(posX, obs.X1, obs.X2);
            float cz = Math.Clamp(posZ, obs.Z1, obs.Z2);
            float dx = posX - cx, dz = posZ - cz;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 0.1f) dist = 0.1f;
            if (dist < ObstacleAvoidRadius)
            {
                float strength = (1f - dist / ObstacleAvoidRadius) * ObstacleAvoidStrength;
                repX += (dx / dist) * strength;
                repZ += (dz / dist) * strength;
            }
        }
        return (moveDx + repX, moveDz + repZ);
    }

    // ── spotting system ────────────────────────────────────────────────────────
    //
    // A tank is "spotted" by the opposing team if it is within spot range of
    // ANY living tank on the spotter's side with a clear LOS (or within ProxySpotRange).
    // Firing temporarily increases detection radius; multi-ray hull/flank checks prevent
    // building corners from hiding half-exposed tanks.
    //
    private static bool SegmentBlocked3DForSight(
        float x1, float y1, float z1,
        float x2, float y2, float z2,
        ObstacleBox[] obstacles)
    {
        if (obstacles.Length == 0) return false;
        float dx = x2 - x1, dy = y2 - y1, dz = z2 - z1;
        float rayLen = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        foreach (var b in obstacles)
        {
            float t0 = 0f, t1 = 1f;
            if (!ClipSlab(-dx, x1 - b.X1,   ref t0, ref t1)) continue;
            if (!ClipSlab( dx, b.X2 - x1,   ref t0, ref t1)) continue;
            if (!ClipSlab(-dy, y1 - b.YBot, ref t0, ref t1)) continue;
            if (!ClipSlab( dy, b.YTop - y1, ref t0, ref t1)) continue;
            if (!ClipSlab(-dz, z1 - b.Z1,   ref t0, ref t1)) continue;
            if (!ClipSlab( dz, b.Z2 - z1,   ref t0, ref t1)) continue;
            // Ignore thin outer-margin overlap at spotter's own origin (human tanks use Unity mesh
            // colliders and aren't depenetrated from voxel boxes, so hugging a wall/parapet would
            // otherwise blind them 360 degrees), while still blocking sight through buildings.
            if (t0 <= 0.0001f && t1 * rayLen <= 2.4f) continue;
            return true;
        }
        return false;
    }

    private const float SpotRange      = 320f; // fallback view range (world units)
    private const float ProxySpotRange = 50f;  // X-ray auto-spot through walls/hills
    private const double FireRevealSec = 3.0;  // gun flash expands detection range
    private const double SpotHoldSec   = 3.5;  // linger after losing LOS/range (kills flicker)

    private bool HasSpotLineOfSight(BattleRoom room, RoomPlayer spotter, RoomPlayer target, ObstacleBox[] obstacles)
    {
        float sy = spotter.PosY + 2.2f;
        float ty = target.PosY + 2.1f;
        if (!SegmentBlocked3DForSight(spotter.PosX, sy, spotter.PosZ, target.PosX, ty, target.PosZ, obstacles)
            && !TerrainBlocksShot(room, spotter.PosX, spotter.PosZ, sy, target.PosX, target.PosZ, ty))
            return true;

        // Check left/right hull/turret edges perpendicular to the sightline so a tank
        // peeking halfway past a building corner is still spotted.
        float dx = target.PosX - spotter.PosX;
        float dz = target.PosZ - spotter.PosZ;
        float len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1f) return true;
        float px = -dz / len * 1.5f;
        float pz =  dx / len * 1.5f;

        if (!SegmentBlocked3DForSight(spotter.PosX, sy, spotter.PosZ, target.PosX + px, ty, target.PosZ + pz, obstacles)
            && !TerrainBlocksShot(room, spotter.PosX, spotter.PosZ, sy, target.PosX + px, target.PosZ + pz, ty))
            return true;
        if (!SegmentBlocked3DForSight(spotter.PosX, sy, spotter.PosZ, target.PosX - px, ty, target.PosZ - pz, obstacles)
            && !TerrainBlocksShot(room, spotter.PosX, spotter.PosZ, sy, target.PosX - px, target.PosZ - pz, ty))
            return true;

        // Commander cupola / turret-roof sightline so hull-down tanks and crest peeks spot properly.
        float syHigh = spotter.PosY + 2.95f;
        float tyHigh = target.PosY + 2.75f;
        if (!SegmentBlocked3DForSight(spotter.PosX, syHigh, spotter.PosZ, target.PosX, tyHigh, target.PosZ, obstacles)
            && !TerrainBlocksShot(room, spotter.PosX, spotter.PosZ, syHigh, target.PosX, target.PosZ, tyHigh))
            return true;

        return false;
    }

    private float ComputeHumanDetectRange(float rawView, long playerGuid, long tankGuid)
    {
        float mult = BotCombatProfiles.VisionScale * 1.15f;
        if (_crewRepo != null && tankGuid != 0)
        {
            try
            {
                var crew = _crewRepo.GetPlayerCrew(playerGuid)
                                    .FirstOrDefault(c => c.TankGuid == tankGuid);
                int crewLv = crew?.CurLv ?? 0;
                if (crewLv > 0)
                    mult *= 1f + Math.Min(crewLv, 12) * 0.015f;
            }
            catch { }
        }
        float range = Math.Clamp(rawView * mult, BotCombatProfiles.MinDetect, 420f);
        Console.WriteLine($"[RS] PlayerDetectRange: base={rawView:F0}, effective={range:F0}");
        return range;
    }

    private bool IsSpotDetectedBy(RoomPlayer spotter, RoomPlayer target, BattleRoom room, ObstacleBox[] obstacles, DateTime now)
    {
        float dist = Distance2D(spotter, target);
        if (dist <= ProxySpotRange) return true;

        // Smoke clouds conceal targets outside proxy-spot distance (50m).
        if (IsConcealedBySmoke(room, target.PosX, target.PosZ, now) || IsConcealedBySmoke(room, spotter.PosX, spotter.PosZ, now))
            return false;

        float range = spotter.DetectRange > 0f ? spotter.DetectRange : SpotRange;
        if (target.HideFactor > 0f)
            range *= Math.Max(0.45f, 1f - target.HideFactor * 0.5f);
        if ((now - target.LastFireTime).TotalSeconds <= FireRevealSec)
            range = MathF.Max(range * 1.35f, 360f);

        if (dist > range) return false;
        return HasSpotLineOfSight(room, spotter, target, obstacles);
    }

    private static bool IsConcealedBySmoke(BattleRoom room, float x, float z, DateTime now)
    {
        if (room.ActiveSmokes.IsEmpty) return false;
        foreach (var cloud in room.ActiveSmokes.Values)
        {
            if (now >= cloud.ExpiresAt) continue;
            float rSq = cloud.Radius * cloud.Radius;
            for (int i = 0; i < cloud.Centers.Length; i++)
            {
                float dx = x - cloud.Centers[i].X;
                float dz = z - cloud.Centers[i].Z;
                if (dx * dx + dz * dz <= rSq) return true;
            }
        }
        return false;
    }

    private bool IsSpottedBy(RoomPlayer target, byte spotterGroup, BattleRoom room)
    {
        var obstacles = (room.FileObstacles != null && room.FileObstacles.Length > 0)
            ? room.FileObstacles : GetMapConfig(room.MapId).Obstacles;
        var now = DateTime.UtcNow;
        foreach (var p in room.Players.Values)
        {
            if (p.IsDead || p.Group != spotterGroup) continue;
            if (IsSpotDetectedBy(p, target, room, obstacles, now))
                return true;
        }
        return false;
    }

    // ── matchmaking tier spread ───────────────────────────────────────────────
    //
    // Like real GWT/WoT matchmaking, the player can end up in one of three
    // "positions" relative to the battle tier bracket:
    //
    //   TOP    (33%) — player is the highest tier; enemies are same tier or 1 below.
    //                  A tier-8 player sees tier 7-8 enemies.
    //   MID    (34%) — player faces tanks up to 1 tier higher.
    //                  A tier-8 player sees tier 7-9 enemies.
    //   BOTTOM (33%) — player is near the lowest tier; enemies can be 2 tiers higher.
    //                  A tier-8 player sees tier 8-10 enemies.
    //
    // The player's OWN team mirrors the same tier bracket so the fight is symmetric.
    // Premium/special tanks and TDs are deliberately included — they spice up the pool.
    //
    public enum TierPosition { Top, Mid, Bottom }

    // v55 (п.5): Id танков БЕЗ модели-бандла в клиенте (папка AssetBundles/3/{Id} отсутствует в
    // манифесте). Это 24 placeholder-танка тира 1 с пустыми именами. Если такой танк попадал боту,
    // клиент падал на загрузке боя («не удалось загрузить ресурсы игры») — особенно на тир 1-2, где
    // окно подбирает низкоуровневые танки. Исключаем их из любого выбора танков для ботов.
    private static readonly HashSet<int> NoModelTankIds = new()
    {
        12, 20, 21, 24, 26, 29, 32, 33, 34, 35, 38, 39, 49, 53, 55, 58, 59, 60, 67, 69, 72, 74, 75, 76
    };

    // v70: разброс уровней ВРАГОВ в «Обороне». Раньше расписание волн (PveWaveTanks)
    // было ФИКСИРОВАНО по ID независимо от уровня танка игрока — на КВ-1 (ур.5) и на
    // Т-10 (ур.10) прилетали одни и те же Пантера II/Е-50/Леопард 1/Маус. Теперь
    // диапазон уровней врагов считается от уровня(ей) союзника(ов)-людей в бою:
    //   loEnemy = min(уровни союзников) − EnemyTierSpreadDown
    //   hiEnemy = max(уровни союзников) + EnemyTierSpreadUp
    // Пример из ТЗ: А-20 (ур.4) соло → диапазон [3..5]. Если в союзниках вперемешку
    // 3 и 5 уровень — диапазон [2..6] (мин.союзников−1, макс.союзников+1).
    // Значения ниже — единственное место, которое нужно трогать, если нужен другой
    // разброс (например, несимметричный «+1/−3»: поставьте Down=3, Up=1).
    private const int EnemyTierSpreadDown = 1;
    private const int EnemyTierSpreadUp   = 1;
    private const int GermanNationId      = 2; // Nation=2 в client_game_tables.json — немецкая ветка (PzI..Maus/E-100/E-90/Leopard1/JgPz E-100 и ПТ-ветка)

    // Случайный НЕМЕЦКИЙ прокачиваемый танк (Nation==2) в диапазоне уровней [lo..hi].
    // Используется волнами обороны вместо старого фиксированного списка ID.
    private TankEntry? PickGermanTankInTierRange(int lo, int hi)
    {
        var cands = _gameTables.Tanks.Values
            .Where(t => t.Nation == GermanNationId
                     && t.Tier >= lo && t.Tier <= hi
                     && t.HullId > 0 && t.MainGunId > 0
                     && !string.IsNullOrEmpty(t.Name)
                     && !NoModelTankIds.Contains(t.Id))
            .ToList();
        if (cands.Count == 0)
        {
            // Диапазон промахнулся мимо реальных тиров (напр. пустая дырка в таблице) —
            // берём любой немецкий танк, лишь бы бой не остался без противников.
            cands = _gameTables.Tanks.Values
                .Where(t => t.Nation == GermanNationId && t.HullId > 0 && t.MainGunId > 0
                         && !string.IsNullOrEmpty(t.Name) && !NoModelTankIds.Contains(t.Id))
                .ToList();
        }
        return cands.Count > 0 ? cands[_random.Next(cands.Count)] : null;
    }

    private (List<TankEntry> allyPool, List<TankEntry> enemyPool, TierPosition pos)
        BuildMatchmakingPools(int playerTier)
    {
        // v48.1: подбор союзников и врагов СТРОГО в диапазоне ±2 от уровня игрока
        // (клампится в [1..11]). Требования игрока:
        //   • разница максимум ±2 (X видит VIII..XI, но НЕ VI/VII; IX видит VII..XI);
        //   • примерно ПОЛОВИНА техники врага — в пределах ±1 от игрока, вторая половина — на ±2.
        // Веса по смещению уровня (delta → вес): −2:3, −1:2, 0:2, +1:2, +2:3. Тогда доля
        // техники в пределах ±1 (delta −1..+1) = 6/12 = ~50%, на краях ±2 = 6/12 = ~50%.
        // Кратность = вес: бот берёт танк как pool[Random], поэтому больше копий = выше шанс.
        var tierWeights = new (int delta, int weight)[] { (-2, 3), (-1, 2), (0, 2), (+1, 2), (+2, 3) };

        List<TankEntry> BuildRangePool()
        {
            var p = new List<TankEntry>();
            foreach (var (delta, weight) in tierWeights)
            {
                int tier = playerTier + delta;
                if (tier < 1 || tier > 11) continue;      // клампим края: у X нет +2, у I нет −2
                var atTier = _gameTables.Tanks.Values.Where(t => t.Tier == tier && !NoModelTankIds.Contains(t.Id)).ToList();
                for (int w = 0; w < weight; w++) p.AddRange(atTier);
            }
            return p;
        }

        var allyPool  = BuildRangePool();
        var enemyPool = allyPool;   // обе команды по одному правилу вокруг уровня игрока; сам игрок — своего

        // Fallback, если по какой-то причине пул пуст.
        if (allyPool.Count == 0)
        {
            allyPool  = _gameTables.Tanks.Values.Where(t => !NoModelTankIds.Contains(t.Id)).ToList();
            enemyPool = allyPool;
        }

        var pos = TierPosition.Mid;   // оставлено для совместимости с логом вызывающего кода
        int loTier = Math.Max(1, playerTier - 2), hiTier = Math.Min(11, playerTier + 2);
        Console.WriteLine($"[RS] Matchmaking v48.1: playerTier={playerTier} диапазон ±2 [{loTier}..{hiTier}], " +
                          $"~50% в пределах ±1, ~50% на ±2; пул={allyPool.Count} записей (с весами)");
        return (allyPool, enemyPool, pos);
    }

    // Legacy single-pool helper kept for any other callers.
    private List<TankEntry> PickBotTankPool(int playerTier)
    {
        var (ally, _, _) = BuildMatchmakingPools(playerTier);
        return ally;
    }

    // v48.5: ОКНО уровней боя с максимальным разбросом 2. Игрок уточнил: в возрождении,
    // генералах и захвате «максимальная разница между самым высоким и низким уровнем = 2».
    // Окно — ровно 3 уровня [lo..lo+2], всегда содержит уровень игрока. Смещение окна случайно:
    // игрок может оказаться СВЕРХУ окна (враги/союзники до 2 НИЖЕ), в ЦЕНТРЕ (±1) или СНИЗУ
    // (до 2 ВЫШЕ). Так выполняется и «может встретиться и +2, и −2 от игрока» (в разных боях),
    // и разброс уровней ВНУТРИ одного боя не превышает 2.
    private (int lo, int hi) RollTierWindow(int playerTier)
    {
        int offset = _random.Next(3);          // 0..2: игрок сверху / в центре / снизу окна
        int lo = playerTier - offset, hi = lo + 2;
        if (lo < 1)  { lo = 1;  hi = 3;  }      // клампим края шкалы [1..11], игрок остаётся в окне
        if (hi > 11) { hi = 11; lo = 9;  }
        return (lo, hi);
    }

    // Случайный танк в диапазоне уровней [lo..hi] — равновероятно среди всех подходящих
    // (валидные корпус/орудие). Ролл уровня отделён от выбора модели, поэтому распределение не
    // зависит от того, сколько моделей в игре на каждом уровне.
    private TankEntry? PickTankInTierRange(int lo, int hi)
    {
        var cands = _gameTables.Tanks.Values
            .Where(t => t.Tier >= lo && t.Tier <= hi && t.HullId > 0 && t.MainGunId > 0
                        && !NoModelTankIds.Contains(t.Id)).ToList();
        return cands.Count > 0 ? cands[_random.Next(cands.Count)] : null;
    }

    // v48 (задача 5): доступные варианты модуля данного типа для танка — стоковый + все узлы
    // дерева исследований этого типа (PartType) + вариант из модификации (для башни — визуал
    // use_turret, для орудия — gun). Тип: 4 = башня, 5 = орудие. Возвращает непустой список.
    // v48.8: погода по КАРТЕ по РЕАЛЬНОЙ таблице Table_Weather (вытащена из level0).
    // WeatherEffectType — это ID СТРОКИ таблицы (клиент делает GetWeatherTable(id)), а не «тип».
    // Для Сталинграда: ID 17 = «Stalingrad _ Snow» (эффект снега 306), ID 16 = «Stalingrad _
    // normal» (ясно). Раньше слали 2 = «Caucasian _ normal» — пустышка, поэтому снега не было.
    private const byte StalingradSnowWeatherId  = 17;
    private const byte StalingradClearWeatherId = 16;
    private static (byte type, float intensity) GetMapWeather(int mapId)
    {
        switch (mapId)
        {
            case 30: return (StalingradSnowWeatherId, 1.0f);   // Сталинград (оборона) — снег (строка 17)
            // v61 (пункт 5): для обычных карт шлём ЯСНУЮ погоду (id 16), а НЕ 0.
            // id 0 — невалидная строка Table_Weather: клиент пишет "The weather of id::0
            // from server is invalid!!" и не применяет ясную погоду → сцена остаётся в
            // дефолтном тумане (белёсый экран в режиме захвата на всех картах).
            default: return (StalingradClearWeatherId, 0f);
        }
    }

    // v49: время суток (Table_Time_type) ПО КАРТЕ. Значения — id строк Table_Time_type,
    // подтверждённые из level0 по полю `des` и по Table_Battlemap.use_time_type (формат
    // «день_ночь»). Ночь = skybox_type 1 (тёмное небо), день = skybox_type 0 (яркое).
    //   NIGHT: Москва(1)->1 «Moscow night», Кан(9)->4 «Caen night»,
    //          Сталинград(30)->1 (своего ночного пресета у карты НЕТ — use_time_type=«17»
    //          только день; берём нейтральную ночь id 1, свет совпадает со снежной ночью).
    //   DAY/УТРО: Закавказье(2)->8, Берлин(3)->9, Горящие пески(11)->11, Нормандия(29)->15,
    //          Лион(74)->36, Склад/Factory(78)->38. Отдельного «утреннего» пресета в данных
    //          почти нет (только AlKarfag sunrise=35, чужой скайбокс) — поэтому «утро» = дневной
    //          яркий пресет каждой карты (это и есть утренний дневной свет).
    //   Не в списке Billy: Пустыня(10)->11, Ледяной(43)->27 — их дневной пресет (не глобальная ночь).
    private static byte GetMapTimeType(int mapId) => mapId switch
    {
        1  => 1,    // Москва — ночь
        9  => 4,    // Кан — ночь
        30 => 1,    // Сталинград — ночь (нет своего ночного пресета, нейтральная ночь)
        2  => 8,    // Закавказье — день/утро
        3  => 9,    // Берлин — день/утро
        11 => 11,   // Горящие пески — день/утро
        29 => 15,   // Нормандия — день/утро
        74 => 36,   // Лион — день/утро
        78 => 38,   // Склад — день/утро
        10 => 11,   // Город в пустыне — день (не в списке)
        43 => 27,   // Ледяной мир — день (не в списке)
        _  => 7,    // прочие — нейтральный день (Moscow day)
    };

    private List<int> CollectModuleOptions(int tankId, int moduleType, int baseId)
    {
        var set = new HashSet<int>();
        if (baseId > 0) set.Add(baseId);
        foreach (var r in _gameTables.GetResearchForTank(tankId))
            if (r.PartType == moduleType && r.PartId > 0)
                set.Add(r.PartId);
        foreach (var tr in _gameTables.GetTransformsForTank(tankId))
        {
            // v49 (задача 2): КОРПУС модификации (use_hull) — чтобы у бота, взявшего модификацию,
            // менялась броня (ResolveHit берёт броню по HullID). У большинства модификаций use_hull
            // по статам совпадает с базовым (косметика) — влияет только там, где реально отличается.
            if (moduleType == 1 && tr.UseHull   > 0) set.Add(tr.UseHull);
            if (moduleType == 1 && tr.Hull      > 0) set.Add(tr.Hull);
            if (moduleType == 4 && tr.UseTurret > 0) set.Add(tr.UseTurret);
            if (moduleType == 4 && tr.Turret    > 0) set.Add(tr.Turret);
            if (moduleType == 5 && tr.Gun       > 0) set.Add(tr.Gun);
        }
        var list = set.ToList();
        if (list.Count == 0) list.Add(baseId);   // фолбэк: хотя бы стоковый (даже если 0)
        return list;
    }

    // ── constructor ────────────────────────────────────────────────────────────
    public RoomServer(int port, DatabaseService db, TankRepository tankRepo,
        BattleStatsRepository battleStatsRepo, GameTablesService gameTables,
        AccountRepository? accountRepo = null,
        CrewRepository? crewRepo = null,
        TankEquipmentRepository? equipRepo = null,
        ModeStatsRepository? modeStatsRepo = null) : base("RS", port)
    {
        _db = db; _tankRepo = tankRepo; _battleStatsRepo = battleStatsRepo;
        _gameTables = gameTables; _accountRepo = accountRepo;
        _crewRepo = crewRepo; _equipRepo = equipRepo;
        _modeStatsRepo = modeStatsRepo;
        Console.WriteLine("[RS] Build marker: v23 " +
            "(броня 290/290 корпусов, экран очереди обороны, умный фокус огня, " +
            "ромбование корпуса, защита от неестественного наклона)");
        Console.WriteLine("[RS] v29: адреналин считается от перезарядки С БОНУСАМИ экипажа и оснащения");
    }

    protected override GameSession CreateSession(TcpClient client) => new(client);
    protected override void OnClientConnected(GameSession session) { }

    // ─────────────────────────── message dispatch ──────────────────────────────
    protected override void OnMessageReceived(GameSession session, IRequest message)
    {
        try
        {
        Console.WriteLine($"[RS] Received: {message.MsgType} from {session.PlayerGUID}");
        switch (message.MsgType)
        {
            case TkMsgType.NM_CR_PlayerLogin:         HandleRoomLogin(session, (PK_CR_PlayerLogin)message); break;
            case TkMsgType.NM_CR_Ping:                HandlePing(session, (PK_CR_Ping)message); break;
            case TkMsgType.NM_CR_SelfTankMove:        HandleTankMove(session, (PK_CR_SelfTankMove)message); break;
            case TkMsgType.NM_CR_TankFire:            HandleTankFire(session, (PK_CR_TankFire)message); break;
            case TkMsgType.NM_CR_ShellHit:            HandleShellHit(session, (PK_CR_ShellHit)message); break;
            case TkMsgType.NM_CR_ShellDecal:          HandleShellDecal(session, (PK_CR_ShellDecal)message); break;
            case TkMsgType.NM_CR_PlayerLogout:        HandlePlayerLogout(session); break;
            case TkMsgType.NM_CR_Chat:                HandleChat(session, (PK_CR_Chat)message); break;
            case TkMsgType.NM_CR_FastChat:            HandleFastChat(session, (PK_CR_FastChat)message); break;
            case TkMsgType.NM_CR_LittleMapDictated:   HandleLittleMap(session, (PK_CR_LittleMapDictated)message); break;
            case TkMsgType.NM_CR_SelfTankGunTurrentState: HandleGunTurretState(session, (PK_CR_SelfTankGunTurrentState)message); break;
            case TkMsgType.NM_CR_SelfTankMoveDictate: HandleTankMoveDictate(session, (PK_CR_SelfTankMoveDictate)message); break;
            case TkMsgType.NM_CR_AddClipFire:         HandleAddClipFire(session, (PK_CR_AddClipFire)message); break;
            case TkMsgType.NM_CR_RequestFire:         HandleRequestFire(session, (PK_CR_RequestFire)message); break;
            case TkMsgType.NM_CR_LoadWarCompleted:    HandleLoadWarCompleted(session); break;
            case TkMsgType.NM_CR_TankShellChange:
                if (message is PK_CR_TankShellChange shellChg) HandleTankShellChange(session, shellChg);
                break;
            case TkMsgType.NM_CR_USECONSUMABLE:
                if (message is PK_CR_UseConsumable useConsumable) HandleUseConsumable(session, useConsumable);
                break;
            case TkMsgType.NM_CR_Smoke:
                if (message is PK_CR_Smoke smokeReq) HandleSmoke(session, smokeReq);
                break;
            case TkMsgType.NM_CR_SelfChangeTank:
            case TkMsgType.NM_CR_VerifyHitTank:
            case TkMsgType.NM_CR_VerifyResult:
            case TkMsgType.NM_CR_ReqFreeView:
            case TkMsgType.NM_CR_CheckModuleExist:
            case TkMsgType.NM_CR_AskCommanderResp:
                if (message is PK_CR_AskCommanderResp acr
                    && _players.TryGetValue(session.PlayerGUID, out var acrPlayer))
                {
                    acrPlayer.CommanderVote = acr.AskCommanderResult; // 1=OK
                    Console.WriteLine($"[RS] Commander vote: {session.PlayerName} -> {acr.AskCommanderResult}");
                }
                break;
            case TkMsgType.NM_CR_HEHit:
            case TkMsgType.NM_CR_CrashDamage:
                break; // known; HE damage is applied via the ShellHit path
            case TkMsgType.NM_CR_PVE_SkillRequest:
                if (message is PK_CR_PVE_SkillRequest skReq) HandleSkillRequest(session, skReq);
                break;
            case TkMsgType.NM_CR_APHit:
                if (message is PK_CR_APHit apHit) HandleAPHit(session, apHit);
                break;
            case TkMsgType.NM_CR_LaserHit:
                if (message is PK_CR_LaserHit laserHit) HandleLaserHit(session, laserHit);
                break;
            case TkMsgType.NM_CR_SceneInfo:
                if (message is PK_CR_SceneInfo sceneInfo) HandleSceneInfo(session, sceneInfo);
                break;
            case TkMsgType.NM_CR_SceneInfoDir:
                if (message is PK_CR_SceneInfoDir sceneInfoDir) HandleSceneInfoDir(session, sceneInfoDir);
                break;
            case TkMsgType.NM_CR_DeadCameroOnFriend:
                if (message is PK_CR_DeadCameroOnFriend dcf) HandleDeadCameroOnFriend(session, dcf);
                break;
            case TkMsgType.NM_CR_PVE_OpenGodView:
            case TkMsgType.NM_CR_PVE_CloseGodView:
                Console.WriteLine($"[RS] Known-but-unhandled: {message.MsgType} from {session.PlayerName}");
                break;
            default:
                Console.WriteLine($"[RS] Unhandled: {message.MsgType}");
                break;
        }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RS] Exception in {message?.MsgType} for {session.PlayerGUID}: {ex.Message}");
        }
    }

    // ─────────────────────────── login ─────────────────────────────────────────
    private void HandleRoomLogin(GameSession session, PK_CR_PlayerLogin login)
    {
        session.PlayerGUID = login.PlayerGUID;
        session.Certification = login.Certification;

        var account   = _accountRepo?.GetByPlayerGUID(login.PlayerGUID);
        session.PlayerName = account?.PlayerName ?? $"Player_{login.PlayerGUID}";
        Console.WriteLine($"[RS] RoomLogin: GUID={login.PlayerGUID}, Name={session.PlayerName}, WarType={login.WarType}");

        // ── detect game mode ──────────────────────────────────────────────────
        // The client HARDCODES PK_CR_PlayerLogin.WarType = 1 (see
        // RoomNetwork.SendPlayerLoginRS in the decompiled client), so WarType can NOT
        // tell us the real mode. The actual menu selection was recorded by HallServer
        // in BattleModeRegistry, keyed by the room GUID we're logging into.
        //
        // The value recorded is the client's EBattleMode. The live server log shows THIS
        // client sends: 0=Standard(BaseCapture), 1=Relive(Respawn), and for the other two
        // buttons it sends 3 and 5 — which in the enum are NormalModeMax(3) and
        // NewGuide(5). Empirically (confirmed by user testing): the DEFENSE button sends 5
        // and the SIMPLIFIED button sends 3 in this build. That's why picking Defense made
        // the hangar flip to "training" mode (NewGuide=5 is the tutorial). We map by the
        // value actually sent. We also accept the reference-enum values (4=PVE, 6=Simplify)
        // for robustness in case a different client build is used.
        byte chosenModeIndex = BattleModeRegistry.Resolve(login.RoomGUID, login.PlayerGUID) ?? 1;

        // NOTE: mode index 2 = Commander (Generals). It used to be wrongly treated as
        // "training" and redirected to Respawn here — that redirect is removed so the
        // switch below can map 2 -> Commander and StartCommanderMode can run.

        var mode = chosenModeIndex switch
        {
            0 => BattleGameMode.BaseCapture,
            2 => BattleGameMode.Commander,
            5 => BattleGameMode.Defense,
            4 => BattleGameMode.Defense,
            3 => BattleGameMode.Simplified,
            6 => BattleGameMode.Simplified,
            _ => BattleGameMode.Respawn,
        };
        Console.WriteLine($"[RS] Resolved mode: index={chosenModeIndex} -> {mode}");

        // ── mode-specific server→client fields ──────────────────────────────
        // WarModel → client's SetPlayerBattleMode; only 0 (Standard) and 1 (Relive/Respawn)
        // render correctly without breaking the UI. Defense uses 4 to enable PVE HUD.
        byte warModel = mode switch
        {
            BattleGameMode.Respawn    => 1, // Relive UI: top score bars
            BattleGameMode.Defense    => 4, // PVE UI: round counter, enemy count, SP bar
            BattleGameMode.Commander  => 2, // Generals UI: general HP bars at top
            BattleGameMode.Simplified => 0,
            _                         => 0,
        };
        // StandardInvade (1) unconditionally indexes stronghold[0] and stronghold[1] in
        // OperateControl.GenerateOccupyCircle(); maps with a single base throw
        // IndexOutOfRangeException before MiniMapView is initialized, hiding the minimap.
        // StandardNormal (0) renders a single base circle safely and matches OccupyValueManager.
        byte littleBattleMode = mode switch
        {
            BattleGameMode.Defense     => 5, // PVE defense screen layout
            _                          => 0,
        };

        // ── player tank ───────────────────────────────────────────────────────
        var tanks     = _tankRepo.GetPlayerTanks(login.PlayerGUID);
        var tank      = tanks.FirstOrDefault(t => t.IsBattleTank) ?? tanks.FirstOrDefault();
        var tankEntry = tank != null ? _gameTables.GetTank(tank.VehicleId) : null;
        // v53 (задача 6): боевой пул HP игрока — по РЕАЛЬНЫМ модулям (корпус + башня), а не по стоку,
        // чтобы совпадал с тем, что показывает клиент (EHpBuff + ETurretHpBuff) и с карточкой в ангаре.
        int maxHp     = tank != null
            ? _gameTables.GetTankMaxHp(tank.VehicleId, tank.HullID, tank.TurretID)
            : (int)_gameTables.GetRealMaxHp(tankEntry, 1000f);
        int curHp     = maxHp; // always full HP at battle start

        // In Defense the human is always a DEFENDER (Team0) — same side as the ally
        // bots (BotSpawns) and opposite the attacker waves (AttackerSpawns). Random
        // side is only for PvP-style modes; in defense it dumped the player onto the
        // enemy attack point (Team1) half the time.
        byte group = mode == BattleGameMode.Defense ? (byte)0 : (byte)_random.Next(0, 2);

        int roomCoatId = tank?.CurRoomTankCoatID > 0 ? tank.CurRoomTankCoatID
                       : tank?.CurTankCoatId     > 0 ? tank.CurTankCoatId
                       : 0;

        var player = new RoomPlayer
        {
            PlayerGUID      = login.PlayerGUID,
            Session         = session,
            TankGUID        = tank?.TankGUID ?? 0,
            VehicleId       = tank?.VehicleId ?? 1,
            HullID          = (ushort)(tank?.HullID ?? 1),
            UnderpanID      = (ushort)(tank?.UnderpanID ?? 1),
            TurretID        = (ushort)(tank?.TurretID ?? 1),
            MainGunID       = (ushort)(tank?.MainGunID ?? 1),
            EngineID        = (ushort)(tank?.EngineID ?? 1),
            FueltankID      = (ushort)(tank?.FueltankID ?? 1),
            CartridgeID     = (ushort)(tank?.CartridgeID ?? 1),
            RadioStationID  = (ushort)(tank?.RadioStationID ?? 1),
            GaugeID         = (ushort)(tank?.GaugeID ?? 1),
            TankCoatId      = roomCoatId,
            PosX            = GetSpawnX(group, 1),   // mapId filled after room is created
            PosY            = GetSpawnY(group, 1),
            PosZ            = GetSpawnZ(group, 1),
            CurHp           = curHp,
            MaxHp           = maxHp,
            Group           = group,
            TankID          = (ushort)(tankEntry?.Id ?? 1),
            Tier            = (byte)(tankEntry?.Tier ?? 1),
            BattleRoomGUID  = login.RoomGUID,
            Provisions      = ParseProvisions(!string.IsNullOrWhiteSpace(tank?.Provisions) ? tank.Provisions : tank?.DeployProvisions),
            // v.fix (надписи в бою): раньше не читались вовсе — см. комментарий на
            // объявлении полей RoomPlayer.SignatureId/EmblemId.
            SignatureId     = ParseIntArray(tank?.SignatureIds, 2),
            EmblemId        = ParseIntArray(tank?.EmblemIds, 4),
        };

        _players[login.PlayerGUID] = player;

        var room = _rooms.GetOrAdd(login.RoomGUID, guid =>
        {
            // Select map: rotate across all working maps for normal modes,
            // prefer Stalingrad for PVE Defense.
            byte mapId = PickRandomMap(mode);
            Console.WriteLine($"[RS] Creating room, selected MapId={mapId} ({GetMapConfig(mapId).Name})");
            return new BattleRoom
            {
                RoomGUID  = guid,
                MapId     = mapId,
                BattleLevel = 1,
                WarType   = login.WarType,
                GameMode  = mode,
            };
        });

        player.Room = room;
        room.Players[login.PlayerGUID] = player;

        // v.fix (камуфляж по карте): теперь, когда карта боя известна (room.MapId),
        // пробуем подобрать сезонный камуфляж вместо ангарного — см. ResolveSeasonalCoatId
        // и словарь MapExteriorType выше. Если карта не описана в словаре — ничего не
        // меняется (остаётся тот же camo, что был выбран в ангаре).
        player.TankCoatId = ResolveSeasonalCoatId(tank, room.MapId, player.TankCoatId);

        // Seed the height grid across the whole map at the spawn ground level so
        // bots are grounded from the first tick (no falling from the sky). We fill
        // a coarse grid covering the play area with the map's ground Y. The player's
        // own movement later refines this with true terrain heights.
        SeedHeightGrid(room);
        EnsureNavGrid(room);

        // Now that we have the room's mapId, fix the spawn position to match this map
        // v13 (баг 6): точка спавна с гарантированным зазором 8 м от союзников.
        (player.PosX, player.PosZ) = PickSpawnSpot(room, group);
        player.PosY = GetSpawnY(group, room.MapId);
        player.RotY = GetSpawnYaw(room.MapId, group);

        var attrs = _gameTables.BuildTankAttributes(
            tankEntry != null ? _gameTables.WithEquippedModules(tankEntry, player.HullID, player.UnderpanID, player.TurretID,
                                                                  player.EngineID, player.FueltankID, player.CartridgeID,
                                                                  player.RadioStationID, player.GaugeID) : null,
            player.MainGunID, 0, curHp);
        // Apply equipment bonuses to the attribute array and HP.
        var (hpMult, flatHp) = ApplyCrewAndEquipmentBonuses(attrs, login.PlayerGUID, player.TankGUID, mode == BattleGameMode.Defense);
        // v29: базовую перезарядку для адреналина берём ПОСЛЕ бонусов экипажа и
        // оснащения — раньше я снимал её до них, поэтому способность считала вдвое от
        // «голой» таблицы и на прокачанном танке фактически замедляла перезарядку.
        if (attrs.Length > 24 && attrs[24] > 0) player.BaseReloadSeconds = attrs[24];
        ApplyEquipmentCombatEffects(player, player.TankGUID, mode == BattleGameMode.Defense, attrs);
        if (hpMult != 1f || flatHp != 0f)
        {
            maxHp        = (int)(maxHp * hpMult + flatHp);
            curHp        = maxHp;
            player.MaxHp = maxHp;
            player.CurHp = curHp;
        }
        player.MaxForwardSpeedMps = attrs[2]; // already in m/s (converted in BuildTankAttributes)
        player.MaxBackSpeedMps    = attrs[3];
        // Per-tank vision: the player spots at their OWN tank's view range (upgraded turret +
        // crew vision bonus) so bots do not consistently out-spot the human player.
        float rawView = attrs.Length > 20 && attrs[20] > 0f ? attrs[20]
                      : (tankEntry != null && tankEntry.ViewRange > 0 ? tankEntry.ViewRange : 260f);
        player.DetectRange = ComputeHumanDetectRange(rawView, login.PlayerGUID, player.TankGUID);

        Console.WriteLine($"[RS] Mode={mode}, WarModel={warModel}, LittleBattleMode={littleBattleMode}");
        Console.WriteLine($"[RS] BattleAttrs: Speed={attrs[2] * 3.6f:F1}km/h, HP={attrs[39]:F0} (hpMult={hpMult:F3}, +flat {flatHp:F0})");

        var roomTank = new RoomMainTankData
        {
            VehicleId      = player.VehicleId,
            HullID         = player.HullID,
            UnderpanID     = player.UnderpanID,
            TurretID       = player.TurretID,
            MainGunID      = player.MainGunID,
            EngineID       = player.EngineID,
            FueltankID     = player.FueltankID,
            CartridgeID    = player.CartridgeID,
            RadioStationID = player.RadioStationID,
            GaugeID        = player.GaugeID,
            TankHp         = player.MaxHp,
            TankCurHp      = player.CurHp,
            TankCoatId     = player.TankCoatId,
            TankSignature  = (int[])player.SignatureId.Clone(),
            EmblemIDs      = (int[])player.EmblemId.Clone(),
            ShellSlots     = BuildRoomShellSlots(tank, player.MainGunID),
            // Расходники: клиент включает клик по иконке ТОЛЬКО если Provisions[i] != 0
            // (RoomProvision.cs), а показывает иконку по DeployProvisions[i] — без этих
            // полей (раньше оставались нулевыми массивами по умолчанию) UI считал, что
            // расходники не экипированы, даже если они реально куплены/поставлены в Ангаре.
            Provisions        = (int[])player.Provisions.Clone(),
            DefaultProvisions = (int[])player.Provisions.Clone(),
        };
        player.CurrentShellId = roomTank.ShellSlots.FirstOrDefault(s => s.ID != 0 && s.Count != 0).ID;
        player.ShellCount     = roomTank.ShellSlots.FirstOrDefault(s => s.ID != 0 && s.Count != 0).Count;
        // Cache slot->shellId so a mid-battle shell switch (NM_CR_TankShellChange sends a
        // slot index) can be resolved back to the actual shell id.
        for (int si = 0; si < player.ShellSlotIds.Length; si++)
            player.ShellSlotIds[si] = si < roomTank.ShellSlots.Length ? roomTank.ShellSlots[si].ID : 0;
        // Drum/autoloader: if ShellSlots gave Count=0, reload from gun.AmmoLoad
        // Also patch roomTank.ShellSlots so the LOGIN PACKET sends the correct drum count
        if (player.ShellCount <= 0)
        {
            var initGun   = _gameTables.GetGun(player.MainGunID);
            int drumCount = Math.Max(1, initGun?.AmmoLoad ?? 20);
            int drumId    = player.CurrentShellId > 0 ? player.CurrentShellId
                          : initGun?.Ammo1Id > 0 ? initGun.Ammo1Id : 1;
            player.ShellCount     = drumCount;
            player.CurrentShellId = drumId;
            // Patch the ShellSlots array that will be included in PK_RC_PlayerLoginSuccess
            roomTank.ShellSlots[0] = new ShellSlot { ID = drumId, Count = drumCount };
        }

        // Per-shell ammo counts (shellId -> count) from the final shell slots. Used by
        // HandleRequestFire so a switched shell reports its own count, not the shared one.
        player.ShellAmmoById.Clear();
        player.ShellAmmoMaxById.Clear();
        foreach (var slot in roomTank.ShellSlots)
            if (slot.ID != 0)
            {
                player.ShellAmmoById[slot.ID]    = slot.Count;
                player.ShellAmmoMaxById[slot.ID] = slot.Count;   // запоминаем полный боезапас
            }

        var tankAttr = new TankAllAttributeStruct
        {
            TankGUID   = player.TankGUID,
            Attributes = attrs,
        };

        var mapWx = GetMapWeather(room.MapId);   // v48.2: погода карты (Сталинград=снег)
        var success = new PK_RC_PlayerLoginSuccess
        {
            Flag                   = 1,
            LockedTime             = 0,
            MapId                  = room.MapId,
            BattleFieldId          = 1,
            SpawnCoordId           = GetSpawnCoordId(room.MapId, player.Group),
            PosX = player.PosX, PosY = player.PosY, PosZ = player.PosZ,
            RotX = 0, RotY = player.RotY, RotZ = 0,
            Group                  = player.Group,
            // «туман» на картах — это ВРЕМЯ СУТОК (Table_Time_type), а не Fog (он давно 0).
            // v49: TimeType теперь ПО КАРТЕ (GetMapTimeType) — ночь на Москве/Кане/Сталинграде,
            // день/утро на остальных. Раньше всюду был TimeType=1 (ночь Москвы) — глобальное марево.
            TimeType               = GetMapTimeType(room.MapId),
            // WeatherEffectType (Table_Weather):
            //   0 = ясно, осадков нет;
            //   2 = снег/ночь — даёт белую пелену на части карт.
            // v32: погода по картам ОТКАЧЕНА — всюду ясно.
            WeatherEffectType      = mapWx.type,        // v48.2: снег на Сталинграде (30), иначе ясно
            WeatherEffectIntensity = mapWx.intensity,
            FogStartDistance       = 30000,
            FogEndDistance         = 50000,
            FogIntensity           = 0f,
            RoomMainTank           = roomTank,
            RoomMainTankAttribute  = tankAttr,
            LittleBattleMode       = littleBattleMode,
            GunAngle               = 0,
            TurretAngle            = 0,
            MovePackNum            = 0,
            IsTerritoryWar         = 0,
            WarModel               = warModel,
            BattleLevel            = room.BattleLevel,
            FastChangeShell        = player.FastChangeShell,
        };
        // ── Hot-join: battle already running → add this player live ─────────
        // (Host/earlier group already entered; late friend picked the same mode and joined.)
        if (room.BattleStarted && !room.BattleEnded)
        {
            Console.WriteLine($"[RS] Hot-join: {login.PlayerGUID} into running room {room.RoomGUID}");
            var existingHuman = room.Players.Values.FirstOrDefault(p => !p.IsBot && p.PlayerGUID != player.PlayerGUID);
            // Put the joiner on the opposite team for PvP, same team (Group 0) for Defense (co-op)
            player.Group = mode == BattleGameMode.Defense || existingHuman == null
                ? (byte)0
                : (byte)(1 - existingHuman.Group);
            (player.PosX, player.PosZ) = PickSpawnSpot(room, player.Group);
            player.PosY = GetSpawnY(player.Group, room.MapId);
            player.RotY = GetSpawnYaw(room.MapId, player.Group);
            room.PendingHumans.Remove(player);

            // Defense has strictly 6 defenders (Team 0): replace an ally bot when a human hot-joins!
            if (mode == BattleGameMode.Defense)
            {
                while (room.Players.Values.Count(p => p.Group == 0) > 6)
                {
                    var allyBot = room.Players.Values.FirstOrDefault(p => p.IsBot && p.Group == 0);
                    if (allyBot == null) break;
                    DespawnWaveBot(room, allyBot);
                }
            }

            SendLoginSuccessForPlayer(player, room, mode, littleBattleMode, warModel);
            // Sync active wave counter/enemy count if Defense wave already rolled
            if (mode == BattleGameMode.Defense && room.CurrentWave > 0)
            {
                try
                {
                    player.Session.Send(new PK_RC_PVE_RoundBegin { ReadyTime = 0, CurRound = room.CurrentWave, CurRoundCD = 0 });
                    player.Session.Send(new PK_RC_PVE_Enemy { CurEnemyNum = room.CurrentWaveEnemies });
                }
                catch { }
            }
            // Tell existing humans about the newcomer via NewPlayerLogin (spawns live tank without resetting battle)
            BroadcastToOthers(room, player.PlayerGUID, new PK_RC_NewPlayerLogin
            {
                PlayerInfo = BuildPlayerInfo(player),
            });
            return;
        }

        // ── Multiplayer: wait for all expected players (with 15s anti-hang timeout) ──
        bool shouldStartNow = false;
        lock (room.StartLock)
        {
            if (room.BattleStarted || room.BattleEnded) return;
            room.ExpectedHumanCount = Math.Max(1, BattleModeRegistry.GetExpectedPlayers(room.RoomGUID));
            if (!room.PendingHumans.Contains(player))
                room.PendingHumans.Add(player);
            room.DeferredStartParams = (littleBattleMode, warModel, mode);

            if (room.PendingHumans.Count >= room.ExpectedHumanCount)
            {
                shouldStartNow = true;
            }
            else if (!room.DeferredWaitStarted)
            {
                room.DeferredWaitStarted = true;
                Console.WriteLine($"[RS] Room {room.RoomGUID}: {room.PendingHumans.Count}/{room.ExpectedHumanCount} players — waiting up to 15s for remaining players...");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(15_000);
                    bool forceStart = false;
                    (byte lm, byte wm, BattleGameMode gm) p = default;
                    lock (room.StartLock)
                    {
                        if (!room.BattleStarted && !room.BattleEnded && room.PendingHumans.Count > 0 && room.DeferredStartParams.HasValue)
                        {
                            room.ExpectedHumanCount = room.PendingHumans.Count;
                            p = room.DeferredStartParams.Value;
                            forceStart = true;
                        }
                    }
                    if (forceStart)
                    {
                        Console.WriteLine($"[RS] Room {room.RoomGUID}: 15s wait timeout -> starting with {room.PendingHumans.Count} connected player(s)");
                        StartPendingRoom(room, p.lm, p.wm, p.gm);
                    }
                });
            }
        }

        if (!shouldStartNow) return;
        StartPendingRoom(room, littleBattleMode, warModel, mode);
    }

    private void StartPendingRoom(BattleRoom room, byte littleBattleMode, byte warModel, BattleGameMode mode)
    {
        List<RoomPlayer> humansSnapshot;
        lock (room.StartLock)
        {
            if (room.BattleStarted || room.BattleEnded || room.PendingHumans.Count == 0) return;
            humansSnapshot = room.PendingHumans.ToList();
        }

        var firstHuman = humansSnapshot[0];
        var tank       = _tankRepo.GetPlayerTanks(firstHuman.PlayerGUID).FirstOrDefault(t => t.TankGUID == firstHuman.TankGUID);
        var tankEntry  = tank != null ? _gameTables.GetTank(tank.VehicleId) : null;
        var session    = firstHuman.Session;
        bool isDefenseRoom = mode == BattleGameMode.Defense;

        // Balance teams & refresh spawns BEFORE spawning bots so all players start cleanly
        if (humansSnapshot.Count > 1)
        {
            byte firstGroup = isDefenseRoom ? (byte)0 : humansSnapshot[0].Group;
            for (int hi = 0; hi < humansSnapshot.Count; hi++)
            {
                var ph = humansSnapshot[hi];
                ph.Group = isDefenseRoom ? (byte)0 : (byte)((firstGroup + hi) % 2);
                (ph.PosX, ph.PosZ) = PickSpawnSpot(room, ph.Group);
                ph.PosY = GetSpawnY(ph.Group, room.MapId);
                ph.RotY = GetSpawnYaw(room.MapId, ph.Group);
            }
        }

        // ── spawn bots BEFORE sending TankBattleData to humans ──────────────
        int humansOn0 = room.Players.Values.Count(p => !p.IsBot && p.Group == 0);
        int humansOn1 = room.Players.Values.Count(p => !p.IsBot && p.Group == 1);
        int botsOnGroup0 = isDefenseRoom ? Math.Max(0, 6 - humansOn0) : Math.Max(0, 7 - humansOn0);
        int botsOnGroup1 = isDefenseRoom ? 0 : Math.Max(0, 7 - humansOn1);
        int botsOnPlayerTeam = botsOnGroup0;
        int botsOnOtherTeam  = botsOnGroup1;
        int totalBots        = botsOnPlayerTeam + botsOnOtherTeam;
        byte group = 0;

        var fallbackBotTank = _gameTables.GetTank(81);
        // v48.5: одно ОКНО уровней на весь бой (разброс ≤ 2, содержит уровень игрока).
        // ВСЕ боты — и союзники, и враги — берутся из этого окна, поэтому «максимальная разница
        // между самым высоким и низким уровнем = 2». Для обороны это задаёт уровни СОЮЗНИКОВ
        // (враги там идут отдельными волнами по расписанию и это окно не трогает).
        var (tierLo, tierHi) = RollTierWindow(tankEntry?.Tier ?? 1);
        // v55 (п.5): необязательный минимум уровня боя (env GWT_MIN_TIER). У клиента Билли нет
        // моделей низких тиров → на тир 1-2 клиент падает («не удалось загрузить ресурсы»). Если
        // задать GWT_MIN_TIER=5, окно сдвигается вверх (напр. [1..3] → [5..7]) и низкоуровневые
        // танки не выбираются. По умолчанию 1 — поведение не меняется.
        int minTier = int.TryParse(Environment.GetEnvironmentVariable("GWT_MIN_TIER"), out var mt) ? Math.Clamp(mt, 1, 11) : 1;
        if (tierLo < minTier) { tierHi = Math.Min(11, tierHi + (minTier - tierLo)); tierLo = minTier; }
        Console.WriteLine($"[RS] Matchmaking v48.5: playerTier={tankEntry?.Tier ?? 1}, окно боя [{tierLo}..{tierHi}] (разброс {tierHi - tierLo})");

        // v70: диапазон уровней ВРАГОВ обороны — от уровней всех союзников-людей в
        // комнате (для мультиплеера это может быть 2 разных танка/уровня).
        if (isDefenseRoom)
        {
            var allyTiers = humansSnapshot.Count > 0
                ? humansSnapshot.Select(p => (int)p.Tier).ToList()
                : new List<int> { tankEntry?.Tier ?? 1 };
            int allyMin = allyTiers.Min();
            int allyMax = allyTiers.Max();
            room.EnemyTierLo = Math.Clamp(allyMin - EnemyTierSpreadDown, 1, 10);
            room.EnemyTierHi = Math.Clamp(allyMax + EnemyTierSpreadUp, 1, 10);
            Console.WriteLine($"[RS] Defense: союзники[{allyMin}..{allyMax}] -> враги (нем. ветка)[{room.EnemyTierLo}..{room.EnemyTierHi}]");
        }

        // v48 (задача 5): боты выкатываются со СЛУЧАЙНЫМИ башней/орудием из тех, что
        // ДОСТУПНЫ их танку (альтернативные модули из дерева исследований + вариант
        // модификации), а не копируют визуал игрока. Тип модуля: 4 = башня, 5 = орудие.
        int botsRandomized = 0;

        // Pre-calculate per-team Z-offsets so bots spread across the map width.
        // With n bots on a team, bot i gets offset = (i/(n-1) - 0.5) * 200
        // → values from –100 … +100, meaning bots spread over 200 units of Z.
        float ZOffset(int i, int n) => n <= 1 ? 0f : (i / (float)(n - 1) - 0.5f) * 200f;

        int teamAIdx = 0, teamBIdx = 0;

        // v68 (п.2): НЕВИДИМЫЙ союзный бот. По репорту это один бот с ДУБЛИКАТОМ танка (напр.
        // «второй T-55»), т.е. клиент не рендерит второй танк с той же моделью → он невидим.
        // Поэтому подбираем ботам УНИКАЛЬНЫЕ танки (в пределах окна тира), не дублируя и танк игрока.
        var usedBotTankIds = new HashSet<int>();
        foreach (var hp in humansSnapshot)
            if (hp.VehicleId > 0) usedBotTankIds.Add(hp.VehicleId);

        for (int b = 0; b < totalBots; b++)
        {
            byte botGroup = (byte)(b < botsOnPlayerTeam ? group : 1 - group);
            long botGuid  = -(b + 1);

            bool isPlayerTeam = b < botsOnPlayerTeam;
            int  teamIdx      = isPlayerTeam ? teamAIdx++ : teamBIdx++;
            int  teamSize     = isPlayerTeam ? botsOnPlayerTeam : botsOnOtherTeam;

            // v48.5: и союзники, и враги берутся из ОДНОГО окна уровней боя (разброс ≤ 2).
            // v68: пытаемся дать уникальный танк (до 12 попыток), иначе — что есть.
            TankEntry botTankEntry = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                var pick = PickTankInTierRange(tierLo, tierHi);
                if (pick == null) break;
                botTankEntry = pick;
                if (!usedBotTankIds.Contains(pick.Id)) break;
            }
            botTankEntry ??= fallbackBotTank;
            if (botTankEntry != null) usedBotTankIds.Add(botTankEntry.Id);
            int botHp        = (int)_gameTables.GetRealMaxHp(botTankEntry, 144f);

            // v48 (задача 5): модули бота. По умолчанию — стоковые из таблицы танка, но
            // БАШНЮ (тип 4) и ОРУДИЕ (тип 5) выбираем случайно из доступных этому танку
            // вариантов (дерево исследований + вариант модификации). Так бот-«тигр» может
            // выехать с другой башней/орудием, которые у этого танка реально есть.
            int mHull  = botTankEntry?.HullId       ?? 102, mUnder = botTankEntry?.UnderpanId ?? 160,
                mTurr  = botTankEntry?.TurretId      ?? 134, mGun   = botTankEntry?.MainGunId  ?? 185,
                mEng   = botTankEntry?.EngineId      ?? 112, mFuel  = botTankEntry?.FueltankId ?? 8,
                mCart  = botTankEntry?.CartridgeId   ?? 81,  mRadio = botTankEntry?.RadioStationId ?? 21,
                mGauge = botTankEntry?.GaugeId       ?? 4;
            if (botTankEntry != null)
            {
                // v49 (задача 2): КОРПУС бота тоже выбираем из доступных вариантов, включая
                // корпус модификации (use_hull). Броня бота считается по HullID (ResolveHit),
                // поэтому бот с модификацией получает её реальную броню (где она отличается).
                var hullOpts   = CollectModuleOptions(botTankEntry.Id, 1, botTankEntry.HullId);
                var turretOpts = CollectModuleOptions(botTankEntry.Id, 4, botTankEntry.TurretId);
                var gunOpts    = CollectModuleOptions(botTankEntry.Id, 5, botTankEntry.MainGunId);
                int newHull = hullOpts[_random.Next(hullOpts.Count)];
                int newTurr = turretOpts[_random.Next(turretOpts.Count)];
                // Выбираем только орудие, совместимое с выбранной башней (Table_Gun.use_turret):
                // иначе клиентский FindGunModelName возвращает false и 3D-модель бота не создаётся.
                var compatGuns = gunOpts.Where(g => _gameTables.IsGunCompatibleWithTurret(g, newTurr)).ToList();
                int newGun;
                if (compatGuns.Count > 0)
                {
                    newGun = compatGuns[_random.Next(compatGuns.Count)];
                }
                else
                {
                    newTurr = botTankEntry.TurretId;
                    compatGuns = gunOpts.Where(g => _gameTables.IsGunCompatibleWithTurret(g, newTurr)).ToList();
                    newGun = compatGuns.Count > 0
                        ? compatGuns[_random.Next(compatGuns.Count)]
                        : botTankEntry.MainGunId;
                }
                if (newHull != mHull || newTurr != mTurr || newGun != mGun) botsRandomized++;
                mHull = newHull; mTurr = newTurr; mGun = newGun;
                // v53 (задача 2): HP бота теперь по ВЫБРАННЫМ модулям (корпус + башня), а не по стоку —
                // бот с другой башней/корпусом получает соответствующий HP (как у игрока).
                botHp = _gameTables.GetTankMaxHp(botTankEntry.Id, mHull, mTurr);
            }

            // Role assignment: every third bot rotates Aggressive → Flanker → Defensive
            var role = (teamIdx % 3) switch
            {
                0 => BotRole.Aggressive,
                1 => BotRole.Flanker,
                _ => BotRole.Defensive,
            };

            // Compute spawn X/Z once so PosY can be derived from the SAME point
            // (calling GetSpawnX/GetSpawnZ again would draw new random jitter and
            // desync the Y sample from the actual X/Z the bot spawns at).
            // v13 (баг 6): разнесённые точки — минимум 8 м между союзниками на спавне.
            var _botPts = GetMapConfig(room.MapId).BotSpawns;
            var (botSpawnX, botSpawnZ) = (_botPts != null && _botPts.Length > 0)
                ? PickSpawnSpotNear(room, _botPts[b % _botPts.Length].X, _botPts[b % _botPts.Length].Z)
                : PickSpawnSpot(room, botGroup);
            var botPlayer = new RoomPlayer
            {
                PlayerGUID     = botGuid,
                Session        = session,
                VehicleId      = botTankEntry?.Id  ?? 81,
                HullID         = (ushort)mHull,
                UnderpanID     = (ushort)mUnder,
                TurretID       = (ushort)mTurr,
                MainGunID      = (ushort)mGun,
                EngineID       = (ushort)mEng,
                FueltankID     = (ushort)mFuel,
                CartridgeID    = (ushort)mCart,
                RadioStationID = (ushort)mRadio,
                GaugeID        = (ushort)mGauge,
                PosX           = botSpawnX,
                // Was hardcoded to 0f and relied on "the first player move" to fix it up —
                // that left every bot sitting at world-Y=0 (visibly buried/floating, since
                // real ground is rarely 0) for the first moments of every battle, on every
                // map whose ground isn't exactly Y=0. SeedHeightGrid() has already run by
                // this point, so the real calibrated spawn height is available immediately.
                PosY           = SampleHeight(room, botSpawnX, botSpawnZ),
                PosZ           = botSpawnZ,
                CurHp          = botHp, MaxHp = botHp,
                Group          = botGroup,
                TankID         = (ushort)(botTankEntry?.Id ?? 81),
                Tier           = (byte)(botTankEntry?.Tier ?? 1),
                BattleRoomGUID = room.RoomGUID,
                IsBot          = true, IsLoaded = true,
                Role           = role,
                AssignedZOffset = ZOffset(teamIdx, teamSize),
                LastYaw        = GetSpawnYaw(room.MapId, botGroup),
            };
            botPlayer.MaxForwardSpeedMps = (botTankEntry?.Speed ?? 30f) / 3.6f;
            botPlayer.MaxBackSpeedMps    = botPlayer.MaxForwardSpeedMps * 0.4f;
            // Randomised but stable reload per bot so they don't all fire in sync.
            // Defense mode allies: halved reload — they need to hold the line!
            botPlayer.BotReloadSeconds = isDefenseRoom && isPlayerTeam
                ? 2.0f + (float)_random.NextDouble() * 1.5f   // 2.0–3.5s for defense allies
                : 4.5f + (float)_random.NextDouble() * 4f;    // 4.5–8.5s normal
            botPlayer.CurrentShellId     = _gameTables.GetGun(botPlayer.MainGunID)?.Ammo1Id ?? 0;
            botPlayer.TankCoatId         = 0;
            botPlayer.Room               = room;
            // Derive class-based combat profile + vision from this tank's real stats.
            botPlayer.Profile     = BotCombatProfiles.Build(botTankEntry, _gameTables);
            botPlayer.Skill       = BotSkill.Roll(_random);
            botPlayer.DetectRange = botPlayer.Profile.DetectRange * BotSkill.ViewMult(botPlayer.Skill);
            try
            {
                var bAttrs = _gameTables.BuildTankAttributes(botTankEntry, botPlayer.MainGunID, botPlayer.CurrentShellId, botPlayer.MaxHp);
                if (bAttrs != null && bAttrs.Length > 12)
                {
                    if (bAttrs[4]  > 1f) botPlayer.HullTurnDegPerSec   = bAttrs[4];
                    if (bAttrs[12] > 1f) botPlayer.TurretTurnDegPerSec = bAttrs[12];
                }
            }
            catch { }
            room.Players[botGuid]        = botPlayer;
            _players[botGuid]            = botPlayer;
            if (b == 0)
                Console.WriteLine($"[RS] BotY check map={room.MapId}: first bot spawns at Y={botPlayer.PosY:F1} " +
                                  $"(X={botSpawnX:F0} Z={botSpawnZ:F0}) = terrain+1.5. If bots FLY in-game while this " +
                                  $"stays low, the CLIENT (a patched Assembly-CSharp.dll) is adding the height, not the server.");
        }
        Console.WriteLine($"[RS] Added {totalBots} bots to room {room.RoomGUID}");
        Console.WriteLine($"[RS] v48 задача 5: случайную башню/орудие получили {botsRandomized} из {totalBots} ботов");

        // Send full startup sequence (LoginSuccess + SceneInfo + PlayerList + TankBattleData +
        // Countdown + Score + HideEnemies + PVE init) to ALL waiting humans now that all bots exist.
        lock (room.StartLock) { room.PendingHumans.Clear(); }
        foreach (var ph in humansSnapshot)
        {
            SendLoginSuccessForPlayer(ph, room, mode, littleBattleMode, warModel);
        }

        // ── start battle once players & bots are in room ─────────────────────
        if (room.Players.Count >= 2 && !room.BattleStarted)
        {
            room.BattleStarted  = true;
            room.StartedUnix    = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(); // v12: для статистики
            // Pre-battle prep countdown: SHORT for Defense (waves come fast), longer for
            // the PvP modes. Lock combat for 3 s load + prep up front, before the AI
            // thread starts, so bots don't lurch during those seconds.
            // v12: было 25 — по просьбе снижено до 15 для захвата базы, возрождения и генералов.
            int prepSecs = mode == BattleGameMode.Defense ? 5 : 15;
            room.CombatStartUtc = DateTime.UtcNow.AddSeconds(3 + prepSecs);
            room.BattleTimeLeft = 359999; // effectively unlimited (no clock timeout)
            room.Timer          = new Timer(GameTimerTick, room, 1000, 1000);

            // v75: было 500 мс — за это время бот мог уже сместиться/скрыться, а клиент
            // ещё держал его старую (видимую, с коллайдером) модель на прежнем месте.
            // Игрок стрелял ровно туда, где танк только что стоял, и снаряд «пролетал
            // сквозь» — потому что физически цели там уже не было (клиент удаляет
            // коллайдер и уносит модель на -2000 ТОЛЬКО по приходу TankHide, см.
            // decompile PlayerProxy.OnHide). Это не серверный баг попадания — сервер
            // вообще не проверяет засвет при расчёте урона (см. ResolveHit) — а чисто
            // задержка in клиентском «скрытии»/обновлении позиции. Сократили тик до 150 мс,
            // чтобы окно рассинхронизации между реальной и видимой позицией было в разы
            // короче (макс. отставание с 500 мс до 150 мс).
            room.MinimapTimer = new Timer(MinimapTick, room, 150, 150);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(3000);   // let clients finish loading the scene

                // ── Pre-battle "Ожидание боя" countdown ──────────────────────
                // v30 (баг 2): шлём РОВНО ОДИН пакет на весь отсчёт.
                // Клиентский TimeCountDown.Begin(keepTime) сам крутит число от Time.time
                // (GetLastTime: keepTime − прошедшие секунды) и сам заводит зацикленный
                // звук тиканья id=60. Прежний вариант «по пакету в секунду» на каждом
                // тике вызывал Begin() заново: DestroyImmediate(_countTimeAudio) +
                // повторный PlayGlobalAudioLoop — клип не успевал догрузиться и звук
                // выходил в эфир с задержкой, уже после окончания отсчёта.
                BroadcastToRoom(room, new PK_RC_TimeCountDown { TimeLeft = prepSecs });
                Thread.Sleep(prepSecs * 1000);

                BroadcastToRoom(room, new PK_RC_WarStart());

                // v14 (баг 7): остаточная дымка. Помимо полей в LoginSuccess шлём ЯВНЫЙ
                // сброс погоды — он перебивает то, что клиент взял из таблицы карты
                // (RenderSettings.fogDensity = CurRoomFogIntensity в MapEntity).
                // v30 (п.3): раньше здесь шёл ЯВНЫЙ СБРОС погоды в ноль — он гасил
                // всё, что клиент взял из LoginSuccess. Теперь шлём погоду этой карты:
                // пакет по‑прежнему перебивает fogDensity из таблицы карты (MapEntity),
                // но уже на нужное значение, а не на ноль.
                // v14 (баг 7) + v32 (откат): явный сброс погоды. Он перебивает то,
                // что клиент взял из таблицы карты (RenderSettings.fogDensity =
                // CurRoomFogIntensity в MapEntity), и убирает остаточную дымку.
                var mapWx2 = GetMapWeather(room.MapId);   // v48.2: погода карты
                BroadcastToRoom(room, new PK_RC_SynWeatherEffect
                {
                    CurWeatherEffectType      = mapWx2.type,
                    CurWeatherEffectIntensity = mapWx2.intensity,
                    FogStartDistance          = 30000,
                    FogEndDistance            = 50000,
                    FogIntensity              = 0f,
                });

                // Fog of war: hide EVERY enemy in 3D at the start, so nothing is visible
                // until a teammate actually spots it (MinimapTick then re-lights spotted
                // ones via BeLighted, and only spotted enemies get position updates —
                // see BroadcastBotMove). Without this the client keeps drawing every
                // tank it was told about at spawn.
                foreach (var hp in room.Players.Values.Where(p => !p.IsBot && p.Session != null))
                {
                    var foes = room.Players.Values
                        .Where(p => p.Group != hp.Group && !p.IsDead)
                        .Select(p => p.PlayerGUID).ToArray();
                    lock (hp.SpotLock)
                    {
                        hp.SpottedEnemies.Clear();
                        hp.SpotHoldUntil.Clear();
                    }
                    try { SendHideEnemies(hp.Session!, foes); }
                    catch { /* player may have disconnected */ }
                }
                // Clip guns (XM815, Mars 15, etc.) start with an EMPTY drum: the
                // client only fills the clip via its revive path, which is why
                // shells appeared "only after death". Sending PK_RC_TankShellChange
                // now drives the client's ReloadAmmoImmediately() -> instant full
                // clip (same as revive). Sent per-player with their own shell id.
                foreach (var hp in room.Players.Values.Where(p => !p.IsBot && p.Session != null))
                {
                    int sid = hp.CurrentShellId > 0
                        ? hp.CurrentShellId
                        : (_gameTables.GetGun(hp.MainGunID)?.Ammo1Id ?? 1);
                    try { hp.Session.Send(new PK_RC_TankShellChange { Flag = 1, ShellId = sid }); }
                    catch { /* player may have disconnected */ }
                }

                if (mode == BattleGameMode.Respawn)
                    BroadcastReviveScore(room);

                if (mode == BattleGameMode.Defense)
                {
                    BroadcastToRoom(room, new PK_RC_PVE_GameBegin { NullData = 0 });
                    room.PveDefenseLives = BattleRoom.PveInitLives;
                    BroadcastToRoom(room, new PK_RC_PVE_Life { CurLife = room.PveDefenseLives });
                    StartSpAccrual(room);
                    StartPveWaves(room);   // first wave rolls in now, after the prep
                    StartSnowstormLoop(room);   // v48.6: периодическая снежная буря на снежных картах
                }

                if (mode == BattleGameMode.Commander)
                {
                    StartCommanderMode(room);
                    StartSpAccrual(room);
                }
            });

            StartBotAI(room, group, mode == BattleGameMode.Defense);
        }
    }

    private static void SendHideEnemies(GameSession session, long[] guids)
    {
        if (guids.Length == 0) return;
        session.Send(new PK_RC_ViewState
        {
            Players = guids.Select(g => (g, (byte)0)).ToArray(),
        });
        session.Send(new PK_RC_TankHide { PlayerGuidList = guids });
    }

    private static PK_RC_TankMoveDictate BuildSnapshotMovePacket(RoomPlayer p)
    {
        p.MoveNumber++;
        return new PK_RC_TankMoveDictate
        {
            PlayerGuid  = p.PlayerGUID,
            DictateInfo = 0,
            SpeedX = 0f, SpeedY = 0f, SpeedZ = 0f,
            MoveNumber  = p.MoveNumber,
            PosX = p.PosX, PosY = p.PosY, PosZ = p.PosZ,
            RotX = p.IsBot ? p.RenderPitch : p.RotX,
            RotY = p.IsBot ? p.LastYaw     : p.RotY,
            RotZ = p.IsBot ? p.RenderRoll  : p.RotZ,
            GunAngle    = 0f,
            TurretAngle = p.TowerRotY,
            CruiseGrade = 0,
            SlideSpeedX = 0, SlideSpeedY = 0, SlideSpeedZ = 0,
        };
    }

    // ── minimap / spotting broadcast ──────────────────────────────────────────
    // Every 150 ms:
    //  1. Compute which enemies are spotted (clear LOS within DetectRange, proxy-spot,
    //     firing reveal, or within SpotHoldSec grace window; dead wrecks stay visible).
    //  2. Send PK_RC_ViewState(2) + PK_RC_BeLighted + immediate TankMoveDictate for newly spotted.
    //  3. Send PK_RC_ViewState(0) + PK_RC_TankHide for living enemies whose spot expired.
    //  4. Send PK_RC_LittleMapSyn with positions of allies + currently spotted living enemies.
    private void MinimapTick(object? state)
    {
        if (state is not BattleRoom room || room.BattleEnded) return;
        try
        {
            var now = DateTime.UtcNow;

            foreach (var human in room.Players.Values.Where(p => !p.IsBot && p.IsLoaded && p.Session != null))
            {
                byte playerGroup = human.Group;

                lock (human.SpotLock)
                {
                    var nowSpotted = new HashSet<long>();
                    var newlyLitAlive = new List<long>();
                    var newlyLitAny   = new List<RoomPlayer>();

                    foreach (var p in room.Players.Values)
                    {
                        if (p.Group == playerGroup) continue; // only enemies
                        if (!p.IsLoaded) continue;

                        if (p.IsDead)
                        {
                            // Never hide an already-spotted dead tank with PK_RC_TankHide:
                            // PlayerProxy.OnHide teleports _trans to (-2000,-2000,-2000) and destroys
                            // the wreck. Keep spotted wrecks visible, and reveal unspotted wrecks
                            // once an ally gets LOS/range on them.
                            if (human.SpottedEnemies.Contains(p.PlayerGUID) || IsSpottedBy(p, playerGroup, room))
                            {
                                nowSpotted.Add(p.PlayerGUID);
                                if (!human.SpottedEnemies.Contains(p.PlayerGUID))
                                    newlyLitAny.Add(p);
                            }
                            continue;
                        }

                        if (IsSpottedBy(p, playerGroup, room))
                        {
                            human.SpotHoldUntil[p.PlayerGUID] = now.AddSeconds(SpotHoldSec);
                            nowSpotted.Add(p.PlayerGUID);
                            if (!human.SpottedEnemies.Contains(p.PlayerGUID))
                            {
                                newlyLitAlive.Add(p.PlayerGUID);
                                newlyLitAny.Add(p);
                            }
                        }
                        else if (human.SpottedEnemies.Contains(p.PlayerGUID)
                            && human.SpotHoldUntil.TryGetValue(p.PlayerGUID, out var holdUntil)
                            && now < holdUntil)
                        {
                            // Keep briefly spotted while grace window holds so targets do not
                            // blink invisible for 0.15-0.3s across small corners/crests.
                            nowSpotted.Add(p.PlayerGUID);
                        }
                    }

                    // Newly spotted enemies -> push their real world transform FIRST while IsShow is still false
                    // (DictateListControl.SetPlayerOperation snaps _trans.position from -2000), then set ViewState=2
                    // and notify BeLighted (alive only).
                    if (newlyLitAny.Count > 0)
                    {
                        foreach (var litPlayer in newlyLitAny)
                            human.Session!.Send(BuildSnapshotMovePacket(litPlayer));
                        human.Session!.Send(new PK_RC_ViewState
                        {
                            Players = newlyLitAny.Select(p => (p.PlayerGUID, (byte)2)).ToArray(),
                        });
                        if (newlyLitAlive.Count > 0)
                        {
                            human.Session.Send(new PK_RC_BeLighted { PlayerGuids = newlyLitAlive.ToArray() });
                            human.LightCount += newlyLitAlive.Count;   // v12: «засвет целей» для статистики
                        }
                    }

                    // Living enemies whose spot expired -> ViewState=0 + TankHide.
                    // Never send TankHide for dead tanks or despawned wave bots.
                    var nowDark = human.SpottedEnemies
                        .Except(nowSpotted)
                        .Where(guid => room.Players.TryGetValue(guid, out var ep) && !ep.IsDead)
                        .ToArray();
                    if (nowDark.Length > 0)
                    {
                        SendHideEnemies(human.Session!, nowDark);
                        foreach (var guid in nowDark)
                            human.SpotHoldUntil.Remove(guid);
                    }

                    human.SpottedEnemies.Clear();
                    foreach (var id in nowSpotted) human.SpottedEnemies.Add(id);

                    // Minimap этого игрока: союзники всегда, враги — только засвеченные живые.
                    var pkt = new PK_RC_LittleMapSyn();
                    foreach (var p in room.Players.Values)
                    {
                        if (p.IsDead) continue;
                        bool isAlly = p.Group == playerGroup;
                        if (!isAlly && !nowSpotted.Contains(p.PlayerGUID)) continue;
                        if (!p.IsLoaded) continue;   // v14: клиент ещё не создал этот танк
                        pkt.Players.Add((p.PlayerGUID, (int)p.PosX, (int)p.PosZ));
                    }
                    if (pkt.Players.Count > 0)
                        human.Session!.Send(pkt);
                }
            }

            // v12: «ОБНАРУЖЕН»/«замаскирован» — переход засвета САМОГО игрока вражеской командой.
            // PK_RC_SynCoverState: 0 = camo_out (обнаружен), 1 = camo_in (скрыт).
            foreach (var hp in room.Players.Values.Where(p => !p.IsBot && p.IsLoaded && !p.IsDead))
            {
                byte enemyGroup = (byte)(1 - hp.Group);
                bool directlySeen = IsSpottedBy(hp, enemyGroup, room);
                if (directlySeen)
                    hp.EnemySpotHoldUntil = now.AddSeconds(SpotHoldSec);
                bool seen = directlySeen || (hp.SpottedByEnemy && now < hp.EnemySpotHoldUntil);
                if (seen != hp.SpottedByEnemy)
                {
                    hp.SpottedByEnemy = seen;
                    try { hp.Session.Send(new PK_RC_SynCoverState { CoverState = seen ? (byte)0 : (byte)1 }); } catch { }
                }
            }
        }
        catch { }
    }

    // ── Multiplayer helpers ───────────────────────────────────────────────────

    /// <summary>Build a PK_PlayerInfo entry for any player (used in TankBattleData).</summary>
    // Players to list in the battle roster. NOTE: we tried excluding Defense
    // attacker-bots here to hide the enemy table, but the client MUST receive every
    // bot in the initial roster to register it and preload its tank model — without
    // it the client spams "id not found" for those bot ids and then fails to load
    // their assets ("Не удалось загрузить ресурсы игры"). So all players are listed;
    // the enemy-table cosmetic will need a different (client-side) approach.
    private static System.Collections.Generic.List<RoomPlayer> RosterPlayers(BattleRoom room)
        => room.Players.Values.ToList();

    private static PK_PlayerInfo BuildPlayerInfo(RoomPlayer p, string? name = null)
    {
        var shellSlot = p.CurrentShellId > 0
            ? new ShellSlot { ID = p.CurrentShellId, Count = Math.Max(p.ShellCount, 1) }
            : new ShellSlot { ID = 1, Count = 10 };
        return new PK_PlayerInfo
        {
            Group = p.Group, TeamGuid = 0,
            PlayerName     = name ?? (p.IsBot ? $"Bot_{-p.PlayerGUID}" : (!string.IsNullOrWhiteSpace(p.Session?.PlayerName) ? p.Session.PlayerName : "Player")),
            PlayerGUID     = p.PlayerGUID, RoomerId = 0, TroopsTag = "",
            TankCoatId     = (ushort)p.TankCoatId,
            PosX = p.PosX, PosY = p.PosY, PosZ = p.PosZ,
            RotX = p.IsBot ? p.RenderPitch : p.RotX,
            RotY = p.IsBot ? p.LastYaw     : p.RotY,
            RotZ = p.IsBot ? p.RenderRoll  : p.RotZ,
            MaxHp = p.MaxHp, CurHp = p.CurHp,
            TankID = p.TankID, HullID = p.HullID, UnderpanID = p.UnderpanID,
            TurretID = p.TurretID, MainGunID = p.MainGunID, EngineID = p.EngineID,
            FueltankID = p.FueltankID, CartridgeID = p.CartridgeID,
            RadioStationID = p.RadioStationID, GaugeID = p.GaugeID,
            IsVisible=1, FightCite=0, TankTotalWeight=37500f, TankPower=500f,
            MaxForwardSpeed = p.MaxForwardSpeedMps, MaxBackSpeed = -p.MaxBackSpeedMps,
            RotateSpeed=30f, EngineStartTime=0f, MoveFastStartTime=0f, MoveFastStartRate=1f,
            TerrainResistanceHard=0.8f, IsVip=0,
            SignatureId=(int[])p.SignatureId.Clone(), EmblemId=(int[])p.EmblemId.Clone(),
            MovePackNum=0, SpwanGunAngle=0f, SpwanTurretAngle=0f, KillCount=0,
            ShellSlots = new ShellSlot[3] { shellSlot, new(){ID=shellSlot.ID,Count=shellSlot.Count}, new(){ID=shellSlot.ID,Count=shellSlot.Count} },
        };
    }

    /// <summary>
    /// Send the full battle startup sequence to a human player who was waiting
    /// for a multiplayer partner. Uses the correct PK_RC_TankBattleData approach
    /// (same as single-player — TankBattleData carries all other players' models).
    /// </summary>
    private void SendLoginSuccessForPlayer(RoomPlayer ph, BattleRoom room,
        BattleGameMode mode, byte littleMode, byte warModel)
    {
        var tank      = _tankRepo.GetPlayerTanks(ph.PlayerGUID).FirstOrDefault(t => t.TankGUID == ph.TankGUID);
        var tankEntry = _gameTables.GetTank(ph.VehicleId);
        int baseMaxHp = tank != null
            ? _gameTables.GetTankMaxHp(tank.VehicleId, tank.HullID, tank.TurretID)
            : (int)_gameTables.GetRealMaxHp(tankEntry, 1000f);
        var attrs     = _gameTables.BuildTankAttributes(
            tankEntry != null ? _gameTables.WithEquippedModules(tankEntry, ph.HullID, ph.UnderpanID, ph.TurretID,
                                                                  ph.EngineID, ph.FueltankID, ph.CartridgeID,
                                                                  ph.RadioStationID, ph.GaugeID) : null,
            ph.MainGunID, 0, baseMaxHp);
        var (phHpMult, phFlatHp) = ApplyCrewAndEquipmentBonuses(attrs, ph.PlayerGUID, ph.TankGUID, mode == BattleGameMode.Defense);
        float phRawView = attrs.Length > 20 && attrs[20] > 0f ? attrs[20]
                        : (tankEntry != null && tankEntry.ViewRange > 0 ? tankEntry.ViewRange : 260f);
        ph.DetectRange = ComputeHumanDetectRange(phRawView, ph.PlayerGUID, ph.TankGUID);
        if (attrs.Length > 24 && attrs[24] > 0) ph.BaseReloadSeconds = attrs[24];
        ApplyEquipmentCombatEffects(ph, ph.TankGUID, mode == BattleGameMode.Defense, attrs);
        ph.MaxHp = (int)(baseMaxHp * phHpMult + phFlatHp);
        ph.CurHp = ph.MaxHp;
        ph.MaxForwardSpeedMps = attrs[2];
        ph.MaxBackSpeedMps    = attrs[3];

        var roomTank = new RoomMainTankData
        {
            VehicleId = ph.VehicleId,
            HullID = ph.HullID, UnderpanID = ph.UnderpanID, TurretID = ph.TurretID,
            MainGunID = ph.MainGunID, EngineID = ph.EngineID, FueltankID = ph.FueltankID,
            CartridgeID = ph.CartridgeID, RadioStationID = ph.RadioStationID, GaugeID = ph.GaugeID,
            TankHp = ph.MaxHp, TankCurHp = ph.CurHp,
            TankCoatId = ph.TankCoatId,
            TankSignature = (int[])ph.SignatureId.Clone(), EmblemIDs = (int[])ph.EmblemId.Clone(),
            ShellSlots = BuildRoomShellSlots(tank, ph.MainGunID),
            Provisions        = (int[])ph.Provisions.Clone(),
            DefaultProvisions = (int[])ph.Provisions.Clone(),
        };
        ph.CurrentShellId = roomTank.ShellSlots.FirstOrDefault(s => s.ID != 0 && s.Count != 0).ID;
        ph.ShellCount     = roomTank.ShellSlots.FirstOrDefault(s => s.ID != 0 && s.Count != 0).Count;
        for (int si = 0; si < ph.ShellSlotIds.Length; si++)
            ph.ShellSlotIds[si] = si < roomTank.ShellSlots.Length ? roomTank.ShellSlots[si].ID : 0;
        if (ph.ShellCount <= 0)
        {
            var initGun   = _gameTables.GetGun(ph.MainGunID);
            int drumCount = Math.Max(1, initGun?.AmmoLoad ?? 20);
            int drumId    = ph.CurrentShellId > 0 ? ph.CurrentShellId
                          : initGun?.Ammo1Id > 0 ? initGun.Ammo1Id : 1;
            ph.ShellCount     = drumCount;
            ph.CurrentShellId = drumId;
            roomTank.ShellSlots[0] = new ShellSlot { ID = drumId, Count = drumCount };
        }
        ph.ShellAmmoById.Clear();
        ph.ShellAmmoMaxById.Clear();
        foreach (var slot in roomTank.ShellSlots)
            if (slot.ID != 0)
            {
                ph.ShellAmmoById[slot.ID]    = slot.Count;
                ph.ShellAmmoMaxById[slot.ID] = slot.Count;
            }
        var tankAttr = new TankAllAttributeStruct { TankGUID = ph.TankGUID, Attributes = attrs };

        ph.Session.Send(new PK_RC_PlayerLoginSuccess
        {
            Flag=1, LockedTime=0, MapId=room.MapId, BattleFieldId=1,
            SpawnCoordId=GetSpawnCoordId(room.MapId, ph.Group),
            PosX=ph.PosX, PosY=ph.PosY, PosZ=ph.PosZ, RotX=0,RotY=ph.RotY,RotZ=0,
            Group=ph.Group, TimeType=GetMapTimeType(room.MapId),
            WeatherEffectType=GetMapWeather(room.MapId).type,
            WeatherEffectIntensity=GetMapWeather(room.MapId).intensity, FogStartDistance=30000, FogEndDistance=50000, FogIntensity=0f,
            RoomMainTank=roomTank, RoomMainTankAttribute=tankAttr,
            LittleBattleMode=littleMode, GunAngle=0, TurretAngle=0, MovePackNum=0,
            IsTerritoryWar=0, WarModel=warModel, BattleLevel=room.BattleLevel, FastChangeShell=ph.FastChangeShell,
        });
        ph.Session.EnableEncryption(ph.PlayerGUID, ph.Session.Certification);
        ph.IsLoaded = true;

        ph.Session.Send(new PK_RC_SceneInfo());
        ph.Session.Send(new PK_RC_PlayerList { PlayerCount = (byte)room.Players.Count });
        var loginRoster = RosterPlayers(room);
        var battleData = new PK_RC_TankBattleData
        {
            TotalCount = (ushort)loginRoster.Count,
            PlayerInfos = loginRoster.Select(p => BuildPlayerInfo(p)).ToArray(),
        };
        ph.Session.Send(battleData);
        SendPrepCountdown(ph.Session, room);
        ph.Session.Send(new PK_RC_Score { Score = 0 });
        foreach (var other in room.Players.Values)
            if (other.PlayerGUID != ph.PlayerGUID)
                ph.Session.Send(new PK_RC_PlayerConnectState { PlayerGUID = other.PlayerGUID, State = 1 });
        var phEnemyGuids = room.Players.Values
            .Where(p => p.Group != ph.Group && !p.IsDead)
            .Select(p => p.PlayerGUID)
            .ToArray();
        SendHideEnemies(ph.Session, phEnemyGuids);
        if (mode == BattleGameMode.Defense)
        {
            ph.Session.Send(new PK_RC_PVE_InitData
            {
                CurSliver               = 0,
                CurGold                 = 0,
                CurSkillCoin            = 0,
                CurFreeSkillOfBomber    = ph.FreeBomberLeft,
                CurFreeSkillOfKatyusha  = ph.FreeKatyushaLeft,
                TotalRound              = BattleRoom.PveTotalRounds,
                LimitEnemyNum           = PveWaveTanks[0].Length,
                InitLife                = BattleRoom.PveInitLives,
                LimitScore              = 10000,
                KatyushaRadius          = 80,
            });
            if (room.BattleStarted)
                ph.Session.Send(new PK_RC_PVE_Life { CurLife = room.PveDefenseLives });
        }
        int phShell = ph.CurrentShellId > 0 ? ph.CurrentShellId
                    : (_gameTables.GetGun(ph.MainGunID)?.Ammo1Id ?? 1);
        try { ph.Session.Send(new PK_RC_TankShellChange { Flag = 1, ShellId = phShell }); }
        catch { }
    }

    /// <summary>Stub for deferred single-player launch (timeout fallback).</summary>
    private void LaunchBattle(BattleRoom room, PK_RC_PlayerLoginSuccess success,
        GameSession session, PK_CR_PlayerLogin login)
    {
        // Re-use the already-built success packet for the single waiting player
        session.Send(success);
        session.EnableEncryption(login.PlayerGUID, login.Certification);
        room.PendingHumans.First().IsLoaded = true;
        Console.WriteLine($"[RS] LaunchBattle (timeout) for room {room.RoomGUID}");
    }


    // ── PVE wave tank selection ──────────────────────────────────────────────────
    // Returns a pool of tanks appropriate for the given wave number.
    // Waves 1-3: Tier 5-7 (recon/light)
    // Waves 4-6: Tier 7-9 (medium/heavy)
    // Waves 7-9: Tier 9-10 (heavy/superheavy)
    // Waves 10-12: Tier 10-11 (elite/boss)
    private List<TankEntry> GetWaveTankPool(int wave)
    {
        int tierMin = wave switch
        {
            <= 2  => 5,
            <= 4  => 7,
            <= 6  => 8,
            <= 9  => 9,
            _     => 10,
        };
        int tierMax = wave switch
        {
            <= 2  => 7,
            <= 4  => 9,
            <= 6  => 10,
            <= 9  => 10,
            _     => 11,
        };
        var pool = _gameTables.Tanks.Values
            .Where(t => t.Tier >= tierMin && t.Tier <= tierMax
                     && t.HullId > 0 && t.MainGunId > 0)
            .ToList();
        if (pool.Count == 0)
            pool = _gameTables.Tanks.Values.Where(t => t.HullId > 0).ToList();
        return pool;
    }

    // ── PVE wave system ───────────────────────────────────────────────────────
    //
    // Waves run on a background thread. Each wave:
    //  1. Announce next round (countdown 10 s)
    //  2. Spawn wave bots with PVE_CreateMonster
    //  3. Broadcast enemy count
    //  4. Wait until all wave bots are dead (or timeout)
    //  5. Broadcast PVE_RoundEnd, loop
    //
    // Constant SP income: every human player earns +500 SP each second. Pushing the
    // Cost packet every tick also pins the on-screen SP counter to the server's value
    // (otherwise the client drifts, adding its own damage-based amount on top).
    private void StartSpAccrual(BattleRoom room)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (!room.BattleEnded)
            {
                Thread.Sleep(1000);
                if (room.BattleEnded) break;
                foreach (var p in room.Players.Values.Where(p => !p.IsBot && p.Session != null).ToList())
                {
                    int gainSp = (int)MathF.Round(500 * p.PveSpBonusMult);
                    p.Sp += gainSp;
                    p.SpEarned += gainSp;  // v12: «заработано очков» для статистики
                    try
                    {
                        p.Session.Send(new PK_RC_PVE_Cost
                        {
                            CurScore = p.Score,
                            CurSkillCoin = p.Sp,
                            CurFreeSkillOfBomber = p.FreeBomberLeft,
                            CurFreeSkillOfKatyusha = p.FreeKatyushaLeft,
                        });
                    }
                    catch { /* disconnected */ }
                }
            }
        });
    }

    // ── Generals (Commander) mode ────────────────────────────────────────────────
    // One general per side. A general gets x3 durability; the side whose general
    // dies loses. Humans are offered the post (client dialog); if nobody takes it,
    // a bot on that side is drafted so both sides always have a general.
    private void StartCommanderMode(BattleRoom room)
    {
        if (room.CommanderStarted) return;
        room.CommanderStarted = true;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            // Let the spawn countdown + tank entry finish.
            Thread.Sleep(8000);
            if (room.BattleEnded) return;

            // Offer the post to every human — pops the "run for commander?" dialog.
            foreach (var p in room.Players.Values.Where(p => !p.IsBot && p.Session != null))
                try { p.Session.Send(new PK_RC_AskCommanderReq { PackageFlag = 1 }); } catch { }

            // Collect answers.
            Thread.Sleep(10000);
            if (room.BattleEnded) return;

            // Draft one general per side: a human who accepted, else any human, else a bot.
            foreach (byte grp in new byte[] { 0, 1 })
            {
                var team = room.Players.Values.Where(p => p.Group == grp && !p.IsDead).ToList();
                if (team.Count == 0) continue;
                // v63 (п.4): генерала берём так — принявший человек, ИНАЧЕ союзный БОТ,
                // и лишь при полном отсутствии ботов — любой человек. Раньше вторым шёл
                // «любой человек», из-за чего ОТКАЗАВШИЙСЯ игрок всё равно назначался генералом.
                var cmd = team.FirstOrDefault(p => !p.IsBot && p.CommanderVote == 1)
                       ?? team.FirstOrDefault(p => p.IsBot)
                       ?? team.FirstOrDefault(p => !p.IsBot);
                if (cmd == null) continue;

                room.CommanderGuid[grp] = cmd.PlayerGUID;
                cmd.IsCommander = true;
                cmd.MaxHp *= BattleRoom.CommanderHpMultiple;   // x3 durability
                cmd.CurHp  = cmd.MaxHp;

                // Announce the general (State 2 = "has become commander").
                BroadcastToRoom(room, new PK_RC_CommanderState { PlayerGUID = cmd.PlayerGUID, State = 2 });
                BroadcastToRoom(room, new PK_RC_SynHP
                {
                    AttackerGUID = 0, PlayerGUID = cmd.PlayerGUID, CurHp = cmd.CurHp, Reason = 0
                });
                BroadcastToRoom(room, new PK_RC_CommanderScore { Side = grp, Score = cmd.CurHp, Para = cmd.MaxHp });
                Console.WriteLine($"[RS] General side {grp}: HP={cmd.MaxHp} bot={cmd.IsBot}");
            }

            // Monitor: keep top HP bars synced; end the battle when a general dies.
            while (!room.BattleEnded)
            {
                foreach (byte grp in new byte[] { 0, 1 })
                {
                    if (!room.CommanderGuid.TryGetValue(grp, out var guid)) continue;
                    var cmd = room.Players.Values.FirstOrDefault(p => p.PlayerGUID == guid);
                    if (cmd == null) continue;

                    BroadcastToRoom(room, new PK_RC_CommanderScore { Side = grp, Score = Math.Max(0, cmd.CurHp), Para = cmd.MaxHp });

                    if (cmd.IsDead || cmd.CurHp <= 0)
                    {
                        BroadcastToRoom(room, new PK_RC_CommanderState { PlayerGUID = guid, State = 3 });
                        Console.WriteLine($"[RS] General of side {grp} destroyed — side {1 - grp} wins.");
                        EndBattle(room, explicitWinnerGroup: (byte)(1 - grp));
                        return;
                    }
                }
                Thread.Sleep(1000);
            }
        });
    }

    // ── General abilities (PVE skills) ───────────────────────────────────────────
    // Handles all seven abilities from the skill wheel. The client checks SP/cooldown
    // on its side and shows the wheel, so the server accepts, echoes a reply, tells
    // everyone to play the visual, and applies the gameplay effect for the ones that
    // are server-authoritative (heal / ammo / area damage). Stimulant, ScoutPlane and
    // DisturbRadio are visual/buff effects the client applies itself.
    // v48.6: союзные боты в ОБОРОНЕ применяют способности — ремонт(1), боекомплект(2),
    // адреналин/энергетик(3). НЕ используют катюшу(5) и бомбардировщик(6). Кулдаун на каждую
    // способность не даёт спамить; за один тик применяется максимум одна. У ботов SP не
    // тратим (у них нет клиента/шкалы) — просто применяем эффект и шлём визуал, как у игрока.
    private void TryBotDefenderSkills(BattleRoom room, RoomPlayer bot, bool engaged)
    {
        if (bot.IsDead || bot.MaxHp <= 0) return;
        var now = DateTime.UtcNow;

        // 1) РЕМОНТ — если HP просело ниже 65% (заметно чаще, чем при 45%)
        if (now >= bot.BotSkillRepairCd && bot.CurHp < bot.MaxHp * 0.65f)
        {
            bot.CurHp = bot.MaxHp;
            BroadcastToRoom(room, new PK_RC_PVE_SkillBroadcast { PlayerGUID = bot.PlayerGUID, SkillID = 1, Vec = new float[3] });
            BroadcastToRoom(room, new PK_RC_SynHP { AttackerGUID = 0, PlayerGUID = bot.PlayerGUID, CurHp = bot.CurHp, Reason = 0 });
            bot.BotSkillRepairCd = now.AddSeconds(28);
            return;
        }
        // 2) БОЕКОМПЛЕКТ — периодически в бою. У ботов боезапас не расходуется (всегда полный),
        //    поэтому триггерим по времени, чтобы способность была видна (пополнение магазина).
        if (engaged && now >= bot.BotSkillAmmoCd)
        {
            var gun = _gameTables.GetGun(bot.MainGunID);
            bot.ShellCount = Math.Max(1, gun?.AmmoLoad ?? 20);
            BroadcastToRoom(room, new PK_RC_PVE_SkillBroadcast { PlayerGUID = bot.PlayerGUID, SkillID = 2, Vec = new float[3] });
            bot.BotSkillAmmoCd = now.AddSeconds(45);
            return;
        }
        // 3) АДРЕНАЛИН/энергетик — в бою, ускоряет перезарядку (боты чтут ReloadBuffUntil)
        if (engaged && now >= bot.BotSkillStimCd)
        {
            bot.ReloadBuffUntil = now.AddSeconds(10);
            BroadcastToRoom(room, new PK_RC_PVE_SkillBroadcast { PlayerGUID = bot.PlayerGUID, SkillID = 3, Vec = new float[3] });
            bot.BotSkillStimCd = now.AddSeconds(22);
            return;
        }
    }

    private void HandleSkillRequest(GameSession session, PK_CR_PVE_SkillRequest req)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var caster)) return;
        var room = caster.Room;
        if (room == null || room.BattleEnded) return;

        int skill = req.SkillID;
        if (skill >= 8 && skill <= 13) skill -= 7;   // PVP variant -> base skill id
        int cost = SkillSpCost(skill);

        // AIM phase (IsExecute<=0): ALWAYS reply OK so the client opens the god-view /
        // aiming reticle (Katyusha). Don't charge here — charge on execute.
        if (req.IsExecute <= 0)
        {
            session.Send(new PK_RC_PVE_SkillReply
            {
                IsExecute = req.IsExecute, SkillID = req.SkillID,
                RequestResult = 1 /* OK */, CostType = req.CostType, fParam1 = req.Parameter1,
            });
            return;
        }

        // EXECUTE (IsExecute>0): enforce SP cost (or consume free Bomber/Katyusha charge).
        bool usedFreeBomber = req.CostType == 5 && skill == 6 && caster.FreeBomberLeft > 0;
        bool usedFreeKatyusha = req.CostType == 6 && skill == 5 && caster.FreeKatyushaLeft > 0;
        if (!usedFreeBomber && !usedFreeKatyusha && caster.Sp < cost)
        {
            session.Send(new PK_RC_PVE_SkillReply
            {
                IsExecute = req.IsExecute, SkillID = req.SkillID,
                RequestResult = 2 /* CostNotEnough */, CostType = req.CostType, fParam1 = req.Parameter1,
            });
            return;
        }
        session.Send(new PK_RC_PVE_SkillReply
        {
            IsExecute = req.IsExecute, SkillID = req.SkillID,
            RequestResult = 1 /* OK */, CostType = req.CostType, fParam1 = req.Parameter1,
        });
        if (usedFreeBomber) caster.FreeBomberLeft--;
        else if (usedFreeKatyusha) caster.FreeKatyushaLeft--;
        else
        {
            caster.Sp = Math.Max(0, caster.Sp - cost);
            caster.SpSpent += cost;                                     // v12: «потрачено очков»
        }
        if (skill >= 1 && skill <= 7) caster.SkillUseCount[skill]++; // v12: счётчик применений
        session.Send(new PK_RC_PVE_Cost
        {
            CurScore = caster.Score,
            CurSkillCoin = caster.Sp,
            CurFreeSkillOfBomber = caster.FreeBomberLeft,
            CurFreeSkillOfKatyusha = caster.FreeKatyushaLeft,
        });

        var vec = new[] { req.Parameter1, req.Parameter2, req.Parameter3 };
        BroadcastToRoom(room, new PK_RC_PVE_SkillBroadcast
        {
            PlayerGUID = caster.PlayerGUID, SkillID = req.SkillID, Vec = vec,
        });

        switch (skill)
        {
            case 1: // Repair — restore caster to full durability
                caster.CurHp = caster.MaxHp;
                BroadcastToRoom(room, new PK_RC_SynHP { AttackerGUID = 0, PlayerGUID = caster.PlayerGUID, CurHp = caster.CurHp, Reason = 0 });
                Console.WriteLine($"[RS] Skill: {caster.PlayerGUID} repaired to {caster.CurHp}");
                break;
            case 2: // Supply ammo — refill the magazine
                var gun = _gameTables.GetGun(caster.MainGunID);
                caster.ShellCount = Math.Max(1, gun?.AmmoLoad ?? 20);
                // v68 (п.1): восполняем и ПО-ТИПОВЫЙ счётчик (ShellAmmoById) — иначе обработчик
                // выстрела шлёт клиенту старое значение и барабан откатывается после пополнения.
                if (caster.ShellAmmoMaxById.Count > 0)
                    foreach (var kv in caster.ShellAmmoMaxById) caster.ShellAmmoById[kv.Key] = kv.Value;
                
                // v68 (п.2): сообщаем КЛИЕНТУ пополнить боезапас — иначе счётчик у игрока не менялся.
                session.Send(new PK_RC_ResetAmmo
                {
                    Shell1Num = caster.ShellCount, Shell2Num = caster.ShellCount, Shell3Num = caster.ShellCount,
                });
                break;
            case 5: // Katyusha — a volley whose damage lands rocket-by-rocket over ~1.6s
                FireKatyusha(room, caster, vec[0], vec[2]);
                break;
            case 6: // Bomber — strike across the WHOLE map (no aim point; client sends 0,0)
                ApplyMapStrike(room, caster, 2500);
                break;
            case 3: // Стимулятор / энергетик
            {
                // Базовая длительность 10с; оснащение «Excitement Booster» (attr 1012) продлевает на 30%.
                float dur = 10f * caster.StimulantDurMult;
                caster.ReloadBuffUntil = DateTime.UtcNow.AddSeconds(dur); // боты: перезарядка ×0.5

                // v28: АДРЕНАЛИН — настоящее сокращение перезарядки на 50%.
                // Раньше здесь была лишь мгновенная дозарядка: я считал, что серверным
                // пакетом время перезарядки не изменить. Это оказалось не так — есть
                // PK_RC_ChangeAttribute (id 456), которым клиент правит атрибуты танка
                // прямо в бою (Player.OnChangeRoomTankAttribute -> AttributeContainer).
                // Ключ 24 = EReloadTime из клиентского enum EqAttributeType.
                if (!caster.IsBot && caster.Session != null && caster.BaseReloadSeconds > 0f)
                {
                    float boosted = caster.BaseReloadSeconds * 0.5f;
                    try
                    {
                        caster.Session.Send(new PK_RC_ChangeAttribute
                        {
                            Attributes = new (byte, float)[] { (24, boosted) },
                        });
                        // и сразу дозаряжаем, чтобы эффект чувствовался с первого выстрела
                        int sid = caster.CurrentShellId > 0
                            ? caster.CurrentShellId
                            : (_gameTables.GetGun(caster.MainGunID)?.Ammo1Id ?? 1);
                        caster.Session.Send(new PK_RC_TankShellChange { Flag = 1, ShellId = sid });
                    }
                    catch { }

                    // возврат базового значения по истечении срока
                    var restoreFor = caster;
                    float baseRl = caster.BaseReloadSeconds;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep((int)(dur * 1000));
                        try
                        {
                            if (restoreFor.Session != null && restoreFor.Session.IsConnected)
                                restoreFor.Session.Send(new PK_RC_ChangeAttribute
                                {
                                    Attributes = new (byte, float)[] { (24, baseRl) },
                                });
                        }
                        catch { }
                    });
                    Console.WriteLine($"[RS] Адреналин: перезарядка {baseRl:F1}с -> {boosted:F1}с на {dur:F0}с");
                }
                Console.WriteLine($"[RS] Skill: энергетик у {caster.PlayerGUID} — мгновенная перезарядка игрока + бот-буфер ×0.5 на {dur:F0}с");
                break;
            }
            // 4 ScoutPlane, 7 DisturbRadio: client-side effects only.
        }
    }

    // SP cost per ability (matches the in-game skill wheel).
    private static int SkillSpCost(int baseSkillId) => baseSkillId switch
    {
        1 => 300,    // Repair
        2 => 300,    // Supply ammo
        3 => 800,    // Stimulant (energy)
        4 => 300,    // Scout plane (recon)
        5 => 2800,   // Katyusha
        6 => 3500,   // Bomber
        7 => 800,    // Disturb radio
        _ => 0,
    };

    // Katyusha: instead of one big instant hit, fire a volley — each rocket lands
    // slightly scattered around the aim over ~1.6s and deals its own damage, so HP
    // drops rocket-by-rocket (and SP ticks up per rocket) instead of one 5000 chunk.
    private void FireKatyusha(BattleRoom room, RoomPlayer caster, float cx, float cz)
    {
        const int rockets = 8;
        // Base 650 per rocket. "Система
        // арт.корректировки" (defense equip) uplifts the caster's Katyusha damage.
        int perRocket = (int)MathF.Round(650 * caster.KatyushaDamageMult);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            for (int i = 0; i < rockets; i++)
            {
                if (room.BattleEnded) return;
                float rx = cx + (float)(_random.NextDouble() * 24 - 12);
                float rz = cz + (float)(_random.NextDouble() * 24 - 12);
                ApplyAreaStrike(room, caster, rx, rz, 20f, perRocket);
                Thread.Sleep(200);
            }
        });
    }

    private void ApplyAreaStrike(BattleRoom room, RoomPlayer caster, float cx, float cz, float radius, int damage)
    {
        float r2 = radius * radius;
        int hits = 0;
        foreach (var t in room.Players.Values.Where(t => t.Group != caster.Group && !t.IsDead).ToList())
        {
            float dx = t.PosX - cx, dz = t.PosZ - cz;
            if (dx * dx + dz * dz > r2) continue;
            hits++;
            // Route through the shell resolver so HP-sync, the death packet, kill-feed
            // and scoring all fire (previously we only sent SynHP, so kills never
            // registered and the "dead" bot lingered/teleported on the client).
            ResolveHit(room, caster, t,
                caster.CurrentShellId, 0,
                t.PosX, t.PosY + 1f, t.PosZ,
                0, 1, 0, 2, 0,
                clientConfirmedPen: true, fixedDamage: damage);
        }
        Console.WriteLine($"[RS] Katyusha by {caster.PlayerGUID} at ({cx:0},{cz:0}) r={radius} hit {hits} enemies");
    }

    // Bomber: damages every enemy on the map (there is no aim point).
    private void ApplyMapStrike(BattleRoom room, RoomPlayer caster, int damage)
    {
        // "Направляющий локатор" (defense equip) uplifts the caster's bombardment damage.
        if (caster.BombDamageMult != 1f)
            damage = (int)MathF.Round(damage * caster.BombDamageMult);
        int hits = 0;
        foreach (var t in room.Players.Values.Where(t => t.Group != caster.Group && !t.IsDead).ToList())
        {
            hits++;
            ResolveHit(room, caster, t,
                caster.CurrentShellId, 0,
                t.PosX, t.PosY + 1f, t.PosZ,
                0, 1, 0, 2, 0,
                clientConfirmedPen: true, fixedDamage: damage);
        }
        Console.WriteLine($"[RS] Bomber map strike by {caster.PlayerGUID} hit {hits} enemies");
    }

    // Fixed Defense wave roster (by tank table Id). Each wave spawns its OWN fresh
    // tanks; the next wave starts only after the current one is fully cleared, after
    // a 20-second cooldown.
    private static readonly int[][] PveWaveTanks =
    {
        new[]{14,14,14,14,14,14},     //  1: Пантера II
        new[]{5,5,5,5,5,5},           //  2: Е-50
        new[]{83,83,83,83,83,83},     //  3: Леопард 1
        new[]{13,13,13,3,3,3},        //  4: Маусы + Е-100
        new[]{14,14,14,14,14,14},     //  5: Пантера II
        new[]{5,5,5,5,5,5},           //  6: Е-50
        new[]{83,83,83,83,83,83},     //  7: Леопард 1
        new[]{13,13,13,3,3,3},        //  8: Маусы + Е-100
        new[]{14,14,14,14,14,14},     //  9: Пантера II
        new[]{5,5,5,5,5,5},           // 10: Е-50
        new[]{83,83,83,83,83,83},     // 11: Леопард 1
        new[]{13,13,13,3,3,3},        // 12: Маусы + Е-100
    };

    private void EquipBotForTank(RoomPlayer bot, int tankId)
    {
        var entry = _gameTables.Tanks.Values.FirstOrDefault(t => t.Id == tankId)
                 ?? _gameTables.Tanks.Values.FirstOrDefault(t => t.HullId > 0 && t.MainGunId > 0);
        if (entry != null) ApplyTankToBot(bot, entry);
    }

    // Create a fresh attacker bot with the given tank and a BRAND-NEW GUID, tell the
    // client to spawn it, then register it. New GUIDs are the key: the client renders
    // each as a genuinely new tank, so the model is correct and nothing lingers as a
    // corpse (the client refused to re-equip/re-create a bot it already knew).
    private RoomPlayer CreateAttackerBot(BattleRoom room, GameSession session, byte group, int tankId)
    {
        long guid = System.Threading.Interlocked.Decrement(ref _waveBotSeq);
        var entry = _gameTables.Tanks.Values.FirstOrDefault(t => t.Id == tankId)
                 ?? _gameTables.Tanks.Values.FirstOrDefault(t => t.HullId > 0 && t.MainGunId > 0);
        int hp = entry != null ? (int)_gameTables.GetRealMaxHp(entry, 144f) : 3000;
        // Enemy wave bots: if the map defines AttackerSpawns (e.g. Stalingrad defense),
        // spawn across those attack points (round-robin) instead of a single team zone.
        float sx, sz;
        var _atk = GetMapConfig(room.MapId).AttackerSpawns;
        if (_atk != null && _atk.Length > 0)
        {
            int _i  = System.Threading.Interlocked.Increment(ref _attackerSpawnIdx);
            var _pt = _atk[((_i % _atk.Length) + _atk.Length) % _atk.Length];
            (sx, sz) = PickSpawnSpotNear(room, _pt.X, _pt.Z);
        }
        else
        {
            (sx, sz) = PickSpawnSpot(room, group);   // v13: не вплотную к союзникам
        }
        var bot = new RoomPlayer
        {
            PlayerGUID = guid, Session = session,
            VehicleId = entry?.Id ?? 81,
            HullID = (ushort)(entry?.HullId ?? 102), UnderpanID = (ushort)(entry?.UnderpanId ?? 160),
            TurretID = (ushort)(entry?.TurretId ?? 134), MainGunID = (ushort)(entry?.MainGunId ?? 185),
            EngineID = (ushort)(entry?.EngineId ?? 112), FueltankID = (ushort)(entry?.FueltankId ?? 8),
            CartridgeID = (ushort)(entry?.CartridgeId ?? 81), RadioStationID = (ushort)(entry?.RadioStationId ?? 21),
            GaugeID = (ushort)(entry?.GaugeId ?? 4),
            PosX = sx, PosZ = sz, PosY = SampleHeight(room, sx, sz),
            CurHp = hp, MaxHp = hp, Group = group,
            TankID = (ushort)(entry?.Id ?? 81), Tier = (byte)(entry?.Tier ?? 1),
            BattleRoomGUID = room.RoomGUID, IsBot = true, IsLoaded = true,
            Role = BotRole.Aggressive, Room = room,
            LastYaw = GetSpawnYaw(room.MapId, group),
        };
        bot.MaxForwardSpeedMps = (entry?.Speed ?? 30f) / 3.6f;
        bot.MaxBackSpeedMps    = bot.MaxForwardSpeedMps * 0.4f;
        bot.BotReloadSeconds   = 4.5f + (float)_random.NextDouble() * 4f;
        bot.CurrentShellId     = _gameTables.GetGun(bot.MainGunID)?.Ammo1Id ?? 0;
        // Class-based combat profile + vision from this tank's real stats.
        bot.Profile     = BotCombatProfiles.Build(entry, _gameTables);
        bot.Skill       = BotSkill.Roll(_random);
        bot.DetectRange = bot.Profile.DetectRange * BotSkill.ViewMult(bot.Skill);
        try
        {
            var bAttrs = _gameTables.BuildTankAttributes(entry, bot.MainGunID, bot.CurrentShellId, bot.MaxHp);
            if (bAttrs != null && bAttrs.Length > 12)
            {
                if (bAttrs[4]  > 1f) bot.HullTurnDegPerSec   = bAttrs[4];
                if (bAttrs[12] > 1f) bot.TurretTurnDegPerSec = bAttrs[12];
            }
        }
        catch { }

        // Tell the client FIRST, then register — so the AI never moves a bot the client
        // doesn't know yet (that would spam "id not found").
        //
        // v13 (баг 2): транспорт зависит от режима. PK_RC_PVE_CreateMonster обрабатывает
        // ТОЛЬКО PVEGame (оборона) — в GameRoom (возрождение/захват/генералы) это no-op,
        // из-за чего респаун-боты были невидимы и клиент сыпал «id not found» (−1002…).
        // Для GameRoom правильный путь — PK_RC_NewPlayerLogin (id 236):
        // клиент делает CreateRemotePlayer и пишет «вернулся в игру».
        if (room.GameMode == BattleGameMode.Defense)
        {
            BroadcastToRoom(room, new PK_RC_PVE_CreateMonster
            {
                Group = group, PlayerGUID = guid, PlayerName = $"Bot_{-guid}",
                PosX = bot.PosX, PosY = bot.PosY, PosZ = bot.PosZ,
                RotX = 0f, RotY = bot.LastYaw, RotZ = 0f,
                MaxHp = bot.MaxHp, CurHp = bot.CurHp,
                TankID = bot.TankID, HullID = bot.HullID,
                UnderpanID = bot.UnderpanID, TurretID = bot.TurretID,
                MainGunID = bot.MainGunID, EngineID = bot.EngineID,
                FueltankID = bot.FueltankID, CartridgeID = bot.CartridgeID,
                RadioStationID = bot.RadioStationID, GaugeID = bot.GaugeID,
                MaxForwardSpeedMps = bot.MaxForwardSpeedMps, MaxBackSpeedMps = bot.MaxBackSpeedMps,
            });
        }
        else
        {
            BroadcastToRoom(room, new PK_RC_NewPlayerLogin
            {
                PlayerInfo = BuildPlayerInfo(bot),
            });
        }
        room.Players[guid] = bot;
        _players[guid]     = bot;
        return bot;
    }

    // v17: НЕ используется — боты теперь возрождаются штатным PK_RC_Revive по тому же
    // GUID (см. блок botRespawns). Оставлено на случай, если понадобится добавить
    // бота в бой «с нуля» (например, довесок волны).
    // v14 (баг 6): замена погибшего бота — ТОЧНАЯ КОПИЯ его личности.
    // Раньше шёл повторный поиск танка по таблицам, и в PK_PlayerInfo уезжал TankID,
    // который клиент не мог сопоставить (в логе: «PKPI Bot_1001:-1001 null», после чего
    // CreateRemotePlayer падал и бот не появлялся вовсе). Теперь модули/тир/ХП берутся
    // прямо у погибшего — они заведомо валидны, клиент их уже отрисовывал.
    private RoomPlayer CreateReplacementBot(BattleRoom room, RoomPlayer dead)
    {
        long guid = System.Threading.Interlocked.Decrement(ref _waveBotSeq);
        var (sx, sz) = PickSpawnSpot(room, dead.Group);
        var bot = new RoomPlayer
        {
            PlayerGUID = guid, Session = dead.Session,
            VehicleId = dead.VehicleId,
            TankID = dead.TankID, HullID = dead.HullID, UnderpanID = dead.UnderpanID,
            TurretID = dead.TurretID, MainGunID = dead.MainGunID, EngineID = dead.EngineID,
            FueltankID = dead.FueltankID, CartridgeID = dead.CartridgeID,
            RadioStationID = dead.RadioStationID, GaugeID = dead.GaugeID,
            Tier = dead.Tier,
            PosX = sx, PosZ = sz, PosY = SampleHeight(room, sx, sz),
            CurHp = dead.MaxHp, MaxHp = dead.MaxHp, Group = dead.Group,
            BattleRoomGUID = room.RoomGUID, IsBot = true, IsLoaded = true,
            Role = dead.Role, Room = room,
            MaxForwardSpeedMps = dead.MaxForwardSpeedMps,
            MaxBackSpeedMps    = dead.MaxBackSpeedMps,
            CurrentShellId     = dead.CurrentShellId,
            ShellCount         = Math.Max(1, dead.ShellCount),
            Profile            = dead.Profile,
            DetectRange        = dead.DetectRange,
            LastYaw            = GetSpawnYaw(room.MapId, dead.Group),
            HullTurnDegPerSec  = dead.HullTurnDegPerSec,
            TurretTurnDegPerSec= dead.TurretTurnDegPerSec,
        };
        bot.BotReloadSeconds = 4.5f + (float)_random.NextDouble() * 4f;
        bot.Skill            = BotSkill.Roll(_random);

        // Сначала сообщаем клиенту (иначе ИИ двинет бота, которого клиент не знает).
        BroadcastToRoom(room, new PK_RC_NewPlayerLogin { PlayerInfo = BuildPlayerInfo(bot) });
        room.Players[guid] = bot;
        _players[guid]     = bot;
        Console.WriteLine($"[RS] Bot respawn: guid={guid} TankID={bot.TankID} vid={bot.VehicleId} " +
                          $"grp={bot.Group} hp={bot.MaxHp} at ({sx:F0},{sz:F0})");
        return bot;
    }

    // v30 (баг 3): сколько секунд корпус подбитого бота лежит на карте, прежде чем
    // его снимут. Достаточно, чтобы клиент доиграл взрыв и игрок увидел фраг.
    private const double WreckLingerSec = 7.0;

    /// <summary>
    /// Полностью убирает бота волны из мира: и с клиента, и из серверных словарей.
    ///
    /// Ключевой момент — пакет. PK_RC_TankDead корпус НЕ удаляет (клиент только
    /// проигрывает смерть, а для уже мёртвого id это вообще ничего не делает).
    /// Удаляет объект PK_RC_PVE_DestroyMonster: в PVEGame.OnPVEDestroyMonster он
    /// вызывает OnPlayerLogout → playerProxy.OnDestroy() + _players.Remove(id),
    /// и дополнительно чистит DictateList, чтобы клиент не сыпал «id not found»
    /// на приходящие следом move-пакеты.
    /// </summary>
    private void DespawnWaveBot(BattleRoom room, RoomPlayer bot)
    {
        try
        {
            if (room.GameMode == BattleGameMode.Defense)
                BroadcastToRoom(room, new PK_RC_PVE_DestroyMonster { PlayerGUID = bot.PlayerGUID });
            else
                BroadcastToRoom(room, new PK_RC_PlayerLogout { PlayerGUID = bot.PlayerGUID });
        }
        catch { /* клиент мог отвалиться */ }

        foreach (var human in room.Players.Values.Where(p => !p.IsBot))
        {
            lock (human.SpotLock)
            {
                human.SpottedEnemies.Remove(bot.PlayerGUID);
                human.SpotHoldUntil.Remove(bot.PlayerGUID);
            }
        }

        // Снимаем из словарей ПОСЛЕ отправки: BroadcastToRoom ходит по room.Players.
        room.Players.TryRemove(bot.PlayerGUID, out RoomPlayer? _);
        _players.TryRemove(bot.PlayerGUID, out RoomPlayer? _);
    }

    // v48.8: периодическая «снежная буря» на Сталинграде. Клиент перерисовывает погоду ТОЛЬКО
    // при СМЕНЕ типа (OnChangeWeather при изменении CurWeatherEffectType) — поэтому чередуем
    // РАЗНЫЕ строки таблицы: снег (17) ↔ ясно (16), а не просто интенсивность. Буря ~40 c →
    // затишье ~30 c → повтор, начинаем с бури. Отдельный поток, стоп по room.BattleEnded.
    private void StartSnowstormLoop(BattleRoom room)
    {
        var wx = GetMapWeather(room.MapId);
        if (wx.type == 0) return;                          // не снежная карта
        byte snowId  = wx.type;                            // 17 для Сталинграда
        byte clearId = StalingradClearWeatherId;           // v61: ясно = id 16 (валидная строка), не 0
        ThreadPool.QueueUserWorkItem(_ =>
        {
            const int stormSec = 40, calmSec = 30, warnSec = 5;
            void Wait(int sec) { for (int t = 0; t < sec && !room.BattleEnded; t++) Thread.Sleep(1000); }
            // старт с ясной погоды; первая буря — после прогноза
            SetRoomWeather(room, clearId, 0f);
            while (!room.BattleEnded)
            {
                // ── затишье ──
                Wait(Math.Max(0, calmSec - warnSec));
                if (room.BattleEnded) return;
                // v61 (пункт 3): нативный прогноз. RoomTips.StartWeahterForcast рисует
                // "надвигается снежная буря через N сек" (battle_message_weather_start),
                // клиент сам ведёт отсчёт. Time — секунды до смены, NextWeather — id строки
                // Table_Weather следующей погоды (17=снег).
                BroadcastToRoom(room, new PK_RC_WeatherForecast { Time = (byte)warnSec, NextWeather = snowId });
                Wait(warnSec);
                if (room.BattleEnded) return;

                // ── буря (снег) ──
                SetRoomWeather(room, snowId, 1.0f);
                BroadcastSystemChat(room, "Снежная буря началась");
                Wait(Math.Max(0, stormSec - warnSec));
                if (room.BattleEnded) return;
                // прогноз прояснения: "снежная буря закончится через N сек" (weather_end)
                BroadcastToRoom(room, new PK_RC_WeatherForecast { Time = (byte)warnSec, NextWeather = clearId });
                Wait(warnSec);
                if (room.BattleEnded) return;

                // ── затишье (ясно) ──
                SetRoomWeather(room, clearId, 0f);
                BroadcastSystemChat(room, "Затишье");
            }
        });
    }

    private void SetRoomWeather(BattleRoom room, byte type, float intensity)
    {
        if (room.BattleEnded) return;
        BroadcastToRoom(room, new PK_RC_SynWeatherEffect
        {
            CurWeatherEffectType      = type,
            CurWeatherEffectIntensity = intensity,
            FogStartDistance          = 30000,
            FogEndDistance            = 50000,
            FogIntensity              = 0f,
        });
    }

    // v49: серверные оповещения в боевой чат (то самое «сообщение в центр экрана»).
    // Клиентский ChatLabelNode.CreateStr рисует строку ТОЛЬКО если находит отправителя в
    // комнате по PlayerGUID (GetPlayerByID), иначе возвращает null и ничего не показывает.
    // Поэтому отправителем ставим GUID реального игрока (в соло+боты — самого игрока); все
    // клиенты в комнате его резолвят. ChatType=1 — общий стиль сообщения.
    private static long SystemChatGuid(BattleRoom room)
    {
        foreach (var p in room.Players.Values) if (!p.IsBot) return p.PlayerGUID;
        foreach (var p in room.Players.Values) return p.PlayerGUID;
        return 0;
    }

    private void BroadcastSystemChat(BattleRoom room, string text)
    {
        if (room.BattleEnded) return;
        long guid = SystemChatGuid(room);
        if (guid == 0) return;
        BroadcastToRoom(room, new PK_RC_Chat { ChatType = 1, PlayerGUID = guid, Message = text });
    }

    private void StartPveWaves(BattleRoom room)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var human = room.Players.Values.FirstOrDefault(p => !p.IsBot);
            if (human == null) return;
            byte playerGroup   = human.Group;
            byte attackerGroup = (byte)(1 - playerGroup);
            var  session       = human.Session;

            for (int wave = 1; wave <= PveWaveTanks.Length && !room.BattleEnded; wave++)
            {
                // 20s cooldown before each wave (5s before the very first).
                //
                // v30 (баг 2): ReadyTime и CurRoundCD — РАЗНЫЕ вещи, раньше в оба
                // уходило одно число:
                //   ReadyTime  → PVEBattleTips.UpdateRoundTips: центральный текст,
                //                тикает вниз и на нуле сменяется на «Наступает
                //                вражеская волна N»;
                //   CurRoundCD → PVEBattleInfo: «Следующая атака: MM:SS» справа сверху,
                //                причём пока ReadyTime > 0 эта надпись принудительно
                //                пустая, так что показывать в ней тот же отсчёт
                //                бессмысленно — она включалась ровно тогда, когда
                //                отсчёт уже кончился.
                // Оба клиент крутит сам, по одному пакету на волну.
                int countdownSec = wave == 1 ? 5 : 20;
                BroadcastToRoom(room, new PK_RC_PVE_RoundBegin
                {
                    ReadyTime  = countdownSec,
                    CurRound   = wave,
                    CurRoundCD = 0,
                });
                Thread.Sleep(countdownSec * 1000);
                if (room.BattleEnded) return;

                // v70: количество танков в волне берём из старого расписания (длина
                // строки PveWaveTanks), а КОНКРЕТНЫЕ модели теперь не фиксированы —
                // каждый бот волны получает случайный немецкий прокачиваемый танк
                // (Nation==2) из диапазона room.EnemyTierLo..EnemyTierHi, посчитанного
                // от уровня(ей) союзника(ов)-людей при старте боя (см. HandlePlayerLogin).
                int waveSize = PveWaveTanks[wave - 1].Length;
                room.WaveKillCount = 0;
                room.CurrentWave   = wave;

                // Spawn THIS wave's tanks as brand-new bots.
                var waveBots = new System.Collections.Generic.List<RoomPlayer>();
                for (int i = 0; i < waveSize; i++)
                {
                    var rolled  = PickGermanTankInTierRange(room.EnemyTierLo, room.EnemyTierHi);
                    int tankId  = rolled?.Id ?? PveWaveTanks[wave - 1][i % PveWaveTanks[wave - 1].Length];
                    waveBots.Add(CreateAttackerBot(room, session, attackerGroup, tankId));
                }

                room.CurrentWaveEnemies = waveBots.Count;
                BroadcastToRoom(room, new PK_RC_PVE_Enemy { CurEnemyNum = waveBots.Count });

                // Wave runs until every bot of this wave is destroyed.
                //
                // v30 (баг 3): подбитые боты убираются НЕ в конце волны, а через
                // WreckLingerSec после смерти каждого. Раньше весь мусор копился до
                // конца волны и вычищался пачкой пакетом PK_RC_TankDead — а он корпус
                // с карты не снимает: клиентский OnTankDead просто проигрывает смерть,
                // и для уже мёртвого бота это no-op. Отсюда «остаётся часть трупов».
                var reaped = new HashSet<long>();
                int waitMs = 0;
                while (!room.BattleEnded && waveBots.Any(b => !b.IsDead) && waitMs < 600_000)
                {
                    int alive = waveBots.Count(b => !b.IsDead);
                    if (alive != room.CurrentWaveEnemies)
                    {
                        room.CurrentWaveEnemies = alive;
                        BroadcastToRoom(room, new PK_RC_PVE_Enemy { CurEnemyNum = alive });
                    }

                    foreach (var bot in waveBots)
                    {
                        if (!bot.IsDead || reaped.Contains(bot.PlayerGUID)) continue;
                        if (bot.DeathUtc == default) { bot.DeathUtc = DateTime.UtcNow; continue; }
                        if ((DateTime.UtcNow - bot.DeathUtc).TotalSeconds < WreckLingerSec) continue;
                        reaped.Add(bot.PlayerGUID);
                        DespawnWaveBot(room, bot);
                    }

                    Thread.Sleep(400);
                    waitMs += 400;
                }
                if (room.BattleEnded) return;

                // Clear this wave's bots off the world entirely before the next wave.
                foreach (var bot in waveBots)
                {
                    if (reaped.Contains(bot.PlayerGUID)) continue;
                    DespawnWaveBot(room, bot);
                }
                BroadcastToRoom(room, new PK_RC_PVE_Enemy   { CurEnemyNum = 0 });
                BroadcastToRoom(room, new PK_RC_PVE_RoundEnd { NullData = 0 });
            }

            if (!room.BattleEnded)
            {
                BroadcastToRoom(room, new PK_RC_PVE_GameEnd { NullData = 0 });
                EndBattle(room, explicitWinnerGroup: human.Group);
            }
        });
    }

    // Helper: change a bot's tank to a wave-appropriate model
    private void EquipBotForWave(RoomPlayer bot, List<TankEntry> pool)
    {
        if (pool.Count == 0) return;
        ApplyTankToBot(bot, pool[_random.Next(pool.Count)]);
    }

    // Equip a bot with a RANDOM tank of a SPECIFIC tier (falls back to any valid
    // tank if that tier has none). Used by the Defense wave schedule so the enemy
    // tier mix matches the real game exactly.
    private void EquipBotForTier(RoomPlayer bot, byte tier)
    {
        var pool = _gameTables.Tanks.Values
            .Where(t => t.Tier == tier && t.HullId > 0 && t.MainGunId > 0 && !NoModelTankIds.Contains(t.Id)).ToList();
        if (pool.Count == 0)
            pool = _gameTables.Tanks.Values.Where(t => t.HullId > 0 && t.MainGunId > 0 && !NoModelTankIds.Contains(t.Id)).ToList();
        if (pool.Count == 0) return;
        ApplyTankToBot(bot, pool[_random.Next(pool.Count)]);
    }

    private void ApplyTankToBot(RoomPlayer bot, TankEntry entry)
    {
        bot.VehicleId  = entry.Id;
        bot.TankID     = (ushort)entry.Id;
        if (entry.HullId     > 0) bot.HullID     = (ushort)entry.HullId;
        if (entry.TurretId   > 0) bot.TurretID   = (ushort)entry.TurretId;
        if (entry.MainGunId  > 0) { bot.MainGunID = (ushort)entry.MainGunId; bot.CurrentShellId = _gameTables.GetGun(bot.MainGunID)?.Ammo1Id ?? bot.CurrentShellId; }
        if (entry.UnderpanId > 0) bot.UnderpanID = (ushort)entry.UnderpanId;
        if (entry.EngineId   > 0) bot.EngineID   = (ushort)entry.EngineId;
        bot.Tier = (byte)entry.Tier;
        int hp = (int)_gameTables.GetRealMaxHp(entry, 144f);
        if (hp > 0) { bot.MaxHp = hp; bot.CurHp = hp; }
        bot.MaxForwardSpeedMps = (entry.Speed > 0 ? entry.Speed : 30f) / 3.6f;
        bot.MaxBackSpeedMps    = bot.MaxForwardSpeedMps * 0.4f;
    }

    // ── Real Defense enemy roster ────────────────────────────────────────────────
    // Taken from the in-game Defense screen: 73 enemy tanks total across the 12
    // waves, distributed by tier — T1:2, T4:1, T5:2, T6:11, T7:9, T8:21, T9:7,
    // T10:20. Sorted ascending and sliced per wave (quota below) so the fight
    // escalates from light tanks to a tier-10 finish, matching the original.
    private static readonly int[] PveWaveQuota = { 5, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 6 }; // sum = 73
    private static readonly byte[] PveEnemyTierSequence = BuildPveTierSequence();
    private static byte[] BuildPveTierSequence()
    {
        var seq = new List<byte>(73);
        void Add(byte tier, int n) { for (int i = 0; i < n; i++) seq.Add(tier); }
        Add(1, 2); Add(4, 1); Add(5, 2); Add(6, 11); Add(7, 9); Add(8, 21); Add(9, 7); Add(10, 20);
        return seq.ToArray();
    }
    // The exact tiers to spawn in a given (1-based) wave — a slice of the sequence.
    private Queue<byte> BuildWaveTierQueue(int wave)
    {
        int start = 0;
        for (int w = 1; w < wave && w <= PveWaveQuota.Length; w++) start += PveWaveQuota[w - 1];
        int count = wave <= PveWaveQuota.Length ? PveWaveQuota[wave - 1] : 6;
        var q = new Queue<byte>(count);
        for (int i = start; i < start + count && i < PveEnemyTierSequence.Length; i++)
            q.Enqueue(PveEnemyTierSequence[i]);
        if (q.Count == 0) q.Enqueue(8); // safety
        return q;
    }


    // ─────────────────────────── shell slots ───────────────────────────────────
    private ShellSlot[] BuildRoomShellSlots(Tank? tank, int gunId)
    {
        var slots = new ShellSlot[3];
        string raw = tank?.ShellSlots ?? string.Empty;
        if (!HasUsableShell(raw)) raw = tank?.DeployShellSlots ?? string.Empty;
        if (!HasUsableShell(raw)) raw = _gameTables.BuildDefaultShellLoadout(gunId).ShellSlots;

        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < slots.Length && i < parts.Length; i++)
        {
            var sc = parts[i].Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (sc.Length >= 2 && int.TryParse(sc[0], out int id) && int.TryParse(sc[1], out int cnt))
                slots[i] = new ShellSlot { ID = id, Count = cnt };
        }

        var gunForValidation = _gameTables.GetGun(gunId);
        bool BelongsToGun(int shellId) => gunForValidation != null &&
            (shellId == gunForValidation.Ammo1Id || shellId == gunForValidation.Ammo2Id || shellId == gunForValidation.Ammo3Id);

        if (!slots.Any(s => s.ID != 0 && s.Count != 0 && BelongsToGun(s.ID)))
        {
            var gun     = _gameTables.GetGun(gunId);
            int shellId = gun?.Ammo1Id ?? 0;
            if (shellId > 0)
            {
                slots[0] = new ShellSlot { ID = shellId, Count = Math.Max(1, gun?.AmmoLoad ?? 20) };
                slots[1] = default; slots[2] = default;
            }
            else
            {
                var any = _gameTables.Shells.Values.FirstOrDefault();
                if (any != null)
                    slots[0] = new ShellSlot { ID = any.Id, Count = 30 };
            }
        }
        return slots;
    }

    private static bool HasUsableShell(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        foreach (var slot in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var sc = slot.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (sc.Length >= 2 && int.TryParse(sc[0], out int id) && int.TryParse(sc[1], out int cnt) && id != 0 && cnt != 0)
                return true;
        }
        return false;
    }

    // ═══════════════════════════ BOT AI ════════════════════════════════════════
    //
    // Architecture:
    //  • Tick rate = exactly 0.12 s (the hardcoded interpolation window the
    //    client uses in OtherTankValuePos = Δpos / 0.12f). Matching it avoids
    //    the accumulating lag → teleport cycle that makes bots appear to "fly".
    //  • Each bot has a Role: Aggressive (straight rush), Flanker (wide arc
    //    approach), or Defensive (hold at medium range). Roles are assigned at
    //    creation to give each team a spread-out tactical formation.
    //  • AssignedZOffset spreads bots across the map's Z-axis so they don't
    //    all converge on the same corridor or building cluster.
    //  • Defense mode: attackers (group opposite to player) drive toward the
    //    defender spawn area instead of hunting the nearest player. Passing
    //    through costs defenders a life; 6 pass-throughs → defender loss.
    //
    private void StartBotAI(BattleRoom room, byte playerGroup, bool isDefense)
    {
        const float tickSeconds = 0.12f;
        const int   tickMs      = (int)(tickSeconds * 1000);
        const float engageRange = 70f;
        const float fireRange   = 140f;
        var mapCfg = GetMapConfig(room.MapId);
        // defensePassX: the X at which an attacker has "broken through" to the defender base
        float defensePassX = mapCfg.Team0.XCenter + mapCfg.Team0.XHalfSpread * 2f;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var sw = Stopwatch.StartNew();
            while (!room.BattleEnded)
            {
                long start = sw.ElapsedMilliseconds;
                try
                {
                    ProcessBotTick(room, playerGroup, engageRange, fireRange,
                                   defensePassX, tickSeconds, isDefense);
                    // Ремонт гусениц игроков-людей (у ботов — внутри ProcessBotTick).
                    foreach (var hp in room.Players.Values)
                        if (!hp.IsBot && !hp.IsDead) CheckTrackRepair(room, hp);
                }
                catch { }
                int elapsed = (int)(sw.ElapsedMilliseconds - start);
                Thread.Sleep(Math.Max(1, tickMs - elapsed));
            }
        });
    }

    // Build a coarse navigation grid (blocked cells) from the map's obstacle boxes,
    // inflated by a tank radius so paths keep clear of walls. Cached per room.
    private void EnsureNavGrid(BattleRoom room)
    {
        if (room.NavBlocked != null) return;
        var cfg = GetMapConfig(room.MapId);
        var obstacles = (room.FileObstacles != null && room.FileObstacles.Length > 0)
            ? room.FileObstacles : cfg.Obstacles;
        float cell = 6f, inflate = 2f;  // мельче сетка и меньше запас — узкие улицы (Лион) перестают быть глухими
        float ox = cfg.XMin, oz = cfg.ZMin;
        int cols = (int)MathF.Ceiling((cfg.XMax - cfg.XMin) / cell) + 1;
        int rows = (int)MathF.Ceiling((cfg.ZMax - cfg.ZMin) / cell) + 1;
        var blocked = new bool[cols, rows];
        var buildingBlocked = new bool[cols, rows];
        var cliffEdge = new bool[cols, rows];
        var hazard = new bool[cols, rows];
        var ground = new float[cols, rows];
        var onBridge = new bool[cols, rows];
        bool hasHeightmap = TerrainHeightmaps.ByMap.ContainsKey(room.MapId);
        for (int c = 0; c < cols; c++)
        {
            float wx = ox + (c + 0.5f) * cell;
            for (int r = 0; r < rows; r++)
            {
                float wz = oz + (r + 0.5f) * cell;
                onBridge[c, r] = TrySampleBridgeDeck(room.MapId, wx, wz, out _);
                float g = GroundAt(room, wx, wz, out bool known);
                ground[c, r] = known ? g : cfg.Team0.Y;
            }
        }
        foreach (var o in obstacles)
        {
            int c0 = Math.Max(0, (int)((o.X1 - inflate - ox) / cell));
            int c1 = Math.Min(cols - 1, (int)((o.X2 + inflate - ox) / cell));
            int r0 = Math.Max(0, (int)((o.Z1 - inflate - oz) / cell));
            int r1 = Math.Min(rows - 1, (int)((o.Z2 + inflate - oz) / cell));
            for (int c = c0; c <= c1; c++)
                for (int r = r0; r <= r1; r++)
                    if (BlocksTankAtY(in o, ground[c, r]))
                        blocked[c, r] = buildingBlocked[c, r] = true;
        }
        if (hasHeightmap)
        {
            // Block sheer cliff edges & canal banks (outside bridge decks), then flood-fill from
            // both team spawns so disconnected water basins / ravines become hard NavHazards.
            int[] dcs = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] drs = { 0, 0, 1, -1, 1, -1, 1, -1 };
            float[] dmuls = { 1f, 1f, 1f, 1f, 1.414f, 1.414f, 1.414f, 1.414f };
            for (int c = 0; c < cols; c++)
            {
                for (int r = 0; r < rows; r++)
                {
                    if (onBridge[c, r]) continue;
                    float g = ground[c, r];
                    for (int k = 0; k < 8; k++)
                    {
                        int nc = c + dcs[k], nr = r + drs[k];
                        if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                        if (MathF.Abs(ground[nc, nr] - g) / (cell * dmuls[k]) > MaxSlopeGrade)
                        {
                            cliffEdge[c, r] = true;
                            blocked[c, r] = true;
                            break;
                        }
                    }
                }
            }
            var reachable = new bool[cols, rows];
            var q = new Queue<(int c, int r)>();
            void EnqueueSeed(float sx, float sz)
            {
                int sc = Math.Clamp((int)((sx - ox) / cell), 0, cols - 1);
                int sr = Math.Clamp((int)((sz - oz) / cell), 0, rows - 1);
                if (!blocked[sc, sr] && !reachable[sc, sr]) { reachable[sc, sr] = true; q.Enqueue((sc, sr)); return; }
                for (int dc = -2; dc <= 2; dc++)
                    for (int dr = -2; dr <= 2; dr++)
                    {
                        int nc = sc + dc, nr = sr + dr;
                        if (nc >= 0 && nc < cols && nr >= 0 && nr < rows && !blocked[nc, nr] && !reachable[nc, nr])
                        { reachable[nc, nr] = true; q.Enqueue((nc, nr)); return; }
                    }
            }
            EnqueueSeed(cfg.Team0.XCenter, cfg.Team0.ZCenter);
            EnqueueSeed(cfg.Team1.XCenter, cfg.Team1.ZCenter);
            while (q.Count > 0)
            {
                var (cc, cr) = q.Dequeue();
                for (int k = 0; k < 4; k++)
                {
                    int nc = cc + dcs[k], nr = cr + drs[k];
                    if (nc >= 0 && nc < cols && nr >= 0 && nr < rows && !blocked[nc, nr] && !reachable[nc, nr])
                    {
                        reachable[nc, nr] = true;
                        q.Enqueue((nc, nr));
                    }
                }
            }
            for (int c = 0; c < cols; c++)
                for (int r = 0; r < rows; r++)
                    if (cliffEdge[c, r] || (!reachable[c, r] && !buildingBlocked[c, r]))
                    {
                        hazard[c, r] = true;
                        blocked[c, r] = true;
                    }
        }
        room.NavBlocked = blocked; room.NavHazard = hazard; room.NavCell = cell;
        room.NavOriginX = ox; room.NavOriginZ = oz;
        room.NavCols = cols; room.NavRows = rows;
        int blk = 0; foreach (var b in blocked) if (b) blk++;
        Console.WriteLine($"[RS] NavGrid map={room.MapId}: {cols}x{rows} cells, {obstacles.Length} boxes, {blk} blocked");
    }

    private static bool IsNavHazard(BattleRoom room, float x, float z)
    {
        var hazard = room.NavHazard;
        if (hazard == null || room.NavCell <= 0f) return false;
        int c = Math.Clamp((int)((x - room.NavOriginX) / room.NavCell), 0, room.NavCols - 1);
        int r = Math.Clamp((int)((z - room.NavOriginZ) / room.NavCell), 0, room.NavRows - 1);
        return hazard[c, r];
    }

    private static bool IsNavCellBlocked(BattleRoom room, float x, float z)
    {
        var blocked = room.NavBlocked;
        if (blocked == null || room.NavCell <= 0f) return false;
        int c = Math.Clamp((int)((x - room.NavOriginX) / room.NavCell), 0, room.NavCols - 1);
        int r = Math.Clamp((int)((z - room.NavOriginZ) / room.NavCell), 0, room.NavRows - 1);
        return blocked[c, r];
    }

    // A* over the nav grid. Returns a list of world-space waypoints, or null if no
    // path. If the target cell is blocked, snaps to the nearest free cell.
    private System.Collections.Generic.List<(float X, float Z)>? FindPath(BattleRoom room, float sx, float sz, float tx, float tz)
    {
        var blocked = room.NavBlocked;
        if (blocked == null) return null;
        float cell = room.NavCell, ox = room.NavOriginX, oz = room.NavOriginZ;
        int cols = room.NavCols, rows = room.NavRows;
        int sc = Math.Clamp((int)((sx - ox) / cell), 0, cols - 1);
        int sr = Math.Clamp((int)((sz - oz) / cell), 0, rows - 1);
        int tc = Math.Clamp((int)((tx - ox) / cell), 0, cols - 1);
        int tr = Math.Clamp((int)((tz - oz) / cell), 0, rows - 1);
        if (blocked[tc, tr])
        {
            bool snapped = false;
            for (int rad = 1; rad <= 14 && !snapped; rad++)
                for (int a = -rad; a <= rad && !snapped; a++)
                    for (int b = -rad; b <= rad && !snapped; b++)
                    {
                        int nc = tc + a, nr = tr + b;
                        if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                        if (!blocked[nc, nr]) { tc = nc; tr = nr; snapped = true; }
                    }
            if (!snapped) return null;
        }
        int Idx(int c, int r) => r * cols + c;
        int start = Idx(sc, sr), goal = Idx(tc, tr);
        var open = new System.Collections.Generic.PriorityQueue<int, float>();
        var came = new System.Collections.Generic.Dictionary<int, int>();
        var gsc  = new System.Collections.Generic.Dictionary<int, float>();
        gsc[start] = 0f; open.Enqueue(start, 0f);
        int[] dcs = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] drs = { 0, 0, 1, -1, 1, -1, 1, -1 };
        float[] cst = { 1f, 1f, 1f, 1f, 1.414f, 1.414f, 1.414f, 1.414f };
        bool ok = false; int guard = 0;
        while (open.Count > 0 && guard++ < 40000)
        {
            int cur = open.Dequeue();
            if (cur == goal) { ok = true; break; }
            int cc = cur % cols, cr = cur / cols;
            for (int k = 0; k < 8; k++)
            {
                int nc = cc + dcs[k], nr = cr + drs[k];
                if (nc < 0 || nc >= cols || nr < 0 || nr >= rows) continue;
                if (blocked[nc, nr]) continue;
                if (k >= 4 && blocked[cc + dcs[k], cr] && blocked[cc, cr + drs[k]]) continue;
                int ni = Idx(nc, nr);
                float ng = gsc[cur] + cst[k];
                if (!gsc.TryGetValue(ni, out var og) || ng < og)
                {
                    gsc[ni] = ng; came[ni] = cur;
                    float h = MathF.Abs(nc - tc) + MathF.Abs(nr - tr);
                    open.Enqueue(ni, ng + h);
                }
            }
        }
        if (!ok) return null;
        var path = new System.Collections.Generic.List<(float X, float Z)>();
        int node = goal, safety = 0;
        while (node != start && safety++ < 6000)
        {
            int cc = node % cols, cr = node / cols;
            path.Add((ox + (cc + 0.5f) * cell, oz + (cr + 0.5f) * cell));
            if (!came.TryGetValue(node, out node)) break;
        }
        path.Reverse();
        // String-pulling: collapse A* grid staircases into clean straight hull segments.
        if (path.Count > 2)
        {
            var cfg = GetMapConfig(room.MapId);
            var obs = (room.FileObstacles != null && room.FileObstacles.Length > 0)
                ? room.FileObstacles : cfg.Obstacles;
            var smoothed = new System.Collections.Generic.List<(float X, float Z)>(path.Count);
            smoothed.Add(path[0]);
            int anchor = 0;
            while (anchor < path.Count - 1)
            {
                int farthest = anchor + 1;
                for (int k = path.Count - 1; k > anchor + 1; k--)
                {
                    float anchorY = SampleHeight(room, path[anchor].X, path[anchor].Z);
                    float endY = SampleHeight(room, path[k].X, path[k].Z);
                    if (!SegmentHitsAnyObstacle(path[anchor].X, path[anchor].Z, path[k].X, path[k].Z, obs, BotHullRadius, MathF.Max(anchorY, endY))
                        && !SegmentTooSteep(room, path[anchor].X, path[anchor].Z, path[k].X, path[k].Z))
                    {
                        farthest = k;
                        break;
                    }
                }
                smoothed.Add(path[farthest]);
                anchor = farthest;
            }
            path = smoothed;
        }
        return path;
    }

    private bool SegmentTooSteep(BattleRoom room, float x1, float z1, float x2, float z2)
    {
        float dist = Distance2D(x1, z1, x2, z2);
        int steps = Math.Max(1, (int)MathF.Ceiling(dist / 4f));
        float px = x1, pz = z1;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float nx = x1 + (x2 - x1) * t;
            float nz = z1 + (z2 - z1) * t;
            if (TooSteep(room, px, pz, nx, nz)) return true;
            px = nx; pz = nz;
        }
        return false;
    }

    // Team-shared perception: enemy GUIDs of the OTHER group that at least one living
    // member of `viewerGroup` currently sees (inside their view range, clear LOS). A
    // building box or a hill on the sightline blocks the spot. O(eyes × foes) but tiny.
    private HashSet<long> ComputeSpotted(BattleRoom room, byte viewerGroup, ObstacleBox[] obstacles)
    {
        var seen = new HashSet<long>();
        var eyes = room.Players.Values.Where(p => p.Group == viewerGroup && !p.IsDead).ToList();
        if (eyes.Count == 0) return seen;
        var now = DateTime.UtcNow;
        foreach (var foe in room.Players.Values)
        {
            if (foe.Group == viewerGroup || foe.IsDead) continue;
            foreach (var eye in eyes)
            {
                if (!IsSpotDetectedBy(eye, foe, room, obstacles, now)) continue;
                seen.Add(foe.PlayerGUID);
                break;
            }
        }
        return seen;
    }

    // ── Phase 4: team focus target ─────────────────────────────────────────────
    // The one spotted enemy the whole team should concentrate fire on. In Generals
    // mode that's the enemy general the instant it's spotted (killing it wins); any
    // other time it's the weakest spotted enemy, so the team finishes wounded tanks
    // fast and removes guns from the fight instead of spreading damage around.
    private long ComputeFocusTarget(BattleRoom room, byte teamGroup, HashSet<long> spotted)
    {
        if (room.GameMode == BattleGameMode.Commander)
        {
            byte enemyGrp = (byte)(1 - teamGroup);
            if (room.CommanderGuid.TryGetValue(enemyGrp, out var gGuid)
                && spotted.Contains(gGuid)
                && room.Players.TryGetValue(gGuid, out var gen) && !gen.IsDead)
                return gGuid;
        }
        // ── v23 (п.4): умный выбор общей цели вместо «у кого меньше ХП» ──────────
        // Считаем ценность каждой цели: добить раненого выгоднее, ближняя цель —
        // надёжнее (меньше промахов и препятствий), обездвиженный враг почти
        // гарантированный фраг, а живой игрок опаснее бота и должен давиться первым.
        RoomPlayer? best = null;
        float bestScore = float.MinValue;
        // центр масс своей команды — от него меряем «близость» цели для всей группы
        float cx = 0f, cz = 0f; int n = 0;
        foreach (var a in room.Players.Values)
            if (a.Group == teamGroup && !a.IsDead) { cx += a.PosX; cz += a.PosZ; n++; }
        if (n > 0) { cx /= n; cz /= n; }

        foreach (var p in room.Players.Values)
        {
            if (p.Group == teamGroup || p.IsDead || !spotted.Contains(p.PlayerGUID)) continue;
            float hpFrac = p.MaxHp > 0 ? (float)p.CurHp / p.MaxHp : 1f;
            float dist   = n > 0 ? Distance2D(cx, cz, p.PosX, p.PosZ) : 0f;
            float score  = 0f;
            score += (1f - hpFrac) * 60f;                       // добить раненого
            score += MathF.Max(0f, 60f - dist * 0.15f);         // ближе — лучше
            if (p.ChassisLDamaged || p.ChassisRDamaged) score += 35f;  // обездвижен
            if (!p.IsBot)                              score += 25f;  // живой игрок опаснее
            if (p.IsCommander)                         score += 40f;  // генерал — главная цель
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best?.PlayerGUID ?? 0L;
    }

    // Distance from a point to the nearest obstacle box (0 if inside one). Used to
    // reward firing positions tucked against cover.
    private static float NearestObstacleDist(float x, float z, ObstacleBox[] obstacles)
    {
        float best = float.MaxValue;
        foreach (var b in obstacles)
        {
            float dx = MathF.Max(MathF.Max(b.X1 - x, x - b.X2), 0f);
            float dz = MathF.Max(MathF.Max(b.Z1 - z, z - b.Z2), 0f);
            float d = MathF.Sqrt(dx * dx + dz * dz);
            if (d < best) best = d;
        }
        return best == float.MaxValue ? 9999f : best;
    }

    // ── Phase 3: pick a tactical firing position ───────────────────────────────
    // Instead of driving straight at the enemy, evaluate a ring of nearby spots (plus
    // the ideal-range point on the line to the enemy) and choose one that (a) has a
    // CLEAR SHOT at the target, (b) sits near the class's preferred range, and (c) is
    // tucked against cover (peek / hull-down). Returns found=false if nowhere nearby
    // can see the target (caller then just closes in to find an angle).
    private (float x, float z, bool found) PickFiringPosition(
        BattleRoom room, RoomPlayer bot, RoomPlayer enemy, in CombatProfile prof, ObstacleBox[] obstacles)
    {
        var cfg = GetMapConfig(room.MapId);
        float ex = enemy.PosX, ez = enemy.PosZ, ey = enemy.PosY + 1.5f;

        Span<float> cxs = stackalloc float[18];
        Span<float> czs = stackalloc float[18];
        int n = 0;
        for (int a = 0; a < 8; a++)
        {
            float ang = a * (MathF.PI / 4f);
            float cx = MathF.Cos(ang), sz = MathF.Sin(ang);
            cxs[n] = bot.PosX + cx * 28f; czs[n] = bot.PosZ + sz * 28f; n++;
            cxs[n] = bot.PosX + cx * 55f; czs[n] = bot.PosZ + sz * 55f; n++;
        }
        // Ideal-range point on the bot's side of the line to the enemy.
        float ddx = bot.PosX - ex, ddz = bot.PosZ - ez;
        float dl = MathF.Sqrt(ddx * ddx + ddz * ddz); if (dl < 0.01f) dl = 0.01f;
        cxs[n] = ex + ddx / dl * prof.PreferredRange; czs[n] = ez + ddz / dl * prof.PreferredRange; n++;
        cxs[n] = bot.PosX; czs[n] = bot.PosZ; n++;   // holding here is an option too

        float bestScore = float.NegativeInfinity;
        float bx = bot.PosX, bz = bot.PosZ; bool found = false;
        for (int i = 0; i < n; i++)
        {
            float px = cxs[i], pz = czs[i];
            if (px < cfg.XMin + 6f || px > cfg.XMax - 6f || pz < cfg.ZMin + 6f || pz > cfg.ZMax - 6f) continue;
            if (IsNavCellBlocked(room, px, pz) || IsNavHazard(room, px, pz)) continue;
            float groundY = SampleHeight(room, px, pz);
            if (InsideObstacle(px, pz, obstacles, 0f, groundY)) continue;
            if (StepHitsTankOrWreck(room, bot, px, pz, px, pz, SolidTankSpacing)) continue;
            float py = groundY + 1.5f;
            if (SegmentBlocked3D(px, py, pz, ex, ey, ez, obstacles)) continue;      // must be able to shoot
            if (TerrainBlocksShot(room, px, pz, py, ex, ez, ey)) continue;
            float d = Distance2D(px, pz, ex, ez);
            float score = -MathF.Abs(d - prof.PreferredRange);            // closeness to preferred range
            if (d < prof.StandoffRange) score -= (prof.StandoffRange - d) * 1.5f;
            float cover = NearestObstacleDist(px, pz, obstacles);         // hug cover
            if (cover < 16f) score += (prof.UseHullDown ? 45f : 25f) * (1f - cover / 16f);
            if (score > bestScore) { bestScore = score; bx = px; bz = pz; found = true; }
        }
        return (bx, bz, found);
    }

    // Cached wrapper around PickFiringPosition: recomputes at most ~every 1.5 s (or when
    // the target has moved > 25 units), so it stays cheap and the chosen spot doesn't
    // jitter tick-to-tick.
    private (float x, float z, bool found) FirePos(
        RoomPlayer bot, RoomPlayer enemy, in CombatProfile prof, ObstacleBox[] obstacles)
    {
        bool stale = DateTime.UtcNow >= bot.FirePosUntil
            || Distance2D(bot.FirePosForEnemyX, bot.FirePosForEnemyZ, enemy.PosX, enemy.PosZ) > 25f;
        if (stale && bot.Room != null)
        {
            var (fx, fz, ok) = PickFiringPosition(bot.Room, bot, enemy, prof, obstacles);
            bot.FirePosX = fx; bot.FirePosZ = fz; bot.FirePosValid = ok;
            bot.FirePosForEnemyX = enemy.PosX; bot.FirePosForEnemyZ = enemy.PosZ;
            bot.FirePosUntil = DateTime.UtcNow.AddSeconds(1.5);
        }
        return (bot.FirePosX, bot.FirePosZ, bot.FirePosValid);
    }

    private void ProcessBotTick(BattleRoom room, byte playerGroup,
        float engageRange, float fireRange, float defensePassX, float dt,
        bool isDefenseMode)
    {
        byte attackerGroup  = (byte)(1 - playerGroup); // enemies in Defense mode
        if (!room.BotAiModeLogged)
        {
            room.BotAiModeLogged = true;
            Console.WriteLine($"[RS] BotAI: map={room.MapId} isDefense={isDefenseMode} playerGroup={playerGroup} attackerGroup={attackerGroup}");
        }

        // ── PERCEPTION (team-shared vision), computed ONCE for the whole tick ──────
        // A bot no longer omnisciently knows every enemy: an enemy only counts as
        // "spotted" if some living member of that bot's team has it inside their view
        // range with a clear line of sight (buildings/hills block the spot). This is
        // what makes lights valuable (they extend the team's vision), lets the player
        // break contact by using cover, and stops TDs firing through walls at things
        // nobody can see. Computed per team so both sides get honest vision.
        var visObstacles = (room.FileObstacles != null && room.FileObstacles.Length > 0)
            ? room.FileObstacles : GetMapConfig(room.MapId).Obstacles;
        var spottedByGroup0 = ComputeSpotted(room, 0, visObstacles);
        var spottedByGroup1 = ComputeSpotted(room, 1, visObstacles);
        // Phase 4 — team focus fire: one shared priority target per side.
        long focusGroup0 = ComputeFocusTarget(room, 0, spottedByGroup0);
        long focusGroup1 = ComputeFocusTarget(room, 1, spottedByGroup1);

        foreach (var bot in room.Players.Values)
        {
            if (!bot.IsBot || bot.IsDead) continue;
            // Pre-battle prep: bot sits at its spawn (no move dictate, no fire) until
            // the 25 s countdown ends. It's already visible to clients from CreateMonster.
            if (DateTime.UtcNow < room.CombatStartUtc) continue;

            // Declare once per bot tick — used by patrol, obstacle avoidance, and bounds clamping
            var mapCfg = GetMapConfig(room.MapId);

            // Building boxes for this map (loaded file/embedded, else hard-coded).
            // Computed once here so waypoint picking can also avoid placing a target
            // inside a building.
            var botObstacles = (room.FileObstacles != null && room.FileObstacles.Length > 0)
                ? room.FileObstacles
                : mapCfg.Obstacles;

            // ── ground correction (terrain-following) ──────────────────────────
            // Snap the bot to the real terrain height sampled from player movement.
            // This is what stops bots floating in the air or sinking under the map.
            bot.PosY = SampleHeight(room, bot.PosX, bot.PosZ);

            // Diagnostic: log one moving bot's height every ~2s so you can compare it
            // to where you (the grounded player) are standing. If this Y matches your
            // own Y at the same spot but the bot still looks airborne, the server Y is
            // correct and the hover is purely client-side (only the local player is
            // ground-clamped by the game) — fix it with OtherTankPosition_ЗЕМЛЯ.cs.
            if (DateTime.UtcNow >= room.NextBotYLog)
            {
                room.NextBotYLog = DateTime.UtcNow.AddSeconds(2);
                float surf = TerrainHeightmaps.ByMap.TryGetValue(room.MapId, out var hmd)
                    ? hmd.SurfaceBilinear(bot.PosX, bot.PosZ) : float.NaN;
                Console.WriteLine($"[RS] botY map={room.MapId} guid={bot.PlayerGUID} Y={bot.PosY:F1} " +
                                  $"(terrain={surf:F1}+{BotGroundOffsetY:F1}) at X={bot.PosX:F0} Z={bot.PosZ:F0}");
            }

            // ── target acquisition through the PERCEPTION layer ────────────────
            var prof    = bot.Profile;
            var spotted = bot.Group == 0 ? spottedByGroup0 : spottedByGroup1;
            // Only enemies the team can actually SEE are candidates.
            var enemies = room.Players.Values
                .Where(p => p.Group != bot.Group && !p.IsDead && spotted.Contains(p.PlayerGUID))
                .ToList();
            // Pick WHICH spotted enemy to fight. Phase 4 focus fire: if the team's
            // shared focus target is spotted AND within this bot's reach, gang up on it;
            // otherwise fall back to the vehicle's own doctrine (heavy→nearest,
            // medium/light→weakest, TD→highest-value).
            long focus = bot.Group == 0 ? focusGroup0 : focusGroup1;
            RoomPlayer? focusP = null;
            if (focus != 0L && room.Players.TryGetValue(focus, out var fp)
                && !fp.IsDead && enemies.Contains(fp)
                && Distance2D(bot, fp) < prof.DetectRange)
                focusP = fp;
            RoomPlayer? nearestEnemy = focusP ?? prof.Target switch
            {
                TargetPriority.Weakest   => enemies.OrderBy(p => p.CurHp).FirstOrDefault(),
                TargetPriority.HighValue => enemies.OrderByDescending(p => p.MaxHp)
                                                   .ThenBy(p => Distance2D(bot, p)).FirstOrDefault(),
                _                        => enemies.OrderBy(p => Distance2D(bot, p)).FirstOrDefault(),
            };
            float nearestDist = nearestEnemy != null ? Distance2D(bot, nearestEnemy) : float.MaxValue;
            // Remember where we last saw something, so we can push to its last-known
            // spot after it slips out of sight instead of forgetting it instantly.
            if (nearestEnemy != null)
            {
                bot.LastSeenEnemyX = nearestEnemy.PosX;
                bot.LastSeenEnemyZ = nearestEnemy.PosZ;
                bot.LastSeenEnemyTime = DateTime.UtcNow;
            }

            // Phase 5 — aim tracking: reset the aim clock whenever the target changes,
            // so the bot must re-acquire (reaction delay) before it can fire, and its
            // accuracy builds up the longer it holds the same target (convergence).
            if (nearestEnemy == null)
                bot.AimTargetGuid = 0;
            else if (nearestEnemy.PlayerGUID != bot.AimTargetGuid)
            {
                bot.AimTargetGuid = nearestEnemy.PlayerGUID;
                bot.AimStartTime  = DateTime.UtcNow;
            }

            bool  isDefenseAttacker = isDefenseMode && bot.Group == attackerGroup;
            bool  isDefender        = isDefenseMode && bot.Group == playerGroup; // союзники игрока в обороне

            // ── select movement target ─────────────────────────────────────────
            float targetX, targetZ;

            // Detection/pursue radius = this tank's vision. Defense attackers press the
            // whole map so the assault never stalls out of contact.
            float engageRadius = isDefenseAttacker ? 600f : prof.DetectRange;
            // Distance at which the bot plants to fire = its preferred engagement range.
            float fireStopRange = MathF.Max(prof.PreferredRange, 40f);

            // ── ENGAGEMENT: stop-and-shoot at the vehicle's PREFERRED range ────
            // The bot plants and fires as soon as it has a clear shot at/under its
            // preferred range (heavy ~90, medium ~125, TD ~200, light ~165) — it does
            // NOT roll in closer than that, which is what a real class would do.
            bool hasShot = nearestEnemy != null
                && nearestDist < fireStopRange
                && !SegmentBlocked3D(bot.PosX, bot.PosY + 1f, bot.PosZ, nearestEnemy.PosX, nearestEnemy.PosY + 1f, nearestEnemy.PosZ, botObstacles)
                && !TerrainBlocksShot(room, bot.PosX, bot.PosZ, bot.PosY + 1f,
                                            nearestEnemy.PosX, nearestEnemy.PosZ, nearestEnemy.PosY + 1f);
            if (hasShot) bot.LastLosTime = DateTime.UtcNow;

            // Too close for this vehicle's comfort (TDs/lights): it wants to back off
            // to its stand-off distance rather than brawl. Heavies have StandoffRange 0
            // so this never triggers for them.
            bool tooClose = nearestEnemy != null && nearestDist < prof.StandoffRange;

            // Sticky engagement (hysteresis): once planted, stay planted until the
            // target leaves range (×1.35), dies, or the shot has been blocked for over
            // 2 s (it took hard cover → go maneuver for a new angle). A bot that wants
            // to back off is not "engaged-in-place".
            bool engaged;
            if (bot.HoldingPosition)
                engaged = nearestEnemy != null && !tooClose
                          && nearestDist < fireStopRange * 1.35f
                          && (DateTime.UtcNow - bot.LastLosTime).TotalSeconds < 2.0;
            else
                engaged = hasShot && !tooClose;
            bot.HoldingPosition = engaged;

            // v48.6: союзные боты обороны используют способности (ремонт/боекомплект/адреналин).
            if (isDefender) TryBotDefenderSkills(room, bot, engaged);

            // Each bot patrols its OWN map half — never crosses into the enemy spawn.
            // This stops the "bot drives to player spawn and teleports back" loop.
            //
            // Which axis actually separates Team0 from Team1 varies by map —
            // Berlin and Kan both spawn the two teams on the SAME X, split
            // north/south along Z, while Caucasus splits east/west along X.
            // Hardcoding "X always divides the teams" (the old assumption)
            // would patrol bots sideways into territory that has nothing to
            // do with where they actually spawned on those maps. Pick
            // whichever axis has the bigger gap between the two team centres.
            const float waypointArrivalDist = 25f;
            bool arrivedAtWaypoint = Distance2D(bot.PosX, bot.PosZ, bot.WaypointX, bot.WaypointZ) < waypointArrivalDist;
            if (arrivedAtWaypoint || (bot.WaypointX == 0 && bot.WaypointZ == 0))
            {
              if (isDefender)
              {
                // v28: РУБЕЖ ОБОРОНЫ вместо спавна.
                // Раньше точкой патруля был центр собственной базы — поэтому между
                // волнами защитники разворачивались и уезжали на спавн, вместо того
                // чтобы держать подходы. Теперь цель смещается на 35% пути в сторону
                // вражеской зоны: бот стоит ВПЕРЕДИ базы, лицом к наступающим, но не
                // убегает к ним навстречу. Разброс держит группу компактной.
                var bz  = playerGroup == 0 ? mapCfg.Team0 : mapCfg.Team1;
                var ez  = playerGroup == 0 ? mapCfg.Team1 : mapCfg.Team0;
                float lineX = bz.XCenter + (ez.XCenter - bz.XCenter) * 0.35f;
                float lineZ = bz.ZCenter + (ez.ZCenter - bz.ZCenter) * 0.35f;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    bot.WaypointX = lineX + _random.Next(-40, 41);
                    bot.WaypointZ = lineZ + _random.Next(-40, 41);
                    bot.WaypointX = Math.Clamp(bot.WaypointX, mapCfg.XMin + 10f, mapCfg.XMax - 10f);
                    bot.WaypointZ = Math.Clamp(bot.WaypointZ, mapCfg.ZMin + 10f, mapCfg.ZMax - 10f);
                    if (!IsNavCellBlocked(room, bot.WaypointX, bot.WaypointZ)
                        && (botObstacles.Length == 0 || !InsideObstacle(bot.WaypointX, bot.WaypointZ, botObstacles, 0f, SampleHeight(room, bot.WaypointX, bot.WaypointZ))))
                        break;
                }
              }
              else
              {
                // ПОИСК ПО ВСЕЙ КАРТЕ: бот выбирает точку в пределах ВСЕЙ карты (а не своей
                // половины), чтобы активно искать врага повсюду. Точка — НЕ внутри здания и
                // НЕ вплотную к вражескому спавну (чтобы не толпиться на споте). Больше попыток,
                // т.к. на плотных картах (Каен) валидную точку найти труднее.
                float exMinX = mapCfg.XMin + 25f, exMaxX = mapCfg.XMax - 25f;
                float exMinZ = mapCfg.ZMin + 25f, exMaxZ = mapCfg.ZMax - 25f;
                var enemyZone = bot.Group == 0 ? mapCfg.Team1 : mapCfg.Team0;
                bool picked = false;

                // Если есть граф маршрутов — патрулируем ПО его узлам: бот берёт случайный узел
                // нарисованного маршрута как waypoint и едет к нему обычным A* (надёжно, без
                // хрупкой steer-логики). Узел — в границах, не в здании, не у вражеского спавна.
                if (room.RouteGraph != null && !room.RouteGraph.IsEmpty)
                {
                    // v55: узел маршрута должен вести К вражеской базе (быть ближе к ней, чем бот
                    // сейчас) — иначе боты катаются по маршруту в центр/назад вместо наступления
                    // (баг на Берлине: ехали через мост в центр).
                    float botToEnemy = Distance2D(bot.PosX, bot.PosZ, enemyZone.XCenter, enemyZone.ZCenter);
                    for (int attempt = 0; attempt < 25; attempt++)
                    {
                        if (!room.RouteGraph.TryGetRandomNode(_random, out float nx, out float nz)) break;
                        nx = Math.Clamp(nx, exMinX, exMaxX); nz = Math.Clamp(nz, exMinZ, exMaxZ);
                        if (Distance2D(nx, nz, enemyZone.XCenter, enemyZone.ZCenter) < 45f) continue;
                        if (Distance2D(nx, nz, enemyZone.XCenter, enemyZone.ZCenter) > botToEnemy - 10f) continue; // ведёт к врагу
                        if (IsNavCellBlocked(room, nx, nz) || IsNavHazard(room, nx, nz)) continue;
                        if (botObstacles.Length != 0 && InsideObstacle(nx, nz, botObstacles, 0f, SampleHeight(room, nx, nz))) continue;
                        // Точка должна быть ДОСТИЖИМА (иначе бот упирается в дома/красную линию)
                        if (FindPath(room, bot.PosX, bot.PosZ, nx, nz) == null) continue;
                        bot.WaypointX = nx; bot.WaypointZ = nz; picked = true; break;
                    }
                }

                // v55 (пути, п.1/3): если по графу не выбрали — вейпоинт СМЕЩЁН К ВРАЖЕСКОЙ БАЗЕ с
                // боковым разбросом. Выбираем только валидную (не в воде/обрыве/здании) точку.
                if (!picked)
                {
                    float ex = enemyZone.XCenter, ez = enemyZone.ZCenter;
                    float adx = ex - bot.PosX, adz = ez - bot.PosZ;
                    float dl = MathF.Sqrt(adx * adx + adz * adz);
                    if (dl < 1f) { adx = 0f; adz = 1f; dl = 1f; }
                    adx /= dl; adz /= dl;
                    float apx = -adz, apz = adx;            // перпендикуляр для бокового разброса по фронту
                    float fx = bot.PosX, fz = bot.PosZ;
                    bool hasFallback = false;
                    for (int attempt = 0; attempt < 30; attempt++)
                    {
                        float frac = 0.4f + (float)_random.NextDouble() * 0.5f;             // 40..90% пути к базе
                        float side = ((float)_random.NextDouble() * 2f - 1f) * dl * 0.35f;  // боковой разброс
                        float wx = Math.Clamp(bot.PosX + adx * dl * frac + apx * side, exMinX, exMaxX);
                        float wz = Math.Clamp(bot.PosZ + adz * dl * frac + apz * side, exMinZ, exMaxZ);
                        if (Distance2D(wx, wz, ex, ez) < 45f) continue;                     // не в сам спавн врага
                        if (IsNavCellBlocked(room, wx, wz) || IsNavHazard(room, wx, wz)) continue;
                        if (botObstacles.Length != 0 && InsideObstacle(wx, wz, botObstacles, 0f, SampleHeight(room, wx, wz))) continue;
                        if (!hasFallback) { fx = wx; fz = wz; hasFallback = true; }
                        if (FindPath(room, bot.PosX, bot.PosZ, wx, wz) == null) continue;
                        bot.WaypointX = wx; bot.WaypointZ = wz; picked = true; break;       // достижима — лучшая
                    }
                    if (!picked) { bot.WaypointX = fx; bot.WaypointZ = fz; }
                }
              }
            }

            if (engaged)
            {
                // Clear shot at/under preferred range → PLANT and fire. Speed is forced
                // to 0 below and separation/wander are suppressed, so the bot sits dead
                // still facing the target instead of skating or wobbling.
                targetX = bot.PosX; targetZ = bot.PosZ;
            }
            else if (tooClose && nearestEnemy != null)
            {
                // Enemy is inside this vehicle's comfort range (a TD/light that got
                // rushed) → reverse away from it toward its stand-off distance, still
                // facing the enemy so the gun stays on target while backing off.
                float ax = bot.PosX - nearestEnemy.PosX, az = bot.PosZ - nearestEnemy.PosZ;
                float al = MathF.Sqrt(ax * ax + az * az); if (al < 0.01f) al = 0.01f;
                targetX = bot.PosX + (ax / al) * 60f;
                targetZ = bot.PosZ + (az / al) * 60f;
            }
            else if (isDefenseMode && nearestEnemy != null)
            {
                // Enemy spotted but no clean shot yet → move to a tactical firing spot
                // (LOS + preferred range + cover), or close in if nowhere near can see it.
                var (fx, fz, ok) = FirePos(bot, nearestEnemy, prof, botObstacles);
                targetX = ok ? fx : nearestEnemy.PosX;
                targetZ = ok ? fz : nearestEnemy.PosZ;
            }
            else if (isDefenseAttacker)
            {
                // Живых защитников не видно → едем на их базу.
                var baseZone = playerGroup == 0 ? mapCfg.Team0 : mapCfg.Team1;
                targetX = baseZone.XCenter + bot.AssignedZOffset * 0.4f;
                targetZ = baseZone.ZCenter + bot.AssignedZOffset;
            }
            else if (nearestEnemy != null && nearestDist < engageRadius)
            {
                // Enemy spotted, out of fire range or shot blocked → move to a tactical
                // firing spot (LOS + preferred range + cover); close in if none found.
                var (fx, fz, ok) = FirePos(bot, nearestEnemy, prof, botObstacles);
                targetX = ok ? fx : nearestEnemy.PosX;
                targetZ = ok ? fz : nearestEnemy.PosZ;
            }
            else if ((DateTime.UtcNow - bot.LastSeenEnemyTime).TotalSeconds < 4.0
                     && Distance2D(bot.PosX, bot.PosZ, bot.LastSeenEnemyX, bot.LastSeenEnemyZ) > 8f)
            {
                // Lost sight of the enemy but saw it moments ago → push to its last-known
                // position to re-acquire, instead of instantly forgetting it (perception
                // memory). Doesn't fire — there's nothing spotted to fire at.
                targetX = bot.LastSeenEnemyX; targetZ = bot.LastSeenEnemyZ;
            }
            else
            {
                // Nothing seen → patrol to the current waypoint.
                targetX = bot.WaypointX;
                targetZ = bot.WaypointZ;
            }

            // Route-following ВРЕМЕННО ОТКЛЮЧЕНО. Нарисованные маршруты сняты с миникарты и
            // требуют калибровки по driven-координатам — без неё уводят ботов не туда и в
            // застревание (как было на Каене). Пока боты ищут врага по ВСЕЙ карте через A* +
            // коллизию. Граф маршрутов грузится и готов — включить можно флагом ниже.
            const bool useRouteGraph = false;  // steer-override ВЫКЛ; маршруты теперь через узлы-waypoint'ы в патруле выше
#pragma warning disable CS0162
            if (useRouteGraph && !engaged && !tooClose && room.RouteGraph != null && !room.RouteGraph.IsEmpty
                && (isDefenseAttacker || nearestEnemy == null))
            {
                if (room.RouteGraph.TryGetSteerTarget(bot.PosX, bot.PosZ, targetX, targetZ,
                                                      out float rwx, out float rwz))
                { targetX = rwx; targetZ = rwz; }
            }
#pragma warning restore CS0162

            // ── anti-stuck escape ──────────────────────────────────────────────
            // While wedged, ignore the normal target and drive toward the open
            // direction chosen when the bot got stuck, until EscapeUntil elapses.
            if (DateTime.UtcNow < bot.EscapeUntil)
            {
                targetX = bot.PosX + bot.EscapeDirX * 60f;
                targetZ = bot.PosZ + bot.EscapeDirZ * 60f;
            }

            // Держать цель СТРОГО в пределах карты (в т.ч. после anti-stuck) — бот не поедет за красную линию.
            targetX = Math.Clamp(targetX, mapCfg.XMin + 5f, mapCfg.XMax - 5f);
            targetZ = Math.Clamp(targetZ, mapCfg.ZMin + 5f, mapCfg.ZMax - 5f);

            // ── A* pathfinding around buildings ────────────────────────────────
            // Follow a grid path toward the desired target so bots route AROUND
            // blocks/walls instead of grinding into them. Skipped when holding still.
            bool followingNavPath = false;
            if (!(targetX == bot.PosX && targetZ == bot.PosZ))
            {
                EnsureNavGrid(room);
                bool goalMoved = (MathF.Abs(targetX - bot.NavGoalX) + MathF.Abs(targetZ - bot.NavGoalZ)) > 18f;
                bool navRecompute = bot.NavPath == null
                    || bot.NavPathIndex >= bot.NavPath.Count
                    || (DateTime.UtcNow >= bot.NavNextRecompute && goalMoved)
                    || DateTime.UtcNow >= bot.NavNextRecompute.AddSeconds(3.0);
                if (navRecompute)
                {
                    var newPath = FindPath(room, bot.PosX, bot.PosZ, targetX, targetZ);
                    if (newPath != null || bot.NavPath == null || bot.NavPathIndex >= bot.NavPath.Count)
                    {
                        bot.NavPath = newPath;
                        bot.NavPathIndex = 0;
                    }
                    bot.NavGoalX = targetX; bot.NavGoalZ = targetZ;
                    bot.NavNextRecompute = DateTime.UtcNow.AddMilliseconds(1400);
                    if (bot.NavPath == null && nearestEnemy == null)
                    { bot.WaypointX = bot.PosX; bot.WaypointZ = bot.PosZ; }
                }
                if (bot.NavPath != null && bot.NavPath.Count > 0)
                {
                    while (bot.NavPathIndex < bot.NavPath.Count &&
                           Distance2D(bot.PosX, bot.PosZ, bot.NavPath[bot.NavPathIndex].X, bot.NavPath[bot.NavPathIndex].Z) < 10f)
                        bot.NavPathIndex++;
                    if (bot.NavPathIndex < bot.NavPath.Count)
                    {
                        targetX = bot.NavPath[bot.NavPathIndex].X;
                        targetZ = bot.NavPath[bot.NavPathIndex].Z;
                        followingNavPath = true;
                    }
                }
            }

            float dx   = targetX - bot.PosX;
            float dz   = targetZ - bot.PosZ;
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 0.001f) dist = 0.001f;

            // ── obstacle avoidance: steer around buildings ─────────────────────
            // When following a valid A* path, the waypoints are ALREADY routed
            // clear of buildings (the nav grid is inflated by the tank radius), so
            // the reactive repulsion below only FIGHTS the path: at a corridor mouth
            // the buildings ahead push the bot backward and cancel the A* "go
            // forward", so it jitters on the spot and never enters the street. Trust
            // the path + SteerAround (hard swept collision) there. Apply the soft
            // repulsion ONLY when steering freely with no path (open-field pursuit),
            // where it helps skirt a lone building.
            if (!followingNavPath)
            {
                (dx, dz) = ComputeObstacleAvoidance(bot.PosX, bot.PosZ, botObstacles, dx, dz);
                dist = MathF.Sqrt(dx * dx + dz * dz);
                if (dist < 0.001f) dist = 0.001f;
            }

            // ── separation: don't let friendly bots pile onto one spot ─────────
            // Every bot targets the same nearest enemy / base point, so without a
            // spacing rule they converge into a single blob and freeze there.
            // Nudge each bot away from nearby SAME-TEAM tanks (gentle, so it spreads
            // the pack laterally without pulling anyone off the A* route).
            float sepX = 0f, sepZ = 0f;
            const float SepRadius = 26f;
            foreach (var mate in room.Players.Values)
            {
                if (ReferenceEquals(mate, bot) || mate.IsDead || mate.Group != bot.Group) continue;
                float mx = bot.PosX - mate.PosX, mz = bot.PosZ - mate.PosZ;
                float m2 = mx * mx + mz * mz;
                if (m2 > 0.01f && m2 < SepRadius * SepRadius)
                {
                    float md = MathF.Sqrt(m2);
                    float w  = 1f - md / SepRadius;      // stronger the closer they are
                    sepX += (mx / md) * w; sepZ += (mz / md) * w;
                }
            }
            float sepMag = MathF.Sqrt(sepX * sepX + sepZ * sepZ);

            // ── wander/flank offset ───────────────────────────────────────────
            if (DateTime.UtcNow >= bot.NextWanderChangeTime)
            {
                bot.WanderOffset       = ((float)_random.NextDouble() - 0.5f) * 10f;
                bot.NextWanderChangeTime = DateTime.UtcNow.AddSeconds(2.5 + _random.NextDouble() * 2.5);
            }

            // Wander/flank amount by movement style: evasive lights snake the most,
            // hit-and-run mediums a fair bit, steady heavies a little, static TDs almost
            // none (they hold a line, they don't weave).
            float wanderGain = prof.Move switch
            {
                MovementStyle.Evasive   => 1.4f,
                MovementStyle.HitAndRun => 1.0f,
                MovementStyle.Static    => 0.15f,
                _                       => 0.5f,   // Steady
            };
            float baseWander = bot.WanderOffset * wanderGain;
            // On a nav path, keep to the routed line — a big perpendicular offset
            // would shove the bot sideways off the corridor into the flanking
            // buildings, re-creating the "stuck at the wall" problem the path solves.
            if (followingNavPath) baseWander *= 0.15f;
            if (nearestEnemy != null) baseWander *= 0.2f; // видит врага → ехать почти прямо, не вилять вбок
            if (engaged) baseWander = 0f;   // planted to fire → no wander wobble

            float perpX = -dz / dist, perpZ = dx / dist;
            float adjDx = dx + perpX * baseWander;
            float adjDz = dz + perpZ * baseWander;
            // spread from crowding: sepGain has a floor so even an ARRIVED bot
            // (dx≈dz≈0) still shuffles apart, but stays a minority of the heading
            // while driving so it never overrides the route. Suppressed while engaged
            // so a bot that has a shot stays perfectly still instead of drifting.
            if (sepMag > 0.001f && !engaged)
            {
                float sepGain = MathF.Max(6f, MathF.Min(dist, 30f) * 0.45f);
                adjDx += (sepX / sepMag) * sepGain;
                adjDz += (sepZ / sepMag) * sepGain;
            }
            float adjDist = MathF.Sqrt(adjDx * adjDx + adjDz * adjDz);
            if (adjDist < 0.001f) adjDist = 0.001f;

            // ── retreat when HP critical (threshold by class) ──────────────────
            float retreatFrac = bot.IsCommander ? MathF.Max(prof.RetreatHpFrac, 0.45f) : prof.RetreatHpFrac;
            bool retreating = bot.CurHp < bot.MaxHp * retreatFrac;
            float moveGoalDx = retreating ? -adjDx : adjDx;
            float moveGoalDz = retreating ? -adjDz : adjDz;

            float arriveThreshold = 4f;
            float roleSpeedScale  = prof.SpeedScale;
            float hullFwdX = MathF.Sin(bot.LastYaw * (MathF.PI / 180f));
            float hullFwdZ = MathF.Cos(bot.LastYaw * (MathF.PI / 180f));
            float targetSpeed;
            bool wantReverse = false;
            if (retreating)
            {
                // Reverse backward while keeping frontal armor facing an enemy in front of us;
                // otherwise turn and flee forward — no double-negative reversal!
                float awayLen = MathF.Max(0.001f, MathF.Sqrt(moveGoalDx * moveGoalDx + moveGoalDz * moveGoalDz));
                float fwdDotAway = (hullFwdX * moveGoalDx + hullFwdZ * moveGoalDz) / awayLen;
                if (nearestEnemy != null && fwdDotAway < -0.25f)
                {
                    wantReverse = true;
                    targetSpeed = -bot.MaxBackSpeedMps * 0.75f;
                }
                else
                {
                    targetSpeed = bot.MaxForwardSpeedMps * roleSpeedScale;
                }
            }
            else if (engaged)
                targetSpeed = 0f;                                    // has a shot → STOP and fire
            else if (dist > arriveThreshold)
                targetSpeed = bot.MaxForwardSpeedMps * roleSpeedScale;
            else if (sepMag > 1.2f)
                targetSpeed = bot.MaxForwardSpeedMps * 0.15f;
            else
                targetSpeed = 0f;                                    // arrived → hold

            // Сбита гусеница → бот полностью обездвижен (не едет И не поворачивает корпус).
            CheckTrackRepair(room, bot);
            bool trackBroken = bot.TrackImmobilized;
            if (trackBroken) { targetSpeed = 0f; bot.CurrentSpeedMps = 0f; }

            // Casemate TDs with a spotted enemy in fire range & clear LOS must rotate their hull
            // onto target rather than driving sideways without bringing the gun into arc.
            bool casemateAimTurn = !trackBroken && !prof.Turreted && nearestEnemy != null
                && !retreating && !tooClose
                && nearestDist < (isDefenseAttacker ? engageRadius : fireStopRange * 1.35f)
                && !SegmentBlocked3D(bot.PosX, bot.PosY + 1f, bot.PosZ, nearestEnemy.PosX, nearestEnemy.PosY + 1f, nearestEnemy.PosZ, botObstacles)
                && !TerrainBlocksShot(room, bot.PosX, bot.PosZ, bot.PosY + 1f, nearestEnemy.PosX, nearestEnemy.PosZ, nearestEnemy.PosY + 1f);

            // Steer desired heading BEFORE moving using forward obstacle whiskers + side hysteresis
            // so tanks drive strictly along their hull axis instead of sliding sideways.
            float steerDirX = wantReverse ? -moveGoalDx : moveGoalDx;
            float steerDirZ = wantReverse ? -moveGoalDz : moveGoalDz;
            float desiredYaw = MathF.Atan2(steerDirX, steerDirZ) * (180f / MathF.PI);
            if (!casemateAimTurn)
            {
                bot.CasemateAligning = false;
            }
            else if (nearestEnemy != null)
            {
                float toEnemy = MathF.Atan2(nearestEnemy.PosX - bot.PosX, nearestEnemy.PosZ - bot.PosZ) * (180f / MathF.PI);
                float aimDelta = MathF.Abs(NormalizeAngle180(toEnemy - bot.LastYaw));
                if (aimDelta > CasemateMaxArcDeg - 2.5f)
                    bot.CasemateAligning = true;
                else if (aimDelta <= 2.5f)
                    bot.CasemateAligning = false;

                if (bot.CasemateAligning)
                {
                    desiredYaw = toEnemy;
                    targetSpeed = 0f;
                }
                else if (MathF.Abs(targetSpeed) < 0.1f)
                {
                    // Стоим на месте и цель уже внутри УГН — фиксируем корпус, наводим только пушку.
                    desiredYaw = bot.LastYaw;
                }
            }
            if (!trackBroken && MathF.Abs(targetSpeed) > 0.1f)
            {
                float lookDist = Math.Clamp(MathF.Abs(bot.CurrentSpeedMps) * 1.1f + 4f, 6f, 15f);
                float probeYaw = wantReverse ? desiredYaw + 180f : desiredYaw;
                desiredYaw += ChooseSteerYawOffset(room, bot, probeYaw, lookDist, botObstacles, mapCfg);
            }

            float yawErr = MathF.Abs(((desiredYaw - bot.LastYaw + 540f) % 360f) - 180f);
            bool turningInPlace = false;
            if (!trackBroken && ((bot.CasemateAligning && targetSpeed == 0f && yawErr > 1.5f) || (targetSpeed > 0.01f && yawErr > 75f)))
            {
                targetSpeed    = 0f;      // pivot turn cleanly without instant zero-slam
                turningInPlace = true;
            }
            else if (!trackBroken && targetSpeed > 0.01f && yawErr > 15f)
            {
                float cornerScale = MathF.Cos((yawErr - 15f) * (MathF.PI / 180f));
                targetSpeed *= Math.Clamp(cornerScale, 0.25f, 1f);
            }

            // Compute target hull yaw smoothly BEFORE stepping position.
            float targetYaw;
            if (trackBroken)
            {
                targetYaw = bot.LastYaw;
            }
            else if (MathF.Abs(targetSpeed) > 0.1f || turningInPlace)
            {
                targetYaw = desiredYaw;
            }
            else if (nearestEnemy != null)
            {
                float toEnemy = MathF.Atan2(nearestEnemy.PosX - bot.PosX, nearestEnemy.PosZ - bot.PosZ) * (180f / MathF.PI);
                float tdErr   = MathF.Abs(NormalizeAngle180(toEnemy - bot.LastYaw));
                // Безбашенную ПТ не дёргаем по мелким углам, если враг и так в УГН;
                // башенные ставим «ромбом» или держим нос на противника с запасом deadZone.
                if (!prof.Turreted && !bot.CasemateAligning && tdErr <= CasemateMaxArcDeg - 2.5f)
                {
                    targetYaw = bot.LastYaw;
                }
                else if (prof.Turreted && bot.HoldingPosition && MathF.Abs(bot.CurrentSpeedMps) < 0.35f && prof.Class != TankClass.Light && prof.Class != TankClass.TD)
                {
                    float side = (bot.PlayerGUID % 2 == 0) ? 1f : -1f;
                    targetYaw = toEnemy + side * 27f;
                }
                else
                {
                    targetYaw = toEnemy;
                }
            }
            else
            {
                targetYaw = bot.LastYaw;
            }

            // Smooth realistic hull traverse rate; completely locked when TrackImmobilized.
            float maxTurnDegPerSec = bot.HullTurnDegPerSec > 1f
                ? bot.HullTurnDegPerSec
                : prof.Class switch
                {
                    TankClass.Light  => 58f,
                    TankClass.Medium => 46f,
                    TankClass.TD     => 38f,
                    TankClass.Heavy  => 34f,
                    _                => 30f,
                };
            float yawStep = 0f;
            if (!trackBroken)
            {
                float yawDelta = ((targetYaw - bot.LastYaw + 540f) % 360f) - 180f;
                bool stationaryHold = MathF.Abs(targetSpeed) < 0.1f && !turningInPlace;
                float deadZone = stationaryHold ? 1.8f : 0.3f;
                if (MathF.Abs(yawDelta) >= deadZone)
                {
                    float maxStep = maxTurnDegPerSec * Math.Clamp(dt, 0.04f, 0.25f);
                    yawStep = Math.Clamp(yawDelta * 0.45f, -maxStep, maxStep);
                }
            }
            float yaw = ((bot.LastYaw + yawStep) % 360f + 360f) % 360f;
            if (yaw >= 360f) yaw -= 360f;
            bot.LastYaw = yaw;

            // Smooth speed ramp
            if (trackBroken)
            {
                bot.CurrentSpeedMps = 0f;
            }
            else
            {
                float accelDist = MathF.Abs(targetSpeed) > 0.01f ? MathF.Abs(targetSpeed) : bot.MaxForwardSpeedMps;
                float accelTime = MathF.Abs(targetSpeed) > 0.01f ? 2.4f : 0.85f;
                float accel     = accelDist / accelTime;
                if (bot.CurrentSpeedMps < targetSpeed)
                    bot.CurrentSpeedMps = Math.Min(targetSpeed, bot.CurrentSpeedMps + accel * dt);
                else if (bot.CurrentSpeedMps > targetSpeed)
                    bot.CurrentSpeedMps = Math.Max(targetSpeed, bot.CurrentSpeedMps - accel * dt);
            }

            // Move strictly along hull axis (+forward or -forward), guarded by swept obstacle, hazard, slope & tank/wreck checks
            float moveDirX = MathF.Sin(yaw * (MathF.PI / 180f));
            float moveDirZ = MathF.Cos(yaw * (MathF.PI / 180f));
            // Реалистичная потеря тяги на подъёмах (если не на мосту): чем круче склон вплоть до MaxSlopeGrade (~30°),
            // тем сильнее падает скорость, а при упоре в гору (> MaxSlopeGrade) бот останавливается и отворачивает.
            if (!trackBroken && bot.CurrentSpeedMps > 0.1f && !TrySampleBridgeDeck(room.MapId, bot.PosX, bot.PosZ, out _))
            {
                float aheadGrade = (SampleHeight(room, bot.PosX + moveDirX * 4f, bot.PosZ + moveDirZ * 4f)
                                  - SampleHeight(room, bot.PosX, bot.PosZ)) / 4f;
                if (aheadGrade > 0.10f)
                {
                    float climbMult = Math.Clamp(1f - (aheadGrade - 0.10f) * 1.45f, 0.25f, 1f);
                    float maxClimbSpeed = bot.MaxForwardSpeedMps * roleSpeedScale * climbMult;
                    if (bot.CurrentSpeedMps > maxClimbSpeed)
                        bot.CurrentSpeedMps = maxClimbSpeed;
                }
            }
            float prevX = bot.PosX, prevZ = bot.PosZ;
            if (!trackBroken)
            {
                float candX = Math.Clamp(prevX + moveDirX * bot.CurrentSpeedMps * dt, mapCfg.XMin + 5f, mapCfg.XMax - 5f);
                float candZ = Math.Clamp(prevZ + moveDirZ * bot.CurrentSpeedMps * dt, mapCfg.ZMin + 5f, mapCfg.ZMax - 5f);
                (bot.PosX, bot.PosZ) = RejectObstaclesAndSteps(room, bot, prevX, prevZ, bot.PosY, candX, candZ, botObstacles, mapCfg);
                bot.PosX = Math.Clamp(bot.PosX, mapCfg.XMin + 5f, mapCfg.XMax - 5f);
                bot.PosZ = Math.Clamp(bot.PosZ, mapCfg.ZMin + 5f, mapCfg.ZMax - 5f);
                SeparateFromTanks(room, bot, botObstacles, mapCfg);
            }

            // ── stuck detection ────────────────────────────────────────────────
            var nowStuck = DateTime.UtcNow;
            if (!trackBroken && MathF.Abs(targetSpeed) > 0.5f) bot.WantedToMove = true;
            bool engagingNow = nearestEnemy != null && nearestDist < engageRadius;
            if (!trackBroken && nowStuck >= bot.StuckCheckTime)
            {
                float progress = Distance2D(bot.StuckRefX, bot.StuckRefZ, bot.PosX, bot.PosZ);
                if (bot.WantedToMove && progress < 4f && nowStuck >= bot.EscapeUntil && !engagingNow)
                {
                    var (ex, ez) = FindOpenDirection(room, bot, bot.PosX, bot.PosZ, botObstacles, mapCfg);
                    bot.EscapeDirX = ex; bot.EscapeDirZ = ez;
                    bot.EscapeUntil = nowStuck.AddSeconds(1.5 + _random.NextDouble());
                    bot.WaypointX = 0f; bot.WaypointZ = 0f;
                    bot.NavPath = null;
                }
                bot.StuckRefX = bot.PosX; bot.StuckRefZ = bot.PosZ;
                bot.StuckCheckTime = nowStuck.AddSeconds(1.2);
                bot.WantedToMove = false;
            }

            if (MathF.Abs(bot.PosX - prevX) + MathF.Abs(bot.PosZ - prevZ) < 0.03f)
            {
                bot.PosX = prevX;
                bot.PosZ = prevZ;
                if (MathF.Abs(targetSpeed) > 0.1f)
                    bot.CurrentSpeedMps = 0f;
            }
            if (turningInPlace && MathF.Abs(bot.CurrentSpeedMps) < 0.5f) { bot.CurrentSpeedMps = 0f; }
            float actualDx = bot.PosX - prevX;
            float actualDz = bot.PosZ - prevZ;
            float velX = dt > 0f ? actualDx / dt : 0f;
            float velZ = dt > 0f ? actualDz / dt : 0f;
            bot.SpeedX = velX; bot.SpeedZ = velZ;

            float prevPosY = bot.PosY;
            float groundRef = room.KnownGroundY ?? GetSpawnY(bot.Group, room.MapId);
            float sampledY = SampleHeight(room, bot.PosX, bot.PosZ);
            bool hasHeightmap = TerrainHeightmaps.ByMap.ContainsKey(room.MapId);
            var ((rawPitch, rawRoll), supportY) = TerrainTiltWithSupport(room, bot.PosX, bot.PosZ, yaw);
            if (hasHeightmap && !float.IsNaN(supportY) && !TrySampleBridgeDeck(room.MapId, bot.PosX, bot.PosZ, out _))
                sampledY = MathF.Max(sampledY, supportY) + 0.08f;
            float targetY = hasHeightmap ? sampledY : MathF.Max(sampledY, groundRef - 6f);
            float dY = Math.Clamp(targetY - prevPosY, -1.5f, 1.5f);
            bot.PosY = bot.TiltInit ? prevPosY + dY * 0.65f : targetY;
            float velY = dt > 0f ? (bot.PosY - prevPosY) / dt : 0f;

            // Low-pass filter terrain tilt (~30%/tick) so tracks follow slopes smoothly
            // without snapping on minor height ripples.
            if (!bot.TiltInit) { bot.RenderPitch = rawPitch; bot.RenderRoll = rawRoll; bot.TiltInit = true; }
            else
            {
                const float tiltLerp = 0.30f;
                bot.RenderPitch += (rawPitch - bot.RenderPitch) * tiltLerp;
                bot.RenderRoll  += (rawRoll  - bot.RenderRoll ) * tiltLerp;
            }
            float slopePitch = bot.RenderPitch, slopeRoll = bot.RenderRoll;

            // ── Defense mode: pass-through check ──────────────────────────────
            // Measure along whichever axis the two team spawns are separated on.
            // On maps like Кан the teams split on Z (same X), so an X-based line is
            // always "already crossed" and every attacker counts as a break-through
            // instantly — which ended Defense on Кан at the start. Detect the axis.
            float dxSpawn = MathF.Abs(mapCfg.Team0.XCenter - mapCfg.Team1.XCenter);
            float dzSpawn = MathF.Abs(mapCfg.Team0.ZCenter - mapCfg.Team1.ZCenter);
            bool  splitOnZ = dzSpawn > dxSpawn;
            float defenderBase = playerGroup == 0
                ? (splitOnZ ? mapCfg.Team0.ZCenter : mapCfg.Team0.XCenter)
                : (splitOnZ ? mapCfg.Team1.ZCenter : mapCfg.Team1.XCenter);
            float attackerStart = playerGroup == 0
                ? (splitOnZ ? mapCfg.Team1.ZCenter : mapCfg.Team1.XCenter)
                : (splitOnZ ? mapCfg.Team0.ZCenter : mapCfg.Team0.XCenter);
            float botAxisPos = splitOnZ ? bot.PosZ : bot.PosX;
            float passZone   = mapCfg.Team0.XHalfSpread * 2f;
            // Attacker begins on the far side; it broke through once it passes the base.
            bool brokeThrough = attackerStart > defenderBase
                ? botAxisPos < defenderBase + passZone
                : botAxisPos > defenderBase - passZone;
            if (isDefenseAttacker && brokeThrough)
            {
                room.EnemiesPassedThrough++;
                room.PveDefenseLives = Math.Max(0, BattleRoom.PveInitLives - room.EnemiesPassedThrough);
                BroadcastToRoom(room, new PK_RC_PVE_Life { CurLife = room.PveDefenseLives });
                Console.WriteLine($"[RS] Defense: attacker passed through! ({room.EnemiesPassedThrough}/{BattleRoom.PveInitLives})");
                BroadcastToRoom(room, new PK_RC_Chat
                {
                    ChatType   = 1,
                    PlayerGUID = SystemChatGuid(room),
                    Message    = $"Прорыв! Жизней осталось: {room.PveDefenseLives}/{BattleRoom.PveInitLives}",
                });
                // Reset attacker to its spawn (using free obstacle-clear spot)
                var _atkReset = mapCfg.AttackerSpawns;
                (bot.PosX, bot.PosZ) = (_atkReset != null && _atkReset.Length > 0)
                    ? PickSpawnSpotNear(room, _atkReset[0].X, _atkReset[0].Z)
                    : PickSpawnSpot(room, attackerGroup);
                bot.PosY            = SampleHeight(room, bot.PosX, bot.PosZ);
                bot.LastYaw         = GetSpawnYaw(room.MapId, attackerGroup);
                bot.CurrentSpeedMps = 0f;
                bot.TiltInit        = false;
                bot.NavPath         = null;
                bot.SteerSideBias   = 0;
                bot.CurHp           = bot.MaxHp;

                if (room.PveDefenseLives <= 0 && !room.BattleEnded)
                    EndBattle(room, explicitWinnerGroup: attackerGroup);
            }

            // ── turret / casemate traverse + gun alignment ────────────────────
            float desiredLocalTurret = 0f;
            float gunAngle           = 0f;
            if (nearestEnemy != null)
            {
                float tdx = nearestEnemy.PosX - bot.PosX;
                float tdz = nearestEnemy.PosZ - bot.PosZ;
                float enemyYaw = MathF.Atan2(tdx, tdz) * (180f / MathF.PI);
                desiredLocalTurret = NormalizeAngle180(enemyYaw - yaw);
                if (!prof.Turreted)
                    desiredLocalTurret = Math.Clamp(desiredLocalTurret, -CasemateMaxArcDeg, CasemateMaxArcDeg);

                float horiz = MathF.Sqrt(tdx * tdx + tdz * tdz);
                float dy    = nearestEnemy.PosY - bot.PosY;
                gunAngle = -MathF.Atan2(dy, MathF.Max(horiz, 0.1f)) * (180f / MathF.PI);
                gunAngle = Math.Clamp(gunAngle, -18f, 8f);
            }
            float maxTurretStep = MathF.Max(15f, bot.TurretTurnDegPerSec) * Math.Clamp(dt, 0.04f, 0.25f);
            float turretDelta = prof.Turreted
                ? NormalizeAngle180(desiredLocalTurret - bot.CurrentTurretAngle)
                : (desiredLocalTurret - bot.CurrentTurretAngle);
            bot.CurrentTurretAngle = NormalizeAngle180(
                bot.CurrentTurretAngle + Math.Clamp(turretDelta, -maxTurretStep, maxTurretStep));
            if (!prof.Turreted)
                bot.CurrentTurretAngle = Math.Clamp(bot.CurrentTurretAngle, -CasemateMaxArcDeg, CasemateMaxArcDeg);
            float turretAngle = bot.CurrentTurretAngle;

            // ── fire on cooldown ───────────────────────────────────────────────
            // A bot fires only at an enemy its team actually SEES AND that its gun barrel
            // is actually aimed at (no shooting sideways/backwards from casemate TDs or
            // mid-traverse turrets).
            float shootRange = isDefenseAttacker ? engageRadius : fireStopRange * 1.35f;
            var realTarget = nearestEnemy;
            float aimSec  = bot.AimTargetGuid != 0 ? (float)(DateTime.UtcNow - bot.AimStartTime).TotalSeconds : 0f;
            bool  reacted = aimSec >= BotSkill.ReactionSec(bot.Skill);
            bool  gunAimed = false;
            if (realTarget != null)
            {
                float tgtYaw = MathF.Atan2(realTarget.PosX - bot.PosX, realTarget.PosZ - bot.PosZ) * (180f / MathF.PI);
                float gunWorldYaw = yaw + turretAngle;
                float aimErr = MathF.Abs(NormalizeAngle180(tgtYaw - gunWorldYaw));
                float maxErr = prof.Turreted ? 8f : 5f;
                bool insideCasemateArc = prof.Turreted || MathF.Abs(NormalizeAngle180(tgtYaw - yaw)) <= CasemateMaxArcDeg;
                gunAimed = aimErr <= maxErr && insideCasemateArc;
            }
            bool canShoot = realTarget != null && reacted && gunAimed && Distance2D(bot, realTarget) < shootRange;
            // v12: повреждённая боеукладка (Injure) заряжает в 1.5 раза дольше.
            float botReload = bot.ReloadBuffUntil > DateTime.UtcNow ? bot.BotReloadSeconds * 0.5f : bot.BotReloadSeconds;
            if (bot.AmmoRackState == 1) botReload *= 1.5f;
            if (canShoot && (DateTime.UtcNow - bot.LastFireTime).TotalSeconds > botReload)
            {
                // Block the shot if any obstacle box or terrain ridge sits on the line of fire
                // to either the target's current position or lead point (checked in FireBot).
                if (realTarget != null
                    && !SegmentBlocked3D(bot.PosX, bot.PosY + 1f, bot.PosZ, realTarget.PosX, realTarget.PosY + 1f, realTarget.PosZ, botObstacles)
                    && !TerrainBlocksShot(room,
                            bot.PosX, bot.PosZ, bot.PosY + 1f,
                            realTarget.PosX, realTarget.PosZ, realTarget.PosY + 1f))
                {
                    if (FireBot(room, bot, realTarget, dt))
                        bot.LastFireTime = DateTime.UtcNow;
                }
            }

            // ── broadcast bot position (PK_RC_TankMoveDictate) ────────────────
            bot.MoveNumber++;

            // Почти стоит → шлём НУЛЕВУЮ скорость и idle, иначе клиент экстраполирует
            // микро-колебания vel (от шаркания/шума высоты) и танк «дрожит» на месте.
            bool botNearlyStopped = MathF.Abs(bot.CurrentSpeedMps) < 0.35f;
            int linSign = bot.CurrentSpeedMps > 0.35f ? 1 : bot.CurrentSpeedMps < -0.35f ? -1 : 0;
            int rotSign = yawStep > 0.35f ? 1 : yawStep < -0.35f ? -1 : 0;
            byte dictate = (linSign, rotSign) switch
            {
                ( 1,  0) => 3,  // W
                ( 1, -1) => 7,  // WA
                ( 1,  1) => 8,  // WD
                (-1,  0) => 4,  // S
                (-1, -1) => 9,  // SA
                (-1,  1) => 10, // SD
                ( 0, -1) => 5,  // A
                ( 0,  1) => 6,  // D
                _        => 0,
            };
            float bVelX = botNearlyStopped ? 0f : velX;
            float bVelY = botNearlyStopped ? 0f : velY;
            float bVelZ = botNearlyStopped ? 0f : velZ;

            BroadcastBotMove(room, bot, new PK_RC_TankMoveDictate
            {
                PlayerGuid  = bot.PlayerGUID,
                DictateInfo = dictate,
                SpeedX = bVelX, SpeedY = bVelY, SpeedZ = bVelZ,
                MoveNumber = bot.MoveNumber,
                PosX = bot.PosX, PosY = bot.PosY, PosZ = bot.PosZ,
                RotX = slopePitch, RotY = yaw, RotZ = slopeRoll,
                GunAngle = gunAngle, TurretAngle = turretAngle,
                CruiseGrade = 0,
                SlideSpeedX = 0, SlideSpeedY = 0, SlideSpeedZ = 0,
            });

            // v57 (задача 1): бот КРУШИТ разрушаемые объекты, мимо которых проезжает (грузовик/забор/
            // коробку). Так снос ботами виден надёжно — не только при удачном простреле по линии огня.
            TryDestroyObjectsNearBot(room, bot);
        }

        // ── Defense resolution ──────────────────────────────────────────────────
        if (room.GameMode == BattleGameMode.Defense && !room.BattleEnded)
        {
            int aliveAttackers = room.Players.Values.Count(p => p.IsBot && p.Group == attackerGroup && !p.IsDead);
            if (aliveAttackers == 0 && room.CurrentWaveEnemies <= 0)
            {
                // All wave enemies cleared — PVE wave loop handles the next wave
            }
        }
    }

    private static float Distance2D(RoomPlayer a, RoomPlayer b)
    {
        float dx = a.PosX - b.PosX, dz = a.PosZ - b.PosZ;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
    private static float Distance2D(float x1, float z1, float x2, float z2)
    {
        float dx = x1 - x2, dz = z1 - z2;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private bool FireBot(BattleRoom room, RoomPlayer bot, RoomPlayer target, float dt)
    {
        // v48.9: бот стреляет не только основным снарядом, но иногда и ВТОРЫМ (Ammo2 —
        // подкалиберный/фугас и т.п.), если он есть у орудия. ~30% выстрелов — вторым.
        // shellId идёт и в PK_RC_TankFire, и в ResolveHit, поэтому урон/пробитие считаются
        // по реальным характеристикам выбранного снаряда.
        var botGun = _gameTables.GetGun(bot.MainGunID);
        int   shellId = bot.CurrentShellId > 0 ? bot.CurrentShellId : (botGun?.Ammo1Id ?? 1);
        if (botGun != null && botGun.Ammo2Id > 0 && _random.Next(100) < 30)
            shellId = botGun.Ammo2Id;
        float skill   = bot.Skill;
        float dist    = Distance2D(bot, target);
        float flightSec = Math.Clamp(dist / 700f, 0.35f, 1.5f);
        // Phase 5 — LEAD: aim where the target WILL be after the shell's flight, scaled
        // by skill (an ace leads fully, a recruit barely). SpeedX/Z = target velocity.
        float lead = BotSkill.LeadQuality(skill);
        float aimX = target.PosX + target.SpeedX * flightSec * lead;
        float aimZ = target.PosZ + target.SpeedZ * flightSec * lead;

        float fireStartX = bot.PosX, fireStartY = bot.PosY + 1f, fireStartZ = bot.PosZ;
        float tgtBearing = MathF.Atan2(target.PosX - fireStartX, target.PosZ - fireStartZ) * (180f / MathF.PI);
        float leadYaw    = MathF.Atan2(aimX - fireStartX, aimZ - fireStartZ) * (180f / MathF.PI);
        float gunWorldYaw = bot.LastYaw + bot.CurrentTurretAngle;
        if (MathF.Abs(NormalizeAngle180(tgtBearing - gunWorldYaw)) > (bot.Profile.Turreted ? 10f : 6f))
            return false;
        if (!bot.Profile.Turreted)
        {
            if (MathF.Abs(NormalizeAngle180(tgtBearing - bot.LastYaw)) > CasemateMaxArcDeg)
                return false;
            if (MathF.Abs(NormalizeAngle180(leadYaw - bot.LastYaw)) > CasemateMaxArcDeg)
            {
                aimX = target.PosX;
                aimZ = target.PosZ;
            }
        }

        var botObstacles = (room.FileObstacles != null && room.FileObstacles.Length > 0)
            ? room.FileObstacles : GetMapConfig(room.MapId).Obstacles;
        float aimY = target.PosY + 1f;
        if (SegmentBlocked3D(fireStartX, fireStartY, fireStartZ, target.PosX, aimY, target.PosZ, botObstacles)
            || SegmentBlocked3D(fireStartX, fireStartY, fireStartZ, aimX, aimY, aimZ, botObstacles)
            || TerrainBlocksShot(room, fireStartX, fireStartZ, fireStartY, target.PosX, target.PosZ, aimY)
            || TerrainBlocksShot(room, fireStartX, fireStartZ, fireStartY, aimX, aimZ, aimY))
            return false;

        // v26: индекс снаряда должен быть УНИКАЛЬНЫМ на каждый выстрел. Клиент кладёт
        // снаряд в AmmoDict по ключу (ShellId, ShellIndex) и по нему же ищет его при
        // попадании: с вечной единицей второй и последующие выстрелы бота не находятся,
        // и в режиме обороны не запускается штатный путь построения следа
        // (PVEAmmoTools -> SyncAITankShellHit -> рейкаст -> декаль).
        short botShellIndex = (short)(bot.ShotSeq = (short)((bot.ShotSeq + 1) % 30000));
        BroadcastToRoom(room, new PK_RC_TankFire
        {
            ShellId    = (short)shellId,
            ShellIndex = botShellIndex,
            PlayerGUID = bot.PlayerGUID,
            StartX     = fireStartX, StartY = fireStartY, StartZ = fireStartZ,
            IdealX     = aimX,       IdealY = target.PosY + 1f, IdealZ = aimZ,
        });

        // v54 (задача 1): снаряд бота сносит разрушаемый объект на своём пути (грузовик/коробка/забор).
        TryDestroyObjectsAlongShot(room, fireStartX, fireStartZ, aimX, aimZ);

        var atk = bot; var tgt = target;
        // The client only SPAWNS a remote shell 0.2s after the fire packet
        // (RemotePlayerFireEventHandler.RemoteFireDelayTime). If our hit packet arrives
        // before that, the shell isn't in the client's AmmoDict yet, the hit is dropped,
        // and the orphaned shell flies on and vanishes at max range ("исчезают в воздухе").
        // So the hit must land AFTER the spawn delay — clamp the low end to 0.35s.
        float flightMs = flightSec * 1000f;
        // Phase 5 — ACCURACY: base hit chance by skill, +up to 15% for aim convergence
        // (the longer it's laid on this target), −up to 30% against a fast CROSSING
        // target (harsher the lower the skill).
        float aimSec   = bot.AimTargetGuid == tgt.PlayerGUID ? (float)(DateTime.UtcNow - bot.AimStartTime).TotalSeconds : 0f;
        // v49: бот «сводится» за РЕАЛЬНОЕ время орудия (aim_max из Table_Gun), а не за хардкод 2.5с —
        // орудие с долгим сведением дольше набирает точность.
        float aimTime  = _gameTables.GetGunAimMax(bot.MainGunID);
        if (aimTime <= 0.1f) aimTime = 2.5f;
        float converge = Math.Clamp(aimSec / aimTime, 0f, 1f) * 0.15f;
        float tgtSpeed = MathF.Sqrt(tgt.SpeedX * tgt.SpeedX + tgt.SpeedZ * tgt.SpeedZ);
        float crossPen = Math.Clamp(tgtSpeed / 15f, 0f, 1f) * (1f - skill) * 0.30f;
        // v49: РЕАЛЬНЫЙ разброс орудия (accuracy из Table_Gun) влияет на шанс: точное орудие
        // (≈0.25) даёт бонус, неточное (≈0.5) — штраф, до ~±0.08 к шансу попадания.
        float accMax   = _gameTables.GetGunAccuracyMax(bot.MainGunID);
        float accPen   = accMax > 0.05f ? (accMax - 0.35f) * 0.5f : 0f;
        float hitChance = Math.Clamp(BotSkill.HitChance(skill) + converge - crossPen - accPen, 0.15f, 0.95f);
        bool willHit = _random.NextDouble() < hitChance;

        float aimRoll  = (float)(_random.NextDouble() * (1.0 - skill * 0.5) + skill * 0.25); // skill1→[.25,.75] skill0→[0,1)
        float aimHitY;
        bool  aimTurret = false;
        if      (aimRoll < 0.18f) aimHitY = tgt.PosY + 0.35f;                     // ходовая
        else if (aimRoll < 0.55f) aimHitY = tgt.PosY + 0.75f;                     // нижний корпус
        else if (aimRoll < 0.80f) aimHitY = tgt.PosY + 1.15f;                     // верхний корпус
        else { aimHitY = tgt.PosY + 1.90f; aimTurret = true; }                    // башня

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep((int)flightMs);
            if (room.BattleEnded || atk.IsDead || tgt.IsDead) return;
            // Re-check LOS at arrival: if target ducked behind cover/terrain mid-flight,
            // do not damage through cover — detonate on the intervening obstacle instead.
            bool blockedAtImpact = SegmentBlocked3D(fireStartX, fireStartY, fireStartZ, tgt.PosX, tgt.PosY + 1f, tgt.PosZ, botObstacles)
                || TerrainBlocksShot(room, fireStartX, fireStartZ, fireStartY, tgt.PosX, tgt.PosZ, tgt.PosY + 1f);
            if (willHit && !blockedAtImpact)
            {
                ResolveHit(room, atk, tgt,
                    shellId, botShellIndex,
                    tgt.PosX, aimHitY, tgt.PosZ,
                    0, 1, 0, 0, 0,
                    attackerSkill: skill,      // skilled bots aim for weak spots
                    hitTurretPart: aimTurret); // v46: попадание в башню, когда бот целит выше
            }
            else
            {
                float hitX = tgt.PosX, hitY = tgt.PosY + 0.5f, hitZ = tgt.PosZ;
                if (blockedAtImpact)
                    (hitX, hitY, hitZ) = FindFirstImpactPoint(room, fireStartX, fireStartY, fireStartZ, tgt.PosX, tgt.PosY + 0.5f, tgt.PosZ, botObstacles);
                // Miss / blocked: land the shell visibly at cover/target with matching ShellIndex.
                BroadcastToRoom(room, new PK_RC_ShellHit
                {
                    ShellID    = (short)shellId,
                    ShellIndex = botShellIndex,
                    AttackerGUID = atk.PlayerGUID,
                    TargetGUID   = 0,
                    HitPosX = hitX, HitPosY = hitY, HitPosZ = hitZ,
                    LocalPosX = 0, LocalPosY = 0, LocalPosZ = 0,
                    NormalX = 0, NormalY = 1, NormalZ = 0,
                    HitLayer = 0, HitModel = 0,
                    ShellResult = 0, // bounce/miss visual, no damage
                });
            }
        });
        return true;
    }

    // ─────────────────────────── movement ──────────────────────────────────────
    private static int GetLinearDirectionSign(byte dictateInfo) => dictateInfo switch
    {
        1 or 3 or 7 or 8 or 11 or 12 => 1,
        2 or 4 or 9 or 10 or 13 or 14 => -1,
        _ => 0
    };

    private void HandleTankMoveDictate(GameSession session, PK_CR_SelfTankMoveDictate move)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player)) return;

        player.PosX = move.PosX; player.PosY = move.PosY; player.PosZ = move.PosZ;
        player.RotX = move.RotX; player.RotY = move.RotY; player.RotZ = move.RotZ;

        // Capture terrain height from the player and store it in the height grid.
        // The player's tank follows the real terrain mesh client-side, so every
        // reported position is a ground-truth (X, Z, Y) sample. Bots later snap to
        // the nearest sampled height — this makes them follow real terrain instead
        // of floating at a single flat Y.
        if (player.Room != null)
        {
            var room2 = player.Room;
            int gx = (int)MathF.Floor(move.PosX / BattleRoom.HeightCellSize);
            int gz = (int)MathF.Floor(move.PosZ / BattleRoom.HeightCellSize);
            // Record the player's real reported height for this cell. This used to be
            // gated to "only accept if not more than 8 units above the current value",
            // which was meant to reject the old high spawn-fall spike (Berlin used to
            // spawn at Y=200 before settling to ~50). Spawn Y values are now calibrated
            // to real ground, so that spike doesn't happen anymore — and the gate had a
            // bad side effect: it permanently blocked the grid from ever learning that
            // real terrain is HIGHER than whatever's currently stored (e.g. a bridge, a
            // rise, the far side of a valley), which is exactly the kind of elevation
            // change that made bots look like they were sinking into/through geometry.
            // Trust the client's real terrain-following position outright.
            room2.HeightGrid[(gx, gz)] = move.PosY;
            if (TerrainHeightmaps.ByMap.TryGetValue(room2.MapId, out var hmLive))
            {
                float hmY = hmLive.SurfaceBilinear(move.PosX, move.PosZ);
                if (move.PosY > hmY + 2.0f)
                {
                    int ex = (int)MathF.Floor(move.PosX / BattleRoom.ElevatedCellSize);
                    int ez = (int)MathF.Floor(move.PosZ / BattleRoom.ElevatedCellSize);
                    room2.LiveElevatedGrid[(ex, ez)] = move.PosY;
                }
            }

            // Track the X/Z bounding box of where the player has actually driven
            // (used only for the calibration log below — no longer used to clamp bots).
            room2.MinSeenX = Math.Min(room2.MinSeenX, move.PosX);
            room2.MaxSeenX = Math.Max(room2.MaxSeenX, move.PosX);
            room2.MinSeenZ = Math.Min(room2.MinSeenZ, move.PosZ);
            room2.MaxSeenZ = Math.Max(room2.MaxSeenZ, move.PosZ);
            // Last-known player Y — used as SampleHeight's final fallback when no
            // grid cell is nearby at all. Always reflects the latest real position.
            room2.LastPlayerY = move.PosY;

            // Generic per-room fallback (not team-specific), same as SampleHeight's.
            float spawnY = GetSpawnY(0, room2.MapId);
            if (room2.KnownGroundY == null) room2.KnownGroundY = spawnY;

            // Throttled coordinate logging for map calibration (every ~3s).
            if ((DateTime.UtcNow - room2.LastCoordLog).TotalSeconds > 3.0)
            {
                room2.LastCoordLog = DateTime.UtcNow;
                Console.WriteLine($"[RS] PlayerPos map={room2.MapId} " +
                    $"X={move.PosX:F0} Y={move.PosY:F0} Z={move.PosZ:F0} " +
                    $"(seen X:{room2.MinSeenX:F0}..{room2.MaxSeenX:F0} Z:{room2.MinSeenZ:F0}..{room2.MaxSeenZ:F0})");
            }

            // Throttled terrain persistence (every ~30s) so learned ground survives
            // even if a battle ends abnormally without hitting EndBattle.
            if ((DateTime.UtcNow - room2.LastGridSave).TotalSeconds > 30.0)
            {
                room2.LastGridSave = DateTime.UtcNow;
                SaveHeightGrid(room2);
            }
        }

        int   dirSign     = GetLinearDirectionSign(move.DictateInfo);
        float targetSpeed = dirSign > 0 ? player.MaxForwardSpeedMps
                          : dirSign < 0 ? -player.MaxBackSpeedMps : 0f;

        var   now  = DateTime.UtcNow;
        float rawDt = player.LastDictateTime == default ? 0.1f
                      : (float)(now - player.LastDictateTime).TotalSeconds;
        float dt   = Math.Clamp(rawDt, 0f, 0.5f);
        player.LastDictateTime = now;

        float accelDist = MathF.Abs(targetSpeed) > 0.01f ? MathF.Abs(targetSpeed) : player.MaxForwardSpeedMps;
        float accelTime = MathF.Abs(targetSpeed) > 0.01f ? 3.0f : 1.0f;
        float accel     = accelDist / accelTime;
        if (player.CurrentSpeedMps < targetSpeed)
            player.CurrentSpeedMps = Math.Min(targetSpeed, player.CurrentSpeedMps + accel * dt);
        else if (player.CurrentSpeedMps > targetSpeed)
            player.CurrentSpeedMps = Math.Max(targetSpeed, player.CurrentSpeedMps - accel * dt);

        float yawRad = move.RotY * (MathF.PI / 180f);
        float velX   = MathF.Sin(yawRad) * player.CurrentSpeedMps;
        float velZ   = MathF.Cos(yawRad) * player.CurrentSpeedMps;
        player.SpeedX = velX;
        player.SpeedZ = velZ;
        player.TowerRotY = move.TurretAngle;

        var fullMove = new PK_RC_TankMoveDictate
        {
            PlayerGuid  = session.PlayerGUID,
            DictateInfo = move.DictateInfo,
            SpeedX = velX, SpeedY = 0, SpeedZ = velZ,
            PosX = move.PosX, PosY = move.PosY, PosZ = move.PosZ,
            RotX = move.RotX, RotY = move.RotY, RotZ = move.RotZ,
            GunAngle    = move.GunAngle,
            TurretAngle = move.TurretAngle,
            MoveNumber  = move.MoveNumber,
        };
        // Echo back to sender (so client's own _dictateInfo != null), and relay to other
        // humans respecting fog-of-war (allies always, enemies only when spotted).
        try { session.Send(fullMove); } catch { }
        if (player.Room != null)
            BroadcastBotMove(player.Room, player, fullMove, excludeGuid: player.PlayerGUID);
    }

    private void HandleTankMove(GameSession session, PK_CR_SelfTankMove move)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player)) return;

        player.PosX = move.PosX; player.PosY = move.PosY; player.PosZ = move.PosZ;
        player.RotX = move.RotX; player.RotY = move.RotY; player.RotZ = move.RotZ;
        player.SpeedX = move.SpeedX; player.SpeedY = move.SpeedY; player.SpeedZ = move.SpeedZ;
        player.RotateSpeed = move.RotateSpeed;

        player.MoveNumber++;
        if (player.Room != null)
            BroadcastToRoom(player.Room, new PK_RC_TankMoveDictate
            {
                PlayerGuid  = session.PlayerGUID,
                DictateInfo = move.KeyCode,
                SpeedX = move.SpeedX, SpeedY = move.SpeedY, SpeedZ = move.SpeedZ,
                MoveNumber  = player.MoveNumber,
                PosX = move.PosX, PosY = move.PosY, PosZ = move.PosZ,
                RotX = move.RotX, RotY = move.RotY, RotZ = move.RotZ,
                GunAngle = 0, TurretAngle = 0, CruiseGrade = 0,
                SlideSpeedX = 0, SlideSpeedY = 0, SlideSpeedZ = 0,
            });
    }

    // ─────────────────────────── firing ────────────────────────────────────────
    private void HandleTankFire(GameSession session, PK_CR_TankFire fire)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player)) return;
        player.ShootCount++;
        player.LastFireTime = DateTime.UtcNow;
        if (player.Room != null)
            BroadcastToRoomOthers(player.Room, session, new PK_RC_TankFire
            {
                ShellId    = fire.ShellId,
                ShellIndex = fire.ShellIndex,
                PlayerGUID = session.PlayerGUID,
                StartX     = fire.StartX, StartY = fire.StartY, StartZ = fire.StartZ,
                IdealX     = fire.IdealX, IdealY = fire.IdealY, IdealZ = fire.IdealZ,
            });
    }

    private void HandleRequestFire(GameSession session, PK_CR_RequestFire _)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player)) return;
        int shellId = player.CurrentShellId;
        if (shellId == 0)
        {
            var gun = _gameTables.GetGun(player.MainGunID);
            shellId = gun?.Ammo1Id ?? 1;
            player.CurrentShellId = shellId;
        }
        // Report the count of the shell that is actually loaded. Previously a single shared
        // player.ShellCount was sent, so switching to shell 2/3 showed the ББ (slot-0)
        // count. Now each shell tracks its own count.
        if (!player.ShellAmmoById.TryGetValue(shellId, out int count) || count <= 0)
        {
            // Drum/magazine auto-reload for this shell
            var reloadGun = _gameTables.GetGun(player.MainGunID);
            count = Math.Max(1, reloadGun?.AmmoLoad ?? 20);
        }
        ClearResolvedShot(player, shellId, (short)count);
        var firePkt = new PK_RC_RequestFire { ShellId = (short)shellId, ShellIndex = (short)count };
        session.Send(firePkt);
        count--;
        player.ShellAmmoById[shellId] = count;
        player.ShellCount = count; // keep legacy field roughly in sync
    }

    private void HandleAddClipFire(GameSession session, PK_CR_AddClipFire clip)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out _)) return;
        session.Send(new PK_RC_AddClipFire { Word = clip.Word });
    }

    // The client sends the SHELL ID it selected, NOT a slot index
    // (TankCannonballOperation.SetStateValue: SendTankShellChange(_shells[sel].ID)).
    // Earlier this was read as a 0/1/2 slot, so an id like 812 fell out of range and the
    // tank kept firing the main shell. Use the id directly, then reply Flag=1 so the
    // client's OnShellChange matches the slot with that id and unblocks firing.
    private void HandleTankShellChange(GameSession session, PK_CR_TankShellChange msg)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player)) return;

        int sid = msg.Index > 0
            ? msg.Index
            : (player.CurrentShellId > 0 ? player.CurrentShellId
               : (_gameTables.GetGun(player.MainGunID)?.Ammo1Id ?? 1));

        bool known = player.ShellSlotIds != null && Array.IndexOf(player.ShellSlotIds, sid) >= 0;
        player.CurrentShellId = sid;
        session.Send(new PK_RC_TankShellChange { Flag = 1, ShellId = sid });
        Console.WriteLine($"[RS] ShellChange: {session.PlayerGUID} -> shell {sid}{(known ? "" : " (not in cached slots)")}");
    }

    private static int[] ParseProvisions(string? rawValue)
    {
        var result = new int[3];
        if (string.IsNullOrWhiteSpace(rawValue)) return result;
        var parts = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(result.Length, parts.Length); i++)
        {
            int.TryParse(parts[i], out result[i]);
        }
        return result;
    }

    // Тот же формат, что и Tanks.SignatureIds/EmblemIds в БД ("id;id;..."), см.
    // HallServer.FillIntArray — используем свой маленький аналог, чтобы не тащить
    // сюда зависимость от HallServer.
    private static int[] ParseIntArray(string? rawValue, int size)
    {
        var result = new int[size];
        if (string.IsNullOrWhiteSpace(rawValue)) return result;
        var parts = rawValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(result.Length, parts.Length); i++)
        {
            int.TryParse(parts[i], out result[i]);
        }
        return result;
    }

    // ── Расходники (Provisions): аптечки, ремкомплекты, огнетушители, дым. шашки ──
    // Поддерживаются как одноразовые (cd_time == 0), так и многоразовые с кулдауном
    // (cd_time > 0, синтезируем PK_RC_Refresh_Consume_CD). Все починенные модули и
    // потушенный пожар рассылаются через SendWidget(..., state=0), чтобы клиент снял
    // жёлтые/красные иконки и разблокировал движение/прицел.
    private void HandleUseConsumable(GameSession session, PK_CR_UseConsumable msg)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player) || player.Room == null) return;
        var room = player.Room;

        int slot = Array.IndexOf(player.Provisions, msg.ProvisionId);
        if (slot < 0 || player.IsDead)
        {
            session.Send(new PK_RC_UseConsumable { SucceedFlag = 0, ProvisionId = msg.ProvisionId, ModuleType = msg.ModuleType });
            return;
        }

        var con = _gameTables.GetConsumable(msg.ProvisionId);
        var now = DateTime.UtcNow;
        bool reusable = con != null && con.CdTime > 0;
        if (reusable)
        {
            if (now < player.ConsumableCdUntil[slot])
            {
                session.Send(new PK_RC_UseConsumable { SucceedFlag = 0, ProvisionId = msg.ProvisionId, ModuleType = msg.ModuleType });
                return;
            }
            player.ConsumableCdUntil[slot] = now.AddSeconds(con!.CdTime);
        }
        else if (player.ProvisionUsed[slot])
        {
            session.Send(new PK_RC_UseConsumable { SucceedFlag = 0, ProvisionId = msg.ProvisionId, ModuleType = msg.ModuleType });
            return;
        }

        // Любое успешное применение (включая многоразовый) помечает слот как использованный в бою для списания после боя.
        player.ProvisionUsed[slot] = true;

        int actionId = con?.ActionId ?? 0;
        int additionalId = msg.ProvisionId;

        if (con?.Type == 4 || actionId == 5) // Smoke bomb (type 4: id 6,7,10,14)
        {
            int smokeIdx = Interlocked.Increment(ref room.NextSmokeIndex);
            room.PendingSmokes[smokeIdx] = (player.PlayerGUID, con?.AdditionalId ?? 1);
            additionalId = smokeIdx;
        }
        else if (con?.Type == 6 || actionId == 6) // Artillery support (id 9)
        {
            ApplyAreaStrike(room, player, msg.PositionX, msg.PositionZ, 15f, 100);
        }
        else if (con?.Type == 7 || actionId == 7) // Full repair kit (id 11: +10% MaxHp & repair modules)
        {
            int heal = Math.Max(1, (int)(player.MaxHp * 0.10f));
            player.CurHp = Math.Min(player.MaxHp, player.CurHp + heal);
            BroadcastToRoom(room, new PK_RC_SynHP { AttackerGUID = 0, PlayerGUID = player.PlayerGUID, CurHp = player.CurHp, Reason = 0 });
            RepairAllPlayerModules(room, player, extinguishFire: true);
        }
        else if (actionId == 3) // Extinguisher (id 2, 4)
        {
            ExtinguishPlayerFire(room, player);
        }
        else if (actionId == 4 || actionId == 2 || con?.Type == 5 || msg.ModuleType == 0)
        {
            // Elite Repair Kit (id 5), Common/Improved reusable repair kits (id 8, 13), or unspecified ModuleType
            RepairAllPlayerModules(room, player, extinguishFire: true);
        }
        else
        {
            // Single module repair (ActionId == 1: small/gold repair kit)
            switch ((EComponent)msg.ModuleType)
            {
                case EComponent.LeftChassis:
                case EComponent.RightChassis:
                    RepairPlayerTracks(room, player);
                    break;
                case EComponent.AmmoBay:
                    player.AmmoRackState = 0;
                    player.AmmoRackHp = player.AmmoRackMaxHp;
                    SendWidget(room, player, null, (byte)EComponent.AmmoBay, 0, 0);
                    break;
                case EComponent.FuelTank:
                    ExtinguishPlayerFire(room, player);
                    break;
                default:
                    RepairAllPlayerModules(room, player, extinguishFire: DateTime.UtcNow < player.BurnUntil);
                    break;
            }
        }

        Console.WriteLine($"[RS] UseConsumable: {session.PlayerGUID} provision={msg.ProvisionId} action={actionId} module={(EComponent)msg.ModuleType} slot={slot} add={additionalId}");
        session.Send(new PK_RC_UseConsumable { SucceedFlag = 1, ProvisionId = msg.ProvisionId, ModuleType = msg.ModuleType, AdditionalId = additionalId });

        if (reusable)
            SendConsumableCooldowns(session, player, now);
    }

    private static void SendConsumableCooldowns(GameSession session, RoomPlayer player, DateTime now)
    {
        var cds = new long[3];
        for (int i = 0; i < 3; i++)
        {
            double remMs = (player.ConsumableCdUntil[i] - now).TotalMilliseconds;
            cds[i] = remMs > 0 ? (long)Math.Ceiling(remMs) : 0L;
        }
        session.Send(new PK_RC_Refresh_Consume_CD
        {
            PlayerGUID = player.PlayerGUID,
            ConsumeCDMs = cds,
        });
    }

    private void RepairPlayerTracks(BattleRoom room, RoomPlayer p)
    {
        var widgets = new List<(byte, byte, byte)>();
        if (p.ChassisLDamaged) { p.ChassisLDamaged = false; widgets.Add(((byte)EComponent.LeftChassis, 0, 0)); }
        if (p.ChassisRDamaged) { p.ChassisRDamaged = false; widgets.Add(((byte)EComponent.RightChassis, 0, 0)); }
        p.TrackRepairAt = DateTime.MinValue;
        if (p.TrackImmunitySeconds > 0f)
            p.TrackImmuneUntil = DateTime.UtcNow.AddSeconds(p.TrackImmunitySeconds);
        if (widgets.Count > 0)
        {
            BroadcastToRoom(room, new PK_RC_WidgetState
            {
                PlayerGUID = p.PlayerGUID,
                AttackPlayerGuid = 0,
                Widgets = widgets.ToArray(),
            });
        }
    }

    private void ExtinguishPlayerFire(BattleRoom room, RoomPlayer p)
    {
        bool hadFireOrFuel = DateTime.UtcNow < p.BurnUntil || p.FuelTankState != 0;
        p.BurnUntil = DateTime.MinValue;
        p.FuelTankState = 0;
        p.FuelTankHp = p.FuelTankMaxHp;
        if (hadFireOrFuel)
            SendWidget(room, p, null, (byte)EComponent.FuelTank, 0, 0);
    }

    private void RepairAllPlayerModules(BattleRoom room, RoomPlayer p, bool extinguishFire)
    {
        RepairPlayerTracks(room, p);
        if (p.AmmoRackState != 0)
        {
            p.AmmoRackState = 0;
            p.AmmoRackHp = p.AmmoRackMaxHp;
            SendWidget(room, p, null, (byte)EComponent.AmmoBay, 0, 0);
        }
        if (extinguishFire)
            ExtinguishPlayerFire(room, p);
    }

    private void HandleSmoke(GameSession session, PK_CR_Smoke msg)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player) || player.Room == null) return;
        var room = player.Room;

        int smokeId = 1;
        if (room.PendingSmokes.TryRemove(msg.SmokeIndex, out var pending))
            smokeId = pending.SmokeId > 0 ? pending.SmokeId : 1;

        var tableSmoke = _gameTables.GetSmokeBomb(smokeId);
        float radius = tableSmoke?.SmokeRadius > 0 ? tableSmoke.SmokeRadius : 15f;
        int durationSec = tableSmoke?.ContinuedTime > 0 ? tableSmoke.ContinuedTime : 20;

        int count = Math.Clamp((int)msg.Count, 1, 6);
        var centers = new (float X, float Z)[count + 1];
        for (int i = 0; i < count; i++)
            centers[i] = (msg.SmokeAroundPosArray[i].X, msg.SmokeAroundPosArray[i].Z);
        centers[count] = (msg.TankPosX, msg.TankPosZ);

        room.ActiveSmokes[msg.SmokeIndex] = new ActiveSmokeCloud
        {
            SmokeIndex = msg.SmokeIndex,
            OwnerGuid = player.PlayerGUID,
            Centers = centers,
            Radius = radius,
            ExpiresAt = DateTime.UtcNow.AddSeconds(durationSec),
        };

        Console.WriteLine($"[RS] Smoke deployed: player={player.PlayerGUID} idx={msg.SmokeIndex} smokeId={smokeId} r={radius} dur={durationSec}s");
        BroadcastToRoom(room, new PK_RC_Smoke
        {
            SmokeIndex = msg.SmokeIndex,
            PlayerGuid = player.PlayerGUID,
            SmokeId = smokeId,
            TankPosX = msg.TankPosX,
            TankPosY = msg.TankPosY,
            TankPosZ = msg.TankPosZ,
            Count = msg.Count,
            SmokeAroundPosArray = msg.SmokeAroundPosArray,
        });
    }

    private void CheckSmokeClouds(BattleRoom room)
    {
        if (room.ActiveSmokes.IsEmpty) return;
        var now = DateTime.UtcNow;
        foreach (var kv in room.ActiveSmokes)
        {
            if (now >= kv.Value.ExpiresAt && room.ActiveSmokes.TryRemove(kv.Key, out _))
            {
                BroadcastToRoom(room, new PK_RC_SmokeDisappear { SmokeId = kv.Key });
            }
        }
    }

    private void HandleGunTurretState(GameSession session, PK_CR_SelfTankGunTurrentState state)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player) || player.Room == null) return;
        BroadcastToRoomOthers(player.Room, session, new PK_RC_OtherTankGunTurrentState
        {
            PlayerGUID  = session.PlayerGUID,
            GunAngle    = state.GunAngle,
            TurretAngle = state.TurretAngle,
        });
    }

    /// <summary>
    /// Посмертный вид: по левому клику переключает камеру игрока на следующего живого
    /// союзника в комнате (по кругу; если живых союзников не осталось — возвращает на свой танк).
    /// </summary>
    private void HandleDeadCameroOnFriend(GameSession session, PK_CR_DeadCameroOnFriend req)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var player) || player.Room == null) return;
        var room = player.Room;

        var allies = room.Players.Values
            .Where(p => p.Group == player.Group && !p.IsDead && p.IsLoaded && p.PlayerGUID != player.PlayerGUID)
            .OrderBy(p => p.PlayerGUID)
            .ToList();

        RoomPlayer nextTarget;
        if (allies.Count == 0)
        {
            nextTarget = player;
        }
        else
        {
            int curIdx = allies.FindIndex(a => a.PlayerGUID == req.FriendGuid);
            nextTarget = curIdx >= 0 ? allies[(curIdx + 1) % allies.Count] : allies[0];
        }

        if (nextTarget.PlayerGUID != player.PlayerGUID)
        {
            try { session.Send(BuildSnapshotMovePacket(nextTarget)); } catch { }
        }

        float visionRange = nextTarget.DetectRange > 0f ? nextTarget.DetectRange : 250f;

        try
        {
            session.Send(new PK_RC_DeadCameroOnFriend
            {
                PlayerGuid = nextTarget.PlayerGUID,
                CircularVisionRadiusRange = visionRange,
            });
        }
        catch { }
    }

    // ─────────────────────────── hit resolution ────────────────────────────────
    private int ResolveIncomingShellId(RoomPlayer attacker, int packetShellId)
        => packetShellId > 0 ? packetShellId
         : attacker.CurrentShellId > 0 ? attacker.CurrentShellId
         : _gameTables.GetGun(attacker.MainGunID)?.Ammo1Id ?? 1;

    private static void ClearResolvedShot(RoomPlayer player, int shellId, short shellIndex)
    {
        lock (player.RecentResolvedHits)
            player.RecentResolvedHits.Remove((shellId, shellIndex));
    }

    /// <summary>
    /// Deduplicates hit packets for the same fired shot (e.g. client sending both
    /// APHit and ShellHit on shell swap, or multiple hitboxes on a single projectile).
    /// </summary>
    private static bool TryClaimShotHit(RoomPlayer attacker, int shellId, short shellIndex)
    {
        var now = DateTime.UtcNow;
        var key = (shellId, shellIndex);
        lock (attacker.RecentResolvedHits)
        {
            if (attacker.RecentResolvedHits.TryGetValue(key, out var seenAt)
                && (now - seenAt).TotalSeconds < 15.0)
                return false;

            if (attacker.RecentResolvedHits.Count > 32)
            {
                foreach (var oldKey in attacker.RecentResolvedHits
                    .Where(kv => (now - kv.Value).TotalSeconds >= 15.0)
                    .Select(kv => kv.Key)
                    .ToList())
                    attacker.RecentResolvedHits.Remove(oldKey);
            }

            attacker.RecentResolvedHits[key] = now;
            return true;
        }
    }

    private void HandleShellHit(GameSession session, PK_CR_ShellHit hit)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var attacker) || attacker.Room == null) return;

        // ── ALWAYS confirm the shooter's OWN shell first ──────────────────────────
        // The local player's shell waits in the client "HitMain" state for HasTouchDown,
        // which only PK_RC_MainShootRes sets. Send it for EVERY reported hit — enemy,
        // friendly, or terrain — otherwise the shell freezes in mid-air. ShellResult=2
        // just makes it play an impact (the actual damage is decided below / via APHit).
        _players.TryGetValue(hit.TargetGUID, out var maybeTarget);
        bool validEnemy = maybeTarget != null && maybeTarget.Group != attacker.Group;
        session.Send(new PK_RC_MainShootRes
        {
            ShellID    = hit.ShellID,
            ShellIndex = hit.ShellIndex,
            AttackerGUID = attacker.PlayerGUID,
            TargetGUID   = validEnemy ? hit.TargetGUID : 0,
            HitPosX = hit.HitPosX, HitPosY = hit.HitPosY, HitPosZ = hit.HitPosZ,
            LocalPosX = hit.HitPosX, LocalPosY = hit.HitPosY, LocalPosZ = hit.HitPosZ,
            NormalX = hit.NormalX, NormalY = hit.NormalY, NormalZ = hit.NormalZ,
            ShellResult = 2,
        });

        if (!validEnemy) return;
        var target = maybeTarget!;


        // AP/APCR/HEAT report their penetration separately via APHit (which we trust
        // for damage). ShellHit here only resolves HE shells, so AP damage isn't
        // applied twice. HE has no APHit, so it must be handled on this path.
        //
        // Use the packet's own ShellID before attacker.CurrentShellId so switching shells
        // mid-flight cannot misclassify an AP shot as HE and double-apply damage.
        int shellId = ResolveIncomingShellId(attacker, hit.ShellID);
        var hitShell = _gameTables.GetShell(shellId);
        bool isHE = hitShell != null && hitShell.Type == (int)ShellKind.He;
        if (!isHE) return;
        if (!TryClaimShotHit(attacker, shellId, hit.ShellIndex)) return;

        ResolveHit(attacker.Room, attacker, target,
            shellId, hit.ShellIndex,
            hit.HitPosX, hit.HitPosY, hit.HitPosZ,
            hit.NormalX, hit.NormalY, hit.NormalZ,
            hit.HitLayer, hit.HitModel);
    }

    // Client-confirmed armor penetration (AP/APCR/HEAT). DamagedCount>0 means the
    // shell actually penetrated and damaged components, so we apply full damage and
    // skip the server's estimated armor roll (which was bouncing valid hits).
    private void HandleAPHit(GameSession session, PK_CR_APHit hit)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var attacker) || attacker.Room == null) return;
        if (!_players.TryGetValue(hit.PlayerGUID, out var target)) return;
        if (attacker.Group == target.Group) return;
        if (hit.DamagedCount == 0) return;   // hit but no penetration -> no damage

        int shellId = ResolveIncomingShellId(attacker, hit.ShellId);
        if (!TryClaimShotHit(attacker, shellId, hit.ShellIndex)) return;

        // Клиент сам сообщает, КАКИЕ модули задеты (EComponent) — используем это вместо
        // геометрических догадок. Гусеница рвётся ТОЛЬКО если клиент прислал ходовую.
        bool hitTurret = false, hitLeftTrack = false, hitRightTrack = false;
        foreach (byte comp in hit.AssemblyTypes)
        {
            if (comp == 4 || comp == 5 || comp == 11) hitTurret = true;   // башня/орудие/погон
            else if (comp == 2) hitLeftTrack  = true;                     // LeftChassis
            else if (comp == 3) hitRightTrack = true;                     // RightChassis
        }
        bool hitTrack = hitLeftTrack || hitRightTrack;
        if (hitTrack && _random.Next(100) < 35)
            DamageTrackSide(attacker.Room, attacker, target, hitRightTrack && !hitLeftTrack, sendShooterTip: false);

        ResolveHit(attacker.Room, attacker, target,
            shellId, hit.ShellIndex,
            hit.HitX, hit.HitY, hit.HitZ,
            0, 1, 0,
            0, 0, clientConfirmedPen: true,
            hitTurretPart: hitTurret, clientAngle: hit.MendAngle,
            trackDamaged: hitTrack);
    }

    // 1K-17 laser (shell type 6) — a special guaranteed-damage weapon. Unlike AP we do
    // NOT gate on DamagedCount: the laser reports a hit that may carry no component
    // list yet must still deal its full shell damage. Resolved as a confirmed pen.
    private void HandleLaserHit(GameSession session, PK_CR_LaserHit hit)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var attacker) || attacker.Room == null) return;
        if (!_players.TryGetValue(hit.PlayerGUID, out var target)) return;
        if (attacker.Group == target.Group) return;
        int shellId = ResolveIncomingShellId(attacker, hit.ShellId);
        if (!TryClaimShotHit(attacker, shellId, hit.ShellIndex)) return;
        ResolveHit(attacker.Room, attacker, target,
            shellId, hit.ShellIndex,
            hit.HitX, hit.HitY, hit.HitZ,
            0, 1, 0,
            0, 0, clientConfirmedPen: true);
    }

    // v50 (задача 1): разрушаемые объекты сцены (грузовики, коробки, укрепления). Клиент
    // (стрелявший игрок или таранивший танк) сообщает о попадании по объекту с его sid; сервер
    // рассылает снос ВСЕМ в комнате, включая стрелявшего — его клиент ждёт этот пакет и локально
    // объект не сносит. Раньше сервер эти сообщения игнорировал + формат PK_RC_DestroyedObject был
    // битый, поэтому объекты не разрушались вообще (ни игроком, ни ботом).
    private void HandleSceneInfo(GameSession session, PK_CR_SceneInfo info)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var p) || p.Room == null) return;
        BroadcastToRoom(p.Room, new PK_RC_SceneInfo { Num = info.Num, PointX = info.PointX, PointY = info.PointY, PointZ = info.PointZ });

        // v54 (задача 1): валидация извлечённых objectID. Клиент прислал sid + точку попадания —
        // сверяем с нашей извлечённой таблицей (позиция объекта с этим sid на этой карте). Если
        // координаты близки — извлечение верное и снос ботами по этой таблице безопасен.
        var objs = _gameTables.GetDestructibles(p.Room.MapId);
        if (objs.Count > 0)
        {
            SceneObjMatch(objs, info.Num, info.PointX, info.PointZ, p.Room.MapId);
        }
    }

    private void SceneObjMatch(System.Collections.Generic.IReadOnlyList<GameTablesService.SceneObj> objs,
                               ushort id, float hitX, float hitZ, int mapId)
    {
        foreach (var o in objs)
            if (o.Id == id)
            {
                float d = MathF.Sqrt((o.X - hitX) * (o.X - hitX) + (o.Z - hitZ) * (o.Z - hitZ));
                Console.WriteLine($"[RS] SceneInfo validate map={mapId} sid={id}: hit=({hitX:F1},{hitZ:F1}) extracted=({o.X:F1},{o.Z:F1}) dist={d:F1}м {(d < 8f ? "OK" : "MISMATCH")}");
                return;
            }
        Console.WriteLine($"[RS] SceneInfo validate map={mapId} sid={id}: НЕТ в извлечённой таблице (hit=({hitX:F1},{hitZ:F1}))");
    }

    // v57 (задача 1): бот сносит разрушаемый объект, к которому подъехал вплотную (крушит проездом).
    private void TryDestroyObjectsNearBot(BattleRoom room, RoomPlayer bot)
    {
        var objs = _gameTables.GetDestructibles(room.MapId);
        if (objs.Count == 0) return;
        const float R2 = 4.5f * 4.5f;      // радиус «крушения» вокруг центра танка
        foreach (var o in objs)
        {
            if (room.DestroyedSceneObjects.Contains(o.Id)) continue;
            float dx = o.X - bot.PosX, dz = o.Z - bot.PosZ;
            if (dx * dx + dz * dz > R2) continue;
            room.DestroyedSceneObjects.Add(o.Id);
            SendSceneDestroy(room, o, bot.PosX, bot.PosZ);
        }
    }

    // v60: снос объекта нужным пакетом — валящийся (дерево/столб, can_fall) роняем через
    // PK_RC_SceneInfoDir в сторону ОТ источника (танка), остальные бьём через PK_RC_SceneInfo.
    private void SendSceneDestroy(BattleRoom room, GameTablesService.SceneObj o, float fromX, float fromZ)
    {
        if (o.IsFall)
        {
            float dx = o.X - fromX, dz = o.Z - fromZ;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 0.01f) { dx = 0f; dz = 1f; len = 1f; }
            BroadcastToRoom(room, new PK_RC_SceneInfoDir { Num = o.Id, DirX = dx / len, DirY = 0f, DirZ = dz / len });
        }
        else
        {
            BroadcastToRoom(room, new PK_RC_SceneInfo { Num = o.Id, PointX = o.X, PointY = o.Y, PointZ = o.Z });
        }
    }

    // v54 (задача 1): если снаряд бота проходит рядом с разрушаемым объектом сцены (по прямой
    // от бота к точке прицела), сносим ближайший к боту такой объект — как это делает снаряд
    // игрока. У бота нет клиента, поэтому объект сносит сервер и шлёт PK_RC_DestroyedObject всем.
    private void TryDestroyObjectsAlongShot(BattleRoom room, float x0, float z0, float x1, float z1)
    {
        var objs = _gameTables.GetDestructibles(room.MapId);
        if (objs.Count == 0) return;
        float dx = x1 - x0, dz = z1 - z0;
        float len2 = dx * dx + dz * dz;
        if (len2 < 1f) return;
        const float R = 3.0f;                 // радиус «попадания» снаряда по объекту
        ushort bestId = 0; float bestT = 2f; bool found = false;
        GameTablesService.SceneObj bestObj = default;
        foreach (var o in objs)
        {
            if (room.DestroyedSceneObjects.Contains(o.Id)) continue;
            float t = ((o.X - x0) * dx + (o.Z - z0) * dz) / len2;   // проекция на отрезок [0..1]
            if (t < 0f || t > 1f) continue;                        // объект не между ботом и целью
            float px = x0 + t * dx, pz = z0 + t * dz;
            float perp2 = (o.X - px) * (o.X - px) + (o.Z - pz) * (o.Z - pz);
            if (perp2 > R * R) continue;
            if (t < bestT) { bestT = t; bestId = o.Id; bestObj = o; found = true; }  // ближайший к боту
        }
        if (found)
        {
            room.DestroyedSceneObjects.Add(bestId);
            SendSceneDestroy(room, bestObj, x0, z0);
        }
    }

    private void HandleSceneInfoDir(GameSession session, PK_CR_SceneInfoDir info)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var p) || p.Room == null) return;
        BroadcastToRoom(p.Room, new PK_RC_DestroyedObjectDir
            { ObjectID = info.Num, DirX = info.DirX, DirY = info.DirY, DirZ = info.DirZ });
    }

    /// <summary>
    /// Shared hit-resolution used for both player-reported hits and server-simulated bot hits.
    /// Handles all 4 modes:
    ///   • BaseCapture / Respawn: standard armour roll (pen/nopen/ricochet)
    ///   • Defense:               same as standard
    ///   • Simplified:            every hit deals damage; non-pens/ricochets do reduced damage
    /// </summary>
    // ── Гусеницы (сбитие/ремонт) ────────────────────────────────────────────
    private const float TrackRepairSeconds = 12f; // через сколько ход восстанавливается

    // Ломает гусеницу указанной стороны. Сторону сообщает КЛИЕНТ (EComponent
    // LeftChassis/RightChassis в PK_CR_APHit) — сервер больше не угадывает её по
    // геометрии, из-за чего раньше гусеница рвалась при попадании в корпус.
    private void DamageTrackSide(BattleRoom room, RoomPlayer attacker, RoomPlayer target, bool right, bool sendShooterTip = true)
    {
        if (target.IsDead) return;
        if (DateTime.UtcNow < target.TrackImmuneUntil) return; // Track Maintenance Device (attr 1007) immunity
        if (right ? target.ChassisRDamaged : target.ChassisLDamaged) return; // уже сбита
        if (right) target.ChassisRDamaged = true; else target.ChassisLDamaged = true;
        float repairSec = TrackRepairSeconds / Math.Max(1f, 1f + target.TrackRepairSpeedBonus) * target.ConsumableRepairMult;
        repairSec = Math.Clamp(repairSec, 3f, 20f);
        target.TrackRepairAt = DateTime.UtcNow.AddSeconds(repairSec);
        target.CurrentSpeedMps = 0f;                 // мгновенно обездвижить (бот)
        byte comp = right ? (byte)3 : (byte)2;       // EComponent.RightChassis=3 / LeftChassis=2
        // v18 (баг 1): Param — это ДЛИТЕЛЬНОСТЬ индикатора у клиента, а не «резерв».
        // TankUIState.ApplyWidgetBuf -> InitOrResetBuf(ChassisInjure, param): именно этот
        // элемент и рисует крупную надпись про сбитую гусеницу с иконкой и обратным
        // отсчётом. Мы слали 0 — индикатор создавался с нулевой длительностью и гас
        // мгновенно, поэтому надписи не было видно. Теперь шлём реальные секунды ремонта.
        BroadcastToRoom(room, new PK_RC_WidgetState
        {
            PlayerGUID       = target.PlayerGUID,
            AttackPlayerGuid = attacker.PlayerGUID,
            Widgets          = new (byte, byte, byte)[]
                { (comp, (byte)Math.Ceiling(repairSec), (byte)2) }, // State.Damage=2
        });
        // v12: крупные надписи — жертве «ГУСЕНИЦА УНИЧТОЖЕНА» (EBeTip 4), стрелку
        // «уничтожил гусеницу» (EShootTip 5), как на референсном скрине.
        if (!target.IsBot)
            try { target.Session.Send(new PK_RC_BeShootedTip
                  { AttackerGuid = attacker.PlayerGUID, BeShootTips = new byte[] { 4 } }); } catch { }
        if (sendShooterTip && !attacker.IsBot)
            try { attacker.Session.Send(new PK_RC_ShootTip
                  { PlayerGuid = target.PlayerGUID, ShootFlag = 0, Damage = 0, ShootTip = 5 }); } catch { }
    }

    // Возвращает ход, когда таймер ремонта истёк, и сообщает клиенту (State.Normal=0).
    private void CheckTrackRepair(BattleRoom room, RoomPlayer p)
    {
        if (!p.ChassisLDamaged && !p.ChassisRDamaged) return;
        if (DateTime.UtcNow < p.TrackRepairAt) return;
        var widgets = new System.Collections.Generic.List<(byte, byte, byte)>();
        if (p.ChassisLDamaged) { widgets.Add(((byte)2, (byte)0, (byte)0)); p.ChassisLDamaged = false; }
        if (p.ChassisRDamaged) { widgets.Add(((byte)3, (byte)0, (byte)0)); p.ChassisRDamaged = false; }
        BroadcastToRoom(room, new PK_RC_WidgetState
        {
            PlayerGUID = p.PlayerGUID, AttackPlayerGuid = 0,
            Widgets    = widgets.ToArray(),
        });
    }

    // ── v12: внутренние модули — боеукладка (EComponent 8) и топливный бак (9) ──
    //
    // ХП модулей — из клиентских таблиц Parts: боеукладка по CartridgeId (например,
    // ИС-7 = 739), бак по FueltankId (302–531 по классу). Пробивший корпус снаряд с
    // шансом (борт/корма выше) наносит модулю 60–100% своего урона. Состояния зеркалят
    // клиентский enum State: ниже 50% → Injure (жёлтый, надпись «повреждена»), в ноль:
    //   • боеукладка → ДЕТОНАЦИЯ — мгновенная смерть, жертве EBeTip 8, стрелку EShootTip 3;
    //   • бак → ПОЖАР BurnSeconds сек (клиент сам включает огонь по State.Damage),
    //     тик 2.5% макс. ХП в секунду, урон зачитывается поджигателю.
    // Повреждённые модули чинятся сами через ModuleRepairSeconds (как гусеницы).
    private const int   ModuleRepairSeconds = 20;
    private const int   BurnSeconds         = 8;
    private const float BurnHpFracPerSec    = 0.025f;

    private void EnsureModuleHp(RoomPlayer p)
    {
        if (p.AmmoRackMaxHp > 0f) return;
        var tank    = _gameTables.GetTank(p.VehicleId);
        int rackId  = p.CartridgeID > 0 ? p.CartridgeID : tank?.CartridgeId ?? 0;
        int fuelId  = p.FueltankID  > 0 ? p.FueltankID  : tank?.FueltankId  ?? 0;
        var rack    = _gameTables.GetPart(8, rackId);
        var fuel    = _gameTables.GetPart(9, fuelId);
        int tier    = Math.Max(1, tank?.Tier ?? p.Tier);
        p.AmmoRackMaxHp = rack is { Value: > 0f } ? rack.Value : 180f + tier * 55f;
        p.FuelTankMaxHp = fuel is { Value: > 0f } ? fuel.Value : 250f + tier * 12f;
        p.AmmoRackHp    = p.AmmoRackMaxHp;
        p.FuelTankHp    = p.FuelTankMaxHp;
    }

    private void SendWidget(BattleRoom room, RoomPlayer target, RoomPlayer? attacker, byte comp, byte state, byte param)
        => BroadcastToRoom(room, new PK_RC_WidgetState
        {
            PlayerGUID       = target.PlayerGUID,
            AttackPlayerGuid = attacker?.PlayerGUID ?? 0,
            Widgets          = new (byte, byte, byte)[] { (comp, param, state) },
        });

    private bool TryTriggerAutoExtinguisher(BattleRoom room, RoomPlayer target)
    {
        for (int i = 0; i < target.Provisions.Length; i++)
        {
            int pid = target.Provisions[i];
            if ((pid == 4 || pid == 5) && !target.ProvisionUsed[i])
            {
                target.ProvisionUsed[i] = true;
                target.BurnUntil = DateTime.MinValue;
                target.FuelTankState = 0;
                target.FuelTankHp = target.FuelTankMaxHp;
                SendWidget(room, target, null, (byte)EComponent.FuelTank, 0, 0);
                if (!target.IsBot && target.Session != null)
                {
                    try
                    {
                        target.Session.Send(new PK_RC_UseConsumable
                        {
                            SucceedFlag = 1,
                            ProvisionId = pid,
                            ModuleType = (byte)EComponent.FuelTank,
                            AdditionalId = pid,
                        });
                    }
                    catch { }
                }
                Console.WriteLine($"[RS] AutoExtinguisher triggered for {target.PlayerGUID} using provision {pid}");
                return true;
            }
        }
        return false;
    }

    // Бросок урона по модулям на ПРОБИВШЕМ попадании. Возвращает подсказки жертве
    // (список EBeShootTip), подсказку стрелку (EShootTip) и флаг детонации БК.
    private (byte[] victimTips, byte shooterTip, bool detonated) RollModuleDamage(
        BattleRoom room, RoomPlayer attacker, RoomPlayer target, int damage, float absFace, bool hitTurret)
    {
        EnsureModuleHp(target);
        bool sideRear   = absFace >= 60f;
        // Сниженные шансы и урон по модулям: первый пробой не взрывает целый БК/бак с одного выстрела.
        int  rackChance = (int)MathF.Round((hitTurret ? (sideRear ? 4 : 1) : (sideRear ? 7 : 2)) * target.AmmoBayNoDmgMult);
        int  fuelChance = (int)MathF.Round((hitTurret ? 0 : (sideRear ? 6 : 2)) * target.FuelTankNoDmgMult);
        float moduleDmg = damage * (0.25f + (float)_random.NextDouble() * 0.15f);

        // Не больше одного модуля за попадание: сперва бросок по БК, затем по баку.
        if (target.AmmoRackState != 2 && _random.Next(100) < rackChance)
        {
            bool wasHealthy = target.AmmoRackState == 0;
            target.AmmoRackHp -= moduleDmg;
            if (wasHealthy && target.AmmoRackHp <= 0f)
                target.AmmoRackHp = MathF.Max(1f, target.AmmoRackMaxHp * 0.25f);

            if (target.AmmoRackHp <= 0f)
            {
                target.AmmoRackState = 2;
                target.AmmoRackHp    = 0f;
                SendWidget(room, target, attacker, 8, 2, 0);
                Console.WriteLine($"[RS] ДЕТОНАЦИЯ боеукладки: {attacker.PlayerGUID} -> {target.PlayerGUID}");
                return (new byte[] { 8 }, 3, true);       // убит взрывом БК / уничтожил БК
            }
            if (target.AmmoRackState == 0 && target.AmmoRackHp <= target.AmmoRackMaxHp * 0.5f)
            {
                target.AmmoRackState    = 1;
                target.AmmoRackRepairAt = DateTime.UtcNow.AddSeconds(ModuleRepairSeconds);
                SendWidget(room, target, attacker, 8, 1, (byte)ModuleRepairSeconds);
                return (new byte[] { 2 }, 6, false);      // боеукладка повреждена / повредил БК
            }
            return (Array.Empty<byte>(), 6, false);       // урон в модуль без смены состояния
        }
        if (target.FuelTankState != 2 && _random.Next(100) < fuelChance)
        {
            bool wasHealthy = target.FuelTankState == 0;
            target.FuelTankHp -= moduleDmg;
            if (wasHealthy && target.FuelTankHp <= 0f)
                target.FuelTankHp = MathF.Max(1f, target.FuelTankMaxHp * 0.25f);

            if (target.FuelTankHp <= 0f)
            {
                // Gold Extinguisher (id=4): auto-extinguish fire if unused slot equipped
                if (target.HasAutoExtinguisher && TryTriggerAutoExtinguisher(room, target))
                    return (Array.Empty<byte>(), 7, false);

                target.FuelTankState    = 2;               // клиент по State.Damage включит огонь
                target.FuelTankHp       = 0f;
                target.BurnUntil        = DateTime.UtcNow.AddSeconds(BurnSeconds);
                target.NextBurnTick     = DateTime.UtcNow.AddSeconds(1);
                target.BurnAttackerGuid = attacker.PlayerGUID;
                SendWidget(room, target, attacker, 9, 2, (byte)BurnSeconds);
                Console.WriteLine($"[RS] ПОЖАР (бак): {attacker.PlayerGUID} -> {target.PlayerGUID}");
                return (new byte[] { 5, 10 }, 4, false);  // бак уничтожен + пожар / уничтожил бак
            }
            if (target.FuelTankState == 0 && target.FuelTankHp <= target.FuelTankMaxHp * 0.5f)
            {
                target.FuelTankState    = 1;
                target.FuelTankRepairAt = DateTime.UtcNow.AddSeconds(ModuleRepairSeconds);
                SendWidget(room, target, attacker, 9, 1, (byte)ModuleRepairSeconds);
                return (new byte[] { 3 }, 7, false);      // бак повреждён / повредил бак
            }
            return (Array.Empty<byte>(), 7, false);
        }
        return (Array.Empty<byte>(), 0, false);
    }

    // Секундный тик пожара. Несмертельные тики капают ХП напрямую (SynHP); смертельный
    // тик прогоняется через ResolveHit(fixedDamage) — так срабатывает ВЕСЬ штатный
    // конвейер смерти (очки, TankDead, респаун), не дублируя его здесь.
    private void ProcessBurnTick(BattleRoom room, RoomPlayer p)
    {
        if (p.FuelTankState != 2 || p.IsDead) return;
        var now = DateTime.UtcNow;
        if (now >= p.BurnUntil)
        {
            p.FuelTankState    = 1;                        // потушился, бак остаётся повреждённым
            p.FuelTankHp       = p.FuelTankMaxHp * 0.4f;
            p.FuelTankRepairAt = now.AddSeconds(ModuleRepairSeconds);
            SendWidget(room, p, null, 9, 1, (byte)ModuleRepairSeconds);
            return;
        }
        if (now < p.NextBurnTick) return;
        p.NextBurnTick = now.AddSeconds(1);
        int burn = Math.Max(10, (int)(p.MaxHp * BurnHpFracPerSec * p.FireDamageMult));
        room.Players.TryGetValue(p.BurnAttackerGuid, out var arsonist);
        if (p.CurHp - burn <= 0 && arsonist != null && arsonist.Group != p.Group)
        {
            ResolveHit(room, arsonist, p, 0, 0, p.PosX, p.PosY + 1f, p.PosZ, 0, 1, 0, 0, 0,
                       clientConfirmedPen: false, fixedDamage: burn);
            return;
        }
        p.CurHp = Math.Max(1, p.CurHp - burn);             // без поджигателя огонь не добивает
        if (arsonist != null && arsonist.Group != p.Group) arsonist.TotalDamage += burn;
        BroadcastToRoom(room, new PK_RC_SynHP
        {
            AttackerGUID = arsonist?.PlayerGUID ?? 0, PlayerGUID = p.PlayerGUID,
            CurHp = p.CurHp, Reason = 0,
        });
    }

    // Автопочинка повреждённых (Injure) модулей до Normal + полный ХП модуля.
    private void CheckModuleRepair(BattleRoom room, RoomPlayer p)
    {
        if (p.IsDead) return;
        var now = DateTime.UtcNow;
        if (p.AmmoRackState == 1 && now >= p.AmmoRackRepairAt)
        {
            p.AmmoRackState = 0;
            p.AmmoRackHp    = p.AmmoRackMaxHp;
            SendWidget(room, p, null, 8, 0, 0);
        }
        if (p.FuelTankState == 1 && now >= p.FuelTankRepairAt)
        {
            p.FuelTankState = 0;
            p.FuelTankHp    = p.FuelTankMaxHp;
            SendWidget(room, p, null, 9, 0, 0);
        }
    }

    // v19: РЕАЛЬНАЯ броня башен, извлечённая из level0 (268 записей, сверено с ангаром:
    // Объект 279 — 280/175/95, Объект 280 — 290/110/80). Ключ — TurretId.
    // Броня корпусов в бинаре лежит с плавающей структурой и пока не извлечена, поэтому
    // для корпуса остаётся оценка от тира.
    private static readonly Dictionary<int, int[]> TurretArmor = LoadArmorTable("turret_armor.json");
    // v22: РЕАЛЬНАЯ броня КОРПУСОВ из level0 — 289 записей. Ключ — HullId.
    // Броня лежит в бинаре ПЕРЕД именем корпуса на фиксированном смещении 188 байт
    // (именно это я и не мог найти раньше — искал поля ПОСЛЕ имени, как у башен).
    // Сверено с ангаром: Объект 279 — 187/178/55, Объект 280 — 190/120/70.
    private static readonly Dictionary<int, int[]> HullArmor = LoadArmorTable("hull_armor.json");

    private static Dictionary<int, int[]> LoadArmorTable(string fileName)
    {
        var res = new Dictionary<int, int[]>();
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "GameTables", "Data", fileName);
            if (!File.Exists(path)) return res;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var kv in doc.RootElement.EnumerateObject())
            {
                var arr = kv.Value.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (arr.Length == 3 && int.TryParse(kv.Name, out int id)) res[id] = arr;
            }
            Console.WriteLine($"[RS] Armor table {fileName}: {res.Count} entries");
        }
        catch (Exception ex) { Console.WriteLine($"[RS] Armor table {fileName} failed: {ex.Message}"); }
        return res;
    }

    private void ResolveHit(BattleRoom room, RoomPlayer attacker, RoomPlayer target,
        int shellID, int shellIndex,
        float hitX, float hitY, float hitZ,
        float normX, float normY, float normZ,
        byte hitLayer, byte hitModel,
        bool clientConfirmedPen = false,   // v21: НЕ признак пробития (клиент шлёт APHit
                                           // при любом попадании); оставлен для совместимости
                                           // вызовов и будущей диагностики
        int fixedDamage = 0,
        float attackerSkill = 0f,
        bool hitTurretPart = false,
        byte clientAngle = 255,
        bool trackDamaged = false)
    {
        if (target.IsDead) return;
        // Friendly-fire guard at the resolution layer: no matter which path called us
        // (player-reported hit or bot-simulated hit), same-team hits never deal damage.
        // This is what stops bots from killing their own teammates.
        if (attacker.PlayerGUID == target.PlayerGUID) return;
        if (attacker.Group == target.Group) return;

        int attackerShellId = ResolveIncomingShellId(attacker, shellID);
        var shell       = _gameTables.GetShell(attackerShellId) ?? _gameTables.GetShell(1);
        int baseDamage  = shell?.Damage      ?? 100;
        int penetration = shell?.Penetration ?? 80;
        // v76: реальная схема (decfilesgame enum ShellType) — Ap=1, He=2, ApCr=3,
        // Heat=4, Rocket=5, Laser=6. Раньше здесь стояла ВЫДУМАННАЯ схема
        // (0=AP,1=APCR,2=HEAT,3=HE,4=HEF), из-за которой isHE проверял 3||4 (на деле
        // ApCr/Heat) — настоящие ОФ-снаряды (type==2, напр. 38-см снаряд Штурмтигра,
        // «Sprg»/«UOF» в имени) никогда не считались «фугасом» и урон по ним не
        // проходил вовсе (см. правку isHE в HandleShellHit выше).
        int shellType   = shell?.Type ?? (int)ShellKind.Ap;
        bool isHE       = shellType == (int)ShellKind.He;

        var tgtEntry    = _gameTables.GetTank(target.VehicleId);
        int tier        = tgtEntry?.Tier ?? 1;
        // ── Броня по РЕАЛЬНОЙ грани + УГОЛ встречи ─────────────────────────────────
        // Калибровка по таблице снарядов: пробитие tier5≈100, tier8≈180, tier10-11≈272.
        // Номинальная броня (перпендикуляр): лоб корпуса tier*18+40 (t11=238), лоб башни
        // tier*21+45 (t11=276), борт tier*10+25 (t11=135), корма tier*7+15 (t11=92).
        float tgtYaw    = target.LastYaw;
        float dirToAtt  = MathF.Atan2(attacker.PosX - target.PosX, attacker.PosZ - target.PosZ) * (180f / MathF.PI);
        float absFace   = MathF.Abs(((dirToAtt - tgtYaw + 540f) % 360f) - 180f); // 0=лоб, 90=борт, 180=корма
        // Куда попали: приоритет — данные КЛИЕНТА (он знает точную деталь), иначе по высоте.
        bool  hitHigh   = hitTurretPart || (hitY - target.PosY) > 1.6f;
        // Класс танка: Type 1=лёгкий, 2=средний, 3=тяжёлый, 4=ПТ-САУ (сверено по таблице:
        // E-100/IS-7/Maus/Conqueror = 3, T-44/T-54/Leopard = 2, M24 = 1, Ferdinand = 4).
        int   vtype     = tgtEntry?.Type ?? 2;
        float classMult = vtype switch
        {
            3 => 1.18f,   // тяжёлый — лоб реально держит (v21: было 1.45 — завышало)
            4 => 1.10f,   // ПТ-САУ — крепкая рубка (v21: было 1.20)
            2 => 1.00f,   // средний
            _ => 0.85f,   // лёгкий
        };
        float nominalArmor;
        float obliquity;                                  // угол встречи с плитой, градусы
        // v22: фактическая броня из level0 — и для башни, и для корпуса.
        // Для ПТ-САУ башни нет (37 машин из 186) — попадание «в верх» это рубка,
        // то есть часть корпуса, поэтому падаем на броню корпуса, а не на оценку.
        int[]? realArmor = null;
        if (hitHigh && TurretArmor.TryGetValue(target.TurretID, out var ta)) realArmor = ta;
        else if (HullArmor.TryGetValue(target.HullID, out var ha))           realArmor = ha;
        // v21: КАЛИБРОВКА брони корпуса по фактическим значениям из ангара
        // (лоб 10-го тира: ИС-7 150, Объект 279 187, Объект 280 190, FV214 190,
        // E-100 200, Maus 200 — то есть 150..200, а не 220 и уж точно не 220x1.45=319,
        // как давала старая формула вместе с классовым множителем).
        // Именно завышенная броня корпуса и порождала «легко пробить, а не пробивает»,
        // из-за чего я в v17 ошибочно начал доверять клиентскому APHit как признаку
        // пробития. На самом деле клиент шлёт APHit при ЛЮБОМ попадании в танк
        // (DamageAP: SendDataCache создаётся, если задета хоть одна деталь), решение о
        // пробитии всегда за сервером — поэтому доверие убрано, а формула исправлена.
        if      (absFace <  60f) { nominalArmor = realArmor != null ? realArmor[0]
                                                : (hitHigh ? tier * 17f + 50f : tier * 14f + 45f); obliquity = absFace; }
        // (ветки ниже: борт и корма — так же из таблицы, иначе оценка по тиру)
        else if (absFace < 120f) { nominalArmor = realArmor != null ? realArmor[1] : tier * 11f + 20f; obliquity = MathF.Abs(absFace - 90f); }
        else                     { nominalArmor = realArmor != null ? realArmor[2] : tier *  6f + 15f; obliquity = 180f - absFace; }
        // Классовый множитель — только для корпуса и в разумных пределах: реальная
        // разница между ТТ и СТ одного тира это ~15-20%, а не 45%.
        if (realArmor == null) nominalArmor *= classMult;   // множитель — только к оценке
        // Угол встречи: клиент присылает свой расчёт (MendAngle) — он точнее серверной
        // оценки по курсу. Берём его, если значение осмысленное (0..85 градусов).
        if (clientAngle <= 85) obliquity = clientAngle;
        // ПРИВЕДЁННАЯ броня: под углом снаряду идти сквозь плиту дольше → armor / cos(угол).
        // Ограничиваем множитель 3× (иначе на предельных углах броня уходит в бесконечность).
        float cosO      = MathF.Cos(obliquity * (MathF.PI / 180f));
        float angleMult = MathF.Min(3f, 1f / MathF.Max(0.33f, cosO));
        float effectiveArmor = nominalArmor * angleMult;
        // Мастеровитый бот-стрелок целится в слабые места → эффективная броня чуть ниже.
        if (attackerSkill > 0f) effectiveArmor *= (1f - attackerSkill * 0.20f);
        double margin   = (double)penetration / Math.Max(1, (int)effectiveArmor);
        // Рикошет по УГЛУ: бронебойный снаряд на очень остром угле соскальзывает независимо
        // от пробития (кумулятивные/фугасные — нет). v48.8: порог поднят 60°→70° — на 60° было
        // «слишком частые рикошеты по врагам»; автососкальзывание только на действительно остром угле.
        // v76: было shellType==0||1 (по старой неверной схеме — 0 не существует ни для одного
        // снаряда, 1 случайно совпадал с Ap). Настоящие «болванки» — Ap(1) и ApCr(3); Heat(4) и
        // He(2) от угла не рикошетят (кумулятивная/фугасная струя не «соскальзывает»).
        bool angleRicochet = obliquity >= 70f && (shellType == (int)ShellKind.Ap || shellType == (int)ShellKind.ApCr);

        float  variance = 0.92f + (float)_random.NextDouble() * (1.15f - 0.92f);
        int    damage;
        byte   shellResult;

        if (fixedDamage > 0)
        {
            // Ability strike (Katyusha / Bomber): fixed damage.
            damage      = fixedDamage;
            shellResult = 0;
        }
        else if (isHE)
        {
            // Фугас: гарантированный урон осколками даже без пробития; полный — при пробитии.
            damage      = margin >= 1.0
                ? Math.Max(1, (int)MathF.Round(baseDamage * variance))
                : Math.Max(1, (int)MathF.Round(baseDamage * (0.4f + (float)_random.NextDouble() * 0.35f)));
            shellResult = (byte)(margin >= 1.0 ? 0 : 1);
        }
        else
        {
            // Пробитие решает СЕРВЕР по реальной грани/броне — для ботов (у них нет
            // клиента) и для экстремальных углов. Simplified — гарантированное пробитие.
            double m = margin
                     + (room.GameMode == BattleGameMode.Simplified ? 1.0 : 0.0);
            if (angleRicochet && m < 1.6 && room.GameMode != BattleGameMode.Simplified)
            {
                damage = 0; shellResult = 4;    // РИКОШЕТ по углу (снаряд соскользнул с брони)
            }
            else if (m >= 1.05)
            {
                damage = Math.Max(1, (int)MathF.Round(baseDamage * variance));   // пробитие
                shellResult = 0;
            }
            else if (m >= 0.85)
            {
                if (_random.Next(100) < 70)
                { damage = Math.Max(1, (int)MathF.Round(baseDamage * variance)); shellResult = 0; }
                else { damage = 0; shellResult = 1; }
            }
            else if (m >= 0.50)
            {
                damage = 0; shellResult = 1;    // непробитие (снаряд отскочил)
            }
            else
            {
                // v48.8: было «рикошет» (4) при m<0.5 — из-за этого рикошетов было слишком много.
                // Низкое пробитие по НЕ острому углу — это НЕПРОБИТИЕ (снаряд не прошёл), а не
                // рикошет. Рикошет остаётся только для реально острых углов (angleRicochet выше).
                damage = 0; shellResult = 1;    // непробитие (броня заметно толще пробития)
            }
        }

        // Apply offensive shell damage modifier (e.g. Bore Pressure Intensifier / Def GLD)
        if (fixedDamage == 0 && damage > 0 && attacker.ShellDamageMult != 1f)
            damage = Math.Max(1, (int)MathF.Round(damage * attacker.ShellDamageMult));

        // Ammo Buffer (attr 1019: -5% AP damage taken) + 1% chance to halve damage (attr 1006)
        if (fixedDamage == 0 && damage > 0 && !isHE && target.ApDamageTakenMult != 1f)
            damage = Math.Max(1, (int)MathF.Round(damage * target.ApDamageTakenMult));
        if (fixedDamage == 0 && damage > 1 && target.HalveDamageChance > 0f && _random.NextDouble() < target.HalveDamageChance)
            damage = Math.Max(1, damage / 2);

        // "Для Обороны" защитная сетка: the defending target takes reduced damage and
        // has a small chance to shrug off the hit entirely. Flags are neutral outside
        // defense mode, so this is a no-op there. Applied after every offensive
        // multiplier and to ability strikes too (bombardment/Katyusha go through here
        // as fixedDamage), right before HP is docked.
        if (damage > 0 && target.DamageTakenMult != 1f)
            damage = Math.Max(1, (int)MathF.Round(damage * target.DamageTakenMult));
        if (damage > 0 && target.InvulnChance > 0f && _random.NextDouble() < target.InvulnChance)
        {
            Console.WriteLine($"[RS] Неуязвимость (сетка): {target.PlayerGUID} поглотил урон {damage}");
            damage = 0;
            shellResult = 1; // visualise as a non-penetration
        }

        // ── v12: внутренние модули (боеукладка / бак) — только на состоявшемся
        // пробитии, ПОСЛЕ защитной сетки: поглощённый сеткой выстрел (damage=0)
        // модули не трогает, а детонацию сетка уже не порежет.
        byte[] moduleVictimTips = Array.Empty<byte>();
        byte   moduleShooterTip = 0;
        if (fixedDamage == 0 && damage > 0 && shellResult == 0 && !target.IsDead)
        {
            bool detonated;
            (moduleVictimTips, moduleShooterTip, detonated) =
                RollModuleDamage(room, attacker, target, damage, absFace, hitHigh);
            // Детонация БК — гарантированная смерть: поднимаем урон до летального, дальше
            // работает штатный конвейер (TankDead, очки, респаун).
            if (detonated) damage = Math.Max(damage, target.CurHp + 1);
        }

        target.CurHp        = Math.Max(0, target.CurHp - damage);
        attacker.TotalDamage += damage;
        if (damage > 0) attacker.HitCount++;

        // ── v12: счётчики послебоевой статистики ────────────────────────────────
        if (damage > 0)
        {
            if (fixedDamage == 0 && shellResult == 0) attacker.PenCount++;   // «пробитий»
            attacker.DamagedEnemyGuids.Add(target.PlayerGUID);
            target.BeHitCount++;
            target.DamageTaken += damage;
            if (target.ChassisLDamaged || target.ChassisRDamaged)
                attacker.ChassisShareDmg += damage;                          // «урон по сбитой гусенице»
            // «Засвет»: урон по цели, которую сейчас держит в засвете союзник-человек
            // (не сам стрелок) — зачитывается разведчику.
            foreach (var spotter in room.Players.Values)
            {
                if (!spotter.IsBot && spotter.Group == attacker.Group && spotter.PlayerGUID != attacker.PlayerGUID)
                {
                    bool spotted;
                    lock (spotter.SpotLock) spotted = spotter.SpottedEnemies.Contains(target.PlayerGUID);
                    if (spotted) spotter.LightDamage += damage;
                }
            }

            // v62 (пункт 7): всплывающие панели урона (до 5 строк). Атакующему-человеку —
            // «мои попадания» (цель, суммарный урон, суммарные пробития, урон выстрела, снаряд);
            // жертве-человеку — «попадания по мне» (атакующий, урон, снаряд, тип). Имя/танк
            // клиент подставляет по GUID сам.
            if (!attacker.IsBot && attacker.Session != null)
                try { attacker.Session.Send(new PK_RC_DamageDetailInfo {
                    VictimGuid    = target.PlayerGUID,
                    TotalDamage   = attacker.TotalDamage,
                    TotalPuncture = attacker.PenCount,
                    ThisDamage    = damage,
                    ShellId       = attackerShellId }); } catch { /* disconnected */ }
            if (!target.IsBot && target.Session != null)
                try { target.Session.Send(new PK_RC_BeDamageDetailInfo {
                    AttackerGuid = attacker.PlayerGUID,
                    ThisDamage   = damage,
                    ShellId      = attackerShellId,
                    HitType      = (byte)(moduleShooterTip != 0 ? 3 : 4) }); } catch { /* disconnected */ }
        }

        attacker.LastShellResult = shellResult;   // v13: для декалей (TypeDecal)

        // ── v26: ГУСЕНИЦЫ ОТ ВЫСТРЕЛОВ БОТОВ ────────────────────────────────────
        // Раньше ходовая рвалась ТОЛЬКО в HandleAPHit, то есть исключительно от
        // выстрелов живого игрока (там клиент сам сообщает, какие детали задеты).
        // Бот сбить гусеницу не мог вообще — поэтому игрок никогда не видел надписи
        // «ЛЕВАЯ/ПРАВАЯ ГУСЕНИЦА УНИЧТОЖЕНА» (а это единственная крупная надпись,
        // которую его билд клиента вообще умеет рисовать).
        // Теперь бот рвёт ходовую по геометрии попадания и — что важно — НЕЗАВИСИМО
        // от пробития брони: гусеница рвётся от удара в неё, а не от пробоя корпуса.
        if (attacker.IsBot && fixedDamage == 0 && !target.IsDead
            && !(target.ChassisLDamaged || target.ChassisRDamaged))
        {
            bool lowHit  = (hitY - target.PosY) < 0.85f;          // низко — по ходовой
            bool sideHit = absFace >= 45f && absFace <= 135f;     // сбоку
            // Шанс держим умеренным: точка попадания у ботов всегда «низкая»
            // (центр цели + 0.5 м), поэтому без броска гусеница рвалась бы с каждого
            // выстрела. 35% сбоку, 12% с лобовых ракурсов — как в оригинале.
            if (lowHit && _random.Next(100) < (sideHit ? 14 : 5))
            {
                // сторона: слева/справа от корпуса цели по знаку бокового смещения
                float relRadTr = MathF.Atan2(attacker.PosX - target.PosX,
                                             attacker.PosZ - target.PosZ)
                               - target.LastYaw * (MathF.PI / 180f);
                bool rightSide = MathF.Sin(relRadTr) > 0f;
                DamageTrackSide(room, attacker, target, rightSide);
                Console.WriteLine($"[RS] Track hit by bot {attacker.PlayerGUID} -> " +
                                  $"{target.PlayerGUID} ({(rightSide ? "правая" : "левая")})");
            }
        }

        // v25: диагностика пробития — видно, ПОЧЕМУ не пробил (пробитие снаряда против
        // фактической брони этой грани). Печатаем только для попаданий по живым игрокам,
        // чтобы не засорять консоль перестрелками ботов между собой.
        if (!target.IsBot && fixedDamage == 0)
            Console.WriteLine($"[RS] Hit {(attacker.IsBot ? "bot" : "player")}->player: " +
                              $"pen={penetration} armor={nominalArmor:F0} eff={effectiveArmor:F0} " +
                              $"angle={obliquity:F0} margin={margin:F2} res={shellResult} dmg={damage}");

        // v13 (баг 8): за ботов декаль синтезирует сервер — у бота нет клиента,
        // который прислал бы PK_CR_ShellDecal. Нормаль — от цели к стрелку (наружу).
        if (attacker.IsBot && fixedDamage == 0 && (shellResult == 0 || shellResult == 1 || shellResult == 4))
        {
            // Локальные координаты: разворачиваем направление «цель -> стрелок» в систему
            // координат цели (минус её курс) и кладём точку на поверхность борта.
            float relRad = (MathF.Atan2(attacker.PosX - target.PosX, attacker.PosZ - target.PosZ)
                            - target.LastYaw * (MathF.PI / 180f));
            float lx = MathF.Sin(relRad), lz = MathF.Cos(relRad);
            // v16: точка ДОЛЖНА лежать на ПОВЕРХНОСТИ детали. Клиент ставит декаль
            // ровно в Hull.TransformPoint(RealPos) без рейкаста, поэтому короткий
            // радиус (1.25–1.55 м) прятал отметину ВНУТРИ корпуса — визуально следов
            // от ботов не было вовсе. Проецируем направление на габаритный
            // прямоугольник детали (метод slab): полудлина 3.4 / полуширина 1.6 у
            // корпуса, 1.7 / 1.4 у башни.
            // v18: точка НЕМНОГО ВНУТРИ брони, а не строго на габарите.
            // Клиент строит декаль проектором (BulletMark.GenerateDecal, DecalMode.MeshFilter)
            // с объёмом всего ~ShellCaliber*0.005 ≈ 0.6-0.7 м. Если точка промахнётся мимо
            // меша хотя бы на полметра — проецировать не на что и следа не будет. Габариты
            // у машин разные, поэтому берём заниженный бокс и ещё 80% от него: точка гарантированно
            // оказывается в теле корпуса, а объём проектора захватывает внешнюю поверхность.
            float halfW = hitHigh ? 1.1f : 1.5f;
            float halfL = hitHigh ? 1.2f : 3.0f;   // заниженные — точка ляжет ближе к броне
            float tX = MathF.Abs(lx) > 0.001f ? halfW / MathF.Abs(lx) : float.MaxValue;
            float tZ = MathF.Abs(lz) > 0.001f ? halfL / MathF.Abs(lz) : float.MaxValue;
            // ── v27: точка должна быть СНАРУЖИ брони, а не внутри ────────────────
            // Разобрал PlayerProxy._createDecal: проектор ставится в присланную точку и
            // разворачивается forward = -normal, то есть смотрит ВНУТРЬ корпуса. Значит
            // точка обязана лежать ПЕРЕД бронёй, снаружи. Я же в v20/v24 уводил её всё
            // глубже внутрь (0.55 габарита, потом фиксированные 0.8 м) — проектор
            // оказывался внутри корпуса и проецировал на внутреннюю геометрию, снаружи
            // не видно ничего. Ровно поэтому следов и не было.
            // Теперь: заниженный габарит (полуширина 1.5, полудлина 3.0) + запас 0.2 м
            // НАРУЖУ. Глубина проекции ~0.6 м, так что реальная броня попадает в объём
            // и у узких, и у широких машин.
            float tHit = MathF.Min(tX, tZ) + 0.2f;
            var atkTank = _gameTables.GetTank(attacker.VehicleId);
            // v46: РАЗБРОС СЛЕДА по детали. Раньше точка всегда была в центре борта на
            // фиксированной высоте (0.25/0.45) → все следы в одной точке. Теперь:
            //  • высота корпуса берётся из реальной точки прицела (ходовая/низ/верх),
            //  • добавляем продольный сдвиг ВДОЛЬ поверхности (перпендикуляр к нормали в
            //    плоскости XZ): для бокового попадания — вдоль длины, для лобового —
            //    вдоль ширины. Random.Shared — потокобезопасен (метод в пуле потоков).
            float decalY = hitHigh ? 0.25f : Math.Clamp(hitY - target.PosY, 0.20f, 1.45f);
            float tangRange = MathF.Abs(lx) > MathF.Abs(lz) ? 2.2f : 0.9f;
            float tang = (Random.Shared.NextSingle() * 2f - 1f) * tangRange;
            float decalX = lx * tHit - lz * tang;
            float decalZ = lz * tHit + lx * tang;
            // v46 (задача: «от пробитий и рикошетов следов не остаётся»). Клиент выбирает
            // текстуру следа по результату: Success→Images[0], Fail→Images[1],
            // JumpBullet→Images[2] (BulletMark.GenerateDecal). На этой сборке видимо
            // рисуется только Images[1] (непробитие); Images[0]/[2] пустые, поэтому следов
            // от пробития и рикошета нет. Пока текстуры в префабе клиента не починены —
            // шлём видимый тип (Fail=1) для ВСЕХ попаданий бота, чтобы след был всегда.
            // Если починишь Images[0]/[2] в клиенте — поставь false для разных текстур.
            // v61 (пункт 2): раньше ВСЕ декали от бота на игроке форсились в тип 1
            // (непробитие) — поэтому на танке игрока не было следов от ПРОБИТИЯ и РИКОШЕТА,
            // только от непробития. Клиент умеет рисовать пробитие (Images[0]) и рикошет
            // (Images[2]) — это видно на танках БОТОВ, где след рисует сам клиент по
            // реальному ShellResult. Ставим настоящий тип: 0=пробитие, 1=непробитие,
            // 4=рикошет. Если пробитие/рикошет окажутся невидимыми — проблема в текстурах
            // префаба клиента (вне сервера), тогда откатим на (byte)1.
            byte decalType = shellResult;
            var botDecal = new PK_RC_ShellDecal
            {
                AttackedPlayerGuid = target.PlayerGUID,
                PartId    = (byte)(hitHigh ? 4 : 1),      // EComponent: Turret=4 / Hull=1
                TypeDecal = decalType,                    // видимая текстура следа
                PosX = decalX, PosY = decalY, PosZ = decalZ,
                NormX = lx, NormY = 0f, NormZ = lz,
                ShellCaliber = 60 + Math.Max(1, atkTank?.Tier ?? attacker.Tier) * 8,
            };
            // v47 (задача: «от ПРОБИТИЙ следов нет, от рикошетов/непробитий есть»).
            // Разобрал клиент: PlayerProxy._createDecal делает след ДОЧЕРНИМ объектом
            // корпуса (gameObject2.parent = hitPoint.transform = Hull). При ПРОБИТИИ (есть
            // урон) клиентский импакт снаряда — он прилетает с задержкой
            // RemoteFireDelayTime ≈ 0.2 c + время полёта — пересобирает/сбрасывает корпус
            // и удаляет ТОЛЬКО ЧТО созданную декаль. При рикошете/непробитии урона нет,
            // корпус не трогается — след остаётся. Поэтому раньше следов от пробития не было.
            // Решение: для пробития шлём декаль С ЗАДЕРЖКОЙ, уже ПОСЛЕ импакта. Каждый
            // выстрел бота и так в своём потоке пула — основной цикл не блокируем.
            // v48.2: раньше для ПРОБИТИЯ (shellResult==0) декаль уходила С ЗАДЕРЖКОЙ 400 мс
            // из отдельного потока пула. Разбор клиента показал две ошибки прежней гипотезы:
            //  (1) корпус на пробитии НЕ пересобирается (подмены модели детали в клиенте нет) —
            //      значит «клиентский импакт удаляет только что созданную декаль» неверно;
            //  (2) клиент и сам рисует след от попадания бота (SyncAITankShellHit →
            //      GenerateAndSynShellDecal по msg.ShellResult), а наш PK_RC_ShellDecal с
            //      TypeDecal=1 кладёт ВИДИМЫЙ след поверх. Отложенная отправка шла из фонового
            //      потока вперемешку с основными записями в сокет (BroadcastToRoom без блокировки)
            //      и терялась/билась — поэтому следа от пробития не было, а от непробития/рикошета
            //      (шлём сразу, из основного пути) был. Теперь шлём СРАЗУ для всех результатов.
            BroadcastToRoom(room, botDecal);
            // ПРИМЕЧАНИЕ: раньше здесь была отложенная до-отправка декали пробития из фонового
            // потока (v52). Она ненадёжно чинила след пробития, зато при отсутствии блокировки
            // сокета корёжила поток и убивала ВСЕ следы. Убрано: шлём только немедленную декаль
            // (видимую для непробития/рикошета). След именно ПРОБИТИЯ клиент всё равно стирает
            // своей реакцией на урон — это уже правится текстурами в префабе клиента, не сервером.
            if (!target.IsBot)
                Console.WriteLine($"[RS] Decal -> player {target.PlayerGUID}: part={(hitHigh ? "Turret" : "Hull")} " +
                                  $"res={shellResult} type={decalType} local=({lx * tHit:F2},{(hitHigh ? 0.20f : 0.35f):F2},{lz * tHit:F2})");
        }

        // v17 (баг 1): локальная точка попадания на ПОВЕРХНОСТИ детали. Клиент кладёт
        // отметину ровно в неё (Hull.TransformPoint), поэтому грубые ±1.5 м по обеим осям
        // били мимо: точка уезжала внутрь корпуса или в угол. Проецируем направление
        // «цель → стрелок» на габаритный прямоугольник детали (корпус 3.4x1.6,
        // башня 1.7x1.4) и подставляем слой/модель, иначе снаряд «долетает в никуда».
        float hitRelRad = MathF.Atan2(attacker.PosX - target.PosX, attacker.PosZ - target.PosZ)
                          - target.LastYaw * (MathF.PI / 180f);
        float hLx = MathF.Sin(hitRelRad), hLz = MathF.Cos(hitRelRad);
        float hHalfW = hitHigh ? 1.4f : 1.6f, hHalfL = hitHigh ? 1.7f : 3.4f;
        float hT = MathF.Min(MathF.Abs(hLx) > 0.001f ? hHalfW / MathF.Abs(hLx) : 99f,
                             MathF.Abs(hLz) > 0.001f ? hHalfL / MathF.Abs(hLz) : 99f);
        byte  hLayerOut = attacker.IsBot && fixedDamage == 0 ? (byte)12 : hitLayer;   // 12 = TankBox
        byte  hModelOut = attacker.IsBot && fixedDamage == 0 ? (byte)(hitHigh ? 4 : 1) : hitModel;

        BroadcastToRoom(room, new PK_RC_ShellHit
        {
            ShellID     = (short)shellID, ShellIndex = (short)shellIndex,
            AttackerGUID = attacker.PlayerGUID, TargetGUID = target.PlayerGUID,
            HitPosX = hitX, HitPosY = hitY, HitPosZ = hitZ,
            LocalPosX = hLx * hT,
            LocalPosY = hitHigh ? 0.30f : 0.55f,
            LocalPosZ = hLz * hT,
            NormalX = normX, NormalY = normY, NormalZ = normZ,
            HitLayer = hLayerOut, HitModel = hModelOut,
            // ShellResult drives the decal type on the client:
            // 0=Success(pen mark), 1=Fail(non-pen mark), 4=JumpBullet(ricochet spark)
            ShellResult = shellResult,
        });
        target.Session.Send(new PK_RC_BeHited
        {
            AttackerGUID = attacker.PlayerGUID, HitType = shellResult, ShellId = (byte)shellID,
        });
        BroadcastToRoom(room, new PK_RC_SynHP
        {
            AttackerGUID = attacker.PlayerGUID, PlayerGUID = target.PlayerGUID,
            CurHp = target.CurHp, Reason = 0,
        });

        // ── v12: подсказки о модулях — крупные надписи по центру ────────────────
        // Send at most ONE PK_RC_ShootTip per shot AFTER PK_RC_SynHP so NamePanelControl.CurDamage
        // already holds this shot's damage and only a single floating damage number is spawned.
        if (moduleVictimTips.Length > 0 && !target.IsBot)
            try { target.Session.Send(new PK_RC_BeShootedTip
                  { AttackerGuid = attacker.PlayerGUID, BeShootTips = moduleVictimTips }); } catch { }
        byte shooterTip = moduleShooterTip != 0 ? moduleShooterTip : (trackDamaged ? (byte)5 : (byte)0);
        if (shooterTip != 0 && !attacker.IsBot)
            try { attacker.Session.Send(new PK_RC_ShootTip
                  { PlayerGuid = target.PlayerGUID, ShootFlag = 0, Damage = damage, ShootTip = shooterTip }); } catch { }

        if (target.CurHp <= 0 && !target.IsDead)
        {
            target.IsDead  = true;
            target.DeathUtc = DateTime.UtcNow;   // v30 (баг 3): отсчёт до снятия корпуса
            target.DeathCount++;
            attacker.KillCount++;

            // SP economy: flat +500 per kill (+ PveSpBonusMult from Defense/Honor Flags)
            int killSp = (int)MathF.Round(500 * attacker.PveSpBonusMult);
            attacker.Sp += killSp;
            attacker.SpEarned += killSp;  // v12: статистика
            // Defense mode: count attacker kills for wave quota
            if (room.GameMode == BattleGameMode.Defense && target.IsBot && target.Group != attacker.Group)
                room.WaveKillCount++;  // enemy wave bot killed

            BroadcastToRoom(room, new PK_RC_TankDead
            {
                KillerGUID = attacker.PlayerGUID, PlayerGUID = target.PlayerGUID,
            });

            // ── scoring ──────────────────────────────────────────────────────
            const int killPoints = 100;
            attacker.Score += 500 + damage;

            if (!attacker.IsBot)
            {
                attacker.Session.Send(new PK_RC_PVE_Cost
                {
                    CurScore = attacker.Score,
                    CurSkillCoin = attacker.Sp,
                    CurFreeSkillOfBomber = attacker.FreeBomberLeft,
                    CurFreeSkillOfKatyusha = attacker.FreeKatyushaLeft,
                });
            }

            // Team score only in Respawn mode; other modes track kills for win condition.
            if (room.GameMode == BattleGameMode.Respawn)
            {
                if (attacker.Group == 0) room.Team1Score += killPoints;
                else                     room.Team2Score += killPoints;
            }

            // Update top-screen kill-count bars (Наши / Противник).
            int t1kills = room.Players.Values.Where(p => p.Group == 0 && !p.IsBot).Sum(p => p.KillCount)
                        + room.Players.Values.Where(p => p.Group == 0 && p.IsBot).Sum(p => p.KillCount);
            int t2kills = room.Players.Values.Where(p => p.Group == 1 && !p.IsBot).Sum(p => p.KillCount)
                        + room.Players.Values.Where(p => p.Group == 1 && p.IsBot).Sum(p => p.KillCount);
            BroadcastToRoom(room, new PK_RC_TabInfo
            {
                GoodDestroyCount = t1kills,
                BadDestroyCount  = t2kills,
                // v13 (баг 4): по-игроковые фраги/урон для колонок таба.
                Players = room.Players.Values
                    .Select(pp => (pp.PlayerGUID, pp.KillCount,
                                   (ushort)Math.Min(ushort.MaxValue, pp.TotalDamage)))
                    .ToArray(),
            });
            if (room.GameMode == BattleGameMode.Respawn)
            {
                BroadcastToRoom(room, new PK_RC_Score { Score = attacker.Group == 0 ? room.Team1Score : room.Team2Score });
            }
            // v48.9: шлём очки ВСЕХ игроков, а не только убившего. Раньше слали один entry
            // (killer) — клиент перестраивал таблицу по этому пакету, поэтому у остальных (и
            // часто у самого игрока) очки обнулялись, и игрок «прыгал» в самый низ списка.
            BroadcastToRoom(room, new PK_RC_PlayerScore
            {
                Players = room.Players.Values
                    .Select(pp => new PlayerScoreEntry { PlayerGUID = pp.PlayerGUID, Score = pp.Score })
                    .ToArray()
            });

            // In Respawn mode the top "Наши / Противник" progress bars are driven by
            // PK_RC_ReviveScore (handled by ReliveModeOccupyValueManager), NOT by
            // PK_RC_TabInfo. Send it so the bars actually fill as teams score.
            if (room.GameMode == BattleGameMode.Respawn)
                BroadcastReviveScore(room);

            // ── win check: Respawn mode point goal ────────────────────────────
            if (room.GameMode == BattleGameMode.Respawn)
            {
                if (room.Team1Score >= BattleRoom.WinScore)
                { EndBattle(room, 0); return; }
                if (room.Team2Score >= BattleRoom.WinScore)
                { EndBattle(room, 1); return; }
            }

            // ── team-wipe check (Respawn / BaseCapture / Simplified only) ─────
            // NOT in Defense (clearing a wave would wrongly end the match — the wave
            // system decides win/lose there) and NOT in Commander (the win is the
            // enemy general's death).
            if (room.GameMode is not BattleGameMode.Defense and not BattleGameMode.Commander)
            {
                bool team0AnyAlive = room.Players.Values.Any(p => p.Group == 0 && !p.IsDead);
                bool team1AnyAlive = room.Players.Values.Any(p => p.Group == 1 && !p.IsDead);
                if (!team1AnyAlive && team0AnyAlive && !room.BattleEnded) { EndBattle(room, 0); return; }
                if (!team0AnyAlive && team1AnyAlive && !room.BattleEnded) { EndBattle(room, 1); return; }
            }

            // ── respawn (Respawn + Defense modes) ────────────────────────────
            //
            // PLAYER respawns in Respawn AND Defense.
            // BOTS respawn only in Respawn mode — this keeps kills accumulating so
            // the score bars fill to 1750 and the match actually ends.
            // Defense bot-waves are managed by StartPveWaves instead.
            // Respawn / Generals: everyone respawns. In Generals the NON-general
            // tanks respawn (killing them feeds SP) but the general's death is final.
            // In Defense only the human respawns here — the wave system handles the
            // attacker bots. Regular players are revived; bots must be re-CREATED,
            // because PK_RC_Revive on a non-main tank makes this client hide it, while
            // PK_RC_PVE_CreateMonster respawns it visibly (same call the waves use).
            // ── respawns ──────────────────────────────────────────────────────
            // Human player: revived in place in Respawn / Defense / Generals (never the
            // general). Bots: in Respawn and Generals a fresh replacement bot spawns
            // (new GUID, same tank) — the client won't cleanly revive an existing bot,
            // but renders a brand-new one perfectly (the same trick the waves use). The
            // general never respawns.
            if (!target.IsCommander)
            {
                bool humanRespawns = !target.IsBot &&
                    room.GameMode is BattleGameMode.Respawn or BattleGameMode.Defense or BattleGameMode.Commander;
                // v14: в «Генералах» возрождаются ВСЕ, кроме самих генералов (гейт
                // !target.IsCommander стоит выше), в «Возрождении» — все.
                // v48.8: в ОБОРОНЕ возрождаем СОЮЗНЫХ ботов (защитников — группа игрока), но НЕ
                // атакующих (их даёт волновой спавн StartPveWaves). Группу игрока берём по человеку.
                byte defPlayerGroup = room.Players.Values.FirstOrDefault(p => !p.IsBot)?.Group ?? (byte)0;
                bool botRespawns = target.IsBot &&
                    (room.GameMode is BattleGameMode.Respawn or BattleGameMode.Commander
                     || (room.GameMode == BattleGameMode.Defense && target.Group == defPlayerGroup));

                if (humanRespawns)
                {
                    const int respawnDelay = 5;
                    target.Session.Send(new PK_RC_ReviveTime { ReviveTimeLeft = respawnDelay });
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(respawnDelay * 1000);
                        if (!target.IsDead || room.BattleEnded) return;
                        target.IsDead          = false;
                        target.DeathUtc        = default;   // v30 (баг 3)
                        target.ReviveCount++;                     // v12: статистика возрождений
                        target.CurHp           = target.MaxHp;
                        (target.PosX, target.PosZ) = PickSpawnSpot(room, target.Group); // v13
                        target.PosY            = SampleHeight(room, target.PosX, target.PosZ);
                        target.RotY            = GetSpawnYaw(room.MapId, target.Group);
                        target.CurrentSpeedMps = 0f;
                        // v12: свежий танк — целые гусеницы и модули, пожар потушен,
                        // засвет сброшен (заново «скрыт» на точке возрождения).
                        target.ChassisLDamaged = false;
                        target.ChassisRDamaged = false;
                        target.AmmoRackState   = 0; target.AmmoRackHp = target.AmmoRackMaxHp;
                        target.FuelTankState   = 0; target.FuelTankHp = target.FuelTankMaxHp;
                        target.BurnUntil       = DateTime.MinValue;
                        target.SpottedByEnemy  = false;
                        target.EnemySpotHoldUntil = DateTime.MinValue;
                        foreach (var otherHuman in room.Players.Values.Where(x => !x.IsBot && x.PlayerGUID != target.PlayerGUID))
                        {
                            lock (otherHuman.SpotLock)
                            {
                                otherHuman.SpottedEnemies.Remove(target.PlayerGUID);
                                otherHuman.SpotHoldUntil.Remove(target.PlayerGUID);
                            }
                        }
                        BroadcastToRoom(room, new PK_RC_WidgetState
                        {
                            PlayerGUID = target.PlayerGUID, AttackPlayerGuid = 0,
                            Widgets = new (byte, byte, byte)[]
                                { (2, 0, 0), (3, 0, 0), (8, 0, 0), (9, 0, 0) },
                        });
                        var respawnGun = _gameTables.GetGun(target.MainGunID);
                        target.ShellCount = Math.Max(1, respawnGun?.AmmoLoad ?? 20);
                        // v68 (п.1): при возрождении тоже восполняем по-типовый счётчик.
                        if (target.ShellAmmoMaxById.Count > 0)
                            foreach (var kv in target.ShellAmmoMaxById) target.ShellAmmoById[kv.Key] = kv.Value;
                        BroadcastToRoom(room, new PK_RC_Revive
                        {
                            NoDamageTime = 3,
                            Players = new[] { new RevivePlayerEntry
                            {
                                PlayerGUID = target.PlayerGUID,
                                PosX = target.PosX, PosY = target.PosY, PosZ = target.PosZ,
                                RotX = 0, RotY = target.RotY, RotZ = 0,
                                CurShellID = (short)target.CurrentShellId,
                            }},
                        });
                        BroadcastToRoom(room, new PK_RC_SynHP
                        {
                            AttackerGUID = 0, PlayerGUID = target.PlayerGUID,
                            CurHp = target.CurHp, Reason = 0,
                        });
                    });
                }
                else if (botRespawns)
                {
                    var deadBot = target;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Thread.Sleep(5000);
                        if (room.BattleEnded) return;
                        // Drop the dead bot and spawn a brand-new one with the same tank.
                        // ── v17 (баг 2): бот возрождается ТЕМ ЖЕ объектом ──────────
                        // Раньше создавался НОВЫЙ бот с новым GUID (PK_RC_NewPlayerLogin).
                        // Клиент его рисовал, но у свежесозданного прокси не был запущен
                        // TankStateSync (Run=false до OnTankCreated), поэтому танк стоял
                        // столбом: MoveDictate и выстрелы к нему не применялись.
                        // Теперь используем ШТАТНЫЙ путь возрождения — тот же, что у
                        // живого игрока: PK_RC_Revive по СУЩЕСТВУЮЩЕМУ GUID. Объект на
                        // клиенте уже полностью инициализирован, поэтому бот сразу едет
                        // и стреляет, а труп исчезает сам (клиент делает OnRelive).
                        deadBot.IsDead          = false;
                        deadBot.DeathUtc        = default;   // v30 (баг 3)
                        deadBot.CurHp           = deadBot.MaxHp;
                        deadBot.ChassisLDamaged = false;
                        deadBot.ChassisRDamaged = false;
                        deadBot.AmmoRackState   = 0; deadBot.AmmoRackHp = deadBot.AmmoRackMaxHp;
                        deadBot.FuelTankState   = 0; deadBot.FuelTankHp = deadBot.FuelTankMaxHp;
                        deadBot.BurnUntil       = DateTime.MinValue;
                        deadBot.CurrentSpeedMps = 0f;
                        deadBot.NavPath         = null;
                        deadBot.WaypointX       = 0f;
                        deadBot.WaypointZ       = 0f;
                        deadBot.TiltInit        = false;
                        deadBot.SteerSideBias   = 0;
                        var (rsx, rsz) = PickSpawnSpot(room, deadBot.Group);
                        deadBot.PosX = rsx; deadBot.PosZ = rsz;
                        deadBot.PosY = SampleHeight(room, rsx, rsz);
                        deadBot.LastYaw = GetSpawnYaw(room.MapId, deadBot.Group);
                        var rgun = _gameTables.GetGun(deadBot.MainGunID);
                        deadBot.ShellCount = Math.Max(1, rgun?.AmmoLoad ?? 20);

                        BroadcastToRoom(room, new PK_RC_Revive
                        {
                            NoDamageTime = 3,
                            Players = new[] { new RevivePlayerEntry
                            {
                                PlayerGUID = deadBot.PlayerGUID,
                                PosX = deadBot.PosX, PosY = deadBot.PosY, PosZ = deadBot.PosZ,
                                RotX = 0, RotY = deadBot.LastYaw, RotZ = 0,
                                CurShellID = (short)deadBot.CurrentShellId,
                            }},
                        });
                        BroadcastToRoom(room, new PK_RC_WidgetState
                        {
                            PlayerGUID = deadBot.PlayerGUID, AttackPlayerGuid = 0,
                            Widgets = new (byte, byte, byte)[]
                                { (2, 0, 0), (3, 0, 0), (8, 0, 0), (9, 0, 0) },
                        });
                        BroadcastToRoom(room, new PK_RC_SynHP
                        {
                            AttackerGUID = 0, PlayerGUID = deadBot.PlayerGUID,
                            CurHp = deadBot.CurHp, Reason = 0,
                        });
                        // ── v18 (баг 3): вернуть возрождённого бота в ВИДИМОСТЬ ──────
                        // Клиентский PlayerProxy.OnRelive для НЕ-главного танка намеренно
                        // гасит объект: `IsShow = false; IsVisible = false;` — и ждёт нового
                        // засвета. Пока бот числился в SpottedEnemies с прошлой жизни,
                        // fog-of-war считал его «уже засвеченным» и повторный PK_RC_BeLighted
                        // не слал — бот оставался живым, но НЕВИДИМЫМ.
                        // Поэтому: чистим память о засвете и сразу подсвечиваем заново —
                        // союзников безусловно, врагов только тем, кто их реально видит.
                        foreach (var human in room.Players.Values.Where(x => !x.IsBot && x.IsLoaded))
                        {
                            lock (human.SpotLock)
                            {
                                human.SpottedEnemies.Remove(deadBot.PlayerGUID);
                                human.SpotHoldUntil.Remove(deadBot.PlayerGUID);
                                bool ally    = human.Group == deadBot.Group;
                                bool visible = ally || IsSpottedBy(deadBot, human.Group, room);
                                if (!visible) continue;
                                try
                                {
                                    // v19: ВИДИМОСТЬ возвращает только ViewState=2. BeLighted (197)
                                    // в обычных режимах ничего не показывает — его обработчик пуст
                                    // (переопределён лишь в CommanderRoom, и то только для UI).
                                    human.Session?.Send(new PK_RC_ViewState
                                    {
                                        Players = new[] { (deadBot.PlayerGUID, (byte)2) },
                                    });
                                    human.Session?.Send(new PK_RC_BeLighted
                                    {
                                        PlayerGuids = new[] { deadBot.PlayerGUID },
                                    });
                                    human.Session?.Send(BuildSnapshotMovePacket(deadBot));
                                    if (!ally)
                                    {
                                        human.SpottedEnemies.Add(deadBot.PlayerGUID);
                                        human.SpotHoldUntil[deadBot.PlayerGUID] = DateTime.UtcNow.AddSeconds(SpotHoldSec);
                                    }
                                }
                                catch { }
                            }
                        }

                        Console.WriteLine($"[RS] Bot revive (same guid): {deadBot.PlayerGUID} " +
                                          $"TankID={deadBot.TankID} at ({rsx:F0},{rsz:F0})");
                    });
                }
            }
        }
    }

    // v30 (баг 2): единственная точка, откуда вообще уходит PK_RC_TimeCountDown.
    //
    // Пакет пишется ОДНИМ БАЙТОМ, поэтому любое значение > 255 приезжает на клиент
    // как 255 и рисуется на табло как «25» (TimeCountDown.DisassembleNumber берёт
    // первые два разряда числа). Плюс каждый такой пакет — это Begin() на клиенте,
    // то есть пересоздание зацикленного звука отсчёта. Отсюда правило:
    //   • шлём ТОЛЬКО пока идёт предбоевая подготовка;
    //   • шлём РОВНО ОДИН раз на игрока (дальше клиент считает сам);
    //   • значение всегда влезает в байт (подготовка ≤ 60 с).
    private static void SendPrepCountdown(GameSession? session, BattleRoom room)
    {
        if (session == null || room.BattleEnded) return;
        int left = (int)Math.Ceiling((room.CombatStartUtc - DateTime.UtcNow).TotalSeconds);
        if (left <= 0 || left > 255) return;   // бой уже идёт или комната ещё не стартовала
        try { session.Send(new PK_RC_TimeCountDown { TimeLeft = left }); } catch { }
    }

    // ─────────────────────────── timer / end ───────────────────────────────────
    private void GameTimerTick(object? state)
    {
        if (state is not BattleRoom room) return;
        // NO battle-ending timer: the clock is frozen at a large constant and never
        // decremented, so nothing can end the battle by time. Battles end ONLY on a
        // win/lose condition (defense: all 12 waves cleared, or player lives = 0).
        //
        // v30 (баг 2): ЗДЕСЬ БОЛЬШЕ НИЧЕГО НЕ ШЛЁМ.
        // PK_RC_TimeCountDown пишется на провод ОДНИМ БАЙТОМ (см. Protocol/Messages.cs),
        // поэтому BattleTimeLeft = 359999 приезжал на клиент как 255. Клиентский
        // TimeCountDown.Begin(255) при каждом таком пакете:
        //   • сбрасывал _beginTime/_keepTime → табло показывало разряды «2» и «5»,
        //     то есть ту самую «25», в которую скакал отсчёт;
        //   • DestroyImmediate + PlayGlobalAudioLoop(60) → звук тиканья пересоздавался
        //     каждую секунду и реально начинал звучать уже ПОСЛЕ отсчёта (в момент,
        //     когда панель включалась снова — например, на экране конца боя);
        //   • оставлял _timeCountFlag = true навсегда.
        // Отсчёт клиент ведёт сам от Time.time, ему достаточно ОДНОГО пакета в начале.

        // v12: секундное обслуживание модулей — пожар от бака, автопочинка и истечение дымов.
        if (!room.BattleEnded)
        {
            try { CheckSmokeClouds(room); } catch { }
            foreach (var p in room.Players.Values)
            {
                try { ProcessBurnTick(room, p); } catch { }
                try { CheckModuleRepair(room, p); } catch { }
            }
        }

        // v13 (баг 4): колонка «УРОН» в табе обновляется только из PK_RC_TabInfo —
        // шлём срез фрагов/урона раз в 3 секунды (не только на фрагах).
        if (!room.BattleEnded && ++room.TabSyncTick >= 3)
        {
            room.TabSyncTick = 0;
            try
            {
                int tg0 = room.Players.Values.Where(x => x.Group == 0).Sum(x => x.KillCount);
                int tg1 = room.Players.Values.Where(x => x.Group == 1).Sum(x => x.KillCount);
                BroadcastToRoom(room, new PK_RC_TabInfo
                {
                    GoodDestroyCount = tg0, BadDestroyCount = tg1,
                    Players = room.Players.Values
                        .Select(pp => (pp.PlayerGUID, pp.KillCount,
                                       (ushort)Math.Min(ushort.MaxValue, pp.TotalDamage)))
                        .ToArray(),
                });
                BroadcastToRoom(room, new PK_RC_PlayerScore
                {
                    Players = room.Players.Values
                        .Select(pp => new PlayerScoreEntry { PlayerGUID = pp.PlayerGUID, Score = pp.Score })
                        .ToArray(),
                });
            }
            catch { }
        }
    }

    private void EndBattle(BattleRoom room, byte? explicitWinnerGroup = null)
    {
        if (room.BattleEnded) return;
        room.BattleEnded = true;
        BattleModeRegistry.MarkEnded(room.RoomGUID);
        room.Timer?.Dispose();

        // Persist the terrain learned this battle so the next battle on this map
        // starts denser (see LoadHeightGrid/SaveHeightGrid).
        SaveHeightGrid(room);

        Console.WriteLine($"[RS] Battle ended. Room={room.RoomGUID} Mode={room.GameMode} " +
                          $"Score:{room.Team1Score}-{room.Team2Score} Winner={explicitWinnerGroup}");

        byte result;
        if (explicitWinnerGroup.HasValue)
        {
            result = explicitWinnerGroup.Value;
        }
        else
        {
            // Time expired: highest score wins (Respawn), or check survivors (others).
            result = room.GameMode == BattleGameMode.Respawn
                ? (room.Team1Score > room.Team2Score ? (byte)0
                   : room.Team2Score > room.Team1Score ? (byte)1 : (byte)2)
                : CheckSurvivorWinner(room);
        }

        BroadcastToRoom(room, new PK_RC_FightEnd { ResultType = result });

        foreach (var p in room.Players.Values)
        {
            if (p.IsBot) continue;
            bool isWin  = result != 2 && p.Group == result;
            var  tank   = _gameTables.GetTank(p.VehicleId);
            int  tier   = Math.Max(1, tank?.Tier ?? p.Tier);

            // ── v12: экономика, калиброванная по референсам оригинала ────────────
            // (36.4k урона / 18 фрагов -> ~74k серебра и ~3.5k опыта при победе).
            int gross = (int)(300 + p.TotalDamage * 1.5f + p.KillCount * 400 + p.LightDamage * 0.30f);
            int exp   = (int)(50  + p.TotalDamage * 0.06f + p.KillCount * 60  + p.LightDamage * 0.02f);
            if (isWin) { gross = (int)(gross * 1.3f); exp = (int)(exp * 1.3f); }
            // v61 (пункт 6): пер-танковый коэффициент фарма (серебро/опыт). По умолчанию 1.0;
            // задаётся в GameTables/Data/vehicle_economy.json (напр. премы — больше).
            var econ = _gameTables.GetVehicleEconomyMult(p.VehicleId);
            gross = (int)(gross * econ.Silver);
            exp   = (int)(exp   * econ.Exp);

            // Расходы: ремонт от потерянной прочности (масштаб от тира — на топах
            // ремонт ощутимо кусается, как в оригинале), снаряды по цене из таблицы.
            int lostShown  = Math.Min(p.DamageTaken, p.MaxHp);
            int repairCost = (int)(p.DamageTaken * tier * 0.55f);
            var shellEntry = _gameTables.GetShell(p.CurrentShellId > 0 ? p.CurrentShellId : 1);
            int shellPrice = Math.Max(10, shellEntry?.Price ?? tier * 40);
            int shellCost  = p.ShootCount * shellPrice;

            // Расходники: списание использованных слотов и автопополнение (флаг AutoRepair & 4)
            int supplyCost = 0;
            if (p.TankGUID > 0 && p.ProvisionUsed.Any(u => u))
            {
                try
                {
                    var dbTank = _tankRepo.GetTankByGUID(p.TankGUID);
                    var acc = _accountRepo?.GetByPlayerGUID(p.PlayerGUID);
                    bool autoProv = dbTank != null && (dbTank.AutoRepair & 4) != 0;
                    int[] updatedProv = (int[])p.Provisions.Clone();
                    var invRepo = new InventoryRepository(_db);
                    int availSilverAfter = (acc?.Silver ?? 0) + Math.Max(0, gross - repairCost - shellCost);

                    for (int si = 0; si < 3; si++)
                    {
                        if (!p.ProvisionUsed[si] || updatedProv[si] <= 0) continue;
                        int pid = updatedProv[si];
                        int stock = invRepo.GetItemCount(p.PlayerGUID, 103, pid);
                        if (stock > 0 && invRepo.RemoveItem(p.PlayerGUID, 103, pid, 1))
                        {
                            // Replenished from depot stock -> stays equipped at 0 silver cost
                            continue;
                        }
                        if (autoProv)
                        {
                            var con = _gameTables.GetConsumable(pid);
                            int silverCost = con == null ? 0
                                : (con.Currency == 2 && con.SilverToGold > 0 ? con.SilverToGold
                                   : (con.Currency == 1 ? con.Price : 0));
                            if (silverCost > 0 && availSilverAfter >= supplyCost + silverCost)
                            {
                                supplyCost += silverCost;
                                continue;
                            }
                            if (con?.Currency == 2 && acc != null && acc.Gold >= con.Price)
                            {
                                _accountRepo?.UpdateResources(p.PlayerGUID, gold: acc.Gold - con.Price);
                                acc.Gold -= con.Price;
                                continue;
                            }
                        }
                        // No stock & not auto-resupplied -> slot becomes empty in hangar
                        updatedProv[si] = 0;
                    }

                    string newProvStr = string.Join(";", updatedProv);
                    using var pCmd = _db.Connection.CreateCommand();
                    pCmd.CommandText = "UPDATE Tanks SET Provisions = @prov WHERE TankGUID = @guid";
                    pCmd.Parameters.AddWithValue("@prov", newProvStr);
                    pCmd.Parameters.AddWithValue("@guid", p.TankGUID);
                    pCmd.ExecuteNonQuery();
                }
                catch (Exception ex) { Console.WriteLine($"[RS] Provision post-battle sync error: {ex.Message}"); }
            }

            int netSilver = gross - repairCost - shellCost - supplyCost;

            // Очки лояльности легиона: за урон и фраги, x1.5 за победу.
            int loyal = (int)((100 + p.TotalDamage / 100f + p.KillCount * 25) * (isWin ? 1.5f : 1f));

            using var cmd = _db.Connection.CreateCommand();
            cmd.CommandText = "UPDATE Accounts SET Silver = MAX(0, Silver + @silver), Experience = Experience + @exp WHERE PlayerGUID = @guid";
            cmd.Parameters.AddWithValue("@silver", netSilver);
            cmd.Parameters.AddWithValue("@exp",    exp);
            cmd.Parameters.AddWithValue("@guid",   p.PlayerGUID);
            cmd.ExecuteNonQuery();

            if (p.TankGUID > 0)
            {
                _battleStatsRepo.RecordBattle(p.PlayerGUID, p.TankGUID,
                    p.KillCount, p.DeathCount, p.Score,
                    p.TotalDamage, p.ShootCount, p.HitCount, isWin, exp);
                _tankRepo.UpdateTankHP(p.TankGUID, Math.Max(0, p.CurHp));
            }

            // Пер-режимная статистика (Respawn/Commander/Defense) — отдельно от
            // пер-танковой выше, нужна для вкладок «Возрождение»/«Командир»/«Оборона»
            // в профиле игрока (см. HallServer.HandleHallLogin, AccountStatisticRevive/
            // Commander/PVE). Очки боя для PVE — число отражённых волн (см. pkg.PveInfo[0]).
            int modeScore = room.GameMode == BattleGameMode.Defense ? room.CurrentWave : room.Team1Score + room.Team2Score;
            _modeStatsRepo?.RecordBattle(p.PlayerGUID, (int)room.GameMode,
                p.KillCount, p.DeathCount, p.TotalDamage, p.ShootCount, p.HitCount, isWin, exp, modeScore);

            // ── пакет для окна «Статистика боя» (Hall дошлёт при возврате в ангар) ──
            var pkg = new BattleEndResult
            {
                RoomGuid   = room.RoomGUID,
                MapId      = room.MapId,
                ModeWire   = (byte)room.GameMode,
                Result     = (byte)(result == 2 ? 2 : (isWin ? 1 : 0)),
                LiveState  = (byte)(p.IsDead ? 0 : 1),
                StartUnix  = room.StartedUnix > 0 ? room.StartedUnix : (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Money      = gross,
                Exp        = exp,
                VipMoney   = (int)(gross * 1.5f),
                VipExp     = (int)(exp * 1.5f),
                TankId     = p.VehicleId,
                ShotCount  = (ushort)Math.Min(ushort.MaxValue, p.ShootCount),
                HitCount   = (ushort)Math.Min(ushort.MaxValue, p.HitCount),
                PierceCount= (ushort)Math.Min(ushort.MaxValue, p.PenCount),
                Damage     = p.TotalDamage,
                Kills      = (byte)Math.Min(255, p.KillCount),
                DamagedEnemies = (byte)Math.Min(255, p.DamagedEnemyGuids.Count),
                OccupyValue= (ushort)Math.Min(ushort.MaxValue, p.OccupyValue),
                BeHitCount = (ushort)Math.Min(ushort.MaxValue, p.BeHitCount),
                LostHp     = lostShown,
                MaxHp      = p.MaxHp,
                RepairCost = repairCost,
                SupplyCost = supplyCost,
                ShellCost  = shellCost,
                LightDamage= p.LightDamage,
                LightCount = (byte)Math.Min(255, p.LightCount),
                ChassisShareDamage = p.ChassisShareDmg,
                PotentialDamage    = 0,
                GroupReward= 1f,
                LoyalPoint = loyal,
                CrewExtraExp = tier * 120,
            };

            // v16 (баг 3): вкладка «Результат команды» — ВСЕ участники боя, включая
            // ботов. Side: 0 = сторона получателя пакета, 1 = противник.
            foreach (var q in room.Players.Values)
            {
                var qTank = _gameTables.GetTank(q.VehicleId);
                int qExp  = (int)(50 + q.TotalDamage * 0.06f + q.KillCount * 60);
                int qSil  = (int)(300 + q.TotalDamage * 1.5f + q.KillCount * 400);
                pkg.TeamRows.Add(new TeamResultRow
                {
                    Guid       = q.PlayerGUID,
                    PlayerName = q.IsBot ? $"Bot_{-q.PlayerGUID}" : (q.Session?.PlayerName ?? "Player"),
                    CorpsName  = "",
                    Side       = (byte)(q.Group == p.Group ? 0 : 1),
                    TankId     = q.VehicleId,
                    Hurt       = q.TotalDamage,
                    Kill       = q.KillCount,
                    BaseExp    = qExp,
                    SilverCoin = qSil,
                    Title      = qTank?.Tier ?? q.Tier,
                    TeamGUID   = 0,
                    ExpMultiWithVip = 1.5f,
                    SilverWithVip   = (int)(qSil * 1.5f),
                });
            }

            // Список «повреждено врагов» для вкладки «Подробные данные».
            foreach (var vg in p.DamagedEnemyGuids)
            {
                if (!room.Players.TryGetValue(vg, out var victim)) continue;
                bool wasSpotted;
                lock (p.SpotLock) wasSpotted = p.SpottedEnemies.Contains(vg);
                pkg.Victims.Add((victim.IsBot ? $"Bot_{-victim.PlayerGUID}" : (victim.Session?.PlayerName ?? "Player"),
                                 victim.VehicleId, 1, 0,
                                 (byte)(victim.IsDead ? 1 : 0), Math.Min(65535, p.TotalDamage / Math.Max(1, p.DamagedEnemyGuids.Count)),
                                 (byte)(wasSpotted ? 1 : 0), 0, 0, 0));
            }
            // PVE-массив (оборона) — раскладка по клиентскому enum PVEEndInfo.
            pkg.PveInfo[0]  = p.KillCount; // ERT_PVE_StopKillCount: остановлено врагов
            pkg.PveInfo[1]  = p.DeathCount;
            pkg.PveInfo[2]  = p.SpEarned;
            pkg.PveInfo[3]  = p.SpSpent;
            pkg.PveInfo[4]  = p.SkillUseCount[4];  // самолёт-разведчик
            pkg.PveInfo[5]  = p.SkillUseCount[5];  // катюши
            pkg.PveInfo[6]  = p.SkillUseCount[6];  // бомбардировщик
            pkg.PveInfo[7]  = p.SkillUseCount[7];  // генератор радиопомех
            pkg.PveInfo[8]  = p.SkillUseCount[1];  // починки танка
            pkg.PveInfo[9]  = p.SkillUseCount[2];  // пополнения снарядов
            pkg.PveInfo[10] = p.SkillUseCount[3];  // энергетики
            pkg.PveInfo[11] = p.ReviveCount;
            pkg.PveInfo[12] = shellCost;
            pkg.PveInfo[13] = 0;                   // расходники
            pkg.PveInfo[14] = room.Players.Values.Count(x => !x.IsBot) > 1 ? 1 : 0; // командой?
            pkg.PveInfo[15] = room.GameMode == BattleGameMode.Defense ? room.CurrentWave : 0;
            pkg.PveInfo[16] = 0;                   // золото на поддержку
            pkg.PveInfo[17] = 0;                   // золото на возрождения
            // v13: окно «Статистика боя» доставляем НЕМЕДЛЕННО через холл-сессию, пока
            // клиент ещё в сцене Room — только в этом состоянии он ставит
            // CurShowPrizeData и показывает окно при загрузке ангара.
            var hall = HallServer.GetHallSession(p.PlayerGUID);
            if (hall != null)
            {
                try
                {
                    // v16: сначала таблица команды (id 231), затем окно итогов (434/442) —
                    // клиент кладёт строки в GroupStatisticsDataManager по RoomGUID,
                    // и вкладка «Результат команды» открывается уже заполненной.
                    var teamMsg = HallServer.BuildTeamResultMessage(pkg);
                    if (teamMsg != null) hall.Send(teamMsg);
                    hall.Send(HallServer.BuildBattleEndMessage(pkg));
                    pkg.SentToClient = true;
                    Console.WriteLine($"[RS] Battle-end window pushed via hall to {p.PlayerGUID}");

                    // v: обновляем и статистику (общая + пер-режимная + карточка
                    // AccountFightStatistics) сразу после боя, через тот же холл-сокет.
                    // Раньше эти пакеты слались только один раз при входе в Ангар,
                    // и после боя (без переподключения) статистика не обновлялась.
                    if (_accountRepo != null && _modeStatsRepo != null)
                    {
                        // v: justPlayedVehicleId — иначе PushAccountStatistics шлёт
                        // полный снимок (ItemCount = кол-во ВСЕХ станков с историей
                        // боёв), а клиентский AccountProfilesManager после первого
                        // пакета за сессию принимает ТОЛЬКО ItemCount==1 доп. запись
                        // (self + один танк). Любое другое значение тихо обнуляет
                        // _apDataNml — и следующая покупка/выдача танка падает в
                        // FillPersonalProfilesWhenUpdateTankInfo() (см. коммент у
                        // PushAccountStatistics в HallServer.cs).
                        HallServer.PushAccountStatistics(hall, p.PlayerGUID, _accountRepo, _battleStatsRepo, _modeStatsRepo, _tankRepo, justPlayedVehicleId: p.VehicleId);
                    }
                }
                catch { }
            }
            BattleEndRegistry.Store(p.PlayerGUID, pkg);

            Console.WriteLine($"[RS] {p.PlayerGUID}: {(isWin ? "WIN" : "LOSE")} " +
                              $"dmg={p.TotalDamage} k={p.KillCount} d={p.DeathCount} pen={p.PenCount} " +
                              $"silver {gross}-{repairCost}-{shellCost}={netSilver} exp={exp} loyal={loyal}");
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(2000);
            BroadcastToRoom(room, new PK_RC_BattleEndTwoSecond());
        });
    }

    private byte CheckSurvivorWinner(BattleRoom room)
    {
        bool t0alive = room.Players.Values.Any(p => p.Group == 0 && !p.IsDead);
        bool t1alive = room.Players.Values.Any(p => p.Group == 1 && !p.IsDead);
        if (!t1alive && t0alive)  return 0;
        if (!t0alive && t1alive)  return 1;
        return 2; // draw
    }

    // ─────────────────────────── misc handlers ─────────────────────────────────
    private void HandlePing(GameSession session, PK_CR_Ping ping)
        => session.Send(new PK_RC_Ping { Marker = ping.Marker });

    private void HandleLoadWarCompleted(GameSession session)
    {
        Console.WriteLine($"[RS] LoadWarCompleted: {session.PlayerGUID}");
        if (_players.TryGetValue(session.PlayerGUID, out var player))
        {
            player.IsLoaded = true;
            if (player.Room != null && player.Room.Players.Values.All(p => p.IsLoaded) && !player.Room.AllLoadedSent)
                player.Room.AllLoadedSent = true;
        }
    }

    private void HandlePlayerLogout(GameSession session)
    {
        if (_players.TryRemove(session.PlayerGUID, out var player) && player.Room != null)
        {
            var room = player.Room;
            bool startNow = false;
            (byte lm, byte wm, BattleGameMode gm) p = default;
            lock (room.StartLock)
            {
                room.PendingHumans.Remove(player);
                room.Players.TryRemove(session.PlayerGUID, out _);
                if (!room.BattleStarted && !room.BattleEnded && room.PendingHumans.Count > 0)
                {
                    room.ExpectedHumanCount = Math.Min(room.ExpectedHumanCount, room.PendingHumans.Count);
                    if (room.DeferredStartParams.HasValue && room.PendingHumans.Count >= room.ExpectedHumanCount)
                    {
                        p = room.DeferredStartParams.Value;
                        startNow = true;
                    }
                }
            }
            bool anyHumansLeft = room.Players.Values.Any(x => !x.IsBot);
            if (!anyHumansLeft && room.BattleStarted && !room.BattleEnded)
                EndBattle(room);
            else if (!anyHumansLeft && !room.BattleStarted)
                BattleModeRegistry.MarkEnded(room.RoomGUID);
            BroadcastToRoom(room, new PK_RC_PlayerLogout { PlayerGUID = session.PlayerGUID });
            if (startNow) StartPendingRoom(room, p.lm, p.wm, p.gm);
        }
        Console.WriteLine($"[RS] Player logout: {session.PlayerGUID}");
    }

    // v13 (баг 8): ретрансляция следа от снаряда. Стрелок сам рисует свою декаль
    // локально, поэтому шлём ОСТАЛЬНЫМ (иначе двойная отметина у стрелка).
    // TypeDecal берём из последнего решения сервера по этому стрелку (пробил/нет/рикошет).
    private void HandleShellDecal(GameSession session, PK_CR_ShellDecal d)
    {
        if (!_players.TryGetValue(session.PlayerGUID, out var shooter) || shooter.Room == null) return;
        var tank = _gameTables.GetTank(shooter.VehicleId);
        // ВАЖНО: именно BroadcastToRoom (а не Others) — клиент-стрелок свою отметину
        // локально НЕ рисует, он ждёт эха от сервера (AmmoInfo: send -> return).
        BroadcastToRoom(shooter.Room, new PK_RC_ShellDecal
        {
            AttackedPlayerGuid = d.AttackedPlayerGuid,
            PartId    = d.PartId,
            TypeDecal = shooter.LastShellResult,
            PosX = d.PosX, PosY = d.PosY, PosZ = d.PosZ,
            NormX = d.NormX, NormY = d.NormY, NormZ = d.NormZ,
            ShellCaliber = 60 + Math.Max(1, tank?.Tier ?? shooter.Tier) * 8,
        });
    }

    private void HandleChat(GameSession session, PK_CR_Chat chat)
        => BroadcastToRoom(session, new PK_RC_Chat
           { PlayerGUID = session.PlayerGUID, ChatType = chat.ChatType, Message = chat.Message });

    private void HandleFastChat(GameSession session, PK_CR_FastChat fc)
        => BroadcastToRoom(session, new PK_RC_FastChat
           { PlayerGUID = session.PlayerGUID, FastChatId = fc.FastChatId });

    private void HandleLittleMap(GameSession session, PK_CR_LittleMapDictated lm)
    {
        if (_players.TryGetValue(session.PlayerGUID, out var player) && player.Room != null)
        {
            var mapCfg = GetMapConfig(player.Room.MapId);
            // Scale world coords (0-800) to byte range (0-255) for the attention-ping packet
            byte gx = (byte)Math.Clamp((int)(lm.PosX / (mapCfg.XMax - mapCfg.XMin) * 255), 0, 255);
            byte gy = (byte)Math.Clamp((int)(lm.PosZ / (mapCfg.ZMax - mapCfg.ZMin) * 255), 0, 255);
            BroadcastToRoomOthers(player.Room, session,
                new PK_RC_LittleMapDictated
                { PlayerGUID = session.PlayerGUID, GuidX = gx, GuidY = gy, Type = 0 });
        }
    }

    // ─────────────────────────── broadcast helpers ─────────────────────────────
    // Bots share the real player's GameSession (no separate socket), so we must
    // dedup on the actual Session reference to avoid sending N copies.
    private void BroadcastToRoom(BattleRoom room, IRequest msg)
    {
        var seen = new HashSet<GameSession>();
        foreach (var p in room.Players.Values)
            if (seen.Add(p.Session)) try { p.Session.Send(msg); } catch { }
    }

    // Fog-of-war-aware broadcast of a bot's movement. An ENEMY client receives the
    // bot's position ONLY while its team has the bot spotted (SpottedEnemies is
    // refreshed by MinimapTick); allies always receive it. This is what keeps unspotted
    // bots hidden in 3D — otherwise every move update re-revealed a hidden tank the
    // instant it started moving (why enemies popped back after the prep timer).
    private void BroadcastBotMove(BattleRoom room, RoomPlayer bot, IRequest msg, long excludeGuid = 0)
    {
        foreach (var p in room.Players.Values)
        {
            if (p.IsBot || p.Session == null || p.PlayerGUID == excludeGuid) continue;
            lock (p.SpotLock)
            {
                bool visible = p.Group == bot.Group || p.SpottedEnemies.Contains(bot.PlayerGUID);
                if (!visible) continue;
                try { p.Session.Send(msg); } catch { /* disconnected */ }
            }
        }
    }

    // Sends the Respawn-mode top-bar update (PK_RC_ReviveScore). The bars are drawn
    // from the receiving player's perspective: GoodScore is the score of the team in
    // the Group field, BadScore is the other team. Since the human player is the only
    // real client (bots share its session), we frame it from the player's group.
    private void BroadcastReviveScore(BattleRoom room)
    {
        // Multiplayer: each human player gets a score packet framed from their own group
        // (GoodScore = your team, BadScore = enemy team).
        // v20 (баг 3): НЕ переворачиваем на сервере! Клиент делает это сам:
        // ReliveModeOccupyValueManager.HandleSyncReviveScore — для стороны Good берёт
        // мои = GoodScore, для стороны Bad мои = BadScore. Мы переворачивали тоже,
        // и у игрока из группы 1 полосы менялись местами (его 4 фрага показывались
        // как 1200, а 12 фрагов врага — как 400).
        // GoodScore — ВСЕГДА счёт группы 0, BadScore — ВСЕГДА счёт группы 1.
        foreach (var human in room.Players.Values.Where(p => !p.IsBot && p.IsLoaded))
        {
            ushort goodScore = (ushort)room.Team1Score;
            ushort badScore  = (ushort)room.Team2Score;
            try { human.Session?.Send(new PK_RC_ReviveScore
            {
                TargetScore = (ushort)BattleRoom.WinScore,
                GoodScore   = goodScore,
                BadScore    = badScore,
                Group       = human.Group,
            }); } catch { }
        }
    }

    private void BroadcastToRoomOthers(BattleRoom room, GameSession exclude, IRequest msg)
    {
        var seen = new HashSet<GameSession>();
        foreach (var p in room.Players.Values)
        {
            if (p.Session == exclude) continue;
            if (seen.Add(p.Session)) try { p.Session.Send(msg); } catch { }
        }
    }

    private void BroadcastToRoom(GameSession session, IRequest msg)
    {
        if (_players.TryGetValue(session.PlayerGUID, out var p) && p.Room != null)
            BroadcastToRoom(p.Room, msg);
    }

    /// <summary>
    /// Send to all HUMAN players EXCEPT the one specified — used to relay one
    /// player's packets (movement, fire) to their partners.
    /// </summary>
    private void BroadcastToOthers(BattleRoom room, long excludePlayerGuid, IRequest packet)
    {
        var seen = new HashSet<GameSession>();
        foreach (var p in room.Players.Values)
        {
            if (p.IsBot || p.PlayerGUID == excludePlayerGuid) continue;
            if (!p.IsLoaded) continue;
            if (seen.Add(p.Session)) try { p.Session?.Send(packet); } catch { }
        }
    }

    private void BroadcastToRoomOthers(GameSession exclude, IRequest msg)
    {
        var seen = new HashSet<GameSession>();
        foreach (var p in _players.Values)
        {
            if (p.Session == exclude || p.Room == null) continue;
            if (seen.Add(p.Session)) try { p.Session.Send(msg); } catch { }
        }
    }

    protected override void OnClientDisconnected(GameSession session)
    {
        if (_players.TryRemove(session.PlayerGUID, out var player))
        {
            if (player.Room != null)
            {
                var room = player.Room;
                bool startNow = false;
                (byte lm, byte wm, BattleGameMode gm) p = default;
                lock (room.StartLock)
                {
                    room.PendingHumans.Remove(player);
                    room.Players.TryRemove(session.PlayerGUID, out _);
                    if (!room.BattleStarted && !room.BattleEnded && room.PendingHumans.Count > 0)
                    {
                        room.ExpectedHumanCount = Math.Min(room.ExpectedHumanCount, room.PendingHumans.Count);
                        if (room.DeferredStartParams.HasValue && room.PendingHumans.Count >= room.ExpectedHumanCount)
                        {
                            p = room.DeferredStartParams.Value;
                            startNow = true;
                        }
                    }
                }
                bool anyHumansLeft = room.Players.Values.Any(x => !x.IsBot);
                if (!anyHumansLeft && room.BattleStarted && !room.BattleEnded)
                    EndBattle(room);
                else if (!anyHumansLeft && !room.BattleStarted)
                    BattleModeRegistry.MarkEnded(room.RoomGUID);
                if (startNow) StartPendingRoom(room, p.lm, p.wm, p.gm);
            }
        }
    }

    public override void Stop()
    {
        foreach (var room in _rooms.Values)
        {
            room.Timer?.Dispose();
            room.MinimapTimer?.Dispose();
        }
        base.Stop();
    }
}

// ═══════════════════════════ data classes ════════════════════════════════════

public class BattleRoom
{
    public long         RoomGUID      { get; set; }
    public byte         MapId         { get; set; }
    // v54 (задача 1): sid уже разрушенных объектов сцены в этом бою — чтобы бот не «сносил» один
    // объект многократно и чтобы не слать повторные PK_RC_DestroyedObject.
    public HashSet<ushort> DestroyedSceneObjects { get; } = new();
    public byte         BattleLevel   { get; set; }
    public byte         WarType       { get; set; }
    public BattleGameMode GameMode    { get; set; }
    public ConcurrentDictionary<long, RoomPlayer> Players { get; } = new();
    public Timer?       Timer         { get; set; }
    public Timer?       MinimapTimer  { get; set; }   // minimap/spotting broadcast
    public int          BattleTimeLeft{ get; set; } = 900;
    public bool         BattleStarted { get; set; }
    // Combat is locked until this moment (pre-battle prep countdown). Bots hold their
    // spawn and don't fire; players are held by the client's countdown UI.
    public DateTime     CombatStartUtc { get; set; } = DateTime.MinValue;
    public bool         BotAiModeLogged { get; set; }  // one-time [RS] BotAI diagnostic
    // A* navigation grid (built once per room from obstacle boxes)
    public bool[,]?     NavBlocked { get; set; }
    public bool[,]?     NavHazard  { get; set; } // sheer cliffs & unreachable water/basins
    public float        NavCell { get; set; }
    public float        NavOriginX { get; set; }
    public float        NavOriginZ { get; set; }
    public int          NavCols { get; set; }
    public int          NavRows { get; set; }
    public bool         BattleEnded   { get; set; }
    public bool         AllLoadedSent { get; set; }
    public int          Team1Score    { get; set; }
    public int          Team2Score    { get; set; }
    public int          StartedUnix   { get; set; }   // v12: unix-время старта боя для статистики
    public int          TabSyncTick   { get; set; }   // v13: счётчик секунд до следующего TabInfo
    public float?       KnownGroundY  { get; set; }

    // PVE Defense fields
    // ── Multiplayer ───────────────────────────────────────────────────────────
    /// <summary>How many humans to wait for before starting. Set from BattleModeRegistry.</summary>
    // ── Terrain height sampling ──────────────────────────────────────────────
    // Built from player-reported positions. Keyed by a coarse (X,Z) grid cell so
    // bots can snap to the actual terrain height the player experienced nearby.
    public Dictionary<(int gx, int gz), float> HeightGrid { get; } = new();
    public System.Collections.Concurrent.ConcurrentDictionary<(int gx, int gz), float> LiveElevatedGrid { get; } = new();
    public const float ElevatedCellSize = 16f;
    // Bounding box of where the player has driven — bots stay inside this.
    public float        MinSeenX     { get; set; } = float.MaxValue;
    public float        MaxSeenX     { get; set; } = float.MinValue;
    public float        MinSeenZ     { get; set; } = float.MaxValue;
    public float        MaxSeenZ     { get; set; } = float.MinValue;
    public float        LastPlayerY  { get; set; }
    public DateTime     LastCoordLog { get; set; } = DateTime.MinValue;
    public DateTime     LastGridSave { get; set; } = DateTime.MinValue;
    internal RoomServer.ObstacleBox[]? FileObstacles { get; set; } = null; // loaded from obstacles/map_N.txt
    internal RouteGraph? RouteGraph { get; set; } = null; // маршруты ботов из routes/map_N_routes.txt
    // Throttle for the diagnostic bot-height log (see movement tick).
    public DateTime NextBotYLog { get; set; }
    public const float HeightCellSize = 40f; // world units per grid cell

    public object           StartLock            { get; } = new();
    public bool             DeferredWaitStarted  { get; set; }
    public int              ExpectedHumanCount   { get; set; } = 1;
    /// <summary>Humans who have sent NM_CR_PlayerLogin but haven't received success yet.</summary>
    public List<RoomPlayer> PendingHumans        { get; } = new();
    /// <summary>Stored login-success build params for deferred send.</summary>
    public (byte littleMode, byte warModel, BattleGameMode mode)? DeferredStartParams { get; set; }

    public int          CurrentWave          { get; set; }
    public int          EnemiesPassedThrough { get; set; }
    public int          WaveKillCount        { get; set; }
    public int          PveDefenseLives      { get; set; }
    public int          CurrentWaveEnemies   { get; set; }
    // v70: диапазон уровней врагов обороны, посчитанный от уровней союзников-людей
    // при старте боя (см. HandlePlayerLogin). 1..10 по умолчанию (не сузит ничего,
    // если почему-то не пересчитался).
    public int          EnemyTierLo          { get; set; } = 1;
    public int          EnemyTierHi          { get; set; } = 10;

    // PVE constants
    public const int PveTotalRounds   = 12;
    public const int PveEnemiesPerWave = 6;    // 6 enemy tanks per wave (matches PveWaveTanks)
    public const int PveInitLives      = 6;   // how many break-throughs allowed
    public const int CommanderHpMultiple = 3; // Generals: general gets x3 durability
    public bool CommanderStarted { get; set; }
    public readonly System.Collections.Generic.Dictionary<byte, long> CommanderGuid = new();

    // Active smoke clouds & pending smoke casts (keyed by SmokeIndex allocated in HandleUseConsumable)
    public int NextSmokeIndex;
    public ConcurrentDictionary<int, (long PlayerGuid, int SmokeId)> PendingSmokes { get; } = new();
    public ConcurrentDictionary<int, ActiveSmokeCloud> ActiveSmokes { get; } = new();

    /// Respawn mode win threshold. 700 = 7 kills (achievable with 13 bots, no respawn).
    public const int WinScore = 2500; // v12: было 700 — цель возрождения теперь 2500 очков
}

public class ActiveSmokeCloud
{
    public int SmokeIndex { get; set; }
    public long OwnerGuid { get; set; }
    public (float X, float Z)[] Centers { get; set; } = Array.Empty<(float, float)>();
    public float Radius { get; set; } = 15f;
    public DateTime ExpiresAt { get; set; }
}

// Зеркало клиентского decfilesgame/EComponent.cs — какой конкретно модуль/слот
// затрагивает операция (используется в NM_CR_USECONSUMABLE: msg.ModuleType).
public enum EComponent
{
    Hull = 1,
    LeftChassis = 2,
    RightChassis = 3,
    Turret = 4,
    Gun = 5,
    Engine = 6,
    Radio = 7,
    AmmoBay = 8,
    FuelTank = 9,
    SurveyingDevice = 10,
    TurretRotation = 11,
    Vehicle = 100,
    Fittings = 101,
    Shell = 102,
    Provision = 103,
    Carport = 104,
    Vip = 105,
    Max = 106
}

public class RoomPlayer
{
    public long     PlayerGUID     { get; set; }
    public required GameSession Session { get; set; }
    public long     TankGUID       { get; set; }
    public int      VehicleId      { get; set; }
    public ushort   TankID, HullID, UnderpanID, TurretID, MainGunID;
    public ushort   EngineID, FueltankID, CartridgeID, RadioStationID, GaugeID;
    public int      TankCoatId     { get; set; }
    // v.fix (надписи/эмблемы в бою): раньше в RoomMainTankData/PK_PlayerInfo эти поля
    // всегда уходили нулевыми массивами — клиент получал "надписей нет" независимо от
    // того, что реально куплено/поставлено в ангаре (Tanks.SignatureIds/EmblemIds в БД).
    // Заполняются в LoginHandler из tank.SignatureIds/EmblemIds (см. ParseIntArray).
    public int[]    SignatureId    { get; set; } = new int[2];
    public int[]    EmblemId       { get; set; } = new int[4];
    public float    PosX, PosY, PosZ;
    public float    RotX, RotY, RotZ;
    public float    SpeedX, SpeedY, SpeedZ;
    public float    MaxForwardSpeedMps { get; set; }
    public float    MaxBackSpeedMps    { get; set; }
    public float    CurrentSpeedMps    { get; set; }
    // ── Гусеницы (сбитие) ──────────────────────────────────────────────
    public bool     ChassisLDamaged    { get; set; }   // левая гусеница сбита
    public bool     ChassisRDamaged    { get; set; }   // правая гусеница сбита
    public DateTime TrackRepairAt      { get; set; }   // когда авто-ремонт вернёт ход
    public bool     TrackImmobilized => (ChassisLDamaged || ChassisRDamaged) && DateTime.UtcNow < TrackRepairAt;
    // ── v12: внутренние модули — боеукладка и топливный бак ────────────────
    // ХП берутся из Parts: боеукладка GetPart(8, CartridgeID), бак GetPart(9, FueltankID).
    // Состояния зеркалят клиентский enum State: 0=Normal, 1=Injure, 2=Damage.
    public float    AmmoRackHp         { get; set; }
    public float    AmmoRackMaxHp     { get; set; }
    public float    FuelTankHp         { get; set; }
    public float    FuelTankMaxHp      { get; set; }
    public byte     AmmoRackState      { get; set; }
    public byte     FuelTankState      { get; set; }
    public DateTime BurnUntil          { get; set; }   // танк горит до этого момента
    public DateTime NextBurnTick       { get; set; }   // следующий тик урона от огня
    public long     BurnAttackerGuid   { get; set; }   // кто поджёг — ему зачтётся урон огня
    public DateTime AmmoRackRepairAt   { get; set; }   // авто-починка Injure -> Normal
    public DateTime FuelTankRepairAt   { get; set; }
    // ── Расходники (Provisions): аптечка/огнетушитель/дым, использует NM_CR_USECONSUMABLE ──
    public int[]      Provisions        { get; set; } = new int[3];      // снаряжённые ProvisionId (0 = пусто)
    public bool[]     ProvisionUsed     { get; set; } = new bool[3];     // был ли слот использован в бою
    public DateTime[] ConsumableCdUntil { get; set; } = new DateTime[3]; // кулдаун многоразовых расходников
    public float      ConsumableRepairMult { get; set; } = 1f;           // пассивный бонус ремкомплекта к скорости ремонта
    public float      FireDamageMult       { get; set; } = 1f;           // пассивный бонус огнетушителя (-20% урона от огня)
    public bool       HasAutoExtinguisher  { get; set; }                 // автотушение пожара золотым/элитным расходником
    // «ОБНАРУЖЕН»/«скрыт»: видим ли этот игрок вражеской командой прямо сейчас.
    // Переходы шлют PK_RC_SynCoverState живым игрокам (не ботам).
    public bool     SpottedByEnemy     { get; set; }
    public DateTime LastDictateTime    { get; set; }
    public float    BotReloadSeconds   { get; set; } = 6f;
    public float    BaseReloadSeconds  { get; set; }    // v28: перезарядка игрока из RoomMainTankAttribute
    public float    WanderOffset       { get; set; }
    public DateTime NextWanderChangeTime { get; set; }
    public float    RotateSpeed;
    public float    AngleSpeedX, AngleSpeedY, AngleSpeedZ;
    public float    TowerRotX, TowerRotY, TowerRotZ;
    public int      TowerRotSpeed;
    public byte[]   OperationInfo      { get; set; } = new byte[64];
    public int      OperationCode;
    public int      CurHp              { get; set; }
    public int      MaxHp              { get; set; }
    public byte     Group              { get; set; }
    public byte     Tier               { get; set; }
    public long     BattleRoomGUID     { get; set; }
    public BattleRoom? Room            { get; set; }
    public int      KillCount          { get; set; }
    public int      DeathCount         { get; set; }
    public int      Score              { get; set; }
    public int      TotalDamage        { get; set; }
    public int      ShootCount         { get; set; }
    public int      HitCount           { get; set; }
    // ── v12: счётчики для «Статистики боя» и фарма ─────────────────────────
    public int      PenCount           { get; set; }   // пробитий
    public int      BeHitCount         { get; set; }   // получено попаданий
    public int      DamageTaken        { get; set; }   // потери прочности суммарно
    public int      ChassisShareDmg    { get; set; }   // урон по целям со сбитой гусеницей
    public int      LightDamage        { get; set; }   // урон союзников по засвеченным ЭТИМ игроком целям
    public int      LightCount         { get; set; }   // сколько целей засветил
    public int      OccupyValue        { get; set; }   // очки захвата
    public int      SpEarned           { get; set; }   // заработано очков (SP)
    public int      SpSpent            { get; set; }   // потрачено очков (SP)
    public int      FreeBomberLeft     { get; set; } = 1;
    public int      FreeKatyushaLeft   { get; set; } = 1;
    public int      ReviveCount        { get; set; }   // возрождений
    public int[]    SkillUseCount      { get; } = new int[8]; // индекс = базовый id скилла 1..7
    public HashSet<long> DamagedEnemyGuids { get; } = new(); // «повреждено врагов»
    public int      CurrentShellId     { get; set; }
    public byte     LastShellResult    { get; set; }   // v13: результат последнего выстрела (для TypeDecal)
    public short    ShotSeq            { get; set; }   // v26: счётчик выстрелов -> уникальный ShellIndex
    public Dictionary<(int ShellId, short ShellIndex), DateTime> RecentResolvedHits { get; } = new();
    public int      ShellCount         { get; set; }
    // Per-shell remaining ammo (shellId -> count). Fixes the mid-battle switch showing the
    // ББ/slot-0 count for every shell: each loaded shell now reports its own count.
    public Dictionary<int, int> ShellAmmoById { get; set; } = new();
    // v68 (п.1): полный боезапас по типу снаряда (для восстановления при пополнении/возрождении).
    public Dictionary<int, int> ShellAmmoMaxById { get; set; } = new();
    public int[]    ShellSlotIds       { get; set; } = new int[3]; // slot->shellId for NM_CR_TankShellChange
    public bool     IsDead             { get; set; }
    // v30 (баг 3): момент смерти — по нему в обороне снимаются корпуса с карты
    // через WreckLingerSec, а не разом в конце волны.
    public DateTime DeathUtc           { get; set; }
    public bool     IsLoaded           { get; set; }
    public bool     IsBot              { get; set; }
    public DateTime LastFireTime       { get; set; }
    public DateTime ReloadBuffUntil    { get; set; } // Стимулятор/энергетик: пока активен — перезарядка ×0.5
    // v48.6: кулдауны способностей союзных ботов в обороне (чтобы не спамили).
    public DateTime BotSkillRepairCd   { get; set; }
    public DateTime BotSkillAmmoCd     { get; set; }
    public DateTime BotSkillStimCd     { get; set; }
    public short    MoveNumber         { get; set; }

    // ── Combat equipment flags (Table_Equipment special & defense-only effects) ──
    public float    DamageTakenMult       { get; set; } = 1f;
    public float    ApDamageTakenMult     { get; set; } = 1f;
    public float    HalveDamageChance     { get; set; } = 0f;
    public float    InvulnChance          { get; set; } = 0f;
    public float    BombDamageMult        { get; set; } = 1f;
    public float    KatyushaDamageMult    { get; set; } = 1f;
    public float    ShellDamageMult       { get; set; } = 1f;
    public float    StimulantDurMult      { get; set; } = 1f;
    public float    RepairCdMult          { get; set; } = 1f;
    public float    PveSpBonusMult        { get; set; } = 1f;
    public float    TrackRepairSpeedBonus { get; set; } = 0f;
    public float    TrackImmunitySeconds  { get; set; } = 0f;
    public DateTime TrackImmuneUntil      { get; set; }
    public float    AmmoBayNoDmgMult      { get; set; } = 1f;
    public float    FuelTankNoDmgMult     { get; set; } = 1f;
    public bool     FastChangeShell       { get; set; }
    public bool     IgnoreWeatherVision   { get; set; }
    public float    HideFactor            { get; set; } = 0f;

    // Spotting state: which enemy GUIDs this player currently sees in 3D.
    // Updated every MinimapTick under SpotLock; drives ViewState/BeLighted/TankHide sends.
    public object          SpotLock           { get; } = new();
    public HashSet<long>   SpottedEnemies     { get; } = new();
    public Dictionary<long, DateTime> SpotHoldUntil { get; } = new();
    public DateTime        EnemySpotHoldUntil { get; set; }

    // Smart-AI fields
    public BotRole  Role               { get; set; }
    /// <summary>
    /// Lateral Z offset (−100 … +100) assigned at creation to spread bots across
    /// the map's Z-axis. Flanker bots also use this as the arc magnitude.
    /// </summary>
    public float    AssignedZOffset    { get; set; }
    // Commander/Generals mode
    public byte     CommanderVote      { get; set; }   // 1=accepted the general offer
    public bool     IsCommander        { get; set; }
    public int      Sp                 { get; set; }   // ability currency: +500 per kill
    // Current patrol waypoint — refreshed each time the bot arrives.
    public float    WaypointX          { get; set; }
    // A* navigation
    public System.Collections.Generic.List<(float X, float Z)>? NavPath { get; set; }
    public int      NavPathIndex       { get; set; }
    public float    NavGoalX           { get; set; }
    public float    NavGoalZ           { get; set; }
    public DateTime NavNextRecompute   { get; set; }
    public float    WaypointZ          { get; set; }
    // Anti-stuck: a reference point sampled every ~1.2s. If the bot wanted to move
    // but barely did (wedged against a building / waypoint behind a wall), it enters
    // a short "escape" where it commits to the most open direction. This is what
    // stops bots shuffling on the spot forever.
    public float    StuckRefX          { get; set; }
    public float    StuckRefZ          { get; set; }
    public DateTime StuckCheckTime     { get; set; }
    public bool     WantedToMove       { get; set; }
    public DateTime EscapeUntil        { get; set; }
    public float    EscapeDirX         { get; set; }
    public float    EscapeDirZ         { get; set; }
    public int      SteerSideBias      { get; set; }
    public DateTime SteerSideUntil     { get; set; }
    // Last hull heading actually broadcast. Reused when the bot is stationary so it
    // holds a steady facing instead of snapping to a noise-derived angle each tick.
    public float    LastYaw            { get; set; }
    // Гистерезис доворота корпуса безбашенной ПТ: включается при выходе цели за УГН
    // и держится до уверенного наведения, исключая раскачку влево-вправо на границе угла.
    public bool     CasemateAligning   { get; set; }
    // Current turret/gun local yaw relative to hull [-180..180] and traverse limits.
    public float    CurrentTurretAngle { get; set; }
    public float    TurretTurnDegPerSec{ get; set; } = 40f;
    public float    HullTurnDegPerSec  { get; set; } = 38f;
    // Sticky "holding position to fire" flag with hysteresis, so the bot doesn't
    // flip advance/hold every tick at the engage boundary (that flip-flop, plus the
    // stuck-escape, is what made engaged bots wander in circles).
    public bool     HoldingPosition    { get; set; }
    // Last time this bot had a clear line of fire to an enemy. Lets an engaged bot
    // stay planted through brief LOS flicker (smoke/debris) but resume maneuvering
    // if the shot stays blocked (target took hard cover).
    public DateTime LastLosTime        { get; set; }
    // Smoothed hull tilt actually broadcast (deg). Low-pass filtered toward the raw
    // terrain tilt so a single noisy height sample can't snap the hull to a crazy
    // angle for one frame; eases onto slopes like a real suspension.
    public float    RenderPitch        { get; set; }
    public float    RenderRoll         { get; set; }
    public bool     TiltInit           { get; set; }
    // ── class-based combat AI ──────────────────────────────────────────────────
    // Derived from the tank's real stats on spawn; drives ranges, aggression, target
    // choice and movement style. DetectRange is this vehicle's vision radius.
    public CombatProfile Profile       { get; set; } = CombatProfile.Default;
    public float    DetectRange        { get; set; } = 260f;
    // Perception memory: last place an enemy was actually SEEN by this bot's team, and
    // when. Lets a bot advance to a target's last-known spot after it breaks LOS
    // instead of instantly forgetting it exists.
    public float    LastSeenEnemyX     { get; set; }
    public float    LastSeenEnemyZ     { get; set; }
    public DateTime LastSeenEnemyTime  { get; set; }
    // ── Phase 3: tactical positioning ──────────────────────────────────────────
    // Cached firing position — a spot with a clear shot at the current target, at the
    // class's preferred range, biased toward cover (peek/hull-down). Recomputed every
    // ~1.5 s (or when the target moves a lot), not every tick, to keep it cheap/stable.
    public float    FirePosX           { get; set; }
    public float    FirePosZ           { get; set; }
    public DateTime FirePosUntil       { get; set; }
    public bool     FirePosValid       { get; set; }
    public float    FirePosForEnemyX   { get; set; }
    public float    FirePosForEnemyZ   { get; set; }
    // ── Phase 5: execution skill (difficulty) ──────────────────────────────────
    public float    Skill              { get; set; } = 0.6f;  // 0=recruit .. 1=ace
    // Aim tracking: which target the bot is currently laying on, and since when. Drives
    // reaction delay (can't fire until it's tracked the target long enough) and aim
    // convergence (accuracy improves the longer it holds the same target).
    public long     AimTargetGuid      { get; set; }
    public DateTime AimStartTime       { get; set; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Embedded building-collision boxes (merged into this file so there is only ONE
// file to replace — no separate ObstacleBoxes.cs needed). See LoadMapObstacles.
// ═══════════════════════════════════════════════════════════════════════════
public static class ObstacleBoxes
{
    public static readonly System.Collections.Generic.Dictionary<byte, string> ByMap = new()
    {
        [1] = // Москва — 1586 boxes
            "SACQAUwAlwFJAI8BUACQAUkAnQFKAKQBSgCjAVEApAFOAJcBUwCcAU8AjgFWAJIBUACjAVcApAFTAEQBYABbAVMAYQFgAHgBVACOAVkAlgFUAJUBWQCcAVUAsgJrALsCVgCcAVcAowFXAJMBXwCYAVcAmgFfAJ4BWQCQAm4AnQJbAHsAagCQAFwAYgBqAHsAXABWAmkAaQJdABsBaQA9AV0A9ABpAAcBXgD8AWsAEQJeAJwBZACdAV4AuAFqANoBXgB9AmsAkQJfALkAagDEAF8AjwBoAKUAXwClAGgAuwBfAMMAaADYAF8ABgFoABwBXwDoAWgA/QFfABACaAAmAmAAQAJoAFYCYABoAmgAfgJgAEkBZwBQAWAATwFnAFcBYABVAWcAXQFgAFwBZwBkAWAAYgFnAGoBYQBpAWcAcAFhAG8BZwB3AWEAdQFnAH0BYQB8AWcAgwFhAIIBZwCKAWEAiAFnAJABYQCPAWcAlgFhAJUBZwCdAWIAnAJrALICZADaAWkA4QFkAOABaQDoAWUAJQJpAC0CZQAsAmkAMwJlADICaQA6AmUAOQJpAEECZQA5AXMARgFlANgAagDgAGUA3wBqAOYAZQDsAGoA9ABmAKQBawCsAWYAqgFrALIBZgCxAWsAuQFnAOYAaADsAGcAVgB6AGMAaAB/AWwAkAFqALoCgADDAmoA9ABuAPUAagDzAW4A9AFqAEECbgBCAmsAkgBuAJMAawCzAW4AtAFrAFQBbgBVAWsADwF2ABoBbABmAXIAbAFsAHAAbQBxAGwAtgBtALcAbABgAnYAcQJtANMAeQDfAG4AYAF1AGYBbgAFAXQAEgFwAOYBdADuAXEA8gF2APoBcQCKAHoAmwBzANwAjgDfAHMASAGOAEoBcwCsAY4ArwFzACMCjgAlAnYAqwB7AK0AdgB7AXsAfAF3AOMBewDkAXoAWACQAGEAegCqAH4ArAB6AHoBfgB8AXoA4gF/AOQBfABsAH0AbwB+AKkAgwCrAH4AeQGDAHsBfwDhAYQA4wGAALoClgDDAoEAqgCGAKwAgQB6AYYAfAGCAOIBhgDkAYQAqwCIAKwAhAB7AYgAfAGFAOMBiQDkAYUAGAKUACYChQA2AZAAQgGKAMcAkADPAIoAlwGWAKgBigD6AJEAAQGKAGwAmwB2AI4AugGUAMIBjwAYAZkAIgGPAFgApQBhAI8AbgGZAHgBkgDEAJkAyQCTABYBlgAXAZMAJgKWACgCkwBeAZYAXwGTAMQBlgDFAZMAygCWAMsAlABZApUAWgKVALkCqADGApUACAGWAAkBlgD5AK8ABwGWAG0AlgBuAJYAkQCWAJIAlgC9AJYAvgCWAHcBmgB+AZYAlAGiAKcBlgBJAaQAXgGXADcBowBKAZcApgGiALYBlwDsAJsA8gCXAE4CogBZApgAywCrANcAmAB+AaAAlAGYANYBpQDrAZgAqgCeALYAmADXAKEA7ACYAP4BpAARApkAtAGmAMYBmQAqApwAMAKZAOkBogD/AZkAOgKiAFACmQAWAawAIgGZABECpAAeApkAHgKkACoCmgBdAaMAcgGdAAUCqQAPAqAAfgG2AIcBoADXAbYA4AGhADcCpAA7AqEATwK3AFgCogCRAK4AlQCjAG8BqgB0AaMAaQGtAHMBpAAIAaoADgGkAF4ArABiAKUAngC0AK0ApQCtAq8AtwKlAL4BtwDIAacAEwGoABQBqAC3AsEAxQKoAAYCuAAQAqkA3ACxAO4AqgBxAKwAjACqAMoAsQDNAKsAFgHAACMBqwBeALMAYgCsAC4CtAA7Aq4AZgC8AHQAsAAuAbcAMgGxABsCtQAeArIAXgC5AGIAswDFAL0AzwC0ABkCtgAcArQALwLBAEQCtQC1AMEAuwC1AB0CwQAwArYA1wHCAOoBtgB3AcIAigG2ALQBwgDIAbYARAHCAFgBtgDpAMEA+QC2AFUCvQBYArcAaAHBAHgBtwD/AcEAEAK3ANMAuwDZALcALwHAAEUBtwDqAcAA/wG4AE0CugBRArgAXgDAAGIAuADYAMMA6gC4AL0AwADCALgAigHBAKABuACfAcEAtQG5AHQAwQB5ALoAQwLEAE0CugCmAskAtAK7AB4ByAArAb0ADQLHABcCvQBZAsAAWgK+ABcCwgAdAr8AXgDGAGIAvwBCAsMASQK/ADkBxgBAAb8AvADCAL0AvwDRAccA2QHAABUBwwAWAcAANwHEADgBwAAIAcEACQHAAG8BxQBwAcEAkQDCAJIAwQBZAsIAWgLBAOoAyADwAMEAcwHIAHkBwQDYAcYA2gHCAMoAwgDLAMIAbQHGAG8BwgB9AcgAgwHCADgBxgA6AcIALwHCADABwgDWAcYA2QHCAGYBwwBnAcIAiwHDAIwBwgDIAcMAyQHCAOoBwwDrAcIAZQDIAGkAwgA6AscAQgLDAGEAwwBmAMQAugLTAMQCxQBjAMgAZQDIADgC1gBGAsgAVgDpAGIAyABiAMkAZwDIACABzwAnAcgAcQDQAHYAyQDxAc8A9wHJAKQC2gCqAskAXgHQAGUBygBcAc4AXQHKANkBzwDbAcoAOgHPADwBywD3ANUAAQHLAG0AzABuAMwAXQHQAF8BzADaAdAA3AHMADgB0QA7AcwAkQDNAJIAzQC9AM0AvQDNAC8B0AAwAc0AmwHQAJwBzQCyANsAwADNAOAA0ADhAM0AQgHOAEMBzQCwAc4AsAHNACEBzgAiAc0AggHOAIIBzQBYAtcAYgLOAOwBzgDtAc4AAgHOAAMBzgCBAdEAhwHOAIYB0QCMAc4AkgDZAJ0AzgAuAs8ALwLOACACzwAhAs4AgQHcAI8BzgBuAdsAgQHOAFkCzwBaAs8AsQDSALcAzwC2ANIAvADPANsA0gDhAM8AMgHZAEMBzwAsAdIAMgHPAO0B2QD+Ac8AswHdAMgB0ACbANgAsQDQAMoA4gDWANAALQLiADoC0ADPAOIA3ADQABMB0QAWAdAAEwLjAB8C0ABDAdkAWQHQAFgB2QBuAdAAxwHZANwB0ACeAdkAtAHQAP4B2QAUAtIAFgLnAB4C0gC5AuAAxALTABcB5gAjAdMABAH0AAUB1AAgAtoAJwLUAL4B6gDGAdQAUALqAFgC1AA6At0AUALWAJgB2gCfAdYA+wDlAAoB1gDeAN4A4wDXAOsA3gDwANgAkwDtAJwA2ADkAN8A6ADYAO0B6AD4AdoAuQDhAL0A2wAoAt0ALALcACoC3wAuAuAAugLsAMUC4ACzAPYAuwDgAPwA8AAIAeEALAL6ADoC4QDIAOgAzADiAB4C5AAjAuIA4ADsAPEA4gDfAeYA5gHjAKMB7wCsAeMAzADtANYA4wDWAfUA4AHkAIsA6gCRAOQAHwLmACQC5QA5AfIAQgHmABgB+wAhAeYAjAHqAJIB5gCSAeoAmAHmAGEB7wB3AeYAdwHvAI0B5gAoAuwALgLnAMgA7QDMAOcAFgL9AB4C6ADtAfsA+QHpAFYA/ABjAOoAvgH/AMYB6gAUAeoAFAHqAFACAAFYAusAuAIBAcQC7QCSAPMAlQDuAOgADAEGAe8ADwH6ABoB8ACQAPEAkwDyANEB9QDYAfIA7gEIAfcB8wD9APsABAH2ALEACQG9APYAbQIAAXcC9gAdAQQBLAH4AK0CAgG3AvgA+AD/APwA+gAuAgcBQAL7AL0AAQHEAPsAGAEIASoB+wBYABEBYQD8ACoBCgFDAfwAzgEKAdwB/ABWAQcBYgH8AHQBBwGAAf0AygAKAdgA/QAMAgkBHwL9AI4ACwGcAP0A9QACAfsA/QBiAQkBdQH9AJUAAQGbAP4AvQEJAcgB/gBsAP8AbwD+AJMBBwGpAf4AqAEHAb4B/gA4AggBSAL+ANgBBwHuAf4A9gEHAQwC/wBHAgkBWQL/AJsACAGxAAAB4QAHAekAAAG4AggBvAICAdMACQHhAAIBFAEEARUBBAGAAQ0BigEEAY0BCAGTAQQB0wEIAdkBBQHKAAcB6QAFASECBgEiAgUBJAEJASoBBgFDAQkBSQEGAUgBCQFOAQcBugIOAbsCCAEHAQgBCAEIAZkACwGaAAgBkQAJAZIACAHKAAkBywAIAUoCDgFQAggBWQIJAVoCCAEuAgkBLwIJAbgBDAG5AQkBFQEKARUBCgHYAQsB2QEKAUIBCwFCAQoBlAELAZUBCgEbAREBIgEMAWwAGAF3AAwBOAETAT8BDQG6AhQBuwIQAacBFwGtARABYAAXAWEAEgGvARwBuQESAQECHAEMAhMBmwAYAaMAFAG4AhwBvAIVAe0BGAHuARUBYQEgAWwBFgHDARYBxAEWASACFgEhAhYBaAEXAWkBFgFYAhcBWQIXAV4AHwFiABgBzQAYAc0AGQGQABkBkQAZAbwAGQG9ABkB/AAmAQkBGQFpASYBfgEaAbwBHQHCARoBFgImASUCGgEFARsBCAEaAS4CJgFCAhoB2wEmAe4BGgEkAiYBLwIaAX4BJgGRARoB+QAeAf8AGgH+AB4BBAEaAQkCJQEXAhsBkAElAaIBGwG4AiIBvAIbAagAJwG7ABsBoQElAbEBGwHNASYB2wEbAe0BJgH6ARsBwgEmAc4BGwFBAiQBVwIdAZIAJQGoAB4BXgAmAWIAIQG4AikBvAIjAbEBJwG3ASQB+QEnAf8BJAFXAD4BZAAlAWUAMwFyACUBkQA+AZ4AJwFXAioBWAInAcABKgHBAScBCQIqAQoCKAH2AC4B+gAoAbgCMAG8AigBaAEpAWkBKQH5ACwB/QAqAQcBKwEIASoBLAIvATQCKgGzAiwBtgItASICMQEqAi0BtwA5AcMALgEDATQBBwEvAbgANQG8ADEBvgI5AcMCMgHVAToB4gEyAXsAMwGAADIBeAA0AX0AMwGAADUBhAAzAXUANAF6ADMB5QA7Ae0ANAH6AEEBBQE0AYMANQGIADUBhwA7AYwANQGxAEYBvAA2AeABOwHtATgBvgJAAcMCOQGJAEABkAA9AVYATwFjAD4BkwBNAZ4APwG4AlUBwQJBAfoATQEFAUEB5QBMAe8AQwHCAEoBygBGAbIAXAG6AEYB7ABMAe8ASQHEAE4BygBKAZMBUAGbAUsBbQBMAW4ASwGPAEwBkABLAe0ATwH0AEsBLAJdAT4CTAH6AF8BBgFMAeUAYgHuAE0BkgBTAZYATQElAlIBLQJOATkCUgFBAk4BPwJSAUcCTgFGAlIBTgJPAVQAVgFYAFABrQFUAbEBUAEsAlEBMwJQATMCUQE6AlABTQJRAVQCUAFsAFEBbwBQASoCWAEvAlQBJwJYASwCVAFFAFkBSgBUAZMBWwGbAVUBuAJrAcECVgFkAmABbwJXASoCXgEvAlcBvgFbAcIBVwGRAF4BlQBYAZIAYgGdAFgBiAFeAZABWAFvAV0BdAFYAWAAYAFlAFgBaAJdAW8CWgFtAGYBdQBbAb0AWwG+AFsBbQJjAXYCWwGwAG4BvABcAdMBYQHZAV0BkQBjAZUAXQGaAGMBnQBdASwCfAE/Al4BbAFiAXEBXgERAWkBHAFfAY4AYAGSAF8BLQFgATABXwFyAmcBewJfAWAAZwFlAF8B+gBrAQUBXwEWAWABFgFgASMBZAEqAWAB1wFtAd4BYQEbAXcBJAFhAXIAbgF4AGIB5AB1AfAAYgHIAGgBywBiAdcAaAHaAGIBeAJqAYACYgGSAHQBnABjAWoBawFyAWMBzQFrAdUBZAEwAWUBMAFlAQUBZwEKAWYBYABtAWUAZgEIAWgBDQFnAX4CawGEAmcByABuAcsAaAGiAWwBpgFqAbYCfQHDAmoBogFzAaoBawH6AHcBBQFrAYQCbwGKAmsBpwBvAasAbAGqAG8BsABsAaoBcgGwAW0BTwB6AWMAbQGIAnUBkQJuAbkAdAG9AG4BbQBvAW4AcAFGAXwBUAFyAY4CeQGPAnMByQB5Ac0AcwGRAnQBlAJzAZMAiQGcAHMBsAGAAbkBdAHlAIoB7gB2AbgAgAHCAHYB+gCKAQYBdgEZAYkBJQF4AcoAjQHXAHgBnQB8AZ8AeQGMAoEBkQJ5AVcAhQFiAHsBngB/AaIAfAEtAoIBLgJ8AbcCkAHEAn4B1wCBAdsAfgFeAYMBYwF/AVACiQFcAn8BVwKJAWICfwG7AIMBvgCAAbAAigG7AIABjgKGAY8CgAGcAIkBsgCBASYChQEoAoEBLAKJATACgwFTApEBXwKFAVcAmAFjAIUBXQKOAWQChgFQAo8BVgKGAY4CjQGPAocB9ACKAfoAhwEAAZUBDgGHASwCjwEwAogBFAGnASoBiQGUAJ4BnACJAVYBjQFbAYkBsQCfAboAigHjAJAB5wCKAesAkAHvAIoBWAKVAWMCiwFRApUBXAKMAc0AoQHWAI0B3QGeAeIBjQGMApUBkQKOAXEAkAGMAI4BLAKWATACjwHrAJUB7wCPAboCpQHCApAB6ACTAewAkAHIAJEByQCUAesBlwHvAZQBjAKbAZEClAH+AJcBAQGUAWYAnwFwAJUBxACWAcgAlQECAZsBBQGVAccAlwHLAJYBLQKcAS4ClwHiAKAB6QCYAVcApAFiAJkB6ACgAewAmgGMAqIBkQKbAfsAsQEEAZsBWgGnAV4BmwEsAqMBMAKdAbsAnwHAAJ0BBwGeAQcBngGRALEBngCeAbIAsAG8AJ8BvACgAcEAoQHDAK8B0QChAY4CqAGQAqEBywCnAc8AoQEwAaIBMAGiASsCqQEwAqIBFAGkARcBowFYALYBYwCkAbkCrAG+AqUBaQCtAXAApwHiAKsB5QCoAeQArgHoAKgBCAGwAQ0BqQG+AbUBygGpAS0CsAEuAqoBVgGuAVsBqwEmArABKAKrAbkCswG+AqsBLAG6ATsBrAFAArgBTAKsAREBtgEbAawBjwCtAZAArgHlALYB+wCuAVICugFXAq8B3gGzAeMBrwGyAMUBuwCvAfoAugEFAbABLQK3AS0CsQGTAMcBnACxARgByQEmAbIBBwG5AQwBsgHTAL4B5gCyAbkCugG+ArIBygC5Ac4AtAGGAbgBigG0AYsAuwGQALQBEgG8ARkBtgF3Ab0BfgG2ASsCvQEwArYBVwDBAWMAtwGAAbwBhQG4AcoAvgHOALgBuQLAAb4CuQH7AM8BBAG5AVQCxgFcAroBEgG8ARYBvAETAb0BGAG8ASsCxAEwArwBggG/AYUBvQGUAcYBnAG+AcsA0wHUAL4BBQHBAQgBvgGsAsoBtgK+AcQAxgHJAL4BjADGAZAAvwG5AscBvgLAAVkA0AFkAMEBrgHFAbMBwQFqAs0BdgLCAawBxQGwAcIBBAHEAQkBwgErAsoBMALDAQcBxQEMAcQBvQDEAb4AxQGvANgBvADFAWUByAFoAcYBuQLNAb4CxgGUANEBoADGAWgBywFtAckBGAHcASUBygEsAtEBLQLLAZIA2AGeAMsBYAHQAWUBzAFqAdQBcgHMAdAB1AHYAcwBuQLUAb4CzQGcANkBsADNAbEB0gG2Ac4B+QDhAQUBzwFdAdsBZwHPAXsA0AGAAM8BbQDQAW4AzwF4ANEBfQDPAYAA0gGEANABYADXAWQA0AEpAtgBMALQAXUA0QF6ANEBgwDSAYgA0wEoAeMBOAHTAcoA5gHXANMBuQLbAb4C1AEVAdUBFQHWAWoC5gFuAtYBKQLeATAC1wGOAOUBnADYAbkA3gG8ANgBqADiAboA2AFlAeEBcgHYAUsC6AFbAtkBnAHdAaIB2gGPANsBkgDaAbkC4QG+AtoBuwDiAcEA2gF8AeEBhAHaAYIB4QGKAdoBiQHhAZAB2gEYAe8BJgHbAWEA4wFlAN0BsQHhAb0B3QG9AeEByQHdASkC5AEwAt0BuQDjAbwA3gF8AeUBgwHeAYIB5QGKAd4BiAHlAZAB3wF8AusBiQLgAXwC5AGBAuABuQLoAb4C4QHJAOEByQDhAVEC5gFTAuEB+gD3AQMB4gFhAOoBZQDiAYIB5wGIAeIBxAHnAckB4wG6AOYBvADjAQUB5AEIAeMBKQLrATAC5AGTAOoBlwDkAbYA6AG6AOUBygDzAd4A5QExAvoBRgLnAbkC7gG+AucBAQH0AQ0B6QFhAPABZQDpAZMA7wGXAOwBiwD+AZQA7QG5AvUBvgLuARgBAQIkAe8BlAAFApwA7wG2APkBwADwATIB9gE4AfABTQD5AWMA8gHLAAgC1ADzAYcABAKMAPMBxgD1AcoA9AG5AvwBvgL1AcUA9wHJAPcB/AAIAgYB9wG6AP0BvQD4ASkCBwI3AvgBbAD5AW8A+AFaAA4CYwD5Ab0A+gG+APoBrAD9AbAA+gGaAgcCowL6ARcCDQIqAvoBDwEBAhYB+wG5AgICvgL8AasA/wGxAPwBqQAAAq0A/QFsAgACcQL9AbEACQK8AP0BIQICAiUC/gG9AAACwgAAArwAAQLBAAECGgEXAiMBAQKEAQkCiwEBArkCCQK+AgICBwECAggBAgJKAhACWAIDAo8ABAKQAAMCBAIeAh8CBAJOAgoCUgIEApIAGAKeAAUCeQELAn0BBQKTAQsClwEFAtABCwLVAQUC8QELAvYBBwL6AB0CBgEIAssAHQLUAAgCpgETArYBCAK5AhACvgIIAgYBDQIMAQgC1AEXAuIBCQKxABYCvAAJApcBEgKgAQkCoQESAqsBCQK2ARICvwEJAp0BEgKnAQkCsgESArsBCQK8ARICxQEKAn4BFgKKAQsCfAIZAooCCwLBARUCywELAvoBDwICAgsCAQIPAgkCCwIOAg8CFgILAhQCDwIcAgwCdgEPAnsBDAJ9AQ8CggEMAoQBDwKJAQwCkAEPApUBDAJnAQ8CbAEMAsMBDwLIAQwCygEPAs8BDALRAQ8C1gEMAtgBDwLdAQwCQwEYAk8BDAJGARECTgEMAloBEQJiAQwCYQERAmgBDALsAQ8C8gEMAvMBDwL4AQwCGwIUAh8CDQJEARQCSAENApQBFwKeAQ0CcwEOAncBDQJ6AQ4CfgENAoEBDgKEAQ0CiAEOAosBDQKNAQ4CkQENApQBDgKYAQ0C4AEOAuQBDQIIAg4CDwINAsgBDgLMAQ0CzgEOAtIBDQLWAQ4C2QENAtwBDgLgAQ0C8QEOAvUBDQL4AQ4C/AENAlgCFwJiAg0CZQEVAmwBDQJTAQ4CWgEOAk0BDgJTAQ4CWgAkAmMADgIUAQ8CFwEPArkCFgK+AhEC6AAeAvsAEwIbAhoCHwITAtMAHALpABMCRAEbAkgBFAJHASECVAEVAqMAIQKqABUCuQIdAr4CFgKxACkCvQAWAiABHgImARcCGQEhAiMBFwKUAC0CnAAYAkQCJgJSAhgCZwAiAnIAGgIBAi0CFAIaAhwCIQIdAhsCRQEiAkYBHALaAB8C3QAcAgcBHAIIARwCcgEpAoABHQK6AiMCvAIdAiABJAImAR0C3AAfAuAAHgL1ACIC9wAeAuIBKALsAR4C5gAfAuYAHwJPASoCWwEgAvUAJQL2ACECHAIoAh0CIgJGASgCRgEiAr0AKQLEACICuQIqAr4CIwK/AigCxwIjAsYCKALOAiMCKAEoAjABIwLMAigC1AIjAlgANgJkACMC0wIoAtsCIwLgAigC6AIkAu4CKAL1AiQC9AIoAvwCJAL7AigCAwMkAgIDKQIJAyQCDwMpAhcDJQIuASwCMwElAroCKAK8AiYC2gInAuECJgLoAicC7wImAgkDJwIQAyYCFgMtAhcDJwIbAi4CHwInAkQBLwJJASgCuAE0AsQBKQKYATMCogEpArAAPAK9ACkCuQIxAr4CKQLlAC4C7QAqAhkBVgIqASoCiQA4ApcAKwIVASwCFgEsAvgAMAL6ACwCFQM0AhoDLQKUAEICnAAtAloBRAJnAS0CcwFEAoABLQL6AUQCBgItAlIBMwJYAS0ChgEzAowBLQLWATMC3AEtAgoCMwIQAi4ChQIvAogCLgL6ADIC/AAuAuEBRALuAS4CLQIzAjkCLgKAAi8CgwIuAnoCMAJ9Ai4CdQIwAngCLgIcAjUCHQIuAqgBNQKzAS8CRQE2AkYBLwK1ATECuQEvAgUBMAIIAS8ClQFFAqgBLwKiATICpgEvAm8CMQJyAi8CIgI3Ai4CLwLWADAC1gAwArkCNwK+AjACagIyAm0CMAJ6ATcCfgEwArMBQgLIATAC7gAyAvIAMQJkAjMCaAIxAsoARgLYADECyQAyAsoAMgIUATMCGAEyAtcAPgLqADIC5QE5AuoBMgLqADwC+QAyAl8CNQJiAjIC+QBLAgYBMwIVAzoCGgMzAhIBNAIWATMCCAE1Ag0BMwIFATUCCQE0AloCNgJdAjQCGwI8AiACNAJEATwCSQE2AlkATAJiADYCVQI4AlkCNgKqAU0CwQE3AnsAOAKAADcCEwFIAhkBNwJ4ADkCfQA3ApwBTQKyATcCugI+ArwCNwKAADkChAA4AnUAOQJ6ADgCtgE7AroBOAKwATsCtgE4AlACOwJUAjgCpwE7AqwBOQKDADoCiAA5AqIBPAKmATkCtQE9AroBOQIHAToCCAE5AhUDQQIaAzoCogE+AqcBOgJLAUACUQE6AokBQAKPAToCEQJAAhgCOwIbAkICIAI7AjACXwJWAjsCRAFDAkkBOwLGAT8CyAE8ArIAUgK7ADwCuwFAAr8BPAK5AUECvgE9ArkCRAK+Aj0CnwFCAqMBPQKeAUECoQE+ApQBQgKWAT8CXAJEAmQCPwKhAEICpABAAssAVgLUAEACWAJMAmMCQAK8AUUCwAFAApwARAKiAEECUwJQAmECQQJ7AkUCgwJBAoICRQKKAkECiAJFApACQQIWA0gCFwNBAmQCZQJ1AkECigJEAo0CQQKeAUYCoAFBAhsCSQIgAkECVgJCAl0CQgJEAUoCSQFCAnQCQwJ7AkICiwJKApICQgKSAEgClgBCAgYBRwIMAUMCwgFOAskBRAK5AksCvgJEApACTAKXAkQClAFPApoBRQKqAU4CswFFArsARgLAAEUCnAFcArIBRQLGAEcCywBGAqoBXALBAUYCvABJAsEARwJ4AkwCgAJHAhUDTgIaA0cCxQBIAsoASAKSAE4ClgBIAtUBTQLbAUgCGwJQAiACSAKOAEkCkgBIAq0BSwKwAUgC3wJQAuQCSAJEAVACSQFJAjABUAI3AUkCiwJQApICSgK5AlICvgJLAvgAXQIFAUsCbQBMAm4ATAJgAFQCZABMAgYBUgINAUwCnQFSAqABTQK8AVICvwFNAlsCUQJiAk0CugJQAsUCTQK2AlECugJNAhUDVQIaA04CtAFlAsgBTwKTAWICqgFPAuICVgLiAk8CWgFmAmcBTwJzAWYCgAFPAuEBZgLuAU8C+gFmAgYCTwKLAlcCkgJPAhwCVgIeAlACRgFXAkcBUAJ4AlQCfwJQAtYBVgLcAVACCgJWAhACUQKUAVUClgFRAp8BVgKjAVECqABeArsAUQJSAVcCWAFRAoYBVwKMAVECtgJcArkCUQK5AVYCvgFSAsYBVgLJAVICngFWAqEBUgK7AVcCvwFTAukAXQL5AFMCWgJYAmICUwJgAFsCZABUAhQBVAIXAVQC/QFZAgQCVAKeAF4CqABVAngBXAJ8AVUC2ABfAukAVQKiAVkCpwFVArUBWQK6AVUCygBeAuAAVgLhAlwC4gJWAhUBXQIZAVYCtgJYArgCVgJ5AloCgAJWAh0CXQIdAlYCKwFaAjABVwJGAV0CRgFXAhABZQIeAVcCogFaAqYBVwK2AVoCuwFYAqYBWgKsAVgCsQFbArYBWAJxAFsCjABaAh4BXwImAVoCJQFfAi0BWgJZAHACYgBaAp0AXgKjAFoCogBeAqgAWwJ4AmACgAJbAhgBXAIfAVwCMwJrAkMCXAIbAmQCIAJcAt8CZALkAlwCRAFkAkkBXAK2AmgCuQJdAowCZAKSAl0C+QBgAvwAXQIHAV4CCAFdAl4BYgJkAV0CKwJhAi0CXgKpAWQCtAFeAtEBZALXAV4CEQJkAhgCXwJLAWQCUQFfAokBZAKPAV8CkABgApEAXwK8AGACvQBgArEAZgK4AGACggJnAooCYAKIAmcCkAJgAhICbAIdAmEC+gBmAgIBYQLKAGQCzABhAqIBZAKmAWECVgJmAl0CYQJcAmYCZAJiArYBZAK6AWICKQJmAiwCYgJ1AmcCfQJjAt8CagLkAmMCHQJqAh4CZAJJAmUCUAJkAqgBbgKyAWQCUAJkAlcCZAJ8AmQCggJkAkYBawJHAWQC8ABqAvgAZAJHAmwCSwJkAmQCaQJmAmUCZAJuAmcCZQIGAnYCFwJlAk0CaAJYAmUCTQJnAlACZwIoAmoCKgJoAiwCbQI0AmgCQQF8AlMBaAJCAmwCSQJpAhsCcQIgAmkCLAF3AjwBagJuAHICcwBqAkQBcQJJAWoCMAKPAkICbAKzAnYCuAJsApICcQKaAmwCnAJzAp0CbAInAnACKQJwAlYAgwJjAHACHAJ4AiACcAKTAnUCmgJwAkQBeAJJAXICJgJ1AigCcgKbAnoCoAJ1ArYCeAK5AncCJgJ6AicCdwKNAnoCmQJ3AokCegKQAngCgQJ8AogCeAJGAXkCTQF5AvUBgAL9AXkC+wGAAgMCeQICAoACCQJ5AggCgAIQAnkCDgKAAhYCeQIVAoACHAJ5AmsBgAJzAXkCcgGAAnoBeQJ5AYACgAF5An8BgAKHAXkChgGAAo0BeQKMAYAClAF5ApIBgAKaAXkCmQGAAqABeQKfAYACpwF5AqUBgAKtAXoCrQGAArIBegKIAosCngJ9AiUCgAInAn4CaAKGAm0CgALTAocC2AKCAiUChQInAoICagGIAnYBggJZAJgCYgCDAh4DigIfA4MCTgKIAlUChAJ2AYkCggGFAlUClwJrAoYC0wKOAtgCiQIiApICKwKJAigCjwIwAooCHAORAiADigLHAo4CzwKKAooCnAKNAosCmgKTAp8CjALPAo0C1gKNAsYClQLKAo4CrAKSArMCjgKyApICugKPAp4CkgKqAo8CqgKSArUCjwIiApcCJgKPArUCkwK5ApACHgOXAh8DkgKaApkCnwKTArYClwK5ApMCtgKcArkClAJqApkCcgKVApUCmgKdApYCIgKeAiYClgLbAaEC5wGXAlMCngJYApcCHgOeAh8DmAKPApkClgKYAlkArgJiAJgCjwKfApACnAJ+Ar0CjQKdAiICrgIlAp4CHAOlAiADngJ0Aq4CgQKfAncCrAKEAqEC1wCoAuQApAIcA6wCIAOlAs4AsALZAKsCHAOzAiEDrAJmArcCaQKsAmsCwQJ9AqwCigKtApACrAKJArMCigKsAlYAwQJjAK0CawCuAm4ArQJmArACawKtAiECtAIlArICHgO4Ah8DsgKoAbsCtQGyAisCuAI3ArMCiQK6AooCtAIjAroCJAK2AnUB0QKQAbcCZgLCAmkCtwIiAsICLQK4AtUAvQLZALgC7wC9AvQAuAItAb0CMQG4Ak0BvQJSAbgCHgO/Ah8DuQKIAsACjAK5AloCwAJbAroCIQLCAiUCugKJAL4CigC7ApgAzwKsALwC2gDJAuYAvAIxAckCPQG9AvMAxAL7AL0C+QDEAgEBvQIAAcQCCAG9AgYBxAIOAb0CDAHEAhQBvQITAcQCGgG9AhkBxAIhAb0CHQHIAicBvQJdAcICZQG9AnEBwgJ5Ab0CfgHCAoYBvQKFAcICjQG+AlYBwgJeAb4CkQHCApgBvgKXAcICnwG+Ap4BwgKmAb4CpQHCAqwBvgKrAcICswG+ArIBwgK5Ab4CuAHCAsABvgK/AcICxgG+AsUBwgLNAb4CzAHCAtQBvgLSAcIC2gG+AtkBwgLhAb4C4AHCAucBvgLmAcIC7gG+Au0BwgL1Ab4C9AHCAvsBvgL6AcICAgK+AgECwgIJAr4CCALCAg8CvgIOAsICFgK+AhUCwgIdAr4CHALCAiMCvgKeAsICpgK+AqUCwgKtAr4CsgLCAroCvgLSAMIC1wC+AtkAwgLeAL4C4ADCAuUAvgLsAMIC8QC/AlsCwwJjAr8CxgDCAswAvwIfAcICJQG/AiYBwgIsAb8CLQHCAjIBvwI0AcICOQG/AkgBwgJOAb8CUAHCAlUBvwIkAsMCLAK/AioCwwIyAr8CkgDDApoAvwKYAMMCoAC/AqYAwwKuAL8CugDDAsEAvwLAAMMCyAC/AosBxgKQAb8CfALDAoQCvwKDAsMCiwK/AooCwwKRAr8CkALDApgCvwIcA8YCIQO/Ar8AxgLHAL8CdwDLAooAvwJtAscCcgK/ApAAxwKUAL8C8QDJAvoAwALPAMEC0wDAAtYAwQLaAMAC3QDBAuEAwALkAMEC6ADAAukAwQLtAMAC8ADBAvQAwAIkAcECKAHAAisBwQIvAcACMgHBAjYBwAI4AcECPAHAAjwBwQJAAcACTQHBAlEBwAJUAcECWAHAAmQBwQJrAcACeAHBAn8BwAI4AtICXALAAmsBwQJyAcACMQLBAjgCwAKfAMECpgDAArMAwQK6AMAClwLBAp4CwAKsAMECswDAAq0CwQKzAsACYgDJAngAwAJmAsMCaQLBAl8CyAJkAsECIwLIAiQCwQKPAM0CnADCAigCxAIxAsICKALEAiwCwgIlAsYCKQLCAmoCxwJvAsUCYgLKAmoCxQJpAsoCcALFAosBzQKQAcYCHgPMAh8DxgIkAtECJwLGApAAzgKUAMYClQDTAqIAxwIhAs8CJQLJAu8CygL2AskC9gLKAv0CygL8AtEC/QLMAh4D0wIfA8wCFAHZAiABzQKMAdQCjgHNApEA1AKSAM4CIwLVAiQCzwLOANwC3ADQAooC2AKPAtEC+gLYAv4C0QIkAtwCJwLRAj4B2wJIAdECWgLYAlsC0QLzAtYC+ALSAqwA3QK3ANICGgHUAh4B0wIcA9oCIQPUAo0B2gKOAdQCkQDbApIA1QIjAtwCJALWAu0C3gLyAtcC+gLfAv4C1wKLAt4CjALYAlgC4AJdAtkCiwHhApAB2QIcA+ECIQPaApAA4QKVANsC9ALgAvwC2wIhAuMCJgLdAu8C3gL2At4CiwLlAowC3gJbAuUCWwLfAgED5wIGA+ACqADmAq4A4ALwAOYC9wDgAiIB5gIoAeACcAHmAnYB4AIcA+gCIQPhAo0B6AKOAeECkQDoApIA4gJSAe8CaQHiAiEC6gImAuICKgHvAkAB4gKyAO8CyQDiAtYA7wLtAOQCQALqAkgC5QKLAvMCrgLlAloC7AJcAuYCLgHrAjUB5wIDA+4CBAPnAh4D7gIgA+cCjAHuApAB5wKQAO8ClQDoAvcA8QIAAegCOgLtAj0C6AJOAusCVwLoAigC6wIzAugCMwLrAj4C6AJOAusCUgLoAksC7AJPAukCegD4AokA6QIhAvACJgLpAuMA7gLqAOwCWAL0Al0C7AJKAvgCTQLsAgED9AIGA+0CoQDzAqcA7QL0APMC+gDtAh0B8wIjAe0CeAHzAn4B7QIfA/QCHwPtAowB9QKQAe4CkAD2ApUA7gI4AgQDSgLvAiMC9gIkAvICWAL6Al0C8wIBA/sCBgP0Ah4D+wIgA/QCjAH8ApAB9AKQAPwClQD2AiMC/QIkAvYCwwL6AsYC9gLOAvkC2QL2AtIC+gLVAvcCvwL6AsoC+AJKAgMDTQL5AlgCAQNdAvkC1QL+AtwC+QLiAv4C6gL5AukC/gLwAvkC8AL+AvcC+QL2Av4C/gL6AsMCHgPVAvoCHAMCAyED+wKMAQIDkAH7AtwC/ALiAvsC/QL8AgQD+wKQAAMDlQD8AiECBAMmAv4C8AIIA/kC/gLlAggD7gL/AvQCBgP4AgADJAIFAysCAAMxAgUDOQIBA2wCFwN9AgEDHAMJAyEDAgONAQkDjgECA5IACQOTAAIDsgAPA8kAAgMrAgMDMgICA9YAEAPsAAMDKgEQA0ABAwNSARADaQEDAyIBCQMoAQMDcAEJA3YBAwMhAgsDJgIDA0kCCgNKAgQDdgEUA4YBBAOoAAoDrgAEA/AACgP2AAUDwAAMA8UABwNWAQsDXQEHA+AADAPnAAcDDgERAxgBCAMcAw8DIQMJA40BEAOOAQkDkgAQA5MACgMhAhIDJgINA4EBGAONAQ4DjQAiA58ADgOMARYDkAEPA5AAFwOVAA8DfQEiA48BEAMdARYDIwEQA3gBFgN+ARADIQIYAyYCEQOhABcDpwARA/QAFwP6ABYDjQEdA44BFgOSAB0DkwAXAyMCHgMkAhkD1AIdA9wCGQPaAh0D4gIaA2UBIQNtARoDbAEhA3QBGgNyASEDegEaA3kBIQOAARoDfwEhA4cBGgOFASEDjQEbA8gAIQPPABsDzgAhA9YAGwPVACED3QAbA9sAIQPjABsD4gAhA+kAGwPoACED8AAbA+4AIQP2ABsD9QAhA/wAGwP7ACEDAwEbA+ICHAPpAhsD6QIcA+8CGwMdASADJQEbAzEBIAM5ARsDOAEgAz8BGwNFASADTAEbA1kBIANgARsDXwEgA2cBHAMkAiADLAIcAysCIAMyAhwDMQIgAzkCHAM/AiADRgIcA1MCIANaAhwDWQIgA2ECHANgAiADaAIcA2YCIANuAhwDbQIgA3UCHAN0AiADewIcA4ECIAOJAhwDlQIgA50CHAOcAiADowIcA6ICIAOqAhwDqQIgA7ECHAO2AiADvgIcA5MAIAObABwDmgAgA6EAHAOnACADrwAcA7sAIAPCABwDwQAgA8kAHQMWAR4DHQEdAyoBHgMxAR0DPgEeA0UBHQNSAR4DWQEdAyQBHgMqAR0DTAEeA1IBHQM4Ah4DPwIdA0wCHgNTAh0DegIeA4ECHQOOAh4DlQIdA7ACHgO2Ah0DRQIeA0wCHQOIAh4DjgIdA70CHgPEAh0DoAAeA6cAHQO0AB4DuwAdA64AHgO0AA==",
        [2] = // Закавказье — 261 boxes
            "bgEDA3wBEQOPAdQClAHXApMB0AKkAeACrQEiA7wBMwOtAZ0BsgGfAa4BnwGvAaQBrwGkAbEBqQGwATIDsQE4A7ABqQGyAa4BsgGZAcEBpgGyAawBtwGuAbcBqwG8Aa0BvgGYAcMBmgHCAZgBxAGeAdIBxALTAckC0gHyAtQB9wLTAb8C1AHEAtMByQLYAcoC0wG6AtQBvwLTAQ0C1gESAtQBCwLYAQ0C1AG1AtUBugLUAe0C1gHyAtQBtALZAbUC1QHcAuQB8ALVAQ4C5AEdAtUBagPaAWwD1QFlA9cBagPWAcMC2QHFAtcBYAPZAWUD2AG2AucBwgLYAWAD3gFiA9kBtQLeAbYC2QHEAuIByQLaAWMD6QFwA9sB1QLdAdkC2wEcAt4BIQLdAZgB4AGeAd0BiwHgAY8B3QGPAeABlQHdAZQB3wGYAd4BHgLjASEC3gG1AuQBtgLiAcsC5wHMAuIBHALnAR8C5AG2AukBtwLmAW8D6wFxA+cBxgLoAcsC5wERAuoBFgLnARoC7AEcAucBwQLoAcYC6AG8AukBwQLoAbcC6QG8AugBiQH/AZ0B6QEVAuwBGgLrAWwD7AFxA+wBZwPuAWwD8AHwAvQB8wIKAhgDDgIdAw4CCgMYAhcDEAIAAxUCAQMVAgADFgIFAxYCBQMWAgoDFwIWAxgCGwMuAjgCMAI6AkkCuQFYAsQBYAJKAmcCTgJmAkwCagJOAmkCTAJvAlACjwIhA5kCKwOtAlgCsQJfAq0CXgKuAmICrwJTArMCWQKwAiECswIlArECSgK1AlECsQJQArMCVAKxAsUBtALJAbYCDAG+AhQBuAIOAb0CEgG4AtABvALTAb8CxwHSAtsBwAKcAMICoQDCApcAwwKcAMMClgDIApkAxAKmAMkCqgDFAsMBygLFAcUCmADWAqUAyAKzAc4CtwHKAsMBzALIAc0CtQHRArcBzgJoAdYCcAHQAtcB0gLcAdUCnQDaAp8A1wKpANwCqgDZAroA3gK6ANkCugDZAr8A2QLQANkC1QDZAtUA3gLVANoCywDdAs8A2gKeAN8CoADcAqUA3gKqAN0CoADfAqUA3gK5AOMCugDeAtUA4wLVAOACvgDqAtAA4QLoAOYC6gDjArkA6AK6AOMC1QDoAtUA5ALrAPcC/gDmAugA6gLsAOgCuQDpAr4A6ALQAOkC1QDqAugA7gLsAOwC6gDwAu4A7QLoAPIC7ADwApsA9QKbAPACowD1AqMA8AKjAPACqADwAqgA9QKoAPEC/QD1AgEB9AKaAAYDpAD1AqgA+gKoAPYC8AD6AvQA9wL3APsC+wD6AvMA/gL3APoC9gD+AvsAAQOoAAYDqAAGA6MABgOoAAYDogALA6MACwOeAA8DowANA9IAGAPlABAD5AAQA+oAEAPpABYD6gAVA+gAGwPpABYDzQAXA9IAFgPNABwDzgAaA4oAGgOPABoDjwAaA5QAGgN/AB8DgAAaA4AAGgOFABoDhQAaA4oAGgOUAB8DlAAbA+gAIAPpABsD4wAfA+YAHAPNACEDzgAdA9wAHwPgAB0D4AAhA+MAHgOpACIDrQAeA60AIgOxAB8DfwAkA4AAHwOCADQDjgAfA5QAJAOUAB8D5AAjA+gAIAPNACED0gAhA9IAIQPYACEDpQAlA6kAIgOwACYDtAAiA98AIgPkACMDogA4A7cAJAN/ACkDgAAlA6IAKAOmACUD/wAyAwUBKAOeACwDogApA7wALgO/ACkDfwAuA4AAKgO4AC4DvAAsA50AMAOhAC4D+gAxA/wALgO8ADIDwAAuA38AMwOAAC4DlAAzA5QALwOhADQDpQAyA7wANgPAADMDfwA4A4AAMwOkADcDqAAzA5QAOAOUADUDuAA5A7wANwOoADsDrAA4A48AOAOUADgDigA4A48AOAOEADkDigA4A38AOQOEADkDtAA9A7gAOgOsAD8DsAA8A8sAQQPMADwDzAA8A9EAPAPYADwD3QA8A9wAQAPgADwDsQBAA7UAPQPPAEED0gBAA+AARAPjAEEDywBGA8wAQgPdAEYD4QBEA+MASQPkAEUDywBPA9kARwPaAE0D4wBIA4MATAOHAEgDhwBKA4wASQOMAE0DkABJA+MATgPjAEsDoQBQA6EASwOhAEsDpwBLA6cASwOsAEsDrABMA7EASwOwAE4DtQBMA38AUAODAEwDjwBQA5MATgO1AFMDtgBOA9kATwPeAE4D3gBPA+MATwOSAFMDlgBPA3wAVAOAAFADoQBVA6EAUQN/AGEDkABTA5IAVwOWAFMDfABXA4AAVAOgAGUDrgBWA48AWwOTAFsDBwFgAwoBYwMLAWYDDAFkA6EAagOiAGUDBQFpAwgBZQMAAWgDBQFmA9oAawPbAGYDxgBnA8sAZwPGAGwDxgBoA/wAawMBAWgDzABtA9UAaQPIAGwDzABpA6IAagOnAGkD/AB8Aw0BaQMHAW4DCgFpA6cAagOsAGkDoQBuA6MAagMQAXADEwFqA/gAbQP8AGsD2gBwA9sAbAMLAXIDEwFsA8YAcQPGAG0D+AByA/sAbgMKAXIDDQFuA6MAcwOmAG8DEwF1AxYBcAPWAHED2wBwA9AAcQPWAHADywBxA9AAcQPGAHEDywByAw0BdgMQAXMDpQB3A6gAdQMWAXsDGQF2AwwBeQMQAXkDBwF8AwwBegMAAX4DAwF6AxkBgAMcAXwDAwF/AwcBhAP+AIQDBQGEAwUBhAMLAYQDCwGEAxEBhAMRAYQDGAGEAxgBhAMeAYQD+ACEA/4A",
        [9] = // Кан — 792 boxes
            "PgB3AlQAjQJOAKUAZAC7AFQAhANyAKIDWAA2A2QANwNZABQDbgA4A1oA2AJlAOQCWwDlAmYA9wJbAEkDZwBKA1wAGwFqADUBXAA+A2gAPwNcAF0CZwB1AlwA9QJnAA0DXQBLA2kAVgNdAHIDaQCHA10AigFuAJwBXQBQAngAXgJdAFgDaQBqA10ANQFpAF8BXQBhAWsAewFeAHsBaACKAV4AugFuAOABXgDfAWwA+gFeACsCaQA6Al4AHAJpACsCXwDfAGgA8QBfAA0CaQAcAl8AtgBpAN4AYAD7AWkADQJhAA8BbgAcAWEAzAJuANkCYgBoAG8AegBiAHQAeQCJAGQANQNpAD8DZQAxAnMAPwJlAA0DZgAYA2UA4wFuAOoBZQCEAHIAkQBlAA4CcAAbAmcATgJvAFQCZwBXAm8AXgJoAD8DaABLA2wAYAB+AGgAbQB9A3oAigNuAJIBfQCcAW8AUAJ4AFcCcABXAngAXgJwAAICgwASAnEAiQODAJQDdgBUAnwAWgJ3APsBhgAMAngAUAGFAF0BeADIAZEA8AF7AB0CggAkAnsAuwGGAMYBewCeAogAqwJ8AAoCkAAzAn8AsgGCALkBfwAoAoYAMAJ/AFsBigBqAX8AagGOAHQBfwCsAY0AugF/AF8AqABpAIAArgGUAMsBgQCIA6IAlgOCAN8AlADoAIIA6AKUAPECggCQApMAowKCACkBiQA6AYIA/gCRAAkBggCBAowAkAKCAAkBjQAYAYMAGQGLACsBgwC/Ao8A6QKDALQCnQDCAoMAEgOsABsDhAB3ApQAggKIAFoBkgBoAYkALgGgAEABjgBjAaEAdAGOALIBlwC8AY8A/gCcAAoBkQCAApsAiwKRAFoBoABkAZIAmQKhAKMCkgDeAKEA6ACSAIcCogCbApMAdAKhAIIClADhArwA8QKVAP4AnwANAZYAtQKvAMIClwAdAZ4ALgGXAA0BngAfAZkANQKoAEMCmgDSAKcA3wCaADoBpwBGAZ8AgAKmAIgCoQCIA7YAlAOoAGcAswBoAKoAwAK5ANICqwASA7oAHAOtAPQAugACAbAAzAK/AOACsgCUAcAAogGzAFoAwgBoALQAbwHCAHwBtQCCA9sAkgO2AHwCzQClArYAdgLFAIACuADoAsUA8gK5ABsByQBBAbkA3gLDAOkCuQBAAccAWwG6ABsBxwAoAboAbwLIAHwCuwD6AMkAHAG7ABIDzQAbA7wAWgHLAGQBwQBgANAAagDEAAgD0QAVA8UAdgLUAIACxQChAtUAswLHAJEB1wCcAcoA+gDcAAMBywCsAugA1gLPAGEA3QBrANAAUALiAFoC0gBaA+AAZwPVAHUC5wB+AtcATQPkAF8D1wBZA+0AbgPZANEC6ADlAtkAXwDeAGgA2gCEA/UAkgPbAPsA6AADAdwARgPrAFQD3ABlAOYAagDeAO0C6AD3At4A4ALtAPAC4QCiAO8AsQDjAFoABwFxAOYAdgL1AIAC5gCMAf8AmAHoAAMB/AAdAekAugDzANYA6QCFAPMAoQDpAPoA9gAGAeoAXgH8AG8B6gCnAPMAuQDrAKEA8wCqAOwAzwL1AOEC7ADiAvcA8QLsAIAC9QCoAuwAqAL1ANAC7gArAfgAOgHvABwB+AArAe8AfAD8AIkA8AA6AfkAYgHyACQB/gAvAfQA4gICAe8C9gCHAwgBkAP3APcABAEEAfwAXwEFAWkBAQFeABwBbQAFAaADGwG2AwgBegIQAYICCQEgAxABKAMJAYcDGAGRAwkBcgIWAX8CDQHJAiAB4wIPAZIAGgGqABABvQAeAdgAEAGpABwBvgAQAYQAHgGSABABMQMdAT4DEQF5ACABhAARAYYCGAGOAhEBfAIuAYYCEQHrAiQB/AISAdgAJgHkABIBMAEpAUIBEgGoAiUBuAISAbkCGwHLAhIB4AIdAe8CEwGKASABlwETASwBHAE1ARQB+gAhARQBFAFfAyQBcQMUAU4DHQFgAxQBPQMnAU0DFwEkASsBOgEXAVEBLAFwARcBQgEiAVIBFwGHAyYBkQMYAYcCKAGXAhgBhgI+AZYCGAEfAScBLQEaAQgBRAEUARwBYwA0AW0AHQHaAjkB9QIgAXkASAGCACQBqAI2AbACJAFnAzMBcQMlAdIANAHgACYBhwM2AZEDJwE9AzABRAMnASUBOwE6ASwBZQE7AW8BLQF8AjYBhAIuASMBSwEwAS8BPAM+AUcDMAH+AkcBFAMyAf4AQAENATIBbgM6AXYDMgHEAEgB4gAzAb8CVgHiAjMBZgNCAXEDMwF8AkUBhQI0AYYDXgGSAzYBogJcAbICNwEZA0QBJgM7AWEBSgFvATwBEANLARoDPgG+AEgByQA+AYUCWQGTAj4BPANNAUcDPwGjAFIBwQA/ARwBTQEqAT8B9gJQAQYDQQFoA2oBcQNGAXwCYwGGAkcB7AJbAQADSAF6AFcBhABIAZQAVgGmAEgBGgBeATAASQENAVYBGgFJAWQBYQFvAUoBKQFTAS8BSgEOA1oBGANNATABXQFCAU0BPANcAUYDTgF+AF8BmABPAbQCYgHHAlABoABdAa4AUQFcAGEBaABRAdwCagHzAlQBPQFkAU8BVQGtAGIBugBWAYADYAGLA1cBgwFqAY4BWQGIAmsBkAJZAQ4DaAEYA1wBPAOEAUUDXAFJAWwBXQFcAaYCaQGuAl0BdABnAX0AXgGFA3cBkwNeAa4CaQG6AmEBXAByAWgAYQFqAWsBbwFiAXwCcgGPAmMBkQJrAZgCYwFaAXMBbAFjAdQCdAHkAmUBCgNvARgDZgGkAnMBsQJoAasCdQG4AmgBrQOGAcoDaQFmA3gBcANqAYIBdAGLAWsBwAKJAd0CbwEZAXkBIwFwAQ8DggEYA3EBAQGMAR4BcwFlAYABcgF3AYYDhgGQA3oBaAOMAXEDfAHzAIsBBgF8ARcBkAEqAYABBQOIAQsDgAHbAI4B6ACAAQcDkwEYA4IBoACMAaoAggElAZcBPwGDAT0DlAFQA4QBzwKTAfYChAGMA5ABjAOFAVsAnwFoAIgB0gCZAeAAiQHEApMB0wKJAVADkwFfA4oBpwCcAbgAigG4AJMBygCKAcQAlAHTAIoBSQGVAVQBigFwAJQBegCKAV0DkwFvA4oB9wKTAQkDiwH2AJ4BAQGNATwBlgFIAY8BhwOkAZMDkAF7AJwBhwCRAUEBpQFbAZQBwAOsAdkDlgE5A50BQAOWAdcAwAHoAJgBeQCxAYwAnAGnAMUBsACeAVIByQFiAZ4BWQDAAWgAowGHA7IBlQOmAYUAtQGUAK0BeQC2AYEAsAFwA70BfQOzAX8AxQGLALYBRwPDAVMDtgFgA8cBcwO3AT0DwQFMA7cBUQPBAWADtwHCAsoB0wK3AQYDvwEPA7gB1ALAAeYCuAEPA8IBHgO4Af0C1AEHA7oB8gLIAQADvQH2AMUB/QC9AeEA0AHsAL4BeADMAYcAvgH8AOcBFAG/AVkA4AFoAMAB2gLPAeUCwQE9A+kBRgPDAX0A0QGLAMMBFQPVAR4DxAGpAM0BuwDGAWgD1gFyA8YBTAHiAV4ByQHCAtsBywLMAagA3QGvAM4B5ADhAe8AzwHaAt4B5QLQAbMD6QHLA9ABEAPaAR4D0QHAAOMB1wDRAX4A5QGKANEBGQPaASMD1AH9AvwBBgPVAQIB5QESAdUBZgP7AXYD1wGwAN4BwgDaARMD9AEhA9oB1gDiAecA3AHCAgQCywLcAcUA5wHQAN4B3QLmAeQC3gFaAPABbADfAVwABAJsAOABSQH9AVwB4QEIAQUCHgHkAQAB8QENAeQBfAAAAooA5QGJAw8ClAPmAdoCAgLlAugB6gHvAfIB6AE9AwoCSwPrAYwA9QGWAPABnwD9Aa0A9QEWAwcCHgP3AbsABgLKAPgBTAEFAlkB+gFIAQoCVAH7AWMDFQJxA/wB/QILAggD/AHBAAgC6wD8AacACgLBAP0B6gAPAvgA/gF9AA8CiQD+AacAKAKzAAACdAENAoEBAAIQASQCJgEDAtsCFgLkAgQCwgIUAtUCBQJvABICfAAGAhQDFQIfAwYCVQMVAmQDBwI+AR8CVQEHAvsCGgIUAwkC0AIWAt0CCQJBAxUCVgMMAu8ANQICAQ0CiAMmApUDDQI3AxoCRAMRAn0AHQKJABICOQE4AlYBFAIVAyECIgMbAl0BQwJ3ARwCoAAqAq4AHAJ+ACwCiQAgAhcBPQIqASECigAuApcAJAKmAD4CtAAnAmUAKQJnACkCWQBOAmkAKgJmACwCaAAqAlkAOQJoACwCZAAuAmcALwLQAjcC2AIxAjoBSwJJATIC+ABEAgYBNAJkADYCZwA3AsQCRALSAjcCZgA6AmgAOgIWA0ICHgM7Ah8BTAIsAT0CDANHAhsDPQLkAkYCDAM9AtUCSALkAj4CpwBIArEAPwLUAFEC5gA/Al8DUAJyA0ACUANKAl8DQAI/A0kCUQNBAvwAWgIMAUICaABEAmoAQgLfAFQC8QBCAs0CXALeAkQC5gBfAggBRgJmAEgCaQBHAiEBWwI3AUcCDgNWAhwDRwI+A1kCRgNIAjIDVwJBA0oCNAFXAkUBSgJmAE0CaABOAl4BWwJrAU4CZgBRAmgATwJpA3gCcgNSAmUAVQJnAFUCXgBkAmgAVQITA20CHQNXAngAZQKGAFgC1gJqAuQCWAJ/A2YCjQNaAnwAfgKSAFsCfgBbAooAWwI+A2oCSANfAsICZwLIAl8CMANnAjcDZAJVAH4CaQBlAlEBgwJiAWYC2gKFAu4CaQK0AHcCxQBpAj4DkgJHA2wCwwB5AtQAbQIQA4ECHANxAtMAfgLmAHIC4gCGAgABcgKnAJACvAB1Ao4DjgKmA3cCaQOGAnMDeAKvAIwCwgB6AoIAhgKLAHsC/ACJAg0BfwIyAYcCOQF/Am8AjAJ8AIACDwObAh0DggLlAowC7wKCAmQAhQJmAIICpwKVArAChAKwA50CyAOFAggBlwIYAYYCiwCSAosAhgJoA5UCcgOHAoYDlgKQA4gC5QKmAvoCiwJlAI0CaACOAqwAnwK4AI4CZACQAmcAkQJ9AKACjACRAj4DoAJIA5ICPQCoAlMAkgJmAJUCaACTAqgCogKzApMCDgGkAh0BlAKJAKICmACVAmgDpAJyA5UC+QKkAgcDmAIZA6UCJwOYAjUDpwJEA5sCBwOnAhsDmwJmAJ4CaACdAkYBsAJTAZ8CZQCiAmcAnwJ/AKsCiwCgAnIArgKAAKACPgOvAkgDoQIUAcoCKgGhAqwArQK4AKEC/gGrAhoCoQIlAqkCLQKiAvEBqwL9AaICGQKiAiUCogJmAKUCaACkAmoDrAJyA6UC4QG3AvEBpQIBA7ICDwOnAhEDsgIbA6gCPAG3AksBqgIqAsQCPwKrAn8AvwKLAKwCrQDAArkArAKmArYCsQKsAmEDvwJyA68CPQO/AlADrwJAAcACTgGyAs4B2gLqAbYCTwO/AmEDuAKIA80ClAO9AqUA4AK7AL0CbAPKAnkDvQIMAckCGAG+AjIC0gJGAsIC/gHOAgoCwgIoA8gCMAPCAggC0wIbAsIC+AHSAgMCwwKYA9kCrgPFAoAA1AKKAMYCGwHeAjEBzAI3AuoCUALMAv8A3QISAc0CiAPoApYDzQIQAeECIQHNAi8B2gI8AdACRwPaAlED0AKKANoClADRAvkB4AIDAtQCEgLmAhsC1gJUAOMCYgDWAsQB6gLVAdYCUgHtAmoB2AIUA+UCIQPZArcA6ALGANkCSgPiAnMD2gI8A+MCSwPaAmgA3AJrAN0CagDgAmwA3QIKAvACGwLeAg4B8gIbAd4CAwPsAhQD3gLoAgUDBQPfAsAA8QLYAN8CrADjArgA4AL4AfACCwLgAg4D8QIeA+ECaADjAmsA4QJmA+4CcgPhAkYC9QJZAuICPAP0AkUD4wK8AfYCzAHkAmMB+wJ8AeUCGAPzAioD5gLUAPcC6QDoAmoA6gJsAOgCgAPwAocD6AKKA/QClQPqAlwAAANyAOoCYQMRA3AD6gIUAfcCIQHsAt8A/wL5AO8CCAEDAxYB8AJNAgkDYgLwAtcB/QLkAfACLAL9AjoC8QK5AfwCxAHyAokDAwOVA/MCHwMCAykD9ALXAP4C4QD1AvUAAQMIAfUCPAMdA0UD9QLhAQID7gH1AioCBwMyAv0C4wEPA+wB/gKCAAsDkgD/ApYAJwO+AP8CIQIOAy8CAAPgAhID8QICAyADEQMqAwMDWgITA2oCBAOUAAUDoAAFA5cADgOgAAUDjAMUA5YDBgNHARwDZQEIA1UAGwNnAAgDKQIYAzQCCwOOAhgDmwILA9YCHwPnAgwDZgM1A3IDDQNgAiADcQIQAz4BGQNIARAD4gEgA+0BEAMIAhgDDwIRAxwDKgMpAxQDYQEeA2wBFAOBAygDlgMXA2gBJwN6ARkDMAEfAzgBGQOtACkDvQAaA1wAKQNnABoD0AImA9sCGgNoAi0DeAIdAzwDOgNHAx0DQAImA0gCHwN1ASoDgAEgA9ABSAPZASADxAIqA84CIAPGAjQD2AIhA7UAOAPLACQDCQIxAxYCJQOGAzsDmAMmAz8CQANMAigDHANRAygDKQNcADgDZwAqAxQCRgMfAi0DZANGA3IDLQO+Aj8D0AIwA8IAQAPTADYDiANOA5IDOANdAEcDZwA4AzsDRQNHAzgDwQJKA84COgPMAE4D4AA+A+8ASwP8AEEDUQFOA14BQwM9A0wDRQNFA7cCUAPCAkYDigFXA50BRgNoAU8DegFGA3sBUQOLAUcDWQFaA2oBSAPEAlIDzQJIA1wAWgNlAEgDFQJaAx4CSAMgA1YDLgNIA4AAYwOeAEkDLwFZA0IBSQP4AFIDIAFJAyABVAMvAUoD2ABbA98ASwOJAlgDlgJLA2YATgNoAEwDPQNbA0cDTgOJA2IDlgNQA8UCbgPWAlADaABSA2oAUQP4AGIDCwFRA/gBWgMGAlIDGgNsAygDUwMbAmADKAJTA5oAYgOoAFUDTAJfA1sCVQNbAl8DagJVAyoBYgM2AVUDcgJfA48CVQNqAl0DcwJWA2UAWQNnAFYDjQFpA50BVwNeA2kDZgNYAx0BYgMsAVgDqABoA84AWAMKAWEDHAFYAy4BYQNAAVoDWgFpA2UBWgO+AGoDzgBaA2YAXANpAFoDPANrA08DXAP5AWsDBAJcAyUDaQMyA1wD1AFmA+QBXAO2AWcDxgFcA8YBZwPVAVwDzQBkA94AXQNiAWoDbwFdA0oAcwNgAF0DBgFsAxQBXgPhAnED+wJeA2cAYQNpAF4DbAFpA3sBYAP4AG0DBQFgA3wBaQOOAWADTwNrA14DYAMnAW4DNQFhA/sCawMKA2ED1QJsA+QCYgOKA24DlQNjAwgDbAMaA2MDaABmA2sAaQNnAGsDaQBrA/kBegMEAm0DiAN8A5cDcgNnAHQDaQByAyUChQMuAnMDaQB1A2sAdgNoAHkDagB5A5ABfwOYAXoDAAF/AwgBegN7A5gDmwN+A1oCpgOCAoADlgGLA58BggM0AZgDVwGEA2EChwNjAoUDNwKVA10ChQNIA5kDXgOGAyQClQMuAoYDXgKJA2EChgOPAIgDkgCGA6oAiQOtAIcDhgCJA4gAhwOvAIkDsQCHA7UBiQO3AYcDXQOkA30DhwOjAYoDpQGIAxMBigMVAYgDmQGKA5sBiAOmAIoDqQCIA7oBlQPUAYgDDwGKAxEBiAMtA5YDSAOIAy0BjQM3AYgDtQCWA8oAiAPKAJYD3wCIA7ABigOzAYgDpwGLA6oBiAOsAYsDrwGIA50BiwOgAYgDUACfA3QAiAN1AZQDhgGIA4gBlAOUAYgDoAKLA6MCiAOLAIsDjQCIAyEBiQMtAYkDFQGUAyEBiQOVAYwDmQGJA9QBlAP9AYkD/AGWAxUCiQNdAosDXwKJA/gCkwMQA4kDYQKLA2MCiQOaAosDnAKJA7oClQPMAokDFQKTAyQCiQNfAZQDdwGJA7EClQO8AokDDgOVAyADiQMhA5UDLQOKA6gCjQOrAooDrAKMA68CigORAIwDlACKA6AAjAOiAIoDkgKMA5QCigP6AYwD/AGKA5gAjQOaAIoDpQCNA6cAigP+AY0DAAKLA50AjQOfAIsDUwGMA18BiwMaA5YDJgOLA5YCjgOZAowDAgKOAwUCjAOFAo4DhwKMA/wBjgP+AYwDjgKPA5ECjAN8Ao4DfgKMA50CjgOfAowD9wGPA/kBjAOgAo8DowKMA4kAlwOhAIwDAQKPAwMCjAOkAo8DpwKMAw4BogMjAY0DbgCbA4kAjQOJApADjAKNA/4AnAMNAY0DmgKQA5wCjgOBApADgwKOA3kCkAN7Ao4DXQOeA20DjgOpAKIDvQCPA0kAlANTAJEDRgCaA0sAlwPNAakD3wGrA5sCvwOwArgD0AHNA+YB",
        [29] = // Нормандия — 1395 boxes
            "MAD7Aj0ACgM4APMCWQAQA0oA5wJcAPsCXwBfAWwAbgFhAK8AaQC0AGEA7wJnAPMCYgCCAHgApQBkALQAbAC6AGcA+wJtAP4CZwAKAXAAGAFnAOoCbQDvAmgAFwFxACYBaAD/AHkADAFoAKcBfgC7AWkAwgBrAMUAaQC5AGsAvABpAL0AawC/AGkAxgBvAM4AagC7AG0AvQBrAMMAbQDGAGsA6QJvAO4CawAJAXUAEgFsALgAbQC6AGwAAgF8AA4BbQBLAXgAWAFvALQBewC/AW8AQQF4AEoBbwBmAXwAdQFxAGYBhwB7AXIAzgB5ANUAcgAzAnoAPAJyAAkBdAAKAXIA/QGAAAoCcgCYAHkAoAByAJ0BiwC2AXIAtwGAAMUBcwAIAXUACgFzAEUBdgBKAXQAlQB2AJgAdAAIAXYACQF1APACfADzAnUAQAF9AEUBdQAGAYAAEAF2AAcBdwAJAXYANQKEAEQCdgC2AYkAyAF2ANgChADnAnYA5AJ8AOsCdgCRAHgAlAB2AFABfgBTAXYAJwKHADgCdwCZAHkAmwB3AAABkAAMAXcABwF5AAkBdwDiAnwA5AJ4ABECiAAhAngAGwKIACsCeAAGAXoACAF4AIwAgACQAHgA0QCAANgAeAB8AYIAhQF4AIQBhACQAXkAwgGRANoBeQAFAocAEgJ6AIwBkQCiAXoAvwKIAM4CegDSAYgA4QF6AJEChwCeAnsA4wGWAP0BfABxAZQAigF8ACMBjwA2AXwAowGGAK4BfADjAn4A5QJ8AOUCfgDmAn0A1gCEANsAfQDiAn4A4wJ9AOYCfgDoAn0A7gJ+APACfQDoAn4A6QJ9AOACfwDiAn0A6QJ/AOsCfQDtAn8A7gJ9APACfwDxAn4A2gJ/ANsCfgDqAn8A7AJ+AOwCfwDtAn4A9AGWAA0CfgDxAn8A8gJ+APICfwD0An4A3wJ/AOACfgD0An8A9QJ+ANQBlwDtAX4A9QKAAPYCfgAeApYANgJ+AN0CgADfAn4A1gGOAOcBfwDcAoAA3QJ/ANoCgADcAoAA3ACWAPIAgAAUAosAHwKBAJwBlACwAYIAkQKUAKUCggClAZsAvwGCAD0CkQBOAoIAgQKUAJQCggC5AZIAygGCAEECjgBNAoMAFQGPACIBgwAGApEAFAKEAHECmQCIAoQAKQGQADUBhQB/AZwAlwGGAAgCoAAhAocA+gCcABQBiADEAaEA3gGJAHQBoQCOAYkAiwKZAJsCigCWAqAArgKKABsCpAA1AooAWAKXAGQCiwAwAqUASQKLAFYCmwBmAosA7QKaAP0CiwBHAp8AWgKLAJUBpACuAYsArQGVALcBiwDQAaUA6QGMAO4BoAAEAowAaQKYAHUCjACJAZoAmAGMAIICnACTAowAaAKZAHUCjQDnAZwA9gGPAN8BqQD4AY8AsAGcAMsBkACWAZoAoAGQAKsBoQC6AZAAhQGoAJsBkADBAaEA0AGQAMcBnwDVAZEAVQKlAGgCkgADAqsAHQKTAHABoAB/AZMAGAKkACkCkwAcAp0AJQKTAHYCoACEApQAKAKtAEIClACQAqYAoQKUAFQBogBjAZUAWQCtAHIAlQD+AZ8ACAKVAKcBnwCxAZUAZwKpAHsClgA+AqQATQKWAJsCogCnApYAQAKiAEwClgACAqAADAKWAP0BoAAHApYAFQGYABwBlgAcAZgAIgGXABQDpgAkA5cAIgGZACgBmACFAq4AmgKYACgBmgAuAZgATAKtAGECmQAuAZsALwGZAHMBpAB+AZoAtgGfAL4BmgByAqYAfwKaAC4BnAAxAZsAVgKrAGUCmwCMArUApwKcABQCqAAgApwARAKsAFQCngAWAqcAIAKeAC8CqAA5Ap4A0wGgANQBngBkAq8AdQKfANMBogDaAaAA2AKuAOgCoAAiA7AAMgOhAHkCsACKAqEA2QGiAOABoQBDAaYASwGhAFgCswBoAqIA4AGiAOYBowBlArEAcgKkAEcBqABMAaQAiQKzAJcCpABCArMAUAKkAEwBpwBNAaUAQQKxAE0CpgBWAbAAYAGmADUCqQA7AqgAXQK0AGoCqADbArEA5QKoAIcCtACUAqgAXwGwAGUBqQA6AqsAQQKqAMoBuQDZAaoAQAKtAEcCrABcAa4AYgGuALcBvQDGAbAA1AG8AN8BsQAzA70APwOyAL0CvADHArMAtQLBAMQCswDMAb8A2QG0ALIBtwC4AbUAuAG2AL4BtQCsAbgAsgG4AO0AuwDwALgA/wHFAAsCuAArAboAMgG5AAMCuwAJArkACgLFABUCugAyAcMAOwG6ADIBvAA4AboA7AC8AO4AuwAIArwADwK7AA8CvAAVArwAOAG+AD4BvQAtAssAPAK9AGwBzgB9Ab0ArwHMAL0BvgA9Ab8APwG/AIwBygCYAcAAcgHEAHgBwQABAs0ADQLCAAcB2wAUAcMAQAHRAE0BwwCmAdQAtwHEAG0ByABzAcQASQHLAE0BxAARAtQAIQLGAGQBzgBpAcYArQHeALgBxgCAAtsAlgLGADUD1gBEA8cAgwHSAI4ByABoAcwAbQHIAGUC0ABrAskAaALUAHMCyQCaAtQApALJAI0B1gCZAcoAbALMAG8CygBqAswAbALKANkA0gDfAMoANwLWAEQCywBnAc0AaQHLAHAB3QB/AcsAggLOAIMCywCxAdoAwAHMAKQB4wC7AcwAcALNAHYCzAB2As0AfQLMANsB2ADmAcwAfALOAIMCzABlAdIAaAHMAGoCzgBxAs4AsgLcAMECzgCTAt4AogLPAEwB1QBUAdAA8gHfAAAC0ADeAd0A6wHQACgD4gA6A9AAaAHbAHIB0QDIAeEA2AHRAB4D5AAyA9EApwLnALwC0wCAAt8AjQLUANwC3QDmAtcAGwPmACsD1wDfANoA4QDXAG4B5wB+AdgA2ADaANoA2ADYAOAA3gDYAOEA2wDjANgA1gDbANkA2QD3AukABwPaABgD8gA3A9oA3wDcAOIA2gBHAv8AawLaALMC8ADHAtoAUALnAF0C2wAyA+gAPgPbAKAC8QC1At4A3wDgAOEA3gBsAekAdwHgAFAB6wBZAeEAWALwAGgC4QAsA/IAPQPiAMcC6ADKAuIAagLuAHUC5ABsAvAAeALlAPICCwESA+UAqAL4ALoC5gAfA/QALQPoAPEC+AACA+gAyQLuAMwC6QCLAQIBpAHpAJIA/ACmAOkAKQH6ADoB6QC9AvcAzALsAJwB+wCrAewAjwL6AJwC7AD1AvwABQPtAGYC+gByAu0AlgL6AKIC7QCrAf4AvAHuAKwC/gC8Au4AygL0AMwC7gBxAv4AgQLuAFYC+QBiAvMAogEDAbIB9ABvAfoAcQH0AH4BBgGSAfQAygL7AMwC9QAIAwUBGAP2AIcBEgGpAfcAAgMIARMD+AC+AgQByQL4AGwBAAFwAfgA+wIKAQwD+gBwAQABcgH6AMYCAAHKAvwAbgEFAXgB/QB3ARABigH9AOICCQHuAv4A3gIOAe4CAAGEARABlAEAAVgBBgFcAQABcQEGAXQBAQHVAhYB8AIBAdsCDQHnAgIB0AITAeICAwFMACkBbgAEAckAEwHSAAQBXQEMAWABBAGqARABtwEGAVwBDAFdAQYBVAESAWABBgECAxYBEgMHAXQBCQF1AQcBfAEYAY0BBwHHAhkB2QIJAQ0BIgEbAQoBDgELARABCgENAQwBDgEKAQsBDAENAQsBqwEXAbYBCwEKAQ0BDAELAVABGwFgAQwBRQMcAVUDDAFcARIBXQENAcYCJAHgAg0BCwEOAQ0BDQGwASABwgEOAdoCHAHqAg4BpAIeAbMCDgELARABDAEQAZ4CIAGwAhABrwIdAbwCEAEKARIBDAEQAQ0BEgEOARIB1gIdAeECEgGnAh4BtQISAQoBFAEMARIB0gIiAeMCEwGeAiIBqwIUAQ0BFQEPARQBSwIjAVoCFAEKARUBDAEUARsDJAEsAxUBvwIlAc8CFgFHAiQBVwIWAQoBFwEMARcBvgEjAcsBFwEMARkBDgEYAQoBGQEMARkBCwEbAQwBGgEPARsBEQEaAQwBHAEOARwBVQIpAWICHAENAR4BDwEcAYgAIQGMAB0BDwEfAREBHgHEAi0B1AIeAcoCKAHUAh4BEQEgARMBHgHfACIB5AAeAYwAIwGQAB8BZgIuAXYCHwESASEBFAEgARQBIgEWASABFgEiARgBIAHdATAB7QEgAeMAJQHoACABRwIuAVQCIQFwAzEBgQMiARUBJAEXASIBhgAmAYoAIwF+Ai4BiQIjASkDNAE3AyMB4wEzAfQBIwHiACcB5gAkAcEBMwHRASQB3gAoAeIAJAGNACgBkAAmAT4CPQFVAicBbQMyAXkDKAEIAjgBGAIoAfQCKgH7AigB+wIpAQEDKAGIAi4BkAIoAQADKwEHAykBOwI4AUoCKQHvAi0B9QIqAQQCOAETAioBnQE7Aa8BKgF9AjABhQIqAeoCNwH3AisBBgMvAQwDLQEKAy4BDAMuAf4CQgEUAy8BDQI/ARwCMAESA0IBIgMxAXEDPAF8AzIBRAJEAVcCMgH/Aj4BCwMzAWQDSAF4AzQBiAFMAaIBNQEEA08BHQM2AcgCQgHUAjYBNgNHAUUDOQE3A0QBQwM5AVwDSQFrAzkBJAFKATMBOgFPAk8BYwI8AV8CSwFvAjwBLAE+AS4BPAEzAz4BNAM8AXsBUwGTAT0BLQE/AS4BPQEzA0EBOAM9Ad4AQQHiAD0BKAE/ASoBPgEtAUABLwE+AfwCUwEPAz8BKAFAASkBPwG3AlAByAJAAS4BQQEvAUABWANYAXEDQAFGA1gBYgNBAeAARQHjAEEBmgFOAagBQQE3A0YBPANCAVICSgFbAkMBcgNSAYIDQwGKAVYBnAFEAfACUQH9AkQBKAJXATkCRQE7A0sBQANGAeEASQHlAEYBLgFIATABRwFsA1gBfANHAS0BSQEvAUgBXwJdAXICSAFIA1cBVwNIAU0DYgFmA0sBggFYAY4BSwFrA1kBegNLAVsBZQF1AUsB7wJbAf4CSwFZAl4BbQJNAeQAUQHpAE4BQwNaAU4DTwFCA14BUQNPAZABYAGgAVEBRANqAVwDUQGHAWEBlgFSASsDVwEwA1IBYgNfAW0DUgH3AmIBBwNTAegAVgHrAFQBYQJfAW4CVAFqA2EBeANVAZsBXgGkAVYBSwFnAVsBVgFoAmoBfAJWAS8DXAE0A1cBvAJbAcQCWAFWAWYBZgFYAYIAXAGGAFgBaAFqAXkBWwGDAF8BiABbAUgBdQFiAVsBWAFzAXABXAGFAGABigBcAR8DaQErA1wBvQJqAcoCXQFtAmYBdQJeAVgDdAFvA14BhQBiAYkAYAFXA2sBYgNgAUYBbgFTAWABiABkAY0AYQFxAnABgQJiAXcCbQGAAmIBegJ3AY4CZQGHAnIBlAJlAQADdgEQA2UBXwN/AXkDZQFwA3ABewNnAWYBdwF1AWcBLQJ2ATwCZwG/AnUBzAJoAUACdAFMAmoBUwF5AWABawFUA4MBbQNtAWIBeQFuAW4B/wF9AQ8CcAGFAoQBmgJ0AfwBggELAnYBWgOQAXMDdwFWAoQBYwJ3AYoCgAGSAngBTAGHAVwBeAFVA3oBVwN5AfIAfQH2AHoBmgKOAa8CewFcAoUBaAJ7AZECiQGhAnsBkAB/AZQAfAFQAYsBXwF9ATgDiQFFA30B9ACCAfkAfQFuA4YBeAN+AZIAggGWAH8BkwCEAZgAgAH1AIQB+QCBAZMAhgGYAIIBngKTAbACggEcA5EBKQOFAXEDlgGCA4UBVAOcAWwDhQEdA5UBLwOFAckCkwHWAoYBcwOUAYIDhgFDA4wBRQOHAbsCmAHKAogBqQKbAb0CiQH8AI4BAQGKAW0ClQF5AooBegOaAYoDiwHPApoB2AKMAf0AjwEBAYwBRAOSAUYDjQFjApgBbgKNAb0CogHTAo0BJAOeATcDjgF0ApkBfwKOAbAClwG3ApABXwOfAW0DkAHOAqAB2QKQAZAAlQGVAJABQAKcAUsCkgGSAJYBlwCSAbgCoAHGApIBRQOYAUYDkwFIAqMBWAKUAYYBoQGTAZQBkgCYAZYAlAFbA6ABaAOUATwCogFKApQBNAOrAUoDlAFwAqQBfwKUAWIDqQF5A5UBVAGWAVUBlgE+A6EBSQOWAVUBmAFXAZcBMAOoAUEDlwFWAZkBWAGXAYACogGLApgBxwGkAdMBmAE7A6QBRwOYAXIDpgGBA5gBVwGaAVkBmQFZAZsBWgGZAakAnQGtAJoBWgGcAVsBmwFbAZ0BXAGbAb4BqgHOAZwBXAGeAV4BnQE4A60BSAOdAV0BnwFfAZ4BVQO4AXADngFeAaABYAGfAasAogGuAKABXwGiAWABoQGOALABnwChATwCrAFIAqEBJwKvATQCoQFfAaMBYQGjAWABpAFiAaMBHgGnASIBowGQArABnAKkARsCtAEsAqQBYQGmAWIBpAEMArQBHAKkAXMAqAF2AKYBYQGnAWMBpgEeAakBIgGnAZYCsgGiAqcBYgGpAWQBpwERArsBKgKoAVsDtwFsA6gBYwGqAWUBqgGnAa8BqwGqAWMBrAFlAasBZAO+AXkDqwFkAa0BZgGsAVEBvQFgAa0BJgK6ATICrQFkAa8BZgGuAWUBxwF0Aa4BbAHBAYABrwFmAbABZwGwAS4CvQE6ArABZwGyAWgBsAEZAsABKAKwASUDwQE0A7IBqwG3Aa8BsgFWA8oBbwOyASoDwAE4A7QBcQHEAYABtAEjAsMBMQK0AVIDxQFiA7UBeQLBAYQCtQE2AsMBRAK1AS4CwQE6ArYBpwLBAbICtwF0AsYBgwK3Aa0BuQGvAbgBUQPHAWADuQHRALwB1QC5AZwCxAGnAroBWQHCAWEBugHoAMsB+QC6AS0C1AFGAroBegPKAYoDugFwAcoBfwG8AdMAwQHXAL0BOwLMAUkCvgG8AskByAK/AXoDzAGIA78B1QDEAdoAvwFlA9QBeAPAAWIAxAFmAMABcgPPAYMDwAFWA8YBWAPDATsDzQFGA8UBbgDlAZQAxQFuAccBcAHGAcgC0QHTAsYBVgPNAVcDxwGjAdABqwHHAVUD3AFpA8cBbgHIAXABxwF+A9MBiQPIAb8C0wHLAsgBbwHKAXAByAGXAM0BnADKAW8BywFwAcoBoALWAasCywFvAc0BcAHMAZoA0AGeAM0BbwHOAXEBzQFVA9MBVwPOAW8B0AFxAc4BbAPlAYIDzwFeA94BbwPPATMD2AE9A9ABbAHhAX0B0AFwAdIBcQHQASwB0wEwAdEBcAHTAXIB0QEuAdYBMgHSAWcB3QFzAdIBcQHUAXMB1AFrAeMBdwHUAS4B1wEyAdQBLwHlAT4B1AFsAuABeALVAVkD5QFqA9YBZgHoAXcB1gHaAuIB5QLYAWgC6gF6AtkBtADcAbgA2QHKAuYB1gLaAVQD6QFiA9sBtgDfAbsA2wFxAeYBfAHdAVAD7QFhA90BUQPyAWUD3QFZA+0BaQPdAXYC6AGBAt0BbALoAXcC3gFmAe0BdwHeATwD7QFMA94BrADiAbAA3gGPAe8BngHfAWQB7wFuAd8BaQPuAXkD3wHPAeYB0QHfAVUD8AFmA+ABiwHrAZcB4QGvAe0BuwHhAa4A5gGzAOIBvADmAcAA4gHQAeoB1gHiAakC7AG0AuQBswDoAbcA5AG/AOcBwwDkAVUD9AFkA+YB5gLxAfEC5gEqA/UBOAPmASoD9wE7A+YBzwHtAdAB5gHJAe8B0gHmAWQD+gF4A+YBVwP4AWgD6AGeAvIBqQLoAcIA6wHGAOgBRAPrAUcD6gF5A/UBhQPqAcQA7gHJAOoB0QHtAdQB6wFCA/EBRAPrAXwD+gGMA+sBjwDuAZMA7QHQAfMB0QHtAdEB8AHUAe4BaQHvAWoB7wFoAfEBagHxAWgB8gFqAfEBOgL0ATwC8QFCA/cBQwPxAZcABAKmAPIBVQMFAmsD8gFoAfQBaQHzAWcB9QFpAfMBAAECAhIB9AFmAfYBaAH0ATYC/AE6AvUBfAMEAowD9gFlAfcBZwH3AWUB+AFmAfcBQQP9AUMD9wE9AvgBPwL3AecCCQL3AvgBOwL+AT4C+AFcAwMCZwP4AWQDEQJ8A/gBZAH6AWYB+AExAwcCPwP5AaYAGwLKAPoBKwMLAj0D+gFkAfsBZgH7AWQB/QFlAfsB0wIWAuwC/AE1AwwCRAP9AdIBAwLTAf0BQQMDAkID/QFkAf4BZQH9AQYBAQIJAf0BWwMNAmsD/gHIAQgC0QH+AVQBGAJuAf8B3QACAuEA/wFkAQACZQEAAmMBAgJlAQMC4AAGAuQAAwLNAQcC0gEEAt0CFgLwAgQCWQMQAmQDBALQAhAC2wIFAuIACQLnAAUC5gIZAvoCBgJVAw0CVgMIAsUBDALKAQgCXQMZAm0DCQIlAxgCNQMJAsgCGALWAgkCIQMZAjIDCgLmAA4C6gAKAhADHwItAwoCwwEMAsUBDAK+ARACxAEMAukADwLtAA0C0wIZAuACDQJUAxMCVQMOAqwBGQK5AQ4C3gIeAu4CDwK5ARQCvgEQAlIDJQJnAxECGgMeAigDEwIJAyICGAMTAtkCIALlAhMCEAMjAiEDFAI7AxoCPQMUAlsBIQJoARYCYAEYAmEBFgJeAy0CdgMXAl8BGQJhARgCowEjArQBGAIiAiMCLgIYAlMDJAJeAxkCYAEaAmEBGQLSASkC4gEaAoACKAKOAhoC/QAeAgIBGgI5AyECOwMaAmABHAJhARoCiAImApQCGwL9ASYCBwIcAmABHQJhARwC3AEnAugBHAJOASwCWgEdAlYDLQJnAx0C9gEoAgICHQJgAR8CYQEeAlkBLgJmAR4CUwMqAmADHgJgASACYQEfAk8DOwJhAx8CXgEgAl8BHwLPASQC0wEgAmABIQJhASECTwMoAlMDIQIDASYCCAEhAlMBMAJfASMC9gAnAvoAIwLKASgCzwEjAncDLwKDAyQCiAEvApQBJAKKAjAClgIlAlIDNQJjAyUCaAM3AnwDJQKCAjECjQIlAl8BLwJuASYC9wApAvwAJgJ1AzgChgMoAtkAQgLwACgCxQI3AtQCKALJASoCywEoAsYCOALVAikCxAEuAskBKQJUAToCYgEpAgoBLQIOASoCagE1AnYBKgI0AzACNwMqAlABMgJZASoCYAE1Am8BKgLLAkIC4wIqAoIBOAKSASsCXwM2AmoDLAJQATsCXgEsAgkBMAINAS0CawE9AnsBLQJXA0ACbAMtAlwBQQJwAS4CvQE0AsIBLgJaAT4CZQEvAmwBQAJ6ATACvQJMAtMCMAIzAzYCNQMwAicDPgI2AzACCgE0Ag8BMAJoAT8CcQEzAvIANwL2ADMCvwE+AssBNAJhAT0CaAE0AhgBOAIbATQCgwE1AoQBNAKEATUChQE0AnYBRQKFATQCgQE2AoMBNAJRAz8CWwM1AoABNgKCATUCIgNHAjYDNQJsA0QCewM1An8BNwKAATYCGQE5Ah0BNgIiA0MCMAM2AoIBNwKDATYCcwNGAoQDNgK+AUMCygE2An0BOAJ/ATYCUQFHAl8BNwJPA0wCZQM3AoABOAKCATcCfAE4An4BNwJJAkECUwI3Al0BRQJtATgC8gA8AvcAOAJ7ATkCfAE4AnABSAKAATgCegE6AnsBOQIKAkUCFQI6AngBOwJ6ATsCJANLAjMDPQK7AkgCxQI+AgQBQQIIAT4CUgFOAmABPgI6AkgCRQJAAl4DVwJ2A0ACUANGAlEDQAJnAU8CdwFAAg8BRAITAUACfgNMAooDQAJYAUwCYwFAAl8BSwJvAUAC9AFOAgICQQJwAU8CgAFBAlcBUQJkAUICEgFHAhYBQwLCAlcC1wJDAscAagLmAEMCuwJSAsoCQwK2AlQCxwJEAg8BSAIUAUQCXQFPAmwBRgJuA1UCfQNGAoUBSAKIAUYCgAFOAoYBRgJPA0wCUQNGAhABSgIUAUcCbAFOAnMBRwIrA0gCLQNHAiwDSgIvA0cCawJSAncCSAJbA1QCZgNIAkwDTgJOA0gCBwFNAgwBSAIqA08CLANKAm8DWwKAA0oCsAFaArsBSwJ4A1cChANMAk4DTgJQA00CgAFQAoMBTgJzAVECegFOAigDVQIqA1ACkgJmAqcCUQLtAFUC8QBSAhQDXgIfA1ICtAFiAsIBVQIcA2QCKgNVAooCYAKXAlYCGANoAiwDVwIMAmUCGwJYAhUCaAIkAlgCsgFaArQBWQJ5A2gCiANaArIBWwKzAVsCDwNqAh4DWwIVAmkCIwJbArIBXAKzAVwCHAJtAiwCXAIaA2oCJwNdArIBXgKzAV0CNgJpAkECXgIXA2oCIwNeAlADagJcA14CsgFgArQBXgK0AWACtQFfAtoAbwLnAF8CpgFnAq4BXwKwAWECsgFfAp4BcAKtAWACrgFhArABYAK0AWECtQFgApUCbAKgAmEC4gB5AgoBYQKWAnECqAJhAgwCcAIlAmECtAFjArYBYgIuAm8CPgJiAoMCcgKSAmICdQN3AokDYgKiAXICsgFiArEBZAKyAWMCtAFlArYBZAK1AWYCtwFlAqwBbgK8AWUCbAJwAnkCZQJIA3QCVwNlArYBZwK4AWYCLAJzAjgCZgKIAn0CoAJnAhYBawIaAWgCDAJqAg4CaQK3AXQCwwFpAgsCawINAmkCrQF0ArwBaQIKAmsCDAJqAgcCawIJAmoCCQJrAgoCagJ5A3YChQNqAhsBbgIfAWoCSgNrAkwDagIGAmwCCAJqAi8CbAIxAmoC9AFsAvUBagL1AWwC9gFqAvcBbAL4AWoC8QFsAvIBagLyAWwC8wFrAvgBbAL6AWsCBAJsAgYCawIiAmwCJAJrAnsCdwKIAmsCnQF7AqwBawIBAmwCAgJrAkoDcQJMA2sCAgJsAgQCawLvAW0C8QFrAv8BbAIAAmsCLgJtAjACawKjAXwCrwFrAvoBbAL7AWsC/AFsAv0BawL9AWwC/gFrAiMCbQIlAmwCNgFwAjoBbAItAm0CLwJsAu4BbQLwAWwCJQJtAiYCbAI8Am4CPgJsAiYCbgIoAmwC7QFuAu4BbAIoAm4CKQJsAqkBgAK9AWwCLAJuAi4CbQJYA3gCZANtAj4CbwI/Am0CdAKEAooCbQIpAm4CKwJtAuwBbwLtAW0CKgJvAiwCbQI4AXICPAFuApkBdQKfAW4CpwF9ArIBbgLqAW8C7AFuAj8CcAJAAm4C6QFwAuoBbgJGA3QCSQNuAkACcAJCAm8C3gFwAuABbwLdAXAC3gFvAuABcALhAW8C2wFwAtwBbwLhAXAC4gFvAkICcQJDAm8C5gFwAucBbwLoAXAC6QFvAtkBcQLbAW8CQwJxAkUCbwLYAXEC2gFvAuIBcALkAW8C5AFxAuYBbwLXAXEC2AFvAkUCcQJGAnAC1QFxAtcBcAJGAnECSAJwAjsBdAI+AXACtgF+Ar4BcAK6AYECxwFwAkgCcQJJAnACSQJxAksCcALMAXsC2AFwAtQBcgLWAXAC9wF6AgYCcAJLAnICTAJxAj0BdQJBAXEC4AJ6AusCcQLSAXIC1AFxAkwCcwJOAnECSgN3AkwDcgLRAXMC0wFyAk0CcwJPAnIC4gJ8AuwCcgLQAXQC0gFyAk8CdAJRAnMCzwF0AtABcwJhAoMCcQJzAioBdwItAXMCzgF1As8BcwKuAX0CtgFzAsQBhALTAXMCUAJ1AlICdALuApoCEAN0AlICdgJTAnQCzAF2As4BdAIPAZwCKQF1AlMCdwJVAnUCywF3As0BdQIuAXkCMgF2Ap4BhgKtAXYCVAJ4AlYCdgKcAXkCngF2AsoBeALMAXYCVgJ4AlgCdgLEAXgCxQF2AqsBhAK6AXcCWAJ4AlkCdwLJAXgCywF3Ar0BiALNAXcCWQJ5AlsCdwLkAoIC7wJ3AkkDfgJLA3gCxAF5AsYBeAJbAnkCXAJ4AsgBeQLKAXgCXAJ6Al4CeAIvAXwCMgF4AsQBegLGAXgCxwF6AsgBeQJdAnoCXwJ5AsYBewLHAXoCXgJ8AmACegLDAYsCzwF7AmACfAJhAnsCQwOCAkcDewJhAn0CYwJ8AjEBgAI2AXwCMQKDAjkCfAIDA4cCEAN8AnEDfwJ0A3wCYgJ+AmQCfQJIA4QCSgN+AmMCfwJlAn4CogGPAq8BfgJeAoACXwJ/AmsDgwJzA38CtAGOAsUBfwJkAoECZgJ/AqUBiwKwAX8CRgOLAlIDgAJmAoICZwKBAv0CjwIKA4ECbQOQAnwDggJlAoMCZgKCAmcCgwJoAoICIwOOAi0DgwJvA4kCcgODAr4BjQLIAYMCRgOGAkgDhAKqAY4CuQGFAkwBiQJQAYUCsQGSAsIBhgIpA4wCKgOGAj4BigJCAYgCPwGMAkQBiAJPAYwCUwGJAqYBkwK2AYkCLAGOAjABigKyAqACywKKAioDkgIuA4oCbgOWAnoDigJ+A5kChwOLAv4ClQIIA4sCZwOQAm8DiwLxApwCAgOLAvcCnAIIA4wCUgGPAlUBjALuApcC+QKMAikDkwIqA40CRAGSAkgBjgJTAZICWAGPArgBkgK6AZECeQOhAoQDkQK6AZQCvQGRAkcBlQJLAZICIQOdAisDkwIpA5kCKgOWAlwBmQJfAZgCXgGcAmEBmAJ8A68CkgOaAm8DsAKGA5oCCQKrAhcCmgIpA6ACKwOaAl8BngJjAZwCUQOrAmADnQJhAaICZgGeAlABoQJUAZ4ChgOfAogDngKFA6AChwOeAn4DoAKAA58CSwOtAlMDoAKBA6ECgwOgAlEBowJUAaACfAOhAn0DoAJ9A6ICfgOiAlMBpgJXAaMCegOkAnsDpALdAqUC3gKkAt8CpgLhAqQC4QKmAuICpAJUAagCWAGlAuMCpgLkAqUC3AKnAt4CpQLlAqcC5wKmAg0DtwImA6YCgQOxAo0DpgLmAqgC6AKmAngDqAJ6A6YCNwGpAjsBpgLeAqgC3wKnAuQCqALlAqcCdwOoAnkDpwLpAqgC6wKnAuwCqQLtAqcC4gKpAuQCpwLoAqgC6QKnAnEDswJ9A6cC3wKpAuECqALhAqkC4gKoAu4CqQLvAqgCbAO4AnsDqALwAqkC8QKoAukCqgLrAqkC8gKqAvMCqQLsAqoC7gKpAgYCrQIKAqkC9AKqAvUCqQJQA6sCUQOpAnMDqwJ0A6oC6wKrAuwCqgLxAqwC8wKqAlADrAJSA6oC9QKsAvcCqwJRA60CUwOrAvICrQL0AqsC9gKtAvgCrAIFAq8CCQKsAlIDrgJUA6wC9QKuAvcCrAJoAbACbAGtAlsBsQJeAa0C8wKvAvUCrQJUA68CVQOtAmwDuQJ4A60CUAOvAlIDrgIFArICCQKuAlUDsAJWA64CUgO9AmsDrwJWA7ECWAOvAlwBswJgAbACVwOxAlkDsAJZA7ICWgOwAm0BtAJwAbECVAOyAlYDsgJwAbYCdAG0AnIBuAJ3AbQCbgO2AnADtAJvA7YCcQO0AnADtgJyA7UCawO2AmwDtQJpA7YCawO1AmwDtgJtA7UCbgO2Am8DvgJoAcECbAG+Ak4B1QJmAb8CagHDAm8BwAJdA9YCcwPAAsgA3ALwAMMCOwHGAj4BwwKDAcgChwHEAoYBxwKKAccCigHLAo0BxwJgAcsCZAHIAoYBzAKKAcwCGAPdAisDzQJcA9ECYAPQAlsD1QJfA9ICdAHWAnkB0gJ4AdYCfQHTAnsB1wJ/AdYCjwLaApIC1gKCAdsChgHXAlsD2gJjA9cCnQHbAqEB2AKgAd0CpAHYAoYB3AKKAdkClALeApgC2gKlAd0CqAHbAogB3wKMAdsCYQPeAmMD4QK0AuUCuALiArkC5gK8AuICvALnAsEC4gJiA/sCcQPjArgC5wK8AuMCwQLnAsUC5AJoAekCbAHqAsIB7gLGAeoC9QH6AgAC6wLGAe8CygHrAl4D8AJlA+wCygHwAswB7QJWAv4CZALtAv4B8QIDAu4CywHxAs8B7wLhAgAD7gLwAlQC9QJYAvICXAP4AmQD9ALyAfcC9gH0AvsB9wL/AfcCVAL8AlgC+AKMAfwCkAH5AmIDAANoA/kCkwEfA7IB+gK/Af0CwwH+AsYBAgPJAf4C4AICA+UC/wLMAQQD0AECA20DBgNyAwgDMQExA0gBCANtAw0DcgMLA/ABDwP0AQwD7QEQA/EBDQPxARED9gEOA/gBEwP8AREDyAE5A+EBEgN0AxkDewMUAx0CGAMgAhsDLQIfAzACHAMXAiADHAIdAyECIgMlAh4DJAIjAygCHwN5AyQDgQMgAycCJAMrAiMD/AFMAw4CJAN7AycDfgMmAzICKgM2AiYDGgIqAx0CJwN/AyoDgQMoAzMCLAM4AikDPAItA0ACLAOCAy8DhAMvA4IDMgOFAy8DfwMyA4IDMQOCAzQDhQM0A3sDOwODAzUDUAI5A1QCOANHAjwDSwI6A20CSgN+AjwDUAJAA1QCPANIAkADTAJAA0oCRANOAkAD5QJRA/ICQQMYAlMDMAJGA1QCSgNYAksDTgJOA1ICUAPnAlQD6wJRA04CVANSAlID5wJWA+oCVQPnAlkD7AJYAzwCXAM/AlsDUgJfA1YCZAMpAoADUQJlA2oCaANtAmoDfwJuA4QCbgNtAnMDcQJwA4UCdAOJAnADXgJ0A2ICcgNhAnYDZAJ0A3MCeAN4AnQDdgJ4A3oCdQN5AnoDfgJ2A2ICegNnAnYDdgJ6A3kCeAN6AnwDfgJ4A3gCfAN8AoMDeAKHA3wCigNLAo0DTwKMA6ICkAOmAowDnwKRA6MCjwOiApIDpgKQA6kClAOtApEDpgKVA6oCkwOoApcDrAKaA7kCpAPIAp4DgAKiA4UC",
        [30] = // Сталинград — 1040 boxes (вкл. 4 невидимые стены-границы)
            "LwASAToAHQEvAAcBOwATATUACgFMACABNQCcAEMAsQA2AI8ANwCWADYAlgA3AJ0ANgCwAEIAwwA4AMIAQADYADgA2ABAAO4APQAHAUYAEQE/AO4AQAD0AD8A9ABAAPsAPwD7AEAAAgE/AAEBQAAIAUAA7QBHAO4AQgAQAU0AHAFDABkBVQAoAUcA7QBOAO4ARwCCAEoAggBJAHQAVQCCAEkAAgBWABAATQAcAVwAKwFNAIEATgCCAE4ALAFgADsBTgB1AWUAhgFQABIBZAAoAVEAggBUAIIAUQCsAFIArwBXAC8BZgA/AVcAoQBsAKoAVwDHAWYA1wFYAB8BbwA3AVgACQFvAB8BWgDCAGQAzABbAEMBZABLAVsAfQBlAIcAXAAWAWgAIgFfAPMBdAAKAl8ArAFsALgBXwDiAW8A8gFgAMQBbADQAWEAFgFwACUBYQAHAW0AFAFiALEBcgDBAWIAHAFuACYBYwDJAIQA1QBjAEUBdQBUAWMA1ABkANsAYwDbAGQA4gBjAOIAZADpAGMA6ABkAO8AYwDvAGQA9gBjAPYAZAD9AGMA/ABkAAMBYwADAWQACgFjAAwCbQAUAmQABgF7ABwBZAAOAXMAHQFmAM8BdQDeAWYANQF4AEkBZwCuAGgArwBnACQBeQA5AWkAvgGAANQBaQAUAXsAKAFpABgCdAAkAmkAeAFyAIEBaQCcAYAAsgFpAE4BdgBaAWoAOwF1AEYBagBxAXEAegFqAHkBfACIAWsAYgF9AHEBawAPAncAGwJrAJwAbACcAGsA3QF6AOwBawAHAXsAFgFsAKEAggCqAGwAAQJ+AA8CbQCOAXUAlwFtAIcBdQCQAW0AkwF/AKIBbQCBAYQAmAFvAFMBhABnAW8ABwGGAB0BbwCyAXwAvwFwAEsBggBaAXEAgAB/AIcAcQAgAYAALwFyAOEAfADrAHIAPwGKAFUBcwCoAYoAvgF0AMMBgADQAXUAEwGAAB8BdQAWAYwALAF1ADUBhwBEAXYAtgGBAMIBdgCIAX8AkgF2ADYBggBCAXcADAGDABgBdwCUAYIAnwF3AA4CgQAYAngAdwF7AHoBeQDHAYgA1wF5ACcBkQA9AXoAngGCAKcBegCfAY4AsgF7ALoBjQDOAXsABAGSABsBfAAXAogAJAJ9AGYBhwByAX0ARwGUAF0BfgCAAIwAhQB+AK4AfwCvAH4ADQKUACQCfwBXAYoAYgGAAJIBhQCeAYAADwGPAB4BgAAzAZcASQGAAAIAjAAQAIAAIgGMAC4BgQAQAZgAJgGBAAEBkAARAYEAdACNAIIAgQCIAY0AkQGCAKAAlQCsAIIAqQGZAL8BgwB+AY8AggGDAEsBjgBXAYQAhgGQAI0BhQC1AZwAywGFAJsBmQCvAYYAagGSAHEBhgC1AY8AvwGGAD4BnQBVAYYAyACHAMgAhwCZAYsAnAGIAGMBlABsAYgAggGUAIsBiQB5AYsAewGJAC0BlAA4AYoAbgGWAHMBigCJAZMAlgGLAB0BmgAtAYsABwGdABYBjAANAZcAGAGNAKsBpADBAY0AxgGXANABjgAtAaUARAGOAJ0BpQCzAY4AJQGgADMBjgCQAJAAlACPAJwAjwCcAI8AzQGYANYBjwBqAZsAcQGPAI4AkwCSAI8AhQGcAI0BjwCEAJMAhwCPAAUBnwAUAZAAkQGWAJ4BkADxAJoA+QCQAMYBpADbAZEAkwCXAJkAkQBzAZQAdQGSAK0BngC5AZIAhQCVAJEAkgCDAJYAhgCTAEEBngBMAZMAlAGWAJYBkwCTAaAAoAGUAIAAlQCDAJQAjgCXAJIAlACfAKkArACUABgBqAAuAZQAOgGlAE4BlACFAJcAiQCVAEwBpwBbAZUAkACbAJYAlQCAAJsAhgCWAK4AlwCvAJYAEAKtACYClwB0AaMAfQGXAI8AmwCSAJcAawGaAG0BmACJAJsAiwCYAIQAnACJAJgAkwGvAKkBmACLAJ0AkACYAA0BpwAcAZkAxAChAM0AmQABAasAEAGZAHEBpgB4AZoAvgGsAM0BmgB/AJ4AgwCaANgBqgDoAZoANAGmAEABmwAqAaYANgGbAAwBrQAbAZsAPQGyAFQBmwBJAasAWQGdAKoBtADAAZ0AlwGpAKMBnQCgAbQAtwGdADABrwA/AZ0AXAGtAGwBngCDAaUAjwGeAFYBtgBsAZ8AxAGuANMBnwDvAaoA+gGgAAcBrwAWAaAAeAGpAIQBoADDAKEAxgChACEBuAA3AaEAtgGzAMUBoQDJALEA0wChAPv/twADAKEAggCkAIIAowB8Aa8AiQGjAKUBrQCwAaMAaAG7AH4BowCKAbMAmgGkAEoBsgBZAaQAjAGwAJYBpQAQArEAHQKlAKsAsAC2AKUAzgG3AN0BpQDxAa8A/AGlABcBsQAjAaYAAQGyAAsBpwBQAb0AZwGnAIEAqACCAKcAXgG+AHUBpwADAbMADwGoAGwBtAB5AagACAK7ABcCqQCqAbIAtQGpAMABuwDPAakAMgHAAEkBqQCJAb8AoAGpAAkBtgAWAaoApgG8ALoBqgCbAKsAnwCqAJsBvACqAasAggCuAIIAqwAwAbQAOgGrABQCtwAfAqwAbgHDAIQBrQCAAb8AjwGuAEABwABPAa4ACwG+ABoBrwAFAbsADwGvAMYAsADHAK8A+wC8AAgBrwDqAb8A+gGwAKQBwAC0AbAAyADCANMAsABMAbsAVwGxAB8BvQAsAbEAKAG5ADEBsgASAckAJwGyABECvgAdArIA1QG/AOEBswCxAcUAwAGzAEwBvwBWAbMA2v/BAO//tABYAckAbAG0AMcBxgDWAbUA6ADDAPYAtgC+AcEAygG2AGcBwgBzAbYAiwHCAJcBtgCUAcUAowG2AA4CzAAlArYAbAHFAHwBtgD7AMgACgG2ACMBxgAxAbYA3AHFAOsBtwDu/78AAwC3AAYBwwASAbcAm//AALD/twCw/8AAxv+3AMb/wADb/7cAfQDCAIcAtwAyAc4ARwG3ACsBwgA2AbcAGQLHACkCuQB4AcsAhwG6AJIByQChAboAVAHCAF0BugC2AdEAzAG7AJwAvACcALsAqQC8AKwAvAADAMYADQC8AGYB0QB9AbwAggHIAI4BvADSAccA3gG8AJ8A0gCoALwApQHOALQBvQA2AckAQgG9AEMBzwBSAb0ABQHNABQBvgCKAdUAoAG+ABIBygAfAb4AcQDCAH8AvwBiAMQAcAC/AFQBywBeAb8AiAHLAJQBwAAhAc8AMQHAANEAygDbAMAAxwDEAMkAwQB9AdYAkwHBACsBzgA3AcEAmAHRAKYBwgC5/8UAuv/CAFkAyABfAMMAVwHXAG0BwwAVAdQAKAHDAMIB2ADUAcMAXQDHAGEAwwABAdUADwHFADYAyQA9AMUA3gHRAOoBxgBbAMkAXwDGAPYA0AD/AMcASQHbAGABxwBDAdMATwHHAHYB0gCCAccAOgHdAE4BxwDo/8sA7//IAGwB3wCCAcgANAHUAD4ByQALAMwAEgDJAJQB1QCgAckATwDMAFYAyQCyAeAAyAHJABEB1QAdAcoA/gDVAAoBygCJAeAAoAHKANcB2gDnAcoAEQHhACcBywDw/88A9//LAJkB4QCtAcsAKgHWADcBywAdAeIAMwHLANH/0QDY/8wAaQHYAHYBzACrAM0ArADNAAkB3AAZAc0ArAHYALcBzgBiAeUAeAHOAHwB5gCSAc8AMgHYADoB0AD5AOQAEAHQAM8B4ADfAdAA1wDaAOIA0QBDAeEAUwHRAKUB6AC7AdEAAwDVAAoA0gCeAOUAqgDSAFoB3ABlAdIAmwDTAJwA0gDFAeoA2wHUAL8B4QDMAdQAfgHfAIoB1AAsAeQAPwHUAMgA6ADVANUA/f/cAAoA1QCNAeUAnAHVAHIB3gB7AdYA2AHiAOUB1gCqAN0AsgDWAFIB6ABhAdYAYwDaAGYA1wBwANsAfgDYAB8B5wAvAdgAFwHnACYB2ABjANsAcADYAFsB4gBmAdkACQDcAAoA2QAHAeIAEQHZAKUB5ACwAdkAiQHiAJIB2QCRAfEApwHaAAgB8AAeAdoA8v/nAAQA2wDH/+QA3f/bANz/5ADy/9sAZQHrAHUB3AA8AesATAHcAPoA8AARAdwAOQHuAEgB3ABEAfMAWgHcAFsB7ABrAd0A1AHtAOQB3QBvAfEAgAHdALsB6QDHAd0AgADhAIQA3gAqAekANgHeAKAB8gCxAd8AewHqAIcB3wBzAesAfgHgAA8B7wAfAeAAxgDhAMcA4ABTAfAAYwHgALAB7QC9AeEAmwHwAKsB4QAkAe0AMAHhAMwB8QDcAeEAhwHqAJAB4QDGAe4A0wHiAI0B6gCWAeIAewH2AI0B4gAvAfEAPgHiAKkB9gC7AeIAGAH1ACcB5AAwAfgARgHkAFUB+wBsAeQAsgH7AMgB5AC9AfsA0wHkAJ8A9ACpAOUAIgH5ADgB5gD7//wAAwDmAP4A9gANAecAQAHzAEwB5wCTAfAAnAHnAMoA/QDTAOcApQH0ALEB6ABmAfEAcAHoANgA8QDgAOgAZAEAAXoB6QDBAPEAyQDpAEsB+wBaAekAggDsAIIA6gByAfUAfQHqAK0B9gC5AeoARQH5AFQB6gDfAPYA6ADrANIA+gDhAOwAywH4ANcB7QBPAfwAXgHuAOQA+QDvAO4AmwDvAJwA7gDFAfcAzwHvAEEB+wBLAe8ArwH/AL8B7wCBAPAAggDwANoA/ADnAPIAmwD2AJ8A8gDAAQIB0AHyAI0B/ACYAfMAggD2AIIA8wCfAAUBqQD0ALkB/wDEAfQA4gH+AOwB9QCYAPgAmwD3AN8ACQHuAPkAqAD9AKwA+wBlAQoBdAH8AHgBBQF/AfwA2QAFAeIA/QDKABIB0wD9AMcAAQHKAP4AEAENAR8BAAFgAQoBawEAAccABAHKAAEBhAADAYcAAgHiAA0B7gACATUBDgE+AQIBgwAIAYkAAgGjAREBsQEEAQsBEAEYAQQB7gARAfwABAHoABMB+AAFAWcBDgFyAQYBgAALAYYABgGbAAcBngAGATcBFAFEAQYBoAARAasABgGrAAcBrAAIAXQAFAGCAAkBAgAVARAACgGAAA4BhAAOAYAADwGDAA8BUwEdAWgBDwECAhkBDAIPAcMAEQHGAA8BpQEdAb4BEAG+AREBxQEQAcQBEQHLARABywERAdIBEAFnARwBegEQARABHQEjARABIwEcATwBEQHJABwB1AARAfIBGAHzAREBAQIYAQICEQHoABsB+QARAe0BFAHuAREB9QEfAfsBEgF6ARoBkAESAZABGgGlARIB0gAbAegAEgG+ASABzAEXAfkBJQEBAhcBwQAdAccAFwHyAR4B8wEXAQECHgECAhkBBwEgARABGgGFAR4BkwEaATsBIAFBARoBkwEfAaABGwF6ASEBiAEbAeAAHQHkABsB+QAeAfoAGwHcAB8B4AAcAZsAIgGiABwBUQEfAVIBHAGZASMBoQEcAWQBIgFqARwBxwAiAc0AHAELASIBEgEdAV0BIwFjAR0BdAEeAXQBHgHGAB4BxwAeAecAHgHoAB4BLAEkATIBHgEpASIBLQEeAVsBIQFfAR4BzQAkAdMAHgG+AR8BvwEeAZgAJAGeAB4BOgEfAToBHwGgACMBpAAfAWMBIwFmASABEQEkARUBIAGXACQBmQAgAWoBJAFuASABmAAoAaAAIQGdACMBoQAhASsBJQEuASMBmAAnAZoAIwGqACQBqwAjAZwAPQGoACUBmgApAZwAKAGCACsBggAuAYEALwGCAC8BGwE1ASEBMAEZATMBGwEwAVIBNAFWATEBWAE4AWABMQFXATQBWwExAYIANAGCADEB5QA1AekAMgHuADsB+AAyAV8BOQFtATIBuAE4Ab4BMgGdATUBoQEyAU8BOAFVATIBVAE4AVoBMwEYATgBHQEzAdoAOQHgADMB1AA5AdoANAFPATUBTwE0AeQAOQHoADQBgwE1AYQBNAFsATkBegE1AUsBOwFRATUBzwA7AdYANQG8ATsBwgE1AYAAOQGDADYBvQE3Ab0BNgE5ATkBOgE2AZcBOQGYATYBIAE6ASMBNgENATcBDgE2ASMBOgEmATcBKQE7ATYBNwGjATsBsQE4AQsBPAEOATgB9wA7AfoAOAFAATkBQQE4AcQAOwHFADgBzwA5Ac8AOQH6ADoB+wA5AYAAPQGDADkBSwE/AVEBOQGwAUQBuwE5AQ4BTQEaAToBFwFDAS0BOgHOAEgB5wA6ASwBRwE+ATsBmgA7AZsAOwHnAEgB+QA7AZwBRAGyATwBfwBCAYYAPAGdAE8BqQA8AcUATAHPAD4B+QBCAfwAPgFPAUMBUwFBAYAARQGCAEEBmAFFAZoBQgECAE4BEABCAasAQwGrAEIB0gFIAdgBQwGxAVkBugFEAXQAUAGCAEQBmgFIAZ0BRAEPAVoBGAFFAc0BSwHTAUUBQwFGAUMBRQF9AVMBigFFAcgBTwHSAUYB2wFIAd4BRgG8AUcBvwFGASICUAEsAkcBNgFXAUABRwGXAUgBmAFHAfAAXQH5AEgB8QFLAfIBSAHfAUkB4AFJAU4BVgFSAUkBuQFXAb4BSQEAAlABAQJJAdkBTAHcAUkBlwBNAZsATAHFAF4B0gBMAWgBYgF1AUwBmgFiAacBTAHHAVIBzQFMAcAAUAHDAEwBlgBQAZoATQHeAVoB8AFNAdEBXwHeAU4BwwBSAcYATwGAAFMBgwBPAZwAaAGqAE8BAAJWAQECUQF/AFcBhQBRAQsBUgEOAVIBlwBVAZsAUgHLAVcB0QFVAQsBWQEPAVYBAAJiARMCVgF/AFwBhQBWATIBaAE/AVcB7wFhAQECVwETAmABKQJYAUMBWAFDAVgB7ABtAfoAWQGxAW4BugFZAYIAXQGGAFkBEAFpARoBWQHoAWkB8gFbAb0BXAG+AV0BywFhAc8BXQEZAWsBMgFeAYAAYQGDAF4BxgBqAdgAXwHPAXgB3QFgAcwBZAHQAWEBewF8AZYBYgG4AWwBwgFjAdcAbAHtAGMBCwFkAQwBZAEGAXEBEwFlAQYBawEMAWYBggBqAYIAZwHLAWgBzAFoAacAcQGxAGkBSgFuAU8BaQHoAX8B8QFqAZIBbAGVAWoBzABtAdAAagEJAXABDwFqAQ0BcAETAWoBfAFtAX8BawGbAGsBnABrAdEAbgHVAGsBFAFsARUBawE6AWwBOwFrAaIAcgGoAGwBwwBsAcQAbAGfAG8BowBsAYEAbQGCAGwB1QByAdsAbQHMAXsB0QFuAXIBegF+AW4BkwF6AZ8BbgH6AG8B+gBuAbABgQG8AW4BoQByAaQAbgGFAXUBjAFuAUkBdAFOAXABof99AbX/cAGCAHMBggByAX0AfAGHAHMB9P99AQUAcwG0/3wByv9zAcn/fAHf/3MB3/98AfX/cwGCAXUBhQFzAY0BdQGQAXQBhAF2AYkBdAGJAXYBjgF0AecBiAH0AXUBfwF4AYQBdQGNAXgBkgF1AX8BgQGTAXUBTgF2AU8BdgFlAZEBgAF2Ad4AhAHsAHcBygB7Ac4AdwGRAZIBrAF3AQ8BfQEVAXgBbwF7AXEBeAHQAYsB3QF4AaEBewGjAXgBHQF8ASEBeQELAX8BEAF5AcUAfgHKAHoBzQGIAdEBegH+/38BCwB7AXkBjgGFAXsBjAGOAZgBewF5AYABfAF7AZUBgAGYAXsBYwB+AXAAewFYAH8BWwB7AXAAgAF+AHsBQgF+AUMBfAEfAYABIgF8AVQAfwFYAHwBwQCCAccAfAE6AIEBSAB8Ab4AgAHCAHwB+QB9AfoAfQGrAH4BrAB9AVkAgwFfAH0B0ACBAd4AfQEPAX4BEAF9AXcBgAF5AX0BmQB+AZ0AfQGdAIEBoQB9AZgBgAGaAX0BrAGLAbABfgHD/4EBxP9/AfYAjQEDAX8BNgGKAUIBfwG8AIUBwgB/AVIBmAFgAX8B5QCKAfcAfwGXAYQBmQF/AXgBhQF6AYABxgCLAdIAgAFgAY0BZAGAAREAhQEYAIABlwGIAZ8BgAGbAIQBngCBARIBiQEoAYEBKAGJAT4BgQGxAZYBugGBAdAAigHmAIEBcwGHAXgBgQEkAIUBKwCBAZ8AlwGoAIIBuwGFAb4BgwESAZkBJwGDAdP/hwHa/4QBlwGJAZkBhAF4AYkBegGHAeoBnQHzAYgB7f+LAfP/iAF/AZQBkgGIAXcBiwF5AYgBKwCMATIAiAGYAYsBmgGIAbkBlgG+AYkBqP+OAbD/iQE3AZ4BQAGJAe8AmwEBAYkBeQGOAXwBiQGVAY4BmAGJAcgAnwHRAIoBlQCVAaAAigEOAYwBEQGKAfkAlgEEAYoB0QGgAdoBiwFAAZgBRAGLAa0BmQGxAY0BYAGbAWUBjQGqAJEBrQCNAXoBqAGVAY0BbgGQAXEBjgGhAZEBowGOATkAkQFAAI4BygGPAc0BjwFyAZsBfgGPAc0BnQHSAY8BkwGbAZ8BkAGtAJQBsACQAX8BlAGEAZABjQGUAZIBkAHyAZkBCAKQAQgCmQEeApIBqQCUAa0AkwH8/5gBCQCTARkBnwEjAZMBgwGVAYkBkwFiAJcBcACTAYQBnAGNAZMBiAGVAY4BkwFwAJgBfQCUASEBqgE2AZQB9wCvARIBlAGBAZUBhAGUAYwBlgGPAZQBCgCXAQsAlQEXAZwBHQGVAZkAlgGaAJUBBQGXAQcBlQFRAaMBVAGWAbABqgG9AZYBmACbAZ0AlgHw/6QBBACWAX8AnAGFAJcBtf+fAcv/lwHL/58B4f+XAZ0AqgGqAJgBVAGrAWABmAGVAJsBmQCYAYsAngGSAJkBCQGgARABmwGBAJ4BhQCbAZkAngGdAJsBkACfAZQAmwEWAqUBIQKcAYkAogGQAJwBHQGdAR4BnAF7AZ4BfwGcAQgBqAEUAZwBkQGfAZQBnQEhAakBLQGdAeYBrwHzAZ0BrACeAa0AnQGRAKABngCeAYIAoQGCAJ4BNQGxAUEBnwGSAKIBlQCfAcgAtQHRAJ8BjgCkAZMAoAGZAKMBnQCgAVABoQFRAaAB0QGxAdsBoAHCAKEBwwCgAZoBtwGnAaEBaAG4AXUBpAFDAaUBQwGkARQBpQEVAaQBgQClAYIApQETAbEBHgGlAQoBuQEdAaYBywGnAcwBpwHaAbEB6gGoAYIAqwGCAKkBKgGuAS8BqQGwAbwBvQGqAZ4AvwGnAKoBMAGvATUBqgHCAKwBxgCqAVUBvAFfAasBvgCvAcIArQG/ALgBygCtAaoArgGuAK0ByADEAd4ArgHLAbQB0QGuAa0AsgGxAK8BvQGvAb4BsAEeAbQBIQGxAS8BtgE1AbEBrAC4AbIAsgHLAbsB1QGyAakAtgGtALIBMwHAAUEBsgEWAcgBKwGzASABtgEiAbMB3wG0AeABtAFCAbQBQwG0AfABtwHxAbUB0QG6AdYBtQEkAbsBKgG2AawAvAGzALcBvgC8AcMAtwGyAc0BuwG5AcEAvwHHALsBrwC+AbMAuwGZALwBmgC8AdYAzAHuAL0BxwDDAc0AvQFQAb4BUwG+AZ0A0wGqAMABLQHDATABwQEuAccBNAHCAYEAxwGGAMMBIwHQAS0BwwHTAMsB4ADDARUB2gEnAcMBiwHNAZUBxAF+AM4BiADEAVEBzAFaAcQB6gDVAQIBxQErAcsBMQHGAb4BxwHBAccB0ADHAdEAyAFPAc0BVAHIAdMAzAHbAMgB4ADSAe0AygGTAdYBpgHKAS8B0AE0AcoBVwHWAXkBygECANYBEADKAXQA1gGCAMsBsgHWAb0BzAGmAdQBvAHMAVIB2AFdAc0B/gDeARUBzgEoAc8BKQHPAS8B0wEyAc8BIwHXASgBzwEtAdMBLwHRASkB3QExAdEBvQHXAcIB0gGeAOUBqgDSATAB1gEyAdIBjgHWAZIB0wFPAdkBVQHUAbgA2QHGANQBpQHZAbMB1QHvANUB7wDVARAB4wEeAdUBkQHYAZMB1QHwANkB+ADWAWEB2gFvAdYBVAHXAVUB1gHjAeQB8QHWAbsB3AHAAdYByQDaAcsA1gFUAd0BWgHXAXIB3QF3AdcBmQDYAZoA2AEBAeABDwHYAXkB2wF6AdgBqQHZAakB2QEmAd0BKgHZAfoB2gH7AdkB+gDbAf4A3AEbAeIBIQHcAakA5QG/ANwBKQHeAS0B3AG9AOkB1ADdASAB4QElAd0B3gHqAegB3gHdAOIB5QDfAdAA8gHmAN8B8AH1AfkB4AHlAe0B7AHjAQ4B5wEWAeUBoAD7AakA5gHrAOgB7wDmAe8A6AHyAOcB4gD4AfoA5wEYAegBGQHoAYIA6wGCAOkBUAHvAV4B6QH4AOoB+QDpATQB7gE6AekBXgHuAWwB6gEwAfQBOwHqAZwB7wGhAesBiAH2AZMB6wGhAfEBpgHrAaoB7wGuAesBPwHuAUAB7AH5APQBBwHsAS0B8gEzAe0BtAHwAbUB7gHaAe8B2wHuAQcB8gEPAe4B9QD+AQgB7gGoAfABqwHvAYEA7wGCAO8BmwD9AaAA7wG6AfMBxwHvAdUB9AHiAe8BPQHzAUoB8AHHAfUB1QHwAZkB9AGnAfABBgH7ARIB8QGMAfUBmgHxAR8B+gEsAfEBMAHzATQB8gEqAfgBMAHyAWMB/wF4AfIBGwH9ASgB8gEWAfcBGwHzAXYB/wGJAfMBtAH/AccB8wGCAPYBggDzATgB/AFOAfMBTgH8AWQB8wEpAQkCPwHzAS8B9wEzAfMBEQH5ARcB9AGJAf0BnwH0AZ8B/QG1AfQBxwH9Ad0B9AHdAf0B8gH0AfkB/QEPAvQBDgL9ASQC9gGZAPYBmgD2AQEBCQIXAfcBGQH3ARoB9wEmAQECMgH3ARYB/QEbAfoBoAAQAqkAAAICAAwCEAAAAnQADAKCAAMCIQEYAjcBBQIAARwCEgEJAh0BCgIeAQ4CmQAPAp0ADwIdAR0CJwEQAg4BHgITARMCfAAhAokAGAL2ADACCAEeAgoBLAISASMCgAAxAoUAPAKAAD0CgwBaAHAA9AGCAFoA9AH0AQYCXABwAG4ABgLgAXAA8gEGAg==",
        [11] = // Берлин — 1087 boxes
            "GgBNATAAZwEfADYBIgA/AR8APwEiAEcBHwBHASIAUAEfAE8BIgBYAR8AVwEiAGMBKQCcACwApQApAKUALACtACkArQAsALYAKQC1ACwAvgApAL4ALADGACoA/QEsAAICKgACAiwABgIqAAYCLAAKAioACgIsAA8CKgAOAiwAEwIqABMCLAAXAjAA3AE6AOQBMQDWATQA2gExANoBNADfATEA3wE0AOMBMQDjATQA6AExAOcBNADsATEA7AE0APABMQDwATQA9AExAPQBNAD5ATQA1wE/AOQBNwACAUEAGAE5AN4BRQDrAToA1wBEAOUAOwAQAlAAJQI+ADACSgA8Aj8AHAJSAC8CQwA2Ak4AQwJDAE0BWQBnAUkA8gBMAPsASQD7AEwAAwFJAAMBTAAMAUkADAFMABQBSQAUAUwAHAFJABwBTAAlAUkAJAFMAC0BSQAtAUwANQFJACoCVAA3AkkANQFMAEEBSgAQAlUAHQJKAD4CVgBLAk0AUAJYAF0CTgDsAVcA9QFOABoCWAAkAk8AIwJaAC8CTwA3AloAQgJQAL8AUwDHAFAAxwBTANAAUADPAFMA2ABQANgAUwDgAFAADAJfABwCUADgAFMA7ABRAEUCXQBSAlIA9AFfAAACUgAwAloANwJTAFgCXwBlAlQA0gBWANMAVAAaAl8AJQJWAPgBYQADAlkADQJqAB4CWQBSAmUAXgJaADcCZQBEAlsAJwJmADMCXACyAm0AxAJcAEMCYABHAlwASwJnAFYCXACpAV8ArgFcAK4BXwCyAVwAsgFfALYBXAC2AV8AuwFcALsBXwC/AV0AdQFxAI8BXQCPAXEAqQFiABUBZQAeAWIAHgFlACYBYgAmAWUALwFiAC4BZQA3AWIANwFlAD8BYgA/AWUASAFiAEcBZQBQAWIAUAFlAFgBYgCeAG0AqQBiAFgBZQBkAWIAEAJyACECYgAdAm4AKgJjAEACbgBNAmUAvgJ0AM4CZgBKAnIAVwJpAHYBbAB+AWkAfgFsAIYBaQCGAWwAjwFpAI8BbACXAWkAlwFsAKABaQCfAWwAqAFpAKcBbACzAWoANQJ2AEICawAjAnoAMgJsAB4BbgAfAW0AuAF1AMIBbgAPAnkAHAJwAHUBdQB4AXEARgJ8AFMCcQCoAoAAuQJzALkCfgDEAnQAmwKDAK0CdAA8An4ARgJ1AHUBeQB4AXUAGgKBACQCdgAtAn0ANAJ4ADQBgQBAAXgAkAKOAKcCeAAPAoQAHAJ5AEgChABVAnkAdQF9AHgBegAoAokAOAJ8AO4AhAD4AHwA7AB/APEAfADxAH8A9QB8APUAfwD5AHwAEAF/ABUBfAAUAX8AGQF8ABkBfwAdAXwAHQF/ACIBfAAiAX8AJgF8ADUBfwA6AXwAOQF/AD4BfAA+AX8AQgF8AEIBfwBGAXwARgF/AEsBfABkAX8AaAF8AGgBfwBtAXwAbQF/AHEBfABxAX8AdgF9ANYBigDkAX0ANAKJAEACfQCTAIcAoQB9AK4AhwC8AH4AOwGBAEUBfgAhAokALAJ+AKMCiwCwAn8ADgKOAB8CgAC2Ao8AxwKDAKcCmQC8AoMAxwKSANYChAChAIcApQCEAKUAhwCqAIQAvACHAMAAhADAAIcAxACEAMQAhwDJAIQAyACHAM0AhAA4Ao8ARAKEANYBjgDgAYUAQQCIAEYAhQBGAIgASgCFAEoAiABOAIUATgCIAFMAhQBTAIgAVwCFAFcAiABbAIUAWwCIAGAAhQBgAIgAZACFAGQAiABoAIUAaACIAG0AhQBtAIgAcQCFAHEAiAB1AIUAdQCIAHoAhQB6AIgAfgCFAH4AiACCAIUAggCIAIcAhQCHAIgAiwCFAIsAiACPAIUAjwCIAJQAhgCQApIAngKGALMCkgC+AocAIQKWADEChwBWAIwAWQCHAEUCkwBRAogAlgKYAKgCiQANApgAHQKMAC0CnwBAAowAEQOVABoDjQA7ApgASAKNABoClgAjAo8AigKbAJcCkACdAp0AqgKRAEwCnABZApEAEwKYABoCkwBVAqQAaAKTAFkBmwBjAZQAsgKeALsClAAZAqMAKQKUAAkCoAAWApYAYgKhAG0ClgCOAqMAnAKXALkCogDEApgApQKkALICmAAYAaMAKAGZAEMCnQBHApkAJwGkAEYBmQAEAaQAGQGaACYCowAvApoAeQKlAIQCmgCaAqUApQKbABECpgAcAp0ARwKlAFECngBeAq0AbgKeAHACpgB2Ap8AUwKuAGICoACFAqkAjwKgAB0CqwAoAqAADwKnABYCogA+ArIATQKjADQCqgA7AqMAIwGnACgBpAAeAaoAJQGkAPsArAAFAaUADAK0ABwCpgCPAqoAkwKoAC0BqQAxAagAoQK1AK0CqADtAr0AAAOpACQCtAAxAqkAqgK4ALoCqQC9Ar4A0wKqAGQCsgBqAqsAiwK2AJcCrABoArkAdQKtAEoCvABbAq0A3QC2AOYArgATAr0AIwKuADgCtwBCAq4AeAK7AIYCrwBfAr8AcAKwAFgCvwBnArEAmQK6AKUCsgAsAr0AOQKyAMECxwDUArMAggC9AJEAswCnArwArwK0AIICxACTArUA7wLFAAADtQDbAb0A5gG1ALQBvgC+AbUAdALAAH8CtQA5AsUASgK2AGcCzAB9ArYAGgLFACsCtwC3AsYAyAK4AI4CxwCfArgAmQLHAKoCuABHAsMAUgK4AHgCxACFArkAVADQAFsAugAqAskAOgK6AFwCygBsArwAgwDoAI8AvABNAswAXgK9AL0CyADIAsAA+ALHAP8CwABAAs0ATQLDAPQC1wAHA8MAAgPWABYDxADnAtcA+wLFAGED2AB1A8YAEQPcACcDxwBSA9kAZgPIAHED1gCBA8kARgPZAFcDyQAeA9oALQPKAOgB2gD5AcwASQPYAFYDzgAsAegASgHOAPAC4gACA84AdAPjAIcDzwBPAPsAWwDRAN8B3ADqAdIARAPmAFYD1QCbAOcAtADVAA8C3gAXAtUA5AHfAO4B1wAgA+gAMQPXABgB5wAwAdoAugHjAMMB3AAOAugAGwLdAI4A6ACdAN0ACAHkAA8B3gA3AukAPwLgAA4BDAEqAe8AcQP7AH4D9AAkAv8ALwL6AFIAFgFbAPoAegH/AIAB+wB+AQQBhwH7ANsCDwHwAvwAdAAIAYEA/QClAAYBrgAAAYUBBAGJAQEBUgEGAVcBAQEiAgwBLwIBAYoBCAGQAQIB6AIOAfUCAwEfARIBNwEDAXcBGgGMAQMBSgEJAVABAwEZAw4BJAMEAaEBFQGyAQUB7QIZAQEDBQEqAhABNwIGARsCEwEqAgYBfQAkAZIABwGQABIBqAAIAWMBHwF9AQoBeQAYAYgACgErARUBNwEOARUCHAEjAg4BpwEYAa8BDwGPARQBlAEQAaoBHAG2AREBkQEWAZYBEgEYAiUBKwISAewCHgH5AhQBhQEtAZsBFQHjAiEB7wIXAR8BIAEoARcBjQMiAZkDFwHmACIB9gAYAbABJQG8ARgBWgEqAW0BHQFEAyoBUQMhAf4BJgECAiQBAgItAQwCJAH/AS8BCwIkARMBKgEXASgBsAA4AcYAMgExAT4BPQE0AYcAPQGRADQBAgE9AQsBOAEcAUEBJgE5AZUDQgGeAzkBMQE/ATgBOwF8AFIBkQA7AZMDRwGgAzwBiANKAZcDQAEUAWsBOAFAAY8CTAGcAkEB2gFKAeMBQgGFA1QBlgNCAQ0DTAEWA0IBHwFSAS4BRgG0AU8BvQFJAVECWwFjAksBRwJXAVQCTAGIAGwBowBMAbAAWgG/AFEBKgFgATgBUgFGAlsBUAJSASEDYgExA1MBWQJiAWgCUwF9AFkBgwBWAUgCYgFVAlcBUgFqAWQBVwF4AXEBlAFYAW8CawGCAloBXAGBAYIBXAG+Am4B0AJcAcgCZgHQAlwBPQFiAUMBXQGeAHABswBfAcoCbQHZAmABtgJ0AcoCYAEqA2oBMgNlAZwAfgGyAGUBvgBwAckAZQEwAYEBSAFmAcgCeQHcAmcB5QBvAe0AagEAAnIBCgJzAU8CgwFfAngBqgCKAbwAeAG2AIUByQB5AdQAhgHoAHkBSAKFAVQCegHoAIQB8QB8AU4DhwFZA30BUgKGAVsCgAFMAokBWQKBAbsBkQHLAYEB/gCKARoBgQHEAZIB1AGBAcwBjQHYAYIB0AGTAd8BgwHWAY4B4QGFAacAjgGwAIcBrgKSAbkCiQFMAZMBVQGaAZsBoAGgAZwBegClAYMAnwELAaIBEQGfAe0BqQH2AaABsgGkAbYBoAGvAaQBswGgAQwCrgEaAqEBagGsAXYBoQGUAaIBmAGhASgBpQErAaIBVAGjAVUBowHXAKwB4ACkARoDrwElA6QBngGrAagBpQGeAL0BrACmAYEAwAGeAKcB7wC1AQcBqAGAAbQBnwGoAQwCtgEaAqkBBgG0ARUBqQEsAbIBRwGqARUBqgEaAaoBGgGqAR4BqgGfAbMBuAGqAbgBqgG8AaoBvAGqAcEBqwF7AawBgAGuAQ4CugEaAq4B3AG7AegBrgH5ArkBDwOvAWIAugFtALQBDALBARoCtwH5Ab4B/wG5AcsCwgHZArkBnQLGAaoCvAGsAcYBtgG+AWcBwwFsAb8BgQDYAY8AwAGqAtABtwLBAdsA2gHpAMIB6ADCAe0AwgHqAssB9ALFAWYBywFsAcYBbALPAXYCxwEYA9kBIgPJAfwA0QEHAcoBEAHPAREBywFEA9UBTAPLAWoC1gF1AswBFQHVAR4BzAGWAtUBnwLNAWcC1gFxAs4BUgDsAWcAzgHvAtgBBQPPAToA4AFUAM8B+gDYAQEB0AGAAuIBmQLSAbYC2wHAAtIB6ADYAe4A1AHqAN4B8wDUAeMC3gHqAtQB1QDeAdwA2AGsAOQBtwDZASkD5AE/A9wBhgHnAZEB3gEQA+oBHQPfAQsC9QEiAuABEAHxASoB4wHJAuwB0gLjAT8D7wFKA+YBUwLwAVwC6gGqAPQBsQDsAaEA+wGsAO0BTgHyAU4B7wE5Af0BRgHvAW4C9gF5AvIBZQL2AW8C8gFOAfcBTgHyAVkC9gFfAvMBXwL2AWUC9QFuAwMCfAP2Ac0CBALXAvYBTgEAAlMB9wGrAgECvQL3AUYBEAJPAfgB6gAGAvMA+AGYAg4CogL6AaIAEAKsAPsBhAIEAo0C+wGGAwUCkgP8AeUCBQLzAv0B+wIGAgQD/gFuAAMCdAAAAkACBAJGAgACOwMPAksDAQJMAgYCVgIBAkYCBQJMAgECVgIGAmACAgKDABsCjAACArMABgK1AAMCHwMNAisDAwJgAgYCZgIDAgQDGQIOAwgChAMSAo8DCgLbAA4C2wAKAtQADwLZAAsCZgASAm0ADAI6AxsCRwMMAgwCGQIbAg4C1QARAtgADgIaAxgCMAMOAlkBFAJcAQ4C2wATAtsADwJ3AhoCjQIQAo0AKQKqABACMQEpAk4BEAKFAxsCkQMQAisBGgI0ARQCDAIiAhoCFgLGACQC1AAXAtwBIwLoARkCDAImAhcCGQLTAiQC6QIaAoMAKgKOAB0CuAMqAsYDHQLdACYC+QAdAgsBKAIgAR0CdAEeAnkBHgL9AR8C/gEeAowCKgKXAh8CugEfAr4BHwK+AR8CwwEfAs4DLgLbAyACDAItAhoCIQKuACoCtwAjAmwBJwJwASQCNwMvAkIDJQJ6ADAChQAlAtMBLALaASYCIAEmAiUBJgLUADAC3QAmAgYBJwILAScCGAEsAiEBJwITASsCGAEnAvQAMQL9ACgCaQExAnIBKwJCATYCTAErAq0DNwK5AysCVQEsAlcBLQIcATICIAEuAuwBMQLxAS4CDAEzAhABLwKeAjsCpwI2Ak4CQQJZAjgC6AFMAvsBOAJJAkMCVAI6AqYBSQK0ATsC8QFIAgACOwJRAkYCWwI7Aq4BSQK9ATsC4AFJAu8BOwK3AUkCxgE8AvwAQwIEATwCpwFKArQBPgK6AUwCyQE+AkgCSgJTAj8CQgJNAlACQQI2AUQCPAFCAkIBRwJIAUYCtABPAsIARwIaAU0CIAFIAiYBVgJGAUkC9gBXAg4BSQK9AFYCxgBKAtkAYwL2AEsCjwNWApoDTQKFAVICigFQAnsCXAKIAlACCwNbAhIDUAIKA2gCIgNRAgADXAIMA1ECfAFUAoIBUgKSA10CnwNTAtEAWQLVAFUCrwFcArYBVQLTAVYC1gFWAnwCYQKGAlcCnwBmAq4AVwKBAGMClABYAnICYwJ/AlgC1gFcAtcBWQIFAWICDgFZAsYBeALSAVoCwwFaAsgBXAKrAV0CsAFcAq8BXQK0AV8C1ABlAtoAYQIPA2wCGwNhAgMCaQINAmICagF+AnMBYwISAWsCGgFjAtgAeQLiAGoCpQB4Aq4AbAJ7AHUChABtAtYBbgLXAXQC+AJ/AgIDdgKMAoICmQJ3AiACgAIqAncCHAKAAiUCeALHAZEC0AF6ApACigKaAnwC8gKLAgMDfAKWAogCowJ8AnEDiAJ+A30CUQGCAlUBfgKsAJgCvgB/AtcBgwLcAYACjAKOApoCgQKZAokCpAKEAmsDkQJ4A4UC1wGKAtwBhQLvApIC/QKGAvACkAL5AocC6wGQAvQBiAJbApUCaAKIAigDmwI7A4gCswKUAsACigJuAZECdQGKAnIDlQJ+A4sC5QKhAvwCiwJOApgCWQKLAmIDlwJvA4wCcAOVAnkDjQLSAJEC1gCOAogDlgKRA44CjQOaApoDkALQAqMC5QKQAokDnwKZA5ECaQGtAnIBkQJVApoCXwKSAlECmwJaApMC3AKlAu8ClALSAJkC1wCUAsAAnALHAJYC2gCsAuQAmAK8AJ0CvQCZAqwBowK2AZkCxQG5AtMBmgKzAaICvgGaAqADoQKrA5wC2QKnAuQCnQJZA6gCZQOdApcDpQKhA58ChwGmAo4BnwK4AKoCwwCgAlwDqgJnA6ECjQOpApgDoQKpAbACxQGiAssCqwLUAqMCaAOwAnUDowJyAa4ChwGjApsBrgKqAaQCbwOvAnoDpQJcA64CZAOmAoYDrAKPA6cC6gCwAvMAqQJRA7UCXQOpAmIDtQJuA6sCgAOyAogDrALZAMkC8wCsAk4DuwJdA60CsgGzArwBrwJiAbgCawGwAnYAtQJ8ALACdwO4AoIDsAKcAbQCoAGzAoUCvwKRArQCigK9ApMCtAJGA8kCWgO1AtwCwQLpArYCbgO+AnkDuAJ4ALwCfQC5ArUAugK6ALsCTAPGAlgDuwIVAcQCMAG8AmgDwgJwA70CXADWAmUAvgJYAsoCZAK/AuICygLuAr8CEgLTAiYCwAIHAtACGALAAk4ByQJWAcECYgPIAmkDwgJNAdACWwHEAjgCzwJEAsUCjgHQApoBxQKXAdACpAHFAoMB0AKPAcUCEAPRAhsDxgJbA80CYwPHAhgC2gIsAscCYALUAm4CxwICAtgCEwLIAlkC0QJjAsgCNQLUAkICyQJ/AM8CiQDLAlMD1AJdA8sCAAHQAgUBzAL3ANEC/ADNAo0D0wKYA80CAALiAhUCzQIoAdUCMAHPAoMD1QKOA9ACLwLYAjgC0AJjANoCagDSAk4D2AJVA9QCowLgArAC1QKDA+ACjwPVAkUD3QJQA9UCQQPlAlAD1gJbAPUCZwDXAqgC5AK1AtgCcgPmAoID2AJ8A9sCgwPZAnMD4gJ9A9kCkQPkAp4D2QIIAu0CHALZAgUC6gIWAtwCaAPwAn0D3AIaAOECGgDcAiwD4AIyA90CnwLoAqsC3QLeAuYC6ALeAiID4wIsA94CoQLnAqoC3gKtAOMCsQDeAi0B4gIyAd4C+wDmAgIB3gKvAeYCtgHfAhEC9AInAt8CagPnAnUD4AJ6AOkChADgAhwD5QIjA+AChgPrApMD4AIlA+UCLwPhAhoA5gIaAOEC9gLtAgMD4gJIAeoCUgHiAtYA6QLcAOICQAHoAkoB4gL0Au0CAAPkAlIB5gJTAeQCZQPpAmsD5QJhAvYCcgLnAjYB8QJFAecCXQPwAmcD5wIeAfACNwHnAkMBBgNPAeoCgQD1ApkA6gKYAPUCpwDqAqYA8wK/AOoCvgD8AtcA7AL0AvgCAAPsAkkC9QJTAu4CGAL5AiMC7wLOAgQD4gLvAikD+AIyA/ICYwL5AmoC8gIJA/gCEAPzAvQC/AL9AvQCTAL9AlYC9AJHA/sCUgP0AksC/gJUAvYCBgP9AgwD9wLAAgQDzQL4AkID+wJIA/gCOAP+AkMD+QLEAg4D2gL6AlcBAANdAfsCrwEEA7kB/ALQAQUD2gH9AiACCAMrAv4CyQIHA9MC/gKWAgYDoAL/AtQCCgPgAgEDvgINA8wCAQNMAQsDUgEEAyQCCwMrAgYDRAEfA00BCAMrAgwDLwIJA1QBDQNVAQsDHwMeAzMDCwOsACID2QAMA2oDHQN7Aw0D/AIfAw0DDgMjAA8DKAAOAygADwMsABEDEAMoAycDEQMaAxgDJAMRAyQDFAMqAxIDcwMWA3oDEwN5AxcDgwMTA1MCJQNlAhMDVwMnA2gDFAOcAyADqAMVAyIDKQM2AxUDFwMeAyADFQMBASADFwEVA/4CKgMSAxUDeQMaA4IDFgNaAiwDcAIXA4QDHwOGAxcDZAMnA3UDFwMJAywDHwMXAz0DJwNOAxgDNwInA0cCGAMwAykDQgMZA5UDJAOhAxoDqgImA7YCGgMuAiUDOwIaA6oCJQO3AhoDTAEgA1EBGgNnAioDdgIbA2ADJANoAxsDcgIqA4ICGwOGAicDkwIcAy8CJQM4AhwD8gImA/wCHQPAAiYDyAIdA7MCKQPAAh4DRQElA08BHgNQASQDVQEeA+oAKAPzAB4DGgEkAyIBHgMxASUDOwEeAzkBJQNDAR4DmQMqA6YDHwOwAigDuQIfA8ECLgPOAh8DhAMnA4YDIAN/AiwDiwIgA04CNgNkAiADmQIrA6MCIAPzAiYD9wIgA0ICLwNSAiEDiwI1A54CIQO6AiwDxAIhA2oCKgNzAiIDLwI3A0UCIgNbADsDZAAjA04CLQNWAiMDkwMvA6ADJQPwAiwD9gImAwoBKgMQASYD0AEvA9oBJwOEAzADhgMoA/QBMQP9ASgDaAI3A3kCKAN5AjMDhAIpA+8CMgP4AikDgQI4A5ECKQOZAjQDpgIqA14CNQNpAioD6AIzA/ICLAOXAzcDowMsA6ACOAOsAi0DSgI4A1UCLgP/ATwDDgIvA7ICOwO/AjADPQI/A0wCMAOEAzgDhgMwAygCPwM4AjADUwI/A2MCMAOSAjsDnQIwA20CRQOBAjADkwJEA6gCMgM2Aj0DQAIzA1sCSQNwAjUDQwJKA1cCNQODAj4DjAI2A7ACPgO3AjcD/gE9AwQCOAN9AkcDjgI5AzACSAM/AjkDswJEA78COgMjAlADOQI6A2EAPwNiADoDKAJBAy4CPANUAkcDXgI9A2wCRgN0Aj0DwAJIA80CPQOTAlMDqQI9AzYCUwNMAj4DoQJMA68CPwNhAEQDYgBAAxYCVgMsAkADswJHA7oCQANUAlYDagJBA7cCSgPAAkEDjQJJA5QCQwN/Ak4DhwJDA2gCUwN3AkQDYQBIA2IARANEAk0DTgJFA3ECWgOHAkUDyAJQA9QCRQOzAkwDugJGA7oCWAPMAkcDSwBnA2IARwOIAlEDkgJIA0QCUwNRAkoDswJVA78CSgNJAmADXwJLA6YCVgOwAksDMwJUAzwCSwPEAlgD0QJMA5oCXwOtAkwDigBZA5cATAPrAFkD+QBNA+EAWgPvAE0DsABYA70ATQP1AFoDAwFNA6QAWAOwAE0DuwBYA8gATQMAAVoDDgFNA2cAWQN0AE0DHQFZAyoBTQMpAVkDNgFNAzUBWQNCAU0DQAFZA0wBTQNiAlgDbAJNAz4BVwNIAU4DdgBZA4IATgOBAFkDjQBOA8YAVQPMAE4DHQJdAy0CTgP/AFoDDAFOA44CWgOZAk8DPwJZA0cCTwPiAlYD6AJPA50AWgOqAE8DOQFgA0oBUAPoAGAD+QBQA/wAYAMMAVAD9ABfAwMBUANnAmIDdwJQA+AAYAPwAFADogBbA60AUAMgAWADMAFQA7MAYAPDAFADKwJdAzgCUANxAloDegJRA3AAXAN8AFEDfAJnA5ICUQNVAmcDawJRA8MCXgPRAlIDfQBdA4kAUgPBAF0DzQBSA2cAXQN0AFIDqwBdA7gAUgMuAV0DOwFSA3UAXgOBAFIDBwFeAxIBUgOhAF4DrgBSA4cAXgOTAFID6wBdA/UAUwNDAV4DTQFTA7UCXAO+AlQDbwBdA3gAVAPlAl8D6wJVAygBYAM0AVUDMgJrA0gCVQNEAmADTwJWAx8BXQMlAVYDugJiA8QCVgPsAl8D8AJXA58CbAO3AlcDOQFdAz4BWQMjAmgDMwJZA5YCaAOkAlkDUAJjA1kCWgMeAmYDKQJcA78CZwPLAlwDegJnA4UCXAMJA2UDEgNcA14CcgN0Al4D5wJpA+wCYANBAnYDWAJgA6ADawOtA2EDLgJsAzoCYQN9A2wDigNhA2wCdgOCAmEDlgNsA6MDYgOQAnYDpAJiAzsCbwNIAmIDVgJtA2ACYwPrAmsD8QJjA4QDawOGA2QDmgNvA6cDZAO9Am8DyAJkAx8CcwMwAmQDqwJzA7wCZgNhAGoDYQBmA1YCdwNnAmYDKwJ8A0ECaAOIAnMDlAJoA3wDcwOIA2gD6QJvA+wCagOIA28DkwNqA4ECcgOIAmoDYQBvA2EAawO3AnQDwAJrAz4CdQNHAmsDhAN0A4YDbQNcAoEDcgJuA2AAhANnAG4DmgKEA7ECbgPsAnYD9AJuA+kCeQPvAm4D8QJ6A/0CbwOzAncDuwJvAxgChQMuAnADRAJ8A1ACcQNrAoADewJxA4IChAOWAnEDjgKGA6MCcQOYAngDngJyA1YCfQNhAnIDeAJ+A4UCcwOsAogDwgJ0A4QDfAOGA3QDiAN5A5MDdAMoAocDOQJ2AzgChANJAnYDTwKCA1sCeAOkAoYDsQJ4A3UDgwN3A3oD+gGEAwQCfANpAoUDcgJ8A4QDhAOGA38DVAOFA14DfwOIA4QDkwOAA2QDhQNsA4EDugGPA9kBggM0A4cDQAOCA0ADhwNMA4IDUwGKA2IBgwNMA4UDVQODA1sDhQNkA4MDbAOFA3UDgwN0A4UDfQODA30DhQOFA4MDkgCMA60AgwNYAosDZwKDA1ADjwNdA4QDlgGMA7EBhANqA48DdwOEA04BlgNnAYQDZgCQA5IAhANHA5ADUwOEAykCkANUAoQDVAKMA2oChQMwA4sDNgOFA3gDkAOEA4cDQAOSA0wDhwPzApMDAAOIAzQDkQM+A4sDhwOWA5MDiwP+ApYDCwOMA2QDlwNwA4wDawOYA3gDjgP2ApkDAwOOAz0DlgNHA48DSAK7A1QCkQP0ApcD9wKVAwADoQMNA5YD9AKhA/sCmAP3AqQDBAOjA/gCqQP+AqYD/QKuAwgDqgMGA7IDEQOwAxEDugMbA7YDjALBA5gCtwMYA8EDIwO9AyEDxgMsA8sDugHsA9sB4APwAewDHAI=",
        [10] = // Город в пустыне — 1344 boxes
            "QgD7AEgAAgFMAP0AUAACAU8A+QBVAP4AUADuAFYA9ABUAPkAZQALAVUABQFaAAoBVwDjAFwA6QBXACQBXQAqAVgA6ABkAPUAWAAaAWoALQFaABIBYAAZAVoALgFmADoBXAD0AGIA+gBdAAcBYwAOAV4APQFkAEQBXgA4AWQAPgFgAO4AZQD0AGEA8ABoAPcAYQACAWwADgFiADYBcQBGAWIA6QBpAO8AYwDlAGoA6wBjAOIAawDpAGMA7QBoAPMAYwD5AGkA/gBmACABbAAlAWYA5ABqAO0AZwAQAW0AFgFnAN4AbQDlAGgACgFtABABaQAvAXAANgFqAMMBcADKAWsACQFvAA4BawANAXEAFAFrAB0BdgApAWwADAFzABQBbQAjAX0AMwFtAEABcwBGAW4AEQFyABcBbgAyAXkAPQFuABUBcwAbAW4AwwF5AM4BbwBMAXQAUgFvAKkBdACvAW8AoQF7AK4BcABeAXYAZAFxAM8AeADWAHEAIwF3ACkBcQCdAXcAowFyAPoAeAAAAXIAHgF4ACQBcgDsAHgA8gBzAMwAeQDRAHMA+ACDAAgBcwD3AHgA/QB0AKABewCnAXQAVAGIAGkBdQBiAXsAaAF1AMYAegDMAHUAIgF7ACgBdQBUAXoAWgF1AMcBhQDXAXUAywB6ANQAdgA4AXoAPQF2APIAfAD5AHYAnQF8AKIBdwBAAXwARgF3AAIBfQAJAXgAKgF+ADABeABEAYQAUQF4AK8BhAC8AXkAoAGEAKwBeQDJAH8AzgB6AC4BfgA0AXoAEAGBABcBewADAYEACgF7AIQBgQCLAXsAxQCBAMwAfAANAYAAEgF8ADUBggA8AX0AFAGEABwBfQBMAYMAUwF9ANgBjADnAX0ACQGNABkBfQA9AYQARAF+APwAigAJAX4AEgGFABkBfgA6AYMAPwGAAFIBhQBXAYAAQQGHAEgBgABqAYUAcAGAAOgBhQDuAYAAGgGEACABgAAWAYUAHAGAAEsBiwBWAYEAOwGFAEEBgQA/AYgARQGBAPoAmAARAYIAywChAOcAggBIAYcATgGCAHYBiAB8AYMACQGIAA8BgwAlAYkAKwGDABkBjwAoAYQAZQGKAGsBhABDAYoASgGEAGIBiQBnAYUARwGLAE0BhgAEAYwACgGGACcBiwAtAYYAhQGgAKABhwBSAY0AWAGHAFwBjABhAYcA+wCNAAEBhwBWAYwAXAGHAOEBkwDuAYcAsACNALYAhwBgAYoAZwGIABEBjwAYAYgAXgGOAGUBiAC2AI4AvQCIACkBjAAxAYkAJgGPACwBiQAqAZAAMAGKAA4BlQAaAYoAeQGiAJEBigB7AZAAgQGMALIBnADCAYwAuACSALwAjABwAZQAeAGMAJcBkgCeAY0AFwGTAB4BjQAcAZIAIgGNAHIBmQB/AY8AbwGVAHUBkAC2AJYAvACRAKgAlgCuAJEA/QCXAAMBkQAHAZgADQGRAAsBlwASAZEAfwGWAIQBkgCXAZgAnQGTAKQAoQCyAJMA7gGiAP4BkwD3AKAABQGTALQAmAC5AJQAqQGgALYBlQAHAZgADgGVAFkBmwBgAZUATAGbAFIBlQBQAZoAVQGVAJEBmwCXAZYAegGaAIABlgCaAZsAoAGWALEAmwC1AJYAsgCeALkAlgDFAJ0AzQCXAEQBngBMAZcAPgGdAEQBlwBdAZwAYwGXADIBnAA4AZgAYQGcAGkBmACzAZ4AugGYAMkAngDNAJgARAGcAEoBmABMAZ4AUgGZAC8BngA1AZkAOAGgAEABmQCuAKAAtQCZAGABngBlAZoA+ACfAP4AmgArAZ8AMQGaAJ4AoAClAJoAxQCgAMoAmgDIAK4A3ACbAKMAoACpAJsAQwGgAEkBmwBuAaAAdAGbACUBnwAtAZwAmACmAKQAnABnAaEAbQGcALwBoQDCAZwAcwGhAHgBnACVAaIAmwGcACYBogArAZwAswGtAMQBnQAfAaQAJQGdACMBogAoAZ4AwgClAMkAngCRAaIAlgGeAOkAowDuAJ4AdgGjAHwBngDFAKQAygCeACwBpAAyAZ4AmQGlAKEBngCnAKQArgCeAHQBpgB7AZ4A9AGpAP8BnwDnAK4A9gCfAGABpQBnAaAAlwGmAJ4BoQDAAKcAxgChAJAArgCdAKEAowCnAKkAoQB5AaYAfwGhAH0BpwCDAaIAnwCoAKUAogDnAKgA7gCiABcBqAAdAaIAmwGnAKEBowC8AKgAwwCkAH8BqQCFAaQA8gCrAPgApAAdAaoAIwGkAIoBrACRAaUAugCsAMAApQCsAasAsgGlAJgAqwCeAKYAwgC7ANQApgC+AawAxQGnAO4BtwD/AagAtgGuAL0BqADQAbMA2gGpANcBuwDoAakAgwGvAIkBqQCuAa4AtAGpAJAArwCWAKkA6QCwAPAAqgDsAbAA8QGqAL8AsADFAKwA6gCyAPAArADtAbIA9AGsALgAsgC+AKwAhACxAIoArAB5AL0AigCsAOMAsgDpAK0AswCyALkArQB+ALMAhACtAKsAsgCwAK0AvQGzAMQBrQC5AbQAwAGuAK8AsQC3AK4AtQGzALsBrgC3AbYAvgGvAKcAtQCtAK8ArgC0ALQAsAD+AbYABAKwAAIBtQAIAbAAiAC2AI0AsACTAbUAmAGwAJ0BtQCjAbAApAC2AKkAsADvAbYA9AGwALwBuADEAbAAuADDAMwAsADQAb4A3gGxAIQAtwCKALEApQG4AKsBsQDMAbkA0wGxALoBuADAAbIAoQG2AKYBsgDCAbgAyAGyAP8AuAAGAbMAoQG5AKcBswCgALkApwCzAP4AuQAEAbMA/gG6AAUCtACoAbsAsAG0APoAugACAbQAvgG6AMUBtACkALkAqQC1AMIBugDIAbUApgG8AK0BtQCsAbwAswG2AGYAzQB+ALYA4gC7AOYAtgD7ALwAAQG2AK4BvAC0AbYAjADIAJ8AtgDdALwA5AC3AN8BvQDmAbcA+AC8AP0AtwCbAMoAsAC3AK4AvQCzALkA4AC/AOUAuQD2AL8A/AC5ANoAvgDiALkA/gDBAAUBugCCAM8AmAC6APoBvwAAAroAsgDHAMEAugDcAMEA4gC7AMwBwADSAbwA2ADCAN4AvACtAMIAtAC8AM8BwgDXAbwA5gHCAOsBvQCRAMMAmAC9AO4BxAD1Ab0A8wDOAAMBvgD0AMQA+wC+ANMBxADaAb4A2gHDAOABvwCIAMQAjQC/AK4AxgC1AL8A1gHEANsBvwD+AcQAAwK/AIQAxgCKAMAA1wDFAN0AwADkAcQA6QHAANcBxgDdAcAA3gHDAOYBwADvAMcA9gDBAPIAxgD3AMEA2wHFAOABwQB2AMcAewDBAMIBxgDHAcEA3wHGAOMBwQDyAccA9gHCAHMAxwB5AMIA7wHHAPMBwgDEAccAygHCAMcByADNAcIAgADJAIcAwwCgAcgApgHDAHIAygB5AMQAfQDTAI4AxACFAckAigHEAO0AygDzAMQAawDKAHIAxABiANoAdwDEAGIA2QB3AMQApgHKAKwBxADvAcsA9AHFAPIBzQD2AcYAyAHMAM8BxgBmAMsAbADGAKoBzACyAcYAzwHMANUByADtAM4A8gDIAPkBzgAAAskAzwHOANUByQCGAM4AiwDJANIBzgDaAcoA8AHPAPQBygDSAdAA2AHKANgB0ADeAcoA3AHQAOIBygDuANwA/wDLAOIB0gDoAcsA2AHQAN4BywD/AdEABALMAKYB0QCsAcwA7ADSAPIAzQCrAdIAsAHOAH4A0wCCAM4A9QHZAAECzgDmAdMA7AHPAOMB1gDrAdAAfwDWAIUA0ABoANYAbwDRAHwA1wCCANEA6QHXAO0B0QCwANYAtADSAI8A4gCgANMArADXALEA0wCXANkAngDTAGwA2gByANMAZgHhAHMB0wCoANoArwDUAKUA1gCtANQAnQDZAKMA1AD4ANoA/gDUAKMA3ACqANQA8wHZAPcB1QCmANkAqwDVAKMA2QCnANUAdQHcAHwB1QB7AN0AggDVAPIB3QD4AdYAbwHkAH4B1gB4AN0AfQDWAPcB4wAEAtYA0ADjAN0A1wDzAd4A+QHYAI8A3gCWANgAagHeAHAB2ACdAN8ApADZANcA6QDmANoAeADgAH4A2gDcAOAA4wDaAPMA6AAAAdoAywDhANEA2gBzAeAAeQHbAPUB4QD6AdsAiwDnAJcA3ADDAOIAyQDcAMcA4gDNANwA4wDoAO4A3AD6AeIAAALcAMwA4gDSANwA4QHiAOcB3gD2AeQA/AHeAOQB4wDqAeAAwgDiAMoA4ACQAOYAlwDhAIwA5wCRAOIA+AHnAP4B4gD3AeoA/AHiAGkA6QBvAOIA+QHvAAUC4wDfAOgA5QDkAGgA7ABsAOUA6gHqAO8B5QDzAOoA+ADlADoB9QBJAeYA6gDrAPAA5gDjAewA6gHmAPYA7AD8AOYA5QDvAO4A5gDwAe0A9gHmAGoA7QBvAOYA5AH2APQB5wAAAu0ABgLoAFsB7gBiAekARwH0AFMB6QBYAe4AXgHpAC8B7wA2AeoAaADwAGwA6wBRAfEAWAHrAPAB8QD2AesAUwH6AGIB7AD6AfEA/gHtAF4B8gBkAe0AoADzAKcA7QCfAPUAqADtAPMB9QD4Ae0AaQD0AHAA7gBvAfMAdQHuACEB/QAxAe4AmgD1AKEA7wBnAPUAbADvACIB/AAwAe8AaQH/AHkB7wDhAfQA5gHwACcB9QAtAfAA8QH1APUB8AB4AP0AhADwAEAB9gBHAfAA5AH1AOoB8ABFAfYASwHwACsB9wAyAfEAPAH3AEMB8QBCAfcASQHxAJkA+ACfAPIATQH2AFMB8gAxAfoAOAHyADkB+ABAAfIA3AH+AOgB8gB0AfkAegHzAPMB+QD4AfMAPQH5AEQB8wBmAPgAawDzAHwA+ACCAPMARwH4AE0B8wBmAfgAbAHzABwB+AAiAfQAQQH5AEkB9AD9AfkAAwL0AAUBBAEUAfQAUgH5AFgB9AAaAfsAIQH1AP0B+wADAvUAgQD7AIUA9QAeAfsAJQH1APAB/AD2AfYAWQEDAWYB9gBQAfwAVgH2AAICAQEOAvYAWAH8AF4B9gBSAf0AWQH2AH8BBAGNAfcAMwH9ADkB9wApAf4ALwH3APAB/QD3AfcAGQH8ACEB+AAgAf4AJgH4AC4B/QAyAfgA/wH+AAMC+ACBAf4AhgH4AFoAEQFxAPgAMQH+ADUB+QBuAf8AcwH5AAIB/gAHAfkAjAEGAZkB+gBnAP8AbQD6AOsB/gDwAfoAVgEBAV0B+gBfAQABZgH6AOQB/wDqAfoABAEBAQoB+wBiAQYBbgH7AN4BAQHkAfsAewABAYAA+wAHAgEBDQL8APAAAQH2AP0AdgEDAX0B/QCIAQIBjgH9AFgBBAFfAf0AZAECAWoB/QB+AQMBhAH9AKEBDAGxAf4ANQELAUIB/gBHAQQBTgH+AMYBAwHLAf4AQAEDAUYB/gAUAQQBGgH+ACIBBQEoAf4AQwEEAUkB/gC8AQwBygH+AAICBAEIAv4AJgEEASwB/gCyAQ0BwQH/AGoABQFxAP8AEAEOASAB/wDMAQUB0gH/AAUCBQELAv8AeAAFAX4A/wB6AAcBgAD/AAYBBwEKAf8A8QAFAfcA/wBbAQQBYAH/AAQBBAEIAf8ArQEFAbMBAAHWAQYB3AEAARACBgEWAgABUAEGAVYBAAFqAQcBcQEAAUUBBwFLAQABSgEGAVABAAGAAQUBhgEAAW4BBwF1AQABXgEGAWQBAAF5AQUBfgEAAXwBBgGCAQEBigEHAZEBAQEcAQYBIQEBAUABBwFHAQEBMAEIATYBAQEIAQYBDgEBATMBBgE5AQIBWwEIAWIBAgGUAQoBmwECASEBBQEpAQIBIQEHAScBAgF3AAgBfAACAY4BCQGUAQIBHgEJASQBAgH0AAoB+AADAQkBCQEPAQMBYgEJAWgBAwHVAQkB2gEDAVABCgFXAQMBnAEJAaIBAwFIAQoBTwEEARkCCgEgAgQB8QAIAfYABAGSAQgBmAEEAbEBCQG3AQQBbQEJAXEBBAHxAQoB9wEEAQoBEAEWAQQBaAEJAW0BBAE/AQoBRQEEAV4BCQFkAQQBaAEKAW4BBAE4AQoBPgEEAUMBDgFNAQQBBgEKAQ0BBAGYAQkBnQEFAQgCEgEVAgUBIQEVATEBBQH6AQwBAAIFASwBEAE4AQUBoAEMAacBBQFNARABWAEGAdIBCgHYAQYBiAAMAY4ABgHwAAwB9wAGAcgBCwHNAQYBmgELAaABBgHtARwBAgIHAVQBFgFjAQcBngENAaQBBwFVAQ0BWwEHAVIBDAFYAQcBzAELAdQBBwFjAA4BagAIAbEBDQG4AQgBvgENAcQBCAHPAQ4B1gEIAeABDgHmAQgBSgENAVABCAHxAQ4B9wEIAegBDQHuAQgBFAIUASECCAHNAQ0B0gEIAeMBDgHqAQgBuwEOAcIBCQHKAQ0BzgEJASABDwEmAQkBogEOAagBCQEFAg4BCQIJAYgADwGPAAkBLAEPATMBCQHHAQ4BywEJAQkBDQENAQkB/AEPAQACCQF7ARABggEJAbYBDwG9AQkB8gATAfsACgERARABGAEKAX8BDwGGAQoBwwEPAckBCgGrARABsgEKAV8AIAF0AAoBhwAPAYsACgGpAQ8BrgEKAW0AEAF0AAoBrwEQAbUBCgEUAREBGwEKAW8BEQF2AQsBtAEQAbgBCwFeARABZAELAYABEQGFAQsBdAEPAXkBCwHfARAB5AELAd4BGgHsAQwB6gARAfAADAHrABcB9wAMAR0BEgEjAQwBBQISAQwCDAGHABMBjQAMAXQBFAF7AQwBewESAYMBDAEKAREBDgEMAXgBEQF+AQwBHwETASUBDAFpARIBbwEMAYMBEQGJAQ0BDQEUARUBDQFgARIBZgENAWUBEgFrAQ0BdgEdAYUBDQFrARoBeAENAYQAFAGKAA0BIAEVAScBDQHVARkB4QEOAYUBFAGLAQ8BGgEUASABDwGLARQBjwEPAQUCHgETAg8BZwEVAW0BDwGDABYBigAPAdABFgHWAQ8BgwEVAYcBEAEHAhUBCwIQAfYAFgH8ABABCwEYAQ4BEAEQAhUBFgIRAY4BFQGUAREBkgEWAZoBEgGEABgBigASAQwBFwEQARIBDgEYARQBEgHyABoB+QASAYoBGAGQARIBnwEgAa4BEgHMARcB0wESAegAHQH0ABIBGgEYAR4BEgGXARgBnQESAfcAGQH+ABMBrAEYAbABEwFlARkBbAETAZEBGQGYARMB9QAaAfsAEwGdARoBowETAeABGAHlARQBugEZAcEBFAGCABoBhwAUAc4BGgHUARQBagAaAW8AFAGIASIBlgEUAZUBGwGbARQBvwEZAcQBFAHWASEB5AEUAdYBGwHdARUBsQEaAbgBFQGuARoBtAEVAfEAGwH2ABUBggEbAYgBFQFwABsBdAAVAX8AHQGGABUB/gEbAQICFgEMARoBEAEWAcEBHAHHARYBvQEbAcMBFgHQARwB1gEWAe4BJQH+ARYB8QEoAQMCFgGqASQBuQEWAfgBHAH+ARYBFAEcARgBFwG1ASYBxAEXAcQBHgHLARcB8QAcAfkAFwHjAR0B6QEYAW8AHgF1ABgBEAEfARYBGAERAR4BFwEYARgCHgEeAhgBuAEgAb8BGQFtAB4BcgAZAdMBIAHaARkBBwInARYCGQHsACAB8wAZAZoBIAGgARkBwAEdAcgBGgHgASUB6gEaAcoBIAHQARoBBwIgAQwCGgF9ACABgwAaAecAIAHtABsB+gAgAf4AGwHjACMB7AAbAfABIQH3ARsBfAAqAYwAGwHIASIBzgEbARUBIgEbARwB3wAjAeYAHAGFASEBigEcAcEBKwHQARwB1wEjAd4BHAHyACMB+gAcAfUAIwH7ABwBIgIiASgCHAHCASIByAEdAZEBIgGXAR0BbQAiAXMAHQH7ACMBAAEdAZsBIgGhAR4B+AAkAf4AHgGsASQBsgEeAcwBIwHRAR4BpwEqAbMBHwHgACUB5wAfAecAJQHuAB8ByQEmAdEBHwH8ACYBAgEfAZ4BJQGlAR8BEQIlARYCIAG9ASYBxAEgAW8AJgF0ACABHgImASQCIAHaASUB4AEgAf8BJgEDAiAB9QAqAf8AIAHcAScB4gEgAd0AJwHjACEB9wEmAfwBIQEnASgBLwEhATEBKgE6ASEB5QAoAesAIQEIAicBDwIhAc8BJwHTASEBbwEoAXYBIgFyAS0BfQEiAQ8BKAEUASIB5gEnAesBIgFqACgBcQAiASkBJwEvASIBDgI1ASICIgFqASoBcgEiAXsAKAGAACIB0AEpAdcBIwHuASkB9QEjAUQBJwFJASMB8wApAfkAIwH+ACsBAgEjAfwBKQEDAiMBQAEqAUcBIwFwACkBdQAjAU0BKQFTASMB8wEuAf0BIwFIASgBUAEkAewAKgHzACQB/AApAQABJAHgASoB5gEkAecBKgHtASQBIwEsASoBJAG6AS8BxQEkAfAAKQH2ACUBegEuAYMBJQEIAisBDgIlAV8BKwFlASUByAEzAdYBJQF+ACsBhAAlAVgBKwFeASUBCwIqARECJQE4ASoBPgElASMCKwEoAiUB3gArAeIAJQEgASwBJgEmAWQBKwFqASYBLQErATQBJgHjASwB6gEmAeoALAHwACYB7QAvAfcAJgFjASwBaQEmARUBLQEbASYBEgEsARgBJgHfAC0B5QAmAVsBKwFhASYB/gEsAQMCJwFuAC0BdAAnAfYALAH6ACcBGgEsASABJwEGAiwBDAInAXYAQgGRACcBIgEsASkBJwGAATIBiwEnAdUALgHbACgB3QAtAeMAKAFnAS0BbQEoARUBLwEdASkBdQEvAXsBKQHsAC4B8gApAfcALgH9ACkBEgEvARgBKQFeASwBZgEpAegALgHuACkB1gEwAdwBKQHZATEB3wEqAXAAMgF0ACoBBwIwAQwCKgH+AC4BAwEqAeQAMQHqACoB5gEwAewBKgH5ATAB/wEqAWwAMQFyACoBeQEwAX8BKwHwATAB9QErAdoAMQHiACsB8QAzAfkAKwHlATMB6wErAfsAMQECASsB4gAzAekAKwGDATEBiAEsAcEBMQHGASwB6QEyAe8BLAHyATkBAAIsAcoBMgHRASwB5AA4AfAALAF+ATEBhAEtAXoAMgGAAC0BQAEyAUcBLQFEATQBSwEtAc4AMgHUAC0B/wAzAQQBLQE6ATQBQQEuAc8ANgHYAC4B0wAzAdgALgEMAjQBEwIuAQYCNgELAi4B3AE1AeIBLgFHATMBTQEuATEBNQE4AS4BbAA1AXIALgHWADUB3AAuAUsBNAFSAS8B4gA0AecALwHXATUB3gEvAekBNQHvAS8BTwE1AVYBLwF5ADYBfgAvAT4BMwFDATABCAI1AQ0CMAEoATYBLgExASwBNgExATEBNgE5AT4BMQEKAkEBGgIxATIBNgE4ATEBJAE4ASsBMQFqADgBcAAxATABNwE1ATEBzQE3AdIBMgFuADgBdAAyAdoBNwHeATIB0gA4AdcAMgEUAjcBGgIyAd4BNwHkATIB3wA4AeUAMwH+ADkBAwEzAT8BPAFIATMBKAE4AS8BMwHmADgB7AAzAW4BOQF0ATMBFgE6AR0BMwEEAjkBCQI0AQcCOAEMAjQB9wE6Af0BNAEjATkBKwE0AdsBRAHqATQBeAA6AX0ANAFyATkBdwE0AXUBOQF9ATUB9QE7AfsBNQHYADoB3AA1AW0AOwFyADUBwwBBAdMANQHQADwB1gA1AdwAOwHiADUB+gE8AQACNgE4ATwBPwE2ARABPAEYATYBeAA+AX4ANgEFAj4BDAI3ARQBPAEaATcBgwE9AYoBNwH2AEABAAE3AQUCPQEJAjgBdgA/AXwAOAHWAD8B3AA4AfgAPgH+ADgB2gA/AeAAOAFsAD4BcQA4AYcBPgGQATgBBwFEARMBOQGgAT4BqAE6AXkAQAGAADoBpgE/AawBOgERAj8BFgI7AQICTAESAjsBcgBVAYwAPAF+AEEBhAA8AXYAQgF8ADwB0gBIAd4APAHVAD8B3QA8AegBQgHuAT0B/gBJAQoBPgHxAEIB9gA+AfYAQwH7AD4B8ABHAfkAPgGyAUUBuQE/AcoARAHQAD8BzQBEAdEAPwG0AUcBuwE/AdkARQHfAEABBgFFAQwBQAHfAUYB5wFAAeUBRgHrAUABdwBGAXwAQQG9AFEBzQBBAcoARwHQAEEBAAJGAQYCQQG3AUYBvQFCAeoBRwHvAUIByABIAc0AQgHhAUkB6AFCAf4BSAEDAkIB1ABHAdoAQwHvAEkB9gBDAd0BSQHkAUMB0ABLAdUARAH8AEoBAgFFAckASgHNAEUB0wBKAdcARQFpAE0BbgBFAaMAUAGuAEYB6wBNAfIARgHaAUsB4AFGAacATAGtAEcBwgBMAcgARwGsAUsBsgFIAYwAUwGYAEgBjgBOAZUASAHOAE8B1QBIAakATgGwAEgB0QBNAdYASAH9AVUBCgJJAcYATwHNAEkBsQFNAbcBSQG4AU8BvwFKAbUBTgG7AUoBuAFPAb4BSgH/AU8BBQJKAYkAUAGPAEoB9ABQAfwASwG8AVEBxAFLAaQAWAGxAEsB1QBRAdsASwH6AVAB/wFMAdIAUgHXAEwBaABRAW0ATAH2AFIB+wBMAXoAUQGAAEwBdABTAXoATAG8AF0BzQBMAbsBUwHCAU0B8QBUAfgATgHvAVkB+wFOAcABUwHGAU4BzwBUAdQATwHNAVYB0wFPAe4BVQH0AU8BZgBWAWwATwHPAFYB1wBPAXMAVgF5AE8B0QFWAdgBUAHrAVYB8QFQAdgBVQHeAVEB8ABXAfYAUgFiAFcBaABSAdsBVgHgAVIB6AFYAe4BUgHVAVgB3AFSAXIAWAF4AFIB8gFiAQICUwEBAlkBBwJTAeIBWAHoAVQB7wBaAfQAVAHjAWYB9gFUAWUAWgFqAFQBxgBbAc0AVQHlAFoB6QBVAaQAXgGuAFYBcABcAXYAVgGFAFwBiwBWAc4AWwHUAFYBdABcAXoAVgHuAF4B8gBWAc4AZgHdAFgBxQBdAcoAWAGmAF0BrABYAXEAXQF2AFgBzgBeAdMAWAFvAGYBfQBYAd8AYwHqAFkB8wFeAfgBWgHlAV8B6gFbAb0AYQHCAFsBWwBrAWoAXgFjAGQBagBeAeIAYwHnAF8BywBmAdIAYAHdAGsB6ABhAb0AZwHEAGEBzgBnAdMAYQHDAGYBxwBjAXQAaAF5AGMBugBvAcYAZAHLAGoB0ABlAW4AbgF3AGUB3QBrAeQAZgFcAGsBYQBnAeIAbgHnAGgBzABtAdAAagFdAHABZABrAWIAcQFoAGsBvwByAcYAbAHJAHIBzgBtAcwAcgHRAG0BbQBzAXIAbgFgAHYBaABvAb4AdwHEAHABygB2AdAAcAF8AHgBggBwAWMAdgFoAHAB4wB2AegAcgHAAHgBxgBzAWAAeQFlAHQBZAB6AWgAdQFtAHsBcgB1AcEAfAHIAHYBvgB7AcIAdgF5AH4BfwB2AckAfgHOAHcBygCCAdQAdwHhAH8B5gB3AWEAfAFmAHcBvAB/AcMAeAF4AH0BfQB4AW0AfQFzAHgB4gB/AekAeQFjAH8BaAB5Ab8AgAHFAHkB7AB/AfEAeQHtAIEB8AB7AWAAggFmAHsBbQCBAXMAfAHhAIIB5wB8Ab0AgwHDAH0BxwCCAcwAfgFvAIQBcwB/AWwAhwFxAH8BYACFAWYAgAHsAIgB8ACAAcsAiwHWAIABxQCHAcsAgAHJAIcBzwCBAbsAiAHBAIEBbQCHAXMAggG0AI0BvgCCAWIAiAFnAIMBWACSAWgAhAFtAIwBdACEAWwAigFxAIUBawCLAXAAiAHAAI4BxQCIAccAkgHRAIgBzgCTAdkAiQFrAI4BcACNAcIAkwHHAI4BXgCTAWMAjwGxAJQBtwCPAb0AlQHDAJAByQCeAdcAkAHAAJwBzQCRAcAAnAHNAJEBvgCXAcQAkgFgAJgBZwCSAWsAmAFwAJMBtQCZAboAlAG9AJkBwQCVAbMAmgG4AJUBYgCbAWYAlgGBAJsBhgCXAbwAnQHCAJcBrQCcAbIAmAFfAJ4BZQCYAb0ApAHJAJgBtQCeAbkAmAFiAJ8BaQCaAcQAnwHKAJsBpwChAa4AmwGBAKABhgCbAbIAogG4AJsBtACjAboAnAFtAKMBcgCdAbsAowHCAJ0BcQCkAXgAngGpAK0BtwCeAcYApgHNAJ8BqACkAa4AoAG+AKgBwgChAZ8ArAGrAKEBvgCnAcUAogFZAKcBXwCiAcEArAHLAKIBXQCnAWMAogF0AKYBegCiAbEAqAG3AKMBbACyAXoApAGHAKoBjgCkAbQAqwG6AKQBZACqAWkApAGLAK8BlwClAb4AqwHEAKUBlQCrAZsApQHmAKoB6wCmAZMAtQGjAKgBpACuAaoAqAGzAKwBuACoAbQArgG7AKgBwQCuAcYAqAHmAK8B6wCpAbEAsAG3AKkBvgCwAcUAqgGNAK4BkwCrAcEAtAHLAKwB2wC0AeAArAHjALEB6ACsAeQAswHrAKwBiwCzAZEArQFSALMBWACtAZAAswGWAK4BYwC1AWkArgGdALMBowCwAZsAtgGhALABVAC1AVoAsAHWALgB3QCwAVoAvAFmALABlwC3AZ0AsQHBALYBxwCyAdkAuAHfALIBtwC4AbwAsgG5ALkBwACzAa0AwgG8ALMBrgDBAbwAtAHBALwBxQC1AbgAuwG9ALYB1wC7AdsAtgHEALwByAC4AagAvQGtALgBVADEAWAAuQFUAMQBYAC5AbcAvgG9ALoB2AC+Ad0AugHCAMABxwC6AZQAwAGbALsB4QDBAegAuwGaAMYBpgC8AbgAwgG9ALwBxADDAcsAvQFKAMIBUAC9AZYAxAGcAL4BnQDDAaMAvwGlAMwBsgC/AeIAxQHmAMABuQDFAb4AwAG4AMgBvADBAcAAzgHNAMIBXADPAWoAwgFMAMgBUgDCAZoAyAGgAMMBswDIAbgAwwG5AMgBvgDFAZ4AyQGjAMYBsADSAbwAxgHLAMwB0QDGAbkAywG+AMYBaADNAW8AyAGiAM0BqQDJAdAAzwHVAMoBlADPAZoAygGmANEBrQDKAWsA0AFvAMoBwgDQAcgAywGkANABqgDMAWIA2gFwAMwBugDSAcAAzAGWANIBnQDMAZMA0gGZAMwBqADUAa8AzAG4ANIBvgDMAVgA2QFlAMwBbADSAXEAzQGtANQBtADNAZYA1AGdAM4BmgDVAaAAzgFsANYBbwDOAacA0gGtAM8BqwDVAbQA0AHNANYB1ADQAboA1gHAANABtQDWAboA0AFtANUBcgDQAd8A1gHjANABsQDWAbcA0QGZANgBoADRAZsA1wGhANEBbwDYAXUA0gHEANcByQDSAcUA2AHLANIB2gDYAeEA0wGdANkBpADTAeAA2AHlANQBbgDYAXIA1AHXANoB3QDUAXAA2wF3ANUBngDaAaMA1QGbANsBoQDVAdkA3AHgANYBoADcAaYA1wFqAOMBdQDYAZ4A3QGkANgBowDeAakA2QHTAN8B2gDaAV4A4AFlANwBpQDgAaoA3AHNAOMB1ADcAdEA4gHYAN0BvQDiAcMA3QG7AOUBwgDeAcEA5AHJAN4BuADkAb8A3gFgAOQBZgDfAcwA7gHcAOABvwDmAcYA4AHMAOMB1ADhAcEA7QHPAOIBaADwAXYA4gGzAOgBuADiAXMA6AF4AOMBwwDsAc0A4wFzAPIBggDmAbEA8QG9AOgBhQDuAYwA6AHLAO4B0QDpAZEA7wGYAOoBiAD1AZQA6wFlAPEBawDrAb8A8QHGAOsBqwDwAbAA7QHGAPMBzADtAc8A8wHWAO4BnwD0AaYA7gGaAPQBoADuAZ0A9AGkAO4BwgDzAccA7gFuAPwBfADvAa0A9AGzAPABwAD8Ac0A8gGcAPcBogDyAb4A+AHEAPMBhgACApUA9AGTAP8BnwD2AbYABALFAPgBwQD9AccA+QGwAP4BtgD8AXoAAgKBAPwBlAABApkA/QGLAAUCkgD+AZAABAKXAP4BwwADAsgA/gF2AAQCfQD+AakADwK5AAMCqQALArAADwK2ABUCvQAgArAALwLAACECrQAmArIAJAK3ACsCvgAoArMALQK4ACoCuAA3AsYAKgK5ADYCxgAsArYAMgK7AC4CvgAzAsQANQK3AEACwwA4ArkAPgLAAD0CwABCAsYAQgK9AEgCxABKAsMAUALKAEwCwABbAtAA",
    };

    /// <summary>Decode the embedded int16-quad blob for a map into (x1,z1,x2,z2) tuples.</summary>
    public static System.Collections.Generic.List<(float,float,float,float)>? Decode(byte mapId)
    {
        if (!ByMap.TryGetValue(mapId, out var b64)) return null;
        byte[] raw = System.Convert.FromBase64String(b64);
        var outl = new System.Collections.Generic.List<(float,float,float,float)>(raw.Length/8);
        for (int i = 0; i + 8 <= raw.Length; i += 8)
        {
            short x1 = System.BitConverter.ToInt16(raw, i);
            short z1 = System.BitConverter.ToInt16(raw, i + 2);
            short x2 = System.BitConverter.ToInt16(raw, i + 4);
            short z2 = System.BitConverter.ToInt16(raw, i + 6);
            outl.Add((x1, z1, x2, z2));
        }
        return outl;
    }
}
