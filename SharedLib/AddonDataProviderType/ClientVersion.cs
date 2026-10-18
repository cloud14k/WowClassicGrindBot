namespace SharedLib;

public enum ClientVersion
{
    None = 0,
    Retail = 1,
    SoM = 2,
    TBC = 5,
    Wrath = 11,
    Cata = 14,
    Mop = 19,
    // Process identity for the Forever product; the addon itself reports the
    // Classic project id, so this value is not written by DataToColor.
    Forever = 20,
    //
    Legacy_Vanilla = 91,
    Legacy_TBC = 92,
    Legacy_Wrath = 93,
    Legacy_Cata = 94,
    Legacy_Mop = 95,
}
