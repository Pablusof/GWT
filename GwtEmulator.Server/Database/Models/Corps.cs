using System;

namespace GwtEmulator.Server.Database.Models;

public class Corps
{
    public long Id { get; set; }
    public long CorpsGUID { get; set; }
    public string CorpsName { get; set; } = string.Empty;
    public string CorpsTag { get; set; } = string.Empty;
    public string TipName { get; set; } = string.Empty;
    public string Introduce { get; set; } = string.Empty;
    public string Notice { get; set; } = string.Empty;
    public long LeaderGUID { get; set; }
    public string LeaderName { get; set; } = string.Empty;
    public ushort CorpsLevel { get; set; } = 1;
    public byte State { get; set; } = 0;
    public byte TaxRate { get; set; }
    public int MaxMemberCount { get; set; } = 20;
    public long BattleCap { get; set; }
    public long Money { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CorpsMember
{
    public long Id { get; set; }
    public long CorpsGUID { get; set; }
    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public byte Job { get; set; }
    public int LoyalPoint { get; set; }
    public int BattleCap { get; set; }
    public DateTime JoinDate { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginDate { get; set; } = DateTime.UtcNow;
}

public class CorpsApplication
{
    public long Id { get; set; }
    public long CorpsGUID { get; set; }
    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public DateTime ApplyTime { get; set; } = DateTime.UtcNow;
}
