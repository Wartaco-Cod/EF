using System.Text.Json;
using TacoFactory.Core.Sim;

namespace TacoFactory.Net;

/// <summary>Window contents the server sends as JSON in a <see cref="MsgType.Ui"/> message. Kind names are the constants below.</summary>
public static class UiKind
{
    public const string Shop = "shop", Stash = "stash", Forge = "forge", Trade = "trade", Auction = "auction",
        AuctionMine = "auction_mine", Guild = "guild", Close = "close", Cutscene = "cutscene", Choice = "choice", Dialogue = "dialogue",
        Extras = "extras", ForgeFx = "forge_fx", Herald = "herald", Daily = "daily", Armoury = "armoury", Pets = "pets";

    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json);
}

public sealed record ShopEntry(ItemInfo Item, int Price);

/// <summary>A story scene: title and lines, shown full screen until dismissed.</summary>
public sealed record CutsceneView(string Id, string Title, string Image, List<CutsceneLineView> Lines);
public sealed record CutsceneLineView(string Speaker, string Text);

/// <summary>A party vote on a yielded boss. <see cref="Votes"/> counts each option so far.</summary>
public sealed record ChoiceView(string Boss, string Prompt, List<ChoiceOptionView> Options, double SecondsLeft, string? MyVote, Dictionary<string, int> Votes, bool Final, string Result);
public sealed record ChoiceOptionView(string Id, string Text);

/// <summary>An NPC speaking: portrait sprite, name and what they say.</summary>
public sealed record DialogueView(string Npc, string Sprite, string Text, string? Quest);

public sealed record ShopState(string Merchant, List<ShopEntry> Stock, bool SellsPotions, int HealthPrice, int ManaPrice,
    bool Gambles, int GamblePrice, bool BuysGear);

public sealed record StashState(string Title, List<ItemInfo> Items, int Limit, bool Guild);

/// <summary>The forge room's windows. Mode: "forge" (Master Aubry), "runes" (the Runecarver) or "athanor".</summary>
public sealed record ForgeState(string Smith, bool Unlocked, string Mode = "forge");

/// <summary>
/// What just happened at the forge, for the cutscene: Kind is temper, break, combine, fuse, mystic, salvage, research,
/// trait or empower. Result is the new item (none when it broke or was taken apart).
/// </summary>
public sealed record ForgeFx(string Kind, string Title, string Text, ItemInfo? Result, Rarity Rarity, RuneInfo? Rune = null);

/// <summary>Endgame state that rides along with the stats: wallet, runes, Mist, sync, Pit, pets, relics, research, collection.</summary>
public sealed record ExtrasState(Dictionary<string, int> Materials, List<RuneInfo> Runes, float Threat, bool Hunted,
    string MistZone, string MistZoneId, long MistEndsUnix, bool MistHere, int SyncLevel, int PitFloor, int PitBest,
    List<string> Pets, string? ActivePet, List<string> Relics, List<string> Researched, List<string> Collection, List<string> SetsClaimed,
    List<string> Instabilities, long WorldBossUnix, bool WorldBossOpen, bool InWorldBoss, int BagLimit, string Zone);

public sealed record HeraldState(bool InMuster, int Mustered, int Limit, long NextUnix, bool Open, bool FightOn, string Boss);

public sealed record DailyTaskView(string Id, bool Pledge, string Text, int Progress, int Target, bool Claimed, string Reward);
public sealed record InstabilityView(string Name, string Text);
public sealed record DailyView(string Date, List<DailyTaskView> Tasks, List<InstabilityView> Instabilities, long WeekEndsUnix, string MistZone, long MistEndsUnix);

public sealed record LoadoutView(string Name, Dictionary<string, long> Items, bool Ready, List<string> Missing);
public sealed record ArmouryView(List<ItemInfo> Rack, int Limit, List<LoadoutView> Loadouts);

public sealed record TradeState(string With, List<ItemInfo> Mine, int MyGold, bool MyAccept, List<ItemInfo> Theirs, int TheirGold, bool TheirAccept);

public sealed record AuctionRow(long ListingId, ItemInfo Item, string Seller, int Price, long ExpiresUnix, bool Mine);

/// <summary>One page of auction search results. Sort is one of: name, level, rarity, slot, price, seller, ends.</summary>
public sealed record AuctionPage(List<AuctionRow> Rows, int Total, int Page, int Pages, string Sort, bool Descending,
    int PostingFeePercent, int SaleCutPercent);

public sealed record GuildMemberInfo(string Name, ClassId Class, int Level, bool Online, string Rank);

public sealed record GuildState(string? Name, string? Motto, List<GuildMemberInfo> Members, int StashCount, int CreateCost, bool IsLeader);
