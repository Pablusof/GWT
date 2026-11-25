namespace GwtEmulator.Server.Database.Models;

public class CrewMember
{
    public long Id { get; set; }
    public long CrewGuid { get; set; }
    public long PlayerGUID { get; set; }
    public long TankGuid { get; set; }
    public int CrewId { get; set; }
    public short FamilyNameId { get; set; }
    public short NameId { get; set; }
    public short BigIconId { get; set; }
    public short SmallIconId { get; set; }
    public short ResumeId { get; set; }
    public int CurExp { get; set; }
    public int CurLv { get; set; } = 1;
    public int Lastlearn { get; set; }
    public int LastSkillExp { get; set; }
    public int LastExp { get; set; }
    public int LastLv { get; set; }
    public string SkillIds { get; set; } = "";
    public bool IsEquipped { get; set; }
}
