using System;
using System.Text;

namespace GwtEmulator.Protocol;

public class ByteArray
{
    private byte[] _buffer;
    private int _position;
    private int _length;

    public byte[] Bytes
    {
        get => _buffer;
        set { _buffer = value; _position = 0; _length = value.Length; }
    }

    public int BytesAvailable => _length - _position;
    public int Length
    {
        get => _length;
        set
        {
            if (_length != value && value >= 0)
            {
                if (value > _buffer.Length) CheckAndExpandIfNeeded(value, 0);
                else _length = value;
                if (_position > _length) _position = _length;
            }
        }
    }
    public int Position
    {
        get => _position;
        set => _position = value;
    }

    public ByteArray() { _position = 0; _length = 0; _buffer = Array.Empty<byte>(); }
    public ByteArray(int size) { _position = 0; _length = size; _buffer = new byte[size]; }
    public ByteArray(byte[] buf) { _position = 0; _buffer = buf; _length = buf.Length; }

    public bool ReadBool() => _buffer[_position++] == 1;
    public byte ReadByte() => _buffer[_position++];
    public sbyte ReadSByte() => (sbyte)_buffer[_position++];

    public byte[] ReadBytes(int count)
    {
        byte[] array = new byte[count];
        Buffer.BlockCopy(_buffer, _position, array, 0, count);
        _position += count;
        return array;
    }

    public double ReadDouble()
    {
        double result = BitConverter.ToDouble(_buffer, _position);
        _position += 8;
        return result;
    }

    public float ReadFloat()
    {
        float result = BitConverter.ToSingle(_buffer, _position);
        _position += 4;
        return result;
    }

    public short ReadShort() => (short)(_buffer[_position++] | (_buffer[_position++] << 8));
    public ushort ReadUShort() => (ushort)ReadShort();

    public int ReadInt() => _buffer[_position++] | (_buffer[_position++] << 8) | (_buffer[_position++] << 16) | (_buffer[_position++] << 24);

    public long ReadLong() => (long)(_buffer[_position++] | ((ulong)_buffer[_position++] << 8) | ((ulong)_buffer[_position++] << 16) | ((ulong)_buffer[_position++] << 24) | ((ulong)_buffer[_position++] << 32) | ((ulong)_buffer[_position++] << 40) | ((ulong)_buffer[_position++] << 48) | ((ulong)_buffer[_position++] << 56));

    public uint ReadUInt() => (uint)ReadInt();

    public string ReadUTF()
    {
        ushort num = ReadUShort();
        string result = Encoding.UTF8.GetString(_buffer, _position, num - 1);
        _position += num + 1;
        return result;
    }

    public string ReadUTF(int length)
    {
        int num = BytesAvailable;
        int limit = length > -1 ? Math.Min(_position + length, num + _position) : num;
        int strLen = 0;
        int pos = _position;
        while (pos < limit && _buffer[pos++] != 0) strLen++;
        string result = strLen > 0 ? Encoding.UTF8.GetString(_buffer, _position, strLen) : string.Empty;
        if (length > -1) _position = limit;
        return result;
    }

    private void CheckAndExpandIfNeeded(int length, int position = -1)
    {
        if (position == -1) position = _position;
        if (position + length > _buffer.Length)
        {
            _length = position + length;
            byte[] array = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, array, 0, _buffer.Length);
            _buffer = array;
        }
    }

    public void WriteBool(bool value) => WriteByte((byte)(value ? 1 : 0));

    public void WriteByte(byte value)
    {
        CheckAndExpandIfNeeded(1);
        _buffer[_position++] = value;
        if (_position > _length) _length = _position;
    }

    public void WriteSByte(sbyte value)
    {
        CheckAndExpandIfNeeded(1);
        _buffer[_position++] = (byte)value;
        if (_position > _length) _length = _position;
    }

    public void WriteDouble(double value)
    {
        CheckAndExpandIfNeeded(8);
        long num = BitConverter.ToInt64(BitConverter.GetBytes(value), 0);
        _buffer[_position++] = (byte)num;
        _buffer[_position++] = (byte)(num >> 8);
        _buffer[_position++] = (byte)(num >> 16);
        _buffer[_position++] = (byte)(num >> 24);
        _buffer[_position++] = (byte)(num >> 32);
        _buffer[_position++] = (byte)(num >> 40);
        _buffer[_position++] = (byte)(num >> 48);
        _buffer[_position++] = (byte)(num >> 56);
        if (_position > _length) _length = _position;
    }

    public void WriteFloat(float value)
    {
        CheckAndExpandIfNeeded(4);
        int num = BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        _buffer[_position++] = (byte)num;
        _buffer[_position++] = (byte)(num >> 8);
        _buffer[_position++] = (byte)(num >> 16);
        _buffer[_position++] = (byte)(num >> 24);
        if (_position > _length) _length = _position;
    }

    public void WriteInt(int value)
    {
        CheckAndExpandIfNeeded(4);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)(value >> 16);
        _buffer[_position++] = (byte)(value >> 24);
        if (_position > _length) _length = _position;
    }

    public void WriteUInt(uint value)
    {
        CheckAndExpandIfNeeded(4);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)(value >> 8);
        _buffer[_position++] = (byte)(value >> 16);
        _buffer[_position++] = (byte)(value >> 24);
        if (_position > _length) _length = _position;
    }

    public void WriteLong(long value)
    {
        CheckAndExpandIfNeeded(8);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)((ulong)value >> 8);
        _buffer[_position++] = (byte)((ulong)value >> 16);
        _buffer[_position++] = (byte)((ulong)value >> 24);
        _buffer[_position++] = (byte)((ulong)value >> 32);
        _buffer[_position++] = (byte)((ulong)value >> 40);
        _buffer[_position++] = (byte)((ulong)value >> 48);
        _buffer[_position++] = (byte)((ulong)value >> 56);
        if (_position > _length) _length = _position;
    }

    public void WriteShort(short value)
    {
        CheckAndExpandIfNeeded(2);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)(value >> 8);
        if (_position > _length) _length = _position;
    }

    public void WriteUShort(ushort value)
    {
        CheckAndExpandIfNeeded(2);
        _buffer[_position++] = (byte)value;
        _buffer[_position++] = (byte)(value >> 8);
        if (_position > _length) _length = _position;
    }

    public void WriteUTF(string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        int num = bytes.Length;
        if (num > 32768) throw new FormatException("String length cannot be greater then 32768");
        WriteUShort((ushort)(num + 1));
        WriteBytes(bytes);
        WriteByte(0);
    }

    public void WriteBytes(byte[] data) => WriteBytes(data, 0, data.Length);

    private void WriteBytes(byte[] src, int srcOffset, int length)
    {
        CheckAndExpandIfNeeded(length);
        Buffer.BlockCopy(src, srcOffset, _buffer, _position, length);
        _position += length;
        if (_position > _length) _length = _position;
    }

    public void WriteUTF(string str, int count)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        int num = bytes.Length;
        if (num > 32768) throw new FormatException("String length cannot be greater then 32768");
        int padding = 0;
        if (count < num) num = count;
        else padding = count - num;
        WriteBytes(bytes, 0, num);
        if (padding > 0)
        {
            CheckAndExpandIfNeeded(padding);
            while (padding-- > 0) _buffer[_position++] = 0;
            if (_position > _length) _length = _position;
        }
    }

    public void WriteVersion(ushort temp = 0)
    {
        WriteUShort(temp);
        WriteUShort(2);
        WriteUShort(12764);
        WriteUShort(99);
    }

    public TankData ReadTankData()
    {
        var tank = new TankData();
        tank.TankGUID = ReadLong();
        tank.PlayerGUID = ReadLong();
        tank.CarportIndex = ReadInt();
        tank.VehicleId = ReadInt();
        tank.HullID = ReadInt();
        tank.UnderpanID = ReadInt();
        tank.TurretID = ReadInt();
        tank.MainGunID = ReadInt();
        tank.EngineID = ReadInt();
        tank.FueltankID = ReadInt();
        tank.CartridgeID = ReadInt();
        tank.RadioStationID = ReadInt();
        tank.GaugeID = ReadInt();
        for (int i = 0; i < 3; i++) { tank.ShellSlots[i].ID = ReadInt(); tank.ShellSlots[i].Count = ReadInt(); }
        tank.TankHp = ReadInt();
        tank.TankExp = ReadInt();
        for (int i = 0; i < 3; i++) { tank.DeployShellSlots[i].ID = ReadInt(); tank.DeployShellSlots[i].Count = ReadInt(); }
        for (int i = 0; i < 3; i++) tank.Provisions[i] = ReadInt();
        for (int i = 0; i < 3; i++) tank.DeployProvisions[i] = ReadInt();
        tank.AutoRepair = ReadByte();
        tank.TankCurrencytType = ReadByte();
        tank.GoldTankLeftTime = ReadInt();
        tank.TankCurHp = ReadInt();
        for (int i = 0; i < 5; i++) { tank.BehaveArray[i].BehaveType = ReadByte(); tank.BehaveArray[i].PrizeType = ReadByte(); tank.BehaveArray[i].Multiple = ReadFloat(); }
        for (int i = 0; i < 2; i++) { tank.SignatureId[i] = ReadInt(); tank.SignatureTime[i] = ReadLong(); }
        for (int i = 0; i < 4; i++) { tank.EmblemId[i] = ReadInt(); tank.EmblemTime[i] = ReadLong(); }
        tank.ModificationId = ReadInt();
        tank.IsElite = ReadByte() == 1;
        tank.AutoFillCostFlag = ReadByte();
        return tank;
    }

    public void WriteTankData(TankData tank)
    {
        WriteLong(tank.TankGUID);
        WriteLong(tank.PlayerGUID);
        WriteInt(tank.CarportIndex);
        WriteInt(tank.VehicleId);
        WriteInt(tank.HullID);
        WriteInt(tank.UnderpanID);
        WriteInt(tank.TurretID);
        WriteInt(tank.MainGunID);
        WriteInt(tank.EngineID);
        WriteInt(tank.FueltankID);
        WriteInt(tank.CartridgeID);
        WriteInt(tank.RadioStationID);
        WriteInt(tank.GaugeID);
        for (int i = 0; i < 3; i++) { WriteInt(tank.ShellSlots[i].ID); WriteInt(tank.ShellSlots[i].Count); }
        WriteInt(tank.TankHp);
        WriteInt(tank.TankExp);
        for (int i = 0; i < 3; i++) { WriteInt(tank.DeployShellSlots[i].ID); WriteInt(tank.DeployShellSlots[i].Count); }
        for (int i = 0; i < 3; i++) WriteInt(tank.Provisions[i]);
        for (int i = 0; i < 3; i++) WriteInt(tank.DeployProvisions[i]);
        WriteByte(tank.AutoRepair);
        WriteByte(tank.TankCurrencytType);
        WriteInt(tank.GoldTankLeftTime);
        WriteInt(tank.TankCurHp);
        for (int i = 0; i < 5; i++) { WriteByte(tank.BehaveArray[i].BehaveType); WriteByte(tank.BehaveArray[i].PrizeType); WriteFloat(tank.BehaveArray[i].Multiple); }
        for (int i = 0; i < 2; i++) { WriteInt(tank.SignatureId[i]); WriteLong(tank.SignatureTime[i]); }
        for (int i = 0; i < 4; i++) { WriteInt(tank.EmblemId[i]); WriteLong(tank.EmblemTime[i]); }
        WriteInt(tank.ModificationId);
        WriteByte((byte)(tank.IsElite ? 1 : 0));
        WriteByte(tank.AutoFillCostFlag);
    }

    // Формат сверен с клиентом (ClientSocket/ByteArray.ReadPKTankCoatInfo +
    // ReadTankCoatData) — это источник истины:
    //
    //     int   TankID                       (= Table_Vehicle.id, НЕ TankGUID)
    //     3 ×  { ushort TableID; int OverdueTime }   ← ровно TankOwnCoat.CoatType,
    //                                                   БЕЗ байта длины
    //     byte  CurSelectType                (0..2 — слот, 3 = None)
    //
    // Итого 23 байта. Прошлая версия писала лишний байт count и int вместо ushort
    // под TableID — клиент разъезжался по буферу и молча ронял весь пакет.
    public PK_TankCoatInfo ReadPKTankCoatInfo()
    {
        var info = new PK_TankCoatInfo();
        info.TankID = ReadInt();
        info.TankCoatArray = PK_TankCoatInfo.CreateSlots();
        for (int i = 0; i < PK_TankCoatInfo.CoatSlots; i++)
        {
            info.TankCoatArray[i].TableID     = ReadUShort();
            info.TankCoatArray[i].OverdueTime = ReadInt();
        }
        info.CurSelectType = ReadByte();
        return info;
    }

    public void WritePKTankCoatInfo(PK_TankCoatInfo info)
    {
        WriteInt(info.TankID);
        var slots = info.TankCoatArray;
        for (int i = 0; i < PK_TankCoatInfo.CoatSlots; i++)
        {
            var slot = (slots != null && i < slots.Length && slots[i] != null) ? slots[i] : null;
            WriteUShort(slot?.TableID ?? 0);
            WriteInt(slot?.OverdueTime ?? 0);
        }
        WriteByte(info.CurSelectType);
    }

    public TankEquipmentData ReadTankEquipmentData()
    {
        var data = new TankEquipmentData();
        data.CircularVisionRadiusRange = ReadFloat();
        data.AimcircleSpeed = ReadFloat();
        data.Stable = ReadFloat();
        data.CommanderBuff = ReadFloat();
        data.GunnerBuff = ReadFloat();
        data.DriverBuff = ReadFloat();
        data.LoaderBuff = ReadFloat();
        data.RadiomanBuff = ReadFloat();
        data.MemberExp = ReadFloat();
        data.LoadBuff = ReadFloat();
        data.ChassisHpBuff = ReadFloat();
        data.TurretHpBuff = ReadFloat();
        data.HpBuff = ReadFloat();
        data.FragmentFactor = ReadFloat();
        data.HideFactor = ReadFloat();
        data.ReloadBuff = ReadFloat();
        data.AmmobayNodamgePercentBuff = ReadFloat();
        data.AmmobayHpBuff = ReadFloat();
        data.PowerBuff = ReadFloat();
        data.FueltankNodamgePercentBuff = ReadFloat();
        data.FueltankHpBuff = ReadFloat();
        return data;
    }

    public RoomMainTankData ReadRoomMainTankData()
    {
        var data = new RoomMainTankData();
        data.VehicleId = ReadInt();
        data.HullID = ReadInt();
        data.UnderpanID = ReadInt();
        data.TurretID = ReadInt();
        data.MainGunID = ReadInt();
        data.EngineID = ReadInt();
        data.FueltankID = ReadInt();
        data.CartridgeID = ReadInt();
        data.RadioStationID = ReadInt();
        data.GaugeID = ReadInt();
        for (int i = 0; i < 3; i++) { data.ShellSlots[i].ID = ReadInt(); data.ShellSlots[i].Count = ReadInt(); }
        for (int i = 0; i < 3; i++) data.Provisions[i] = ReadInt();
        for (int i = 0; i < 3; i++) data.DefaultProvisions[i] = ReadInt();
        data.TankHp = ReadInt();
        data.TankCurHp = ReadInt();
        data.TankCoatId = ReadInt();
        for (int i = 0; i < 2; i++) data.TankSignature[i] = ReadInt();
        for (int i = 0; i < 4; i++) data.EmblemIDs[i] = ReadInt();
        return data;
    }

    public void WriteRoomMainTankData(RoomMainTankData data)
    {
        WriteInt(data.VehicleId);
        WriteInt(data.HullID);
        WriteInt(data.UnderpanID);
        WriteInt(data.TurretID);
        WriteInt(data.MainGunID);
        WriteInt(data.EngineID);
        WriteInt(data.FueltankID);
        WriteInt(data.CartridgeID);
        WriteInt(data.RadioStationID);
        WriteInt(data.GaugeID);
        for (int i = 0; i < 3; i++) { WriteInt(data.ShellSlots[i].ID); WriteInt(data.ShellSlots[i].Count); }
        for (int i = 0; i < 3; i++) WriteInt(data.Provisions[i]);
        for (int i = 0; i < 3; i++) WriteInt(data.DefaultProvisions[i]);
        WriteInt(data.TankHp);
        WriteInt(data.TankCurHp);
        WriteInt(data.TankCoatId);
        for (int i = 0; i < 2; i++) WriteInt(data.TankSignature[i]);
        for (int i = 0; i < 4; i++) WriteInt(data.EmblemIDs[i]);
    }

    public TankAllAttributeStruct ReadTankAllAttributeStruct()
    {
        var data = new TankAllAttributeStruct();
        data.TankGUID = ReadLong();
        data.Attributes = new float[57];
        for (int i = 0; i < 57; i++) data.Attributes[i] = ReadFloat();
        return data;
    }

    public void WriteTankAllAttributeStruct(TankAllAttributeStruct data)
    {
        WriteLong(data.TankGUID);
        for (int i = 0; i < 57; i++) WriteFloat(data.Attributes[i]);
    }

    public void WriteTankEquipmentData(TankEquipmentData data)
    {
        WriteFloat(data.CircularVisionRadiusRange);
        WriteFloat(data.AimcircleSpeed);
        WriteFloat(data.Stable);
        WriteFloat(data.CommanderBuff);
        WriteFloat(data.GunnerBuff);
        WriteFloat(data.DriverBuff);
        WriteFloat(data.LoaderBuff);
        WriteFloat(data.RadiomanBuff);
        WriteFloat(data.MemberExp);
        WriteFloat(data.LoadBuff);
        WriteFloat(data.ChassisHpBuff);
        WriteFloat(data.TurretHpBuff);
        WriteFloat(data.HpBuff);
        WriteFloat(data.FragmentFactor);
        WriteFloat(data.HideFactor);
        WriteFloat(data.ReloadBuff);
        WriteFloat(data.AmmobayNodamgePercentBuff);
        WriteFloat(data.AmmobayHpBuff);
        WriteFloat(data.PowerBuff);
        WriteFloat(data.FueltankNodamgePercentBuff);
        WriteFloat(data.FueltankHpBuff);
    }

    // v15: КОРОТКИЙ формат PlayerInfo — ровно то, что читает клиентский
    // ByteArray.ReadPlayerInfo() (его использует PK_RC_NewPlayerLogin id 236 и
    // PK_RC_PlayerList). В отличие от ReadPlayerInfoByDictate() (его использует
    // PK_RC_TankBattleData) здесь НЕТ полей RoomerId(byte) и TroopsTag(11 байт):
    // сразу после PlayerGUID идёт TankCoatId. Полный формат в этом сообщении сдвигал
    // всё на 12 байт, TankID читался из мусора -> GetVehicle(null) -> клиент не мог
    // создать танк («PKPI Bot_1001:-1001 null») и респаун-боты не появлялись.
    public void WritePlayerInfoShort(PK_PlayerInfo info)
    {
        WriteByte(info.Group);
        WriteLong(info.TeamGuid);
        WriteUTF(info.PlayerName, 64);
        WriteLong(info.PlayerGUID);
        WriteUShort(info.TankCoatId);
        WriteFloat(info.PosX);
        WriteFloat(info.PosY);
        WriteFloat(info.PosZ);
        WriteFloat(info.RotX);
        WriteFloat(info.RotY);
        WriteFloat(info.RotZ);
        WriteInt(info.MaxHp);
        WriteInt(info.CurHp);
        WriteUShort(info.TankID);
        WriteUShort(info.HullID);
        WriteUShort(info.UnderpanID);
        WriteUShort(info.TurretID);
        WriteUShort(info.MainGunID);
        WriteUShort(info.EngineID);
        WriteUShort(info.FueltankID);
        WriteUShort(info.CartridgeID);
        WriteUShort(info.RadioStationID);
        WriteUShort(info.GaugeID);
        WriteByte(info.IsVisible);
    }

    public void WritePlayerInfo(PK_PlayerInfo info)
    {
        WriteByte(info.Group);
        WriteLong(info.TeamGuid);
        WriteUTF(info.PlayerName, 64);
        WriteLong(info.PlayerGUID);
        WriteByte(info.RoomerId);
        WriteUTF(info.TroopsTag, 11);
        WriteUShort(info.TankCoatId);
        WriteFloat(info.PosX);
        WriteFloat(info.PosY);
        WriteFloat(info.PosZ);
        WriteFloat(info.RotX);
        WriteFloat(info.RotY);
        WriteFloat(info.RotZ);
        WriteInt(info.MaxHp);
        WriteInt(info.CurHp);
        WriteUShort(info.TankID);
        WriteUShort(info.HullID);
        WriteUShort(info.UnderpanID);
        WriteUShort(info.TurretID);
        WriteUShort(info.MainGunID);
        WriteUShort(info.EngineID);
        WriteUShort(info.FueltankID);
        WriteUShort(info.CartridgeID);
        WriteUShort(info.RadioStationID);
        WriteUShort(info.GaugeID);
        WriteByte(info.IsVisible);
        WriteByte(info.FightCite);
        WriteFloat(info.TankTotalWeight);
        WriteFloat(info.TankPower);
        WriteFloat(info.MaxForwardSpeed);
        WriteFloat(info.MaxBackSpeed);
        WriteFloat(info.RotateSpeed);
        WriteFloat(info.EngineStartTime);
        WriteFloat(info.MoveFastStartTime);
        WriteFloat(info.MoveFastStartRate);
        WriteFloat(info.TerrainResistanceHard);
        WriteByte(info.IsVip);
        WriteInt(info.SignatureId[0]);
        WriteInt(info.SignatureId[1]);
        WriteInt(info.EmblemId[0]);
        WriteInt(info.EmblemId[1]);
        WriteInt(info.EmblemId[2]);
        WriteInt(info.EmblemId[3]);
        WriteShort(info.MovePackNum);
        WriteShort((short)(info.SpwanGunAngle * 100f));
        WriteShort((short)(info.SpwanTurretAngle * 100f));
        WriteInt(info.KillCount);
        // NOTE: The client's ByteArray.ReadPlayerInfoByDictate() ends at KillCount.
        // PK_RC_TankBattleData packs every player into ONE message and reads them in a
        // loop, so any extra bytes per player shift every subsequent player's read and the
        // final read runs off the buffer -> IndexOutOfRangeException that kills the room
        // receive thread (battle hangs on the loading screen). Do NOT append ShellSlots here.
    }
}
