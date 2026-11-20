using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Server;

/// <summary>
/// The endgame: the forge room (tempering, salvage, research, traits, empowering, runes, the Athanor), daily pledges
/// and endeavours, the set collection, armoury loadouts, the menagerie, relic arts, the Mist takeover, the Proving Pit
/// and the world boss muster.
/// </summary>
public sealed partial class GameServer
{
    private const string ForgeZone = "the_forge";

    // ------------------------------------------------------------------ arriving

    /// <summary>A new zone copy: this week's instabilities, the Mist if it holds this zone, the Pit's level.</summary>
    private void PrepareInstance(Instance inst, Party? party)
    {
        var w = inst.World;
        w.Instabilities.AddRange(Endgame.ThisWeek(DateTime.UtcNow).Select(i => i.Id));
        if (_mistZoneId is not null && inst.Zone.Id == _mistZoneId) w.SetMist(true);
        if (inst.Zone.ProvingPit && party is not null)
            w.PitLevel = System.Math.Max(1, party.Members.Max(m => m.Entity?.Level ?? m.Record.Level));
    }

    private void ArriveExtras(Session s, Entity p)
    {
        var x = p.Extras;
        if (_mistId is not null && x.MistId != _mistId)
        {
            x.Materials.Remove(Materials.MistEmber);
            x.MistId = _mistId;
        }
        ResetDailyIfNeeded(p);
        p.PetIndex = Endgame.PetIndex(x.ActivePet);
        foreach (var i in s.Stash) Runes.SocketsOf(i);
    }

    private void EndgameOnEnter(Session s, Entity p, ZoneDef z)
    {
        var w = s.Instance!.World;
        if (w.MistActive)
            Tell(s, $"{{#B8C4D8}}The Grey Mist holds {z.Name}.{{/}} Monsters are stronger and drop Mist embers; open the coffers with them. Kill enough and the Grey Huntsman will come for you.");
        if ((w.MistActive || z.ProvingPit) && w.Instabilities.Count > 0)
            Tell(s, "{#E0A060}This week's instabilities:{/} " + string.Join("; ", Endgame.ThisWeek(DateTime.UtcNow).Select(i => $"{i.Name} ({i.Text})")));
        if (p.SyncLevel > 0) Tell(s, $"{{#9FC6FF}}Level sync:{{/}} you fight as level {p.SyncLevel} here (your gear at {p.GearScale * 100:F0}%).");
        if (z.ProvingPit) Tell(s, $"{{#F2D25A}}The Proving Pit.{{/}} Ten floors, each harder than the last. Every floor cleared pays a Soul Core; floors 6 to 10 can yield Exalted set pieces.");
        if (z.WorldBoss) Tell(s, "{#F2D25A}La Tarasque!{/} Her blows are shared between all of you. If a brother falls, stand by him and press Revive.");
        if (z.IsPersonal) Tell(s, "Your cell. The armoury rack keeps your loadouts: swap your whole harness here in one go.");
    }

    // ------------------------------------------------------------------ tick

    private string? _mistZoneId, _mistId;
    private DateTime _mistEnds;

    private void TickEndgame()
    {
        if (_ticks % 20 != 0) return;
        var now = DateTime.UtcNow;
        TickMist(now);
        TickWorldBoss(now);
        if (_ticks % (20 * 60) == 0)
            foreach (var s in _sessions.Values) if (s.Entity is { } p) ResetDailyIfNeeded(p);
    }

    private void TickMist(DateTime now)
    {
        var (zone, ends, id) = Endgame.MistNow(_data, now, _opt.MistMinutes);
        if (_opt.MistZone is string forced && _data.Zones.TryGetValue(forced, out var fz)) (zone, id) = (fz, "test:" + fz.Id);
        if (id == _mistId) return;
        bool first = _mistId is null;
        _mistId = id;
        _mistZoneId = zone.Id;
        _mistEnds = ends;
        foreach (var inst in _instances.Values) inst.World.SetMist(inst.Zone.Id == zone.Id);
        foreach (var s in _sessions.Values)
        {
            if (s.Entity is not { } p) continue;
            if (p.Extras.Has(Materials.MistEmber) > 0 && !first) Tell(s, "{#8A8070}The Mist moves on, and your embers go cold.{/}");
            p.Extras.Materials.Remove(Materials.MistEmber);
            p.Extras.MistId = id;
            Tell(s, $"{{#B8C4D8}}The Grey Mist rolls over {zone.Name} (act {zone.Act}).{{/}} It holds for {(ends - now).TotalMinutes:F0} minutes.");
        }
        Log($"the Mist takes {zone.Id} until {ends:HH:mm} UTC");
    }

    // ------------------------------------------------------------------ world boss

    private DateTime _wbNext;
    private bool _wbWarned;
    private Instance? _wbFight;
    private double _wbStarted, _wbEndAt = -1;
    private int _wbCount;
    private const double WorldBossMaxSeconds = 15 * 60, MusterWarnMinutes = 5;

    private DateTime NextHorn(DateTime now)
    {
        var mins = System.Math.Max(2, _opt.WorldBossMinutes);
        var t = now.Date.AddMinutes(System.Math.Ceiling((now - now.Date).TotalMinutes / mins) * mins);
        return t <= now ? t.AddMinutes(mins) : t;
    }

    private void TickWorldBoss(DateTime now)
    {
        if (_wbNext == default) _wbNext = NextHorn(now);
        if (!_wbWarned && now >= _wbNext.AddMinutes(-MusterWarnMinutes))
        {
            _wbWarned = true;
            foreach (var s in _sessions.Values)
                Tell(s, $"{{#F2D25A}}La Tarasque stirs in the Rhône.{{/}} The muster gathers at Tarascon: speak to Guillaume the Herald in the Sanctuary. The horn sounds in {(_wbNext - now).TotalMinutes:F0} minutes.");
        }
        if (now >= _wbNext && _wbFight is null)
        {
            StartWorldBoss();
            _wbNext = NextHorn(now.AddSeconds(1));
            _wbWarned = false;
        }
        if (_wbFight is { } fight)
        {
            var boss = fight.World.ScriptedBoss is not null ? fight.World.Monsters.FirstOrDefault(m => m.UniqueId == Endgame.WorldBossId) : null;
            if (boss is { Dead: true } && _wbEndAt < 0) _wbEndAt = Clock + 45;
            if ((_wbEndAt >= 0 && Clock >= _wbEndAt) || Clock - _wbStarted > WorldBossMaxSeconds || fight.Sessions.Count == 0) EndWorldBoss();
        }
    }

    private void StartWorldBoss()
    {
        var muster = GetInstance(Endgame.MusterZone, null);
        var who = muster.Sessions.Where(x => x.Entity is { Dead: false }).Take(_data.Zones[Endgame.WorldBossZone].PlayerLimit).ToList();
        if (who.Count == 0) { Log("world boss: nobody mustered"); return; }
        var zone = _data.Zones[Endgame.WorldBossZone];
        var inst = new Instance { Key = $"{zone.Id}#{++_wbCount}", World = new World(_data, zone.Id, ++_seed) };
        _instances[inst.Key] = inst;
        PrepareInstance(inst, null);
        inst.World.BossLevel = System.Math.Clamp((int)who.Average(x => x.Entity!.Level), 10, Progression.MaxLevel);
        inst.World.Populate(groupSize: who.Count);
        _wbFight = inst;
        _wbStarted = Clock;
        _wbEndAt = -1;
        foreach (var s in who)
        {
            Tell(s, "{#F2D25A}The horn sounds! You cross the river to the shore where she waits.{/}");
            Enter(s, inst, new Vec2(zone.PlayerSpawn[0] + Random.Shared.Next(-200, 201), zone.PlayerSpawn[1] + Random.Shared.Next(-60, 61)));
        }
        Log($"world boss: {who.Count} knights face La Tarasque (level {inst.World.BossLevel})");
    }

    private void EndWorldBoss()
    {
        if (_wbFight is not { } fight) return;
        var muster = GetInstance(Endgame.MusterZone, null);
        foreach (var s in fight.Sessions.ToList())
        {
            if (s.Entity is { Dead: true } p) { p.Flags &= ~EntityFlags.Dead; p.Hp = p.MaxHp; }
            Enter(s, muster);
        }
        _instances.Remove(fight.Key);
        _wbFight = null;
        Log("world boss: the shore is empty again");
    }

    private void SendHerald(Session s)
    {
        var muster = _instances.GetValueOrDefault(Endgame.MusterZone);
        SendUi(s, UiKind.Herald, new HeraldState(s.Instance?.Zone.Muster == true, muster?.World.Players.Count() ?? 0,
            _data.Zones[Endgame.WorldBossZone].PlayerLimit, new DateTimeOffset(_wbNext == default ? NextHorn(DateTime.UtcNow) : _wbNext).ToUnixTimeSeconds(),
            DateTime.UtcNow >= _wbNext.AddMinutes(-MusterWarnMinutes), _wbFight is not null, "La Tarasque"));
    }

    // ------------------------------------------------------------------ events

    private void EndgameEvent(Instance inst, SimEvent e)
    {
        switch (e.Type)
        {
            case SimEventType.UniqueKilled:
                foreach (var s in inst.Sessions) s.Entity?.Extras.Count("unique:" + e.Text);
                if (e.Text == Endgame.WorldBossId) foreach (var s in inst.Sessions.ToList()) TarasqueRewards(s, inst.World);
                if (e.Text == "philip_iv") foreach (var s in inst.Sessions.ToList()) KingRewards(s);
                break;
            case SimEventType.PitFloorCleared:
                Log($"{inst.Key}: floor {e.Value} cleared by " + string.Join(", ", inst.Sessions.Select(x => $"{x.Name} ({x.Entity?.Deaths ?? 0} deaths)")));
                foreach (var s in inst.Sessions.ToList()) PitRewards(s, inst.World, e.Value);
                break;
            case SimEventType.PitFloor:
                foreach (var s in inst.Sessions) Tell(s, $"{{#F2D25A}}The Proving Pit: floor {e.Value} of {World.PitFloors}.{{/}}");
                break;
            case SimEventType.Hunted:
                foreach (var s in inst.Sessions) Tell(s, $"{{#E0463C}}{e.Text} is hunted!{{/}} The Grey Huntsman comes out of the Mist with his hounds.");
                break;
            case SimEventType.HuntsmanSlain:
                foreach (var s in inst.Sessions)
                {
                    if (s.Entity is not { } p) continue;
                    p.Extras.Give(Materials.SoulCore, 1);
                    p.Extras.Give(Materials.Whetstone, 2);
                    Tell(s, "{#F2D25A}The Grey Huntsman falls.{/} +1 Soul Core, +2 Whetstones, +40 Mist embers. Your threat is spent.");
                }
                break;
            case SimEventType.RuneUp:
                if (inst.Sessions.FirstOrDefault(x => x.EntityId == e.Source) is { } owner)
                    Tell(owner, e.Value >= Runes.MaxLevel
                        ? $"{{#F2D25A}}Your {e.Text} reaches level {Runes.MaxLevel}.{{/}} Fuse it with another at the Runecarver."
                        : $"{{#9FC6FF}}Your {e.Text} grows to level {e.Value}.{{/}}");
                break;
            case SimEventType.RuneFound:
                if (inst.Sessions.FirstOrDefault(x => x.EntityId == e.Source) is { } finder) Tell(finder, $"{{#9FC6FF}}You find a rune: {e.Text}.{{/}} Set it in your gear from the bag (I).");
                break;
        }
    }

    // ------------------------------------------------------------------ rewards

    /// <summary>Puts an item in the bag, or the strongbox, or the auction mailbox if both are full. Notes set pieces.</summary>
    private void Grant(Session s, Entity p, Item item, string why = "")
    {
        Runes.SocketsOf(item);
        p.Extras.Note(item);
        string where;
        if (p.Inventory.Count < Gear.BagLimit(p)) { p.Inventory.Add(item); where = ""; }
        else if (s.Stash.Count < Loot.StashLimit) { s.Stash.Add(item); where = " (sent to your strongbox)"; }
        else { _auctions.MailFor(s.Name).Items.Add(item); _auctions.Save(); where = " (held by Jehan the Broker: your bag and strongbox are full)"; }
        string set = item.Set is not null ? $" {{#6BD66B}}[{ItemSets.Get(item.Set)?.Name}{(item.Exalted ? ", Exalted" : "")}]{{/}}" : "";
        Tell(s, $"{why}{RarityTag(item.Rarity)}{item.Rarity} {item.DisplayName}{{/}}{set}{where}");
    }

    private void GrantRune(Session s, Entity p, Rune r)
    {
        if (p.Extras.Runes.Count >= Runes.PouchLimit) { Tell(s, "Your rune pouch is full; a rune is lost."); return; }
        p.Extras.Runes.Add(r);
        Tell(s, $"{{#9FC6FF}}Rune: {Runes.Name(r)}.{{/}}");
    }

    private void GrantMaterials(Session s, Entity p, Dictionary<string, int> m, int gold = 0)
    {
        foreach (var (k, v) in m) p.Extras.Give(k, v);
        p.Gold += gold;
        var parts = m.Where(kv => kv.Value > 0).Select(kv => $"{kv.Value} {Materials.Name(kv.Key)}{(kv.Value > 1 && !kv.Key.EndsWith("oil") ? "s" : "")}").ToList();
        if (gold > 0) parts.Add($"{gold:N0} gold");
        if (parts.Count > 0) Tell(s, "{#E6C14A}+ " + string.Join(", ", parts) + "{/}");
    }

    private static string RarityTag(Rarity r) => r switch
    {
        Rarity.Uncommon => "{#4CC24C}", Rarity.Rare => "{#4FA3FF}", Rarity.Elite => "{#E8D04A}", Rarity.Epic => "{#E8892B}",
        Rarity.Legendary => "{#D8342C}", Rarity.Exotic => "{#36D6C3}", Rarity.Mythic => "{#FF4FB8}", _ => "{#E8E4DA}",
    };

    private static Rarity HighRoll(Random rng, double exotic, double mythic) =>
        rng.NextDouble() < mythic ? Rarity.Mythic : rng.NextDouble() < exotic ? Rarity.Exotic : Rarity.Legendary;

    private void TarasqueRewards(Session s, World w)
    {
        if (s.Entity is not { } p) return;
        var rng = Random.Shared;
        int level = w.BossLevel ?? p.Level;
        p.Extras.Count("tarasque");
        Tell(s, "{#F2D25A}La Tarasque is slain!{/} The shore is yours. The Herald will take you back across in a moment.");
        var mats = new Dictionary<string, int> { [Materials.TrophyRelic] = 1, [Materials.SoulCore] = 2, [Materials.HolyOil] = 1 };
        if (rng.NextDouble() < 0.35) mats[Materials.PetToken] = 1;
        GrantMaterials(s, p, mats, 30 * level);
        Grant(s, p, Smithing.MakeHigh(rng, level, HighRoll(rng, 0.12, 0.02)), "La Tarasque leaves you: ");
        if (rng.NextDouble() < 0.10) Grant(s, p, ItemSets.MakePiece(rng, ItemSets.Get("iron_king")!, level, Rarity.Epic), "From her hoard: ");
        if (rng.NextDouble() < 0.6) GrantRune(s, p, Runes.Make(rng, (Rarity)rng.Next((int)Rarity.Uncommon, (int)Rarity.Epic + 1)));
        _store.Save(s.Record = Snapshot(s, p));
    }

    /// <summary>The Iron King (the campaign's last foe) drops the finest runes and gear outside the world bosses.</summary>
    private void KingRewards(Session s)
    {
        if (s.Entity is not { } p) return;
        var rng = Random.Shared;
        GrantMaterials(s, p, new() { [Materials.SoulCore] = 2, [Materials.HolyOil] = 2 });
        if (rng.NextDouble() < 0.5) GrantRune(s, p, Runes.Make(rng, (Rarity)rng.Next((int)Rarity.Rare, (int)Rarity.Legendary + 1)));
        var r = HighRoll(rng, 0.10, 0.02);
        if (r >= Rarity.Exotic) Grant(s, p, Smithing.MakeHigh(rng, p.Level, r), "From the king's own armoury: ");
        if (rng.NextDouble() < 0.25) Grant(s, p, ItemSets.MakePiece(rng, ItemSets.Get("iron_king")!, p.Level, Rarity.Legendary), "From the king's own armoury: ");
    }

    private void PitRewards(Session s, World w, int floor)
    {
        if (s.Entity is not { } p) return;
        var rng = Random.Shared;
        int level = System.Math.Clamp(w.PitLevel + floor / 2, 1, Progression.MaxLevel);
        var mats = new Dictionary<string, int> { [Materials.SoulCore] = floor == World.PitFloors ? 3 : 1, [Materials.Whetstone] = System.Math.Max(1, floor / 2) };
        if (floor % 5 == 0) mats[Materials.HolyOil] = floor / 5;
        Tell(s, $"{{#F2D25A}}Floor {floor} cleared.{{/}}" + (floor < World.PitFloors ? " The next opens in a few seconds." : " The Pit is conquered!"));
        GrantMaterials(s, p, mats, 60 * floor * System.Math.Max(1, level / 10));
        if (floor % 3 == 0) Grant(s, p, ItemSets.MaybeSet(rng, Loot.MakeItem(rng, Loot.NewId(), level, Loot.RollRarity(rng, Rarity.Legendary, Rarity.Rare)), 0.15), "The Pit pays: ");
        // Floors 6 and deeper can yield Exalted set pieces: same art, a gold name and one more set bonus line.
        if (floor >= 6 && rng.NextDouble() < (floor == World.PitFloors ? 0.5 : 0.25))
        {
            var sets = ItemSets.All.Where(x => !x.BossOnly).ToList();
            var r = (Rarity)rng.Next((int)Rarity.Elite, (int)Rarity.Legendary + 1);
            Grant(s, p, ItemSets.MakePiece(rng, sets[rng.Next(sets.Count)], level, r, exalted: true), "{#F2D25A}Exalted!{/} ");
        }
        if (floor == World.PitFloors)
        {
            Grant(s, p, rng.NextDouble() < 0.15 ? Smithing.MakeHigh(rng, level, Rarity.Exotic) : Loot.MakeItem(rng, Loot.NewId(), level, Rarity.Legendary), "The Champion's prize: ");
            if (rng.NextDouble() < 0.3) GrantRune(s, p, Runes.Make(rng, Rarity.Uncommon));
        }
        _store.Save(s.Record = Snapshot(s, p));
    }

    private void OpenCoffer(Session s, Entity p, Entity coffer)
    {
        var w = s.Instance!.World;
        if (w.OpenCoffer(p, coffer.Id) is string why) { Tell(s, why); return; }
        var rng = Random.Shared;
        int level = System.Math.Max(w.Zone.LevelMax, 1) + World.MistLevelBonus;
        Grant(s, p, ItemSets.MaybeSet(rng, Loot.MakeItem(rng, Loot.NewId(), level, Loot.RollRarity(rng, Rarity.Legendary, Rarity.Rare)), 0.12), "{#B8C4D8}The coffer opens:{/} ");
        var mats = new Dictionary<string, int> { [Materials.Whetstone] = rng.Next(2, 5) };
        if (rng.NextDouble() < 0.15) mats[Materials.SoulCore] = 1;
        GrantMaterials(s, p, mats);
        if (rng.NextDouble() < 0.3) GrantRune(s, p, Runes.Make(rng));
        SendExtras(s);
    }

    // ------------------------------------------------------------------ extras for the client

    private void SendExtras(Session s)
    {
        if (s.Entity is not { } p) return;
        var x = p.Extras;
        var w = s.Instance!.World;
        var mz = _mistZoneId is not null ? _data.Zones[_mistZoneId] : null;
        SendUi(s, UiKind.Extras, new ExtrasState(
            new Dictionary<string, int>(x.Materials), x.Runes.Select(RuneInfo.From).ToList(), x.Threat, x.Hunted,
            mz?.Name ?? "", mz?.Id ?? "", new DateTimeOffset(_mistEnds).ToUnixTimeSeconds(), w.MistActive,
            p.SyncLevel, w.PitFloor, x.PitBest, x.Pets.ToList(), x.ActivePet, x.Relics.ToList(), x.Researched.ToList(),
            x.Collection.ToList(), x.SetsClaimed.ToList(), w.Instabilities.ToList(),
            new DateTimeOffset(_wbNext == default ? NextHorn(DateTime.UtcNow) : _wbNext).ToUnixTimeSeconds(),
            DateTime.UtcNow >= _wbNext.AddMinutes(-MusterWarnMinutes), w.Zone.WorldBoss, Gear.BagLimit(p), w.Zone.Id));
    }

    // ------------------------------------------------------------------ commands

    /// <summary>Endgame commands. Returns false for verbs it doesn't know (the economy handles those).</summary>
    private bool EndgameCommand(Session s, Entity p, string verb, string[] a, out string? error)
    {
        error = null;
        var world = s.Instance!.World;
        switch (verb)
        {
            case "temper": error = DoTemper(s, p, a); break;
            case "salvage": error = DoSalvage(s, p, a); break;
            case "research": error = DoResearch(s, p, a); break;
            case "trait": error = DoApplyTrait(s, p, a); break;
            case "empower": error = DoEmpower(s, p, a); break;
            case "rune.socket": error = SocketRune(s, p, a); break;
            case "rune.unsocket": error = UnsocketRune(s, p, a); break;
            case "rune.fuse": error = FuseRunes(s, p, a); break;
            case "rune.discard":
                if (TryId(a, 0, out var rid) && p.Extras.Runes.FirstOrDefault(r => r.Id == rid) is { } dr) { p.Extras.Runes.Remove(dr); Tell(s, $"Discarded {Runes.Name(dr)}."); }
                break;
            case "athanor": error = DoAthanor(s, p, a); break;
            case "athanor.runes": error = DoAthanorRunes(s, p, a); break;
            case "daily.claim": error = ClaimDaily(s, p, a.ElementAtOrDefault(0) ?? ""); break;
            case "daily.open":
                if (NearService(s, "daily") is null) { error = "Brother Almoner keeps the pledges, in the Sanctuary."; break; }
                SendDaily(s); break;
            case "collection.claim": error = ClaimCollection(s, p, a.ElementAtOrDefault(0) ?? ""); break;
            case "armoury.put": error = ArmouryPut(s, p, a); break;
            case "armoury.take": error = ArmouryTake(s, p, a); break;
            case "loadout.save": error = SaveLoadout(s, p, a); break;
            case "loadout.equip": error = EquipLoadout(s, p, a); break;
            case "loadout.clear":
                if (!AtArmoury(s)) { error = "Use the armoury rack in your cell or your guild hall."; break; }
                int ci = IntArg(a, 0, -1);
                if (ci >= 0 && ci < p.Extras.Loadouts.Count) p.Extras.Loadouts[ci] = new Loadout { Name = "" };
                SendArmoury(s); break;
            case "pet.buy": error = BuyPet(s, p, a.ElementAtOrDefault(0) ?? ""); break;
            case "pet.summon":
                if (!p.Extras.Pets.Contains(a.ElementAtOrDefault(0) ?? "")) { error = "You don't own that companion."; break; }
                p.Extras.ActivePet = a[0]; p.PetIndex = Endgame.PetIndex(a[0]);
                Tell(s, $"Your {Endgame.Pet(a[0])!.Name} follows you."); break;
            case "pet.dismiss": p.Extras.ActivePet = null; p.PetIndex = 0; break;
            case "relic.learn": error = LearnRelic(s, p, a.ElementAtOrDefault(0) ?? ""); break;
            case "revive": error = world.ReviveAlly(p, IntArg(a, 0)); break;
            case "muster.go": error = GoMuster(s); break;
            case "muster.leave":
                if (s.Instance.Zone.Muster) Enter(s, GetInstance(_data.Town.Id, null));
                break;
            case "herald": SendHerald(s); break;
            default: return false;
        }
        return true;
    }

    private void GoToForge(Session s)
    {
        var z = _data.Zones[ForgeZone];
        Enter(s, GetInstance(ForgeZone, null), new Vec2(z.PlayerSpawn[0], z.PlayerSpawn[1]));
    }

    private string? GoMuster(Session s)
    {
        if (s.Instance!.Zone.Muster) return "You are already at the muster.";
        if (NearService(s, "herald") is null) return "Speak to Guillaume the Herald in the Sanctuary.";
        var muster = GetInstance(Endgame.MusterZone, null);
        var z = muster.Zone;
        if (muster.World.Players.Count() >= z.PlayerLimit) return $"The muster is full ({z.PlayerLimit} knights). Wait for the next horn.";
        Enter(s, muster, new Vec2(z.PlayerSpawn[0], z.PlayerSpawn[1]));
        SendHerald(s);
        return null;
    }

    // ---- item lookup

    /// <summary>An item the knight holds: in the bag or worn.</summary>
    private static Item? Owned(Entity p, long id, out ItemSlot? worn)
    {
        worn = null;
        if (p.Inventory.FirstOrDefault(i => i.Id == id) is { } b) return b;
        foreach (var (slot, i) in p.Equipped) if (i.Id == id) { worn = slot; return i; }
        return null;
    }

    private void Replace(World w, Entity p, Item old, Item now, ItemSlot? worn)
    {
        if (worn is { } slot) { p.Equipped[slot] = now with { Slot = slot }; w.RecalcStats(p); }
        else { int i = p.Inventory.IndexOf(old); if (i >= 0) p.Inventory[i] = now; else p.Inventory.Add(now); }
    }

    /// <summary>Runes come out of an item before it is destroyed, back into the pouch.</summary>
    private void Reclaim(Session s, Entity p, Item item)
    {
        if (item.Sockets is null) return;
        int n = 0;
        for (int i = 0; i < item.Sockets.Count; i++)
        {
            if (item.Sockets[i] is not { } r) continue;
            item.Sockets[i] = null;
            if (p.Extras.Runes.Count < Runes.PouchLimit) { p.Extras.Runes.Add(r); n++; }
        }
        if (n > 0) Tell(s, $"{n} rune{(n == 1 ? "" : "s")} returned to your pouch.");
    }

    private void Fx(Session s, string kind, string title, string text, Item? result, Rarity rarity, Rune? rune = null) =>
        SendUi(s, UiKind.ForgeFx, new ForgeFx(kind, title, text, result is null ? null : Msg.Info(result), rarity, rune is null ? null : RuneInfo.From(rune)));

    private string? AtForge(Session s) =>
        NearService(s, "forge") is null || s.Instance!.Zone.Id != ForgeZone ? "Master Aubry's forge is under the Sanctuary, through the west door."
        : !s.Unlocks.Contains("forge") ? "The forge is cold until Aubry has his hammer back." : null;

    // ---- the forge

    private string? DoTemper(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        if (!TryId(a, 0, out var id) || Owned(p, id, out var worn) is not { } item) return "Put an item on the anvil.";
        if (Smithing.CanTemper(item) is string why) return why;
        int gold = Smithing.TemperGold(item), stones = Smithing.TemperWhetstones(item), oil = Smithing.TemperOil(item);
        if (p.Gold < gold) return $"Tempering costs {gold:N0} gold.";
        if (p.Extras.Has(Materials.Whetstone) < stones) return $"Tempering needs {stones} whetstone{(stones == 1 ? "" : "s")}. Salvage gear or finish endeavours for more.";
        if (p.Extras.Has(Materials.HolyOil) < oil) return $"Past +4 the steel needs Holy Oil: {oil}.";
        p.Gold -= gold;
        p.Extras.Spend(Materials.Whetstone, stones);
        p.Extras.Spend(Materials.HolyOil, oil);
        p.Extras.Count("temper");
        var w = s.Instance!.World;
        if (Random.Shared.NextDouble() < Smithing.BreakChance(item))
        {
            Reclaim(s, p, item);
            if (worn is { } slot) { p.Equipped.Remove(slot); w.RecalcStats(p); } else p.Inventory.Remove(item);
            Fx(s, "break", "The steel shatters", $"{item.DisplayName} could not take the temper.", null, item.Rarity);
            Tell(s, $"{{#E0463C}}{item.DisplayName} shatters on the anvil.{{/}}");
            return null;
        }
        var now = Smithing.Temper(item);
        Replace(w, p, item, now, worn);
        Fx(s, "temper", $"Tempered to +{now.Upgrade}", now.DisplayName, now, now.Rarity);
        return null;
    }

    private string? DoSalvage(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        if (!TryId(a, 0, out var id) || p.Inventory.FirstOrDefault(i => i.Id == id) is not { } item) return "Only items in your bag can be salvaged.";
        var got = Smithing.Salvage(item, Random.Shared);
        Reclaim(s, p, item);
        p.Inventory.Remove(item);
        p.Extras.Count("salvage");
        foreach (var (k, v) in got) p.Extras.Give(k, v);
        Fx(s, "salvage", "Taken apart", string.Join(", ", got.Select(kv => $"{kv.Value} {Materials.Name(kv.Key)}")), null, item.Rarity);
        return null;
    }

    private string? DoResearch(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        if (!TryId(a, 0, out var id) || p.Inventory.FirstOrDefault(i => i.Id == id) is not { } item) return "Only items in your bag can be researched.";
        if (Traits.Get(item.Trait) is not { } t) return "That item has no trait to learn.";
        if (p.Extras.Researched.Contains(Traits.Key(t))) return $"You already know how to make {t.Name.ToLowerInvariant()} {t.Family.ToString().ToLowerInvariant()}.";
        int gold = Smithing.ResearchGold(item);
        if (p.Gold < gold) return $"Research costs {gold:N0} gold.";
        p.Gold -= gold;
        Reclaim(s, p, item);
        p.Inventory.Remove(item);
        p.Extras.Researched.Add(Traits.Key(t));
        Fx(s, "research", $"Trait learned: {t.Name}", $"Aubry takes the {item.Name} apart to see how it was made: {t.Text}. You can now give any {t.Family.ToString().ToLowerInvariant()} piece this trait.", null, item.Rarity);
        return null;
    }

    private string? DoApplyTrait(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        if (!TryId(a, 0, out var id) || Owned(p, id, out var worn) is not { } item) return "Choose an item.";
        if (Traits.Get(a.ElementAtOrDefault(1)) is not { } t) return "Choose a trait.";
        if (t.Family != Traits.FamilyOf(item.Slot)) return $"{t.Name} is a {t.Family.ToString().ToLowerInvariant()} trait.";
        if (!p.Extras.Researched.Contains(Traits.Key(t))) return "Research that trait first: take apart an item that has it.";
        if (item.Trait == t.Id) return "It already has that trait.";
        int gold = Smithing.ApplyTraitGold(item);
        if (p.Gold < gold) return $"Applying a trait costs {gold:N0} gold.";
        if (p.Extras.Has(Materials.HolyOil) < 1) return "Applying a trait needs 1 Holy Oil.";
        p.Gold -= gold;
        p.Extras.Spend(Materials.HolyOil, 1);
        var now = item with { Trait = t.Id };
        Replace(s.Instance!.World, p, item, now, worn);
        Fx(s, "trait", $"{t.Name}", $"{now.DisplayName} is now {t.Name.ToLowerInvariant()}: {t.Text}.", now, now.Rarity);
        return null;
    }

    private string? DoEmpower(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        if (!TryId(a, 0, out var tid) || Owned(p, tid, out var worn) is not { } target) return "Choose the piece to empower.";
        if (!TryId(a, 1, out var sid) || p.Inventory.FirstOrDefault(i => i.Id == sid) is not { } sacrifice) return "Choose a piece from your bag to give up.";
        if (Smithing.CanEmpower(target, sacrifice) is string why) return why;
        Reclaim(s, p, sacrifice);
        p.Inventory.Remove(sacrifice);
        var now = Smithing.Empower(target);
        Replace(s.Instance!.World, p, target, now, worn);
        Fx(s, "empower", $"Empowered to rank {now.Upgrade}", $"{sacrifice.DisplayName} is melted into {now.DisplayName}.", now, now.Rarity);
        return null;
    }

    // ---- runes

    private string? SocketRune(Session s, Entity p, string[] a)
    {
        if (!TryId(a, 0, out var rid) || p.Extras.Runes.FirstOrDefault(r => r.Id == rid) is not { } rune) return "That rune isn't in your pouch.";
        if (!TryId(a, 1, out var iid) || Owned(p, iid, out var worn) is not { } item) return "Choose an item for the rune.";
        var sockets = Runes.SocketsOf(item);
        if (sockets.Count == 0) return "That item has no sockets.";
        int idx = IntArg(a, 2, -1);
        if (idx < 0) idx = sockets.FindIndex(x => x is null);
        if (idx < 0 || idx >= sockets.Count) return "Every socket is taken: take a rune out first.";
        if (sockets[idx] is { } old) p.Extras.Runes.Add(old);
        sockets[idx] = rune;
        p.Extras.Runes.Remove(rune);
        p.Extras.Count("socket");
        if (worn is not null) s.Instance!.World.RecalcStats(p);
        Tell(s, $"{{#9FC6FF}}{Runes.Name(rune)} set in {item.DisplayName}.{{/}}");
        return null;
    }

    /// <summary>Runes can be taken out of any item, anywhere, at no cost.</summary>
    private string? UnsocketRune(Session s, Entity p, string[] a)
    {
        if (!TryId(a, 0, out var iid) || Owned(p, iid, out var worn) is not { } item) return "Choose an item.";
        var sockets = Runes.SocketsOf(item);
        int idx = IntArg(a, 1, -1);
        if (idx < 0 || idx >= sockets.Count || sockets[idx] is not { } rune) return "That socket is empty.";
        if (p.Extras.Runes.Count >= Runes.PouchLimit) return "Your rune pouch is full.";
        sockets[idx] = null;
        p.Extras.Runes.Add(rune);
        if (worn is not null) s.Instance!.World.RecalcStats(p);
        Tell(s, $"{Runes.Name(rune)} taken out of {item.DisplayName}.");
        return null;
    }

    private string? FuseRunes(Session s, Entity p, string[] a)
    {
        if (NearService(s, "runes") is null) return "Rune fusing is done at the forge, by Gilles the Runecarver.";
        if (!TryId(a, 0, out var ia) || !TryId(a, 1, out var ib)) return "Choose two runes.";
        var ra = p.Extras.Runes.FirstOrDefault(r => r.Id == ia);
        var rb = p.Extras.Runes.FirstOrDefault(r => r.Id == ib);
        if (ra is null || rb is null) return "Both runes must be in your pouch (take them out of your gear first).";
        if (Runes.CanFuse(ra, rb) is string why) return why;
        p.Extras.Runes.Remove(ra);
        p.Extras.Runes.Remove(rb);
        var r = Runes.Fuse(ra, rb);
        p.Extras.Runes.Add(r);
        Fx(s, "fuse", $"{Runes.Name(r)}", $"Two runes at the top of their craft become one of a finer kind. It starts again at level 1 and adds {Runes.Value(r)} {Runes.Kind(r.Kind)!.Property}.", null, r.Tier, r);
        return null;
    }

    // ---- the Athanor

    private string? DoAthanor(Session s, Entity p, string[] a)
    {
        if (NearService(s, "athanor") is null) return "The Athanor burns in Aubry's forge.";
        var ids = new List<long>();
        for (int i = 0; i < a.Length; i++) if (long.TryParse(a[i], out var id) && !ids.Contains(id)) ids.Add(id);
        var items = ids.Select(id => p.Inventory.FirstOrDefault(i => i.Id == id)).ToList();
        if (items.Count != 4 || items.Any(i => i is null)) return "The Athanor takes four items from your bag.";
        var four = items.Select(i => i!).ToList();
        var rng = Random.Shared;
        Item result;
        string text;
        int level = (int)System.Math.Round(four.Average(i => i.ItemLevel));
        if (four.Count(i => i.Rarity == Rarity.Exotic) == 1 && four.Count(i => i.Rarity == Rarity.Legendary) == 3)
        {
            // One Exotic, three Legendary, a trophy relic and five Soul Cores: a Mythic piece of the Exotic's kind.
            if (p.Extras.Has(Materials.SoulCore) < 5 || p.Extras.Has(Materials.TrophyRelic) < 1) return "A Mythic piece also needs 5 Soul Cores and a Trophy Relic.";
            p.Extras.Spend(Materials.SoulCore, 5); p.Extras.Spend(Materials.TrophyRelic, 1);
            var ex = four.First(i => i.Rarity == Rarity.Exotic);
            result = Smithing.MakeHigh(rng, System.Math.Max(level, ex.ItemLevel), Rarity.Mythic, ex.Slot);
            text = "The furnace roars white. Something old is born in it.";
        }
        else if (four.All(i => i.Rarity == Rarity.Legendary))
        {
            if (p.Extras.Has(Materials.SoulCore) < 2) return "Four Legendary pieces become an Exotic one, with 2 Soul Cores.";
            p.Extras.Spend(Materials.SoulCore, 2);
            result = Smithing.MakeHigh(rng, level, Rarity.Exotic);
            text = "Four legends melt into one exotic piece.";
        }
        else if (four.All(i => i.Rarity == four[0].Rarity) && four[0].Rarity <= Rarity.Epic)
        {
            var r = four[0].Rarity;
            if (rng.NextDouble() < 0.25) r++;
            result = ItemSets.MaybeSet(rng, Loot.MakeItem(rng, Loot.NewId(), System.Math.Min(p.Level + 3, four.Max(i => i.ItemLevel) + 2), r), 0.1);
            text = r > four[0].Rarity ? "The furnace flares: the result is finer than what went in." : "Four become one, of the same quality.";
        }
        else return "The Athanor takes four items of one rarity; or four Legendary (and 2 Soul Cores) for an Exotic; or one Exotic and three Legendary (with 5 Soul Cores and a Trophy Relic) for a Mythic.";
        foreach (var i in four) { Reclaim(s, p, i); p.Inventory.Remove(i); }
        p.Extras.Count("athanor");
        Grant(s, p, result, "From the Athanor: ");
        Fx(s, "mystic", result.DisplayName, text, result, result.Rarity);
        return null;
    }

    private string? DoAthanorRunes(Session s, Entity p, string[] a)
    {
        if (NearService(s, "athanor") is null) return "The Athanor burns in Aubry's forge.";
        var runes = a.Select(x => long.TryParse(x, out var id) ? p.Extras.Runes.FirstOrDefault(r => r.Id == id) : null).Distinct().ToList();
        if (runes.Count != 3 || runes.Any(r => r is null)) return "The Athanor takes three runes from your pouch.";
        var tier = runes[0]!.Tier;
        if (runes.Any(r => r!.Tier != tier)) return "All three runes must be of one tier.";
        if (!p.Extras.Spend(Materials.SoulCore, 1)) return "Recasting runes needs a Soul Core.";
        foreach (var r in runes) p.Extras.Runes.Remove(r!);
        var made = Runes.Make(Random.Shared, tier);
        p.Extras.Runes.Add(made);
        p.Extras.Count("athanor");
        Fx(s, "mystic", Runes.Name(made), "Three runes melt and run together into a new one.", null, tier, made);
        return null;
    }

    // ---- dailies

    private static void ResetDailyIfNeeded(Entity p)
    {
        string today = Endgame.Today(DateTime.UtcNow);
        if (p.Extras.Daily.Date == today) return;
        p.Extras.Daily = new DailyState { Date = today };
    }

    private List<DailyTask> TasksFor(Session s) =>
        Endgame.Pledges(_data, Endgame.Today(DateTime.UtcNow)).Concat(Endgame.Endeavours(s.Name, Endgame.Today(DateTime.UtcNow))).ToList();

    private static string RewardText(DailyTask t) =>
        string.Join(", ", t.Reward.Select(kv => $"{kv.Value} {Materials.Name(kv.Key)}").Append($"{t.Gold:N0} gold"));

    private void SendDaily(Session s)
    {
        if (s.Entity is not { } p) return;
        ResetDailyIfNeeded(p);
        var d = p.Extras.Daily;
        var now = DateTime.UtcNow;
        var mz = _mistZoneId is not null ? _data.Zones[_mistZoneId].Name : "";
        SendUi(s, UiKind.Daily, new DailyView(d.Date,
            TasksFor(s).Select(t => new DailyTaskView(t.Id, t.Pledge, t.Text, System.Math.Min(t.Target, d.Tally.GetValueOrDefault(t.Key)), t.Target, d.Claimed.Contains(t.Id), RewardText(t))).ToList(),
            Endgame.ThisWeek(now).Select(i => new InstabilityView(i.Name, i.Text)).ToList(),
            new DateTimeOffset(Endgame.NextWeek(now)).ToUnixTimeSeconds(), mz, new DateTimeOffset(_mistEnds).ToUnixTimeSeconds()));
    }

    private string? ClaimDaily(Session s, Entity p, string id)
    {
        if (NearService(s, "daily") is null) return "Claim your rewards from Brother Almoner in the Sanctuary.";
        ResetDailyIfNeeded(p);
        var t = TasksFor(s).FirstOrDefault(x => x.Id == id);
        if (t is null) return "That task isn't on today's list.";
        var d = p.Extras.Daily;
        if (d.Claimed.Contains(t.Id)) return "Already claimed today.";
        if (d.Tally.GetValueOrDefault(t.Key) < t.Target) return "Not done yet.";
        d.Claimed.Add(t.Id);
        Tell(s, $"{{#F2D25A}}{(t.Pledge ? "Pledge" : "Endeavour")} fulfilled: {t.Text}.{{/}}");
        GrantMaterials(s, p, t.Reward, t.Gold);
        SendDaily(s);
        return null;
    }

    // ---- collection

    private string? ClaimCollection(Session s, Entity p, string setId)
    {
        if (ItemSets.Get(setId) is not { } set) return "No such set.";
        if (p.Extras.SetsClaimed.Contains(set.Id)) return "You have already been rewarded for that set.";
        var missing = set.Pieces.Where(pc => !p.Extras.Collection.Contains($"{set.Id}:{pc.Slot}")).ToList();
        if (missing.Count > 0) return $"Still missing: {string.Join(", ", missing.Select(m => Loot.SlotName(m.Slot)))}.";
        p.Extras.SetsClaimed.Add(set.Id);
        Tell(s, $"{{#6BD66B}}Collection complete: {set.Name}.{{/}}");
        GrantMaterials(s, p, new() { [Materials.SoulCore] = 2, [Materials.HolyOil] = 2, [Materials.Whetstone] = 5 }, 500 * set.Pieces.Count);
        return null;
    }

    // ---- armoury and loadouts

    private bool AtArmoury(Session s) => (s.Instance!.Zone.IsPersonal || s.Instance.Zone.IsGuildHall) && NearService(s, "armoury") is not null;

    private List<string> MissingFor(Entity p, Loadout l)
    {
        var have = p.Equipped.Values.Concat(p.Inventory).Concat(p.Extras.Armoury).Select(i => i.Id).ToHashSet();
        return l.Items.Where(kv => !have.Contains(kv.Value)).Select(kv => kv.Key).ToList();
    }

    private void SendArmoury(Session s)
    {
        if (s.Entity is not { } p) return;
        while (p.Extras.Loadouts.Count < Gear.MaxLoadouts) p.Extras.Loadouts.Add(new Loadout());
        SendUi(s, UiKind.Armoury, new ArmouryView(p.Extras.Armoury.Select(Msg.Info).ToList(), Gear.ArmouryLimit,
            p.Extras.Loadouts.Select(l =>
            {
                var miss = MissingFor(p, l);
                return new LoadoutView(l.Name, new Dictionary<string, long>(l.Items), l.Items.Count > 0 && miss.Count == 0, miss);
            }).ToList()));
    }

    private string? ArmouryPut(Session s, Entity p, string[] a)
    {
        if (!AtArmoury(s)) return "Use the armoury rack in your cell or your guild hall.";
        if (!TryId(a, 0, out var id) || p.Inventory.FirstOrDefault(i => i.Id == id) is not { } item) return "That item isn't in your bag.";
        if (p.Extras.Armoury.Count >= Gear.ArmouryLimit) return "The rack is full.";
        p.Inventory.Remove(item);
        p.Extras.Armoury.Add(item);
        SendArmoury(s);
        return null;
    }

    private string? ArmouryTake(Session s, Entity p, string[] a)
    {
        if (!AtArmoury(s)) return "Use the armoury rack in your cell or your guild hall.";
        if (!TryId(a, 0, out var id) || p.Extras.Armoury.FirstOrDefault(i => i.Id == id) is not { } item) return "That isn't on the rack.";
        if (p.Inventory.Count >= Gear.BagLimit(p)) return "Your bag is full.";
        p.Extras.Armoury.Remove(item);
        p.Inventory.Add(item);
        SendArmoury(s);
        return null;
    }

    private string? SaveLoadout(Session s, Entity p, string[] a)
    {
        if (!AtArmoury(s)) return "Use the armoury rack in your cell or your guild hall.";
        int i = IntArg(a, 0, -1);
        if (i < 0 || i >= Gear.MaxLoadouts) return "Choose a loadout.";
        while (p.Extras.Loadouts.Count < Gear.MaxLoadouts) p.Extras.Loadouts.Add(new Loadout());
        string name = new string((a.ElementAtOrDefault(1) ?? "").Where(c => !char.IsControl(c) && c != '{' && c != '}').Take(20).ToArray()).Trim();
        p.Extras.Loadouts[i] = new Loadout { Name = name.Length > 0 ? name : $"Loadout {i + 1}", Items = p.Equipped.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Id) };
        Tell(s, $"Saved what you wear as \"{p.Extras.Loadouts[i].Name}\".");
        SendArmoury(s);
        return null;
    }

    /// <summary>
    /// Swaps the whole harness at once. Every piece of the loadout must be in the house (worn, in the bag or on the rack);
    /// whatever is taken off goes onto the rack.
    /// </summary>
    private string? EquipLoadout(Session s, Entity p, string[] a)
    {
        if (!AtArmoury(s)) return "Loadouts can only be put on in your cell or your guild hall, where the rack is.";
        int idx = IntArg(a, 0, -1);
        if (idx < 0 || idx >= p.Extras.Loadouts.Count || p.Extras.Loadouts[idx].Items.Count == 0) return "That loadout is empty.";
        var l = p.Extras.Loadouts[idx];
        var miss = MissingFor(p, l);
        if (miss.Count > 0) return $"Not everything is here: {string.Join(", ", miss.Select(m => Enum.TryParse<ItemSlot>(m, out var ms) ? Loot.SlotName(ms) : m))}.";
        var want = new Dictionary<ItemSlot, Item>();
        foreach (var (slotName, id) in l.Items)
        {
            if (!Enum.TryParse<ItemSlot>(slotName, out var slot)) continue;
            var item = p.Equipped.Values.Concat(p.Inventory).Concat(p.Extras.Armoury).First(i => i.Id == id);
            if (item.ItemLevel > p.Level) return $"{item.DisplayName} needs level {item.ItemLevel}.";
            want[slot] = item;
        }
        var off = p.Equipped.Where(kv => !want.Values.Any(w => w.Id == kv.Value.Id)).ToList();
        int freed = want.Values.Count(w => p.Extras.Armoury.Any(r => r.Id == w.Id));
        if (p.Extras.Armoury.Count - freed + off.Count > Gear.ArmouryLimit) return "The rack has no room for what you would take off.";
        foreach (var (slot, item) in off) { p.Equipped.Remove(slot); p.Extras.Armoury.Add(item with { Slot = Loot.Home(item.Slot) }); }
        foreach (var (slot, item) in want)
        {
            p.Inventory.RemoveAll(i => i.Id == item.Id);
            p.Extras.Armoury.RemoveAll(i => i.Id == item.Id);
            foreach (var kv in p.Equipped.Where(kv => kv.Value.Id == item.Id && kv.Key != slot).ToList()) p.Equipped.Remove(kv.Key);
            p.Equipped[slot] = item with { Slot = slot };
        }
        var world = s.Instance!.World;
        if (Weapons.Of(world.WeaponOf(p)).TwoHanded && p.Equipped.Remove(ItemSlot.Shield, out var shield)) p.Extras.Armoury.Add(shield);
        world.RecalcStats(p);
        Fx(s, "loadout", l.Name, "You change into your harness.", null, Rarity.Rare);
        SendArmoury(s);
        return null;
    }

    // ---- pets and relic arts

    private string? BuyPet(Session s, Entity p, string id)
    {
        if (NearService(s, "pets") is null) return "Mathilde of the Menagerie keeps the beasts, in the Sanctuary.";
        if (Endgame.Pet(id) is not { } pet) return "No such companion.";
        if (p.Extras.Pets.Contains(id)) return "You already own one.";
        if (p.Gold < pet.Gold) return $"The {pet.Name} costs {pet.Gold:N0} gold.";
        if (p.Extras.Has(Materials.PetToken) < pet.Tokens) return $"The {pet.Name} also costs {pet.Tokens} Menagerie Tokens (from the great hunts).";
        p.Gold -= pet.Gold;
        p.Extras.Spend(Materials.PetToken, pet.Tokens);
        p.Extras.Pets.Add(id);
        p.Extras.ActivePet = id;
        p.PetIndex = Endgame.PetIndex(id);
        Tell(s, $"{{#F2D25A}}The {pet.Name} is yours.{{/}} It follows you now.");
        _store.Save(s.Record = Snapshot(s, p));
        return null;
    }

    private string? LearnRelic(Session s, Entity p, string spellId)
    {
        if (!_data.Spells.TryGetValue(spellId, out var sp) || sp.Field != GameData.RelicField) return "No such relic art.";
        if (p.Extras.Relics.Contains(sp.Id)) return $"You know {sp.Name} already.";
        if (!p.Extras.Spend(Materials.TrophyRelic, 1)) return "Each relic art costs a Trophy Relic, won from the world bosses.";
        p.Extras.Relics.Add(sp.Id);
        s.Instance!.World.RefreshPlayer(p);
        Tell(s, $"{{#F2D25A}}You learn {sp.Name}.{{/}} {sp.Description}");
        return null;
    }
}
