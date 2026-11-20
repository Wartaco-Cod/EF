namespace TacoFactory.Core.Sim;

public enum SimEventType : byte
{
    Damage = 0, Heal = 1, Death = 2, LevelUp = 3, MasteryUp = 4, LootPicked = 5, ZoneCleared = 6, Respawn = 7, SkillUsed = 8, SkillLearned = 9, Blocked = 10, Revived = 11, Buffed = 12,
    DuelStarted = 13, DuelEnded = 14, Telegraph = 15, BossPhase = 16, UniqueKilled = 17,
    /// <summary>A boss yielded: the server puts the choice to the party.</summary>
    Yielded = 18,
    /// <summary>A disguise broke and the guards are coming.</summary>
    Alarm = 19,
    /// <summary>A worn rune gained a level (Value = level, Text = rune name).</summary>
    RuneUp = 20,
    CofferOpened = 21,
    HuntsmanSlain = 22,
    /// <summary>Threat is full: the Grey Huntsman comes for the knight named in Text.</summary>
    Hunted = 23,
    PitFloor = 24,
    PitFloorCleared = 25,
    /// <summary>A rune dropped into a knight's pouch (Text = rune name).</summary>
    RuneFound = 26,
}

/// <summary>Something that happened during a tick, sent to clients for effects, numbers and the log.</summary>
public readonly record struct SimEvent(SimEventType Type, int Source, int Target, int Value, string Text = "");
