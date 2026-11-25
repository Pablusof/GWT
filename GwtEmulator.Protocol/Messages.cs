namespace GwtEmulator.Protocol;

[MessagePortal(TkMsgType.NM_CL_LOGIN)]
public class PK_CL_PlayerLogin : BaseRequest
{
    public string PlayerName = string.Empty;
    public string PassWord = string.Empty;
    public int ClientVersion;
    public ushort VersionTemp;
    public int ResourceVersion;
    public byte LoginType;

    public PK_CL_PlayerLogin() => MsgType = TkMsgType.NM_CL_LOGIN;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerName = byteArray.ReadUTF(64);
        PassWord = byteArray.ReadUTF(30);
        ClientVersion = byteArray.ReadInt();
        VersionTemp = byteArray.ReadUShort();
        byteArray.Position += 6; // skip Version (2, 12764, 99)
        ResourceVersion = byteArray.ReadInt();
        LoginType = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUTF(PassWord, 30);
        byteArray.WriteInt(ClientVersion);
        byteArray.WriteVersion(VersionTemp);
        byteArray.WriteInt(ResourceVersion);
        byteArray.WriteByte(LoginType);
    }
}

[MessagePortal(TkMsgType.NM_LC_LGOIN_SUCCESS)]
public class PK_LC_LoginRet : BaseRequest
{
    public int Certification;
    public long PlayerGUID;
    public string Ip = string.Empty;
    public ushort ServerId;
    public ushort AgentId;
    public ushort Port;
    public int IsEncrypted = 1;

    public PK_LC_LoginRet() => MsgType = TkMsgType.NM_LC_LGOIN_SUCCESS;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Certification = byteArray.ReadInt();
        PlayerGUID = byteArray.ReadLong();
        Ip = byteArray.ReadUTF(20);
        Port = byteArray.ReadUShort();
        ServerId = byteArray.ReadUShort();
        AgentId = byteArray.ReadUShort();
        IsEncrypted = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        Console.WriteLine($"[PK_LC_LoginRet.WriteByte] Certification={Certification}, PlayerGUID={PlayerGUID}, Ip={Ip}, Port={Port}");
        byteArray.WriteInt(Certification);
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteUTF(Ip, 20);
        byteArray.WriteUShort(Port);
        byteArray.WriteUShort(ServerId);
        byteArray.WriteUShort(AgentId);
        byteArray.WriteInt(IsEncrypted);
    }
}

[MessagePortal(TkMsgType.NM_LC_LOGIN_FAIL)]
public class PK_LC_PlayerLogin_Fail : BaseRequest
{
    public ushort Flag;

    public PK_LC_PlayerLogin_Fail() => MsgType = TkMsgType.NM_LC_LOGIN_FAIL;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Flag = byteArray.ReadUShort();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUShort(Flag);
}

[MessagePortal(TkMsgType.NM_CH_LOGIN)]
public class PK_CH_PlayerLogin : BaseRequest
{
    public int Certification;
    public long PlayerGUID;
    public byte LoginType;

    public PK_CH_PlayerLogin() => MsgType = TkMsgType.NM_CH_LOGIN;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Certification = byteArray.ReadInt();
        PlayerGUID = byteArray.ReadLong();
        LoginType = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Certification);
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteByte(LoginType);
    }
}

[MessagePortal(TkMsgType.NM_HC_LOGIN_RESULT)]
public class PK_HC_LoginResult : BaseRequest
{
    public byte Flag;

    public PK_HC_LoginResult() => MsgType = TkMsgType.NM_HC_LOGIN_RESULT;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Flag = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Flag);
}

[MessagePortal(TkMsgType.NM_CH_Ping)]
public class PK_CH_Ping : BaseRequest
{
    public int Marker;

    public PK_CH_Ping() => MsgType = TkMsgType.NM_CH_Ping;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Marker = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(Marker);
}

[MessagePortal(TkMsgType.NM_HC_Ping)]
public class PK_HC_Ping : BaseRequest
{
    public int Marker;

    public PK_HC_Ping() => MsgType = TkMsgType.NM_HC_Ping;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Marker = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(Marker);
}

[MessagePortal(TkMsgType.NM_CR_Ping)]
public class PK_CR_Ping : BaseRequest
{
    public int Marker;

    public PK_CR_Ping() => MsgType = TkMsgType.NM_CR_Ping;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Marker = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(Marker);
}

[MessagePortal(TkMsgType.NM_RC_Ping)]
public class PK_RC_Ping : BaseRequest
{
    public int Marker;

    public PK_RC_Ping() => MsgType = TkMsgType.NM_RC_Ping;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Marker = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(Marker);
}

[MessagePortal(TkMsgType.NM_CR_PlayerLogin)]
public class PK_CR_PlayerLogin : BaseRequest
{
    public int Certification;
    public long PlayerGUID;
    public long RoomGUID;
    public byte WarType;

    public PK_CR_PlayerLogin() => MsgType = TkMsgType.NM_CR_PlayerLogin;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Certification = byteArray.ReadInt();
        PlayerGUID = byteArray.ReadLong();
        RoomGUID = byteArray.ReadLong();
        WarType = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Certification);
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteLong(RoomGUID);
        byteArray.WriteByte(WarType);
    }
}

[MessagePortal(TkMsgType.NM_RC_PlayerLoginSuccess)]
public class PK_RC_PlayerLoginSuccess : BaseRequest
{
    public byte Flag;
    public byte LockedTime;
    public byte MapId;
    public byte BattleFieldId;
    public ushort SpawnCoordId;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public byte Group;
    public byte TimeType;
    public byte WeatherEffectType;
    public float WeatherEffectIntensity;
    public int FogStartDistance;
    public int FogEndDistance;
    public float FogIntensity;
    public RoomMainTankData RoomMainTank = new();
    public TankAllAttributeStruct RoomMainTankAttribute;
    public byte LittleBattleMode;
    public float GunAngle;
    public float TurretAngle;
    public short MovePackNum;
    public byte IsTerritoryWar;
    public byte WarModel;
    public int BattleLevel;
    public bool FastChangeShell;

    public PK_RC_PlayerLoginSuccess() => MsgType = TkMsgType.NM_RC_PlayerLoginSuccess;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Flag = byteArray.ReadByte();
        LockedTime = byteArray.ReadByte();
        MapId = byteArray.ReadByte();
        BattleFieldId = byteArray.ReadByte();
        SpawnCoordId = byteArray.ReadUShort();
        PosX = byteArray.ReadFloat(); PosY = byteArray.ReadFloat(); PosZ = byteArray.ReadFloat();
        RotX = byteArray.ReadFloat(); RotY = byteArray.ReadFloat(); RotZ = byteArray.ReadFloat();
        Group = byteArray.ReadByte();
        TimeType = byteArray.ReadByte();
        WeatherEffectType = byteArray.ReadByte();
        WeatherEffectIntensity = byteArray.ReadFloat();
        FogStartDistance = byteArray.ReadInt();
        FogEndDistance = byteArray.ReadInt();
        FogIntensity = byteArray.ReadFloat();
        RoomMainTank = byteArray.ReadRoomMainTankData();
        RoomMainTankAttribute = byteArray.ReadTankAllAttributeStruct();
        LittleBattleMode = byteArray.ReadByte();
        GunAngle = byteArray.ReadShort() * 0.01f;
        TurretAngle = byteArray.ReadShort() * 0.01f;
        MovePackNum = byteArray.ReadShort();
        IsTerritoryWar = byteArray.ReadByte();
        WarModel = byteArray.ReadByte();
        BattleLevel = byteArray.ReadInt();
        FastChangeShell = byteArray.ReadByte() == 1;
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Flag);
        byteArray.WriteByte(LockedTime);
        byteArray.WriteByte(MapId);
        byteArray.WriteByte(BattleFieldId);
        byteArray.WriteUShort(SpawnCoordId);
        byteArray.WriteFloat(PosX); byteArray.WriteFloat(PosY); byteArray.WriteFloat(PosZ);
        byteArray.WriteFloat(RotX); byteArray.WriteFloat(RotY); byteArray.WriteFloat(RotZ);
        byteArray.WriteByte(Group);
        byteArray.WriteByte(TimeType);
        byteArray.WriteByte(WeatherEffectType);
        byteArray.WriteFloat(WeatherEffectIntensity);
        byteArray.WriteInt(FogStartDistance);
        byteArray.WriteInt(FogEndDistance);
        byteArray.WriteFloat(FogIntensity);
        byteArray.WriteRoomMainTankData(RoomMainTank);
        byteArray.WriteTankAllAttributeStruct(RoomMainTankAttribute);
        byteArray.WriteByte(LittleBattleMode);
        byteArray.WriteShort((short)(GunAngle * 100));
        byteArray.WriteShort((short)(TurretAngle * 100));
        byteArray.WriteShort(MovePackNum);
        byteArray.WriteByte(IsTerritoryWar);
        byteArray.WriteByte(WarModel);
        byteArray.WriteInt(BattleLevel);
        byteArray.WriteByte((byte)(FastChangeShell ? 1 : 0));
    }
}

[MessagePortal(TkMsgType.NM_CH_RequestJoinRoom)]
public class PK_CH_RequestJoinRoom : BaseRequest
{
    public byte BattleMode;
    public byte BattleLevel;
    public long TankGUID;
    public byte ProjectID;

    public PK_CH_RequestJoinRoom() => MsgType = TkMsgType.NM_CH_RequestJoinRoom;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        BattleMode = byteArray.ReadByte();
        BattleLevel = byteArray.ReadByte();
        TankGUID = byteArray.ReadLong();
        ProjectID = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(BattleMode);
        byteArray.WriteByte(BattleLevel);
        byteArray.WriteLong(TankGUID);
        byteArray.WriteByte(ProjectID);
    }
}

[MessagePortal(TkMsgType.NM_HC_JoinRoomRes)]
public class PK_HC_JoinRoomRes : BaseRequest
{
    public byte Flag;
    public int Certification;
    public string Ip = string.Empty;
    public ushort Port;
    public long RoomGUID;
    public long TankGUID;
    public byte IsObserver;

    public PK_HC_JoinRoomRes() => MsgType = TkMsgType.NM_HC_JoinRoomRes;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Flag = byteArray.ReadByte();
        Certification = byteArray.ReadInt();
        Ip = byteArray.ReadUTF(20);
        Port = byteArray.ReadUShort();
        RoomGUID = byteArray.ReadLong();
        TankGUID = byteArray.ReadLong();
        IsObserver = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Flag);
        byteArray.WriteInt(Certification);
        byteArray.WriteUTF(Ip, 20);
        byteArray.WriteUShort(Port);
        byteArray.WriteLong(RoomGUID);
        byteArray.WriteLong(TankGUID);
        byteArray.WriteByte(IsObserver);
    }
}

[MessagePortal(TkMsgType.NM_CH_CheckIntegrity)]
public class PK_CH_CheckIntegrity : BaseRequest
{
    public string[] AssetFiles = { "sharedassets0.assets", "sharedassets1.assets", "sharedassets2.assets", "sharedassets3.assets" };
    public string[] IntegrityHashes = new string[4];

    public PK_CH_CheckIntegrity() => MsgType = TkMsgType.NM_CH_CheckIntegrity;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        for (int i = 0; i < 4; i++)
        {
            byteArray.ReadUTF(20); // filename
            IntegrityHashes[i] = byteArray.ReadUTF(32); // md5 hash
        }
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        for (int i = 0; i < 4; i++)
        {
            byteArray.WriteUTF(AssetFiles[i], 20);
            byteArray.WriteUTF(IntegrityHashes[i], 32);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_ReqServerTime)]
public class PK_CH_ReqServerTime : BaseRequest
{
    public PK_CH_ReqServerTime() => MsgType = TkMsgType.NM_CH_ReqServerTime;
}

[MessagePortal(TkMsgType.NM_HC_RespServerTime)]
public class PK_HC_RespServerTime : BaseRequest
{
    public long UnixTime;
    public string TimeString = string.Empty;

    public PK_HC_RespServerTime() => MsgType = TkMsgType.NM_HC_RespServerTime;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        UnixTime = byteArray.ReadLong();
        TimeString = byteArray.ReadUTF(20);
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(UnixTime);
        byteArray.WriteUTF(TimeString, 20);
    }
}

[MessagePortal(TkMsgType.NM_RC_FightEnd)]
public class PK_RC_FightEnd : BaseRequest
{
    public byte ResultType;

    public PK_RC_FightEnd() => MsgType = TkMsgType.NM_RC_FightEnd;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ResultType = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(ResultType);
}

[MessagePortal(TkMsgType.NM_RC_WarStart)]
public class PK_RC_WarStart : BaseRequest
{
    public byte Context;

    public PK_RC_WarStart() => MsgType = TkMsgType.NM_RC_WarStart;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Context = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Context);
}

// Sent to a player at war-start to trigger the client's ReloadAmmoImmediately()
// path (OnTankShellChange -> _firstShellChanging -> SyncReliveClipCountFull ->
// StopRechargingClip -> FillShell). This is the SAME code path the client runs
// on revive, which is why the drum only ever filled after a respawn before.
// Flag=1 = "use this slot"; ShellId = the player's currently selected shell.
[MessagePortal(TkMsgType.NM_RC_TankShellChange)]
public class PK_RC_TankShellChange : BaseRequest
{
    public byte Flag;
    public int  ShellId;

    public PK_RC_TankShellChange() => MsgType = TkMsgType.NM_RC_TankShellChange;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Flag    = byteArray.ReadByte();
        ShellId = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Flag);
        byteArray.WriteInt(ShellId);
    }
}

[MessagePortal(TkMsgType.NM_RC_PlayerList)]
public class PK_RC_PlayerList : BaseRequest
{
    public ushort PlayerCount;

    public PK_RC_PlayerList() => MsgType = TkMsgType.NM_RC_PlayerList;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerCount = byteArray.ReadUShort();
        return this;
    }

    // Клиент читает это как TotalCount(ushort) + TotalCount * ReadPlayerInfoByDictate()
    // — ПОЛНЫЙ формат (с RoomerId и TroopsTag). Не путать с ReadPlayerInfo() из
    // PK_RC_NewPlayerLogin: там формат КОРОТКИЙ, см. WritePlayerInfoShort.
    // The full battle roster is delivered via PK_RC_TankBattleData (which actually creates
    // the remote players), so this list is sent empty (count 0). Writing a single byte here
    // (the old behaviour) made the client read a ushort that bled into the next message and
    // could loop on a garbage count -> over-read -> IndexOutOfRangeException on the room thread.
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUShort(0);
}

[MessagePortal(TkMsgType.NM_RC_SceneInfo)]
public class PK_RC_SceneInfo : BaseRequest
{
    // v59: ЖИВОЙ снос объекта в бою. Клиент (OnSyncSceneInfo) при загруженной карте зовёт
    // OnSyncCrashUp(Num, Point) — объект сносится сразу. А PK_RC_DestroyedObject клиент кладёт
    // в список пред-разрушенных, который ПОСЛЕ загрузки карты = null → исключение и объект не
    // сносится. Поэтому и релей игрока, и снос ботами шлём именно этим пакетом. Num = sid объекта.
    public ushort Num;
    public float PointX, PointY, PointZ;

    public PK_RC_SceneInfo() => MsgType = TkMsgType.NM_RC_SceneInfo;

    public override IRequest ReadByte(ByteArray byteArray) => this;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(Num);
        byteArray.WriteFloat(PointX);
        byteArray.WriteFloat(PointY);
        byteArray.WriteFloat(PointZ);
    }
}

[MessagePortal(TkMsgType.NM_RC_SceneInfoDir)]
public class PK_RC_SceneInfoDir : BaseRequest
{
    // v60: ЖИВОЕ ПАДЕНИЕ объекта (дерево/столб). Клиент (OnSyncSceneInfoDir) при загруженной карте
    // зовёт OnSync(Num, Dir) — объект ВАЛИТСЯ в сторону Dir. Для сноса ботом валящихся объектов
    // (can_fall) шлём именно это; Dir = направление от бота к объекту (валится «от тарана»).
    public ushort Num;
    public float DirX, DirY, DirZ;

    public PK_RC_SceneInfoDir() => MsgType = TkMsgType.NM_RC_SceneInfoDir;

    public override IRequest ReadByte(ByteArray byteArray) => this;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(Num);
        byteArray.WriteFloat(DirX);
        byteArray.WriteFloat(DirY);
        byteArray.WriteFloat(DirZ);
    }
}

[MessagePortal(TkMsgType.NM_CH_HALLCHAT)]
public class PK_CH_HallChat : BaseRequest
{
    // v: тот же баг, что и в PK_CH_HALLCHAT_ALL — клиент (decfilesgame/PK_CH_HallChat.cs)
    // шлёт PlayerGUID(long) ДВАЖДЫ (сначала как поле самого пакета, затем ещё раз внутри
    // Content.WriteBytes), потом PlayerName (UTF, фикс. 64 байта), потом Context (UTF,
    // переменная длина). ReadUTF() как одна строка читал мусор и падал за границы буфера.
    public long PlayerGUID;
    public long ContentPlayerGUID;
    public string PlayerName = string.Empty;
    public string Message = string.Empty;

    public PK_CH_HallChat() => MsgType = TkMsgType.NM_CH_HALLCHAT;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        ContentPlayerGUID = byteArray.ReadLong();
        PlayerName = byteArray.ReadUTF(64);
        Message = byteArray.ReadUTF();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_HC_HALL_CHAT_RES)]
public class PK_HC_HallChatRes : BaseRequest
{
    public string PlayerName = string.Empty;
    public string Message = string.Empty;

    public PK_HC_HallChatRes() => MsgType = TkMsgType.NM_HC_HALL_CHAT_RES;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUTF(Message);
    }
}

[MessagePortal(TkMsgType.NM_RC_Chat)]
public class PK_RC_Chat : BaseRequest
{
    // v49: формат приведён к клиенту (PK_RC_Chat.ReadByte в Assembly-CSharp):
    //   PlayerGUID (long) -> ChatType (byte) -> ContextStr (UTF).
    // Раньше сервер писал ChatType/PlayerName(64)/Message. Клиент читал первые 8 байт как
    // PlayerGUID (мусор), дальше всё съезжало — поэтому боевой чат и любые серверные
    // сообщения в центр экрана не отображались вовсе. Плюс клиентский CreateStr показывает
    // сообщение ТОЛЬКО если находит отправителя в комнате по PlayerGUID (GetPlayerByID),
    // поэтому в качестве отправителя серверных оповещений нужно слать GUID реального
    // игрока/бота из комнаты, а не выдуманное имя.
    public long   PlayerGUID;
    public byte   ChatType;
    public string Message = string.Empty;   // клиентский ContextStr

    public PK_RC_Chat() => MsgType = TkMsgType.NM_RC_Chat;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        ChatType   = byteArray.ReadByte();
        Message    = byteArray.ReadUTF();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteByte(ChatType);
        byteArray.WriteUTF(Message);
    }
}

[MessagePortal(TkMsgType.NM_CR_Chat)]
public class PK_CR_Chat : BaseRequest
{
    public byte ChatType;
    public string Message = string.Empty;

    public PK_CR_Chat() => MsgType = TkMsgType.NM_CR_Chat;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ChatType = byteArray.ReadByte();
        Message = byteArray.ReadUTF();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_FastChat)]
public class PK_RC_FastChat : BaseRequest
{
    public long PlayerGUID;
    public byte FastChatId;

    public PK_RC_FastChat() => MsgType = TkMsgType.NM_RC_FastChat;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        FastChatId = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteByte(FastChatId);
    }
}

[MessagePortal(TkMsgType.NM_CR_FastChat)]
public class PK_CR_FastChat : BaseRequest
{
    public byte FastChatId;

    public PK_CR_FastChat() => MsgType = TkMsgType.NM_CR_FastChat;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        FastChatId = byteArray.ReadByte();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_SelfChangeTank)]
public class PK_RC_SelfChangeTank : BaseRequest
{
    public long PlayerGUID;
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

    public PK_RC_SelfChangeTank() => MsgType = TkMsgType.NM_RC_SelfChangeTank;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteUShort(TankID);
        byteArray.WriteUShort(HullID);
        byteArray.WriteUShort(UnderpanID);
        byteArray.WriteUShort(TurretID);
        byteArray.WriteUShort(MainGunID);
        byteArray.WriteUShort(EngineID);
        byteArray.WriteUShort(FueltankID);
        byteArray.WriteUShort(CartridgeID);
        byteArray.WriteUShort(RadioStationID);
        byteArray.WriteUShort(GaugeID);
    }
}

[MessagePortal(TkMsgType.NM_CR_SelfTankMove)]
public class PK_CR_SelfTankMove : BaseRequest
{
    // Matches the client PK_CR_SelfTankMove.WriteByte:
    //   Pos(vec3) Rot(vec3) KeyCode(byte) CurSpeed(vec3) RotateSpeed(float)
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public byte KeyCode;
    public float SpeedX, SpeedY, SpeedZ;
    public float RotateSpeed;

    public PK_CR_SelfTankMove() => MsgType = TkMsgType.NM_CR_SelfTankMove;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PosX = byteArray.ReadFloat(); PosY = byteArray.ReadFloat(); PosZ = byteArray.ReadFloat();
        RotX = byteArray.ReadFloat(); RotY = byteArray.ReadFloat(); RotZ = byteArray.ReadFloat();
        KeyCode = byteArray.ReadByte();
        SpeedX = byteArray.ReadFloat(); SpeedY = byteArray.ReadFloat(); SpeedZ = byteArray.ReadFloat();
        RotateSpeed = byteArray.ReadFloat();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_CR_SelfTankGunTurrentState)]
public class PK_CR_SelfTankGunTurrentState : BaseRequest
{
    // Matches client PK_CR_SelfTankGunTurrentState.WriteByte:
    //   GunAngle(short, *100) TurretAngle(short, *100) CurDispersionFactor(ushort, *10000)
    public float GunAngle;
    public float TurretAngle;
    public float CurDispersionFactor;

    public PK_CR_SelfTankGunTurrentState() => MsgType = TkMsgType.NM_CR_SelfTankGunTurrentState;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        GunAngle = byteArray.ReadShort() * 0.01f;
        TurretAngle = byteArray.ReadShort() * 0.01f;
        CurDispersionFactor = byteArray.ReadUShort() * 0.0001f;
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_OtherTankGunTurrentState)]
public class PK_RC_OtherTankGunTurrentState : BaseRequest
{
    // Matches client PK_RC_OtherTankGunTurrentState.ReadByte:
    //   PlayerGUID(long) GunAngle(short, *100) TurretAngle(short, *100)
    public long PlayerGUID;
    public float GunAngle;
    public float TurretAngle;

    public PK_RC_OtherTankGunTurrentState() => MsgType = TkMsgType.NM_RC_OtherTankGunTurrentState;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteShort((short)(GunAngle * 100f));
        byteArray.WriteShort((short)(TurretAngle * 100f));
    }
}

[MessagePortal(TkMsgType.NM_CR_SelfTankMoveDictate)]
public class PK_CR_SelfTankMoveDictate : BaseRequest
{
    // Matches client PK_CR_SelfTankMoveDictate.WriteByte:
    //   DictateInfo(byte) ClientCurPosition(vec3) ClientCurRotation(vec3) MoveNumber(short)
    //   TankMoveNumber(byte) TankOldNum(byte) MoveState(byte) TankColliderAngle(byte)
    //   TankColliderDirection(byte) GunAngle(short,*100) TurretAngle(short,*100)
    //   CurDispersionFactor(ushort,*10000) nCount(int)
    public byte DictateInfo;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public short MoveNumber;
    public byte TankMoveNumber;
    public byte TankOldNum;
    public byte MoveState;
    public byte TankColliderAngle;
    public byte TankColliderDirection;
    public float GunAngle;
    public float TurretAngle;
    public float CurDispersionFactor;
    public int NCount;

    public PK_CR_SelfTankMoveDictate() => MsgType = TkMsgType.NM_CR_SelfTankMoveDictate;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        DictateInfo = byteArray.ReadByte();
        PosX = byteArray.ReadFloat(); PosY = byteArray.ReadFloat(); PosZ = byteArray.ReadFloat();
        RotX = byteArray.ReadFloat(); RotY = byteArray.ReadFloat(); RotZ = byteArray.ReadFloat();
        MoveNumber = byteArray.ReadShort();
        TankMoveNumber = byteArray.ReadByte();
        TankOldNum = byteArray.ReadByte();
        MoveState = byteArray.ReadByte();
        TankColliderAngle = byteArray.ReadByte();
        TankColliderDirection = byteArray.ReadByte();
        GunAngle = byteArray.ReadShort() * 0.01f;
        TurretAngle = byteArray.ReadShort() * 0.01f;
        CurDispersionFactor = byteArray.ReadUShort() * 0.0001f;
        NCount = byteArray.ReadInt();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_TankMoveDictate)]
public class PK_RC_TankMoveDictate : BaseRequest
{
    // Matches client PK_RC_TankMoveDictate.ReadByte:
    //   PlayerGuid(long) DictateInfo(byte) CurSpeed(vec3) MoveNumber(short) ClientCurPosition(vec3)
    //   ClientCurRotation(vec3) GunAngle(short,*100) TurretAngle(short,*100) CruiseGrade(byte) CurSlidingSpeed(vec3)
    public long PlayerGuid;
    public byte DictateInfo;
    public float SpeedX, SpeedY, SpeedZ;
    public short MoveNumber;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public float GunAngle;
    public float TurretAngle;
    public byte CruiseGrade;
    public float SlideSpeedX, SlideSpeedY, SlideSpeedZ;

    public PK_RC_TankMoveDictate() => MsgType = TkMsgType.NM_RC_TankMoveDictate;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGuid);
        byteArray.WriteByte(DictateInfo);
        byteArray.WriteFloat(SpeedX); byteArray.WriteFloat(SpeedY); byteArray.WriteFloat(SpeedZ);
        byteArray.WriteShort(MoveNumber);
        byteArray.WriteFloat(PosX); byteArray.WriteFloat(PosY); byteArray.WriteFloat(PosZ);
        byteArray.WriteFloat(RotX); byteArray.WriteFloat(RotY); byteArray.WriteFloat(RotZ);
        byteArray.WriteShort((short)(GunAngle * 100f));
        byteArray.WriteShort((short)(TurretAngle * 100f));
        byteArray.WriteByte(CruiseGrade);
        byteArray.WriteFloat(SlideSpeedX); byteArray.WriteFloat(SlideSpeedY); byteArray.WriteFloat(SlideSpeedZ);
    }
}

[MessagePortal(TkMsgType.NM_CR_RequestFire)]
public class PK_CR_RequestFire : BaseRequest
{
    public byte Word;

    public PK_CR_RequestFire() => MsgType = TkMsgType.NM_CR_RequestFire;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Word = byteArray.ReadByte();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_RequestFire)]
public class PK_RC_RequestFire : BaseRequest
{
    // The client only allows firing after receiving this ack with a valid shell.
    public short ShellId;
    public short ShellIndex;

    public PK_RC_RequestFire() => MsgType = TkMsgType.NM_RC_RequestFire;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort(ShellId);
        byteArray.WriteShort(ShellIndex);
    }
}

[MessagePortal(TkMsgType.NM_CR_AddClipFire)]
public class PK_CR_AddClipFire : BaseRequest
{
    public byte Word;

    public PK_CR_AddClipFire() => MsgType = TkMsgType.NM_CR_AddClipFire;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Word = byteArray.ReadByte();
        return this;
    }
}

// Sent by the client when the player selects a different shell slot mid-battle.
// Client ClientSocket/PK_CR_TankShellChange.WriteByte writes a single int: the SLOT
// index (0/1/2), NOT the shell id. The server must reply with PK_RC_TankShellChange or
// firing stays blocked on the client.
[MessagePortal(TkMsgType.NM_CR_TankShellChange)]
public class PK_CR_TankShellChange : BaseRequest
{
    public int Index;

    public PK_CR_TankShellChange() => MsgType = TkMsgType.NM_CR_TankShellChange;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Index = byteArray.ReadInt();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_CR_USECONSUMABLE)]
public class PK_CR_UseConsumable : BaseRequest
{
    public int   ProvisionId;
    public byte  ModuleType;
    public float PositionX;
    public float PositionY;
    public float PositionZ;

    public PK_CR_UseConsumable() => MsgType = TkMsgType.NM_CR_USECONSUMABLE;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ProvisionId = byteArray.ReadInt();
        ModuleType  = byteArray.ReadByte();
        PositionX   = byteArray.ReadFloat();
        PositionY   = byteArray.ReadFloat();
        PositionZ   = byteArray.ReadFloat();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(ProvisionId);
        byteArray.WriteByte(ModuleType);
        byteArray.WriteFloat(PositionX);
        byteArray.WriteFloat(PositionY);
        byteArray.WriteFloat(PositionZ);
    }
}

[MessagePortal(TkMsgType.NM_RC_USECONSUMABLE)]
public class PK_RC_UseConsumable : BaseRequest
{
    public byte SucceedFlag;
    public int  ProvisionId;
    public byte ModuleType;
    public int  AdditionalId;

    public PK_RC_UseConsumable() => MsgType = TkMsgType.NM_RC_USECONSUMABLE;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        SucceedFlag  = byteArray.ReadByte();
        ProvisionId  = byteArray.ReadInt();
        ModuleType   = byteArray.ReadByte();
        AdditionalId = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(SucceedFlag);
        byteArray.WriteInt(ProvisionId);
        byteArray.WriteByte(ModuleType);
        byteArray.WriteInt(AdditionalId);
    }
}

[MessagePortal(TkMsgType.NM_RC_Refresh_Consume_CD)]
public class PK_RC_Refresh_Consume_CD : BaseRequest
{
    public long   PlayerGUID;
    public long[] ConsumeCDMs = new long[3]; // client divides each slot value by 1000 -> seconds

    public PK_RC_Refresh_Consume_CD() => MsgType = TkMsgType.NM_RC_Refresh_Consume_CD;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        ConsumeCDMs = new long[3];
        for (int i = 0; i < 3; i++) ConsumeCDMs[i] = byteArray.ReadLong();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        for (int i = 0; i < 3; i++)
            byteArray.WriteLong(ConsumeCDMs != null && i < ConsumeCDMs.Length ? ConsumeCDMs[i] : 0L);
    }
}

[MessagePortal(TkMsgType.NM_RC_AddClipFire)]
public class PK_RC_AddClipFire : BaseRequest
{
    public byte Word;

    public PK_RC_AddClipFire() => MsgType = TkMsgType.NM_RC_AddClipFire;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Word);
    }
}

[MessagePortal(TkMsgType.NM_CR_LoadWarCompleted)]
public class PK_CR_LoadWarCompleted : BaseRequest
{
    public byte Context;

    public PK_CR_LoadWarCompleted() => MsgType = TkMsgType.NM_CR_LoadWarCompleted;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Context = byteArray.ReadByte();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_OtherTankMove)]
public class PK_RC_OtherTankMove : BaseRequest
{
    // Matches the client PK_RC_OtherTankMove.ReadByte:
    //   PlayerGUID(long) Clock(int) Pos(vec3) Rot(vec3) OperationCode(byte) MoveSpd(vec3) RotateSpeed(float)
    public long PlayerGUID;
    public int Clock;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public byte OperationCode;
    public float SpeedX, SpeedY, SpeedZ;
    public float RotateSpeed;

    public PK_RC_OtherTankMove() => MsgType = TkMsgType.NM_RC_OtherTankMove;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteInt(Clock);
        byteArray.WriteFloat(PosX); byteArray.WriteFloat(PosY); byteArray.WriteFloat(PosZ);
        byteArray.WriteFloat(RotX); byteArray.WriteFloat(RotY); byteArray.WriteFloat(RotZ);
        byteArray.WriteByte(OperationCode);
        byteArray.WriteFloat(SpeedX); byteArray.WriteFloat(SpeedY); byteArray.WriteFloat(SpeedZ);
        byteArray.WriteFloat(RotateSpeed);
    }
}

[MessagePortal(TkMsgType.NM_CR_TankFire)]
public class PK_CR_TankFire : BaseRequest
{
    // Matches the client PK_CR_TankFire.WriteByte:
    //   ShellId(short) ShellIndex(short) StartPos(vec3) IdealPos(vec3) GunDirection(vec3)
    //   TimeStamp(int) TankPos(vec3) TankRotation(vec3) TurretAngle(float) GunAngle(float)
    //   ShotDispersionRadiusMultiple(float)
    public short ShellId;
    public short ShellIndex;
    public float StartX, StartY, StartZ;
    public float IdealX, IdealY, IdealZ;
    public float GunDirX, GunDirY, GunDirZ;
    public int TimeStamp;
    public float TankPosX, TankPosY, TankPosZ;
    public float TankRotX, TankRotY, TankRotZ;
    public float TurretAngle;
    public float GunAngle;
    public float ShotDispersionRadiusMultiple;

    public PK_CR_TankFire() => MsgType = TkMsgType.NM_CR_TankFire;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ShellId = byteArray.ReadShort();
        ShellIndex = byteArray.ReadShort();
        StartX = byteArray.ReadFloat(); StartY = byteArray.ReadFloat(); StartZ = byteArray.ReadFloat();
        IdealX = byteArray.ReadFloat(); IdealY = byteArray.ReadFloat(); IdealZ = byteArray.ReadFloat();
        GunDirX = byteArray.ReadFloat(); GunDirY = byteArray.ReadFloat(); GunDirZ = byteArray.ReadFloat();
        TimeStamp = byteArray.ReadInt();
        TankPosX = byteArray.ReadFloat(); TankPosY = byteArray.ReadFloat(); TankPosZ = byteArray.ReadFloat();
        TankRotX = byteArray.ReadFloat(); TankRotY = byteArray.ReadFloat(); TankRotZ = byteArray.ReadFloat();
        TurretAngle = byteArray.ReadFloat();
        GunAngle = byteArray.ReadFloat();
        ShotDispersionRadiusMultiple = byteArray.ReadFloat();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_TankFire)]
public class PK_RC_TankFire : BaseRequest
{
    // Matches the client PK_RC_TankFire.ReadByte:
    //   ShellId(short) ShellIndex(short) PlayerGUID(long) StartPos(vec3) IdealPos(vec3)
    public short ShellId;
    public short ShellIndex;
    public long PlayerGUID;
    public float StartX, StartY, StartZ;
    public float IdealX, IdealY, IdealZ;

    public PK_RC_TankFire() => MsgType = TkMsgType.NM_RC_TankFire;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort(ShellId);
        byteArray.WriteShort(ShellIndex);
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteFloat(StartX); byteArray.WriteFloat(StartY); byteArray.WriteFloat(StartZ);
        byteArray.WriteFloat(IdealX); byteArray.WriteFloat(IdealY); byteArray.WriteFloat(IdealZ);
    }
}

[MessagePortal(TkMsgType.NM_RC_ShellHit)]
public class PK_RC_ShellHit : BaseRequest
{
    // Layout must match the client ClientSocket/PK_RC_ShellHit.ReadByte:
    // short ShellID, short ShellIndex, long AttackPlayerGuid, long HitPlayerGuid,
    // Vector3 RealWorldPosition, Vector3 RealLocalPosition, Vector3 HitNormal,
    // byte HitLayer, byte HitModel, byte ShellResult.
    public short ShellID;
    public short ShellIndex;
    public long AttackerGUID;
    public long TargetGUID;
    public float HitPosX, HitPosY, HitPosZ;       // RealWorldPosition
    public float LocalPosX, LocalPosY, LocalPosZ; // RealLocalPosition
    public float NormalX, NormalY, NormalZ;       // HitNormal
    public byte HitLayer;
    public byte HitModel;
    public byte ShellResult; // 0 = miss/bounce, 1 = non-penetrate, 2 = penetrate

    public PK_RC_ShellHit() => MsgType = TkMsgType.NM_RC_ShellHit;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort(ShellID);
        byteArray.WriteShort(ShellIndex);
        byteArray.WriteLong(AttackerGUID);
        byteArray.WriteLong(TargetGUID);
        byteArray.WriteFloat(HitPosX); byteArray.WriteFloat(HitPosY); byteArray.WriteFloat(HitPosZ);
        byteArray.WriteFloat(LocalPosX); byteArray.WriteFloat(LocalPosY); byteArray.WriteFloat(LocalPosZ);
        byteArray.WriteFloat(NormalX); byteArray.WriteFloat(NormalY); byteArray.WriteFloat(NormalZ);
        byteArray.WriteByte(HitLayer);
        byteArray.WriteByte(HitModel);
        byteArray.WriteByte(ShellResult);
    }
}

// Sent to the SHOOTER after their own shell reports a hit (NM_CR_ShellHit). The main
// player's shell sits in the client's "HitMain" state waiting for HasTouchDown, which
// ONLY this packet sets (Player.OnMainShootRes -> GunSync.UpdatePlayerShellHit). Without
// it every shell the local player fires freezes mid-air and never explodes/despawns.
// Layout must match client ClientSocket/PK_RC_MainShootRes.ReadByte: short ShellID,
// short ShellIndex, long AttackPlayerGuid, long HitPlayerGuid, Vector3 RealWorldPosition,
// Vector3 RealLocalPosition, Vector3 HitNormal, byte ShellResult.
[MessagePortal(TkMsgType.NM_RC_MainShootRes)]
public class PK_RC_MainShootRes : BaseRequest
{
    public short ShellID;
    public short ShellIndex;
    public long  AttackerGUID;
    public long  TargetGUID;
    public float HitPosX, HitPosY, HitPosZ;       // RealWorldPosition
    public float LocalPosX, LocalPosY, LocalPosZ; // RealLocalPosition
    public float NormalX, NormalY, NormalZ;       // HitNormal
    public byte  ShellResult;                     // 0 = miss/bounce, 1 = non-pen, 2 = penetrate

    public PK_RC_MainShootRes() => MsgType = TkMsgType.NM_RC_MainShootRes;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort(ShellID);
        byteArray.WriteShort(ShellIndex);
        byteArray.WriteLong(AttackerGUID);
        byteArray.WriteLong(TargetGUID);
        byteArray.WriteFloat(HitPosX); byteArray.WriteFloat(HitPosY); byteArray.WriteFloat(HitPosZ);
        byteArray.WriteFloat(LocalPosX); byteArray.WriteFloat(LocalPosY); byteArray.WriteFloat(LocalPosZ);
        byteArray.WriteFloat(NormalX); byteArray.WriteFloat(NormalY); byteArray.WriteFloat(NormalZ);
        byteArray.WriteByte(ShellResult);
    }
}

[MessagePortal(TkMsgType.NM_CR_ShellHit)]
public class PK_CR_ShellHit : BaseRequest
{
    // Client ClientSocket/PK_CR_ShellHit.WriteByte: short ShellID, short ShellIndex,
    // long HitPlayerGuid, Vector3 RealPostion, Vector3 HitNormal, byte HitLayer, byte HitModel,
    // int TimeStamp.
    public short ShellID;
    public short ShellIndex;
    public long TargetGUID;
    public float HitPosX, HitPosY, HitPosZ;
    public float NormalX, NormalY, NormalZ;
    public byte HitLayer;
    public byte HitModel;
    public int TimeStamp;

    public PK_CR_ShellHit() => MsgType = TkMsgType.NM_CR_ShellHit;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ShellID = byteArray.ReadShort();
        ShellIndex = byteArray.ReadShort();
        TargetGUID = byteArray.ReadLong();
        HitPosX = byteArray.ReadFloat(); HitPosY = byteArray.ReadFloat(); HitPosZ = byteArray.ReadFloat();
        NormalX = byteArray.ReadFloat(); NormalY = byteArray.ReadFloat(); NormalZ = byteArray.ReadFloat();
        HitLayer = byteArray.ReadByte();
        HitModel = byteArray.ReadByte();
        TimeStamp = byteArray.ReadInt();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_BeHited)]
public class PK_RC_BeHited : BaseRequest
{
    // Client ClientSocket/PK_RC_BeHited.ReadByte: long _hitGUID, byte hitType, byte ShellId.
    public long AttackerGUID;
    public byte HitType;
    public byte ShellId;

    public PK_RC_BeHited() => MsgType = TkMsgType.NM_RC_BeHited;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(AttackerGUID);
        byteArray.WriteByte(HitType);
        byteArray.WriteByte(ShellId);
    }
}

[MessagePortal(TkMsgType.NM_RC_SynHP)]
public class PK_RC_SynHP : BaseRequest
{
    // Client ClientSocket/PK_RC_SynHP.ReadByte: long AttackPlayerGuid, long PlayerGUID,
    // int BodyHP (current HP), byte Reason. There is NO MaxHp field.
    public long AttackerGUID;
    public long PlayerGUID;
    public int CurHp;
    public byte Reason;

    public PK_RC_SynHP() => MsgType = TkMsgType.NM_RC_SynHP;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(AttackerGUID);
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteInt(CurHp);
        byteArray.WriteByte(Reason);
    }
}

[MessagePortal(TkMsgType.NM_RC_TankDead)]
public class PK_RC_TankDead : BaseRequest
{
    // Client ClientSocket/PK_RC_TankDead.ReadByte: long AttackPlayerGUID, long DeadPlayerGUID.
    public long KillerGUID;
    public long PlayerGUID;

    public PK_RC_TankDead() => MsgType = TkMsgType.NM_RC_TankDead;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(KillerGUID);
        byteArray.WriteLong(PlayerGUID);
    }
}

public struct RebirthEntry
{
    public long PlayerGUID;
    public float PosX, PosY, PosZ;
}

// NOTE: the decompiled client's [MessageHandle(213)] PK_RC_Rebirth handler is a literal
// empty stub ({ }) — it parses fine but does nothing at all with the data. This class is
// kept only because the wire format was already reverse-engineered; do not use it to drive
// actual respawn behavior, use PK_RC_Revive below instead (RoomNetwork.OnRevive ->
// ReliveMode.OnTankRevive -> PlayerProxy.OnRelive, which actually recreates the tank
// object and re-runs the ground-snap raycast that real spawn gets).
[MessagePortal(TkMsgType.NM_RC_Rebirth)]
public class PK_RC_Rebirth : BaseRequest
{
    // Client ClientSocket/PK_RC_Rebirth.ReadByte: byte PlayerCount, then for each:
    // long PlayerGuid, Vector3 RebirthPos.
    public RebirthEntry[] Players = System.Array.Empty<RebirthEntry>();

    public PK_RC_Rebirth() => MsgType = TkMsgType.NM_RC_Rebirth;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte((byte)Players.Length);
        foreach (var p in Players)
        {
            byteArray.WriteLong(p.PlayerGUID);
            byteArray.WriteFloat(p.PosX); byteArray.WriteFloat(p.PosY); byteArray.WriteFloat(p.PosZ);
        }
    }
}

public struct RevivePlayerEntry
{
    public long PlayerGUID;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public short CurShellID;
}

// Matches the decompiled client's PK_RC_Revive.ReadByte exactly:
//   byte NoDamageTime; int PlayerCount; then per player:
//   long RevivePlayerGUID, Vector3 RevivePos, Vector3 ReviveRotation, short CurShellID.
// This is THE real respawn message (RoomNetwork.OnRevive -> ReliveMode.OnTankRevive ->
// PlayerProxy.OnRelive) — it destroys and recreates the tank object via InitMainPlayer/
// InitRemotePlayer, which re-runs SetPosition()'s ground raycast, exactly like initial
// spawn. ReliveMode.OnTankRevive also hard-requires the target's PlayerState to already be
// Dead (logs an error and skips otherwise), which our existing PK_RC_TankDead broadcast
// already sets via PlayerProxy.OnDead.
[MessagePortal(TkMsgType.NM_RC_Revive)]
public class PK_RC_Revive : BaseRequest
{
    public byte NoDamageTime;
    public RevivePlayerEntry[] Players = System.Array.Empty<RevivePlayerEntry>();

    public PK_RC_Revive() => MsgType = TkMsgType.NM_RC_Revive;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(NoDamageTime);
        byteArray.WriteInt(Players.Length);
        foreach (var p in Players)
        {
            byteArray.WriteLong(p.PlayerGUID);
            byteArray.WriteFloat(p.PosX); byteArray.WriteFloat(p.PosY); byteArray.WriteFloat(p.PosZ);
            byteArray.WriteFloat(p.RotX); byteArray.WriteFloat(p.RotY); byteArray.WriteFloat(p.RotZ);
            byteArray.WriteShort(p.CurShellID);
        }
    }
}

[MessagePortal(TkMsgType.NM_RC_ReviveTime)]
public class PK_RC_ReviveTime : BaseRequest
{
    public int ReviveTimeLeft;

    public PK_RC_ReviveTime() => MsgType = TkMsgType.NM_RC_ReviveTime;

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(ReviveTimeLeft);
}

[MessagePortal(TkMsgType.NM_RC_PlayerLogout)]
public class PK_RC_PlayerLogout : BaseRequest
{
    public long PlayerGUID;

    public PK_RC_PlayerLogout() => MsgType = TkMsgType.NM_RC_PlayerLogout;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        return this;
    }

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(PlayerGUID);
}

[MessagePortal(TkMsgType.NM_CR_PlayerLogout)]
public class PK_CR_PlayerLogout : BaseRequest
{
    public PK_CR_PlayerLogout() => MsgType = TkMsgType.NM_CR_PlayerLogout;
}

[MessagePortal(TkMsgType.NM_RC_TimeCountDown)]
public class PK_RC_TimeCountDown : BaseRequest
{
    public int TimeLeft;

    public PK_RC_TimeCountDown() => MsgType = TkMsgType.NM_RC_TimeCountDown;

    // Client reads a single byte (LastTime). Writing an int desynced the per-message buffer.
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte((byte)Math.Clamp(TimeLeft, 0, 255));
}

[MessagePortal(TkMsgType.NM_RC_Score)]
public class PK_RC_Score : BaseRequest
{
    // Client ClientSocket/PK_RC_Score.ReadByte: a single int Score (base capture/point sync).
    public int Score;

    public PK_RC_Score() => MsgType = TkMsgType.NM_RC_Score;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Score);
    }
}

// Drives the top "Наши / Противник" progress bars in Respawn (Relive) mode.
// Client ClientSocket/PK_RC_ReviveScore.ReadByte reads, in order:
//   ushort TargetScore  – the score needed to win (bar maximum)
//   ushort GoodScore    – the "good" team's current score (filled from Group's view)
//   ushort BadScore     – the opposing team's current score
//   byte   Group        – which group counts as "good" for the receiving client
// Handled by ReliveModeOccupyValueManager.HandleSyncReviveScore (msgType 357). Without
// this message the bars stay at 0/0 and never move, which is the bug being fixed.
public class PK_RC_ReviveScore : BaseRequest
{
    public ushort TargetScore;
    public ushort GoodScore;
    public ushort BadScore;
    public byte   Group;

    public PK_RC_ReviveScore() => MsgType = TkMsgType.NM_RC_ReviveScore;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(TargetScore);
        byteArray.WriteUShort(GoodScore);
        byteArray.WriteUShort(BadScore);
        byteArray.WriteByte(Group);
    }
}

public struct PlayerScoreEntry
{
    public long PlayerGUID;
    public int Score;
}

[MessagePortal(TkMsgType.NM_RC_PlayerScore)]
public class PK_RC_PlayerScore : BaseRequest
{
    // Client ClientSocket/PK_RC_PlayerScore.ReadByte: byte PlayerCount, then for each:
    // long PlayerGuid, int Score. (Client warns and bails if PlayerCount <= 0, so always send >=1.)
    public PlayerScoreEntry[] Players = System.Array.Empty<PlayerScoreEntry>();

    public PK_RC_PlayerScore() => MsgType = TkMsgType.NM_RC_PlayerScore;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte((byte)Players.Length);
        foreach (var p in Players)
        {
            byteArray.WriteLong(p.PlayerGUID);
            byteArray.WriteInt(p.Score);
        }
    }
}

// NM_RC_TabInfo (488): Updates the team kill-count progress bars at the top of the screen.
// Client TabInfo.OnTabSyn: reads int GoodDestroyCount (allied kills) and int BadDestroyCount
// (enemy kills). Both bars fill toward the win-score cap.
[MessagePortal(TkMsgType.NM_RC_TabInfo)]
public class PK_RC_TabInfo : BaseRequest
{
    // v13 (баг 4): клиент читает PlayerCount ПЕРВЫМ полем, затем счётчики команд и
    // список {Guid(long), Destroyed(int), Damage(ushort)} по каждому игроку — колонки
    // «УНИЧТОЖИЛ»/«УРОН» в табе. Раньше писались только 2 int и GoodDestroyCount
    // съедался как PlayerCount — отсюда мусор в таблице.
    public int GoodDestroyCount; // kills by the player's own team
    public int BadDestroyCount;  // kills by the enemy team
    public (long Guid, int Destroyed, ushort Damage)[] Players =
        System.Array.Empty<(long, int, ushort)>();

    public PK_RC_TabInfo() => MsgType = TkMsgType.NM_RC_TabInfo;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Players.Length);
        byteArray.WriteInt(GoodDestroyCount);
        byteArray.WriteInt(BadDestroyCount);
        foreach (var p in Players)
        {
            byteArray.WriteLong(p.Guid);
            byteArray.WriteInt(p.Destroyed);
            byteArray.WriteUShort(p.Damage);
        }
    }
}

// PK_RC_LittleMapSyn — server pushes ALL visible tank positions in one packet.
// Client reads: PlayerNum(int), then N × {GUID(long), X(int), Y(int)}.
// This is the correct packet for minimap position broadcasting (NOT LittleMapDictated).
[MessagePortal(TkMsgType.NM_RC_LittleMapSyn)]
public class PK_RC_LittleMapSyn : BaseRequest
{
    public List<(long Guid, int X, int Y)> Players = new();
    public PK_RC_LittleMapSyn() => MsgType = TkMsgType.NM_RC_LittleMapSyn;
    public override IRequest ReadByte(ByteArray ba)
    {
        int n = ba.ReadInt();
        for (int i = 0; i < n; i++) Players.Add((ba.ReadLong(), ba.ReadInt(), ba.ReadInt()));
        return this;
    }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(Players.Count);
        foreach (var (g, x, y) in Players) { ba.WriteLong(g); ba.WriteInt(x); ba.WriteInt(y); }
    }
}

[MessagePortal(TkMsgType.NM_CR_LittleMapDictated)]
public class PK_CR_LittleMapDictated : BaseRequest
{
    public float PosX, PosZ;

    public PK_CR_LittleMapDictated() => MsgType = TkMsgType.NM_CR_LittleMapDictated;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PosX = byteArray.ReadFloat();
        PosZ = byteArray.ReadFloat();
        return this;
    }
}

// PK_RC_LittleMapDictated — "Внимание!" attention ping.
// Client reads: PlayerGUId(long), GuidX(byte), GuidY(byte), Type(byte).
// Triggers chat label + minimap attention marker. NOT for regular position sync.
[MessagePortal(TkMsgType.NM_RC_LittleMapDictated)]
public class PK_RC_LittleMapDictated : BaseRequest
{
    public long PlayerGUID;
    public byte GuidX;   // minimap X byte (0-255, scaled from world X)
    public byte GuidY;   // minimap Y byte (0-255, scaled from world Z)
    public byte Type;

    public PK_RC_LittleMapDictated() => MsgType = TkMsgType.NM_RC_LittleMapDictated;

    public override IRequest ReadByte(ByteArray ba)
    { PlayerGUID = ba.ReadLong(); GuidX = ba.ReadByte(); GuidY = ba.ReadByte(); Type = ba.ReadByte(); return this; }

    public override void WriteByte(ByteArray ba)
    { ba.WriteLong(PlayerGUID); ba.WriteByte(GuidX); ba.WriteByte(GuidY); ba.WriteByte(Type); }
}

[MessagePortal(TkMsgType.NM_RC_PlayerConnectState)]
public class PK_RC_PlayerConnectState : BaseRequest
{
    public long PlayerGUID;
    public byte State;

    public PK_RC_PlayerConnectState() => MsgType = TkMsgType.NM_RC_PlayerConnectState;

    // Client reads State(byte) FIRST, then PlayerGUID(long). The old order (GUID then State)
    // shifted the long by one byte -> garbage GUID (e.g. 144115188075855871) -> "Not find player".
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(State);
        byteArray.WriteLong(PlayerGUID);
    }
}

[MessagePortal(TkMsgType.NM_RC_WidgetState)]
public class PK_RC_WidgetState : BaseRequest
{
    // Клиент читает: long PlayerGUID, long AttackPlayerGuid, byte Count, Count×{byte Type,Param,State}.
    // Type = EComponent (2=LeftChassis, 3=RightChassis, 6=Engine, 8=AmmoBay, 9=FuelTank, ...).
    // State = модульное состояние (0=Normal, 1=Injure, 2=Damage).
    public long PlayerGUID;
    public long AttackPlayerGuid;
    public (byte Type, byte Param, byte State)[] Widgets = System.Array.Empty<(byte, byte, byte)>();

    public PK_RC_WidgetState() => MsgType = TkMsgType.NM_RC_WidgetState;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteLong(AttackPlayerGuid);
        byteArray.WriteByte((byte)Widgets.Length);
        foreach (var w in Widgets)
        {
            byteArray.WriteByte(w.Type);
            byteArray.WriteByte(w.Param);
            byteArray.WriteByte(w.State);
        }
    }
}

// v23: экран очереди РЕЖИМА ОБОРОНЫ (id 394). Отдельный от PvP-экрана: показывает
// «Игроков в бою: N/M», «Ожидается: K», «Участники боя», левую таблицу по уровням
// машин (I..XI) и правую по уровням боя (I..V).
[MessagePortal(TkMsgType.NM_HC_PVEBeginQueueWait)]
public class PK_HC_PVEBeginQueueWait : BaseRequest
{
    public byte     Success = 1;
    public byte     CurrentCount;          // сколько игроков уже в бою
    public byte     TotalCount = 5;        // сколько нужно
    public int      TotalWaitingCount;     // «Участники боя»
    public ushort[] BattleLevelCount = new ushort[5];   // правая таблица (уровни боя)
    public ushort[] TankLevelCount   = new ushort[11];  // левая таблица (уровни машин)
    public byte[]   CanEnterBattleLevel = { 1, 1, 1 };
    public byte     BattleMode;

    public PK_HC_PVEBeginQueueWait() => MsgType = TkMsgType.NM_HC_PVEBeginQueueWait;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(Success);
        ba.WriteByte(CurrentCount);
        ba.WriteByte(TotalCount);
        ba.WriteInt(TotalWaitingCount);
        for (int i = 0; i < 5;  i++) ba.WriteUShort(i < BattleLevelCount.Length ? BattleLevelCount[i] : (ushort)0);
        for (int i = 0; i < 11; i++) ba.WriteUShort(i < TankLevelCount.Length   ? TankLevelCount[i]   : (ushort)0);
        for (int i = 0; i < 3;  i++) ba.WriteByte(i < CanEnterBattleLevel.Length ? CanEnterBattleLevel[i] : (byte)1);
        ba.WriteByte(BattleMode);
    }
}

[MessagePortal(TkMsgType.NM_HC_UpdateWaitingInfo)]
public class PK_HC_UpdateWaitingInfo : BaseRequest
{
    public byte CurrentCount;
    public byte TotalCount = 6;

    public PK_HC_UpdateWaitingInfo() => MsgType = TkMsgType.NM_HC_UpdateWaitingInfo;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(CurrentCount);
        ba.WriteByte(TotalCount);
    }
}

[MessagePortal(TkMsgType.NM_HC_PVEFreshWaitingBattle)]
public class PK_HC_PVEFreshWaitingBattle : BaseRequest
{
    public int TotalCount;
    public ushort[] BattleLevelCount = new ushort[5];
    public ushort[] TankLevelCount = new ushort[11];

    public PK_HC_PVEFreshWaitingBattle() => MsgType = TkMsgType.NM_HC_PVEFreshWaitingBattle;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(TotalCount);
        for (int i = 0; i < 5; i++) ba.WriteUShort(i < BattleLevelCount.Length ? BattleLevelCount[i] : (ushort)0);
        for (int i = 0; i < 11; i++) ba.WriteUShort(i < TankLevelCount.Length ? TankLevelCount[i] : (ushort)0);
    }
}

// v16 (баг 3): вкладка «РЕЗУЛЬТАТ КОМАНДЫ» в окне «Статистика боя» (id 231).
// Клиент: GroupStatisticsDataManager.OnSynTeamResultDataHandler. Раньше не слалось
// вообще — отсюда пустая таблица.
[MessagePortal(TkMsgType.NM_HC_TeamBattleResult)]
public class PK_HC_TeamBattleResult : BaseRequest
{
    public long RoomGUID;
    public System.Collections.Generic.List<TeamResultRow> Rows = new();
    public PK_HC_TeamBattleResult() => MsgType = TkMsgType.NM_HC_TeamBattleResult;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(RoomGUID);
        ba.WriteByte((byte)Math.Min(255, Rows.Count));
        foreach (var r in Rows)
        {
            ba.WriteLong(r.Guid);
            ba.WriteUTF(r.PlayerName, 64);
            ba.WriteUTF(r.CorpsName, 11);
            ba.WriteByte(r.Side);            // EBattleSide: сторона (0 своя / 1 враг)
            ba.WriteInt(r.TankId);
            ba.WriteInt(r.Hurt);
            ba.WriteInt(r.Kill);
            ba.WriteInt(r.BaseExp);
            ba.WriteInt(r.SilverCoin);
            ba.WriteInt(r.Title);
            ba.WriteLong(r.TeamGUID);
            ba.WriteFloat(r.ExpMultiWithVip);
            ba.WriteInt(r.SilverWithVip);
        }
    }
}

public class TeamResultRow
{
    public long   Guid;
    public string PlayerName = "";
    public string CorpsName  = "";
    public byte   Side;
    public int    TankId, Hurt, Kill, BaseExp, SilverCoin, Title;
    public long   TeamGUID;
    public float  ExpMultiWithVip = 1.5f;
    public int    SilverWithVip;
}

// v14 (баг 7): явный сброс погоды/тумана уже в бою. Клиент: RoomNetwork ->
// CurRoomFog*, дальше MapEntity/EnvirEffectEntity выставляют RenderSettings.fog*.
[MessagePortal(TkMsgType.NM_RC_SynWeatherEffect)]
public class PK_RC_SynWeatherEffect : BaseRequest
{
    public byte  CurWeatherEffectType;
    public float CurWeatherEffectIntensity;
    public int   FogStartDistance = 30000;
    public int   FogEndDistance   = 50000;
    public float FogIntensity;
    public PK_RC_SynWeatherEffect() => MsgType = TkMsgType.NM_RC_SynWeatherEffect;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(CurWeatherEffectType);
        ba.WriteFloat(CurWeatherEffectIntensity);
        ba.WriteInt(FogStartDistance);
        ba.WriteInt(FogEndDistance);
        ba.WriteFloat(FogIntensity);
    }
}

// v61 (пункт 3): нативный прогноз погоды. Клиент RoomTips.StartWeahterForcast рисует
// "надвигается <погода> через N сек" (battle_message_weather_start) либо "<погода>
// закончится через N сек" (battle_message_weather_end) и сам ведёт посекундный отсчёт.
// Time — секунды до смены; NextWeather — id строки Table_Weather следующей погоды
// (17 = снег, 16 = ясно). id 0 невалиден. Пакет только исходящий (id 277).
public class PK_RC_WeatherForecast : BaseRequest
{
    public byte Time;
    public byte NextWeather;
    public PK_RC_WeatherForecast() => MsgType = TkMsgType.NM_RC_WeatherForecast;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(Time);
        ba.WriteByte(NextWeather);
    }
}

// v62 (пункт 7): всплывающая панель «мои попадания» (UIPlayerWarDamageInfoListPanel, до 5 строк,
// справа сверху). Клиент читает: Long(игнор), Long(GUID цели), Int(суммарный урон),
// Int(суммарные пробития), Int(урон этого выстрела), Int(id снаряда). Имя/танк цели клиент
// достаёт по GUID сам. id пакета 333.
public class PK_RC_DamageDetailInfo : BaseRequest
{
    public long VictimGuid;
    public int  TotalDamage, TotalPuncture, ThisDamage, ShellId;
    public PK_RC_DamageDetailInfo() => MsgType = TkMsgType.NM_RC_DamageDetailInfo;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(0);
        ba.WriteLong(VictimGuid);
        ba.WriteInt(TotalDamage);
        ba.WriteInt(TotalPuncture);
        ba.WriteInt(ThisDamage);
        ba.WriteInt(ShellId);
    }
}

// v68 (п.2): пополнение боеприпасов — сервер шлёт этот пакет при использовании PVE-скилла
// «пополнение боезапаса» (skill 2). Клиентский OnResetAmmo полностью восполняет боезапас
// (ResetShellSlotData + ResetShellCount + обновление UI). Раньше сервер только менял свой
// счётчик, а клиент не узнавал → боезапас не пополнялся. id 263. Клиент читает 3 int.
public class PK_RC_ResetAmmo : BaseRequest
{
    public int Shell1Num;
    public int Shell2Num;
    public int Shell3Num;
    public PK_RC_ResetAmmo() => MsgType = TkMsgType.NM_RC_ResetAmmo;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(Shell1Num);
        ba.WriteInt(Shell2Num);
        ba.WriteInt(Shell3Num);
    }
}

// v65 (п.2): удаление танка из ангара клиента (после продажи). Клиентский OnHCSellItem пуст,
// поэтому проданный танк не убирался с полки — плитка оставалась. Этот пакет (HandleDeleteTank)
// делает data.Count-- и Player.DeleteTank. Клиент читает Int(TankId=VehicleId), Long(TankGUID). id 219.
public class PK_HC_DeleteTank : BaseRequest
{
    public int  TankId;
    public long TankGUID;
    public PK_HC_DeleteTank() => MsgType = TkMsgType.NM_HC_DELETETANK;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(TankId);
        ba.WriteLong(TankGUID);
    }
}

// v62 (пункт 7): всплывающая панель «попадания по мне» (слева снизу). Клиент читает:
// Long(GUID атакующего), Long(игнор), Int(урон), Int(id снаряда), Byte(тип EShowHitType:
// 1=рикошет, 2=Strike, 3=крит/Vital, 4=урон). id пакета 255.
public class PK_RC_BeDamageDetailInfo : BaseRequest
{
    public long AttackerGuid;
    public int  ThisDamage, ShellId;
    public byte HitType;
    public PK_RC_BeDamageDetailInfo() => MsgType = TkMsgType.NM_RC_BeDamageDetailInfo;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(AttackerGuid);
        ba.WriteLong(0);
        ba.WriteInt(ThisDamage);
        ba.WriteInt(ShellId);
        ba.WriteByte(HitType);
    }
}

// v13 (баг 8): следы от снарядов на броне. Клиент-стрелок после своего попадания
// шлёт PK_CR_ShellDecal (id 135); сервер обязан разослать остальным PK_RC_ShellDecal
// (id 466) с TypeDecal = результат выстрела (0=пробитие-дырка, 1=непробитие, 4=рикошет)
// и ShellCaliber (размер отметины). За ботов декали синтезирует сам сервер в ResolveHit.
[MessagePortal(TkMsgType.NM_CR_ShellDecal)]
public class PK_CR_ShellDecal : BaseRequest
{
    public short ShellId, ShellIndex;
    public long  AttackedPlayerGuid;
    public byte  PartId;
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public PK_CR_ShellDecal() => MsgType = TkMsgType.NM_CR_ShellDecal;
    public override IRequest ReadByte(ByteArray ba)
    {
        ShellId    = ba.ReadShort();
        ShellIndex = ba.ReadShort();
        AttackedPlayerGuid = ba.ReadLong();
        PartId = ba.ReadByte();
        PosX = ba.ReadFloat(); PosY = ba.ReadFloat(); PosZ = ba.ReadFloat();
        NormX = ba.ReadFloat(); NormY = ba.ReadFloat(); NormZ = ba.ReadFloat();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_ShellDecal)]
public class PK_RC_ShellDecal : BaseRequest
{
    public long  AttackedPlayerGuid;
    public byte  PartId;
    public byte  TypeDecal;      // = ShellResult: 0 пробил, 1 не пробил, 4 рикошет
    public float PosX, PosY, PosZ;
    public float NormX, NormY, NormZ;
    public int   ShellCaliber = 122;
    public PK_RC_ShellDecal() => MsgType = TkMsgType.NM_RC_ShellDecal;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(AttackedPlayerGuid);
        ba.WriteByte(PartId);
        ba.WriteByte(TypeDecal);
        ba.WriteFloat(PosX); ba.WriteFloat(PosY); ba.WriteFloat(PosZ);
        ba.WriteFloat(NormX); ba.WriteFloat(NormY); ba.WriteFloat(NormZ);
        ba.WriteInt(ShellCaliber);
    }
}

// v12: подсказка СТРЕЛКУ (id 474). Клиент: RoomTips.OnRoomTips → ShootTips.OnSynShow(ShootTip)
// + NamePanelManager.ShowDamageTipsAnimation (летящие цифры урона).
// ShootTip = EShootTip: 3=уничтожил боеукладку, 4=уничтожил бак, 5=уничтожил гусеницу,
// 6=повредил боеукладку, 7=повредил бак, 8=повредил гусеницу, 9=непробитие, 10=рикошет, 11=попал в корпус.
[MessagePortal(TkMsgType.NM_RC_ShootTip)]
public class PK_RC_ShootTip : BaseRequest
{
    public long PlayerGuid;    // жертва (в чей танк попали)
    public int  ShootFlag;
    public int  Damage;
    public byte ShootTip;
    public PK_RC_ShootTip() => MsgType = TkMsgType.NM_RC_ShootTip;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(PlayerGuid);
        ba.WriteInt(ShootFlag);
        ba.WriteInt(Damage);
        ba.WriteByte(ShootTip);
    }
}

// v12: подсказки ЖЕРТВЕ (id 343) — крупные надписи в центре (HitTips.OnCenterTips).
// EBeShootTip: 1=гусеница повреждена, 2=боеукладка повреждена, 3=бак повреждён,
// 4=ГУСЕНИЦА УНИЧТОЖЕНА, 5=БАК УНИЧТОЖЕН, 6=убит врагом, 8=убит взрывом боеукладки,
// 10=ПОЖАР (загорелся бак).
[MessagePortal(TkMsgType.NM_RC_BeShootedTip)]
public class PK_RC_BeShootedTip : BaseRequest
{
    public long   AttackerGuid;
    public byte[] BeShootTips = System.Array.Empty<byte>();
    public PK_RC_BeShootedTip() => MsgType = TkMsgType.NM_RC_BeShootedTip;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(AttackerGuid);
        ba.WriteByte((byte)BeShootTips.Length);
        foreach (var t in BeShootTips) ba.WriteByte(t);
    }
}

// v12: смена состояния маскировки ПОЛУЧАТЕЛЯ (id 272). Клиент: HitTips.OnCoverStateChange —
// CoverState=0 → «battle_message_camo_out» (ОБНАРУЖЕН), 1 → «battle_message_camo_in» (скрыт).
[MessagePortal(TkMsgType.NM_RC_SynCoverPoint)]
public class PK_RC_SynCoverState : BaseRequest
{
    public byte CoverState;
    public PK_RC_SynCoverState() => MsgType = TkMsgType.NM_RC_SynCoverPoint;
    public override void WriteByte(ByteArray ba) => ba.WriteByte(CoverState);
}

[MessagePortal(TkMsgType.NM_RC_ViewState)]
public class PK_RC_ViewState : BaseRequest
{
    // v19: формат был НЕВЕРНЫЙ — писали long+byte, а клиент читает
    // PlayerCount(byte) + PlayerCount×PK_ViewInfo{ PlayerGUID(long), ViewState(byte) }.
    // Из-за этого сообщение вообще не работало. Это единственный способ ВЕРНУТЬ
    // видимость танку: GameRoom.OnTankViewStateChange -> IsVisible = (ViewState == 2).
    // PK_RC_BeLighted (197) видимость НЕ меняет — в обычных режимах его обработчик
    // пустой (переопределён только в CommanderRoom и лишь помечает врага в UI).
    public (long Guid, byte State)[] Players = System.Array.Empty<(long, byte)>();

    // Совместимость со старыми вызовами: одиночная пара.
    public long PlayerGUID { set { Players = new[] { (value, ViewStateValue) }; } }
    public byte ViewState  { set { ViewStateValue = value; if (Players.Length == 1) Players[0].State = value; } }
    private byte ViewStateValue;

    public PK_RC_ViewState() => MsgType = TkMsgType.NM_RC_ViewState;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte((byte)Math.Min(255, Players.Length));
        foreach (var p in Players)
        {
            ba.WriteLong(p.Guid);
            ba.WriteByte(p.State);
        }
    }
}

[MessagePortal(TkMsgType.NM_RC_CommanderState)]
public class PK_RC_CommanderState : BaseRequest
{
    public long PlayerGUID;
    public byte State;

    public PK_RC_CommanderState() => MsgType = TkMsgType.NM_RC_CommanderState;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteByte(State);
    }
}

// Server -> client: pop the "do you want to run for commander?" dialog.
[MessagePortal(TkMsgType.NM_RC_AskCommanderReq)]
public class PK_RC_AskCommanderReq : BaseRequest
{
    public byte PackageFlag;
    public PK_RC_AskCommanderReq() => MsgType = TkMsgType.NM_RC_AskCommanderReq;
    public override void WriteByte(ByteArray ba) => ba.WriteByte(PackageFlag);
}

// Client -> server: the player's answer (1=OK, 2=NO, 3=Close).
[MessagePortal(TkMsgType.NM_CR_AskCommanderResp)]
public class PK_CR_AskCommanderResp : BaseRequest
{
    public byte AskCommanderResult;
    public PK_CR_AskCommanderResp() => MsgType = TkMsgType.NM_CR_AskCommanderResp;
    public override IRequest ReadByte(ByteArray ba) { AskCommanderResult = ba.ReadByte(); return this; }
}

// Server -> client: a general's HP bar at the top (Side 0/1, Score=cur HP, Para=max HP).
[MessagePortal(TkMsgType.NM_RC_CommanderScore)]
public class PK_RC_CommanderScore : BaseRequest
{
    public byte Side;
    public int  Score;
    public int  Para;
    public PK_RC_CommanderScore() => MsgType = TkMsgType.NM_RC_CommanderScore;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(Side);
        ba.WriteInt(Score);
        ba.WriteInt(Para);
    }
}

// Client -> server: the shooter's client confirms an armor PENETRATION on a target
// (PlayerGUID). Sent in addition to ShellHit, and only on a real penetration, so we
// trust it for damage instead of the server's estimated armor roll.
[MessagePortal(TkMsgType.NM_CR_APHit)]
public class PK_CR_APHit : BaseRequest
{
    public short ShellId, ShellIndex;
    public long  PlayerGUID;
    public float HitX, HitY, HitZ;
    public byte  MendAngle, DamagedCount;
    /// Какие модули клиент считает задетыми (EComponent: 1=Hull, 2=LeftChassis,
    /// 3=RightChassis, 4=Turret, 5=Gun, 6=Engine, 8=AmmoBay, 9=FuelTank...).
    /// Раньше эти байты просто выбрасывались — из-за этого сервер угадывал зону
    /// попадания геометрией и рвал гусеницу при попадании в корпус.
    public byte[] AssemblyTypes = System.Array.Empty<byte>();
    public PK_CR_APHit() => MsgType = TkMsgType.NM_CR_APHit;
    public override IRequest ReadByte(ByteArray ba)
    {
        ShellId = ba.ReadShort(); ShellIndex = ba.ReadShort();
        PlayerGUID = ba.ReadLong();
        HitX = ba.ReadFloat(); HitY = ba.ReadFloat(); HitZ = ba.ReadFloat();
        MendAngle = ba.ReadByte(); DamagedCount = ba.ReadByte();
        AssemblyTypes = new byte[DamagedCount];
        for (int i = 0; i < DamagedCount; i++)
        { ba.ReadFloat(); AssemblyTypes[i] = ba.ReadByte(); ba.ReadByte(); }
        return this;
    }
}

// 1K-17 laser hit (shell type 6). Same wire layout as APHit — the client writes the
// hit point as a Vector3 (3 floats). Without this class the parser dropped the whole
// message ("Unknown message type: NM_CR_LaserHit") and the laser dealt no damage.
[MessagePortal(TkMsgType.NM_CR_LaserHit)]
public class PK_CR_LaserHit : BaseRequest
{
    public short ShellId, ShellIndex;
    public long  PlayerGUID;
    public float HitX, HitY, HitZ;
    public byte  MendAngle, DamagedCount;
    public PK_CR_LaserHit() => MsgType = TkMsgType.NM_CR_LaserHit;
    public override IRequest ReadByte(ByteArray ba)
    {
        ShellId = ba.ReadShort(); ShellIndex = ba.ReadShort();
        PlayerGUID = ba.ReadLong();
        HitX = ba.ReadFloat(); HitY = ba.ReadFloat(); HitZ = ba.ReadFloat();
        MendAngle = ba.ReadByte(); DamagedCount = ba.ReadByte();
        for (int i = 0; i < DamagedCount; i++) { ba.ReadFloat(); ba.ReadByte(); ba.ReadByte(); }
        return this;
    }
}
// Client -> server: use a skill. IsExecute 0 = aim phase (area skills open the aim
// view), >0 = execute. SkillID is PVESkillType (1 Repair,2 SupplyAmmo,3 Stimulant,
// 4 ScoutPlane,5 Katyusha,6 Bomber,7 DisturbRadio; 8-13 are the PVP variants).
[MessagePortal(TkMsgType.NM_CR_PVE_SkillRequest)]
public class PK_CR_PVE_SkillRequest : BaseRequest
{
    public int IsExecute, SkillID, CostType;
    public float Parameter1, Parameter2, Parameter3;
    public PK_CR_PVE_SkillRequest() => MsgType = TkMsgType.NM_CR_PVE_SkillRequest;
    public override IRequest ReadByte(ByteArray ba)
    {
        IsExecute = ba.ReadInt(); SkillID = ba.ReadInt(); CostType = ba.ReadInt();
        Parameter1 = ba.ReadFloat(); Parameter2 = ba.ReadFloat(); Parameter3 = ba.ReadFloat();
        return this;
    }
}

// Server -> requester: accept/deny. RequestResult 1 = OK.
[MessagePortal(TkMsgType.NM_RC_PVE_SkillReply)]
public class PK_RC_PVE_SkillReply : BaseRequest
{
    public int IsExecute, SkillID, RequestResult, CostType;
    public float fParam1;
    public PK_RC_PVE_SkillReply() => MsgType = TkMsgType.NM_RC_PVE_SkillReply;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(IsExecute); ba.WriteInt(SkillID); ba.WriteInt(RequestResult);
        ba.WriteInt(CostType); ba.WriteFloat(fParam1);
    }
}

// Server -> everyone: play the skill's visual (plane, katyusha volley, etc.) at Vec.
[MessagePortal(TkMsgType.NM_RC_PVE_SkillBroadcast)]
public class PK_RC_PVE_SkillBroadcast : BaseRequest
{
    public long PlayerGUID;
    public int  SkillID;
    public float[] Vec = new float[3];
    public PK_RC_PVE_SkillBroadcast() => MsgType = TkMsgType.NM_RC_PVE_SkillBroadcast;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(PlayerGUID); ba.WriteInt(SkillID);
        ba.WriteFloat(Vec[0]); ba.WriteFloat(Vec[1]); ba.WriteFloat(Vec[2]);
    }
}

// Server -> client queue-wait (id 452). Its handler sets the client's EBattleMode
// from BattleMode. We use it to push the client into Generals (Commander) mode,
// because this client build's mode-select screen has no Generals button — the
// client can't request it on its own. BattleMode is the wire value (the client
// runs ConvertModeFromServer on it; 2 => Commander).
[MessagePortal(TkMsgType.NM_HC_BeginQueueWait)]
public class PK_HC_BeginQueueWait : BaseRequest
{
    public byte Flag = 1;
    public int  TotalNum;
    // v12: экран «Подбор команды». PeopleCounts[11] — колонка «Кол-во машин» по уровням
    // I..XI, RoomCounts[11] — колонка «Кол-во Бой». null => нули (старое поведение).
    public ushort[]? PeopleCounts;
    public ushort[]? RoomCounts;
    public byte BattleMode;
    public PK_HC_BeginQueueWait() => MsgType = TkMsgType.NM_HC_BeginQueueWait;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(Flag);
        ba.WriteInt(TotalNum);
        for (int i = 0; i < 11; i++) ba.WriteUShort(PeopleCounts != null && i < PeopleCounts.Length ? PeopleCounts[i] : (ushort)0);
        for (int i = 0; i < 11; i++) ba.WriteUShort(RoomCounts   != null && i < RoomCounts.Length   ? RoomCounts[i]   : (ushort)0);
        ba.WriteByte(BattleMode);
    }
}

[MessagePortal(TkMsgType.NM_CR_Smoke)]
public class PK_CR_Smoke : BaseRequest
{
    public int SmokeIndex;
    public float TankPosX, TankPosY, TankPosZ;
    public byte Count;
    public (float X, float Y, float Z)[] SmokeAroundPosArray = new (float, float, float)[6];

    public PK_CR_Smoke() => MsgType = TkMsgType.NM_CR_Smoke;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        SmokeIndex = byteArray.ReadInt();
        TankPosX = byteArray.ReadFloat();
        TankPosY = byteArray.ReadFloat();
        TankPosZ = byteArray.ReadFloat();
        Count = byteArray.ReadByte();
        SmokeAroundPosArray = new (float, float, float)[6];
        for (int i = 0; i < 6; i++)
            SmokeAroundPosArray[i] = (byteArray.ReadFloat(), byteArray.ReadFloat(), byteArray.ReadFloat());
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(SmokeIndex);
        byteArray.WriteFloat(TankPosX);
        byteArray.WriteFloat(TankPosY);
        byteArray.WriteFloat(TankPosZ);
        byteArray.WriteByte(Count);
        for (int i = 0; i < 6; i++)
        {
            var pt = SmokeAroundPosArray != null && i < Count && i < SmokeAroundPosArray.Length ? SmokeAroundPosArray[i] : default;
            byteArray.WriteFloat(pt.X);
            byteArray.WriteFloat(pt.Y);
            byteArray.WriteFloat(pt.Z);
        }
    }
}

[MessagePortal(TkMsgType.NM_RC_Smoke)]
public class PK_RC_Smoke : BaseRequest
{
    public int SmokeIndex;
    public long PlayerGuid;
    public int SmokeId;
    public float TankPosX, TankPosY, TankPosZ;
    public byte Count;
    public (float X, float Y, float Z)[] SmokeAroundPosArray = new (float, float, float)[6];

    public PK_RC_Smoke() => MsgType = TkMsgType.NM_RC_Smoke;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        SmokeIndex = byteArray.ReadInt();
        PlayerGuid = byteArray.ReadLong();
        SmokeId = byteArray.ReadInt();
        TankPosX = byteArray.ReadFloat();
        TankPosY = byteArray.ReadFloat();
        TankPosZ = byteArray.ReadFloat();
        Count = byteArray.ReadByte();
        SmokeAroundPosArray = new (float, float, float)[6];
        for (int i = 0; i < 6; i++)
            SmokeAroundPosArray[i] = (byteArray.ReadFloat(), byteArray.ReadFloat(), byteArray.ReadFloat());
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(SmokeIndex);
        byteArray.WriteLong(PlayerGuid);
        byteArray.WriteInt(SmokeId);
        byteArray.WriteFloat(TankPosX);
        byteArray.WriteFloat(TankPosY);
        byteArray.WriteFloat(TankPosZ);
        byteArray.WriteByte(Count);
        for (int i = 0; i < 6; i++)
        {
            var pt = SmokeAroundPosArray != null && i < SmokeAroundPosArray.Length ? SmokeAroundPosArray[i] : default;
            byteArray.WriteFloat(pt.X);
            byteArray.WriteFloat(pt.Y);
            byteArray.WriteFloat(pt.Z);
        }
    }
}

[MessagePortal(TkMsgType.NM_RC_SmokeDisappear)]
public class PK_RC_SmokeDisappear : BaseRequest
{
    public int SmokeId;

    public PK_RC_SmokeDisappear() => MsgType = TkMsgType.NM_RC_SmokeDisappear;

    public override IRequest ReadByte(ByteArray byteArray) { SmokeId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(SmokeId);
}

[MessagePortal(TkMsgType.NM_RC_DestroyedObject)]
public class PK_RC_DestroyedObject : BaseRequest
{
    // v50: формат приведён к клиенту (PK_RC_DestroyedObject.ReadByte): Count(ushort) + Count×ObjectID(ushort).
    // Раньше сервер писал один ObjectID + Vector3 — клиент читал Count/список и всё съезжало,
    // из-за чего объекты не разрушались вообще. ObjectID — это sid объекта в сцене (см. релей ниже).
    public ushort[] ObjectIDs = System.Array.Empty<ushort>();

    public PK_RC_DestroyedObject() => MsgType = TkMsgType.NM_RC_DestroyedObject;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort((ushort)ObjectIDs.Length);
        foreach (var id in ObjectIDs) byteArray.WriteUShort(id);
    }
}

[MessagePortal(TkMsgType.NM_RC_DestroyedObjectDir)]
public class PK_RC_DestroyedObjectDir : BaseRequest
{
    // Вариант «с направлением» (для валящихся объектов, таран): Count + Count×{ObjectID, Point(Vector3)}.
    public ushort ObjectID;
    public float DirX, DirY, DirZ;

    public PK_RC_DestroyedObjectDir() => MsgType = TkMsgType.NM_RC_DestroyedObjectDir;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(1);            // Count
        byteArray.WriteUShort(ObjectID);
        byteArray.WriteFloat(DirX);
        byteArray.WriteFloat(DirY);
        byteArray.WriteFloat(DirZ);
    }
}

// v50: клиент шлёт эти пакеты, когда снаряд/танк попадает по разрушаемому объекту сцены
// (при UseLegacyShooting, что для этого сервера по умолчанию включено). Num = sid объекта.
// Клиент СНОСИТ объект только получив от сервера PK_RC_DestroyedObject — поэтому сервер обязан
// принять хит и разослать снос всем в комнате (включая стрелявшего).
[MessagePortal(TkMsgType.NM_CR_SceneInfo)]
public class PK_CR_SceneInfo : BaseRequest
{
    public ushort Num;
    public float PointX, PointY, PointZ;
    public ushort ID;

    public PK_CR_SceneInfo() => MsgType = TkMsgType.NM_CR_SceneInfo;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Num    = byteArray.ReadUShort();
        PointX = byteArray.ReadFloat();
        PointY = byteArray.ReadFloat();
        PointZ = byteArray.ReadFloat();
        ID     = byteArray.ReadUShort();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_CR_SceneInfoDir)]
public class PK_CR_SceneInfoDir : BaseRequest
{
    public ushort Num;
    public float DirX, DirY, DirZ;
    public ushort ID;

    public PK_CR_SceneInfoDir() => MsgType = TkMsgType.NM_CR_SceneInfoDir;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        Num  = byteArray.ReadUShort();
        DirX = byteArray.ReadFloat();
        DirY = byteArray.ReadFloat();
        DirZ = byteArray.ReadFloat();
        ID   = byteArray.ReadUShort();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_RC_BaseState)]
public class PK_RC_BaseState : BaseRequest
{
    public byte Team;
    public int BaseHp;
    public int BaseMaxHp;

    public PK_RC_BaseState() => MsgType = TkMsgType.NM_RC_BaseState;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Team);
        byteArray.WriteInt(BaseHp);
        byteArray.WriteInt(BaseMaxHp);
    }
}

[MessagePortal(TkMsgType.NM_RC_OneSideBaseState)]
public class PK_RC_OneSideBaseState : BaseRequest
{
    public int BaseHp;

    public PK_RC_OneSideBaseState() => MsgType = TkMsgType.NM_RC_OneSideBaseState;

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BaseHp);
}

[MessagePortal(TkMsgType.NM_RC_ChangeAttribute)]
public class PK_RC_ChangeAttribute : BaseRequest
{
    // v28: формат был НЕВЕРНЫЙ (long+int+float). Клиент читает
    // Count(byte) + Count×{ AttrKind(byte), AttrValue(float) } — см. PK_RC_ChangeValue.
    // Меняет атрибуты ТАНКА ПОЛУЧАТЕЛЯ (адресата пакета), PlayerGUID в теле не нужен.
    // AttrKind — индекс из клиентского enum EqAttributeType: 24 = EReloadTime,
    // 12 = ETurretRotationSpeed, 20 = ECircularVisionRadiusRange и т.д.
    public (byte Kind, float Value)[] Attributes = System.Array.Empty<(byte, float)>();

    public PK_RC_ChangeAttribute() => MsgType = TkMsgType.NM_RC_ChangeAttribute;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte((byte)Math.Min(255, Attributes.Length));
        foreach (var a in Attributes)
        {
            ba.WriteByte(a.Kind);
            ba.WriteFloat(a.Value);
        }
    }
}

// Server -> everyone: a tank (re)joins the battle LIVE. This is the same path a
// real player's join uses (client HandleNewUserLoginMsg), so it spawns a fully
// active, shooting tank — unlike PK_RC_Revive (hides non-main tanks) or
// PK_RC_PVE_CreateMonster (spawns "corpses" outside PVE rooms). Used to respawn
// bots in Respawn / Generals without the corpse bug.
[MessagePortal(TkMsgType.NM_RC_NewPlayerLogin)]
public class PK_RC_NewPlayerLogin : BaseRequest
{
    public PK_PlayerInfo PlayerInfo = new();
    public PK_RC_NewPlayerLogin() => MsgType = TkMsgType.NM_RC_NewPlayerLogin;
    // v15: именно КОРОТКИЙ формат — клиент читает это сообщение через ReadPlayerInfo().
    public override void WriteByte(ByteArray ba) => ba.WritePlayerInfoShort(PlayerInfo);
}

[MessagePortal(TkMsgType.NM_RC_TankBattleData)]
public class PK_RC_TankBattleData : BaseRequest
{
    public ushort TotalCount;
    public PK_PlayerInfo[] PlayerInfos = new PK_PlayerInfo[0];

    public PK_RC_TankBattleData() => MsgType = TkMsgType.NM_RC_TankBattleData;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(TotalCount);
        for (int i = 0; i < TotalCount; i++)
        {
            byteArray.WritePlayerInfo(PlayerInfos[i]);
        }
    }
}

[MessagePortal(TkMsgType.NM_RC_BattleEndTwoSecond)]
public class PK_RC_BattleEndTwoSecond : BaseRequest
{
    public PK_RC_BattleEndTwoSecond() => MsgType = TkMsgType.NM_RC_BattleEndTwoSecond;
}

[MessagePortal(TkMsgType.NM_HC_PLAYMAIN)]
public class PK_HC_PlayMain : BaseRequest
{
    public long PlayerGUID;
    public string PlayerName = string.Empty;
    public byte CarportNum;
    public byte LastSelect;
    public byte TankProjectIndex;
    public int GameMoney;
    public int GoldMoney;
    public byte LeftRenameTime;
    public int PlayerPlatformId;
    public byte StandaloneReward;
    public byte FirstLoginFlag;

    public PK_HC_PlayMain() => MsgType = TkMsgType.NM_HC_PLAYMAIN;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteByte(CarportNum);
        byteArray.WriteByte(LastSelect);
        byteArray.WriteByte(TankProjectIndex);
        byteArray.WriteInt(GameMoney);
        byteArray.WriteInt(GoldMoney);
        byteArray.WriteByte(LeftRenameTime);
        byteArray.WriteInt(PlayerPlatformId);
        byteArray.WriteByte(StandaloneReward);
        byteArray.WriteByte(FirstLoginFlag);
    }
}

[MessagePortal(TkMsgType.NM_HC_Tank_Info)]
public class PK_HC_Tank_Info : BaseRequest
{
    public long PlayerGuid;
    public short TankId;

    public PK_HC_Tank_Info() => MsgType = TkMsgType.NM_HC_Tank_Info;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGuid);
        byteArray.WriteShort(TankId);
    }
}

public struct ItemInfoData
{
    public int ItemType;
    public int ItemId;
    public int ItemNum;
    public byte ItemFlag;
}

[MessagePortal(TkMsgType.NM_HC_ITEMINFO)]
public class PK_HC_ItemInfo : BaseRequest
{
    public int ItemCount;
    public ItemInfoData[] Items = new ItemInfoData[0];

    public PK_HC_ItemInfo() => MsgType = TkMsgType.NM_HC_ITEMINFO;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ItemCount = byteArray.ReadInt();
        Items = new ItemInfoData[ItemCount];
        for (int i = 0; i < ItemCount; i++)
        {
            Items[i].ItemType = byteArray.ReadInt();
            Items[i].ItemId = byteArray.ReadInt();
            Items[i].ItemNum = byteArray.ReadInt();
            Items[i].ItemFlag = byteArray.ReadByte();
        }
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(ItemCount);
        for (int i = 0; i < ItemCount; i++)
        {
            byteArray.WriteInt(Items[i].ItemType);
            byteArray.WriteInt(Items[i].ItemId);
            byteArray.WriteInt(Items[i].ItemNum);
            byteArray.WriteByte(Items[i].ItemFlag);
        }
    }
}

public struct ShellStruct
{
    public byte ShellSlotId;
    public int ShellID;
    public int Count;
}

[MessagePortal(TkMsgType.NM_HC_ShellInfo)]
public class PK_HC_ShellInfo : BaseRequest
{
    public long TankGuid;
    public ShellStruct[] ShellInfoStruct = new ShellStruct[3];

    public PK_HC_ShellInfo() => MsgType = TkMsgType.NM_HC_ShellInfo;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(TankGuid);
        for (int i = 0; i < 3; i++)
        {
            byteArray.WriteByte(ShellInfoStruct[i].ShellSlotId);
            byteArray.WriteInt(ShellInfoStruct[i].ShellID);
            byteArray.WriteInt(ShellInfoStruct[i].Count);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_TaskData)]
public class PK_HC_TaskData : BaseRequest
{
    public ushort MissionID;
    public int Schedule;
    public byte CompleteTag;
    public PK_HC_TaskData() => MsgType = TkMsgType.NM_HC_TaskData;
    public override IRequest ReadByte(ByteArray byteArray) { MissionID = byteArray.ReadUShort(); Schedule = byteArray.ReadInt(); CompleteTag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteUShort(MissionID); byteArray.WriteInt(Schedule); byteArray.WriteByte(CompleteTag); }
}

[MessagePortal(TkMsgType.NM_HC_LoginReward)]
public class PK_HC_LoginReward : BaseRequest
{
    public byte WeekID;
    public ushort Days;
    public byte IsGet;

    public PK_HC_LoginReward() => MsgType = TkMsgType.NM_HC_LoginReward;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(WeekID);
        byteArray.WriteUShort(Days);
        byteArray.WriteByte(IsGet);
    }
}

[MessagePortal(TkMsgType.NM_HC_NewGuideLoginReward)]
public class PK_HC_NewGuideLoginReward : BaseRequest
{
    public ushort LoginDays;
    public ushort OperRewardDayIndex;
    public byte[] Rewards = new byte[7];
    public bool IsLoginRewardFinish;

    public PK_HC_NewGuideLoginReward() => MsgType = TkMsgType.NM_HC_NewGuideLoginReward;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUShort(LoginDays);
        byteArray.WriteUShort(OperRewardDayIndex);
        for (int i = 0; i < 7; i++)
        {
            byteArray.WriteByte(i < Rewards.Length ? Rewards[i] : (byte)0);
        }
        byteArray.WriteByte(IsLoginRewardFinish ? (byte)1 : (byte)0);
    }
}

public struct ChannelPlayerEntry
{
    public long Guid;
    public string Name;
}

[MessagePortal(TkMsgType.NM_HC_CHANNEL_RES)]
public class PK_HC_Channel_Res : BaseRequest
{
    // NOTE: The client reads this as PK_HC_ChannelInfo:
    //   PlayerGUID(long) + HallChatType(byte) + Count(int) + Count*(Guid long + Name utf64)
    // Writing only an int here desynced the client's per-message buffer read, producing a
    // garbage Count -> new List<PlayerInfo>(garbage) -> OutOfMemoryException that killed the
    // client's receive thread. Keep the wire layout in sync with the client.
    public int ChannelId;               // kept for call-site compatibility; mapped to HallChatType
    public long PlayerGUID;
    public byte HallChatType;
    public ChannelPlayerEntry[] Players = System.Array.Empty<ChannelPlayerEntry>();

    public PK_HC_Channel_Res() => MsgType = TkMsgType.NM_HC_CHANNEL_RES;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteByte(HallChatType);
        byteArray.WriteInt(Players.Length);
        for (int i = 0; i < Players.Length; i++)
        {
            byteArray.WriteLong(Players[i].Guid);
            byteArray.WriteUTF(Players[i].Name ?? string.Empty, 64);
        }
    }
}

// PK_WHC_CorpsData — full corps/legion info sent to a player.
// Client ReadByte (PK_HC_NM_WHC_CORPS_DATA):
//   ReadLong (session/unused), TroopsID(long), TroopsName(21), TroopsTag(11),
//   EditNameCount(ushort), EditTagCount(ushort), TroopsLevel(ushort),
//   TroopsNotice(200), TroopsIntroduce(200), TroopsCreateTime(20),
//   TroopsState(byte), AddMaxMemberCount(int), GoldMoney(long), SilverMoney(long),
//   TaxRate(int), ConvertRate(int), ExternMulti(int), WarNotice(60),
//   BiddingDomainId(int), Activity(int), MaxMemSize(int), PlayerNum(ushort),
//   LeaderName(64), MyJob(byte), FealtyPoint(int),
//   ConvertSilverTimes(int), ConvertGoldTimes(int), BidGold(int),
//   LaserState(byte), JoinTroopsID(long), JoinTroopsName(21)
//
// When TroopsID == 0  → player has no corps, all other fields are empty/0.
[MessagePortal(TkMsgType.NM_WHC_CORPS_DATA)]
public class PK_WHC_CorpsData : BaseRequest
{
    public long   TroopsID;
    public string TroopsName      = string.Empty;
    public string TroopsTag       = string.Empty;
    public ushort EditNameCount;
    public ushort EditTagCount;
    public ushort TroopsLevel     = 1;
    public string TroopsNotice    = string.Empty;
    public string TroopsIntroduce = string.Empty;
    public string TroopsCreateTime = string.Empty;
    public byte   TroopsState;
    public int    AddMaxMemberCount = 100;
    public long   GoldMoney;
    public long   SilverMoney;
    public int    TaxRate;
    public int    ConvertRate     = 100;
    public int    ExternMulti     = 1;
    public string WarNotice       = string.Empty;
    public int    BiddingDomainId;
    public int    Activity;
    public int    MaxMemSize      = 100;
    public ushort PlayerNum;
    public string LeaderName      = string.Empty;
    public byte   MyJob;          // 0=member, 1=officer, 2=commander
    public int    FealtyPoint;
    public int    ConvertSilverTimes;
    public int    ConvertGoldTimes;
    public int    BidGold;
    public byte   LaserState;
    public long   JoinTroopsID;
    public string JoinTroopsName  = string.Empty;

    public PK_WHC_CorpsData() => MsgType = TkMsgType.NM_WHC_CORPS_DATA;

    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(0L);              // session/unused first long
        ba.WriteLong(TroopsID);
        ba.WriteUTF(TroopsName,       21);
        ba.WriteUTF(TroopsTag,        11);
        ba.WriteUShort(EditNameCount);
        ba.WriteUShort(EditTagCount);
        ba.WriteUShort(TroopsLevel);
        ba.WriteUTF(TroopsNotice,     200);
        ba.WriteUTF(TroopsIntroduce,  200);
        ba.WriteUTF(TroopsCreateTime, 20);
        ba.WriteByte(TroopsState);
        ba.WriteInt(AddMaxMemberCount);
        ba.WriteLong(GoldMoney);
        ba.WriteLong(SilverMoney);
        ba.WriteInt(TaxRate);
        ba.WriteInt(ConvertRate);
        ba.WriteInt(ExternMulti);
        ba.WriteUTF(WarNotice,        60);
        ba.WriteInt(BiddingDomainId);
        ba.WriteInt(Activity);
        ba.WriteInt(MaxMemSize);
        ba.WriteUShort(PlayerNum);
        ba.WriteUTF(LeaderName,       64);
        ba.WriteByte(MyJob);
        ba.WriteInt(FealtyPoint);
        ba.WriteInt(ConvertSilverTimes);
        ba.WriteInt(ConvertGoldTimes);
        ba.WriteInt(BidGold);
        ba.WriteByte(LaserState);
        ba.WriteLong(JoinTroopsID);
        ba.WriteUTF(JoinTroopsName,   21);
    }
}

public struct FriendInfoData
{
    public long FriendGUID;
    public string FriendName;
    public int RelationType;    // PlayerRelation enum: 0=Friend,1=Block,2=Unconcerned,3=Myself
    public byte OnlineState;    // PlayerOnlineState enum: 0=INVALID,1=OFFLINE,2=ONLINE,...
    public byte HSIndex;
    public long TeamGUID;
}

[MessagePortal(TkMsgType.NM_WHC_FRIENDLIST)]
public class PK_WHC_FriendList : BaseRequest
{
    public long PlayerGUID;
    public int FriendCount;
    public FriendInfoData[] Friends = new FriendInfoData[0];

    public PK_WHC_FriendList() => MsgType = TkMsgType.NM_WHC_FRIENDLIST;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerGUID = byteArray.ReadLong();
        FriendCount = byteArray.ReadInt();
        Friends = new FriendInfoData[FriendCount];
        for (int i = 0; i < FriendCount; i++)
        {
            Friends[i].FriendGUID = byteArray.ReadLong();
            Friends[i].FriendName = byteArray.ReadUTF(64);
            Friends[i].RelationType = byteArray.ReadInt();
            Friends[i].OnlineState = byteArray.ReadByte();
            Friends[i].HSIndex = byteArray.ReadByte();
            Friends[i].TeamGUID = byteArray.ReadLong();
        }
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(PlayerGUID);
        byteArray.WriteInt(FriendCount);
        for (int i = 0; i < FriendCount; i++)
        {
            byteArray.WriteLong(Friends[i].FriendGUID);
            byteArray.WriteUTF(Friends[i].FriendName, 64);
            byteArray.WriteInt(Friends[i].RelationType);
            byteArray.WriteByte(Friends[i].OnlineState);
            byteArray.WriteByte(Friends[i].HSIndex);
            byteArray.WriteLong(Friends[i].TeamGUID);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_ActivityConfig)]
public class PK_HC_ActivityConfig : BaseRequest
{
    public PK_HC_ActivityConfig() => MsgType = TkMsgType.NM_HC_ActivityConfig;

    public override void WriteByte(ByteArray byteArray)
    {
        // GoldToSilverDiscountActivityInfor
        byteArray.WriteInt(0);
        byteArray.WriteUTF("", 20);
        byteArray.WriteUTF("", 20);
        // GoldTransExpDiscountActivityInfor
        byteArray.WriteInt(0);
        byteArray.WriteUTF("", 20);
        byteArray.WriteUTF("", 20);
        // GoldDepositActivityInfor
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteUTF("", 20);
        byteArray.WriteUTF("", 20);
        // VIPBuyActivityInfor
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteInt(0);
        byteArray.WriteUTF("", 20);
        byteArray.WriteUTF("", 20);
        // DiscountTankActivityInfor
        byteArray.WriteUTF("", 20);
        byteArray.WriteUTF("", 20);
        byteArray.WriteInt(0);
    }
}

// Курс обмена золота на серебро/опыт, читается клиентом в Player.HandleActivityEffectInfor
// и используется диалогами ExchangeSliver/ExchangeExperience (CommonTools.CalGoldToSliverDiscount /
// CalGoldToExpDiscount). Без этого пакета GoldToSilver/GoldToExp остаются 0 — отсюда
// "ОБМЕННЫЙ КУРС 1 = 0" в диалоге покупки серебра.
public struct TankDiscount
{
    public int TankID;
    public float Discount;
}

[MessagePortal(TkMsgType.NM_HC_ActivityEffectInfor)]
public class PK_HC_ActivityEffectInfor : BaseRequest
{
    public int GoldToSilver;
    public int GoldToExp;
    public int TankDiscountCount;
    public TankDiscount[] TankDiscounts = Array.Empty<TankDiscount>();

    public PK_HC_ActivityEffectInfor() => MsgType = TkMsgType.NM_HC_ActivityEffectInfor;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(GoldToSilver);
        byteArray.WriteInt(GoldToExp);
        byteArray.WriteInt(TankDiscountCount);
        for (int i = 0; i < TankDiscountCount; i++)
        {
            byteArray.WriteInt(TankDiscounts[i].TankID);
            byteArray.WriteFloat(TankDiscounts[i].Discount);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_HotGoods)]
public class PK_HC_HotGoods : BaseRequest
{
    public int Count;

    public PK_HC_HotGoods() => MsgType = TkMsgType.NM_HC_HotGoods;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Count);
    }
}

[MessagePortal(TkMsgType.NM_HC_Garage_Background)]
public class PK_HC_Garage_Background : BaseRequest
{
    public int BackgroundId;

    public PK_HC_Garage_Background() => MsgType = TkMsgType.NM_HC_Garage_Background;

    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BackgroundId);
}

public struct CrewInfoData
{
    public int Id;
    public int CurExp;
    public int CurLv;
    public long CrewGuid;
    public long TankGuid;
    public short FamilyNameId;
    public short NameId;
    public short BigIconId;
    public short SmallIconId;
    public short ResumeId;
    public int Lastlearn;
    public int LastSkillExp;
    public int[] SkillIds;
}

[MessagePortal(TkMsgType.NM_HC_PlayerAllCrew)]
public class PK_HC_PlayerAllCrew : BaseRequest
{
    public int CrewCount;
    public CrewInfoData[] Crews = new CrewInfoData[0];

    public PK_HC_PlayerAllCrew() => MsgType = TkMsgType.NM_HC_PlayerAllCrew;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        CrewCount = byteArray.ReadInt();
        Crews = new CrewInfoData[CrewCount];
        for (int i = 0; i < CrewCount; i++)
        {
            Crews[i].Id = byteArray.ReadInt();
            Crews[i].CurExp = byteArray.ReadInt();
            Crews[i].CurLv = byteArray.ReadInt();
            Crews[i].CrewGuid = byteArray.ReadLong();
            Crews[i].TankGuid = byteArray.ReadLong();
            Crews[i].FamilyNameId = byteArray.ReadShort();
            Crews[i].NameId = byteArray.ReadShort();
            Crews[i].BigIconId = byteArray.ReadShort();
            Crews[i].SmallIconId = byteArray.ReadShort();
            Crews[i].ResumeId = byteArray.ReadShort();
            Crews[i].Lastlearn = byteArray.ReadInt();
            Crews[i].LastSkillExp = byteArray.ReadInt();
            Crews[i].SkillIds = new int[30];
            for (int j = 0; j < Crews[i].SkillIds.Length; j++)
            {
                Crews[i].SkillIds[j] = byteArray.ReadInt();
            }
        }
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(CrewCount);
        for (int i = 0; i < CrewCount; i++)
        {
            byteArray.WriteInt(Crews[i].Id);
            byteArray.WriteInt(Crews[i].CurExp);
            byteArray.WriteInt(Crews[i].CurLv);
            byteArray.WriteLong(Crews[i].CrewGuid);
            byteArray.WriteLong(Crews[i].TankGuid);
            byteArray.WriteShort(Crews[i].FamilyNameId);
            byteArray.WriteShort(Crews[i].NameId);
            byteArray.WriteShort(Crews[i].BigIconId);
            byteArray.WriteShort(Crews[i].SmallIconId);
            byteArray.WriteShort(Crews[i].ResumeId);
            byteArray.WriteInt(Crews[i].Lastlearn);
            byteArray.WriteInt(Crews[i].LastSkillExp);
            var skillIds = Crews[i].SkillIds ?? new int[30];
            for (int j = 0; j < 30; j++)
            {
                byteArray.WriteInt(j < skillIds.Length ? skillIds[j] : 0);
            }
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_AllEquipmentData)]
public class PK_HC_AllEquipmentData : BaseRequest
{
    public byte TankCount;
    public EquipmentInfor[] EquipmentInfors = System.Array.Empty<EquipmentInfor>();

    public PK_HC_AllEquipmentData() => MsgType = TkMsgType.NM_HC_AllEquipmentData;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(TankCount);
        for (int i = 0; i < TankCount; i++)
        {
            byteArray.WriteLong(EquipmentInfors[i].TankGuid);
            byteArray.WriteTankEquipmentData(EquipmentInfors[i].TankEquData);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_TankHonorList)]
public class PK_HC_TankHonorList : BaseRequest
{
    public int TankId;
    public byte BattleType;
    public byte MedalCount;

    public PK_HC_TankHonorList() => MsgType = TkMsgType.NM_HC_TankHonorList;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(TankId);
        byteArray.WriteByte(BattleType);
        byteArray.WriteByte(MedalCount);
    }
}

// v12: «Статистика боя» (id 434) — окно итогов после PvP-боя. Формат зеркалит
// клиентский PK_HC_BattleEndInfo.ReadByte поле-в-поле (сверено с декомпилятом).
[MessagePortal(TkMsgType.NM_HC_BattleEndInfo)]
public class PK_HC_BattleEndInfo : BaseRequest
{
    public long  RoomGuid;
    public int   MapId;
    public byte  VipOrNomal = 1;
    public int   Money, Exp, VipMoney, VipExp;
    public float[] VipExpMultiple   = { 1.5f, 1.51f, 1.52f, 1.53f };
    public float[] VipMoneyMultiple = { 1.5f, 1.51f, 1.52f, 1.53f };
    public byte  HasBehave;
    public float ExpMultiple = 1f, MoneyMultiple = 1f;
    public byte  State = 1;        // 1 = танк уцелел
    public byte  Result;           // 1=победа, 0=поражение, 2=ничья
    public byte  EndReason;
    public int   StartSecond;      // unix-время старта (клиент форматирует дату)
    public byte  IsGarageIncrease;
    public float SpeActivityIncrease;
    // TankBattleEndInfo
    public int   TankId;
    public ushort ShotCount, HitCount, PierceCount;
    public int   Damage;
    public byte  KillCount, DamagedEnemies;
    public ushort OccupyValue, BeHitCount;
    public int   LostHp, MaxHp, RepairSilver, SupplyCost, ShellCost, LightDamage;
    public byte  LightCount;
    public int   ChassisShareDamage, PotentialDamage;
    public byte  RoomTypeWire;     // серверный байт режима, клиент конвертирует сам
    public float GroupReward = 1f;
    public int   LoyalPoint;       // очки лояльности легиона

    public PK_HC_BattleEndInfo() => MsgType = TkMsgType.NM_HC_BattleEndInfo;

    public void WriteCommonAndTank(ByteArray ba)
    {
        ba.WriteLong(RoomGuid);
        ba.WriteInt(MapId);
        ba.WriteByte(VipOrNomal);
        ba.WriteInt(Money);
        ba.WriteInt(Exp);
        ba.WriteInt(VipMoney);
        ba.WriteInt(VipExp);
        for (int i = 0; i < 4; i++) ba.WriteFloat(i < VipExpMultiple.Length   ? VipExpMultiple[i]   : 1f);
        for (int i = 0; i < 4; i++) ba.WriteFloat(i < VipMoneyMultiple.Length ? VipMoneyMultiple[i] : 1f);
        ba.WriteByte(HasBehave);
        ba.WriteFloat(ExpMultiple);
        ba.WriteFloat(MoneyMultiple);
        ba.WriteByte(State);
        ba.WriteByte(Result);
        ba.WriteByte(EndReason);
        ba.WriteInt(StartSecond);
        ba.WriteByte(IsGarageIncrease);
        ba.WriteFloat(SpeActivityIncrease);
        ba.WriteInt(TankId);
        ba.WriteUShort(ShotCount);
        ba.WriteUShort(HitCount);
        ba.WriteUShort(PierceCount);
        ba.WriteInt(Damage);
        ba.WriteByte(KillCount);
        ba.WriteByte(DamagedEnemies);
        ba.WriteUShort(OccupyValue);
        ba.WriteUShort(BeHitCount);
        ba.WriteInt(LostHp);
        ba.WriteInt(MaxHp);
        ba.WriteInt(RepairSilver);
        ba.WriteInt(SupplyCost);
        ba.WriteInt(ShellCost);
        ba.WriteInt(LightDamage);
        ba.WriteByte(LightCount);
        ba.WriteInt(ChassisShareDamage);
        ba.WriteInt(PotentialDamage);
        ba.WriteByte(RoomTypeWire);
        ba.WriteFloat(GroupReward);
    }

    // v16: список «повреждено врагов» для вкладки «Подробные данные».
    public System.Collections.Generic.List<(string Name, int TankId, int DamageCount,
        int DestroyModules, byte KillOrDamage, int Damage, byte IsLight, byte IsLighted,
        int LightDamage, int ShareDamage)> Victims = new();

    public override void WriteByte(ByteArray ba)
    {
        WriteCommonAndTank(ba);
        ba.WriteInt(LoyalPoint);
        ba.WriteByte((byte)Math.Min(255, Victims.Count));
        foreach (var v in Victims)
        {
            ba.WriteUTF(v.Name, 64);
            ba.WriteInt(v.TankId);
            ba.WriteUShort((ushort)Math.Min(ushort.MaxValue, v.DamageCount));
            ba.WriteUShort((ushort)Math.Min(ushort.MaxValue, v.DestroyModules));
            ba.WriteByte(v.KillOrDamage);
            ba.WriteUShort((ushort)Math.Min(ushort.MaxValue, v.Damage));
            ba.WriteByte(v.IsLight);
            ba.WriteByte(v.IsLighted);
            ba.WriteInt(v.LightDamage);
            ba.WriteInt(v.ShareDamage);
        }
    }
}

// v12: PVE-вариант итогов (id 442) — окно после ОБОРОНЫ. CommonInfo + TankBattleEndInfo
// (без LoyalPoint и списка жертв) + массив из 18 int по enum PVEEndInfo + CrewExtraExp.
// Композиция (не наследование), чтобы не путать рефлексию MessageRegistry атрибутами.
[MessagePortal(TkMsgType.NM_HC_BattleEndInfoPVE)]
public class PK_HC_BattleEndInfoPVE : BaseRequest
{
    public PK_HC_BattleEndInfo Common { get; set; } = new();
    public int[] PveInfo = new int[18];
    public int   CrewExtraExp;

    public PK_HC_BattleEndInfoPVE() => MsgType = TkMsgType.NM_HC_BattleEndInfoPVE;

    public override void WriteByte(ByteArray ba)
    {
        Common.WriteCommonAndTank(ba);
        for (int i = 0; i < 18; i++) ba.WriteInt(i < PveInfo.Length ? PveInfo[i] : 0);
        ba.WriteInt(CrewExtraExp);
    }
}

public struct AccountStatItem
{
    public int StatId;
    public int StatValue;
}

public class AccountStatisticItem
{
    public int VehicleId;
    public int BattleCount;
    public int WinCount;
    public int LoseCount;
    public int SurviveCount;
    public int TotalKillCount;
    public int MaxKillCount;
    public int TotalDamage;
    public int TotalExp;
    public int ShootCount;
    public int HitCount;
    public int MaxExp;
    public byte MedalLevel;
}

[MessagePortal(TkMsgType.NM_HC_AccountStatistic)]
public class PK_HC_AccountStatistic : BaseRequest
{
    public string PlayerName = string.Empty;
    public uint CreationTime;
    public int MaxKillVehicleId;
    public int MaxExpVehicleId;
    public int TotalBattleCount;
    public int TotalWinCount;
    public int TotalLoseCount;
    public int TotalSurviveCount;
    public int TotalKillCount;
    public int TotalMaxKillCount;
    public int TotalDamage;
    public int TotalExp;
    public int TotalShootCount;
    public int TotalHitCount;
    public int TotalMaxExp;
    public int ItemCount;
    public AccountStatisticItem[] TankStats = new AccountStatisticItem[0];

    public PK_HC_AccountStatistic() => MsgType = TkMsgType.NM_HC_AccountStatistic;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUInt(CreationTime);
        byteArray.WriteInt(MaxKillVehicleId);
        byteArray.WriteInt(MaxExpVehicleId);
        byteArray.WriteInt(TotalBattleCount);
        byteArray.WriteInt(TotalWinCount);
        byteArray.WriteInt(TotalLoseCount);
        byteArray.WriteInt(TotalSurviveCount);
        byteArray.WriteInt(TotalKillCount);
        byteArray.WriteInt(TotalMaxKillCount);
        byteArray.WriteInt(TotalDamage);
        byteArray.WriteInt(TotalExp);
        byteArray.WriteInt(TotalShootCount);
        byteArray.WriteInt(TotalHitCount);
        byteArray.WriteInt(TotalMaxExp);
        byteArray.WriteInt(ItemCount);
        for (int i = 0; i < ItemCount; i++)
        {
            byteArray.WriteInt(TankStats[i].VehicleId);
            byteArray.WriteInt(TankStats[i].BattleCount);
            byteArray.WriteInt(TankStats[i].WinCount);
            byteArray.WriteInt(TankStats[i].LoseCount);
            byteArray.WriteInt(TankStats[i].SurviveCount);
            byteArray.WriteInt(TankStats[i].TotalKillCount);
            byteArray.WriteInt(TankStats[i].MaxKillCount);
            byteArray.WriteInt(TankStats[i].TotalDamage);
            byteArray.WriteInt(TankStats[i].TotalExp);
            byteArray.WriteInt(TankStats[i].ShootCount);
            byteArray.WriteInt(TankStats[i].HitCount);
            byteArray.WriteInt(TankStats[i].MaxExp);
            byteArray.WriteByte(TankStats[i].MedalLevel);
        }
    }
}

// The client's AccountProfilesManager keeps FOUR profile buckets (Nml, RvV, Commander, PVE),
// each initialised by its own message. If RvV/Commander/PVE are never sent, those buckets stay
// null and FillPersonalProfilesWhenUpdateTankInfo() (invoked on every AddTank) throws a
// NullReferenceException -> the buy/garage refresh aborts and the money panel never updates.
// Revive and Commander share the Nml wire layout; PVE has an extra MaxScoreVehicleId + a
// different base-item shape.
//
// v: раньше все три пакета слали ItemCount=0 и все 11/8 "базовых" полей нулями —
// вкладки статистики "Возрождение" (Respawn), "Командир" (Commander) и "Оборона" (PVE)
// никогда не росли, даже после сотен боёв в этих режимах. Теперь сервер (HallServer,
// на основе ModeStatsRepository) подставляет сюда реальные агрегаты по режиму.
[MessagePortal(TkMsgType.NM_HC_AccountStatisticRevive)]
public class PK_HC_AccountStatisticRevive : BaseRequest
{
    public string PlayerName = string.Empty;
    public uint CreationTime;
    public int MaxKillVehicleId;
    public int MaxExpVehicleId;
    public int BattleCount;
    public int WinCount;
    public int LoseCount;
    public int SurviveCount;
    public int TotalKillCount;
    public int MaxKillCount;
    public int TotalDamage;
    public int TotalExp;
    public int ShootCount;
    public int HitCount;
    public int MaxExp;
    public PK_HC_AccountStatisticRevive() => MsgType = TkMsgType.NM_HC_AccountStatisticRevive;
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUInt(CreationTime);
        byteArray.WriteInt(MaxKillVehicleId);
        byteArray.WriteInt(MaxExpVehicleId);
        byteArray.WriteInt(BattleCount);
        byteArray.WriteInt(WinCount);
        byteArray.WriteInt(LoseCount);
        byteArray.WriteInt(SurviveCount);
        byteArray.WriteInt(TotalKillCount);
        byteArray.WriteInt(MaxKillCount);
        byteArray.WriteInt(TotalDamage);
        byteArray.WriteInt(TotalExp);
        byteArray.WriteInt(ShootCount);
        byteArray.WriteInt(HitCount);
        byteArray.WriteInt(MaxExp);
        byteArray.WriteInt(0); // additional item count (per-tank breakdown within mode — not tracked)
    }
}

[MessagePortal(TkMsgType.NM_HC_AccountStatisticCommander)]
public class PK_HC_AccountStatisticCommander : BaseRequest
{
    public string PlayerName = string.Empty;
    public uint CreationTime;
    public int MaxKillVehicleId;
    public int MaxExpVehicleId;
    public int BattleCount;
    public int WinCount;
    public int LoseCount;
    public int SurviveCount;
    public int TotalKillCount;
    public int MaxKillCount;
    public int TotalDamage;
    public int TotalExp;
    public int ShootCount;
    public int HitCount;
    public int MaxExp;
    public PK_HC_AccountStatisticCommander() => MsgType = TkMsgType.NM_HC_AccountStatisticCommander;
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUInt(CreationTime);
        byteArray.WriteInt(MaxKillVehicleId);
        byteArray.WriteInt(MaxExpVehicleId);
        byteArray.WriteInt(BattleCount);
        byteArray.WriteInt(WinCount);
        byteArray.WriteInt(LoseCount);
        byteArray.WriteInt(SurviveCount);
        byteArray.WriteInt(TotalKillCount);
        byteArray.WriteInt(MaxKillCount);
        byteArray.WriteInt(TotalDamage);
        byteArray.WriteInt(TotalExp);
        byteArray.WriteInt(ShootCount);
        byteArray.WriteInt(HitCount);
        byteArray.WriteInt(MaxExp);
        byteArray.WriteInt(0); // additional item count
    }
}

[MessagePortal(TkMsgType.NM_HC_AccountStatisticPVE)]
public class PK_HC_AccountStatisticPVE : BaseRequest
{
    public string PlayerName = string.Empty;
    public uint CreationTime;
    public int MaxKillVehicleId;
    public int MaxExpVehicleId;
    public int MaxScoreVehicleId;
    public int BattleCount;
    public int WinCount;
    public int TotalKill;
    public int MaxKillOneBattle;
    public int TotalScore;
    public int MaxScoreOneBattle;
    public int TotalExp;
    public int MaxExpOneBattle;
    public PK_HC_AccountStatisticPVE() => MsgType = TkMsgType.NM_HC_AccountStatisticPVE;
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteUTF(PlayerName, 64);
        byteArray.WriteUInt(CreationTime);
        byteArray.WriteInt(MaxKillVehicleId);
        byteArray.WriteInt(MaxExpVehicleId);
        byteArray.WriteInt(MaxScoreVehicleId);
        byteArray.WriteInt(BattleCount);
        byteArray.WriteInt(WinCount);
        byteArray.WriteInt(TotalKill);
        byteArray.WriteInt(MaxKillOneBattle);
        byteArray.WriteInt(TotalScore);
        byteArray.WriteInt(MaxScoreOneBattle);
        byteArray.WriteInt(TotalExp);
        byteArray.WriteInt(MaxExpOneBattle);
        byteArray.WriteInt(0); // additional item count
    }
}

// v: НОВОЕ сообщение — раньше вообще не отправлялось сервером (был только TkMsgType-
// enum, ни отправки, ни обработчика). Это "карточка статистики" игрока (Rank, %побед,
// боёв, ср./макс. опыт, ср. урон, макс./ср. фраги, "эффективность", любимые танки) —
// именно её читает PlayerAccountStatisticsManager/UIPlayerAccountStatistics на клиенте.
// Раз сервер её не слал, все цифры на этом экране были константным нулём независимо
// от числа сыгранных боёв — это и была причина "не идёт в статистику".
[MessagePortal(TkMsgType.NM_HC_AccountFightStatistics)]
public class PK_HC_AccountFightStatistics : BaseRequest
{
    public int Rank;
    public int WinRate;   // проценты, 0..100
    public int FightTimes;
    public int AvgExp;
    public int MaxExp;
    public int LightTimes; // проценты "засветов" — не считаем (нет данных), шлём 0
    public int AvgHurt;
    public int MaxKill;
    public int AvgKill;    // клиент делит на 100.0 при отображении -> шлём kills*100/battles
    public int Effect;
    public int[] TankIds = Array.Empty<int>(); // до 5 самых "заезженных" танков, остальное — паддинг
    public PK_HC_AccountFightStatistics() => MsgType = TkMsgType.NM_HC_AccountFightStatistics;
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Rank);
        byteArray.WriteInt(WinRate);
        byteArray.WriteInt(FightTimes);
        byteArray.WriteInt(AvgExp);
        byteArray.WriteInt(MaxExp);
        byteArray.WriteInt(LightTimes);
        byteArray.WriteInt(AvgHurt);
        byteArray.WriteInt(MaxKill);
        byteArray.WriteInt(AvgKill);
        byteArray.WriteInt(Effect);
        int count = Math.Min(5, TankIds.Length);
        byteArray.WriteByte((byte)count);
        for (int i = 0; i < count; i++) byteArray.WriteInt(TankIds[i]);
        for (int i = count; i < 5; i++) byteArray.WriteInt(0); // паддинг — клиент читает ровно 5 int всегда
    }
}

// v: НОВОЕ сообщение — раньше не отправлялось вообще (только запись в enum TkMsgType,
// ни отправки, ни обработчика). Клиент (TerritoryWarDataManager.DomainHsDataList setter,
// вызывается из OnDomainHSData) ждёт его перед тем, как открывать глобальную карту
// (UILandWarPanel -> ContendWarManager.InitMap -> ContendMap.SetStart/SetMapSituation).
// Пока сервер этот пакет не слал, поле DomainHsDataList оставалось null, и
// `foreach (... in Singleton<TerritoryWarDataManager>.Instance.DomainHsDataList)` в
// ContendMap.SetMapSituation падал с NullReferenceException — отсюда и "карта криво
// отображается" (клиент проглатывал исключение и оставлял карту недорисованной).
//
// Список территорий (Table_Domain_attribute — их id/названия/соседи) целиком на
// стороне клиента (собственный game-table файл), сервер его не знает — поэтому шлём
// ПУСТОЙ список владений (все территории считаются "не разыгранными"/нейтральными).
// Это убирает краш и корректно рисует карту (просто без каких-либо владений корпусов);
// полноценная война кланов (кто чем владеет) потребовала бы отдельной игровой системы.
[MessagePortal(TkMsgType.NM_HC_DomainInfo)]
public class PK_HC_DomainInfo : BaseRequest
{
    public byte DomainPhase;  // 0 = None (см. TerritoryWarDataManager.DomainPhase)
    public byte BiddingPhase; // 0 = None (см. TerritoryWarDataManager.BiddingPhase)
    public PK_HC_DomainInfo() => MsgType = TkMsgType.NM_HC_DomainInfo;
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(DomainPhase);
        byteArray.WriteByte(BiddingPhase);
        byteArray.WriteInt(0); // число территорий в списке — 0 (нейтральная карта)
    }
}

[MessagePortal(TkMsgType.NM_HC_PlayerStatistics)]
public class PK_HC_PlayerStatistics : BaseRequest
{
    public PK_HC_PlayerStatistics() => MsgType = TkMsgType.NM_HC_PlayerStatistics;
}

[MessagePortal(TkMsgType.NM_HC_UpdateCasern)]
public class PK_HC_UpdateCasern : BaseRequest
{
    public int MaxCount;
    public PK_HC_UpdateCasern() => MsgType = TkMsgType.NM_HC_UpdateCasern;
    public override IRequest ReadByte(ByteArray byteArray) { MaxCount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(MaxCount);
}

[MessagePortal(TkMsgType.NM_HC_PayTaskData)]
public class PK_HC_PayTaskData : BaseRequest
{
    public PK_HC_PayTaskData() => MsgType = TkMsgType.NM_HC_PayTaskData;
}

[MessagePortal(TkMsgType.NM_HC_ActivityOpenState)]
public class PK_HC_ActivityOpenState : BaseRequest
{
    public PK_HC_ActivityOpenState() => MsgType = TkMsgType.NM_HC_ActivityOpenState;
}

[MessagePortal(TkMsgType.NM_HC_ModuleOperate)]
public class PK_HC_ModuleOperate : BaseRequest
{
    public long TankGUID;
    public byte ModuleType;
    public ushort ModuleID;
    public byte ResultFlag;
    public PK_HC_ModuleOperate() => MsgType = TkMsgType.NM_HC_ModuleOperate;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankGUID = byteArray.ReadLong(); ModuleType = byteArray.ReadByte(); ModuleID = byteArray.ReadUShort(); ResultFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(TankGUID); byteArray.WriteByte(ModuleType); byteArray.WriteUShort(ModuleID); byteArray.WriteByte(ResultFlag); }
}

[MessagePortal(TkMsgType.NM_CL_SouzLogin)]
public class PK_CL_SouzLogin : BaseRequest
{
    public int ClientVersion;
    public int ResourceVersion;
    public int PersId;
    public string PersName = string.Empty;
    public int ServerId;
    public long ReqTime;
    public string StrMD5 = string.Empty;

    public PK_CL_SouzLogin() => MsgType = TkMsgType.NM_CL_SouzLogin;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        ClientVersion = byteArray.ReadInt();
        byteArray.Position += 8; // skip Version struct (4 ushorts)
        ResourceVersion = byteArray.ReadInt();
        PersId = byteArray.ReadInt();
        PersName = byteArray.ReadUTF(64);
        ServerId = byteArray.ReadInt();
        ReqTime = byteArray.ReadLong();
        StrMD5 = byteArray.ReadUTF(65);
        return this;
    }
}

[MessagePortal(TkMsgType.NM_HC_ChangeMoneyExp)]
public class PK_HC_ChangeMoneyExp : BaseRequest
{
    public int GameMoney;
    public int GoldMoney;
    public int PlayerExp;

    public PK_HC_ChangeMoneyExp() => MsgType = TkMsgType.NM_HC_ChangeMoneyExp;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(GameMoney);
        byteArray.WriteInt(GoldMoney);
        byteArray.WriteInt(PlayerExp);
    }
}

[MessagePortal(TkMsgType.NM_LC_SouzLogin)]
public class PK_LC_SouzLogin : BaseRequest
{
    public int PersId;
    public int ServerId;
    public string StrMD5 = string.Empty;

    public PK_LC_SouzLogin() => MsgType = TkMsgType.NM_LC_SouzLogin;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(PersId);
        byteArray.WriteInt(ServerId);
        byteArray.WriteUTF(StrMD5, 65);
    }
}

[MessagePortal(TkMsgType.NM_CH_LoadCarport)]
public class PK_CH_LoadCarport : BaseRequest
{
    public byte LoadFlag;
    public byte FirstLogin;
    public PK_CH_LoadCarport() => MsgType = TkMsgType.NM_CH_LoadCarport;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        LoadFlag = byteArray.ReadByte();
        FirstLogin = byteArray.ReadByte();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(LoadFlag);
        byteArray.WriteByte(FirstLogin);
    }
}

[MessagePortal(TkMsgType.NM_HC_TANKINFO)]
public class PK_HC_TANKINFO : BaseRequest
{
    public byte TankTotalCount;
    public TankData[] TankDatas = new TankData[0];
    public PK_HC_TANKINFO() => MsgType = TkMsgType.NM_HC_TANKINFO;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankTotalCount = byteArray.ReadByte();
        TankDatas = new TankData[TankTotalCount];
        for (int i = 0; i < TankTotalCount; i++) TankDatas[i] = byteArray.ReadTankData();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(TankTotalCount);
        for (int i = 0; i < TankTotalCount; i++) byteArray.WriteTankData(TankDatas[i]);
    }
}

[MessagePortal(TkMsgType.NM_HC_TankPlayerExp)]
public class PK_HC_TankPlayerExp : BaseRequest
{
    // ВАЖНО: клиент (ClientSocket.PK_HC_TankPlayerExp.ReadByte) читает
    // { int PlayerExp; int TankCount; {int TankId; int TankExp}[] } — сервер слал
    // только { byte TankCount; ... } без PlayerExp вообще. Из-за этого клиент
    // читал первые 4 байта полезной нагрузки (byte TankCount + первые 3 байта
    // TankId) как PlayerExp — мусорное значение, близкое по величине к
    // TankId/TankExp самого исследуемого танка. Это и есть тот самый «свободный
    // опыт резко прыгает на случайное маленькое число после каждого исследования»
    // — на самом деле счёт СВОБОДНОГО опыта на сервере не портился, портилось
    // только то, что клиент из-за сдвига байт ошибочно показывал на экране.
    public int PlayerExp;
    public int TankCount;
    public StructTankExp[] TankArray = new StructTankExp[0];

    public PK_HC_TankPlayerExp() => MsgType = TkMsgType.NM_HC_TankPlayerExp;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        PlayerExp = byteArray.ReadInt();
        TankCount = byteArray.ReadInt();
        TankArray = new StructTankExp[TankCount];
        for (int i = 0; i < TankCount; i++)
        {
            TankArray[i].TankId = byteArray.ReadInt();
            TankArray[i].TankExp = byteArray.ReadInt();
        }

        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(PlayerExp);
        byteArray.WriteInt(TankCount);
        for (int i = 0; i < TankCount; i++)
        {
            byteArray.WriteInt(TankArray[i].TankId);
            byteArray.WriteInt(TankArray[i].TankExp);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_ADDTANK)]
public class PK_HC_AddTank : BaseRequest
{
    public TankData TankDataItem = new();
    public PK_HC_AddTank() => MsgType = TkMsgType.NM_HC_ADDTANK;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankDataItem = byteArray.ReadTankData();
        return this;
    }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteTankData(TankDataItem);
}

[MessagePortal(TkMsgType.NM_HC_AllTankAttribute)]
public class PK_HC_AllTankAttribute : BaseRequest
{
    public byte TankCount;
    public TankAllAttributeStruct[] TankAttributes = new TankAllAttributeStruct[0];

    public PK_HC_AllTankAttribute() => MsgType = TkMsgType.NM_HC_AllTankAttribute;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(TankCount);
        for (int i = 0; i < TankCount; i++)
        {
            byteArray.WriteTankAllAttributeStruct(TankAttributes[i]);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_ResearchRequest)]
public class PK_CH_ResearchRequest : BaseRequest
{
    public byte TypeId;
    public ushort TableId;
    public short TankId;
    public PK_CH_ResearchRequest() => MsgType = TkMsgType.NM_CH_ResearchRequest;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TypeId = byteArray.ReadByte(); TableId = byteArray.ReadUShort(); TankId = byteArray.ReadShort(); return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(TypeId); byteArray.WriteUShort(TableId); byteArray.WriteShort(TankId);
    }
}

[MessagePortal(TkMsgType.NM_HC_ResearchResult)]
public class PK_HC_ResearchResult : BaseRequest
{
    public byte PartCount;
    public ResearchNode[] PartList = new ResearchNode[0];
    public PK_HC_ResearchResult() => MsgType = TkMsgType.NM_HC_ResearchResult;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        PartCount = byteArray.ReadByte(); PartList = new ResearchNode[PartCount];
        for (int i = 0; i < PartCount; i++) { PartList[i].TypeId = byteArray.ReadByte(); PartList[i].Id = byteArray.ReadUShort(); }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(PartCount);
        for (int i = 0; i < PartCount; i++) { byteArray.WriteByte((byte)PartList[i].TypeId); byteArray.WriteUShort((ushort)PartList[i].Id); }
    }
}

[MessagePortal(TkMsgType.NM_HC_TankResearchList)]
public class PK_HC_TankResearchList : BaseRequest
{
    public int TankCount;
    public ResearchTank[] TankList = new ResearchTank[0];
    public PK_HC_TankResearchList() => MsgType = TkMsgType.NM_HC_TankResearchList;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankCount = byteArray.ReadInt(); TankList = new ResearchTank[TankCount];
        for (int i = 0; i < TankCount; i++) { TankList[i].Id = byteArray.ReadInt(); TankList[i].HasBuy = byteArray.ReadByte(); }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(TankCount);
        for (int i = 0; i < TankCount; i++) { byteArray.WriteInt(TankList[i].Id); byteArray.WriteByte(TankList[i].HasBuy); }
    }
}

[MessagePortal(TkMsgType.NM_HC_PartResearchList)]
public class PK_HC_PartResearchList : BaseRequest
{
    public int PartCount;
    public ResearchNode[] PartList = new ResearchNode[0];
    public PK_HC_PartResearchList() => MsgType = TkMsgType.NM_HC_PartResearchList;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        PartCount = byteArray.ReadInt(); PartList = new ResearchNode[PartCount];
        for (int i = 0; i < PartCount; i++) { PartList[i].TypeId = byteArray.ReadInt(); PartList[i].Id = byteArray.ReadInt(); }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(PartCount);
        for (int i = 0; i < PartCount; i++) { byteArray.WriteInt(PartList[i].TypeId); byteArray.WriteInt(PartList[i].Id); }
    }
}

[MessagePortal(TkMsgType.NM_CH_BUYITEM)]
public class PK_CH_BUYITEM : BaseRequest
{
    public byte ItemType; public int ItemId; public int ItemNum; public byte ItemFlag;
    public PK_CH_BUYITEM() => MsgType = TkMsgType.NM_CH_BUYITEM;
    public override IRequest ReadByte(ByteArray byteArray)
    { ItemType = byteArray.ReadByte(); ItemId = byteArray.ReadInt(); ItemNum = byteArray.ReadInt(); ItemFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(ItemType); byteArray.WriteInt(ItemId); byteArray.WriteInt(ItemNum); byteArray.WriteByte(ItemFlag); }
}

[MessagePortal(TkMsgType.NM_HC_BUYITEM)]
public class PK_HC_BUYITEM : BaseRequest
{
    public byte IsBuy;
    public PK_HC_BUYITEM() => MsgType = TkMsgType.NM_HC_BUYITEM;
    public override IRequest ReadByte(ByteArray byteArray) { IsBuy = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(IsBuy);
}

[MessagePortal(TkMsgType.NM_HC_UPDATEITEM)]
public class PK_HC_UPDATEITEM : BaseRequest
{
    public byte ItemType; public int ItemId; public int ItemNum;
    public PK_HC_UPDATEITEM() => MsgType = TkMsgType.NM_HC_UPDATEITEM;
    public override IRequest ReadByte(ByteArray byteArray)
    { ItemType = byteArray.ReadByte(); ItemId = byteArray.ReadInt(); ItemNum = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(ItemType); byteArray.WriteInt(ItemId); byteArray.WriteInt(ItemNum); }
}

[MessagePortal(TkMsgType.NM_CH_SELLITEM)]
public class PK_CH_SELLITEM : BaseRequest
{
    public byte SellItemType; public int SellItemId; public int SellItemNum; public byte SellItemFlag;
    public PK_CH_SELLITEM() => MsgType = TkMsgType.NM_CH_SELLITEM;
    public override IRequest ReadByte(ByteArray byteArray)
    { SellItemType = byteArray.ReadByte(); SellItemId = byteArray.ReadInt(); SellItemNum = byteArray.ReadInt(); SellItemFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(SellItemType); byteArray.WriteInt(SellItemId); byteArray.WriteInt(SellItemNum); byteArray.WriteByte(SellItemFlag); }
}

[MessagePortal(TkMsgType.NM_HC_SELLITEM)]
public class PK_HC_SELLITEM : BaseRequest
{
    public byte IsSell;
    public PK_HC_SELLITEM() => MsgType = TkMsgType.NM_HC_SELLITEM;
    public override IRequest ReadByte(ByteArray byteArray) { IsSell = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(IsSell);
}

[MessagePortal(TkMsgType.NM_CH_BuyCrew)]
public class PK_CH_BuyCrew : BaseRequest
{
    public int CrewId; public long TankGuid; public short FamilyNameId; public short NameId;
    public short BigIconId; public short SmallIconId; public short ResumeId;
    public PK_CH_BuyCrew() => MsgType = TkMsgType.NM_CH_BuyCrew;
    public override IRequest ReadByte(ByteArray byteArray)
    { CrewId = byteArray.ReadInt(); TankGuid = byteArray.ReadLong(); FamilyNameId = byteArray.ReadShort(); NameId = byteArray.ReadShort(); BigIconId = byteArray.ReadShort(); SmallIconId = byteArray.ReadShort(); ResumeId = byteArray.ReadShort(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(CrewId); byteArray.WriteLong(TankGuid); byteArray.WriteShort(FamilyNameId); byteArray.WriteShort(NameId); byteArray.WriteShort(BigIconId); byteArray.WriteShort(SmallIconId); byteArray.WriteShort(ResumeId); }
}

[MessagePortal(TkMsgType.NM_HC_AddCrew)]
public class PK_HC_AddCrew : BaseRequest
{
    public int Id; public int CurExp; public int CurLv; public long Guid; public long TankGuid;
    public short FamilyNameId; public short NameId; public short BigIconId; public short SmallIconId;
    public short ResumeId; public int Lastlearn; public int LastSkillExp;
    public PK_HC_AddCrew() => MsgType = TkMsgType.NM_HC_AddCrew;
    public override IRequest ReadByte(ByteArray byteArray)
    { Id = byteArray.ReadInt(); CurExp = byteArray.ReadInt(); CurLv = byteArray.ReadInt(); Guid = byteArray.ReadLong(); TankGuid = byteArray.ReadLong(); FamilyNameId = byteArray.ReadShort(); NameId = byteArray.ReadShort(); BigIconId = byteArray.ReadShort(); SmallIconId = byteArray.ReadShort(); ResumeId = byteArray.ReadShort(); Lastlearn = byteArray.ReadInt(); LastSkillExp = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(Id); byteArray.WriteInt(CurExp); byteArray.WriteInt(CurLv); byteArray.WriteLong(Guid); byteArray.WriteLong(TankGuid); byteArray.WriteShort(FamilyNameId); byteArray.WriteShort(NameId); byteArray.WriteShort(BigIconId); byteArray.WriteShort(SmallIconId); byteArray.WriteShort(ResumeId); byteArray.WriteInt(Lastlearn); byteArray.WriteInt(LastSkillExp); }
}

[MessagePortal(TkMsgType.NM_HC_UpdateCrew)]
public class PK_HC_UpdateCrew : BaseRequest
{
    public int CurExp; public int CurLv; public long CrewGuid; public long TankGuid;
    public PK_HC_UpdateCrew() => MsgType = TkMsgType.NM_HC_UpdateCrew;
    public override IRequest ReadByte(ByteArray byteArray)
    { CurExp = byteArray.ReadInt(); CurLv = byteArray.ReadInt(); CrewGuid = byteArray.ReadLong(); TankGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(CurExp); byteArray.WriteInt(CurLv); byteArray.WriteLong(CrewGuid); byteArray.WriteLong(TankGuid); }
}

[MessagePortal(TkMsgType.NM_HC_DelCrew)]
public class PK_HC_DelCrew : BaseRequest
{
    public long CrewGuid;
    public PK_HC_DelCrew() => MsgType = TkMsgType.NM_HC_DelCrew;
    public override IRequest ReadByte(ByteArray byteArray) { CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(CrewGuid);
}

[MessagePortal(TkMsgType.NM_CH_OperCrew)]
public class PK_CH_OperCrew : BaseRequest
{
    public byte OperType; public long CrewGuid; public long TankGuid;
    public PK_CH_OperCrew() => MsgType = TkMsgType.NM_CH_OperCrew;
    public override IRequest ReadByte(ByteArray byteArray)
    { OperType = byteArray.ReadByte(); CrewGuid = byteArray.ReadLong(); TankGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(OperType); byteArray.WriteLong(CrewGuid); byteArray.WriteLong(TankGuid); }
}

[MessagePortal(TkMsgType.NM_HC_Update_CrewSkill)]
public class PK_HC_Update_CrewSkill : BaseRequest
{
    public long CrewGuid; public int LastSkillId; public int LastSkillExp; public int[] Ids = new int[30];
    public PK_HC_Update_CrewSkill() => MsgType = TkMsgType.NM_HC_Update_CrewSkill;
    public override IRequest ReadByte(ByteArray byteArray)
    { CrewGuid = byteArray.ReadLong(); LastSkillId = byteArray.ReadInt(); LastSkillExp = byteArray.ReadInt();
      for (int i = 0; i < 30; i++) Ids[i] = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(CrewGuid); byteArray.WriteInt(LastSkillId); byteArray.WriteInt(LastSkillExp);
      for (int i = 0; i < 30; i++) byteArray.WriteInt(Ids[i]); }
}

[MessagePortal(TkMsgType.NM_HC_Update_CrewCurSkill)]
public class PK_HC_Update_CrewCurSkill : BaseRequest
{
    public int LastSkillExp; public int LastSkillId; public long CrewGuid;
    public PK_HC_Update_CrewCurSkill() => MsgType = TkMsgType.NM_HC_Update_CrewCurSkill;
    public override IRequest ReadByte(ByteArray byteArray)
    { LastSkillId = byteArray.ReadInt(); LastSkillExp = byteArray.ReadInt(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(LastSkillId); byteArray.WriteInt(LastSkillExp); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_CH_LearnCrewSkill)]
public class PK_CH_LearnCrewSkill : BaseRequest
{
    public long CrewGuid; public int SkillId;
    public PK_CH_LearnCrewSkill() => MsgType = TkMsgType.NM_CH_LearnCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { SkillId = byteArray.ReadInt(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(SkillId); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_HC_LearnCrewSkill)]
public class PK_HC_LearnCrewSkill : BaseRequest
{
    public byte Flag; public int SkillId; public long CrewGuid;
    public PK_HC_LearnCrewSkill() => MsgType = TkMsgType.NM_HC_LearnCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { Flag = byteArray.ReadByte(); SkillId = byteArray.ReadInt(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Flag); byteArray.WriteInt(SkillId); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_CH_ResetCrewSkill)]
public class PK_CH_ResetCrewSkill : BaseRequest
{
    public byte ResetFlag; public long CrewGuid;
    public PK_CH_ResetCrewSkill() => MsgType = TkMsgType.NM_CH_ResetCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { ResetFlag = byteArray.ReadByte(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(ResetFlag); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_HC_ResetCrewSkill)]
public class PK_HC_ResetCrewSkill : BaseRequest
{
    public byte ResetFlag; public byte ResultFlag; public long CrewGuid;
    public PK_HC_ResetCrewSkill() => MsgType = TkMsgType.NM_HC_ResetCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { ResetFlag = byteArray.ReadByte(); ResultFlag = byteArray.ReadByte(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(ResetFlag); byteArray.WriteByte(ResultFlag); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_CH_QuickLearnCrewSkill)]
public class PK_CH_QuickLearnCrewSkill : BaseRequest
{
    public long CrewGuid;
    public PK_CH_QuickLearnCrewSkill() => MsgType = TkMsgType.NM_CH_QuickLearnCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(CrewGuid);
}

[MessagePortal(TkMsgType.NM_HC_QuickLearnCrewSkill)]
public class PK_HC_QuickLearnCrewSkill : BaseRequest
{
    public byte Result; public long CrewGuid;
    public PK_HC_QuickLearnCrewSkill() => MsgType = TkMsgType.NM_HC_QuickLearnCrewSkill;
    public override IRequest ReadByte(ByteArray byteArray) { Result = byteArray.ReadByte(); CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Result); byteArray.WriteLong(CrewGuid); }
}

[MessagePortal(TkMsgType.NM_CH_CrewFastTrain)]
public class PK_CH_CrewFastTrain : BaseRequest
{
    public int TankID; public byte Operate;
    public PK_CH_CrewFastTrain() => MsgType = TkMsgType.NM_CH_CrewFastTrain;
    public override IRequest ReadByte(ByteArray byteArray) { TankID = byteArray.ReadInt(); Operate = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(TankID); byteArray.WriteByte(Operate); }
}

[MessagePortal(TkMsgType.NM_HC_CrewFastTrainTankInfo)]
public class PK_HC_CrewFastTrainTankInfo : BaseRequest
{
    public int TankCount; public CrewFastTrainOnTank[] TankIDArray = new CrewFastTrainOnTank[0];
    public PK_HC_CrewFastTrainTankInfo() => MsgType = TkMsgType.NM_HC_CrewFastTrainTankInfo;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankCount = byteArray.ReadShort(); TankIDArray = new CrewFastTrainOnTank[TankCount];
      for (int i = 0; i < TankCount; i++) { TankIDArray[i].TankID = byteArray.ReadInt(); TankIDArray[i].State = byteArray.ReadByte(); } return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteShort((short)TankCount); for (int i = 0; i < TankCount; i++) { byteArray.WriteInt(TankIDArray[i].TankID); byteArray.WriteByte(TankIDArray[i].State); } }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_CREATE)]
public class PK_CHW_CORPS_CREATE : BaseRequest
{
    public long UserGUID; public string CorpsName = string.Empty; public string CorpsTag = string.Empty;
    public PK_CHW_CORPS_CREATE() => MsgType = TkMsgType.NM_CHW_CORPS_CREATE;
    public override IRequest ReadByte(ByteArray byteArray)
    { UserGUID = byteArray.ReadLong(); CorpsName = byteArray.ReadUTF(21); CorpsTag = byteArray.ReadUTF(11); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(UserGUID); byteArray.WriteUTF(CorpsName, 21); byteArray.WriteUTF(CorpsTag, 11); }
}

[MessagePortal(TkMsgType.NM_WHC_CORPS_LIST)]
public class PK_WHC_CORPS_LIST : BaseRequest
{
    public int CorpsTotalCount; public ushort CurrentPageCount; public int CurrentPageNumber;
    public CorpsInfo[] CorpsList = new CorpsInfo[0];
    public PK_WHC_CORPS_LIST() => MsgType = TkMsgType.NM_WHC_CORPS_LIST;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        CorpsTotalCount = byteArray.ReadInt(); CurrentPageCount = byteArray.ReadUShort(); CurrentPageNumber = byteArray.ReadInt();
        CorpsList = new CorpsInfo[CurrentPageNumber];
        for (int i = 0; i < CurrentPageNumber; i++)
        {
            CorpsList[i].CorpsID = byteArray.ReadLong(); CorpsList[i].CorpsName = byteArray.ReadUTF(21);
            CorpsList[i].CorpsLevel = byteArray.ReadUShort(); CorpsList[i].PlayerCount = byteArray.ReadUShort();
            CorpsList[i].Notice = byteArray.ReadUTF(200); CorpsList[i].Introduce = byteArray.ReadUTF(200);
            CorpsList[i].CreateTime = byteArray.ReadUTF(20); CorpsList[i].State = byteArray.ReadByte();
            CorpsList[i].LeaderName = byteArray.ReadUTF(64); byteArray.ReadByte();
            CorpsList[i].MaxMemberCount = byteArray.ReadInt(); CorpsList[i].BattleCap = byteArray.ReadLong();
            CorpsList[i].MaxMemSize = byteArray.ReadInt();
        }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(CorpsTotalCount); byteArray.WriteUShort(CurrentPageCount); byteArray.WriteInt(CurrentPageNumber);
        for (int i = 0; i < CurrentPageNumber; i++)
        {
            byteArray.WriteLong(CorpsList[i].CorpsID); byteArray.WriteUTF(CorpsList[i].CorpsName, 21);
            byteArray.WriteUShort(CorpsList[i].CorpsLevel); byteArray.WriteUShort(CorpsList[i].PlayerCount);
            byteArray.WriteUTF(CorpsList[i].Notice, 200); byteArray.WriteUTF(CorpsList[i].Introduce, 200);
            byteArray.WriteUTF(CorpsList[i].CreateTime, 20); byteArray.WriteByte(CorpsList[i].State);
            byteArray.WriteUTF(CorpsList[i].LeaderName, 64); byteArray.WriteByte(0);
            byteArray.WriteInt(CorpsList[i].MaxMemberCount); byteArray.WriteLong(CorpsList[i].BattleCap);
            byteArray.WriteInt(CorpsList[i].MaxMemSize);
        }
    }
}

[MessagePortal(TkMsgType.NM_WHC_CORPS_MEMBER)]
public class PK_WHC_CORPS_MEMBER : BaseRequest
{
    public long GUID; public short CurrentPageCount; public int MemberNumber; public int PageNumber; public byte PageType;
    public CorpsMemberInfo[] Members = new CorpsMemberInfo[0];
    public PK_WHC_CORPS_MEMBER() => MsgType = TkMsgType.NM_WHC_CORPS_MEMBER;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        GUID = byteArray.ReadLong(); MemberNumber = byteArray.ReadInt(); CurrentPageCount = byteArray.ReadShort();
        PageType = byteArray.ReadByte(); PageNumber = byteArray.ReadInt(); Members = new CorpsMemberInfo[PageNumber];
        for (int i = 0; i < PageNumber; i++)
        {
            Members[i].CorpsID = byteArray.ReadLong(); Members[i].UserGUID = byteArray.ReadLong();
            Members[i].Job = byteArray.ReadByte(); Members[i].JoinDate = byteArray.ReadUTF(20);
            Members[i].LastLoginDate = byteArray.ReadUTF(20); Members[i].UserName = byteArray.ReadUTF(64);
            Members[i].LoyalPoint = byteArray.ReadInt(); Members[i].BattleCap = byteArray.ReadInt();
        }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(GUID); byteArray.WriteInt(MemberNumber); byteArray.WriteShort(CurrentPageCount);
        byteArray.WriteByte(PageType); byteArray.WriteInt(PageNumber);
        for (int i = 0; i < PageNumber; i++)
        {
            byteArray.WriteLong(Members[i].CorpsID); byteArray.WriteLong(Members[i].UserGUID);
            byteArray.WriteByte(Members[i].Job); byteArray.WriteUTF(Members[i].JoinDate, 20);
            byteArray.WriteUTF(Members[i].LastLoginDate, 20); byteArray.WriteUTF(Members[i].UserName, 64);
            byteArray.WriteInt(Members[i].LoyalPoint); byteArray.WriteInt(Members[i].BattleCap);
        }
    }
}

[MessagePortal(TkMsgType.NM_WHC_CORPS_APPLICATION)]
public class PK_WHC_CORPS_APPLICATION : BaseRequest
{
    public byte Type; public int ApplicationTotalCount; public ushort CurrentPageCount; public int CurrentPageNumber;
    public CorpsApplicationInfo[] Applications = new CorpsApplicationInfo[0];
    public PK_WHC_CORPS_APPLICATION() => MsgType = TkMsgType.NM_WHC_CORPS_APPLICATION;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        Type = byteArray.ReadByte(); byteArray.ReadLong(); ApplicationTotalCount = byteArray.ReadInt();
        CurrentPageCount = byteArray.ReadUShort(); CurrentPageNumber = byteArray.ReadInt();
        Applications = new CorpsApplicationInfo[CurrentPageNumber];
        for (int i = 0; i < CurrentPageNumber; i++)
        { byteArray.ReadLong(); Applications[i].GUID = byteArray.ReadLong(); byteArray.ReadByte(); byteArray.ReadByte();
          Applications[i].JoinTime = byteArray.ReadUTF(20); Applications[i].UserName = byteArray.ReadUTF(64); }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Type); byteArray.WriteLong(0); byteArray.WriteInt(ApplicationTotalCount);
        byteArray.WriteUShort(CurrentPageCount); byteArray.WriteInt(CurrentPageNumber);
        for (int i = 0; i < CurrentPageNumber; i++)
        { byteArray.WriteLong(0); byteArray.WriteLong(Applications[i].GUID); byteArray.WriteByte(0); byteArray.WriteByte(0);
          byteArray.WriteUTF(Applications[i].JoinTime, 20); byteArray.WriteUTF(Applications[i].UserName, 64); }
    }
}

[MessagePortal(TkMsgType.NM_WHC_CORPS_INVITATION)]
public class PK_WHC_CORPS_INVITATION : BaseRequest
{
    public long PlayerGuid; public long InvitePlayerGuid; public string PlayerName = string.Empty;
    public string CorpsName = string.Empty; public int DataTime;
    public PK_WHC_CORPS_INVITATION() => MsgType = TkMsgType.NM_WHC_CORPS_INVITATION;
    public override IRequest ReadByte(ByteArray byteArray)
    { PlayerGuid = byteArray.ReadLong(); InvitePlayerGuid = byteArray.ReadLong(); PlayerName = byteArray.ReadUTF(64);
      CorpsName = byteArray.ReadUTF(21); DataTime = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(PlayerGuid); byteArray.WriteLong(InvitePlayerGuid); byteArray.WriteUTF(PlayerName, 64);
      byteArray.WriteUTF(CorpsName, 21); byteArray.WriteInt(DataTime); }
}

[MessagePortal(TkMsgType.NM_CH_TournamentRegister)]
public class PK_CH_TournamentRegister : BaseRequest
{
    public byte Word;
    public PK_CH_TournamentRegister() => MsgType = TkMsgType.NM_CH_TournamentRegister;
    public override IRequest ReadByte(ByteArray byteArray) { Word = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Word);
}

[MessagePortal(TkMsgType.NM_HC_TournamentRegisterResult)]
public class PK_HC_TournamentRegisterResult : BaseRequest
{
    public byte Result;
    public PK_HC_TournamentRegisterResult() => MsgType = TkMsgType.NM_HC_TournamentRegisterResult;
    public override IRequest ReadByte(ByteArray byteArray) { Result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Result);
}

[MessagePortal(TkMsgType.NM_HC_TournamentInfo)]
public class PK_HC_TournamentInfo : BaseRequest
{
    public byte Status; public byte RegisterState; public byte GetRewardState;
    public PK_HC_TournamentInfo() => MsgType = TkMsgType.NM_HC_TournamentInfo;
    public override IRequest ReadByte(ByteArray byteArray)
    { Status = byteArray.ReadByte(); RegisterState = byteArray.ReadByte(); GetRewardState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(Status); byteArray.WriteByte(RegisterState); byteArray.WriteByte(GetRewardState); }
}

[MessagePortal(TkMsgType.NM_HC_TournamentAllRange)]
public class PK_HC_TournamentAllRange : BaseRequest
{
    public byte Count; public TournamentRangeInfo[] AllInfos = new TournamentRangeInfo[0];
    public PK_HC_TournamentAllRange() => MsgType = TkMsgType.NM_HC_TournamentAllRange;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        Count = byteArray.ReadByte(); AllInfos = new TournamentRangeInfo[Count];
        for (int i = 0; i < Count; i++)
        { AllInfos[i].Range = byteArray.ReadInt(); AllInfos[i].Name = byteArray.ReadUTF(64); AllInfos[i].Damage = byteArray.ReadInt();
          AllInfos[i].VictoryTime = byteArray.ReadInt(); AllInfos[i].TotalBattleTime = byteArray.ReadInt();
          AllInfos[i].MaxDamage = byteArray.ReadInt(); AllInfos[i].VictoryRate = AllInfos[i].TotalBattleTime > 0 ? (float)AllInfos[i].VictoryTime / AllInfos[i].TotalBattleTime * 100f : 0f; }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Count);
        for (int i = 0; i < Count; i++)
        { byteArray.WriteInt(AllInfos[i].Range); byteArray.WriteUTF(AllInfos[i].Name, 64); byteArray.WriteInt(AllInfos[i].Damage);
          byteArray.WriteInt(AllInfos[i].VictoryTime); byteArray.WriteInt(AllInfos[i].TotalBattleTime); byteArray.WriteInt(AllInfos[i].MaxDamage); }
    }
}

[MessagePortal(TkMsgType.NM_HC_TournamentSelfRange)]
public class PK_HC_TournamentSelfRange : BaseRequest
{
    public byte Count; public int SelfRank; public TournamentRangeInfo[] SelfInfos = new TournamentRangeInfo[0];
    public PK_HC_TournamentSelfRange() => MsgType = TkMsgType.NM_HC_TournamentSelfRange;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        Count = byteArray.ReadByte(); SelfRank = byteArray.ReadInt(); SelfInfos = new TournamentRangeInfo[Count];
        for (int i = 0; i < Count; i++)
        { SelfInfos[i].Range = byteArray.ReadInt(); SelfInfos[i].Name = byteArray.ReadUTF(64); SelfInfos[i].Damage = byteArray.ReadInt();
          SelfInfos[i].VictoryTime = byteArray.ReadInt(); SelfInfos[i].TotalBattleTime = byteArray.ReadInt();
          SelfInfos[i].MaxDamage = byteArray.ReadInt(); SelfInfos[i].VictoryRate = SelfInfos[i].TotalBattleTime > 0 ? (float)SelfInfos[i].VictoryTime / SelfInfos[i].TotalBattleTime * 100f : 0f; }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(Count); byteArray.WriteInt(SelfRank);
        for (int i = 0; i < Count; i++)
        { byteArray.WriteInt(SelfInfos[i].Range); byteArray.WriteUTF(SelfInfos[i].Name, 64); byteArray.WriteInt(SelfInfos[i].Damage);
          byteArray.WriteInt(SelfInfos[i].VictoryTime); byteArray.WriteInt(SelfInfos[i].TotalBattleTime); byteArray.WriteInt(SelfInfos[i].MaxDamage); }
    }
}

[MessagePortal(TkMsgType.NM_CH_BuyBox)]
public class PK_CH_BuyBox : BaseRequest
{
    public byte Level;
    public PK_CH_BuyBox() => MsgType = TkMsgType.NM_CH_BuyBox;
    public override IRequest ReadByte(ByteArray byteArray) { Level = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Level);
}

[MessagePortal(TkMsgType.NM_HC_BuyBox)]
public class PK_HC_BuyBox : BaseRequest
{
    public byte Level; public byte BuyResult;
    public PK_HC_BuyBox() => MsgType = TkMsgType.NM_HC_BuyBox;
    public override IRequest ReadByte(ByteArray byteArray) { Level = byteArray.ReadByte(); BuyResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Level); byteArray.WriteByte(BuyResult); }
}

[MessagePortal(TkMsgType.NM_HC_LeftSuperPrizeCount)]
public class PK_HC_LeftSuperPrizeCount : BaseRequest
{
    public int RewardLeft;
    public PK_HC_LeftSuperPrizeCount() => MsgType = TkMsgType.NM_HC_LeftSuperPrizeCount;
    public override IRequest ReadByte(ByteArray byteArray) { RewardLeft = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(RewardLeft);
}

[MessagePortal(TkMsgType.NM_HC_LotteryData)]
public class PK_HC_LotteryData : BaseRequest
{
    public int LeftTime; public byte HaveGot; public float PowerValue;
    public bool IsEveryDayFirstLottery_One; public bool IsEveryDayFirstLottery_Ten;
    public PK_HC_LotteryData() => MsgType = TkMsgType.NM_HC_LotteryData;
    public override IRequest ReadByte(ByteArray byteArray)
    { LeftTime = byteArray.ReadInt(); HaveGot = byteArray.ReadByte(); PowerValue = byteArray.ReadFloat();
      byte flags = byteArray.ReadByte(); IsEveryDayFirstLottery_One = (flags & 1) == 0; IsEveryDayFirstLottery_Ten = (flags & 2) == 0; return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(LeftTime); byteArray.WriteByte(HaveGot); byteArray.WriteFloat(PowerValue);
      byte flags = (byte)((IsEveryDayFirstLottery_One ? 0 : 1) | (IsEveryDayFirstLottery_Ten ? 0 : 2)); byteArray.WriteByte(flags); }
}

[MessagePortal(TkMsgType.NM_CH_TankCoatOperator)]
public class PK_CH_TankCoatOperator : BaseRequest
{
    public ushort TankId; public ushort CoatTableID; public byte OperatorType; public byte CoatSlotIndex; public byte BuyType;
    public PK_CH_TankCoatOperator() => MsgType = TkMsgType.NM_CH_TankCoatOperator;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankId = byteArray.ReadUShort(); CoatTableID = byteArray.ReadUShort(); OperatorType = byteArray.ReadByte();
      CoatSlotIndex = byteArray.ReadByte(); BuyType = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteUShort(TankId); byteArray.WriteUShort(CoatTableID); byteArray.WriteByte(OperatorType);
      byteArray.WriteByte(CoatSlotIndex); byteArray.WriteByte(BuyType); }
}

[MessagePortal(TkMsgType.NM_HC_TankCoatInfo)]
public class PK_HC_TankCoatInfo : BaseRequest
{
    /// <summary>0 — полная синхронизация (клиент молча запоминает),
    /// 1 — изменение: Player.OnSynTankCoatInfo вызовет OnTankCoatChange и
    /// перекрасит модель танка в ангаре прямо сейчас.</summary>
    public byte OperateType;
    public PK_TankCoatInfo[] TankCoatsInfoArray = Array.Empty<PK_TankCoatInfo>();

    /// <summary>Всегда равен длине массива — иначе клиент читает мусор.</summary>
    public byte Count
    {
        get => (byte)Math.Min(255, TankCoatsInfoArray?.Length ?? 0);
        set { /* совместимость: реальная длина берётся из массива */ }
    }

    public PK_HC_TankCoatInfo() => MsgType = TkMsgType.NM_HC_TankCoatInfo;

    public override IRequest ReadByte(ByteArray byteArray)
    { OperateType = byteArray.ReadByte(); int n = byteArray.ReadByte(); TankCoatsInfoArray = new PK_TankCoatInfo[n];
      for (int i = 0; i < n; i++) TankCoatsInfoArray[i] = byteArray.ReadPKTankCoatInfo(); return this; }

    public override void WriteByte(ByteArray byteArray)
    { byte n = Count; byteArray.WriteByte(OperateType); byteArray.WriteByte(n);
      for (int i = 0; i < n; i++) byteArray.WritePKTankCoatInfo(TankCoatsInfoArray[i]); }
}

[MessagePortal(TkMsgType.NM_CH_EquipmentOperate)]
public class PK_CH_EquipmentOperate : BaseRequest
{
    public long TankGuid; public byte OperateType; public ushort EquipmentID; public byte IsBuy;
    public PK_CH_EquipmentOperate() => MsgType = TkMsgType.NM_CH_EquipmentOperate;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankGuid = byteArray.ReadLong(); OperateType = byteArray.ReadByte(); EquipmentID = byteArray.ReadUShort(); IsBuy = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(TankGuid); byteArray.WriteByte(OperateType); byteArray.WriteUShort(EquipmentID); byteArray.WriteByte(IsBuy); }
}

[MessagePortal(TkMsgType.NM_HC_EquipmentOperate)]
public class PK_HC_EquipmentOperate : BaseRequest
{
    public long TankGuid; public byte OperateType; public ushort EquipmentID; public byte IsBuy;
    public PK_HC_EquipmentOperate() => MsgType = TkMsgType.NM_HC_EquipmentOperate;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankGuid = byteArray.ReadLong(); OperateType = byteArray.ReadByte(); EquipmentID = byteArray.ReadUShort(); IsBuy = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(TankGuid); byteArray.WriteByte(OperateType); byteArray.WriteUShort(EquipmentID); byteArray.WriteByte(IsBuy); }
}

[MessagePortal(TkMsgType.NM_HC_TankEquipment)]
public class PK_HC_TankEquipment : BaseRequest
{
    public byte TankCount; public PK_HC_TankEquipmentInfor[] TankEquipments = new PK_HC_TankEquipmentInfor[0];
    public PK_HC_TankEquipment() => MsgType = TkMsgType.NM_HC_TankEquipment;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankCount = byteArray.ReadByte(); TankEquipments = new PK_HC_TankEquipmentInfor[TankCount];
        for (int i = 0; i < TankCount; i++)
        { TankEquipments[i] = new PK_HC_TankEquipmentInfor(); TankEquipments[i].TankGuid = byteArray.ReadLong();
          for (int j = 0; j < 36; j++) TankEquipments[i].EquipmentIDs[j] = byteArray.ReadUShort(); }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(TankCount);
        for (int i = 0; i < TankCount; i++)
        { byteArray.WriteLong(TankEquipments[i].TankGuid); for (int j = 0; j < 36; j++) byteArray.WriteUShort(TankEquipments[i].EquipmentIDs[j]); }
    }
}

[MessagePortal(TkMsgType.NM_HC_TankEquipmentData)]
public class PK_HC_TankEquipmentData : BaseRequest
{
    public long TankGuid; public TankEquipmentData? TankEquData;
    public PK_HC_TankEquipmentData() => MsgType = TkMsgType.NM_HC_TankEquipmentData;
    public override IRequest ReadByte(ByteArray byteArray) { TankGuid = byteArray.ReadLong(); TankEquData = byteArray.ReadTankEquipmentData(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(TankGuid); if (TankEquData != null) byteArray.WriteTankEquipmentData(TankEquData); }
}

[MessagePortal(TkMsgType.NM_CH_BuyShell)]
public class PK_CH_BuyShell : BaseRequest
{
    public int NKindCount;
    public ShellBuyStruct[] Shells = System.Array.Empty<ShellBuyStruct>();
    public PK_CH_BuyShell() => MsgType = TkMsgType.NM_CH_BuyShell;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        NKindCount = byteArray.ReadInt();
        int shellCount = System.Math.Max(0, System.Math.Min(NKindCount, byteArray.BytesAvailable / 8));
        Shells = new ShellBuyStruct[shellCount];
        for (int i = 0; i < shellCount; i++)
        {
            Shells[i].ShellID = byteArray.ReadInt();
            Shells[i].Count = byteArray.ReadInt();
        }

        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        int shellCount = Shells?.Length ?? 0;
        if (NKindCount > 0)
        {
            shellCount = System.Math.Min(shellCount, NKindCount);
        }

        byteArray.WriteInt(shellCount);
        for (int i = 0; i < shellCount; i++)
        {
            byteArray.WriteInt(Shells[i].ShellID);
            byteArray.WriteInt(Shells[i].Count);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_BuyShell)]
public class PK_HC_BuyShell : BaseRequest
{
    public bool Success;
    public PK_HC_BuyShell() => MsgType = TkMsgType.NM_HC_BuyShell;
    public override IRequest ReadByte(ByteArray byteArray) { Success = byteArray.ReadBool(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteBool(Success);
}

[MessagePortal(TkMsgType.NM_CH_SetShell)]
public class PK_CH_SetShell : BaseRequest
{
    public long TankGuid;
    public SetShellStruct[] SetShells = System.Array.Empty<SetShellStruct>();
    public PK_CH_SetShell() => MsgType = TkMsgType.NM_CH_SetShell;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankGuid = byteArray.ReadLong();
        var shells = new System.Collections.Generic.List<SetShellStruct>();
        while (byteArray.BytesAvailable >= 9)
        {
            var shell = new SetShellStruct
            {
                ShellSlotId = byteArray.ReadByte(),
                ShellID = byteArray.ReadInt(),
                Count = byteArray.ReadInt()
            };
            shells.Add(shell);
        }

        SetShells = shells.ToArray();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(TankGuid);
        foreach (var shell in SetShells)
        {
            byteArray.WriteByte(shell.ShellSlotId);
            byteArray.WriteInt(shell.ShellID);
            byteArray.WriteInt(shell.Count);
        }
    }
}

[MessagePortal(TkMsgType.NM_HC_SetShell)]
public class PK_HC_SetShell : BaseRequest
{
    public bool nSuccessFlag;
    public PK_HC_SetShell() => MsgType = TkMsgType.NM_HC_SetShell;
    public override IRequest ReadByte(ByteArray byteArray) { nSuccessFlag = byteArray.ReadBool(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteBool(nSuccessFlag);
}

[MessagePortal(TkMsgType.NM_CH_TANKREPAIR)]
public class PK_CH_TANKREPAIR : BaseRequest
{
    // The service (СЕРВИС) screen sends the FULL desired loadout here, not just the
    // tank guid: which shells and how many to buy/keep in each of the 3 slots, the
    // three consumable/provision ids, the auto-repair/auto-refill checkbox bitmask,
    // and the currency (silver=1 / gold=2) chosen per shell and per consumable.
    // Layout must mirror the client's PK_CH_TankRepair.WriteByte exactly.
    public long TankGuid;
    public ShellBuyStruct[] Shell = new ShellBuyStruct[3];
    public int[] Provision = new int[3];
    public byte AutoRepair;
    public byte[] ShellMoneyType = new byte[3];
    public byte[] ConsumableMoneyType = new byte[3];

    public PK_CH_TANKREPAIR() => MsgType = TkMsgType.NM_CH_TANKREPAIR;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankGuid = byteArray.ReadLong();
        for (int i = 0; i < 3; i++)
        {
            Shell[i].ShellID = byteArray.ReadInt();
            Shell[i].Count   = byteArray.ReadInt();
        }
        for (int i = 0; i < 3; i++) Provision[i] = byteArray.ReadInt();
        AutoRepair = byteArray.ReadByte();
        for (int i = 0; i < 3; i++) ShellMoneyType[i]      = byteArray.ReadByte();
        for (int i = 0; i < 3; i++) ConsumableMoneyType[i] = byteArray.ReadByte();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(TankGuid);
        for (int i = 0; i < 3; i++) { byteArray.WriteInt(Shell[i].ShellID); byteArray.WriteInt(Shell[i].Count); }
        for (int i = 0; i < 3; i++) byteArray.WriteInt(Provision[i]);
        byteArray.WriteByte(AutoRepair);
        for (int i = 0; i < 3; i++) byteArray.WriteByte(ShellMoneyType[i]);
        for (int i = 0; i < 3; i++) byteArray.WriteByte(ConsumableMoneyType[i]);
    }
}

[MessagePortal(TkMsgType.NM_HC_TANKREPAIR)]
public class PK_HC_TANKREPAIR : BaseRequest
{
    public long TankGuid; public byte RepairResult;
    public PK_HC_TANKREPAIR() => MsgType = TkMsgType.NM_HC_TANKREPAIR;
    public override IRequest ReadByte(ByteArray byteArray) { RepairResult = byteArray.ReadByte(); TankGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(RepairResult); byteArray.WriteLong(TankGuid); }
}

// Sent AFTER a successful NM_CH_TANKREPAIR / NM_CH_BuyShell so the client can (1) sync
// the tank's new shells / provisions / HP locally (Player.OnSyncTankInfo) and (2) actually
// CLOSE the service screen when the player pressed OK — RepairFaceBase.StoreConfigInfo only
// runs, and thus ShutFace() only fires, when this packet arrives. Without it the OK button
// looks dead (the request is sent, the server replies success, but the panel never closes).
[MessagePortal(TkMsgType.NM_HC_CHANGE_SHELLCONSUMABLE)]
public class PK_HC_ChangeShellComsumable : BaseRequest
{
    public long TankGuid;
    public ShellBuyStruct[] Shell = new ShellBuyStruct[3];         // active magazine
    public int[] Provision = new int[3];                           // active consumables
    public byte AutoRepair;
    public int HP;                                                 // current durability after service
    public ShellBuyStruct[] ShellProject = new ShellBuyStruct[3];  // saved loadout ("проект")
    public int[] ProvisionProjects = new int[3];
    public byte AutoFillCostFlag;

    public PK_HC_ChangeShellComsumable() => MsgType = TkMsgType.NM_HC_CHANGE_SHELLCONSUMABLE;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(TankGuid);
        for (int i = 0; i < 3; i++) { byteArray.WriteInt(Shell[i].ShellID); byteArray.WriteInt(Shell[i].Count); }
        for (int i = 0; i < 3; i++) byteArray.WriteInt(Provision[i]);
        byteArray.WriteByte(AutoRepair);
        byteArray.WriteInt(HP);
        for (int i = 0; i < 3; i++) { byteArray.WriteInt(ShellProject[i].ShellID); byteArray.WriteInt(ShellProject[i].Count); }
        for (int i = 0; i < 3; i++) byteArray.WriteInt(ProvisionProjects[i]);
        byteArray.WriteByte(AutoFillCostFlag);
    }

    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankGuid = byteArray.ReadLong();
        for (int i = 0; i < 3; i++) { Shell[i].ShellID = byteArray.ReadInt(); Shell[i].Count = byteArray.ReadInt(); }
        for (int i = 0; i < 3; i++) Provision[i] = byteArray.ReadInt();
        AutoRepair = byteArray.ReadByte();
        HP = byteArray.ReadInt();
        for (int i = 0; i < 3; i++) { ShellProject[i].ShellID = byteArray.ReadInt(); ShellProject[i].Count = byteArray.ReadInt(); }
        for (int i = 0; i < 3; i++) ProvisionProjects[i] = byteArray.ReadInt();
        AutoFillCostFlag = byteArray.ReadByte();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_CH_TransformTank)]
public class PK_CH_TransformTank : BaseRequest
{
    // v47: формат сверен с КЛИЕНТОМ (PK_CH_TransformTank): int ModificationId + byte
    // CurrencyType. Раньше сервер читал long TankGuid + byte TransformType — не совпадало
    // с тем, что шлёт клиент (5 байт против 9), покупка модификации не проходила по-настоящему.
    // TankGuid клиент не шлёт: целевой танк выводится из tank_id модификации.
    public int ModificationId; public byte CurrencyType;
    public PK_CH_TransformTank() => MsgType = TkMsgType.NM_CH_TransformTank;
    public override IRequest ReadByte(ByteArray byteArray) { ModificationId = byteArray.ReadInt(); CurrencyType = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(ModificationId); byteArray.WriteByte(CurrencyType); }
}

[MessagePortal(TkMsgType.NM_HC_TransformTank)]
public class PK_HC_TransformTank : BaseRequest
{
    // v47: формат ответа тоже по клиенту (PK_HC_TransformTank): byte SucFlag + int ModificationId.
    public byte SucFlag; public int ModificationId;
    public PK_HC_TransformTank() => MsgType = TkMsgType.NM_HC_TransformTank;
    public override IRequest ReadByte(ByteArray byteArray) { SucFlag = byteArray.ReadByte(); ModificationId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(SucFlag); byteArray.WriteInt(ModificationId); }
}

// v48.2: статус ЭЛИТ танков. Клиент (OnGetTankDevpState) включает элитный статус машине,
// когда nState==1, а «Модифицировать» разрешено только элитным. Тест-аккаунт имеет всё
// изучено → шлём nState=1 для всех машин игрока, иначе кнопка модификации заблокирована.
// nTankID = VehicleId (тип танка). TankCount пишется как short (клиент читает ReadShort).
public class tankDevpState
{
    public int nTankID;
    public byte nState;
}

[MessagePortal(TkMsgType.NM_HC_TankDevpState)]
public class PK_HC_TankDevpState : BaseRequest
{
    public tankDevpState[] TankStates = System.Array.Empty<tankDevpState>();
    public PK_HC_TankDevpState() => MsgType = TkMsgType.NM_HC_TankDevpState;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        int count = byteArray.ReadShort();
        TankStates = new tankDevpState[count];
        for (int i = 0; i < count; i++)
            TankStates[i] = new tankDevpState { nTankID = byteArray.ReadInt(), nState = byteArray.ReadByte() };
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort((short)TankStates.Length);
        foreach (var s in TankStates)
        {
            byteArray.WriteInt(s.nTankID);
            byteArray.WriteByte(s.nState);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_BUYGOLDTANK)]
public class PK_CH_BUYGOLDTANK : BaseRequest
{
    public int TankId;
    public byte TimeLimitType;
    public byte IsBuyCarport;
    public PK_CH_BUYGOLDTANK() => MsgType = TkMsgType.NM_CH_BUYGOLDTANK;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); TimeLimitType = byteArray.ReadByte(); IsBuyCarport = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(TankId); byteArray.WriteByte(TimeLimitType); byteArray.WriteByte(IsBuyCarport); }
}

[MessagePortal(TkMsgType.NM_HC_BUYGOLDTANK)]
public class PK_HC_BUYGOLDTANK : BaseRequest
{
    public int TankId;
    public byte TimeLimitType;
    public byte IsBuyCarport;
    public byte BuyResult;
    public PK_HC_BUYGOLDTANK() => MsgType = TkMsgType.NM_HC_BUYGOLDTANK;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); TimeLimitType = byteArray.ReadByte(); IsBuyCarport = byteArray.ReadByte(); BuyResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(TankId); byteArray.WriteByte(TimeLimitType); byteArray.WriteByte(IsBuyCarport); byteArray.WriteByte(BuyResult); }
}

[MessagePortal(TkMsgType.NM_CH_BUYCARPORT)]
public class PK_CH_BUYCARPORT : BaseRequest
{
    public byte BuyCar;
    public PK_CH_BUYCARPORT() => MsgType = TkMsgType.NM_CH_BUYCARPORT;
    public override IRequest ReadByte(ByteArray byteArray) { BuyCar = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(BuyCar);
}

[MessagePortal(TkMsgType.NM_HC_BUYCARPORT)]
public class PK_HC_BUYCARPORT : BaseRequest
{
    public byte FlagSucceed;
    public byte CarNum;
    public PK_HC_BUYCARPORT() => MsgType = TkMsgType.NM_HC_BUYCARPORT;
    public override IRequest ReadByte(ByteArray byteArray) { FlagSucceed = byteArray.ReadByte(); CarNum = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(FlagSucceed); byteArray.WriteByte(CarNum); }
}

[MessagePortal(TkMsgType.NM_CH_GOLDEXCHANGE_MONEY)]
public class PK_CH_GOLDEXCHANGE_MONEY : BaseRequest
{
    public int GoldAmount;
    public PK_CH_GOLDEXCHANGE_MONEY() => MsgType = TkMsgType.NM_CH_GOLDEXCHANGE_MONEY;
    public override IRequest ReadByte(ByteArray byteArray) { GoldAmount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(GoldAmount);
}

[MessagePortal(TkMsgType.NM_CH_GOLDEXCHANGE_EXP)]
public class PK_CH_GOLDEXCHANGE_EXP : BaseRequest
{
    public int GoldAmount;
    public PK_CH_GOLDEXCHANGE_EXP() => MsgType = TkMsgType.NM_CH_GOLDEXCHANGE_EXP;
    public override IRequest ReadByte(ByteArray byteArray) { GoldAmount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(GoldAmount);
}

[MessagePortal(TkMsgType.NM_CH_VipOperate)]
public class PK_CH_VipOperate : BaseRequest
{
    // ВАЖНО: клиент (PK_CH_VipOperate в decfilesgame/ClientSocket) шлёт ОДИН байт VipKind
    // (см. UIVipDialog.OnClickBuy -> GameManager.Hall.SendVipOperate((EVipKind)(i+1))).
    // Старая версия читала byte+int (5 байт) — ByteArray.ReadInt() уходил за границы
    // payload (который был длиной 1 байт), бросал IndexOutOfRangeException,
    // сессия ловила это в catch{} и рвала соединение -> у игрока выглядело как краш при покупке VIP.
    public byte VipKind;
    public PK_CH_VipOperate() => MsgType = TkMsgType.NM_CH_VipOperate;
    public override IRequest ReadByte(ByteArray byteArray) { VipKind = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(VipKind); }
}

[MessagePortal(TkMsgType.NM_CH_GiftCardUse)]
public class PK_CH_GiftCardUse : BaseRequest
{
    public string CardCode = string.Empty;
    public PK_CH_GiftCardUse() => MsgType = TkMsgType.NM_CH_GiftCardUse;
    public override IRequest ReadByte(ByteArray byteArray) { CardCode = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(CardCode, 64);
}

[MessagePortal(TkMsgType.NM_HC_GiftCardUse)]
public class PK_HC_GiftCardUse : BaseRequest
{
    public byte UseResult;
    public PK_HC_GiftCardUse() => MsgType = TkMsgType.NM_HC_GiftCardUse;
    public override IRequest ReadByte(ByteArray byteArray) { UseResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(UseResult);
}

[MessagePortal(TkMsgType.NM_CH_ModuleOperate)]
public class PK_CH_ModuleOperate : BaseRequest
{
    public long TankGUID;
    public byte ModuleType;
    public ushort ModuleID;
    public byte ResultFlag;
    public PK_CH_ModuleOperate() => MsgType = TkMsgType.NM_CH_ModuleOperate;
    public override IRequest ReadByte(ByteArray byteArray)
    { TankGUID = byteArray.ReadLong(); ModuleType = byteArray.ReadByte(); ModuleID = byteArray.ReadUShort(); ResultFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(TankGUID); byteArray.WriteByte(ModuleType); byteArray.WriteUShort(ModuleID); byteArray.WriteByte(ResultFlag); }
}

[MessagePortal(TkMsgType.NM_CH_EndShoot)]
// v.fix: тот же класс бага, что и у PK_CH_BeginLott — раньше без полей, поэтому
// сервер не знал, какой именно тир закрывают (client шлёт { byte nLevl }, см.
// decfilesgame Hall.SendTreasureEndShoot), и HandleEndShoot всегда отвечал
// хардкодом nlevl=1 (Тир 2) независимо от того, что реально закрыл игрок.
public class PK_CH_EndShoot : BaseRequest
{
    public byte nLevl;
    public PK_CH_EndShoot() => MsgType = TkMsgType.NM_CH_EndShoot;
    public override IRequest ReadByte(ByteArray byteArray) { nLevl = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(nLevl);
}

[MessagePortal(TkMsgType.NM_HC_EndShoot)]
public class PK_HC_EndShoot : BaseRequest
{
    public byte nlevl;
    public PK_HC_EndShoot() => MsgType = TkMsgType.NM_HC_EndShoot;
    public override IRequest ReadByte(ByteArray byteArray) { nlevl = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(nlevl);
}

// v.fix (Тир): сервер раньше НИКОГДА не слал это сообщение (класс отсутствовал совсем),
// из-за чего клиент (NewTreasureBoxData.RemainShellShootNum) всегда видел 0 оставшихся
// выстрелов, экран Тира вечно показывал "Недостаточно выстрелов", а покупка (NM_CH_BuyBox)
// не давала видимого эффекта. Раскладка байт — 1-в-1 из decfilesgame (PK_HC_RemainShootTimes):
// int nCount, затем nCount × { byte nLvl; int nLeftTimes }.
public class RemainShootTimesItem { public byte Level; public int LeftTimes; }

[MessagePortal(TkMsgType.NM_HC_RemainShootTimes)]
public class PK_HC_RemainShootTimes : BaseRequest
{
    public List<RemainShootTimesItem> Items = new();
    public PK_HC_RemainShootTimes() => MsgType = TkMsgType.NM_HC_RemainShootTimes;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        int count = byteArray.ReadInt();
        Items = new List<RemainShootTimesItem>(count);
        for (int i = 0; i < count; i++)
            Items.Add(new RemainShootTimesItem { Level = byteArray.ReadByte(), LeftTimes = byteArray.ReadInt() });
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Items.Count);
        foreach (var item in Items) { byteArray.WriteByte(item.Level); byteArray.WriteInt(item.LeftTimes); }
    }
}

[MessagePortal(TkMsgType.NM_CH_ShootPrize)]
public class PK_CH_ShootPrize : BaseRequest
{
    // Клиент (Hall.SendTreasureShootPrize) шлёт { byte nLevl; byte nIndex } — 2 байта.
    // Раньше сервер читал только { byte ShootType } (1 байт), из-за чего nIndex
    // оставался непрочитанным и сдвигал разбор следующего сообщения в очереди.
    public byte nLevl;
    public byte nIndex;
    public PK_CH_ShootPrize() => MsgType = TkMsgType.NM_CH_ShootPrize;
    public override IRequest ReadByte(ByteArray byteArray) { nLevl = byteArray.ReadByte(); nIndex = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(nLevl); byteArray.WriteByte(nIndex); }
}

[MessagePortal(TkMsgType.NM_HC_ShootPrize)]
public class PK_HC_ShootPrize : BaseRequest
{
    // ВАЖНО: клиент (NewTreasureBoxDownPanel.OnShootItemPrizeRet) читает
    // { byte nLevl; byte nResult; int nID; byte nIndex; string sTime(20) } и требует
    // nResult == 1, чтобы засчитать приз в открытой ячейке nIndex. Раньше сервер
    // слал только { byte ShootType; int PrizeId } (5 байт вместо нужных 26+),
    // поэтому клик по ячейке ничего не показывал и разбор следующего пакета
    // сдвигался на неверное число байт.
    public byte nLevl;
    public byte nResult;
    public int nID;
    public byte nIndex;
    public string sTime = "";
    public PK_HC_ShootPrize() => MsgType = TkMsgType.NM_HC_ShootPrize;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        nLevl = byteArray.ReadByte();
        nResult = byteArray.ReadByte();
        nID = byteArray.ReadInt();
        nIndex = byteArray.ReadByte();
        sTime = byteArray.ReadUTF(20);
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(nLevl);
        byteArray.WriteByte(nResult);
        byteArray.WriteInt(nID);
        byteArray.WriteByte(nIndex);
        byteArray.WriteUTF(sTime, 20);
    }
}

[MessagePortal(TkMsgType.NM_CH_ChangeBattleTankList)]
public class PK_CH_ChangeBattleTankList : BaseRequest
{
    public byte Index;
    public long[] TankGuids = new long[5];
    public PK_CH_ChangeBattleTankList() => MsgType = TkMsgType.NM_CH_ChangeBattleTankList;
    public override IRequest ReadByte(ByteArray byteArray) { Index = byteArray.ReadByte(); for (int i = 0; i < 5; i++) TankGuids[i] = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Index); for (int i = 0; i < 5; i++) byteArray.WriteLong(TankGuids[i]); }
}

[MessagePortal(TkMsgType.NM_HC_ChangeBattleTankList)]
public class PK_HC_ChangeBattleTankList : BaseRequest
{
    public bool Flag;
    public PK_HC_ChangeBattleTankList() => MsgType = TkMsgType.NM_HC_ChangeBattleTankList;
    public override IRequest ReadByte(ByteArray byteArray) { Flag = byteArray.ReadBool(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteBool(Flag);
}

[MessagePortal(TkMsgType.NM_CH_Change_Tank)]
public class PK_CH_Change_Tank : BaseRequest
{
    public long TankGuid;
    public PK_CH_Change_Tank() => MsgType = TkMsgType.NM_CH_Change_Tank;
    public override IRequest ReadByte(ByteArray byteArray) { TankGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(TankGuid);
}

[MessagePortal(TkMsgType.NM_CH_Set_Garage_Background)]
public class PK_CH_Set_Garage_Background : BaseRequest
{
    public int BackgroundId;
    public PK_CH_Set_Garage_Background() => MsgType = TkMsgType.NM_CH_Set_Garage_Background;
    public override IRequest ReadByte(ByteArray byteArray) { BackgroundId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BackgroundId);
}

[MessagePortal(TkMsgType.NM_HC_Set_Garage_Background)]
public class PK_HC_Set_Garage_Background : BaseRequest
{
    // ВАЖНО: клиент (AdvancedAccountManager.OnSetGarageBackgroundResult) читает этот
    // пакет как { byte Result; int TargetIndex; } — TargetIndex это РЕАЛЬНЫЙ id фона
    // (10001..10005), а не 0/1 признак успеха. Раньше сервер писал один int
    // (BackgroundId) без байта результата — клиент читал Result из первого байта
    // BackgroundId (почти всегда != 1) и TargetIndex мусором из следующего пакета
    // в потоке, поэтому фон визуально не менялся, а следующий пакет в очереди
    // десинхронизировался.
    public byte Result;
    public int TargetIndex;
    public PK_HC_Set_Garage_Background() => MsgType = TkMsgType.NM_HC_Set_Garage_Background;
    public override IRequest ReadByte(ByteArray byteArray) { Result = byteArray.ReadByte(); TargetIndex = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Result); byteArray.WriteInt(TargetIndex); }
}

[MessagePortal(TkMsgType.NM_CH_Team_Ready)]
public class PK_CH_Team_Ready : BaseRequest
{
    public byte ReadyState;
    public PK_CH_Team_Ready() => MsgType = TkMsgType.NM_CH_Team_Ready;
    public override IRequest ReadByte(ByteArray byteArray) { ReadyState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(ReadyState);
}

[MessagePortal(TkMsgType.NM_CH_Invite_Team)]
public class PK_CH_Invite_Team : BaseRequest
{
    public string PlayerName = string.Empty;
    public PK_CH_Invite_Team() => MsgType = TkMsgType.NM_CH_Invite_Team;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerName = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(PlayerName, 64);
}

[MessagePortal(TkMsgType.NM_HC_Be_Invite_Team)]
public class PK_HC_Be_Invite_Team : BaseRequest
{
    public string InviterName = string.Empty; public byte BattleMode;
    public PK_HC_Be_Invite_Team() => MsgType = TkMsgType.NM_HC_Be_Invite_Team;
    public override IRequest ReadByte(ByteArray byteArray) { InviterName = byteArray.ReadUTF(64); BattleMode = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteUTF(InviterName, 64); byteArray.WriteByte(BattleMode); }
}

[MessagePortal(TkMsgType.NM_CH_Join_Team)]
public class PK_CH_Join_Team : BaseRequest
{
    public long TeamGuid;
    public PK_CH_Join_Team() => MsgType = TkMsgType.NM_CH_Join_Team;
    public override IRequest ReadByte(ByteArray byteArray) { TeamGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(TeamGuid);
}

[MessagePortal(TkMsgType.NM_CH_Leave_Team)]
public class PK_CH_Leave_Team : BaseRequest { public PK_CH_Leave_Team() => MsgType = TkMsgType.NM_CH_Leave_Team; }

[MessagePortal(TkMsgType.NM_HC_Leave_Team)]
public class PK_HC_Leave_Team : BaseRequest
{
    public long PlayerGuid;
    public PK_HC_Leave_Team() => MsgType = TkMsgType.NM_HC_Leave_Team;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(PlayerGuid);
}

[MessagePortal(TkMsgType.NM_CH_Kick_Team)]
public class PK_CH_Kick_Team : BaseRequest
{
    public long PlayerGuid;
    public PK_CH_Kick_Team() => MsgType = TkMsgType.NM_CH_Kick_Team;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(PlayerGuid);
}

[MessagePortal(TkMsgType.NM_CH_Create_Team)]
public class PK_CH_Create_Team : BaseRequest
{
    public byte BattleMode; public byte BattleLevel;
    public PK_CH_Create_Team() => MsgType = TkMsgType.NM_CH_Create_Team;
    public override IRequest ReadByte(ByteArray byteArray) { BattleMode = byteArray.ReadByte(); BattleLevel = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(BattleMode); byteArray.WriteByte(BattleLevel); }
}

[MessagePortal(TkMsgType.NM_CH_Team_Change_Mode)]
public class PK_CH_Team_Change_Mode : BaseRequest
{
    public byte BattleMode;
    public PK_CH_Team_Change_Mode() => MsgType = TkMsgType.NM_CH_Team_Change_Mode;
    public override IRequest ReadByte(ByteArray byteArray) { BattleMode = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(BattleMode);
}

[MessagePortal(TkMsgType.NM_HC_Team_Info)]
public class PK_HC_Team_Info : BaseRequest
{
    public long TeamGuid; public byte BattleMode; public byte MemberCount; public byte MaxMemberCount;
    public PK_HC_Team_Info() => MsgType = TkMsgType.NM_HC_Team_Info;
    public override IRequest ReadByte(ByteArray byteArray)
    { TeamGuid = byteArray.ReadLong(); BattleMode = byteArray.ReadByte(); MemberCount = byteArray.ReadByte(); MaxMemberCount = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(TeamGuid); byteArray.WriteByte(BattleMode); byteArray.WriteByte(MemberCount); byteArray.WriteByte(MaxMemberCount); }
}

[MessagePortal(TkMsgType.NM_CH_Quick_Match)]
public class PK_CH_Quick_Match : BaseRequest
{
    public byte BattleMode; public byte BattleLevel;
    public PK_CH_Quick_Match() => MsgType = TkMsgType.NM_CH_Quick_Match;
    public override IRequest ReadByte(ByteArray byteArray) { BattleMode = byteArray.ReadByte(); BattleLevel = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(BattleMode); byteArray.WriteByte(BattleLevel); }
}

[MessagePortal(TkMsgType.NM_CH_Cancel_Quick_Match)]
public class PK_CH_Cancel_Quick_Match : BaseRequest { public PK_CH_Cancel_Quick_Match() => MsgType = TkMsgType.NM_CH_Cancel_Quick_Match; }

[MessagePortal(TkMsgType.NM_HC_Quick_Match_Fail)]
public class PK_HC_Quick_Match_Fail : BaseRequest { public PK_HC_Quick_Match_Fail() => MsgType = TkMsgType.NM_HC_Quick_Match_Fail; }

[MessagePortal(TkMsgType.NM_CH_Start_Match)]
public class PK_CH_Start_Match : BaseRequest { public PK_CH_Start_Match() => MsgType = TkMsgType.NM_CH_Start_Match; }

[MessagePortal(TkMsgType.NM_HC_Start_End_Match)]
public class PK_HC_Start_End_Match : BaseRequest
{
    public byte State;
    public PK_HC_Start_End_Match() => MsgType = TkMsgType.NM_HC_Start_End_Match;
    public override IRequest ReadByte(ByteArray byteArray) { State = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(State);
}

[MessagePortal(TkMsgType.NM_CH_Set_Free_Team)]
public class PK_CH_Set_Free_Team : BaseRequest
{
    public byte FreeTeamState;
    public PK_CH_Set_Free_Team() => MsgType = TkMsgType.NM_CH_Set_Free_Team;
    public override IRequest ReadByte(ByteArray byteArray) { FreeTeamState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(FreeTeamState);
}

[MessagePortal(TkMsgType.NM_HC_Free_Team)]
public class PK_HC_Free_Team : BaseRequest
{
    public byte FreeTeamState;
    public PK_HC_Free_Team() => MsgType = TkMsgType.NM_HC_Free_Team;
    public override IRequest ReadByte(ByteArray byteArray) { FreeTeamState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(FreeTeamState);
}

[MessagePortal(TkMsgType.NM_CH_Be_Invite_Team_Ret)]
public class PK_CH_Be_Invite_Team_Ret : BaseRequest
{
    public byte AcceptState;
    public PK_CH_Be_Invite_Team_Ret() => MsgType = TkMsgType.NM_CH_Be_Invite_Team_Ret;
    public override IRequest ReadByte(ByteArray byteArray) { AcceptState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(AcceptState);
}

[MessagePortal(TkMsgType.NM_HC_Confirm_Ready)]
public class PK_HC_Confirm_Ready : BaseRequest
{
    public long PlayerGuid; public byte ReadyState;
    public PK_HC_Confirm_Ready() => MsgType = TkMsgType.NM_HC_Confirm_Ready;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerGuid = byteArray.ReadLong(); ReadyState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(PlayerGuid); byteArray.WriteByte(ReadyState); }
}

[MessagePortal(TkMsgType.NM_HC_Cancel_Ready)]
public class PK_HC_Cancel_Ready : BaseRequest
{
    public long PlayerGuid;
    public PK_HC_Cancel_Ready() => MsgType = TkMsgType.NM_HC_Cancel_Ready;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(PlayerGuid);
}

[MessagePortal(TkMsgType.NM_CH_RespReconnect)]
public class PK_CH_RespReconnect : BaseRequest
{
    public byte RespState;
    public PK_CH_RespReconnect() => MsgType = TkMsgType.NM_CH_RespReconnect;
    public override IRequest ReadByte(ByteArray byteArray) { RespState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(RespState);
}

[MessagePortal(TkMsgType.NM_HC_RespReconnectResult)]
public class PK_HC_RespReconnectResult : BaseRequest
{
    public byte Result;
    public PK_HC_RespReconnectResult() => MsgType = TkMsgType.NM_HC_RespReconnectResult;
    public override IRequest ReadByte(ByteArray byteArray) { Result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Result);
}

[MessagePortal(TkMsgType.NM_CH_CancleJoinRoom)]
public class PK_CH_CancleJoinRoom : BaseRequest { public PK_CH_CancleJoinRoom() => MsgType = TkMsgType.NM_CH_CancleJoinRoom; }

[MessagePortal(TkMsgType.NM_HC_CancleJoinRoom)]
public class PK_HC_CancleJoinRoom : BaseRequest { public PK_HC_CancleJoinRoom() => MsgType = TkMsgType.NM_HC_CancleJoinRoom; }

// Client sends flag byte: 0 = free/silver spin, 1 = gold spin
[MessagePortal(TkMsgType.NM_CH_LotteryDraw)]
public class PK_CH_LotteryDraw : BaseRequest
{
    public byte Flag;
    public PK_CH_LotteryDraw() => MsgType = TkMsgType.NM_CH_LotteryDraw;
    public override IRequest ReadByte(ByteArray byteArray) { Flag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(Flag); }
}

// Client reads only RewardID (int) — no PrizeType field
[MessagePortal(TkMsgType.NM_HC_LotteryDraw)]
public class PK_HC_LotteryDraw : BaseRequest
{
    public int PrizeId; // RewardID that matches client prize table (1-8 for wheel positions)
    public PK_HC_LotteryDraw() => MsgType = TkMsgType.NM_HC_LotteryDraw;
    public override IRequest ReadByte(ByteArray byteArray) { PrizeId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(PrizeId); }
}

[MessagePortal(TkMsgType.NM_CH_10_LotteryDraw)]
public class PK_CH_10_LotteryDraw : BaseRequest { public PK_CH_10_LotteryDraw() => MsgType = TkMsgType.NM_CH_10_LotteryDraw; }

// Client reads: Count (int), then Count × RewardID (int)
[MessagePortal(TkMsgType.NM_HC_10_LotteryDraw)]
public class PK_HC_10_LotteryDraw : BaseRequest
{
    public int[] PrizeIds = new int[10];
    public PK_HC_10_LotteryDraw() => MsgType = TkMsgType.NM_HC_10_LotteryDraw;
    public override IRequest ReadByte(ByteArray byteArray)
    { int count = byteArray.ReadInt(); PrizeIds = new int[count]; for (int i = 0; i < count; i++) PrizeIds[i] = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteInt(PrizeIds.Length); foreach (var id in PrizeIds) byteArray.WriteInt(id); }
}

[MessagePortal(TkMsgType.NM_CH_BuyDiscountItem)]
public class PK_CH_BuyDiscountItem : BaseRequest
{
    public int ItemId; public int ItemNum;
    public PK_CH_BuyDiscountItem() => MsgType = TkMsgType.NM_CH_BuyDiscountItem;
    public override IRequest ReadByte(ByteArray byteArray) { ItemId = byteArray.ReadInt(); ItemNum = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(ItemId); byteArray.WriteInt(ItemNum); }
}

[MessagePortal(TkMsgType.NM_HC_BuyDiscountItem)]
public class PK_HC_BuyDiscountItem : BaseRequest
{
    public byte BuyResult;
    public PK_HC_BuyDiscountItem() => MsgType = TkMsgType.NM_HC_BuyDiscountItem;
    public override IRequest ReadByte(ByteArray byteArray) { BuyResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(BuyResult);
}

[MessagePortal(TkMsgType.NM_CH_HotGoods)]
public class PK_CH_HotGoods : BaseRequest { public PK_CH_HotGoods() => MsgType = TkMsgType.NM_CH_HotGoods; }

[MessagePortal(TkMsgType.NM_CH_Discount_Item_Remain)]
public class PK_CH_Discount_Item_Remain : BaseRequest { public PK_CH_Discount_Item_Remain() => MsgType = TkMsgType.NM_CH_Discount_Item_Remain; }

[MessagePortal(TkMsgType.NM_HC_Discount_Item_Remain)]
public class PK_HC_Discount_Item_Remain : BaseRequest
{
    public int RemainCount;
    public PK_HC_Discount_Item_Remain() => MsgType = TkMsgType.NM_HC_Discount_Item_Remain;
    public override IRequest ReadByte(ByteArray byteArray) { RemainCount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(RemainCount);
}

[MessagePortal(TkMsgType.NM_CH_LimitBuyActivityTime)]
public class PK_CH_LimitBuyActivityTime : BaseRequest { public PK_CH_LimitBuyActivityTime() => MsgType = TkMsgType.NM_CH_LimitBuyActivityTime; }

[MessagePortal(TkMsgType.NM_HC_LimitBuyActivityTime)]
public class PK_HC_LimitBuyActivityTime : BaseRequest
{
    public long ServerStartTime;
    public long ServerEndTime;
    public long PersonStartTime;
    public long PersonEndTime;
    public PK_HC_LimitBuyActivityTime() => MsgType = TkMsgType.NM_HC_LimitBuyActivityTime;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        ServerStartTime = byteArray.ReadLong();
        ServerEndTime = byteArray.ReadLong();
        PersonStartTime = byteArray.ReadLong();
        PersonEndTime = byteArray.ReadLong();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteLong(ServerStartTime);
        byteArray.WriteLong(ServerEndTime);
        byteArray.WriteLong(PersonStartTime);
        byteArray.WriteLong(PersonEndTime);
    }
}

[MessagePortal(TkMsgType.NM_CH_GetSingleRechargeReward)]
public class PK_CH_GetSingleRechargeReward : BaseRequest
{
    public int RewardId;
    public PK_CH_GetSingleRechargeReward() => MsgType = TkMsgType.NM_CH_GetSingleRechargeReward;
    public override IRequest ReadByte(ByteArray byteArray) { RewardId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(RewardId);
}

[MessagePortal(TkMsgType.NM_CH_GetAccumulateRechargeReward)]
public class PK_CH_GetAccumulateRechargeReward : BaseRequest
{
    public int RewardId;
    public PK_CH_GetAccumulateRechargeReward() => MsgType = TkMsgType.NM_CH_GetAccumulateRechargeReward;
    public override IRequest ReadByte(ByteArray byteArray) { RewardId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(RewardId);
}

[MessagePortal(TkMsgType.NM_CH_GetContinuousLoginReward)]
public class PK_CH_GetContinuousLoginReward : BaseRequest
{
    public int DayIndex;
    public PK_CH_GetContinuousLoginReward() => MsgType = TkMsgType.NM_CH_GetContinuousLoginReward;
    public override IRequest ReadByte(ByteArray byteArray) { DayIndex = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(DayIndex);
}

[MessagePortal(TkMsgType.NM_CH_ContinuousLoginRewardID)]
public class PK_CH_ContinuousLoginRewardID : BaseRequest { public PK_CH_ContinuousLoginRewardID() => MsgType = TkMsgType.NM_CH_ContinuousLoginRewardID; }

[MessagePortal(TkMsgType.NM_HC_NewRecruitActivityInfo)]
public class PK_HC_NewRecruitActivityInfo : BaseRequest
{
    public byte IsPushToday;
    public byte FirstChargeFlag;
    public byte VIPFlag;
    public byte GoldTankFlag;
    public PK_HC_NewRecruitActivityInfo() => MsgType = TkMsgType.NM_HC_NewRecruitActivityInfo;
    public override IRequest ReadByte(ByteArray byteArray) { IsPushToday = byteArray.ReadByte(); FirstChargeFlag = byteArray.ReadByte(); VIPFlag = byteArray.ReadByte(); GoldTankFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(IsPushToday); byteArray.WriteByte(FirstChargeFlag); byteArray.WriteByte(VIPFlag); byteArray.WriteByte(GoldTankFlag); }
}

[MessagePortal(TkMsgType.NM_HC_ContinuousLoginRewardID)]
public class PK_HC_ContinuousLoginRewardID : BaseRequest
{
    public int[] RewardIds = new int[7];
    public PK_HC_ContinuousLoginRewardID() => MsgType = TkMsgType.NM_HC_ContinuousLoginRewardID;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        for (int i = 0; i < RewardIds.Length; i++)
        {
            RewardIds[i] = byteArray.ReadInt();
        }
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        for (int i = 0; i < RewardIds.Length; i++)
        {
            byteArray.WriteInt(RewardIds[i]);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_TournamentGetAllRange)]
public class PK_CH_TournamentGetAllRange : BaseRequest { public PK_CH_TournamentGetAllRange() => MsgType = TkMsgType.NM_CH_TournamentGetAllRange; }

[MessagePortal(TkMsgType.NM_CH_TournamentGetReward)]
public class PK_CH_TournamentGetReward : BaseRequest { public PK_CH_TournamentGetReward() => MsgType = TkMsgType.NM_CH_TournamentGetReward; }

[MessagePortal(TkMsgType.NM_HC_TournamentGetRewardResult)]
public class PK_HC_TournamentGetRewardResult : BaseRequest
{
    public byte Result;
    public PK_HC_TournamentGetRewardResult() => MsgType = TkMsgType.NM_HC_TournamentGetRewardResult;
    public override IRequest ReadByte(ByteArray byteArray) { Result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Result);
}

// v.fix (Тир): раньше был пустышкой без полей — сервер физически не мог узнать,
// какой уровень тира нажали "СТАРТ" (client шлёт { byte nLevl }, см.
// decfilesgame/Hall.cs SendTreasureBeginLott). Из-за этого HandleBeginLott отвечал
// ВСЕГДА одним и тем же хардкодом (nLevl=1 → Тир 2 в 0-based индексации клиента),
// какой бы тир игрок ни открыл.
[MessagePortal(TkMsgType.NM_CH_BeginLott)]
public class PK_CH_BeginLott : BaseRequest
{
    public byte nLevl;
    public PK_CH_BeginLott() => MsgType = TkMsgType.NM_CH_BeginLott;
    public override IRequest ReadByte(ByteArray byteArray) { nLevl = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(nLevl);
}

[MessagePortal(TkMsgType.NM_HC_BeginLott)]
public class PK_HC_BeginLott : BaseRequest
{
    public byte nLevl;
    public int[] nConfigID = new int[12];

    public PK_HC_BeginLott() => MsgType = TkMsgType.NM_HC_BeginLott;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        nLevl = byteArray.ReadByte();
        for (int i = 0; i < 12; i++)
            nConfigID[i] = byteArray.ReadInt();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(nLevl);
        for (int i = 0; i < 12; i++)
            byteArray.WriteInt(nConfigID[i]);
    }
}

[MessagePortal(TkMsgType.NM_CH_AskGetPowerPrize)]
public class PK_CH_AskGetPowerPrize : BaseRequest { public PK_CH_AskGetPowerPrize() => MsgType = TkMsgType.NM_CH_AskGetPowerPrize; }

[MessagePortal(TkMsgType.NM_HC_AskGetPowerPrize)]
public class PK_HC_AskGetPowerPrize : BaseRequest
{
    // Формат сверен с клиентом (ClientSocket/PK_HC_AskGetPowerPrize):
    //     byte  resultFlag   — 0 отказ, 1 приз выдан
    //     float powerValue   — новое значение шкалы мощи (0..100)
    // v30: раньше сервер писал ТОЛЬКО байт — клиент дочитывал float из мусора.
    public byte HasPowerPrize;
    public float PowerValue;
    public PK_HC_AskGetPowerPrize() => MsgType = TkMsgType.NM_HC_AskGetPowerPrize;
    public override IRequest ReadByte(ByteArray byteArray)
    { HasPowerPrize = byteArray.ReadByte(); PowerValue = byteArray.ReadFloat(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteByte(HasPowerPrize); byteArray.WriteFloat(PowerValue); }
}

[MessagePortal(TkMsgType.NM_CH_GETPOWERPRIZE)]
public class PK_CH_GETPOWERPRIZE : BaseRequest
{
    // Клиент (Hall.SendGetPowerPrize) шлёт int PowerID (4 байта). Раньше сервер не
    // вычитывал это поле — буфер запроса не сдвигался на эти 4 байта, из-за чего
    // следующее сообщение в очереди читалось со сдвигом (десинхронизация потока).
    public int PowerID;
    public PK_CH_GETPOWERPRIZE() => MsgType = TkMsgType.NM_CH_GETPOWERPRIZE;
    public override IRequest ReadByte(ByteArray byteArray) { PowerID = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(PowerID);
}

[MessagePortal(TkMsgType.NM_HC_GETPOWERPRIZE)]
public class PK_HC_GETPOWERPRIZE : BaseRequest
{
    // ВАЖНО: клиент (TreasureBoxManager.GetPowerPrizeResult) читает этот пакет как
    // { int PowerIndex; int PowerValue; byte Result; } — 9 байт. Раньше сервер писал
    // только 1 байт (GetResult), поэтому клиент читал PowerIndex/PowerValue мусором
    // из начала следующего пакета в очереди, а Result почти никогда не оказывался
    // равным 1 — приз визуально не выдавался, а поток сообщений дальше
    // десинхронизировался (следующие пакеты читались со сдвигом).
    public int PowerIndex;
    public int PowerValue;
    public byte Result;
    public PK_HC_GETPOWERPRIZE() => MsgType = TkMsgType.NM_HC_GETPOWERPRIZE;
    public override IRequest ReadByte(ByteArray byteArray) { PowerIndex = byteArray.ReadInt(); PowerValue = byteArray.ReadInt(); Result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(PowerIndex); byteArray.WriteInt(PowerValue); byteArray.WriteByte(Result); }
}

[MessagePortal(TkMsgType.NM_CH_GIVEUP_PRIZE)]
public class PK_CH_GIVEUP_PRIZE : BaseRequest { public PK_CH_GIVEUP_PRIZE() => MsgType = TkMsgType.NM_CH_GIVEUP_PRIZE; }

[MessagePortal(TkMsgType.NM_HC_GIVEUP_PRIZE)]
public class PK_HC_GIVEUP_PRIZE : BaseRequest
{
    public byte GiveUpResult;
    public PK_HC_GIVEUP_PRIZE() => MsgType = TkMsgType.NM_HC_GIVEUP_PRIZE;
    public override IRequest ReadByte(ByteArray byteArray) { GiveUpResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(GiveUpResult);
}

[MessagePortal(TkMsgType.NM_CH_ReqDomainBidInfo)]
public class PK_CH_ReqDomainBidInfo : BaseRequest { public PK_CH_ReqDomainBidInfo() => MsgType = TkMsgType.NM_CH_ReqDomainBidInfo; }

[MessagePortal(TkMsgType.NM_HC_RespDomainBidInfo)]
public class PK_HC_RespDomainBidInfo : BaseRequest
{
    public int BidInfoCount;
    public PK_HC_RespDomainBidInfo() => MsgType = TkMsgType.NM_HC_RespDomainBidInfo;
    public override IRequest ReadByte(ByteArray byteArray) { BidInfoCount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BidInfoCount);
}

[MessagePortal(TkMsgType.NM_CH_Domain_FightList)]
public class PK_CH_Domain_FightList : BaseRequest { public PK_CH_Domain_FightList() => MsgType = TkMsgType.NM_CH_Domain_FightList; }

[MessagePortal(TkMsgType.NM_HC_Domain_FightList)]
public class PK_HC_Domain_FightList : BaseRequest
{
    public int FightCount;
    public PK_HC_Domain_FightList() => MsgType = TkMsgType.NM_HC_Domain_FightList;
    public override IRequest ReadByte(ByteArray byteArray) { FightCount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(FightCount);
}

[MessagePortal(TkMsgType.NM_CH_RenameRequest)]
public class PK_CH_RenameRequest : BaseRequest
{
    public string NewName = string.Empty;
    public PK_CH_RenameRequest() => MsgType = TkMsgType.NM_CH_RenameRequest;
    public override IRequest ReadByte(ByteArray byteArray) { NewName = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(NewName, 64);
}

[MessagePortal(TkMsgType.NM_HC_RenameReply)]
public class PK_HC_RenameReply : BaseRequest
{
    // v73: реальный формат клиента (см. decfilesgame Assembly-CSharp.cs, class
    // PK_HC_RenameReply) — Name (UTF, max 64) + LeftTime (byte) + ReturnFlag (byte).
    // Старая версия слала только один байт RenameResult — клиент читал 3 поля из
    // 1-байтового пакета, поэтому переименование никогда реально не применялось
    // (а сервер даже не читал NewName из запроса).
    public string Name = string.Empty;
    public byte LeftTime;
    public byte ReturnFlag;
    public PK_HC_RenameReply() => MsgType = TkMsgType.NM_HC_RenameReply;
    public override IRequest ReadByte(ByteArray byteArray)
    { Name = byteArray.ReadUTF(64); LeftTime = byteArray.ReadByte(); ReturnFlag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteUTF(Name, 64); byteArray.WriteByte(LeftTime); byteArray.WriteByte(ReturnFlag); }
}

[MessagePortal(TkMsgType.NM_CH_TestAutoGen)]
public class PK_CH_TestAutoGen : BaseRequest { public PK_CH_TestAutoGen() => MsgType = TkMsgType.NM_CH_TestAutoGen; }

[MessagePortal(TkMsgType.NM_HC_TestAutoGen)]
public class PK_HC_TestAutoGen : BaseRequest { public PK_HC_TestAutoGen() => MsgType = TkMsgType.NM_HC_TestAutoGen; }

[MessagePortal(TkMsgType.NM_CH_EnterOutNewGuideOrBattle)]
public class PK_CH_EnterOutNewGuideOrBattle : BaseRequest
{
    public byte State;
    public PK_CH_EnterOutNewGuideOrBattle() => MsgType = TkMsgType.NM_CH_EnterOutNewGuideOrBattle;
    public override IRequest ReadByte(ByteArray byteArray) { State = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(State);
}

[MessagePortal(TkMsgType.NM_CH_CheckHashRuntime)]
public class PK_CH_CheckHashRuntime : BaseRequest
{
    public string HashValue = string.Empty;
    public PK_CH_CheckHashRuntime() => MsgType = TkMsgType.NM_CH_CheckHashRuntime;
    public override IRequest ReadByte(ByteArray byteArray) { HashValue = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(HashValue, 64);
}

[MessagePortal(TkMsgType.NM_CH_SetManualMapID)]
public class PK_CH_SetManualMapID : BaseRequest
{
    public int MapId;
    public int BattleMode;
    public PK_CH_SetManualMapID() => MsgType = TkMsgType.NM_CH_SetManualMapID;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        MapId = byteArray.ReadInt();
        BattleMode = byteArray.ReadInt();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(MapId);
        byteArray.WriteInt(BattleMode);
    }
}

[MessagePortal(TkMsgType.NM_HC_SetManualMapID)]
public class PK_HC_SetManualMapID : BaseRequest
{
    public int MapId;
    public int BattleMode;
    public PK_HC_SetManualMapID() => MsgType = TkMsgType.NM_HC_SetManualMapID;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        MapId = byteArray.ReadInt();
        BattleMode = byteArray.ReadInt();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(MapId);
        byteArray.WriteInt(BattleMode);
    }
}

[MessagePortal(TkMsgType.NM_CH_GetGlobalCardTankLvl)]
public class PK_CH_GetGlobalCardTankLvl : BaseRequest { public PK_CH_GetGlobalCardTankLvl() => MsgType = TkMsgType.NM_CH_GetGlobalCardTankLvl; }

[MessagePortal(TkMsgType.NM_HC_GetGlobalCardTankLvl)]
public class PK_HC_GetGlobalCardTankLvl : BaseRequest
{
    public PK_GlobalCardTankLvlDay[] TankLvl = new PK_GlobalCardTankLvlDay[8];

    public PK_HC_GetGlobalCardTankLvl() => MsgType = TkMsgType.NM_HC_GetGlobalCardTankLvl;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        TankLvl = new PK_GlobalCardTankLvlDay[8];
        for (int i = 0; i < 8; i++)
            TankLvl[i] = new PK_GlobalCardTankLvlDay { TankMaxLevel = byteArray.ReadInt(), UnixTimeStamp = byteArray.ReadLong() };
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        for (int i = 0; i < 8; i++)
        {
            byteArray.WriteInt(TankLvl[i].TankMaxLevel);
            byteArray.WriteLong(TankLvl[i].UnixTimeStamp);
        }
    }
}

public class PK_GlobalCardTankLvlDay
{
    public int TankMaxLevel;
    public long UnixTimeStamp;
}

public struct PersonalEmblemAccessItemDto { public int EmblemID; public int TankID; }

[MessagePortal(TkMsgType.NM_CH_PersonalEmblemAccess)]
public class PK_CH_PersonalEmblemAccess : BaseRequest
{
    // Тот же msgType используется в обе стороны: клиент шлёт пустой запрос
    // («дай мои личные эмблемы»), сервер отвечает тем же типом, заполненным
    // { Add; Remove; Emblems[100] } (см. клиент — ClientSocket.PK_HC_PersonalEmblemAccess,
    // содержит ТОЛЬКО ReadByte, т.е. это ответ, который клиент только читает).
    // Раньше сервер никогда не отвечал на этот запрос вовсе.
    public const int MaxPersonalEmblems = 100;
    public PersonalEmblemAccessItemDto Add;
    public PersonalEmblemAccessItemDto Remove;
    public PersonalEmblemAccessItemDto[] Emblems = new PersonalEmblemAccessItemDto[MaxPersonalEmblems];

    public PK_CH_PersonalEmblemAccess() => MsgType = TkMsgType.NM_CH_PersonalEmblemAccess;

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(Add.EmblemID);
        byteArray.WriteInt(Add.TankID);
        byteArray.WriteInt(Remove.EmblemID);
        byteArray.WriteInt(Remove.TankID);
        for (int i = 0; i < MaxPersonalEmblems; i++)
        {
            byteArray.WriteInt(Emblems[i].EmblemID);
            byteArray.WriteInt(Emblems[i].TankID);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_BuyTankEmblem)]
public class PK_CH_BuyTankEmblem : BaseRequest
{
    // ВАЖНО: клиент (Hall.SendTankEmblemOperator) шлёт
    // { sbyte PriceType; sbyte EmblemSlot; int TankID; int EmblemID } — 10 байт.
    // Раньше сервер читал только { int EmblemId } (4 байта) — расхождение и
    // сдвиг разбора всех последующих сообщений в очереди.
    public sbyte PriceType;
    public sbyte EmblemSlot;
    public int TankID;
    public int EmblemID;
    public PK_CH_BuyTankEmblem() => MsgType = TkMsgType.NM_CH_BuyTankEmblem;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        PriceType = byteArray.ReadSByte();
        EmblemSlot = byteArray.ReadSByte();
        TankID = byteArray.ReadInt();
        EmblemID = byteArray.ReadInt();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteSByte(PriceType);
        byteArray.WriteSByte(EmblemSlot);
        byteArray.WriteInt(TankID);
        byteArray.WriteInt(EmblemID);
    }
}

[MessagePortal(TkMsgType.NM_HC_BuyTankEmblem)]
public class PK_HC_BuyTankEmblem : BaseRequest
{
    // ВАЖНО: клиент (Player.OnSynTankEmblemInfo) читает
    // { sbyte SucessFlag; sbyte PriceId; sbyte EmblemSlot; int TankID; int EmblemID;
    //   long EmblemTime } — и применяет эмблему НА ТАНК именно из этих полей
    // (tank.TankData.EmblemId[EmblemSlot] = EmblemID). Раньше сервер слал только
    // { byte BuyResult } (1 байт) — клиент никогда не получал ни TankID, ни
    // EmblemSlot, ни EmblemID, поэтому эмблема физически не могла быть применена.
    public sbyte SucessFlag;
    public sbyte PriceId;
    public sbyte EmblemSlot;
    public int TankID;
    public int EmblemID;
    public long EmblemTime;
    public PK_HC_BuyTankEmblem() => MsgType = TkMsgType.NM_HC_BuyTankEmblem;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        SucessFlag = byteArray.ReadSByte();
        PriceId = byteArray.ReadSByte();
        EmblemSlot = byteArray.ReadSByte();
        TankID = byteArray.ReadInt();
        EmblemID = byteArray.ReadInt();
        EmblemTime = byteArray.ReadLong();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteSByte(SucessFlag);
        byteArray.WriteSByte(PriceId);
        byteArray.WriteSByte(EmblemSlot);
        byteArray.WriteInt(TankID);
        byteArray.WriteInt(EmblemID);
        byteArray.WriteLong(EmblemTime);
    }
}

[MessagePortal(TkMsgType.NM_CH_DropTankEmblem)]
public class PK_CH_DropTankEmblem : BaseRequest
{
    // Клиент (Hall.SendTankRemoveEmblem) шлёт { sbyte EmblemSlot; int TankID;
    // int EmblemID } — 9 байт. Раньше сервер читал только { int EmblemId }.
    public sbyte EmblemSlot;
    public int TankID;
    public int EmblemID;
    public PK_CH_DropTankEmblem() => MsgType = TkMsgType.NM_CH_DropTankEmblem;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        EmblemSlot = byteArray.ReadSByte();
        TankID = byteArray.ReadInt();
        EmblemID = byteArray.ReadInt();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteSByte(EmblemSlot);
        byteArray.WriteInt(TankID);
        byteArray.WriteInt(EmblemID);
    }
}

[MessagePortal(TkMsgType.NM_CH_UseOffer)]
public class PK_CH_UseOffer : BaseRequest
{
    public int OfferId;
    public PK_CH_UseOffer() => MsgType = TkMsgType.NM_CH_UseOffer;
    public override IRequest ReadByte(ByteArray byteArray) { OfferId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(OfferId);
}

[MessagePortal(TkMsgType.NM_HC_UseOfferStatus)]
public class PK_HC_UseOfferStatus : BaseRequest
{
    public int OfferId; public byte UseResult;
    public PK_HC_UseOfferStatus() => MsgType = TkMsgType.NM_HC_UseOfferStatus;
    public override IRequest ReadByte(ByteArray byteArray) { OfferId = byteArray.ReadInt(); UseResult = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(OfferId); byteArray.WriteByte(UseResult); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_QUIT)]
public class PK_CHW_CORPS_QUIT : BaseRequest
{
    public long CorpsId;
    public PK_CHW_CORPS_QUIT() => MsgType = TkMsgType.NM_CHW_CORPS_QUIT;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsId); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_DISMISS)]
public class PK_CHW_CORPS_DISMISS : BaseRequest
{
    public long CorpsId;
    public PK_CHW_CORPS_DISMISS() => MsgType = TkMsgType.NM_CHW_CORPS_DISMISS;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsId); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_MOD_INTRODUCE)]
public class PK_CHW_CORPS_MOD_INTRODUCE : BaseRequest
{
    public long CorpsId; public string Introduce = string.Empty;
    public PK_CHW_CORPS_MOD_INTRODUCE() => MsgType = TkMsgType.NM_CHW_CORPS_MOD_INTRODUCE;
    public override IRequest ReadByte(ByteArray byteArray) { CorpsId = byteArray.ReadLong(); Introduce = byteArray.ReadUTF(200); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(CorpsId); byteArray.WriteUTF(Introduce, 200); }
}

[MessagePortal(TkMsgType.NM_CH_CORPS_MOD_TIPNAME)]
public class PK_CH_CORPS_MOD_TIPNAME : BaseRequest
{
    public long CorpsId; public string TipName = string.Empty;
    public PK_CH_CORPS_MOD_TIPNAME() => MsgType = TkMsgType.NM_CH_CORPS_MOD_TIPNAME;
    public override IRequest ReadByte(ByteArray byteArray) { CorpsId = byteArray.ReadLong(); TipName = byteArray.ReadUTF(21); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(CorpsId); byteArray.WriteUTF(TipName, 21); }
}

[MessagePortal(TkMsgType.NM_CH_CORPS_MOD_NAME)]
public class PK_CH_CORPS_MOD_NAME : BaseRequest
{
    public long CorpsId; public string CorpsName = string.Empty;
    public PK_CH_CORPS_MOD_NAME() => MsgType = TkMsgType.NM_CH_CORPS_MOD_NAME;
    public override IRequest ReadByte(ByteArray byteArray) { CorpsId = byteArray.ReadLong(); CorpsName = byteArray.ReadUTF(21); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(CorpsId); byteArray.WriteUTF(CorpsName, 21); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_APPLICATION_REQ)]
public class PK_CHW_CORPS_APPLICATION_REQ : BaseRequest
{
    public long CorpsId; public ushort Bias;
    public PK_CHW_CORPS_APPLICATION_REQ() => MsgType = TkMsgType.NM_CHW_CORPS_APPLICATION_REQ;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); Bias = ba.ReadUShort(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsId); ba.WriteUShort(Bias); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_APPLICATION_REP)]
public class PK_CHW_CORPS_APPLICATION_REP : BaseRequest
{
    public long ApplicantGuid; public byte AcceptState;
    public PK_CHW_CORPS_APPLICATION_REP() => MsgType = TkMsgType.NM_CHW_CORPS_APPLICATION_REP;
    public override IRequest ReadByte(ByteArray byteArray) { ApplicantGuid = byteArray.ReadLong(); AcceptState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(ApplicantGuid); byteArray.WriteByte(AcceptState); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_SEARCH)]
public class PK_CHW_CORPS_SEARCH : BaseRequest
{
    public long PlayerGuid; public string SearchName = string.Empty;
    public PK_CHW_CORPS_SEARCH() => MsgType = TkMsgType.NM_CHW_CORPS_SEARCH;
    public override IRequest ReadByte(ByteArray ba) { PlayerGuid = ba.ReadLong(); SearchName = ba.ReadUTF(21); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(SearchName, 21);
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_QUERY)]
public class PK_CHW_CORPS_QUERY : BaseRequest
{
    public long CorpsId; public ushort PageCount; public byte SortType; public ushort PageNumb;
    public PK_CHW_CORPS_QUERY() => MsgType = TkMsgType.NM_CHW_CORPS_QUERY;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); PageCount = ba.ReadUShort(); SortType = ba.ReadByte(); PageNumb = ba.ReadUShort(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(CorpsId);
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_DATA)]
public class PK_CHW_CORPS_DATA : BaseRequest { public PK_CHW_CORPS_DATA() => MsgType = TkMsgType.NM_CHW_CORPS_DATA; }

[MessagePortal(TkMsgType.NM_CHW_CORPS_MEMBER_REQ)]
public class PK_CHW_CORPS_MEMBER_REQ : BaseRequest
{
    // Client sends: GUID(long,8) + Bias(ushort,2) + PageNum(byte,1) = 11 bytes
    public long   CorpsId;  // = GUID in client
    public ushort Bias;
    public byte   PageType; // = PageNum in client
    public PK_CHW_CORPS_MEMBER_REQ() => MsgType = TkMsgType.NM_CHW_CORPS_MEMBER_REQ;
    public override IRequest ReadByte(ByteArray ba)
        { CorpsId = ba.ReadLong(); Bias = ba.ReadUShort(); PageType = ba.ReadByte(); return this; }
    public override void WriteByte(ByteArray ba)
        { ba.WriteLong(CorpsId); ba.WriteUShort(Bias); ba.WriteByte(PageType); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_COMMISSION)]
public class PK_CHW_CORPS_COMMISSION : BaseRequest
{
    public long PlayerGuid; public long MemberGuid; public byte NewJob;
    public PK_CHW_CORPS_COMMISSION() => MsgType = TkMsgType.NM_CHW_CORPS_COMMISSION;
    public override IRequest ReadByte(ByteArray ba) { PlayerGuid = ba.ReadLong(); MemberGuid = ba.ReadLong(); NewJob = ba.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(MemberGuid); byteArray.WriteByte(NewJob); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_EXPEL)]
public class PK_CHW_CORPS_EXPEL : BaseRequest
{
    public long PlayerGuid; public long MemberGuid;
    public PK_CHW_CORPS_EXPEL() => MsgType = TkMsgType.NM_CHW_CORPS_EXPEL;
    public override IRequest ReadByte(ByteArray ba) { PlayerGuid = ba.ReadLong(); MemberGuid = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(MemberGuid);
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_CLOSE)]
public class PK_CHW_CORPS_CLOSE : BaseRequest
{
    public long PlayerGuid; public byte CloseState;
    public PK_CHW_CORPS_CLOSE() => MsgType = TkMsgType.NM_CHW_CORPS_CLOSE;
    public override IRequest ReadByte(ByteArray ba) { PlayerGuid = ba.ReadLong(); CloseState = ba.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(CloseState);
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_INVITE_REP)]
public class PK_CHW_CORPS_INVITE_REP : BaseRequest
{
    public long InviterGuid; public byte AcceptState;
    public PK_CHW_CORPS_INVITE_REP() => MsgType = TkMsgType.NM_CHW_CORPS_INVITE_REP;
    public override IRequest ReadByte(ByteArray byteArray) { InviterGuid = byteArray.ReadLong(); AcceptState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteLong(InviterGuid); byteArray.WriteByte(AcceptState); }
}

[MessagePortal(TkMsgType.NM_CHW_CORPS_ASSIGNMENT)]
public class PK_CHW_CORPS_ASSIGNMENT : BaseRequest
{
    public long CorpsId; public long MemGuid;
    public PK_CHW_CORPS_ASSIGNMENT() => MsgType = TkMsgType.NM_CHW_CORPS_ASSIGNMENT;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); MemGuid = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(CorpsId);
}

[MessagePortal(TkMsgType.NM_CHW_Corps_Mod_TaxRate)]
public class PK_CHW_Corps_Mod_TaxRate : BaseRequest
{
    public byte TaxRate;
    public PK_CHW_Corps_Mod_TaxRate() => MsgType = TkMsgType.NM_CHW_Corps_Mod_TaxRate;
    public override IRequest ReadByte(ByteArray byteArray) { TaxRate = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(TaxRate);
}

// Corps exchange: send gold or silver → personal loyalty points
// Fields: PlayerGuid(long), CorpsId(long), Type(byte: 0=gold 1=silver), ConvertTimes(int)
[MessagePortal(TkMsgType.NM_CHW_Corps_ConvertMoney)]
public class PK_CHW_Corps_ConvertMoney : BaseRequest
{
    public long PlayerGuid;
    public long CorpsId;
    public byte Type;          // 0=Gold, 1=Silver
    public int  ConvertTimes;  // how many exchange cycles to run
    public PK_CHW_Corps_ConvertMoney() => MsgType = TkMsgType.NM_CHW_Corps_ConvertMoney;
    public override IRequest ReadByte(ByteArray ba)
    { PlayerGuid = ba.ReadLong(); CorpsId = ba.ReadLong(); Type = ba.ReadByte(); ConvertTimes = ba.ReadInt(); return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteLong(PlayerGuid); ba.WriteLong(CorpsId); ba.WriteByte(Type); ba.WriteInt(ConvertTimes); }
}

// Server response to corps convert money.
// Client reads: PlayerGuid(long) + RetFlag(byte)
public class PK_WHC_Corps_ConvertMoney_Res : BaseRequest
{
    public long PlayerGuid;
    public byte RetFlag;   // 1=success, 0=fail
    public PK_WHC_Corps_ConvertMoney_Res() => MsgType = (TkMsgType)415;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteLong(PlayerGuid); ba.WriteByte(RetFlag); }
}

[MessagePortal(TkMsgType.NM_CHW_Corps_MoneyChangeList)]
public class PK_CHW_Corps_MoneyChangeList : BaseRequest { public PK_CHW_Corps_MoneyChangeList() => MsgType = TkMsgType.NM_CHW_Corps_MoneyChangeList; }

[MessagePortal(TkMsgType.NM_CHW_Corps_MoneyChangeList2)]
public class PK_CHW_Corps_MoneyChangeList2 : BaseRequest { public PK_CHW_Corps_MoneyChangeList2() => MsgType = TkMsgType.NM_CHW_Corps_MoneyChangeList2; }

[MessagePortal(TkMsgType.NM_CHW_CorpsAth_Invite)]
public class PK_CHW_CorpsAth_Invite : BaseRequest
{
    public string PlayerName = string.Empty;
    public PK_CHW_CorpsAth_Invite() => MsgType = TkMsgType.NM_CHW_CorpsAth_Invite;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerName = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(PlayerName, 64);
}

[MessagePortal(TkMsgType.NM_CHW_FriendStateReq)]
public class PK_CHW_FriendStateReq : BaseRequest
{
    public long FriendGuid;
    public PK_CHW_FriendStateReq() => MsgType = TkMsgType.NM_CHW_FriendStateReq;
    public override IRequest ReadByte(ByteArray byteArray) { FriendGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(FriendGuid);
}

[MessagePortal(TkMsgType.NM_CHM_CorpsAth_AllowJoinAll)]
public class PK_CHM_CorpsAth_AllowJoinAll : BaseRequest
{
    public byte AllowState;
    public PK_CHM_CorpsAth_AllowJoinAll() => MsgType = TkMsgType.NM_CHM_CorpsAth_AllowJoinAll;
    public override IRequest ReadByte(ByteArray byteArray) { AllowState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(AllowState);
}

[MessagePortal(TkMsgType.NM_CHM_CorpsAth_AllowQuit)]
public class PK_CHM_CorpsAth_AllowQuit : BaseRequest
{
    public byte AllowState;
    public PK_CHM_CorpsAth_AllowQuit() => MsgType = TkMsgType.NM_CHM_CorpsAth_AllowQuit;
    public override IRequest ReadByte(ByteArray byteArray) { AllowState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(AllowState);
}

[MessagePortal(TkMsgType.NM_CHW_FRIEND_UPDATE)]
public class PK_CHW_FRIEND_UPDATE : BaseRequest { public PK_CHW_FRIEND_UPDATE() => MsgType = TkMsgType.NM_CHW_FRIEND_UPDATE; }

[MessagePortal(TkMsgType.NM_CHW_FRIEND_OPERATE)]
public class PK_CHW_FRIEND_OPERATE : BaseRequest
{
    public byte OperType; public string FriendName = string.Empty;
    public PK_CHW_FRIEND_OPERATE() => MsgType = TkMsgType.NM_CHW_FRIEND_OPERATE;
    public override IRequest ReadByte(ByteArray byteArray) { OperType = byteArray.ReadByte(); FriendName = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(OperType); byteArray.WriteUTF(FriendName, 64); }
}

[MessagePortal(TkMsgType.NM_WHC_FRIEND_OPERATE_RES)]
public class PK_WHC_FRIEND_OPERATE_RES : BaseRequest
{
    public byte OperType; public byte Result;
    public PK_WHC_FRIEND_OPERATE_RES() => MsgType = TkMsgType.NM_WHC_FRIEND_OPERATE_RES;
    public override IRequest ReadByte(ByteArray byteArray) { OperType = byteArray.ReadByte(); Result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(OperType); byteArray.WriteByte(Result); }
}

[MessagePortal(TkMsgType.NM_CHW_Domain_Bidding)]
public class PK_CHW_Domain_Bidding : BaseRequest
{
    public int BidAmount;
    public PK_CHW_Domain_Bidding() => MsgType = TkMsgType.NM_CHW_Domain_Bidding;
    public override IRequest ReadByte(ByteArray byteArray) { BidAmount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BidAmount);
}

[MessagePortal(TkMsgType.NM_CHW_Domain_FightOther)]
public class PK_CHW_Domain_FightOther : BaseRequest
{
    public long OpponentGuid;
    public PK_CHW_Domain_FightOther() => MsgType = TkMsgType.NM_CHW_Domain_FightOther;
    public override IRequest ReadByte(ByteArray byteArray) { OpponentGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(OpponentGuid);
}

[MessagePortal(TkMsgType.NM_CHW_Domain_FightTime)]
public class PK_CHW_Domain_FightTime : BaseRequest { public PK_CHW_Domain_FightTime() => MsgType = TkMsgType.NM_CHW_Domain_FightTime; }

[MessagePortal(TkMsgType.NM_CHW_PLAYER_QUERY)]
public class PK_CHW_PLAYER_QUERY : BaseRequest
{
    public string PlayerName = string.Empty;
    public PK_CHW_PLAYER_QUERY() => MsgType = TkMsgType.NM_CHW_PLAYER_QUERY;
    public override IRequest ReadByte(ByteArray byteArray) { PlayerName = byteArray.ReadUTF(64); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(PlayerName, 64);
}

[MessagePortal(TkMsgType.NM_WHC_PLAYER_QUERY_RES)]
public class PK_WHC_PLAYER_QUERY_RES : BaseRequest
{
    public long PlayerGuid; public string PlayerName = string.Empty; public int Level; public int BattleCap;
    public PK_WHC_PLAYER_QUERY_RES() => MsgType = TkMsgType.NM_WHC_PLAYER_QUERY_RES;
    public override IRequest ReadByte(ByteArray byteArray)
    { PlayerGuid = byteArray.ReadLong(); PlayerName = byteArray.ReadUTF(64); Level = byteArray.ReadInt(); BattleCap = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray)
    { byteArray.WriteLong(PlayerGuid); byteArray.WriteUTF(PlayerName, 64); byteArray.WriteInt(Level); byteArray.WriteInt(BattleCap); }
}

[MessagePortal(TkMsgType.NM_CHW_CorpsBid_PlaceBid)]
public class PK_CHW_CorpsBid_PlaceBid : BaseRequest
{
    public int BidAmount;
    public PK_CHW_CorpsBid_PlaceBid() => MsgType = TkMsgType.NM_CHW_CorpsBid_PlaceBid;
    public override IRequest ReadByte(ByteArray byteArray) { BidAmount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BidAmount);
}

[MessagePortal(TkMsgType.NM_CHW_CorpsBid_GetTank)]
public class PK_CHW_CorpsBid_GetTank : BaseRequest
{
    public int TankId;
    public PK_CHW_CorpsBid_GetTank() => MsgType = TkMsgType.NM_CHW_CorpsBid_GetTank;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(TankId);
}

[MessagePortal(TkMsgType.NM_CHW_CorpsBid_Lists)]
public class PK_CHW_CorpsBid_Lists : BaseRequest { public PK_CHW_CorpsBid_Lists() => MsgType = TkMsgType.NM_CHW_CorpsBid_Lists; }

[MessagePortal(TkMsgType.NM_CHW_CorpsLevel_upgrade)]
public class PK_CHW_CorpsLevel_upgrade : BaseRequest { public PK_CHW_CorpsLevel_upgrade() => MsgType = TkMsgType.NM_CHW_CorpsLevel_upgrade; }

[MessagePortal(TkMsgType.NM_CH_AskGetPayTask)]
public class PK_CH_AskGetPayTask : BaseRequest { public PK_CH_AskGetPayTask() => MsgType = TkMsgType.NM_CH_AskGetPayTask; }

[MessagePortal(TkMsgType.NM_HC_AskGetPayTask)]
public class PK_HC_AskGetPayTask : BaseRequest { public PK_HC_AskGetPayTask() => MsgType = TkMsgType.NM_HC_AskGetPayTask; }

[MessagePortal(TkMsgType.NM_CH_AskStandaloneLoginReward)]
public class PK_CH_AskStandaloneLoginReward : BaseRequest { public PK_CH_AskStandaloneLoginReward() => MsgType = TkMsgType.NM_CH_AskStandaloneLoginReward; }

[MessagePortal(TkMsgType.NM_HC_AskStandaloneLoginReward)]
public class PK_HC_AskStandaloneLoginReward : BaseRequest
{
    public byte HasReward;
    public PK_HC_AskStandaloneLoginReward() => MsgType = TkMsgType.NM_HC_AskStandaloneLoginReward;
    public override IRequest ReadByte(ByteArray byteArray) { HasReward = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(HasReward);
}

[MessagePortal(TkMsgType.NM_CH_Get_MonthlyLoginAward)]
public class PK_CH_Get_MonthlyLoginAward : BaseRequest { public PK_CH_Get_MonthlyLoginAward() => MsgType = TkMsgType.NM_CH_Get_MonthlyLoginAward; }

[MessagePortal(TkMsgType.NM_CH_SignIn_MonthlyLoginAward)]
public class PK_CH_SignIn_MonthlyLoginAward : BaseRequest
{
    public int DayIndex;
    public PK_CH_SignIn_MonthlyLoginAward() => MsgType = TkMsgType.NM_CH_SignIn_MonthlyLoginAward;
    public override IRequest ReadByte(ByteArray byteArray) { DayIndex = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(DayIndex);
}

[MessagePortal(TkMsgType.NM_CH_GetTaskReward)]
public class PK_CH_GetTaskReward : BaseRequest
{
    public ushort TaskId;
    public PK_CH_GetTaskReward() => MsgType = TkMsgType.NM_CH_GetTaskReward;
    public override IRequest ReadByte(ByteArray byteArray) { TaskId = byteArray.ReadUShort(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUShort(TaskId);
}

[MessagePortal(TkMsgType.NM_CH_GetChouJiang)]
public class PK_CH_GetChouJiang : BaseRequest { public PK_CH_GetChouJiang() => MsgType = TkMsgType.NM_CH_GetChouJiang; }

[MessagePortal(TkMsgType.NM_CH_ChouJiangRun)]
public class PK_CH_ChouJiangRun : BaseRequest { public PK_CH_ChouJiangRun() => MsgType = TkMsgType.NM_CH_ChouJiangRun; }

[MessagePortal(TkMsgType.NM_HC_ChouJiang)]
public class PK_HC_ChouJiang : BaseRequest
{
    public int PrizeId;
    public PK_HC_ChouJiang() => MsgType = TkMsgType.NM_HC_ChouJiang;
    public override IRequest ReadByte(ByteArray byteArray) { PrizeId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(PrizeId);
}

[MessagePortal(TkMsgType.NM_HC_ChouJiangOpen)]
public class PK_HC_ChouJiangOpen : BaseRequest
{
    public byte IsOpen;
    public PK_HC_ChouJiangOpen() => MsgType = TkMsgType.NM_HC_ChouJiangOpen;
    public override IRequest ReadByte(ByteArray byteArray) { IsOpen = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(IsOpen);
}

[MessagePortal(TkMsgType.NM_HC_ChouJiangRunRes)]
public class PK_HC_ChouJiangRunRes : BaseRequest
{
    public int PrizeId;
    public PK_HC_ChouJiangRunRes() => MsgType = TkMsgType.NM_HC_ChouJiangRunRes;
    public override IRequest ReadByte(ByteArray byteArray) { PrizeId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(PrizeId);
}

[MessagePortal(TkMsgType.NM_CH_OPENBOX)]
public class PK_CH_OPENBOX : BaseRequest
{
    public int BoxId;
    public PK_CH_OPENBOX() => MsgType = TkMsgType.NM_CH_OPENBOX;
    public override IRequest ReadByte(ByteArray byteArray) { BoxId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BoxId);
}

public struct TreasureRewardItemDto
{
    public int PrizeID;
    public byte IsGiveUp; // клиент читает как byte, приводит к bool
}

[MessagePortal(TkMsgType.NM_HC_OPENBOX)]
public class PK_HC_OPENBOX : BaseRequest
{
    // ВАЖНО: клиент (TreasureBoxManager.GetOpenBoxResult) читает полную структуру
    // ниже и заполняет TreasureRewarditems ИЗ ЭТОГО ПАКЕТА — это единственное место,
    // где приз попадает на клиент. Раньше сервер слал только { int BoxId; byte
    // OpenResult } (5 байт вместо нужных как минимум 21), поэтому rewards
    // оставался пуст и последующий GETBOXPRIZE не мог ничего выдать — плюс лишние
    // байты в потоке сдвигали разбор следующего сообщения.
    public int powerValue;
    public byte IsShine;
    public int nBtnTxtStata;
    public int nTotalValue;
    public int Price;
    public int nPrizeNum;
    public TreasureRewardItemDto[] rewards = System.Array.Empty<TreasureRewardItemDto>();

    public PK_HC_OPENBOX() => MsgType = TkMsgType.NM_HC_OPENBOX;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        powerValue = byteArray.ReadInt();
        IsShine = byteArray.ReadByte();
        nBtnTxtStata = byteArray.ReadInt();
        nTotalValue = byteArray.ReadInt();
        Price = byteArray.ReadInt();
        nPrizeNum = byteArray.ReadInt();
        rewards = new TreasureRewardItemDto[nPrizeNum];
        for (int i = 0; i < nPrizeNum; i++)
        {
            rewards[i].PrizeID = byteArray.ReadInt();
            rewards[i].IsGiveUp = byteArray.ReadByte();
        }
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteInt(powerValue);
        byteArray.WriteByte(IsShine);
        byteArray.WriteInt(nBtnTxtStata);
        byteArray.WriteInt(nTotalValue);
        byteArray.WriteInt(Price);
        byteArray.WriteInt(nPrizeNum);
        foreach (var r in rewards)
        {
            byteArray.WriteInt(r.PrizeID);
            byteArray.WriteByte(r.IsGiveUp);
        }
    }
}

[MessagePortal(TkMsgType.NM_CH_GETBOXPRIZE)]
public class PK_CH_GETBOXPRIZE : BaseRequest
{
    public int BoxId;
    public PK_CH_GETBOXPRIZE() => MsgType = TkMsgType.NM_CH_GETBOXPRIZE;
    public override IRequest ReadByte(ByteArray byteArray) { BoxId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(BoxId);
}

[MessagePortal(TkMsgType.NM_HC_GETBOXPRIZE)]
public class PK_HC_GETBOXPRIZE : BaseRequest
{
    // ВАЖНО: клиент (TreasureBoxManager.GetBoxPrizeResult) читает этот пакет как
    // { byte result } — 1 байт, и раздаёт награды из TreasureRewarditems, которые
    // были заполнены РАНЕЕ пакетом OPENBOX, а не из полей этого пакета. Раньше
    // сервер слал { int BoxId; byte GetResult } (5 байт) — лишние 4 байта
    // оставались непрочитанными и сдвигали разбор следующего сообщения в очереди.
    public byte result;
    public PK_HC_GETBOXPRIZE() => MsgType = TkMsgType.NM_HC_GETBOXPRIZE;
    public override IRequest ReadByte(ByteArray byteArray) { result = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(result);
}

[MessagePortal(TkMsgType.NM_CH_BuyCasernCrew)]
public class PK_CH_BuyCasernCrew : BaseRequest
{
    public byte Flag;
    public PK_CH_BuyCasernCrew() => MsgType = TkMsgType.NM_CH_BuyCasernCrew;
    public override IRequest ReadByte(ByteArray byteArray) { Flag = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(Flag);
}

[MessagePortal(TkMsgType.NM_CH_NewRecruitGetReq)]
public class PK_CH_NewRecruitGetReq : BaseRequest { public PK_CH_NewRecruitGetReq() => MsgType = TkMsgType.NM_CH_NewRecruitGetReq; }

[MessagePortal(TkMsgType.NM_CH_NewRecruitPushReq)]
public class PK_CH_NewRecruitPushReq : BaseRequest { public PK_CH_NewRecruitPushReq() => MsgType = TkMsgType.NM_CH_NewRecruitPushReq; }

[MessagePortal(TkMsgType.NM_CH_BuyTankSignature)]
public class PK_CH_BuyTankSignature : BaseRequest
{
    // Клиент (Hall.SendTankDecalOperator) шлёт
    // { byte PriceTime; byte SignaturePos; int TankID; int Signature } — 10 байт.
    // Раньше сервер читал только { int SignatureId } (4 байта).
    public byte PriceTime;
    public byte SignaturePos;
    public int TankID;
    public int Signature;
    public PK_CH_BuyTankSignature() => MsgType = TkMsgType.NM_CH_BuyTankSignature;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        PriceTime = byteArray.ReadByte();
        SignaturePos = byteArray.ReadByte();
        TankID = byteArray.ReadInt();
        Signature = byteArray.ReadInt();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(PriceTime);
        byteArray.WriteByte(SignaturePos);
        byteArray.WriteInt(TankID);
        byteArray.WriteInt(Signature);
    }
}

[MessagePortal(TkMsgType.NM_HC_BuyTankSignature)]
public class PK_HC_BuyTankSignature : BaseRequest
{
    // ВАЖНО: клиент (Player.OnSynTankDecalInfo) читает
    // { byte SucessFlag; byte PriceTimeType; byte SignaturePos; int TankID;
    //   int Signature; long SignatureTime } — и применяет надпись на танк именно
    // из этих полей. Раньше сервер слал { int SignatureId; byte BuyResult }
    // (5 байт вместо нужных 19), поэтому надпись физически не могла примениться.
    public byte SucessFlag;
    public byte PriceTimeType;
    public byte SignaturePos;
    public int TankID;
    public int Signature;
    public long SignatureTime;
    public PK_HC_BuyTankSignature() => MsgType = TkMsgType.NM_HC_BuyTankSignature;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        SucessFlag = byteArray.ReadByte();
        PriceTimeType = byteArray.ReadByte();
        SignaturePos = byteArray.ReadByte();
        TankID = byteArray.ReadInt();
        Signature = byteArray.ReadInt();
        SignatureTime = byteArray.ReadLong();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte(SucessFlag);
        byteArray.WriteByte(PriceTimeType);
        byteArray.WriteByte(SignaturePos);
        byteArray.WriteInt(TankID);
        byteArray.WriteInt(Signature);
        byteArray.WriteLong(SignatureTime);
    }
}

[MessagePortal(TkMsgType.NM_CH_GetTankHonor)]
public class PK_CH_GetTankHonor : BaseRequest
{
    public int TankId;
    public PK_CH_GetTankHonor() => MsgType = TkMsgType.NM_CH_GetTankHonor;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(TankId);
}

[MessagePortal(TkMsgType.NM_CH_CorpsAth_RankList)]
public class PK_CH_CorpsAth_RankList : BaseRequest { public PK_CH_CorpsAth_RankList() => MsgType = TkMsgType.NM_CH_CorpsAth_RankList; }

[MessagePortal(TkMsgType.NM_CH_Corps_TankList_Req)]
public class PK_CH_Corps_TankList_Req : BaseRequest { public PK_CH_Corps_TankList_Req() => MsgType = TkMsgType.NM_CH_Corps_TankList_Req; }

[MessagePortal(TkMsgType.NM_CH_CorpsAth_MyRankList)]
public class PK_CH_CorpsAth_MyRankList : BaseRequest { public PK_CH_CorpsAth_MyRankList() => MsgType = TkMsgType.NM_CH_CorpsAth_MyRankList; }

[MessagePortal(TkMsgType.NM_CH_CorpsAth_AwardList)]
public class PK_CH_CorpsAth_AwardList : BaseRequest { public PK_CH_CorpsAth_AwardList() => MsgType = TkMsgType.NM_CH_CorpsAth_AwardList; }

[MessagePortal(TkMsgType.NM_CH_Corps_IntelligenceList)]
public class PK_CH_Corps_IntelligenceList : BaseRequest { public PK_CH_Corps_IntelligenceList() => MsgType = TkMsgType.NM_CH_Corps_IntelligenceList; }

[MessagePortal(TkMsgType.NM_CH_Corps_PrevTankList)]
public class PK_CH_Corps_PrevTankList : BaseRequest { public PK_CH_Corps_PrevTankList() => MsgType = TkMsgType.NM_CH_Corps_PrevTankList; }

[MessagePortal(TkMsgType.NM_CH_CorpsAth_GetAward)]
public class PK_CH_CorpsAth_GetAward : BaseRequest
{
    public int AwardId;
    public PK_CH_CorpsAth_GetAward() => MsgType = TkMsgType.NM_CH_CorpsAth_GetAward;
    public override IRequest ReadByte(ByteArray byteArray) { AwardId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(AwardId);
}

[MessagePortal(TkMsgType.NM_CH_Corps_DevelopTank)]
public class PK_CH_Corps_DevelopTank : BaseRequest
{
    public int  TankId;
    public byte IsTimeTank;      // 0=permanent, 1=7-day rental
    public int  AssignWholeExp;  // free exp the player wants to contribute
    public List<int> SelectedPrevTanks = new();
    public int ResearchExp => AssignWholeExp; // alias

    public PK_CH_Corps_DevelopTank() => MsgType = TkMsgType.NM_CH_Corps_DevelopTank;
    public override IRequest ReadByte(ByteArray ba)
    {
        TankId = ba.ReadInt();
        IsTimeTank = ba.ReadByte();
        AssignWholeExp = ba.ReadInt();
        int count = ba.ReadInt();
        // Client sends count=0 with NO padding int. Only read entries if count > 0.
        for (int i = 0; i < count; i++) SelectedPrevTanks.Add(ba.ReadInt());
        return this;
    }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(TankId); ba.WriteByte(IsTimeTank); ba.WriteInt(AssignWholeExp);
        ba.WriteInt(SelectedPrevTanks.Count);
        if (SelectedPrevTanks.Count == 0) ba.WriteInt(0);
        else foreach (var t in SelectedPrevTanks) ba.WriteInt(t);
    }
}

[MessagePortal(TkMsgType.NM_CH_CorpsAth_JoinReady)]
public class PK_CH_CorpsAth_JoinReady : BaseRequest { public PK_CH_CorpsAth_JoinReady() => MsgType = TkMsgType.NM_CH_CorpsAth_JoinReady; }

[MessagePortal(TkMsgType.NM_CH_CorpsAth_Join)]
public class PK_CH_CorpsAth_Join : BaseRequest
{
    public long CorpsId;
    public PK_CH_CorpsAth_Join() => MsgType = TkMsgType.NM_CH_CorpsAth_Join;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsId); }
}

[MessagePortal(TkMsgType.NM_CH_CorpsAth_JoinQuit)]
public class PK_CH_CorpsAth_JoinQuit : BaseRequest
{
    public long CorpsId;
    public PK_CH_CorpsAth_JoinQuit() => MsgType = TkMsgType.NM_CH_CorpsAth_JoinQuit;
    public override IRequest ReadByte(ByteArray ba) { CorpsId = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsId); }
}

[MessagePortal(TkMsgType.NM_CH_CorpsAth_ChangeTank)]
public class PK_CH_CorpsAth_ChangeTank : BaseRequest
{
    public long TankGuid;
    public PK_CH_CorpsAth_ChangeTank() => MsgType = TkMsgType.NM_CH_CorpsAth_ChangeTank;
    public override IRequest ReadByte(ByteArray byteArray) { TankGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(TankGuid);
}

[MessagePortal(TkMsgType.NM_CH_CorpsAth_AllowJoin)]
public class PK_CH_CorpsAth_AllowJoin : BaseRequest
{
    public byte AllowState;
    public PK_CH_CorpsAth_AllowJoin() => MsgType = TkMsgType.NM_CH_CorpsAth_AllowJoin;
    public override IRequest ReadByte(ByteArray byteArray) { AllowState = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(AllowState);
}

[MessagePortal(TkMsgType.NM_CH_Corps_Buy_NumLimitTimes)]
public class PK_CH_Corps_Buy_NumLimitTimes : BaseRequest { public PK_CH_Corps_Buy_NumLimitTimes() => MsgType = TkMsgType.NM_CH_Corps_Buy_NumLimitTimes; }

[MessagePortal(TkMsgType.NM_CH_Corps_FastDevelop)]
public class PK_CH_Corps_FastDevelop : BaseRequest
{
    public int TankId;
    public PK_CH_Corps_FastDevelop() => MsgType = TkMsgType.NM_CH_Corps_FastDevelop;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(TankId);
}

[MessagePortal(TkMsgType.NM_CH_FireCrew)]
public class PK_CH_FireCrew : BaseRequest
{
    public long CrewGuid;
    public PK_CH_FireCrew() => MsgType = TkMsgType.NM_CH_FireCrew;
    public override IRequest ReadByte(ByteArray byteArray) { CrewGuid = byteArray.ReadLong(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteLong(CrewGuid);
}

[MessagePortal(TkMsgType.NM_CH_TankRankData)]
public class PK_CH_TankRankData : BaseRequest
{
    public int TankId;
    public PK_CH_TankRankData() => MsgType = TkMsgType.NM_CH_TankRankData;
    public override IRequest ReadByte(ByteArray byteArray) { TankId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(TankId);
}

[MessagePortal(TkMsgType.NM_HC_TankRankData)]
public class PK_HC_TankRankData : BaseRequest
{
    public byte SendNewData; public byte DelPrevData; public byte EndTankData;
    public int Count; public int TankID; public int DataSendTime;
    public TankRankDataItem[] TankDataItems = Array.Empty<TankRankDataItem>();
    public PK_HC_TankRankData() => MsgType = TkMsgType.NM_HC_TankRankData;
    public override IRequest ReadByte(ByteArray byteArray) { SendNewData = byteArray.ReadByte(); DelPrevData = byteArray.ReadByte(); EndTankData = byteArray.ReadByte(); Count = byteArray.ReadInt(); TankID = byteArray.ReadInt(); DataSendTime = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(SendNewData); byteArray.WriteByte(DelPrevData); byteArray.WriteByte(EndTankData); byteArray.WriteInt(Count); byteArray.WriteInt(TankID); byteArray.WriteInt(DataSendTime); for (int i = 0; i < TankDataItems.Length; i++) { byteArray.WriteInt(TankDataItems[i].PlayerRank); byteArray.WriteByte(TankDataItems[i].RankMedalLevel); byteArray.WriteLong(TankDataItems[i].PlayerGUID); byteArray.WriteUTF(TankDataItems[i].PlayerName, 64); byteArray.WriteInt(TankDataItems[i].MaxExp); byteArray.WriteInt(TankDataItems[i].KillNum); byteArray.WriteInt(TankDataItems[i].HurtNum); } }
}

[MessagePortal(TkMsgType.NM_CH_PlayerRankData)]
public class PK_CH_PlayerRankData : BaseRequest { public PK_CH_PlayerRankData() => MsgType = TkMsgType.NM_CH_PlayerRankData; }

[MessagePortal(TkMsgType.NM_HC_PlayerRankData)]
public class PK_HC_PlayerRankData : BaseRequest
{
    public byte SendNewData; public byte DelPrevData; public byte EndPlayerData;
    public byte Count;
    public PlayerRankDataItem[] PlayerDataItems = Array.Empty<PlayerRankDataItem>();
    public PK_HC_PlayerRankData() => MsgType = TkMsgType.NM_HC_PlayerRankData;
    public override IRequest ReadByte(ByteArray byteArray) { SendNewData = byteArray.ReadByte(); DelPrevData = byteArray.ReadByte(); EndPlayerData = byteArray.ReadByte(); Count = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(SendNewData); byteArray.WriteByte(DelPrevData); byteArray.WriteByte(EndPlayerData); byteArray.WriteByte(Count); for (int i = 0; i < PlayerDataItems.Length; i++) { byteArray.WriteUShort(PlayerDataItems[i].PlayerRank); byteArray.WriteUTF(PlayerDataItems[i].PlayerRoleName, 64); byteArray.WriteInt((int)PlayerDataItems[i].WinPercent); byteArray.WriteInt((int)PlayerDataItems[i].Effect); byteArray.WriteInt((int)PlayerDataItems[i].TotalFight); byteArray.WriteInt((int)PlayerDataItems[i].FightHurtAverage); byteArray.WriteInt((int)PlayerDataItems[i].FightAssistAverage); byteArray.WriteInt((int)PlayerDataItems[i].HitTargetPercent); byteArray.WriteInt((int)PlayerDataItems[i].HitDestroyAverage); byteArray.WriteInt((int)PlayerDataItems[i].SilverAverage); byteArray.WriteInt((int)PlayerDataItems[i].ExpAverage); byteArray.WriteLong(PlayerDataItems[i].PlayerGUID); } }
}

[MessagePortal(TkMsgType.NM_HC_PlayerOwnTankRank)]
public class PK_HC_PlayerOwnTankRank : BaseRequest
{
    public int Count;
    public PlayerOwnTankRankItem[] Rankitems = Array.Empty<PlayerOwnTankRankItem>();
    public PK_HC_PlayerOwnTankRank() => MsgType = TkMsgType.NM_HC_PlayerOwnTankRank;
    public override IRequest ReadByte(ByteArray byteArray) { Count = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(Count); for (int i = 0; i < Rankitems.Length; i++) { byteArray.WriteInt(Rankitems[i].nTankID); byteArray.WriteInt(Rankitems[i].nRankOrder); byteArray.WriteByte(Rankitems[i].UpOrDown); } }
}

public class PlayerOwnTankRankItem
{
    public int nTankID; public int nRankOrder; public byte UpOrDown;
}

public class TankRankDataItem
{
    public int PlayerRank; public byte RankMedalLevel; public long PlayerGUID;
    public string PlayerName = string.Empty; public int MaxExp; public int KillNum; public int HurtNum; public int TankID;
}

public class PlayerRankDataItem
{
    public ushort PlayerRank; public string PlayerRoleName = string.Empty;
    public uint WinPercent; public uint Effect; public uint TotalFight;
    public uint FightHurtAverage; public uint FightAssistAverage; public uint HitTargetPercent;
    public uint HitDestroyAverage; public uint SilverAverage; public uint ExpAverage;
    public long PlayerGUID;
}

[MessagePortal(TkMsgType.NM_CH_GetLoginReward)]
public class PK_CH_GetLoginReward : BaseRequest { public PK_CH_GetLoginReward() => MsgType = TkMsgType.NM_CH_GetLoginReward; }

[MessagePortal(TkMsgType.NM_CH_PlayerSuccessInCarport)]
public class PK_CH_PlayerSuccessInCarport : BaseRequest { public PK_CH_PlayerSuccessInCarport() => MsgType = TkMsgType.NM_CH_PlayerSuccessInCarport; }

[MessagePortal(TkMsgType.NM_CH_HALLCHAT_ALL)]
public class PK_CH_HALLCHAT_ALL : BaseRequest
{
    // v: раньше читалось как ОДНА строка (ReadUTF()), но клиент (decfilesgame/
    // PK_CH_HallChatAll.cs -> PK_ChatContent.WriteBytes) на самом деле шлёт:
    //   ChatType(byte) + PlayerGUID(long) + PlayerName(UTF, фикс. 64 байта) + Context(UTF, переменная длина).
    // ReadUTF() читал первые 2 байта пакета (часть ChatType+GUID) как длину строки,
    // получал огромное мусорное число и пытался прочитать за пределы буфера ->
    // IndexOutOfRangeException -> сессия рвалась -> "вылет" при отправке в глобальный чат.
    public byte ChatType;
    public long PlayerGUID;
    public string PlayerName = string.Empty;
    public string Message = string.Empty;
    public PK_CH_HALLCHAT_ALL() => MsgType = TkMsgType.NM_CH_HALLCHAT_ALL;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        ChatType = byteArray.ReadByte();
        PlayerGUID = byteArray.ReadLong();
        PlayerName = byteArray.ReadUTF(64);
        Message = byteArray.ReadUTF();
        return this;
    }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUTF(Message);
}

[MessagePortal(TkMsgType.NM_CH_CHANNEL)]
public class PK_CH_CHANNEL : BaseRequest
{
    public int ChannelId;
    public PK_CH_CHANNEL() => MsgType = TkMsgType.NM_CH_CHANNEL;
    public override IRequest ReadByte(ByteArray byteArray) { ChannelId = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(ChannelId);
}

[MessagePortal(TkMsgType.NM_CH_EXCHANGE_TANKEXP)]
public class PK_CH_EXCHANGE_TANKEXP : BaseRequest
{
    public int ExpAmount;
    public PK_CH_EXCHANGE_TANKEXP() => MsgType = TkMsgType.NM_CH_EXCHANGE_TANKEXP;
    public override IRequest ReadByte(ByteArray byteArray) { ExpAmount = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(ExpAmount);
}

[MessagePortal(TkMsgType.NM_CH_ASKBONUSBOXDATA)]
public class PK_CH_ASKBONUSBOXDATA : BaseRequest { public PK_CH_ASKBONUSBOXDATA() => MsgType = TkMsgType.NM_CH_ASKBONUSBOXDATA; }

public struct ResearchNode { public int TypeId; public int Id; }
public struct ResearchTank { public int Id; public byte HasBuy; }
public struct CrewFastTrainOnTank { public int TankID; public byte State; }
public struct CorpsInfo
{
    public long CorpsID; public string CorpsName; public ushort CorpsLevel; public ushort PlayerCount;
    public string Notice; public string Introduce; public string CreateTime; public byte State;
    public string LeaderName; public int MaxMemberCount; public long BattleCap; public int MaxMemSize;
}
public struct CorpsMemberInfo
{
    public long CorpsID; public long UserGUID; public byte Job; public string JoinDate;
    public string LastLoginDate; public string UserName; public int LoyalPoint; public int BattleCap;
}
public struct CorpsApplicationInfo { public long GUID; public string JoinTime; public string UserName; }
public struct TournamentRangeInfo
{
    public int Range; public string Name; public int Damage; public int VictoryTime;
    public int TotalBattleTime; public int MaxDamage; public float VictoryRate;
}

[MessagePortal(TkMsgType.NM_HC_VipTime)]
public class PK_HC_VipTime : BaseRequest
{
    public int VipLeftTime;
    public PK_HC_VipTime() => MsgType = TkMsgType.NM_HC_VipTime;
    public override IRequest ReadByte(ByteArray byteArray) { VipLeftTime = byteArray.ReadInt(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteInt(VipLeftTime);
}

[MessagePortal(TkMsgType.NM_HC_Team_Update_Mode)]
public class PK_HC_Team_Update_Mode : BaseRequest
{
    public byte BattleMode;
    public PK_HC_Team_Update_Mode() => MsgType = TkMsgType.NM_HC_Team_Update_Mode;
    public override IRequest ReadByte(ByteArray byteArray) { BattleMode = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteByte(BattleMode);
}

[MessagePortal(TkMsgType.NM_LC_CreatRoleName)]
public class PK_LC_CreatRoleNameRet : BaseRequest
{
    public ushort Flag;
    public PK_LC_CreatRoleNameRet() => MsgType = TkMsgType.NM_LC_CreatRoleName;
    public override IRequest ReadByte(ByteArray byteArray) { Flag = byteArray.ReadUShort(); return this; }
    public override void WriteByte(ByteArray byteArray) => byteArray.WriteUShort(Flag);
}

[MessagePortal(TkMsgType.NM_HC_SingleRechargeRewardStatus)]
public class PK_HC_SingleRechargeRewardStatus : BaseRequest
{
    public byte[] RewardStatus = new byte[5];
    public PK_HC_SingleRechargeRewardStatus() => MsgType = TkMsgType.NM_HC_SingleRechargeRewardStatus;
    public override IRequest ReadByte(ByteArray byteArray) { for (int i = 0; i < 5; i++) RewardStatus[i] = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { for (int i = 0; i < 5; i++) byteArray.WriteByte(RewardStatus[i]); }
}



[MessagePortal(TkMsgType.NM_HC_UpdatePlayerCharged)]
public class PK_HC_UpdatePlayerCharged : BaseRequest
{
    public bool IsChargedFlag;
    public int  TotalCharge;
    public PK_HC_UpdatePlayerCharged() => MsgType = TkMsgType.NM_HC_UpdatePlayerCharged;
    public override IRequest ReadByte(ByteArray ba) { IsChargedFlag = ba.ReadBool(); TotalCharge = ba.ReadInt(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteBool(IsChargedFlag); ba.WriteInt(TotalCharge); }
}
[MessagePortal(TkMsgType.NM_HC_AccumulateRechargeRewardStatus)]
public class PK_HC_AccumulateRechargeRewardStatus : BaseRequest
{
    public int TotalCharge;
    public byte[] RewardStatus = new byte[5];
    public PK_HC_AccumulateRechargeRewardStatus() => MsgType = TkMsgType.NM_HC_AccumulateRechargeRewardStatus;
    public override IRequest ReadByte(ByteArray byteArray) { TotalCharge = byteArray.ReadInt(); for (int i = 0; i < 5; i++) RewardStatus[i] = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteInt(TotalCharge); for (int i = 0; i < 5; i++) byteArray.WriteByte(RewardStatus[i]); }
}

[MessagePortal(TkMsgType.NM_HC_ContinuousLoginRewardStatus)]
public class PK_HC_ContinuousLoginRewardStatus : BaseRequest
{
    public byte[] RewardStatus = new byte[7];
    public PK_HC_ContinuousLoginRewardStatus() => MsgType = TkMsgType.NM_HC_ContinuousLoginRewardStatus;
    public override IRequest ReadByte(ByteArray byteArray) { for (int i = 0; i < 7; i++) RewardStatus[i] = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { for (int i = 0; i < 7; i++) byteArray.WriteByte(RewardStatus[i]); }
}

[MessagePortal(TkMsgType.NM_HC_Update_MonthlyLoginAward_GetInfo)]
public class PK_HC_Update_MonthlyLoginAward_GetInfo : BaseRequest
{
    public byte AwardNum;
    public byte[] AwardState = Array.Empty<byte>();
    public PK_HC_Update_MonthlyLoginAward_GetInfo() => MsgType = TkMsgType.NM_HC_Update_MonthlyLoginAward_GetInfo;
    public override IRequest ReadByte(ByteArray byteArray) { AwardNum = byteArray.ReadByte(); AwardState = new byte[AwardNum]; for (int i = 0; i < AwardNum; i++) AwardState[i] = byteArray.ReadByte(); return this; }
    public override void WriteByte(ByteArray byteArray) { byteArray.WriteByte(AwardNum); for (int i = 0; i < AwardState.Length; i++) byteArray.WriteByte(AwardState[i]); }
}

[MessagePortal(TkMsgType.NM_HC_Update_MonthlyLoginAward_SignInfo)]
public class PK_HC_Update_MonthlyLoginAward_SignInfo : BaseRequest
{
    public short Year;
    public byte Month;
    public byte Date;
    public byte IsSignIn;
    public byte BoomFactor;
    public byte SignInNum;
    public byte[] SignInList = Array.Empty<byte>();
    public PK_HC_Update_MonthlyLoginAward_SignInfo() => MsgType = TkMsgType.NM_HC_Update_MonthlyLoginAward_SignInfo;
    public override IRequest ReadByte(ByteArray byteArray)
    {
        Year = byteArray.ReadShort(); Month = byteArray.ReadByte(); Date = byteArray.ReadByte();
        IsSignIn = byteArray.ReadByte(); BoomFactor = byteArray.ReadByte();
        SignInNum = byteArray.ReadByte(); SignInList = new byte[SignInNum];
        for (int i = 0; i < SignInNum; i++) SignInList[i] = byteArray.ReadByte();
        return this;
    }
    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteShort(Year); byteArray.WriteByte(Month); byteArray.WriteByte(Date);
        byteArray.WriteByte(IsSignIn); byteArray.WriteByte(BoomFactor);
        byteArray.WriteByte(SignInNum);
        for (int i = 0; i < SignInList.Length; i++) byteArray.WriteByte(SignInList[i]);
    }
}

[MessagePortal(TkMsgType.NM_SEND_CONFIG)]
public class PK_CH_SendConfig : BaseRequest
{
    public short AimFactorPace, AimFactorVehicleRotation, AimFactorTurretRotation;
    public short DeviceDamageFactor1, DeviceDamageFactor2, MaxDispersionFactor;
    public short ConsumeUseDelay;

    public PK_CH_SendConfig() => MsgType = TkMsgType.NM_SEND_CONFIG;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        AimFactorPace = byteArray.ReadShort();
        AimFactorVehicleRotation = byteArray.ReadShort();
        AimFactorTurretRotation = byteArray.ReadShort();
        DeviceDamageFactor1 = byteArray.ReadShort();
        DeviceDamageFactor2 = byteArray.ReadShort();
        MaxDispersionFactor = byteArray.ReadShort();
        ConsumeUseDelay = byteArray.ReadShort();
        return this;
    }
}

[MessagePortal(TkMsgType.NM_CS_NEWGUIDE)]
public class PK_CS_NEWGUIDE : BaseRequest
{
    public int missionId1;
    public int present1;
    public int missionId2;
    public int present2;
    public int missionId3;
    public int present3;
    public long guide;

    public PK_CS_NEWGUIDE() => MsgType = TkMsgType.NM_CS_NEWGUIDE;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        missionId1 = byteArray.ReadByte();
        present1 = byteArray.ReadByte();
        missionId2 = byteArray.ReadByte();
        present2 = byteArray.ReadByte();
        missionId3 = byteArray.ReadByte();
        present3 = byteArray.ReadByte();
        guide = byteArray.ReadLong();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte((byte)missionId1);
        byteArray.WriteByte((byte)present1);
        byteArray.WriteByte((byte)missionId2);
        byteArray.WriteByte((byte)present2);
        byteArray.WriteByte((byte)missionId3);
        byteArray.WriteByte((byte)present3);
        byteArray.WriteLong(guide);
    }
}

[MessagePortal(TkMsgType.NM_SC_NEWGUIDE)]
public class PK_SC_NEWGUIDE : BaseRequest
{
    public int missionId1;
    public int present1;
    public int missionId2;
    public int present2;
    public int missionId3;
    public int present3;
    public long guide;

    public PK_SC_NEWGUIDE() => MsgType = TkMsgType.NM_SC_NEWGUIDE;

    public override IRequest ReadByte(ByteArray byteArray)
    {
        missionId1 = byteArray.ReadByte();
        present1 = byteArray.ReadByte();
        missionId2 = byteArray.ReadByte();
        present2 = byteArray.ReadByte();
        missionId3 = byteArray.ReadByte();
        present3 = byteArray.ReadByte();
        guide = byteArray.ReadLong();
        return this;
    }

    public override void WriteByte(ByteArray byteArray)
    {
        byteArray.WriteByte((byte)missionId1);
        byteArray.WriteByte((byte)present1);
        byteArray.WriteByte((byte)missionId2);
        byteArray.WriteByte((byte)present2);
        byteArray.WriteByte((byte)missionId3);
        byteArray.WriteByte((byte)present3);
        byteArray.WriteLong(guide);
    }
}


public class CorpsTankItem
{
    public int TankId;
    public byte Nation;
    public byte Tier;
    public int ExpCost;
    public int LoyaltyCost;
    public byte StudyStatus;
    public int StudyProgress;
}

// PK_HC_Corps_TankList — sends study state for each tank that any member has studied.
// Client reads (PK_HC_Corps_TankStudyStateListRes):
//   Count(int), then per entry:
//     CorpsTankId(int), PlayerTankGuid(long), PlayerGuid(long),
//     TimeType(byte: 0=Forever,1=Time,2=Time2), StudyState(byte), PassStudyTime(long)
//
// IMPORTANT: Previously this sent wrong format (PlayerLoyalty+PlayerCorpsExp+tank data)
// which the client read as Count=PlayerLoyalty, then tried to read that many entries →
// stream corruption, breaking roulette, legion tabs, and battle entry.
public class PK_HC_Corps_TankList : BaseRequest
{
    public record StudyEntry(int TankId, long PlayerTankGuid, long PlayerGuid,
                              byte TimeType, byte StudyState, long PassStudyTime);
    public List<StudyEntry> Entries = new();

    public PK_HC_Corps_TankList() => MsgType = TkMsgType.NM_HC_Corps_TankList;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(Entries.Count);
        foreach (var e in Entries)
        {
            ba.WriteInt(e.TankId);
            ba.WriteLong(e.PlayerTankGuid);
            ba.WriteLong(e.PlayerGuid);
            ba.WriteByte(e.TimeType);   // 0=Forever
            ba.WriteByte(e.StudyState); // 2=studied
            ba.WriteLong(e.PassStudyTime);
        }
    }
}

public class PK_HC_Corps_StudyTankRes : BaseRequest
{
    public int CorpsTankId;
    public byte TroopsTankTimeTypeRaw = 0;
    public byte RetFlag;
    public PK_HC_Corps_StudyTankRes() => MsgType = TkMsgType.NM_HC_Corps_DevelopTank_Res;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(CorpsTankId);
        ba.WriteByte(TroopsTankTimeTypeRaw);
        ba.WriteByte(RetFlag);
    }
}


// Corps application result: guid=corpsId, flag=1 success/0 fail
public class PK_HC_Corps_Application_Res : BaseRequest
{
    public long CorpsGuid;
    public byte Flag;
    public PK_HC_Corps_Application_Res() => MsgType = TkMsgType.NM_HC_Corps_Application_Res;
    public override IRequest ReadByte(ByteArray ba) { CorpsGuid = ba.ReadLong(); Flag = ba.ReadByte(); return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteLong(CorpsGuid); ba.WriteByte(Flag); }
}

public class PK_HC_Corps_IntelligenceList : BaseRequest
{
    public int CorpsId;
    public PK_HC_Corps_IntelligenceList() => MsgType = TkMsgType.NM_HC_Corps_IntelligenceList;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteInt(CorpsId); ba.WriteInt(0); }
}

public class PK_HC_Corps_PrevTankList : BaseRequest
{
    public PK_HC_Corps_PrevTankList() => MsgType = TkMsgType.NM_HC_Corps_PrevTankList;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteInt(0); }
}

public class PK_HC_CorpsAth_RankList_Res : BaseRequest
{
    public PK_HC_CorpsAth_RankList_Res() => MsgType = TkMsgType.NM_HC_CorpsAth_RankList_Res;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteInt(0); ba.WriteShort(0); ba.WriteByte(0); ba.WriteInt(0); }
}

public class PK_HC_CorpsAth_AwardList_Res : BaseRequest
{
    public PK_HC_CorpsAth_AwardList_Res() => MsgType = TkMsgType.NM_HC_CorpsAth_AwardList_Res;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteByte(0); ba.WriteByte(0); ba.WriteByte(0); ba.WriteByte(0); ba.WriteInt(0); ba.WriteShort(0); ba.WriteInt(0); }
}

public class PK_HC_Corps_MoneyChangeList : BaseRequest
{
    public long CorpsId; public long PlayerGuid;
    public PK_HC_Corps_MoneyChangeList() => MsgType = TkMsgType.NM_WHC_Corps_MoneyChangeList;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteLong(CorpsId); ba.WriteInt(0); ba.WriteInt(0); ba.WriteLong(PlayerGuid); ba.WriteInt(0); }
}

public class PK_HC_Corps_MoneyChangeList2 : BaseRequest
{
    public long CorpsId; public long PlayerGuid;
    public PK_HC_Corps_MoneyChangeList2() => MsgType = TkMsgType.NM_WHC_Corps_MoneyChangeList2;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteLong(CorpsId); ba.WriteInt(0); ba.WriteInt(0); ba.WriteLong(PlayerGuid); ba.WriteInt(0); }
}

public class PK_WHC_CorpsLevel_upgrade_Res : BaseRequest
{
    public byte Flag; public int NewLevel;
    public PK_WHC_CorpsLevel_upgrade_Res() => MsgType = TkMsgType.NM_WHC_CorpsLevel_upgrade_Res;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) { ba.WriteByte(Flag); ba.WriteInt(NewLevel); }
}

// Server -> a player: authoritative currency/score update. We use it to drive SP
// (CurScore + CurSkillCoin) so it reflects kills (+500 each) instead of the client's
// own damage-based drip.
[MessagePortal(TkMsgType.NM_RC_PVE_Cost)]
public class PK_RC_PVE_Cost : BaseRequest
{
    public int CurScore, CurSliver, CurGold, CurSkillCoin;
    public int CurFreeSkillOfBomber, CurFreeSkillOfKatyusha;
    public PK_RC_PVE_Cost() => MsgType = TkMsgType.NM_RC_PVE_Cost;
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(CurScore); ba.WriteInt(CurSliver); ba.WriteInt(CurGold);
        ba.WriteInt(CurSkillCoin);
        ba.WriteInt(CurFreeSkillOfBomber); ba.WriteInt(CurFreeSkillOfKatyusha);
    }
}

// ── PVE Defense mode packets ─────────────────────────────────────────────────

public class PK_RC_PVE_InitData : BaseRequest
{
    public int CurSliver, CurGold, CurSkillCoin;
    public int CurFreeSkillOfBomber, CurFreeSkillOfKatyusha;
    public int TotalRound, LimitEnemyNum, InitLife, LimitScore, KatyushaRadius;
    public PK_RC_PVE_InitData() => MsgType = TkMsgType.NM_RC_PVE_InitData;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteInt(CurSliver); ba.WriteInt(CurGold); ba.WriteInt(CurSkillCoin);
        ba.WriteInt(CurFreeSkillOfBomber); ba.WriteInt(CurFreeSkillOfKatyusha);
        ba.WriteInt(TotalRound); ba.WriteInt(LimitEnemyNum); ba.WriteInt(InitLife);
        ba.WriteInt(LimitScore); ba.WriteInt(KatyushaRadius);
    }
}

public class PK_RC_PVE_GameBegin : BaseRequest
{
    public int NullData;
    public PK_RC_PVE_GameBegin() => MsgType = TkMsgType.NM_RC_PVE_GameBegin;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(NullData);
}

public class PK_RC_PVE_GameEnd : BaseRequest
{
    public int NullData;
    public PK_RC_PVE_GameEnd() => MsgType = TkMsgType.NM_RC_PVE_GameEnd;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(NullData);
}

public class PK_RC_PVE_RoundBegin : BaseRequest
{
    public int ReadyTime, CurRound, CurRoundCD;
    public PK_RC_PVE_RoundBegin() => MsgType = TkMsgType.NM_RC_PVE_RoundBegin;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteInt(ReadyTime); ba.WriteInt(CurRound); ba.WriteInt(CurRoundCD); }
}

public class PK_RC_PVE_RoundEnd : BaseRequest
{
    public int NullData;
    public PK_RC_PVE_RoundEnd() => MsgType = TkMsgType.NM_RC_PVE_RoundEnd;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(NullData);
}

public class PK_RC_PVE_Life : BaseRequest
{
    public int CurLife;
    public PK_RC_PVE_Life() => MsgType = TkMsgType.NM_RC_PVE_Life;
    public override IRequest ReadByte(ByteArray ba) { CurLife = ba.ReadInt(); return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(CurLife);
}

public class PK_RC_PVE_Enemy : BaseRequest
{
    public int CurEnemyNum;
    public PK_RC_PVE_Enemy() => MsgType = TkMsgType.NM_RC_PVE_Enemy;
    public override IRequest ReadByte(ByteArray ba) { CurEnemyNum = ba.ReadInt(); return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(CurEnemyNum);
}

// PVE_CreateMonster — uses ReadPlayerInfoByDictate layout on the client side
// All fields are primitives so Protocol has no dependency on the Server assembly.
public class PK_RC_PVE_CreateMonster : BaseRequest
{
    public byte  Group;
    public long  PlayerGUID;
    public string PlayerName = string.Empty;
    public float PosX, PosY, PosZ;
    public float RotX, RotY, RotZ;
    public int   MaxHp, CurHp;
    public ushort TankID, HullID, UnderpanID, TurretID, MainGunID;
    public ushort EngineID, FueltankID, CartridgeID, RadioStationID, GaugeID;
    public float MaxForwardSpeedMps, MaxBackSpeedMps;

    public PK_RC_PVE_CreateMonster() => MsgType = TkMsgType.NM_RC_PVE_CreateMonster;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteByte(Group);
        ba.WriteLong(0L);
        ba.WriteUTF(PlayerName, 64);
        ba.WriteLong(PlayerGUID);
        ba.WriteByte(0);
        ba.WriteUTF(string.Empty, 11);
        ba.WriteUShort(0);                               // TankCoatId
        ba.WriteFloat(PosX); ba.WriteFloat(PosY); ba.WriteFloat(PosZ);
        ba.WriteFloat(RotX); ba.WriteFloat(RotY); ba.WriteFloat(RotZ);
        ba.WriteInt(MaxHp); ba.WriteInt(CurHp);
        ba.WriteUShort(TankID);  ba.WriteUShort(HullID);  ba.WriteUShort(UnderpanID);
        ba.WriteUShort(TurretID); ba.WriteUShort(MainGunID); ba.WriteUShort(EngineID);
        ba.WriteUShort(FueltankID); ba.WriteUShort(CartridgeID);
        ba.WriteUShort(RadioStationID); ba.WriteUShort(GaugeID);
        ba.WriteByte(1);    // IsVisible
        ba.WriteByte(0);    // FightCite
        ba.WriteFloat(50f); ba.WriteFloat(500f);
        ba.WriteFloat(MaxForwardSpeedMps);
        ba.WriteFloat(MaxBackSpeedMps);
        ba.WriteFloat(40f);
        ba.WriteFloat(1f); ba.WriteFloat(1f); ba.WriteFloat(1f); ba.WriteFloat(1f);
        ba.WriteByte(0);    // IsVip
        ba.WriteInt(0); ba.WriteInt(0);
        ba.WriteInt(0); ba.WriteInt(0); ba.WriteInt(0); ba.WriteInt(0);
        ba.WriteShort(0); ba.WriteShort(0); ba.WriteShort(0);
        ba.WriteInt(0);
    }
}

public class PK_RC_PVE_DestroyMonster : BaseRequest
{
    public long PlayerGUID;
    public PK_RC_PVE_DestroyMonster() => MsgType = TkMsgType.NM_RC_PVE_DestroyMonster;
    public override IRequest ReadByte(ByteArray ba) { PlayerGUID = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteLong(PlayerGUID);
}

// PK_RC_BeLighted — make specific enemies visible (spotted). Client reads: byte count + N×long GUIDs.
public class PK_RC_BeLighted : BaseRequest
{
    public long[] PlayerGuids = Array.Empty<long>();
    public PK_RC_BeLighted() => MsgType = TkMsgType.NM_RC_BeLighted;
    public override IRequest ReadByte(ByteArray ba)
    { int n = ba.ReadByte(); PlayerGuids = new long[n]; for (int i = 0; i < n; i++) PlayerGuids[i] = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteByte((byte)Math.Min(PlayerGuids.Length, 255)); foreach (var g in PlayerGuids.Take(255)) ba.WriteLong(g); }
}

// PK_RC_BeDarked — CLIENT READS NOTHING (no ReadByte override in decompiled code).
// Sending any payload bytes would corrupt the stream. Send as empty packet only.
// Use PK_RC_TankHide instead when you need to hide specific tanks.
public class PK_RC_BeDarked : BaseRequest
{
    public PK_RC_BeDarked() => MsgType = TkMsgType.NM_RC_BeDarked;
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba) { /* intentionally empty — client reads zero bytes */ }
}

// PK_RC_TankHide — hide specific tanks. Client reads: byte count + N×long GUIDs.
// Use this (not BeDarked) when you need to hide specific enemy tanks.
public class PK_RC_TankHide : BaseRequest
{
    public long[] PlayerGuidList = Array.Empty<long>();
    public PK_RC_TankHide() => MsgType = TkMsgType.NM_RC_TankHide;
    public override IRequest ReadByte(ByteArray ba)
    { byte n = ba.ReadByte(); PlayerGuidList = new long[n]; for (int i = 0; i < n; i++) PlayerGuidList[i] = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteByte((byte)Math.Min(PlayerGuidList.Length, 255)); foreach (var g in PlayerGuidList.Take(255)) ba.WriteLong(g); }
}

// ── Corps fast-develop (Ускорить) packets ─────────────────────────────────────
// Client sends CorpsTankId when clicking "Ускорить" (Accelerate study)
[MessagePortal(TkMsgType.NM_CH_Corps_FastDevelop)]
public class PK_CH_Corps_SpeedStudyTank : BaseRequest
{
    public int CorpsTankId;
    public PK_CH_Corps_SpeedStudyTank() => MsgType = TkMsgType.NM_CH_Corps_FastDevelop;
    public override IRequest ReadByte(ByteArray ba) { CorpsTankId = ba.ReadInt(); return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteInt(CorpsTankId);
}

// Server response to corps fast-develop (Ускорить).
// Client reads: CorpsTankId(int) + RetFlag(byte)
// RetFlag=1 → client jumps state to WaitForPick (3)
public class PK_HC_Corps_SpeedStudyTankRes : BaseRequest
{
    public int  CorpsTankId;
    public byte RetFlag;
    public PK_HC_Corps_SpeedStudyTankRes() => MsgType = (TkMsgType)368; // NM_HC_Corps_FastDevelop_Res
    public override IRequest ReadByte(ByteArray ba) { return this; }
    public override void WriteByte(ByteArray ba)
    { ba.WriteInt(CorpsTankId); ba.WriteByte(RetFlag); }
}

// ── Postmortem (DeathCamera) spectate switch packets ─────────────────────────
[MessagePortal(TkMsgType.NM_CR_DeadCameroOnFriend)]
public class PK_CR_DeadCameroOnFriend : BaseRequest
{
    public long FriendGuid;
    public PK_CR_DeadCameroOnFriend() => MsgType = TkMsgType.NM_CR_DeadCameroOnFriend;
    public override IRequest ReadByte(ByteArray ba) { FriendGuid = ba.ReadLong(); return this; }
    public override void WriteByte(ByteArray ba) => ba.WriteLong(FriendGuid);
}

[MessagePortal(TkMsgType.NM_RC_DeadCameroOnFriend)]
public class PK_RC_DeadCameroOnFriend : BaseRequest
{
    public long PlayerGuid;
    public float CircularVisionRadiusRange;
    public PK_RC_DeadCameroOnFriend() => MsgType = TkMsgType.NM_RC_DeadCameroOnFriend;
    public override IRequest ReadByte(ByteArray ba)
    {
        PlayerGuid = ba.ReadLong();
        CircularVisionRadiusRange = ba.ReadFloat();
        return this;
    }
    public override void WriteByte(ByteArray ba)
    {
        ba.WriteLong(PlayerGuid);
        ba.WriteFloat(CircularVisionRadiusRange);
    }
}
