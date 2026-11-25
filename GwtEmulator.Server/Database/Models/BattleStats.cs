namespace GwtEmulator.Server.Database.Models;

public class BattleStats
{
    public long Id { get; set; }
    public long PlayerGUID { get; set; }
    public long TankGUID { get; set; }
    public int KillNum { get; set; }
    public int DeathNum { get; set; }
    public int Score { get; set; }
    public int Damage { get; set; }
    public int ShootNum { get; set; }
    public int HitNum { get; set; }
    public int BattleCount { get; set; }
    public int WinCount { get; set; }
    // v77: суммарный опыт по этому танку + лучшие показатели за один бой
    // (для «Макс. Фраги» / «Макс. Опыт» в окне статистики — раньше эти поля
    // были захардкожены нулями, потому что нигде не считались).
    public int ExpTotal { get; set; }
    public int MaxKillInBattle { get; set; }
    public int MaxExpInBattle { get; set; }
}
