namespace GwtEmulator.Server.Database.Models;

public class Tank
{
    public long Id { get; set; }
    public long TankGUID { get; set; }
    public long PlayerGUID { get; set; }
    public int CarportIndex { get; set; }
    public int VehicleId { get; set; }
    public int HullID { get; set; }
    public int UnderpanID { get; set; }
    public int TurretID { get; set; }
    public int MainGunID { get; set; }
    public int EngineID { get; set; }
    public int FueltankID { get; set; }
    public int CartridgeID { get; set; }
    public int RadioStationID { get; set; }
    public int GaugeID { get; set; }
    public int TankHp { get; set; } = 1000;
    public int TankExp { get; set; }
    public int TankCurHp { get; set; } = 1000;
    public byte AutoRepair { get; set; } = 1;
    public byte TankCurrencyType { get; set; }
    public int GoldTankLeftTime { get; set; }
    public int CurTankCoatId { get; set; }
    public int CurRoomTankCoatID { get; set; }
    public int CurShellId { get; set; }
    public int ModificationId { get; set; }
    public bool IsElite { get; set; }
    public byte AutoFillCostFlag { get; set; }
    public string ShellSlots { get; set; } = "1,10;1,10;1,10";
    public string DeployShellSlots { get; set; } = "0,0;0,0;0,0";
    public string Provisions { get; set; } = "0;0;0";
    public string DeployProvisions { get; set; } = "0;0;0";
    public string SignatureIds { get; set; } = "0;0";
    public string SignatureTimes { get; set; } = "0;0";
    public string EmblemIds { get; set; } = "0;0;0;0";
    public string EmblemTimes { get; set; } = "0;0;0;0";
    public string BehaveArray { get; set; } = "0,0,1;0,0,1;0,0,1;0,0,1;0,0,1";
    public bool IsBattleTank { get; set; }
    public byte BattleSlot { get; set; }
}
