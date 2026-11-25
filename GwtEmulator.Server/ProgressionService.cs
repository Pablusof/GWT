using System;
using System.Collections.Generic;

namespace GwtEmulator.Server;

public class ProgressionService
{
    private static readonly Dictionary<int, int> _xpPerLevel = new();

    static ProgressionService()
    {
        int xp = 0;
        for (int lvl = 1; lvl <= 100; lvl++)
        {
            xp += 500 + (lvl - 1) * 200;
            _xpPerLevel[lvl] = xp;
        }
    }

    public static int GetXpForLevel(int level)
    {
        if (_xpPerLevel.TryGetValue(level, out int xp)) return xp;
        return 500 + (level - 1) * 200;
    }

    public static int GetLevelForXp(int totalXp)
    {
        int level = 1;
        foreach (var kvp in _xpPerLevel)
        {
            if (totalXp < kvp.Value) return kvp.Key;
            level = kvp.Key + 1;
        }
        return 100;
    }

    public static int GetTankXpForLevel(int level)
    {
        return 200 + (level - 1) * 100;
    }

    public static int CalculateBattleCap(int level, int tankCount, int crewCount)
    {
        return level * 100 + tankCount * 50 + crewCount * 30;
    }

    public static (int Silver, int Gold, int Exp) CalculateBattleRewards(int tier, bool isWin, int damage, int kills, bool isPremium, bool isVip)
    {
        float multiplier = isWin ? 1.5f : 1.0f;
        if (isPremium) multiplier *= 2.0f;
        if (isVip) multiplier *= 1.2f;

        int baseSilver = 100 + tier * 50 + damage / 10 + kills * 100;
        int baseExp = 50 + tier * 20 + damage / 20 + kills * 50;
        int baseGold = isWin ? 10 + tier * 5 : 5;

        return (
            (int)(baseSilver * multiplier),
            (int)(baseGold * multiplier),
            (int)(baseExp * multiplier)
        );
    }

    public static int CalculateRepairCost(int tier, int hpDeficit)
    {
        return (int)(hpDeficit * (5 + tier * 2));
    }

    public static int CalculateSellPrice(int originalCost)
    {
        return (int)(originalCost * 0.5f);
    }
}
