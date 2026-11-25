using System.Collections.Generic;
using GwtEmulator.Protocol;

namespace GwtEmulator.Protocol;

public struct ShellSlot
{
    public int ID;
    public int Count;
}

public struct BehaveStruct
{
    public byte BehaveType;
    public byte PrizeType;
    public float Multiple;
}

public class TankData
{
    public long TankGUID;
    public long PlayerGUID;
    public int CarportIndex;
    public int VehicleId;
    public int HullID;
    public int UnderpanID;
    public int TurretID;
    public int MainGunID;
    public int EngineID;
    public int FueltankID;
    public int CartridgeID;
    public int RadioStationID;
    public int GaugeID;
    public ShellSlot[] ShellSlots = new ShellSlot[3];
    public int TankHp;
    public int TankExp;
    public int TankCurHp;
    public ShellSlot[] DeployShellSlots = new ShellSlot[3];
    public int[] Provisions = new int[3];
    public int[] DeployProvisions = new int[3];
    public byte AutoRepair;
    public byte TankCurrencytType;
    public int GoldTankLeftTime;
    public int CurTankCoatId;
    public int CurRoomTankCoatID;
    public int CurShellId;
    public int[] SignatureId = new int[2];
    public long[] SignatureTime = new long[2];
    public int[] EmblemId = new int[4];
    public long[] EmblemTime = new long[4];
    public int ModificationId;
    public bool IsElite;
    public byte AutoFillCostFlag;
    public BehaveStruct[] BehaveArray = new BehaveStruct[5];
}

public class RoomMainTankData
{
    public int VehicleId;
    public int HullID;
    public int UnderpanID;
    public int TurretID;
    public int MainGunID;
    public int EngineID;
    public int FueltankID;
    public int CartridgeID;
    public int RadioStationID;
    public int GaugeID;
    public ShellSlot[] ShellSlots = new ShellSlot[3];
    public int[] Provisions = new int[3];
    public int[] DefaultProvisions = new int[3];
    public int TankHp;
    public int TankCurHp;
    public int TankCoatId;
    public int[] TankSignature = new int[2];
    public int[] EmblemIDs = new int[4];
}

public struct TankAllAttributeStruct
{
    public long TankGUID;
    public float[] Attributes;
}

public struct StructTankExp
{
    public int TankId;
    public int TankExp;
}

public struct ShellBuyStruct
{
    public int ShellID;
    public int Count;
}

public struct SetShellStruct
{
    public byte ShellSlotId;
    public int ShellID;
    public int Count;
}

public class PK_TankStatisticData
{
    public long TankStatisticGuid;
    public int TankID;
    public int KillNum;
    public int BeKillNum;
    public int ShootNum;
    public int HitTargetNum;
    public int FightNum;
    public int HarmVolume;
    public int Score;
    public int RepairSliver;
    public int MaxScore;
    public int MaxDamage;
    public byte Flag;
    public long PlayerGUID;
    public int TankExp;
    public int DestroyAmmobayNum;
    public int DestroyFueltankNum;
    public int DestroyTrackNum;
}

public class PK_PlayerInfo
{
    public byte Group;
    public long TeamGuid;
    public string PlayerName = string.Empty;
    public long PlayerGUID;
    public byte RoomerId;
    public string TroopsTag = string.Empty;
    public ushort TankCoatId;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public int MaxHp;
    public int CurHp;
    public ushort TankID;
    public ushort HullID;
    public ushort UnderpanID;
    public ushort TurretID;
    public ushort MainGunID;
    public ushort EngineID;
    public ushort FueltankID;
    public ushort CartridgeID;
    public ushort RadioStationID;
    public ushort GaugeID;
    public byte IsVisible;
    public byte FightCite;
    public float TankTotalWeight;
    public float TankPower;
    public float MaxForwardSpeed;
    public float MaxBackSpeed;
    public float RotateSpeed;
    public float EngineStartTime;
    public float MoveFastStartTime;
    public float MoveFastStartRate;
    public float TerrainResistanceHard;
    public byte IsVip;
    public int[] SignatureId = new int[2];
    public int[] EmblemId = new int[4];
    public short MovePackNum;
    public float SpwanGunAngle;
    public float SpwanTurretAngle;
    public int KillCount;
    public ShellSlot[] ShellSlots = new ShellSlot[3];
}

public class PK_RC_TankUnit
{
    public long PlayerGUID;
    public float PosX, PosY, PosZ;
    public int MaxHp;
    public int CurHp;
    public ushort VehicleId;
    public ushort HullID;
    public ushort ChassisID;
    public ushort TurretID;
    public ushort MainGunID;
    public ushort EngineID;
    public ushort FueltankID;
    public ushort CartridgeID;
    public ushort RadioStationID;
    public ushort GaugeID;
}

public class CrewInfo
{
    public int Id;
    public int CurExp;
    public int CurLv;
    public long Guid;
    public long TankGuid;
    public short FamilyNameId;
    public short NameId;
    public short BigIconId;
    public short SmallIconId;
    public short ResumeId;
    public int Lastlearn;
    public int LastSkillExp;
    public int LastExp;
    public int LastLv;
    public int[] SkillIds = new int[30];
}

public class MissionInfoModel
{
    public ushort MissionID;
    public int Schedule;
    public byte CompleteTag;
}

public class BattleEndInfoCommon
{
    public long RoomGuid;
    public int MapId;
    public byte VipOrNomal;
    public int Money;
    public int Exp;
    public int VipMoney;
    public int VipExp;
    public float[] VipExpMultiple = new float[4];
    public float[] VipMoneyMultiple = new float[4];
    public byte HasBehave;
    public float ExpMultiple;
    public float MoneyMultiple;
    public byte State;
    public byte Result;
    public byte EndReason;
    public int StartSecond;
    public byte IsGarageIncrease;
    public float SpeActivityIncrease;
}

public class TankBattleEndInfo
{
    public int tankId;
    public ushort shotCount;
    public ushort hitCount;
    public ushort priceCount;
    public int damage;
    public byte killEnermyCount;
    public byte damageEnermyCarCount;
    public ushort occupyValue;
    public ushort beHitedCount;
    public int lostHp;
    public int MaxHp;
    public int repairSliver;
    public int supplyCost;
    public int shellCost;
    public int LightDamage;
    public byte LightCount;
    public int ChassisShareDamage;
    public int PotentialDamage;
    public byte RoomType;
    public float groupReward;
    public int LoyalPoint;
}

public class PlayerBattleEndInfo
{
    public string playerName = string.Empty;
    public int tankId;
    public ushort damageCount;
    public ushort destroyModuleCount;
    public byte killOrDamage;
    public ushort Damage;
    public byte IsLight;
    public byte IsLighted;
    public int LightDamage;
    public int ShareDamage;
}

public class TankCoatData
{
    public ushort TableID;
    public int OverdueTime;
}

public class PK_TankCoatInfo
{
    /// <summary>Клиент держит РОВНО столько слотов камуфляжа (TankOwnCoat.CoatType):
    /// 0 = Ice (снежный), 1 = Field (полевой), 2 = Desert (пустынный).</summary>
    public const int CoatSlots = 3;

    /// <summary>ВНИМАНИЕ: это Table_Vehicle.id, а НЕ TankGUID.
    /// Клиент делает TryGetHallTank(TankID) и сравнивает с tank.TankData.VehicleId.</summary>
    public int TankID;

    public TankCoatData[] TankCoatArray = CreateSlots();

    /// <summary>EnTankCoatType надетого камуфляжа: 0..2 — слот, 3 = None (снят).</summary>
    public byte CurSelectType = 3;

    public static TankCoatData[] CreateSlots()
    {
        var arr = new TankCoatData[CoatSlots];
        for (int i = 0; i < CoatSlots; i++) arr[i] = new TankCoatData();
        return arr;
    }
}

public class TankEquipmentData
{
    public float CircularVisionRadiusRange;
    public float AimcircleSpeed;
    public float Stable;
    public float CommanderBuff;
    public float GunnerBuff;
    public float DriverBuff;
    public float LoaderBuff;
    public float RadiomanBuff;
    public float MemberExp;
    public float LoadBuff;
    public float ChassisHpBuff;
    public float TurretHpBuff;
    public float HpBuff;
    public float FragmentFactor;
    public float HideFactor;
    public float ReloadBuff;
    public float AmmobayNodamgePercentBuff;
    public float AmmobayHpBuff;
    public float PowerBuff;
    public float FueltankNodamgePercentBuff;
    public float FueltankHpBuff;
}

public class EquipmentInfor
{
    public long TankGuid;
    public TankEquipmentData TankEquData = new();
}

public struct HTankState
{
    public byte TankLockState;
    public long TankGuid;
}

public class PK_HC_ItemData
{
    public byte ItemType;
    public ushort ItemID;
    public int ItemNum;
}

public class PK_HC_TankEquipmentInfor
{
    public long TankGuid;
    public ushort[] EquipmentIDs = new ushort[36];
}
