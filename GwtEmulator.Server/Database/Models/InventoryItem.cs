namespace GwtEmulator.Server.Database.Models;

public class InventoryItem
{
    public long Id { get; set; }
    public long PlayerGUID { get; set; }
    public byte ItemType { get; set; }
    public int ItemId { get; set; }
    public int ItemNum { get; set; }
    public byte ItemFlag { get; set; }
}
