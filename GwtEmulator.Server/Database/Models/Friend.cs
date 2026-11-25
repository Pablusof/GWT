namespace GwtEmulator.Server.Database.Models;

public class Friend
{
    public long Id { get; set; }
    public long PlayerGUID { get; set; }
    public long FriendGUID { get; set; }
    public string FriendName { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public int FriendLevel { get; set; }
    public byte RelationType { get; set; }
}
