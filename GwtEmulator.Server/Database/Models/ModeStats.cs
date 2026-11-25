namespace GwtEmulator.Server.Database.Models;

// Отдельная от BattleStats (пер-танковой) агрегация — пер-РЕЖИМНАЯ, суммарная
// по всем танкам игрока в данном режиме боя (BattleGameMode). Нужна клиенту
// для трёх отдельных вкладок статистики профиля: "Возрождение" (Respawn),
// "Командир" (Commander) и "Оборона/PVE" (Defense) — раньше сервер слал туда
// всегда нули (см. HallServer.cs, PK_HC_AccountStatisticRevive/Commander/PVE).
public class ModeStats
{
    public long Id { get; set; }
    public long PlayerGUID { get; set; }
    public int GameMode { get; set; } // соответствует RoomServer.BattleGameMode
    public int BattleCount { get; set; }
    public int WinCount { get; set; }
    public int KillNum { get; set; }
    public int DeathNum { get; set; }
    public int Damage { get; set; }
    public int ShootNum { get; set; }
    public int HitNum { get; set; }
    public int ExpTotal { get; set; }
    public int ScoreTotal { get; set; }
    public int MaxKillInBattle { get; set; }
    public int MaxExpInBattle { get; set; }
    public int MaxScoreInBattle { get; set; }
}
