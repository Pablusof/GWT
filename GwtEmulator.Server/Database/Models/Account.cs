using System;

namespace GwtEmulator.Server.Database.Models;

public class Account
{
    public long Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public long PlayerGUID { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int Silver { get; set; } = 50000;
    public int Gold { get; set; } = 1000;
    public int Experience { get; set; }
    public int Level { get; set; } = 1;
    public int BattleCap { get; set; }
    public int CasernCapacity { get; set; } = 85;   // потолок клиента (TankSelectList максимум 85)
    // ВАЖНО: реальные id фонов ангара из клиента (Table_Garage / AdvancedAccountManager)
    // — это значения вроде 10001..10005, они не влезают в byte (переполнение при
    // касте int->byte давало искажённое число, из-за чего сохранённый фон при
    // следующем логине не совпадал с выбранным).
    public int GarageBackground { get; set; } = 10001;
    public byte ChannelId { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLogin { get; set; } = DateTime.UtcNow;
    public int ContinuousLoginDays { get; set; }
    public int MonthlyLoginDay { get; set; }
    public bool IsVip { get; set; }
    public DateTime VipExpireDate { get; set; } = DateTime.MinValue;
    public bool IsPremium { get; set; }
    public DateTime PremiumExpireDate { get; set; } = DateTime.MinValue;
    public long GuideStates { get; set; }
    public string MissionData { get; set; } = "0,0,0,0,0,0";
    // v.fix: оставшиеся купленные выстрелы в Тире (треасур-боксе), по уровням 1/2/3.
    public int ShootTimesLvl1 { get; set; }
    public int ShootTimesLvl2 { get; set; }
    public int ShootTimesLvl3 { get; set; }
    // v.fix: забрана ли уже награда за скачивание клиента (NM_CH_AskStandaloneLoginReward).
    public bool StandaloneRewardClaimed { get; set; }
}
