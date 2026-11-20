using TacoFactory.Core.Data;

namespace TacoFactory.Core.Sim;

public sealed record InstabilityDef(string Id, string Name, string Text);

/// <summary>A daily pledge (shared by everyone) or endeavour (each knight's own). Done when Tally[Key] reaches Target.</summary>
public sealed record DailyTask(string Id, bool Pledge, string Text, string Key, int Target, Dictionary<string, int> Reward, int Gold);

public sealed record PetDef(string Id, string Name, int Gold, int Tokens, string Text, bool Flies);

/// <summary>
/// The calendar of the endgame: which zone the Mist holds this hour, this week's instabilities, today's pledges and
/// endeavours, and the menagerie. Pure functions of the date, so every server agrees and tests can pin a day.
/// </summary>
public static class Endgame
{
    // ------------------------------------------------------------------ weekly instabilities

    public static readonly InstabilityDef[] Instabilities =
    [
        new("bloodthirst", "Bloodthirst", "Monsters strike 25% harder, and give 25% more experience."),
        new("vigour", "Unholy Vigour", "Wounded monsters slowly close their wounds."),
        new("martyrdom", "Martyrdom", "The dying burst: step out of the mark where a monster falls."),
        new("resolute", "Resolute", "Monsters have more health; champions half again as much."),
        new("frenzy", "Frenzy", "Monsters move a fifth faster."),
        new("plague", "Plague Winds", "Every few seconds a plague cloud settles on one of you. Move."),
    ];

    private static readonly DateTime Epoch = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // a Monday

    public static int WeekIndex(DateTime utc) => (int)System.Math.Floor((utc - Epoch).TotalDays / 7);

    /// <summary>Two instabilities each week, changing on Monday (UTC).</summary>
    public static List<InstabilityDef> ThisWeek(DateTime utc)
    {
        int w = System.Math.Abs(WeekIndex(utc));
        int a = w % Instabilities.Length, b = (a + 1 + w / Instabilities.Length % (Instabilities.Length - 1)) % Instabilities.Length;
        if (b == a) b = (a + 1) % Instabilities.Length;
        return [Instabilities[a], Instabilities[b]];
    }

    public static DateTime NextWeek(DateTime utc) => Epoch.AddDays(7 * (WeekIndex(utc) + 1));

    // ------------------------------------------------------------------ the Mist

    /// <summary>Which zone the Mist holds now, when it moves on, and an id for this takeover (embers belong to it).</summary>
    public static (ZoneDef Zone, DateTime Ends, string Id) MistNow(GameData data, DateTime utc, int minutes = 60)
    {
        var zones = data.Zones.Values.Where(z => z.MistEligible).OrderBy(z => z.Act).ThenBy(z => z.LevelMin).ThenBy(z => z.Id).ToList();
        long slot = (long)System.Math.Floor((utc - Epoch).TotalMinutes / System.Math.Max(1, minutes));
        // Stride through the list so consecutive hours land in different acts.
        var zone = zones[(int)((slot * 7) % zones.Count)];
        return (zone, Epoch.AddMinutes((slot + 1) * minutes), $"{slot}:{zone.Id}");
    }

    // ------------------------------------------------------------------ dailies

    public static string Today(DateTime utc) => utc.ToString("yyyy-MM-dd");

    private static int Seed(string s) { unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h & 0x7FFFFFFF; } }

    private static Dictionary<string, int> R(params (string K, int V)[] kv) => kv.ToDictionary(x => x.K, x => x.V);

    /// <summary>Three pledges a day, the same for everyone: a zone to clear, a champion to slay, and a trial.</summary>
    public static List<DailyTask> Pledges(GameData data, string date)
    {
        var rng = new Random(Seed("pledge" + date));
        var list = new List<DailyTask>();
        var zones = data.Zones.Values.Where(z => z.MistEligible && z.Superunique is null && z.Boss is null).OrderBy(z => z.Id).ToList();
        if (zones.Count > 0)
        {
            var z = zones[rng.Next(zones.Count)];
            list.Add(new("p_clear", true, $"Clear {z.Name} (act {z.Act})", "clear:" + z.Id, 1, R((Materials.SoulCore, 1), (Materials.Whetstone, 3)), 300 * z.Act));
        }
        var uniques = data.Zones.Values.Where(z => z.Superunique is not null && !z.Prologue && data.Superuniques.ContainsKey(z.Superunique!)).OrderBy(z => z.Id).ToList();
        if (uniques.Count > 0)
        {
            var z = uniques[rng.Next(uniques.Count)];
            var su = data.Superuniques[z.Superunique!];
            list.Add(new("p_unique", true, $"Slay {su.Name} in {z.Name}", "unique:" + su.Id, 1, R((Materials.HolyOil, 1), (Materials.Whetstone, 3)), 400 * z.Act));
        }
        var trials = new DailyTask[]
        {
            new("p_pit", true, "Reach floor 5 of the Proving Pit", "pit_floor", 5, R((Materials.SoulCore, 2), (Materials.HolyOil, 1)), 1500),
            new("p_huntsman", true, "Slay the Grey Huntsman in the Mist", "huntsman", 1, R((Materials.SoulCore, 2), (Materials.HolyOil, 1)), 1500),
            new("p_tarasque", true, "Stand against La Tarasque", "tarasque", 1, R((Materials.SoulCore, 2), (Materials.HolyOil, 2)), 2000),
        };
        list.Add(trials[rng.Next(trials.Length)]);
        return list;
    }

    /// <summary>Four small endeavours a day for each knight.</summary>
    public static List<DailyTask> Endeavours(string name, string date)
    {
        var pool = new DailyTask[]
        {
            new("e_kill", false, "Slay 120 foes", "kill", 120, R((Materials.Whetstone, 2)), 400),
            new("e_undead", false, "Lay 40 undead to rest", "undead", 40, R((Materials.Whetstone, 2)), 400),
            new("e_elite", false, "Slay 3 champions", "elite", 3, R((Materials.Whetstone, 3)), 600),
            new("e_temper", false, "Temper an item at the forge", "temper", 1, R((Materials.Whetstone, 2)), 300),
            new("e_socket", false, "Set a rune in your gear", "socket", 1, R((Materials.Whetstone, 1)), 200),
            new("e_salvage", false, "Salvage 5 items", "salvage", 5, R((Materials.Whetstone, 2)), 250),
            new("e_coffer", false, "Open a Mist coffer", "coffer", 1, R((Materials.HolyOil, 1)), 500),
            new("e_embers", false, "Gather 150 Mist embers", "embers", 150, R((Materials.SoulCore, 1)), 500),
            new("e_pit", false, "Clear a floor of the Proving Pit", "pit_floor", 1, R((Materials.Whetstone, 2)), 300),
            new("e_mystic", false, "Feed the Athanor", "athanor", 1, R((Materials.HolyOil, 1)), 300),
        };
        var rng = new Random(Seed(name.ToLowerInvariant() + date));
        return pool.OrderBy(_ => rng.Next()).Take(4).ToList();
    }

    // ------------------------------------------------------------------ the menagerie

    public static readonly PetDef[] Pets =
    [
        new("greyhound", "Greyhound", 20000, 3, "A lean hound from the kennels of Troyes.", false),
        new("barn_cat", "Barn Cat", 15000, 2, "She found you. Not the other way round.", false),
        new("piglet", "Truffle Piglet", 12000, 2, "Noses out gold the way it noses out truffles. It does not, but it tries.", false),
        new("raven", "Raven", 25000, 3, "Watches the battlefield from your shoulder, and remembers faces.", true),
        new("goshawk", "Goshawk", 40000, 5, "A noble's bird: the falconers of Philip would kill for it.", true),
        new("white_hart", "White Hart Fawn", 60000, 8, "A white hart, the old sign of a holy quest. Very rare.", false),
    ];

    public static PetDef? Pet(string? id) => id is null ? null : Pets.FirstOrDefault(p => p.Id == id);
    public static int PetIndex(string? id) => Array.FindIndex(Pets, p => p.Id == id) + 1;

    // ------------------------------------------------------------------ world boss

    public const string WorldBossId = "tarasque", MusterZone = "tarascon_muster", WorldBossZone = "tarascon_shore";
}
