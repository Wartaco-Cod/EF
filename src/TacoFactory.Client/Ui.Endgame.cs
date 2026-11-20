using TacoFactory.Client.Rendering;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// The forge room (Master Aubry's forge, the Runecarver, the Athanor) and its cutscene; the Almoner's pledges; the
/// Herald's muster; the armoury rack; the menagerie; the Book of Deeds; and the endgame HUD (Mist, Pit, revives, pets).
/// </summary>
public sealed partial class GameClient
{
    private string _forgeTab = "temper";
    private readonly List<long> _athanor = [];
    private readonly List<long> _athanorRunes = [];
    private long? _fuseA, _fuseB;
    private HeraldState? _herald;
    private DailyView? _daily;
    private ArmouryView? _armoury;
    private string _petsNpc = "";
    private string _bookTab = "sets";
    private readonly Field _loadoutName = new() { Id = "loadout.name", Max = 20 };

    private void HandleEndgameUi(string kind, string json)
    {
        switch (kind)
        {
            case UiKind.Extras: _extras = UiKind.Deserialize<ExtrasState>(json); break;
            case UiKind.ForgeFx: if (UiKind.Deserialize<ForgeFx>(json) is { } fx) StartForgeScene(fx); break;
            case UiKind.Herald: _herald = UiKind.Deserialize<HeraldState>(json); Open(Win.Herald); break;
            case UiKind.Daily: _daily = UiKind.Deserialize<DailyView>(json); Open(Win.Daily); break;
            case UiKind.Armoury: _armoury = UiKind.Deserialize<ArmouryView>(json); Open(Win.Armoury); break;
            case UiKind.Pets: _petsNpc = UiKind.Deserialize<string>(json) ?? ""; Open(Win.Pets); break;
        }
    }

    private void ToggleAthanor(long id)
    {
        if (!_athanor.Remove(id) && _athanor.Count < 4) _athanor.Add(id);
    }

    private int Mat(string id) => _extras?.Materials.GetValueOrDefault(id) ?? 0;

    private void Tabs(float x, float y, params (string Id, string Label)[] tabs)
    {
        float bx = x;
        foreach (var (id, label) in tabs)
        {
            float bw = _small.Measure(label) + 30;
            string tid = id;
            Button(bx, y, bw, 32, label, () => _forgeTab = tid, tone: _forgeTab == tid ? "gold" : "plain");
            bx += bw + 8;
        }
    }

    private void Wallet(float x, float y, params string[] ids)
    {
        float wx = x;
        foreach (var id in ids)
        {
            MaterialIcon(id, wx, y, 24);
            _small.Draw(_batch, $"{Mat(id)}", wx + 28, y, Color.FromHex("#E8E4DA"));
            wx += 28 + _small.Measure($"{Mat(id)}") + 18;
        }
    }

    // ================================================================== the forge (Master Aubry)

    private void DrawForge(PlayerStats? st)
    {
        switch (_forge?.Mode)
        {
            case "runes": DrawRunecarver(st); return;
            case "athanor": DrawAthanor(st); return;
        }
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, "MASTER AUBRY'S FORGE");
        Tabs(x + 24, y0 + 58, ("temper", "Temper"), ("combine", "Combine"), ("salvage", "Salvage"), ("traits", "Traits"), ("empower", "Empower"));
        Wallet(x + 24, y0 + h - 42, Materials.Whetstone, Materials.HolyOil);
        _small.Draw(_batch, $"{{#E6C14A}}{st?.Gold ?? 0:N0} gold{{/}}", x + 330, y0 + h - 42, Color.White);
        var all = (st?.Inventory ?? []).Concat(st?.Equipped ?? []).ToList();
        var a = all.FirstOrDefault(i => i.Id == _forgeA);
        var b = st?.Inventory.FirstOrDefault(i => i.Id == _forgeB);
        float y = y0 + 110;
        DropZone(x, y0, w, h, (from, _) => from is DragFrom.Bag or DragFrom.Equipped, (_, it) => { _forgeA = it.Id; if (_forgeB == it.Id) _forgeB = null; });
        switch (_forgeTab)
        {
            case "combine": ForgeCombine(x, w, y, y0, h, a, b); break;
            case "salvage": ForgeSalvage(x, w, y, y0, h, a); break;
            case "traits": ForgeTraits(x, w, y, y0, h, a, st); break;
            case "empower": ForgeEmpower(x, w, y, y0, h, a, b); break;
            default: ForgeTemper(x, w, y, y0, h, a, st); break;
        }
    }

    private float AnvilCard(float x, float w, float y, string label, ItemInfo? item, string empty)
    {
        _batch.FillRect(x + 24, y, w - 48, 96, new Color(36, 30, 22, 255));
        _small.Draw(_batch, label, x + 36, y + 8, Color.FromHex("#C9A227"));
        if (item is null) { _small.Draw(_batch, $"{{#6A6050}}{empty}{{/}}", x + 140, y + 36, Color.White); return y + 108; }
        ItemTile(item, x + 36, y + 32, 52, false);
        if (Over(x + 36, y + 32, 52, 52)) Tip(item);
        _small.Draw(_batch, $"{{{NameHex(item)}}}{item.Name}{{/}}", x + 104, y + 30, Color.White);
        _small.Draw(_batch, $"{{#9A9080}}{ItemDetail(item)}{{/}}", x + 104, y + 56, Color.White);
        return y + 108;
    }

    private void ForgeTemper(float x, float w, float y, float y0, float h, ItemInfo? a, PlayerStats? st)
    {
        _small.Draw(_batch, "{#9A9080}Each temper adds 8% damage or defense and 6% to every property. Past +4 the steel needs Holy Oil{/}", x + 24, y, Color.White);
        _small.Draw(_batch, "{#9A9080}and may shatter; runes in it come back to your pouch. Exotic and Mythic pieces are empowered instead.{/}", x + 24, y + 24, Color.White);
        y = AnvilCard(x, w, y + 60, "On the anvil", a, "choose an item from your bag or what you wear (right-click it)");
        if (a is null) return;
        var item = ToItem(a);
        if (Smithing.CanTemper(item) is string why) { _small.Draw(_batch, $"{{#E0A060}}{why}{{/}}", x + 24, y + 8, Color.White); return; }
        var next = Msg.Info(Smithing.Temper(item));
        _ui.Draw(_batch, $"+{a.Upgrade}  ›  {{#F2D25A}}+{next.Upgrade}{{/}}", x + 24, y + 4, Color.FromHex("#E8E4DA"));
        y += 44;
        if (a.Damage > 0) { _small.Draw(_batch, $"Damage {a.Damage}  ›  {{#6BD66B}}{next.Damage}{{/}}", x + 36, y, Color.White); y += 26; }
        if (a.Defense > 0) { _small.Draw(_batch, $"Defense {a.Defense}  ›  {{#6BD66B}}{next.Defense}{{/}}", x + 36, y, Color.White); y += 26; }
        foreach (var (k, v) in a.Props) { _small.Draw(_batch, $"{{#9FC6FF}}{Loot.PropertyTitle(k)}{{/}} {v}  ›  {{#6BD66B}}{next.Props.GetValueOrDefault(k)}{{/}}", x + 36, y, Color.White); y += 26; }
        y += 10;
        int gold = Smithing.TemperGold(item), stones = Smithing.TemperWhetstones(item), oil = Smithing.TemperOil(item);
        double brk = Smithing.BreakChance(item);
        _small.Draw(_batch, $"Cost: {{#E6C14A}}{gold:N0} gold{{/}}, {stones} whetstone{(stones == 1 ? "" : "s")}{(oil > 0 ? $", {oil} Holy Oil" : "")}", x + 24, y, Color.White);
        y += 28;
        _small.Draw(_batch, brk > 0 ? $"{{#E0463C}}Chance to shatter: {brk * 100:F0}%{{/}}" : "{#6BD66B}No risk at this level.{/}", x + 24, y, Color.White);
        bool can = (st?.Gold ?? 0) >= gold && Mat(Materials.Whetstone) >= stones && Mat(Materials.HolyOil) >= oil;
        string sid = a.Id.ToString();
        Button(x + w - 244, y0 + h - 70, 220, 44, "Strike the anvil", () => _conn.Command("temper", sid), enabled: can && !SceneBusy);
    }

    private void ForgeCombine(float x, float w, float y, float y0, float h, ItemInfo? a, ItemInfo? b)
    {
        _small.Draw(_batch, "{#9A9080}Two items become one: the chosen item's type, their average rarity (rounded up) and level,{/}", x + 24, y, Color.White);
        _small.Draw(_batch, "{#9A9080}and every magical property of both. Shared properties are averaged.{/}", x + 24, y + 24, Color.White);
        y = AnvilCard(x, w, y + 60, "Anvil", a, "choose an item (right-click it in your bag)");
        y = AnvilCard(x, w, y, "Second", b, "choose a second item: select it and press Second");
        if (a is null || b is null) return;
        Button(x + 24, y, 300, 30, $"Keep the type of the first ({Loot.SlotName(a.Slot)})", () => _forgeKeepFirst = true, tone: _forgeKeepFirst ? "gold" : "plain");
        Button(x + 340, y, 300, 30, $"Keep the type of the second ({Loot.SlotName(b.Slot)})", () => _forgeKeepFirst = false, tone: !_forgeKeepFirst ? "gold" : "plain");
        y += 48;
        var ia = ToItem(a);
        var ib = ToItem(b);
        if (Forge.CanCombine(ia, ib) is string problem) { _small.Draw(_batch, $"{{#E0A060}}{problem}{{/}}", x + 24, y, Color.White); return; }
        var ri = Msg.Info(Forge.Combine(ia, ib, _forgeKeepFirst));
        _ui.Draw(_batch, "Result", x + 24, y, Color.FromHex("#C9A227"));
        _small.Draw(_batch, $"{{{NameHex(ri)}}}{ri.Name}{{/}}  {{#9A9080}}{ItemDetail(ri)}{{/}}", x + 24, y + 34, Color.White);
        _small.Draw(_batch, PropsLine(ri), x + 24, y + 60, Color.FromHex("#9FC6FF"));
        string sa = a.Id.ToString(), sb = b.Id.ToString(), keep = _forgeKeepFirst ? "1" : "0";
        Button(x + w - 244, y0 + h - 70, 220, 44, "Strike the anvil", () => { _conn.Command("forge", sa, sb, keep); _forgeA = _forgeB = null; }, enabled: !SceneBusy);
    }

    private void ForgeSalvage(float x, float w, float y, float y0, float h, ItemInfo? a)
    {
        _small.Draw(_batch, "{#9A9080}Take a piece apart for Whetstones (and sometimes Holy Oil). Runes in it return to your pouch.{/}", x + 24, y, Color.White);
        y = AnvilCard(x, w, y + 40, "To salvage", a, "choose an item from your bag");
        if (a is null) return;
        bool worn = _conn.Stats?.Equipped.Any(e => e.Id == a.Id) == true;
        int stones = a.Rarity switch { <= Rarity.Rare => 1, Rarity.Elite => 2, Rarity.Epic => 3, _ => 5 } + a.Upgrade / 2;
        _small.Draw(_batch, $"Yields about {{#E8E4DA}}{stones} whetstone{(stones == 1 ? "" : "s")}{{/}}{(a.Rarity >= Rarity.Legendary ? " and 1 Holy Oil" : "")}.", x + 24, y, Color.White);
        if (worn) _small.Draw(_batch, "{#E0A060}Take it off first.{/}", x + 24, y + 28, Color.White);
        string sid = a.Id.ToString();
        Button(x + w - 244, y0 + h - 70, 220, 44, "Salvage", () => { _conn.Command("salvage", sid); _forgeA = null; }, enabled: !worn && !SceneBusy, tone: "red");
    }

    private void ForgeTraits(float x, float w, float y, float y0, float h, ItemInfo? a, PlayerStats? st)
    {
        _small.Draw(_batch, "{#9A9080}Research takes an item apart to learn its trait. Once known, a trait can be put on any piece of{/}", x + 24, y, Color.White);
        _small.Draw(_batch, "{#9A9080}its family with Holy Oil. A piece has one trait at a time.{/}", x + 24, y + 24, Color.White);
        y = AnvilCard(x, w, y + 60, "On the bench", a, "choose an item");
        var known = _extras?.Researched.ToHashSet() ?? [];
        if (a is not null && Traits.Get(a.Trait) is { } has && !known.Contains(Traits.Key(has)))
        {
            bool worn = st?.Equipped.Any(e => e.Id == a.Id) == true;
            int gold = Smithing.ResearchGold(ToItem(a));
            string sid = a.Id.ToString();
            Button(x + 24, y, 340, 32, $"Research {has.Name} ({gold:N0}g, destroys it)", () => { _conn.Command("research", sid); _forgeA = null; }, enabled: !worn && (st?.Gold ?? 0) >= gold && !SceneBusy, tone: "red");
            y += 44;
        }
        float cx = x + 24;
        foreach (var fam in Enum.GetValues<TraitFamily>())
        {
            float ty = y;
            _small.Draw(_batch, $"{{#C9A227}}{fam}{{/}}", cx, ty, Color.White);
            ty += 28;
            foreach (var t in Traits.All.Where(t => t.Family == fam))
            {
                bool k = known.Contains(Traits.Key(t));
                bool fits = a is not null && Traits.FamilyOf(a.Slot) == fam && k && a.Trait != t.Id;
                string label = $"{(k ? "{#6BD66B}" : "{#5A5448}")}{t.Name}{{/}}";
                if (fits)
                {
                    string sid = a!.Id.ToString(), tid = t.Id;
                    int gold = Smithing.ApplyTraitGold(ToItem(a));
                    Button(cx, ty, 214, 28, $"Apply {t.Name}", () => _conn.Command("trait", sid, tid), enabled: Mat(Materials.HolyOil) >= 1 && (st?.Gold ?? 0) >= gold && !SceneBusy, tone: "blue");
                }
                else _small.Draw(_batch, label + (a?.Trait == t.Id ? " {#9A9080}(on it){/}" : ""), cx + 4, ty + 2, Color.White);
                if (Over(cx, ty, 214, 28)) { _tipItem = null; }
                ty += 32;
            }
            cx += 228;
        }
        if (a is not null) _small.Draw(_batch, $"{{#7A7060}}Applying costs 1 Holy Oil and {Smithing.ApplyTraitGold(ToItem(a)):N0} gold.{{/}}", x + 24, y0 + h - 76, Color.White);
    }

    private void ForgeEmpower(float x, float w, float y, float y0, float h, ItemInfo? a, ItemInfo? b)
    {
        _small.Draw(_batch, "{#9A9080}Exotic and Mythic pieces can't be tempered. They grow by swallowing another piece of the same{/}", x + 24, y, Color.White);
        _small.Draw(_batch, "{#9A9080}rarity: each sacrifice adds a rank (+6% damage or defense, +5% properties), up to rank 10.{/}", x + 24, y + 24, Color.White);
        y = AnvilCard(x, w, y + 60, "To empower", a, "an Exotic or Mythic piece");
        y = AnvilCard(x, w, y, "Sacrifice", b, "another piece of the same rarity, from your bag");
        if (a is null || b is null) return;
        if (Smithing.CanEmpower(ToItem(a), ToItem(b)) is string why) { _small.Draw(_batch, $"{{#E0A060}}{why}{{/}}", x + 24, y, Color.White); return; }
        string sa = a.Id.ToString(), sb = b.Id.ToString();
        Button(x + w - 244, y0 + h - 70, 220, 44, $"Empower to rank {a.Upgrade + 1}", () => { _conn.Command("empower", sa, sb); _forgeB = null; }, enabled: !SceneBusy);
    }

    // ================================================================== the Runecarver

    private void DrawRunecarver(PlayerStats? st)
    {
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, "GILLES THE RUNECARVER");
        _small.Draw(_batch, "{#9A9080}Runes grow as you fight: kills made while they are worn, blows they strike or turn aside.{/}", x + 24, y0 + 58, Color.White);
        _small.Draw(_batch, $"{{#9A9080}}Two runes of one kind and tier, both at level {Runes.MaxLevel}, fuse into one of the next tier.{{/}}", x + 24, y0 + 82, Color.White);
        _small.Draw(_batch, "{#9A9080}Set runes in your gear from the Runes tab of your bag. Taking them out is free, anywhere.{/}", x + 24, y0 + 106, Color.White);
        var runes = _extras?.Runes ?? [];
        float y = y0 + 146;
        const int cols = 10;
        const float S = 52, gs = 64;
        for (int i = 0; i < runes.Count && i < cols * 5; i++)
        {
            var r = runes[i];
            float gx = x + 30 + i % cols * gs, gy = y + i / cols * gs;
            RuneIcon(r, gx, gy, S);
            bool pick = _fuseA == r.Id || _fuseB == r.Id;
            if (pick) _batch.FillRect(gx, gy + S + 2, S, 4, Color.FromHex("#F2D25A"));
            long rid = r.Id;
            _buttons.Add((gx, gy, S, S, () =>
            {
                if (_fuseA == rid) _fuseA = null; else if (_fuseB == rid) _fuseB = null;
                else if (_fuseA is null) _fuseA = rid; else _fuseB = rid;
            }));
            if (Over(gx, gy, S, S)) Tip(r);
        }
        if (runes.Count == 0) _small.Draw(_batch, "{#6A6050}Your pouch is empty. Runes drop from the fallen, now and then; champions drop them more often.{/}", x + 30, y + 10, Color.White);
        var ra = runes.FirstOrDefault(r => r.Id == _fuseA);
        var rb = runes.FirstOrDefault(r => r.Id == _fuseB);
        float fy = y0 + 500;
        _batch.FillRect(x + 24, fy, w - 48, 200, new Color(36, 30, 22, 255));
        _ui.Draw(_batch, "Fusion", x + 40, fy + 12, Color.FromHex("#C9A227"));
        if (ra is not null) { RuneIcon(ra, x + 60, fy + 70, 64); _small.Draw(_batch, $"{{{TierHex(ra.Tier)}}}{ra.Name}{{/}} L{ra.Level}", x + 40, fy + 146, Color.White); }
        _title.Draw(_batch, "+", x + 196, fy + 80, Color.FromHex("#9A9080"));
        if (rb is not null) { RuneIcon(rb, x + 250, fy + 70, 64); _small.Draw(_batch, $"{{{TierHex(rb.Tier)}}}{rb.Name}{{/}} L{rb.Level}", x + 230, fy + 146, Color.White); }
        if (ra is null || rb is null) { _small.Draw(_batch, "{#6A6050}Click two runes above.{/}", x + 420, fy + 90, Color.White); return; }
        var why = Runes.CanFuse(new Rune { Id = ra.Id, Kind = ra.Kind, Tier = ra.Tier, Level = ra.Level }, new Rune { Id = rb.Id, Kind = rb.Kind, Tier = rb.Tier, Level = rb.Level });
        if (why is not null) { _small.Draw(_batch, $"{{#E0A060}}{why}{{/}}", x + 380, fy + 90, Color.White); return; }
        var made = new RuneInfo(0, ra.Kind, ra.Tier + 1, 1, 0, 0, Runes.Value(new Rune { Kind = ra.Kind, Tier = ra.Tier + 1, Level = 1 }));
        _title.Draw(_batch, "=", x + 360, fy + 80, Color.FromHex("#9A9080"));
        RuneIcon(made, x + 420, fy + 70, 64);
        _small.Draw(_batch, $"{{{TierHex(made.Tier)}}}{made.Name}{{/}}", x + 400, fy + 146, Color.White);
        string sa = ra.Id.ToString(), sb = rb.Id.ToString();
        Button(x + w - 244, y0 + h - 70, 220, 44, "Fuse the runes", () => { _conn.Command("rune.fuse", sa, sb); _fuseA = _fuseB = null; }, enabled: !SceneBusy);
    }

    // ================================================================== the Athanor

    private void DrawAthanor(PlayerStats? st)
    {
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, "THE ATHANOR");
        _small.Draw(_batch, "{#9A9080}The alchemists' furnace takes four and gives back one:{/}", x + 24, y0 + 58, Color.White);
        float ry = y0 + 86;
        foreach (var line in new[]
        {
            "Four items of one rarity (up to Epic): one new item of that rarity, one time in four the next.",
            "Four Legendary items and 2 Soul Cores: an Exotic piece.",
            "One Exotic and three Legendary, 5 Soul Cores and a Trophy Relic: a Mythic piece of the Exotic's kind.",
            "Three runes of one tier and a Soul Core: a new rune of that tier.",
        })
        { _small.Draw(_batch, $"{{#C9A227}}·{{/}} {{#9A9080}}{line}{{/}}", x + 30, ry, Color.White); ry += 26; }
        Wallet(x + 24, y0 + h - 42, Materials.SoulCore, Materials.TrophyRelic);

        var bag = st?.Inventory ?? [];
        _athanor.RemoveAll(id => bag.All(i => i.Id != id));
        float sy = y0 + 210;
        _ui.Draw(_batch, "Into the fire", x + 24, sy, Color.FromHex("#C9A227"));
        _small.Draw(_batch, "{#7A7060}right-click items in your bag{/}", x + 210, sy + 6, Color.White);
        DropZone(x + 24, sy + 36, w - 48, 90, (from, _) => from == DragFrom.Bag, (_, it) => ToggleAthanor(it.Id));
        for (int i = 0; i < 4; i++)
        {
            float bx = x + 40 + i * 120, by = sy + 40;
            var it = i < _athanor.Count ? bag.FirstOrDefault(b => b.Id == _athanor[i]) : null;
            if (it is null) { _batch.FillRect(bx, by, 80, 80, new Color(36, 30, 22, 255)); continue; }
            ItemTile(it, bx, by, 80, false);
            if (Over(bx, by, 80, 80)) Tip(it);
            long id = it.Id;
            _buttons.Add((bx, by, 80, 80, () => _athanor.Remove(id)));
        }
        if (_athanor.Count == 4)
        {
            var ids = _athanor.Select(i => i.ToString()).ToArray();
            Button(x + w - 244, sy + 56, 220, 44, "Feed the Athanor", () => { _conn.Command("athanor", ids); _athanor.Clear(); }, enabled: !SceneBusy);
        }

        float rz = y0 + 380;
        _ui.Draw(_batch, "Recast runes", x + 24, rz, Color.FromHex("#C9A227"));
        var runes = _extras?.Runes ?? [];
        _athanorRunes.RemoveAll(id => runes.All(r => r.Id != id));
        const int cols = 10;
        for (int i = 0; i < runes.Count && i < cols * 4; i++)
        {
            var r = runes[i];
            float gx = x + 30 + i % cols * 64, gy = rz + 40 + i / cols * 64;
            RuneIcon(r, gx, gy, 52);
            if (_athanorRunes.Contains(r.Id)) _batch.FillRect(gx, gy + 54, 52, 4, Color.FromHex("#B070FF"));
            long rid = r.Id;
            _buttons.Add((gx, gy, 52, 52, () => { if (!_athanorRunes.Remove(rid) && _athanorRunes.Count < 3) _athanorRunes.Add(rid); }));
            if (Over(gx, gy, 52, 52)) Tip(r);
        }
        if (_athanorRunes.Count == 3)
        {
            var ids = _athanorRunes.Select(i => i.ToString()).ToArray();
            Button(x + w - 244, y0 + h - 70, 220, 44, "Recast three runes", () => { _conn.Command("athanor.runes", ids); _athanorRunes.Clear(); }, enabled: Mat(Materials.SoulCore) >= 1 && !SceneBusy);
        }
    }

    // ================================================================== the forge cutscene

    private sealed class ForgeScene
    {
        public required ForgeFx Fx;
        public double Start;
        public int Strikes;
        public bool Resolved;
    }

    private ForgeScene? _scene;
    private readonly ParticleSystem _sceneParticles = new(2500);
    private const double SceneLength = 3.6, ResolveAt = 2.05;
    private bool SceneBusy => _scene is not null && Now - _scene.Start < SceneLength;

    private void StartForgeScene(ForgeFx fx)
    {
        if (fx.Kind is "loadout") { Sfx("ui_open", 0.6f); Log($"{{#9FC6FF}}{fx.Title}:{{/}} {fx.Text}"); return; }
        _scene = new ForgeScene { Fx = fx, Start = Now };
        _sceneParticles.Clear();
    }

    private bool Mystic => _scene?.Fx.Kind is "mystic";

    /// <summary>
    /// Plays over everything: the smith brings the hammer down three times in a shower of sparks, then the
    /// work answers — a burst of light the colour of its rarity, or a cloud of smoke if the steel broke.
    /// For the Athanor and the Runecarver, the furnace swirls instead of the hammer falling.
    /// </summary>
    private void DrawForgeScene(float dt)
    {
        if (_scene is not { } sc) return;
        double t = Now - sc.Start;
        if (t > SceneLength + 0.5) { _scene = null; return; }
        _blockRects.Add((0, 0, VW, VH));
        _buttons.Add((0, 0, VW, VH, () => { if (t > 0.8) sc.Start = Now - SceneLength; }));
        float fade = (float)Math.Clamp(Math.Min(t / 0.3, (SceneLength + 0.5 - t) / 0.5), 0, 1);
        _batch.FillRect(0, 0, VW, VH, new Color(6, 4, 3, (byte)(225 * fade)));
        var rc = Color.FromHex(sc.Fx.Kind == "break" ? "#8A8070" : RarityHex(sc.Fx.Rarity));
        float cx = VW / 2, groundY = 720;
        bool rune = sc.Fx.Kind is "fuse" || sc.Fx.Rune is not null;
        bool furnace = Mystic || rune;

        // Warm glow under the stage.
        _batch.SetBlend(BlendMode.Additive);
        _batch.Draw(_lightTex, cx - 520, groundY - 380, 1040, 640, (furnace ? Color.FromHex("#7040C0") : Color.FromHex("#FF7A30")) with { A = (byte)(110 * fade) });
        _batch.SetBlend(BlendMode.Alpha);

        // The stage: anvil and smith, or the Athanor.
        if (furnace)
        {
            string key = rune ? "sprites/prop_rune_bench_1.png" : "sprites/npc_athanor.png";
            float sz = 420;
            float pulse = 1 + 0.03f * MathF.Sin((float)t * 9);
            if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), cx - sz / 2 * pulse, groundY - sz * pulse + 30, sz * pulse, sz * pulse, Color.White with { A = (byte)(255 * fade) });
            // Swirl: motes spiralling in towards the mouth of the furnace.
            if (t < ResolveAt && _sceneParticles.Count < 1800)
                for (int i = 0; i < 6; i++)
                {
                    float ang = (float)(t * 5 + i * MathF.PI / 3), rr = 260 - (float)t * 90;
                    var at = new Vec2(cx + MathF.Cos(ang) * rr, groundY - 220 + MathF.Sin(ang) * rr * 0.45f);
                    _sceneParticles.Add(new Particle { Pos = at, Vel = (new Vec2(cx, groundY - 230) - at) * 1.6f, Life = 0.5f, Size0 = 14, Size1 = 3, C0 = rc with { A = 220 }, C1 = rc with { A = 0 }, Glow = true, Seed = i });
                }
        }
        else
        {
            const string anvil = "sprites/prop_anvil_1.png", smith = "sprites/npc_master_aubry.png";
            float asz = 300;
            if (_assets.Exists(anvil)) _batch.Draw(_assets.Texture(anvil), cx - asz / 2, groundY - asz + 40, asz, asz, Color.White with { A = (byte)(255 * fade) });
            else _batch.FillRect(cx - 110, groundY - 110, 220, 90, new Color(40, 40, 44, (byte)(255 * fade)));

            // Three blows: the hammer rises and falls on the beat.
            double[] beats = [0.55, 1.05, 1.55];
            float swing = 0;
            for (int i = 0; i < beats.Length; i++)
            {
                double d = t - beats[i];
                if (d > -0.28 && d < 0) swing = (float)(1 + d / 0.28); // raising
                if (d >= 0 && d < 0.08) swing = (float)(1 - d / 0.08) * 0.2f;
                if (d >= 0 && sc.Strikes == i)
                {
                    sc.Strikes++;
                    Sfx("anvil_strike", 0.9f);
                    _trauma = Math.Min(1, _trauma + 0.35f);
                    var tip = new Vec2(cx - 30, groundY - 170);
                    for (int k = 0; k < 70; k++)
                    {
                        float a = -MathF.PI / 2 + (Random.Shared.NextSingle() - 0.5f) * 2.6f, sp = 300 + Random.Shared.NextSingle() * 600;
                        _sceneParticles.Add(new Particle
                        {
                            Pos = tip, Vel = new Vec2(MathF.Cos(a), MathF.Sin(a)) * sp, Gravity = 0, Drag = 1.8f, Life = 0.4f + Random.Shared.NextSingle() * 0.5f,
                            Size0 = 5, Size1 = 1, C0 = Color.FromHex("#FFE6A0"), C1 = Color.FromHex("#FF6A20") with { A = 0 }, Glow = true, Streak = true,
                        });
                    }
                    _sceneParticles.Add(new Particle { Pos = tip, Life = 0.25f, Size0 = 40, Size1 = 260, C0 = Color.FromHex("#FFD080") with { A = 200 }, C1 = Color.FromHex("#FF8040") with { A = 0 }, Glow = true });
                }
            }
            float ssz = 380;
            float bob = -26 * swing;
            if (_assets.Exists(smith)) _batch.Draw(_assets.Texture(smith), cx - 340, groundY - ssz + 50 + bob * 0.3f, ssz, ssz, Color.White with { A = (byte)(255 * fade) });
            // The hammer: haft and head swinging about the smith's hand.
            var hand = new Vec2(cx - 150, groundY - 230 + bob * 0.3f);
            float ang2 = -0.15f - swing * 1.9f;
            var dir = new Vec2(MathF.Cos(ang2), MathF.Sin(ang2));
            var head = hand + dir * 150;
            _batch.DrawRotated(_disc, (hand.X + head.X) / 2, (hand.Y + head.Y) / 2, 150, 12, ang2, new Color(92, 62, 36, (byte)(255 * fade)));
            _batch.DrawRotated(_disc, head.X, head.Y, 44, 64, ang2, new Color(70, 72, 78, (byte)(255 * fade)));
        }

        // The answer.
        if (t >= ResolveAt && !sc.Resolved)
        {
            sc.Resolved = true;
            var at = new Vec2(cx, groundY - (furnace ? 240 : 190));
            if (sc.Fx.Kind == "break" || sc.Fx.Kind == "salvage" && false)
            {
                Sfx("forge_break", 1f);
                for (int k = 0; k < 160; k++)
                {
                    float a = Random.Shared.NextSingle() * MathF.PI * 2, sp = 40 + Random.Shared.NextSingle() * 220;
                    byte g = (byte)(90 + Random.Shared.Next(70));
                    _sceneParticles.Add(new Particle
                    {
                        Pos = at + new Vec2(Random.Shared.Next(-40, 41), Random.Shared.Next(-20, 21)), Vel = new Vec2(MathF.Cos(a) * sp, MathF.Sin(a) * sp * 0.5f - 80),
                        Drag = 0.9f, Life = 1.2f + Random.Shared.NextSingle() * 0.9f, Size0 = 40 + Random.Shared.Next(40), Size1 = 180 + Random.Shared.Next(120),
                        C0 = new Color(g, g, g, 200), C1 = new Color(g, g, g, 0), Wobble = 30, Seed = k,
                    });
                }
                for (int k = 0; k < 24; k++)
                {
                    float a = -MathF.PI / 2 + (Random.Shared.NextSingle() - 0.5f) * 3f, sp = 200 + Random.Shared.NextSingle() * 400;
                    _sceneParticles.Add(new Particle { Pos = at, Vel = new Vec2(MathF.Cos(a), MathF.Sin(a)) * sp, Gravity = -900, Drag = 0.4f, Life = 1.2f, Size0 = 10, Size1 = 8, C0 = new Color(60, 60, 64, 255), C1 = new Color(40, 40, 44, 0), Streak = true });
                }
                _trauma = Math.Min(1, _trauma + 0.5f);
            }
            else
            {
                Sfx(sc.Fx.Kind is "salvage" or "research" ? "forge_quench" : "forge_magic", 1f);
                int n = 120 + (int)sc.Fx.Rarity * 30;
                for (int k = 0; k < n; k++)
                {
                    float a = Random.Shared.NextSingle() * MathF.PI * 2, sp = 150 + Random.Shared.NextSingle() * (500 + 80 * (int)sc.Fx.Rarity);
                    _sceneParticles.Add(new Particle
                    {
                        Pos = at, Vel = new Vec2(MathF.Cos(a), MathF.Sin(a)) * sp, Drag = 1.3f, Life = 0.7f + Random.Shared.NextSingle() * 0.9f,
                        Size0 = 12, Size1 = 2, C0 = ParticleSystem.Lerp(rc, Color.White, 0.4f) with { A = 255 }, C1 = rc with { A = 0 }, Glow = true, Streak = Random.Shared.Next(3) == 0, Wobble = 20, Seed = k,
                    });
                }
                for (int ring = 0; ring < 3; ring++)
                    _sceneParticles.Add(new Particle { Pos = at, Life = 0.7f + ring * 0.25f, Size0 = 40, Size1 = 520 + ring * 200, C0 = rc with { A = 220 }, C1 = rc with { A = 0 }, Glow = true, Ring = true });
                _sceneParticles.Add(new Particle { Pos = at, Life = 0.5f, Size0 = 120, Size1 = 900, C0 = Color.White with { A = 230 }, C1 = rc with { A = 0 }, Glow = true });
            }
        }

        _sceneParticles.Update(dt);
        _sceneParticles.Draw(_batch, _softDot, _ringTex, v => v, 1, glow: false);
        _batch.SetBlend(BlendMode.Additive);
        // Rays of light behind the finished work.
        if (sc.Resolved && sc.Fx.Kind != "break")
        {
            float k = (float)Math.Clamp((t - ResolveAt) / 0.4, 0, 1) * fade;
            for (int i = 0; i < 12; i++)
            {
                float a = i * MathF.PI / 6 + (float)t * 0.4f;
                _batch.DrawRotated(_beamTex, cx + MathF.Cos(a) * 260, groundY - 200 + MathF.Sin(a) * 260, 520, 60, a, rc with { A = (byte)(90 * k) });
            }
        }
        _sceneParticles.Draw(_batch, _softDot, _ringTex, v => v, 1, glow: true);
        _batch.SetBlend(BlendMode.Alpha);

        if (sc.Resolved)
        {
            float k = (float)Math.Clamp((t - ResolveAt) / 0.35, 0, 1) * fade;
            float rise = 40 * (1 - k);
            if (sc.Fx.Result is { } it)
            {
                float size = 120 * (0.6f + 0.4f * k);
                ItemIcon(it, cx - size / 2, groundY - 520 + rise, size);
            }
            else if (sc.Fx.Rune is { } r) RuneIcon(r, cx - 60, groundY - 520 + rise, 120, level: false);
            string titleHex = sc.Fx.Kind == "break" ? "#E0463C" : RarityHex(sc.Fx.Rarity);
            _title.DrawCentered(_batch, sc.Fx.Title.ToUpperInvariant(), cx, groundY - 380 + rise, Color.FromHex(titleHex) with { A = (byte)(255 * k) });
            _small.DrawCentered(_batch, sc.Fx.Text, cx, groundY - 330 + rise, Color.FromHex("#E8E4DA") with { A = (byte)(255 * k) });
        }
        if (t > 0.8) _small.DrawCentered(_batch, "{#6A6050}click to continue{/}", cx, VH - 60, Color.White with { A = (byte)(180 * fade) });
    }

    // ================================================================== the Almoner

    private void DrawDaily()
    {
        if (_daily is not { } d) return;
        const float x = 280, w = 900, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, "BROTHER ALMONER");
        _small.Draw(_batch, $"{{#9A9080}}Today, {d.Date}. Pledges are the same for every brother; endeavours are yours. New ones at midnight (UTC).{{/}}", x + 24, y0 + 58, Color.White);
        float y = y0 + 96;
        foreach (bool pledge in new[] { true, false })
        {
            _ui.Draw(_batch, pledge ? "Daily pledges" : "Daily endeavours", x + 24, y, Color.FromHex("#C9A227"));
            y += 38;
            foreach (var t in d.Tasks.Where(t => t.Pledge == pledge))
            {
                _batch.FillRect(x + 24, y, w - 48, 58, new Color(30, 25, 18, 255));
                _small.Draw(_batch, t.Claimed ? $"{{#6A6050}}{t.Text}{{/}}" : t.Text, x + 36, y + 6, Color.FromHex("#E8E4DA"));
                _small.Draw(_batch, $"{{#7A7060}}{t.Reward}{{/}}", x + 36, y + 30, Color.White);
                float frac = Math.Clamp(t.Progress / (float)Math.Max(1, t.Target), 0, 1);
                float bx = x + w - 380;
                _batch.FillRect(bx, y + 20, 200, 14, new Color(10, 8, 6, 255));
                _batch.FillRect(bx, y + 20, 200 * frac, 14, Color.FromHex(frac >= 1 ? "#6BD66B" : "#C9A227"));
                _small.Draw(_batch, $"{t.Progress}/{t.Target}", bx + 208, y + 16, Color.FromHex("#9A9080"));
                string id = t.Id;
                if (t.Claimed) _small.Draw(_batch, "{#6BD66B}claimed{/}", x + w - 130, y + 16, Color.White);
                else Button(x + w - 130, y + 12, 96, 32, "Claim", () => _conn.Command("daily.claim", id), enabled: frac >= 1);
                y += 64;
            }
            y += 12;
        }
        _ui.Draw(_batch, "This week's instabilities", x + 24, y, Color.FromHex("#E0A060"));
        _small.Draw(_batch, $"{{#7A7060}}in the Grey Mist and the Proving Pit · change in {Remaining(d.WeekEndsUnix)}{{/}}", x + 330, y + 6, Color.White);
        y += 36;
        foreach (var i in d.Instabilities) { _small.Draw(_batch, $"{{#E8E4DA}}{i.Name}{{/}} {{#9A9080}}{i.Text}{{/}}", x + 36, y, Color.White); y += 26; }
        if (d.MistZone.Length > 0) _small.Draw(_batch, $"{{#B8C4D8}}The Grey Mist holds {d.MistZone} for another {Remaining(d.MistEndsUnix)}.{{/}}", x + 24, y0 + h - 44, Color.White);
    }

    // ================================================================== the Herald

    private void DrawHerald()
    {
        if (_herald is not { } hs) return;
        const float x = 280, w = 720, y0 = 160, h = 560;
        WinFrame(x, y0, w, h, "THE HUNT FOR LA TARASQUE");
        float y = y0 + 64;
        foreach (var line in new[]
        {
            "The Tarasque comes up the Rhône. When the horn sounds, every knight at the muster in Tarascon",
            $"(up to {hs.Limit}) is carried across to the shore to face her together.",
            "Her blows are shared between everyone on the shore, so stand together: no one is singled out.",
            "A fallen brother can be raised by any of you who stands beside him: press Revive. Only there.",
            "She pays in Trophy Relics (relic arts such as Cleave and Riposte), Menagerie Tokens, high runes,",
            "and now and then an Exotic or Mythic piece.",
        })
        { _small.Draw(_batch, $"{{#9A9080}}{line}{{/}}", x + 24, y, Color.White); y += 26; }
        y += 18;
        _ui.Draw(_batch, hs.FightOn ? "{#E0463C}The fight is on the shore now.{/}" : $"The horn sounds in {{#F2D25A}}{Remaining(hs.NextUnix)}{{/}}", x + 24, y, Color.FromHex("#E8E4DA"));
        y += 40;
        _small.Draw(_batch, $"{{#9A9080}}At the muster:{{/}} {hs.Mustered}/{hs.Limit} knights", x + 24, y, Color.White);
        if (hs.InMuster) Button(x + w - 284, y0 + h - 70, 260, 44, "Back to the Sanctuary", () => { _conn.Command("muster.leave"); CloseWindow(); }, tone: "plain");
        else Button(x + w - 284, y0 + h - 70, 260, 44, "Go to the muster", () => { _conn.Command("muster.go"); CloseWindow(); }, enabled: hs.Mustered < hs.Limit);
    }

    // ================================================================== the armoury rack

    private void DrawArmoury(PlayerStats? st)
    {
        if (_armoury is not { } ar) return;
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, "THE ARMOURY RACK");
        DropZone(x, y0, w, h, (from, _) => from == DragFrom.Bag, (_, it) => _conn.Command("armoury.put", it.Id.ToString()));
        _small.Draw(_batch, "{#9A9080}Save what you wear as a loadout; put one on to change your whole harness at once. Everything{/}", x + 24, y0 + 58, Color.White);
        _small.Draw(_batch, "{#9A9080}in it must be here (worn, in your bag or on the rack). What you take off goes on the rack.{/}", x + 24, y0 + 82, Color.White);
        float y = y0 + 122;
        _small.Draw(_batch, "{#C9A227}Name for a new loadout{/}", x + 24, y + 4, Color.White);
        TextField(_loadoutName, x + 240, y, 220, "Loadout");
        y += 44;
        var all = (st?.Equipped ?? []).Concat(st?.Inventory ?? []).Concat(ar.Rack).ToList();
        for (int i = 0; i < ar.Loadouts.Count; i++)
        {
            var l = ar.Loadouts[i];
            _batch.FillRect(x + 24, y, w - 48, 92, new Color(30, 25, 18, 255));
            _ui.Draw(_batch, l.Name.Length > 0 ? l.Name : $"{{#6A6050}}Empty loadout {i + 1}{{/}}", x + 36, y + 6, Color.FromHex("#E8E4DA"));
            float ix = x + 36;
            foreach (var (slot, id) in l.Items)
            {
                var it = all.FirstOrDefault(a => a.Id == id);
                if (it is null) { _batch.FillRect(ix, y + 44, 38, 38, new Color(60, 20, 18, 255)); ix += 42; continue; }
                ItemTile(it, ix, y + 44, 38, false);
                if (Over(ix, y + 44, 38, 38)) Tip(it);
                ix += 42;
            }
            string si = i.ToString();
            Button(x + w - 312, y + 8, 92, 30, "Save", () => _conn.Command("loadout.save", si, _loadoutName.Text), tone: "plain");
            Button(x + w - 212, y + 8, 92, 30, "Put on", () => _conn.Command("loadout.equip", si), enabled: l.Ready && !SceneBusy);
            Button(x + w - 112, y + 8, 80, 30, "Clear", () => _conn.Command("loadout.clear", si), enabled: l.Items.Count > 0, tone: "red");
            if (l.Missing.Count > 0) _small.Draw(_batch, $"{{#E0A060}}missing: {string.Join(", ", l.Missing)}{{/}}", x + 300, y + 52, Color.White);
            y += 100;
        }
        y += 6;
        _ui.Draw(_batch, $"On the rack  {{#7A7060}}{ar.Rack.Count}/{ar.Limit}{{/}}", x + 24, y, Color.FromHex("#C9A227"));
        y += 36;
        for (int i = 0; i < ar.Rack.Count && i < 26; i++)
        {
            var it = ar.Rack[i];
            float gx = x + 30 + i % 13 * 51, gy = y + i / 13 * 51;
            ItemTile(it, gx, gy, 46, false);
            string sid = it.Id.ToString();
            _buttons.Add((gx, gy, 46, 46, () => _conn.Command("armoury.take", sid)));
            if (Over(gx, gy, 46, 46)) Tip(it);
        }
        _small.Draw(_batch, "{#7A7060}Click a piece on the rack to take it into your bag; right-click a piece in your bag to hang it here.{/}", x + 24, y0 + h - 40, Color.White);
    }

    // ================================================================== the menagerie

    private void DrawPets(PlayerStats? st)
    {
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, _petsNpc.Length > 0 ? _petsNpc.ToUpperInvariant() : "THE MENAGERIE");
        _small.Draw(_batch, "{#9A9080}Companions follow you everywhere. They do not fight; they are not cheap. Menagerie Tokens come{/}", x + 24, y0 + 58, Color.White);
        _small.Draw(_batch, "{#9A9080}from the great hunts: La Tarasque drops them.{/}", x + 24, y0 + 82, Color.White);
        Wallet(x + 24, y0 + h - 42, Materials.PetToken);
        _small.Draw(_batch, $"{{#E6C14A}}{st?.Gold ?? 0:N0} gold{{/}}", x + 140, y0 + h - 42, Color.White);
        float y = y0 + 120;
        var owned = _extras?.Pets.ToHashSet() ?? [];
        foreach (var pet in Endgame.Pets)
        {
            _batch.FillRect(x + 24, y, w - 48, 100, new Color(30, 25, 18, 255));
            PetPortrait(pet, x + 34, y + 4, 92);
            _ui.Draw(_batch, pet.Name, x + 140, y + 8, Color.FromHex("#E8E4DA"));
            _small.Draw(_batch, $"{{#9A9080}}{pet.Text}{{/}}", x + 140, y + 42, Color.White);
            _small.Draw(_batch, $"{{#E6C14A}}{pet.Gold:N0} gold{{/}}  ·  {pet.Tokens} tokens", x + 140, y + 68, Color.White);
            string id = pet.Id;
            if (owned.Contains(pet.Id))
            {
                bool active = _extras?.ActivePet == pet.Id;
                Button(x + w - 170, y + 30, 140, 36, active ? "Dismiss" : "Summon", () => _conn.Command(active ? "pet.dismiss" : "pet.summon", id), tone: active ? "plain" : "blue");
            }
            else Button(x + w - 170, y + 30, 140, 36, "Buy", () => _conn.Command("pet.buy", id), enabled: (st?.Gold ?? 0) >= pet.Gold && Mat(Materials.PetToken) >= pet.Tokens);
            y += 108;
        }
    }

    // ================================================================== the Book of Deeds

    private void DrawBook(PlayerStats? st)
    {
        const float x = 160, w = 1600, y0 = 90, h = 840;
        WinFrame(x, y0, w, h, "THE BOOK OF DEEDS");
        float tx = x + 360;
        foreach (var (id, label) in new[] { ("sets", "Set collection"), ("relics", "Relic arts"), ("pets", "Companions") })
        {
            float bw = _small.Measure(label) + 30;
            string tid = id;
            Button(tx, y0 + 16, bw, 32, label, () => _bookTab = tid, tone: _bookTab == tid ? "gold" : "plain");
            tx += bw + 8;
        }
        switch (_bookTab)
        {
            case "relics": BookRelics(x, w, y0); break;
            case "pets": BookPets(x, w, y0); break;
            default: BookSets(x, w, y0, st); break;
        }
    }

    private void BookSets(float x, float w, float y0, PlayerStats? st)
    {
        var have = _extras?.Collection.ToHashSet() ?? [];
        var claimed = _extras?.SetsClaimed.ToHashSet() ?? [];
        _small.Draw(_batch, "{#9A9080}Every set piece you have ever held is written here. Complete a set for a reward from the Order. Exalted pieces (Proving Pit, floor 6 and deeper) are marked in gold.{/}", x + 24, y0 + 60, Color.White);
        const float cw = 508, ch = 360;
        for (int i = 0; i < ItemSets.All.Length; i++)
        {
            var set = ItemSets.All[i];
            float cx = x + 24 + i % 3 * (cw + 14), cy = y0 + 96 + i / 3 * (ch + 12);
            _batch.FillRect(cx, cy, cw, ch, new Color(30, 25, 18, 255));
            int got = set.Pieces.Count(p => have.Contains($"{set.Id}:{p.Slot}"));
            bool done = got == set.Pieces.Count;
            _ui.Draw(_batch, $"{(done ? "{#6BD66B}" : "{#E8E4DA}")}{set.Name}{{/}}", cx + 14, cy + 10, Color.White);
            _small.Draw(_batch, $"{{#7A7060}}{got}/{set.Pieces.Count} · from level {set.MinLevel}{(set.BossOnly ? " · bosses only" : "")}{{/}}", cx + 14, cy + 44, Color.White);
            _small.Draw(_batch, $"{{#9A9080}}{set.Lore}{{/}}", cx + 14, cy + 68, Color.White);
            float px = cx + 14;
            foreach (var piece in set.Pieces)
            {
                bool own = have.Contains($"{set.Id}:{piece.Slot}"), ex = have.Contains($"{set.Id}:{piece.Slot}:exalted");
                string key = $"ui/icons/items/{Loot.Slug(piece.Base)}.png";
                _batch.FillRect(px, cy + 102, 70, 70, new Color(16, 13, 10, 255));
                if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), px + 3, cy + 105, 64, 64, own ? Color.White : new Color(255, 255, 255, 40));
                if (ex) { var g = Color.FromHex("#FFC93C"); _batch.FillRect(px, cy + 102, 70, 3, g); _batch.FillRect(px, cy + 169, 70, 3, g); }
                _small.DrawCentered(_batch, $"{(own ? "{#9A9080}" : "{#4A4438}")}{Loot.SlotName(piece.Slot)}{{/}}", px + 35, cy + 176, Color.White, shadow: false);
                px += 80;
            }
            float by = cy + 214;
            int worn = st?.Equipped.Count(e => e.Set == set.Id) ?? 0;
            foreach (var b in set.Bonuses)
            {
                _small.Draw(_batch, $"{(worn >= b.Pieces ? "{#6BD66B}" : "{#7A8A7A}")}({b.Pieces}) {ItemSets.Describe(b.Props)}{{/}}", cx + 14, by, Color.White);
                by += 24;
            }
            _small.Draw(_batch, $"{{#B8963A}}(Exalted) {ItemSets.Describe(set.Exalted)}{{/}}", cx + 14, by, Color.White);
            string sid = set.Id;
            if (claimed.Contains(set.Id)) _small.Draw(_batch, "{#6BD66B}Reward claimed{/}", cx + cw - 150, cy + 14, Color.White);
            else if (done) Button(cx + cw - 170, cy + 10, 156, 32, "Claim reward", () => _conn.Command("collection.claim", sid));
        }
    }

    private void BookRelics(float x, float w, float y0)
    {
        _small.Draw(_batch, $"{{#9A9080}}Trophy Relics from the world bosses teach relic arts, outside the skill tree. They take the first places on your bar.  You hold {{/}}{{#F2D25A}}{Mat(Materials.TrophyRelic)} Trophy Relic{(Mat(Materials.TrophyRelic) == 1 ? "" : "s")}{{/}}.", x + 24, y0 + 60, Color.White);
        float y = y0 + 110;
        var known = _extras?.Relics.ToHashSet() ?? [];
        foreach (var sp in _data.Spells.Values.Where(s => s.Field == GameData.RelicField).OrderBy(s => s.Name))
        {
            _batch.FillRect(x + 24, y, w - 48, 110, new Color(30, 25, 18, 255));
            _ui.Draw(_batch, sp.Name, x + 40, y + 10, Color.FromHex(known.Contains(sp.Id) ? "#F2D25A" : "#E8E4DA"));
            _small.Draw(_batch, $"{{#9A9080}}{sp.Description}{{/}}", x + 40, y + 46, Color.White);
            _small.Draw(_batch, $"{{#7A7060}}{sp.ManaCost} mana · {sp.Cooldown:0.#} s cooldown{{/}}", x + 40, y + 74, Color.White);
            string id = sp.Id;
            if (known.Contains(sp.Id)) _small.Draw(_batch, "{#6BD66B}Known{/}", x + w - 160, y + 40, Color.White);
            else Button(x + w - 260, y + 34, 220, 40, "Learn (1 Trophy Relic)", () => _conn.Command("relic.learn", id), enabled: Mat(Materials.TrophyRelic) >= 1);
            y += 122;
        }
    }

    private void BookPets(float x, float w, float y0)
    {
        var owned = _extras?.Pets ?? [];
        _small.Draw(_batch, "{#9A9080}Your companions. Buy more from Mathilde of the Menagerie, in the Sanctuary.{/}", x + 24, y0 + 60, Color.White);
        if (owned.Count == 0) { _small.Draw(_batch, "{#6A6050}None yet.{/}", x + 24, y0 + 110, Color.White); return; }
        float px = x + 24;
        foreach (var id in owned)
        {
            if (Endgame.Pet(id) is not { } pet) continue;
            _batch.FillRect(px, y0 + 110, 240, 300, new Color(30, 25, 18, 255));
            PetPortrait(pet, px + 40, y0 + 120, 160);
            _ui.Draw(_batch, pet.Name, px + 16, y0 + 290, Color.FromHex("#E8E4DA"));
            bool active = _extras?.ActivePet == id;
            string pid = id;
            Button(px + 16, y0 + 340, 208, 36, active ? "Dismiss" : "Summon", () => _conn.Command(active ? "pet.dismiss" : "pet.summon", pid), tone: active ? "plain" : "blue");
            px += 256;
        }
    }

    /// <summary>A companion's portrait: the still is a whole figure frame, so crop to the animal.</summary>
    private void PetPortrait(PetDef pet, float x, float y, float size)
    {
        string key = $"sprites/pet_{pet.Id}.png";
        _batch.FillRect(x, y, size, size, new Color(16, 13, 10, 255));
        if (!_assets.Exists(key)) return;
        float cv = pet.Flies ? 0.46f : 0.7f, half = pet.Id == "white_hart" ? 0.22f : 0.17f;
        _batch.Draw(_assets.Texture(key), x, y, size, size, 0.5f - half, cv - half, 0.5f + half, cv + half, Color.White);
    }

    // ================================================================== HUD

    private void DrawEndgameHud(WelcomeInfo w, RemoteEntity me)
    {
        var e = _extras;
        if (e is null || e.Zone != w.ZoneId) return;
        float y = 112;
        bool boss = _conn.Entities.Values.Any(x => (x.Current.Flags & EntityFlags.Boss) != 0 && !x.Current.Dead);
        if (boss) y = 120;
        if (e.MistHere)
        {
            float cx = VW / 2;
            _batch.FillRect(cx - 230, y, 460, 56, new Color(14, 16, 20, 200));
            _small.Draw(_batch, $"{{#B8C4D8}}THE GREY MIST{{/}}  {{#7A8494}}{Remaining(e.MistEndsUnix)}{{/}}", cx - 216, y + 4, Color.White);
            MaterialIcon(Materials.MistEmber, cx + 120, y + 4, 20);
            _small.Draw(_batch, $"{Mat(Materials.MistEmber)}", cx + 146, y + 4, Color.FromHex("#E8E4DA"));
            float frac = Math.Clamp(e.Threat / 100f, 0, 1);
            _batch.FillRect(cx - 216, y + 34, 432, 12, new Color(0, 0, 0, 220));
            var tc = e.Hunted ? Color.FromHex((int)(Now * 4) % 2 == 0 ? "#E0463C" : "#801A14") : ParticleSystem.Lerp(Color.FromHex("#6A7A94"), Color.FromHex("#E0463C"), frac);
            _batch.FillRect(cx - 216, y + 34, 432 * frac, 12, tc);
            _small.DrawCentered(_batch, e.Hunted ? "{#FFD0C8}HUNTED{/}" : "{#9AA6B8}threat{/}", cx, y + 30, Color.White, shadow: false);
            y += 64;
        }
        if (w.ZoneId == "proving_pit" && e.PitFloor > 0)
        {
            _title.DrawCentered(_batch, $"FLOOR {e.PitFloor} / {World.PitFloors}", VW / 2, y, Color.FromHex("#F2D25A"));
            y += 46;
        }
        if ((e.MistHere || w.ZoneId == "proving_pit") && e.Instabilities.Count > 0)
            _small.DrawCentered(_batch, "{#E0A060}" + string.Join(" · ", e.Instabilities.Select(i => Endgame.Instabilities.FirstOrDefault(d => d.Id == i)?.Name ?? i)) + "{/}", VW / 2, y, Color.White);
        if (e.SyncLevel > 0) _small.Draw(_batch, $"{{#9FC6FF}}synced to level {e.SyncLevel}{{/}}", 30, 88, Color.White);
        if (e.WorldBossOpen && !e.InWorldBoss && _openWindow == Win.None)
            _small.Draw(_batch, $"{{#F2D25A}}La Tarasque rises in {Remaining(e.WorldBossUnix)}{{/}} {{#7A7060}}— the Herald musters in the Sanctuary{{/}}", 1580, 272, Color.White);

        // At a world boss: raise the fallen beside you.
        if (e.InWorldBoss && !me.Current.Dead)
        {
            var fallen = _conn.Entities.Values.Where(x => x.Current.Kind == EntityKind.Player && x.Current.Dead && x.Current.Id != _conn.YourId)
                .OrderBy(x => Vec2.Distance(x.Current.Pos, me.Current.Pos)).FirstOrDefault();
            if (fallen is not null && Vec2.Distance(fallen.Current.Pos, me.Current.Pos) <= World.ReviveRange)
            {
                string name = _conn.Roster.TryGetValue(fallen.Current.Id, out var r) ? r.Name : "your brother";
                string fid = fallen.Current.Id.ToString();
                Button(VW / 2 - 140, 878, 280, 40, $"Revive {name}  (F)", () => _conn.Command("revive", fid), tone: "blue");
                _blockRects.Add((VW / 2 - 140, 878, 280, 40));
                _reviveTarget = fallen.Current.Id;
            }
            else _reviveTarget = 0;
        }
        if (e.InWorldBoss && me.Current.Dead)
            _small.DrawCentered(_batch, "{#E8E4DA}Fallen. A brother beside you can raise you, or you return to the reeds shortly.{/}", VW / 2, 470, Color.White);
    }

    private int _reviveTarget;

    // ================================================================== pets, coffers and the Mist in the world

    private readonly Dictionary<int, (Vec2 Pos, double Seen)> _petPos = new();

    /// <summary>A companion trotting (or flying) behind its knight. Followed on the client: the server only says which.</summary>
    private void DrawPet(EntityState s, Vec2 worldPos)
    {
        int idx = s.PlayerPet;
        if (idx <= 0 || idx > Endgame.Pets.Length || s.Dead) { _petPos.Remove(s.Id); return; }
        var def = Endgame.Pets[idx - 1];
        if (!_petPos.TryGetValue(s.Id, out var st) || Vec2.Distance(st.Pos, worldPos) > 500) st = (worldPos + new Vec2(-40, 20), Now);
        var want = worldPos + new Vec2(-46, def.Flies ? -10 : 18);
        float dt = (float)Math.Min(0.1, Now - st.Seen);
        var to = want - st.Pos;
        float d = to.Length;
        if (d > 28) st.Pos += to / d * Math.Min(d - 26, (d > 120 ? 320 : 170) * dt);
        st.Seen = Now;
        _petPos[s.Id] = st;
        var p = ToScreen(st.Pos);
        string key = $"sprites/pet_{def.Id}.png";
        if (!_assets.Exists(key)) { _batch.Draw(_disc, p.X - 14, p.Y - 20, 28, 20, Color.FromHex("#8A7A64")); return; }
        float lift = def.Flies ? 70 + 8 * MathF.Sin((float)Now * 3 + s.Id) : 0;
        _batch.Draw(_disc, p.X - 22 * Zoom * 0.5f, p.Y - 4, 44 * Zoom * 0.5f, 10, new Color(0, 0, 0, 90));
        var fake = new EntityState(-1000 - s.Id, EntityKind.Monster, 0, st.Pos, 1, 1, 1, EntityFlags.None);
        DrawFigure(key, fake, new Vec2(p.X, p.Y - lift), 60, Color.White, Zoom);
    }

    private void DrawCoffer(EntityState s, Vec2 p)
    {
        bool hovered = Hovered(EntityKind.Chest)?.Current.Id == s.Id;
        _batch.Draw(_disc, p.X - 40 * Zoom * 0.6f, p.Y - 10, 80 * Zoom * 0.6f, 20, new Color(0, 0, 0, 110));
        const string key = "sprites/prop_mist_coffer_1.png";
        float sz = 110 * Zoom * 0.6f;
        if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), p.X - sz / 2, p.Y - sz + 14, sz, sz, hovered ? new Color(220, 235, 255) : Color.White);
        else _batch.FillRect(p.X - 30, p.Y - 40, 60, 40, Color.FromHex("#5A6A80"));
        float pulse = 0.6f + 0.4f * MathF.Sin((float)Now * 2.4f + s.Id);
        _emissive.Add(() => _batch.Draw(_lightTex, p.X - 120, p.Y - 150, 240, 180, Color.FromHex("#6A8ABE") with { A = (byte)(120 * pulse) }));
        AddLight(s.Pos, 260, Color.FromHex("#8AA8D8"), 0.6f * pulse);
        if (hovered) _overlay.Add(() => _small.DrawCentered(_batch, $"{{#B8C4D8}}Mist Coffer{{/}} {{#9A9080}}({World.CofferCost} embers){{/}}", p.X, p.Y - sz - 6, Color.White));
    }

    /// <summary>The Grey Mist: low banks of fog drifting across the ground.</summary>
    private void DrawFog()
    {
        const float cell = 520;
        var drift = new Vec2(14, 4) * (float)Now;
        var tl = _camera - HalfView - drift;
        int cx0 = (int)MathF.Floor(tl.X / cell) - 1, cy0 = (int)MathF.Floor(tl.Y / cell) - 1;
        for (int cy = cy0; cy < cy0 + 5; cy++)
        for (int cx = cx0; cx < cx0 + 7; cx++)
        {
            uint hsh = Hash(cx + 911, cy - 37);
            float ox = (hsh & 1023) / 1023f, oy = ((hsh >> 10) & 1023) / 1023f, sz = 0.8f + ((hsh >> 20) & 255) / 255f * 0.8f;
            var p = ToScreen(new Vec2((cx + ox) * cell, (cy + oy) * cell) + drift);
            float r = cell * 0.6f * sz * Zoom;
            float breathe = 0.75f + 0.25f * MathF.Sin((float)Now * 0.4f + hsh % 7);
            _batch.Draw(_softDot, p.X - r, p.Y - r * 0.55f, r * 2, r * 1.1f, new Color(176, 188, 206, (byte)(46 * breathe)));
        }
    }

    private static readonly Dictionary<string, AmbienceDef> MistLooks = new();

    private static AmbienceDef MistLook(string zoneId, AmbienceDef a)
    {
        if (MistLooks.TryGetValue(zoneId, out var m)) return m;
        m = new AmbienceDef
        {
            Ambient = "#A4B0C4", Level = Math.Min(a.Level, 0.92f) * 0.82f, Tint = "#DCE4F2", Saturation = a.Saturation * 0.62f, Contrast = a.Contrast + 0.06f,
            Vignette = Math.Max(a.Vignette, 0.55f), Bloom = a.Bloom, Particles = "ash", ParticleDensity = 1.1f, HeroLight = Math.Max(a.HeroLight, 220),
            Clouds = Math.Max(a.Clouds, 0.45f), Dust = a.Dust, Sound = "wind_cold",
        };
        MistLooks[zoneId] = m;
        return m;
    }
}
