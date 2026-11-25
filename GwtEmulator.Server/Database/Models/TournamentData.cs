namespace GwtEmulator.Server.Database.Models;

public class TournamentData
{
    public long Id { get; set; }
    public long PlayerGUID { get; set; }
    public bool IsRegistered { get; set; }
    public int TotalDamage { get; set; }
    public int VictoryTime { get; set; }
    public int TotalBattleTime { get; set; }
    public int MaxDamage { get; set; }
    public bool RewardClaimed { get; set; }
}
