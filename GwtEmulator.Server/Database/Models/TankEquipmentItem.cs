namespace GwtEmulator.Server.Database.Models;

public class TankEquipmentItem
{
    public long Id { get; set; }
    public long TankGuid { get; set; }
    public int SlotIndex { get; set; }
    public int EquipmentId { get; set; }
}
