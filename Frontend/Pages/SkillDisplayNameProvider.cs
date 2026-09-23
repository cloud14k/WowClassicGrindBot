namespace Frontend.Pages;

/// <summary>UI-only translations for class action names. Combat and profile data remain English.</summary>
public static class SkillDisplayNameProvider
{
    private static readonly Dictionary<string, string> ChineseNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Frostbolt"] = "寒冰箭", ["Fireball"] = "火球术", ["Frost Armor"] = "冰甲术",
            ["Arcane Intellect"] = "奥术智慧", ["Fire Blast"] = "火焰冲击", ["Frost Nova"] = "冰霜新星",
            ["Conjure Food"] = "制造食物", ["Conjure Water"] = "制造水", ["Shoot"] = "射击",
            ["Drink"] = "喝水", ["Food"] = "吃食物", ["Charge"] = "冲锋",
            ["Heroic Strike"] = "英勇打击", ["Rend"] = "撕裂", ["Battle Shout"] = "战斗怒吼",
            ["Sinister Strike"] = "邪恶攻击", ["Eviscerate"] = "剔骨", ["Stealth"] = "潜行",
            ["Serpent Sting"] = "毒蛇钉刺", ["Arcane Shot"] = "奥术射击", ["Aimed Shot"] = "瞄准射击",
            ["Aspect of the Hawk"] = "雄鹰守护", ["Healing Touch"] = "治疗之触", ["Wrath"] = "愤怒",
            ["Moonfire"] = "月火术", ["Rejuvenation"] = "回春术", ["Smite"] = "惩击",
            ["Renew"] = "恢复", ["Shadow Word: Pain"] = "暗言术：痛", ["Lightning Bolt"] = "闪电箭",
            ["Earth Shock"] = "地震术", ["Flame Shock"] = "烈焰震击", ["Holy Light"] = "圣光术",
            ["Judgement"] = "审判", ["Crusader Strike"] = "十字军打击", ["Auto Shot"] = "自动射击"
        };

    public static string ChineseName(string? englishName) =>
        !string.IsNullOrWhiteSpace(englishName) && ChineseNames.TryGetValue(englishName, out string? chinese)
            ? chinese
            : "-";
}
