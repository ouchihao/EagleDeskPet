namespace DuckDeskPet.Core;

/// <summary>
/// Authored action clips and continuous scene phases. Ordinary actions return
/// to standing; work phases meet at explicitly authored scene endpoints.
/// </summary>
public enum ClipKind
{
    Idle = 0,
    SideEye = 1,
    Bomb = 2,
    Yawn = 3,
    Shy = 4,
    Wiggle = 5,
    Hop = 6,
    Nod = 7,
    Shimmy = 8,
    Stretch = 9,
    Eat = 11,
    WorkEnter = 20,
    WorkLoop = 21,
    WorkToBusy = 22,
    BusyLoop = 23,
    WorkExit = 24,
    BusyExit = 25,
    HungryEnter = 40,
    HungryLoop = 41,
    HungryExit = 42,
    Tea = 50,
    RpsRock = 51,
    RpsPaper = 52,
    RpsScissors = 53,
    RpsWin = 54,
    RpsLose = 55,
    Annoyed = 56,
}
