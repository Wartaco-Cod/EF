using TacoFactory.Client.Rendering;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// The bag as a paper doll and an icon grid: fourteen gear slots around the knight, the bag below, rich tooltips
/// (properties with runes and traits, sockets, set bonuses), the rune pouch and the wallet.
/// </summary>
public sealed partial class GameClient
{
    private ExtrasState? _extras;
    private string _invTab = "gear";
    private long? _selectedRune;
    /// <summary>A socket waiting for a rune: chosen on an item, then filled from the pouch.</summary>
    private (long Item, int Index)? _socketTarget;
    private ItemInfo? _tipItem;
    private RuneInfo? _tipRune;
    private (float X, float Y) _tipAt;
    private readonly List<(float X, float Y, float W, float H, Action OnClick)> _rightButtons = [];

    private static readonly ItemSlot[] DollLeft = [ItemSlot.Helm, ItemSlot.Shoulders, ItemSlot.Armor, ItemSlot.Mantle, ItemSlot.Gloves, ItemSlot.Belt];
    private static readonly ItemSlot[] DollRight = [ItemSlot.Amulet, ItemSlot.Ring, ItemSlot.Ring2, ItemSlot.Pack, ItemSlot.Legs, ItemSlot.Boots];

    private bool Over(float x, float y, float w, float h)
    {
        var m = MouseVirtual();
        return m.X >= x && m.X <= x + w && m.Y >= y && m.Y <= y + h;
    }

    private void RightClick(System.Numerics.Vector2 mouse)
    {
        var (scale, ox, oy) = Letterbox(_window.Size);
        float x = (mouse.X - ox) / scale, y = (mouse.Y - oy) / scale;
        foreach (var b in Enumerable.Reverse(_rightButtons))
            if (x >= b.X && x <= b.X + b.W && y >= b.Y && y <= b.Y + b.H) { Sfx("ui_click", 0.6f); b.OnClick(); return; }
    }

    private void Tip(ItemInfo i) { _tipItem = i; _tipRune = null; var m = MouseVirtual(); _tipAt = (m.X, m.Y); }
    private void Tip(RuneInfo r) { _tipRune = r; _tipItem = null; var m = MouseVirtual(); _tipAt = (m.X, m.Y); }

    // ------------------------------------------------------------------ icons

    private static string NameHex(ItemInfo i) => i.Exalted ? "#FFC93C" : RarityHex(i.Rarity);

    private static string TierHex(Rarity r) => RarityHex(r);

    /// <summary>A rune's icon: its kind's glyph, tinted by tier, with its level in the corner.</summary>
    private void RuneIcon(RuneInfo r, float x, float y, float size, bool level = true)
    {
        var tint = Color.FromHex(TierHex(r.Tier));
        _batch.Draw(_disc, x, y, size, size, new Color(16, 13, 10, 240));
        _batch.Draw(_disc, x + 2, y + 2, size - 4, size - 4, new Color(tint.R, tint.G, tint.B, 70));
        string key = $"ui/icons/runes/{r.Kind}.png";
        if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), x + size * 0.12f, y + size * 0.12f, size * 0.76f, size * 0.76f, Color.White);
        else _small.DrawCentered(_batch, (Runes.Kind(r.Kind)?.Name.Split(' ').Last() ?? "?")[..1], x + size / 2, y + size / 2 - 12, tint, shadow: false);
        if (level && size >= 30) _small.Draw(_batch, $"{r.Level}", x + size - 16, y + size - 22, r.Level >= Runes.MaxLevel ? Color.FromHex("#F2D25A") : Color.FromHex("#E8E4DA"));
    }

    private void MaterialIcon(string id, float x, float y, float size)
    {
        string key = $"ui/icons/materials/{id}.png";
        if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), x, y, size, size, Color.White);
        else
        {
            _batch.Draw(_disc, x, y, size, size, Color.FromHex(id switch
            {
                Materials.SoulCore => "#7A5AE0", Materials.MistEmber => "#9AB0C8", Materials.HolyOil => "#E8C25A",
                Materials.TrophyRelic => "#E8892B", Materials.PetToken => "#4CC24C", _ => "#8A8070",
            }));
        }
    }

    /// <summary>The empty-slot silhouette: the slot's own icon if one was rendered, else its name.</summary>
    private void EmptySlot(ItemSlot slot, float x, float y, float size)
    {
        _batch.FillRect(x, y, size, size, new Color(26, 22, 17, 240));
        _batch.FillRect(x, y + size - 2, size, 2, new Color(70, 60, 44, 255));
        string key = slot == ItemSlot.Weapon ? "ui/icons/items/arming_sword.png" : $"ui/icons/items/{Loot.Slug(Loot.NamesFor(slot)[0])}.png";
        if (_assets.Exists(key)) _batch.Draw(_assets.Texture(key), x + 6, y + 6, size - 12, size - 12, new Color(0, 0, 0, 150));
        else _small.DrawCentered(_batch, $"{{#5A5448}}{Loot.SlotName(slot)}{{/}}", x + size / 2, y + size / 2 - 12, Color.White, shadow: false);
    }

    /// <summary>A full item tile: icon, rarity edge, temper level, and the sockets as small dots.</summary>
    private void ItemTile(ItemInfo it, float x, float y, float size, bool selected)
    {
        ItemIcon(it, x, y, size);
        if (it.Exalted) { var g = Color.FromHex("#FFC93C"); _batch.FillRect(x, y, size, 2, g); _batch.FillRect(x, y, 2, size, g); _batch.FillRect(x + size - 2, y, 2, size, g); }
        if (it.Upgrade > 0) _small.Draw(_batch, $"{{#F2D25A}}+{it.Upgrade}{{/}}", x + 3, y + 1, Color.White);
        var socks = it.SocketList;
        for (int i = 0; i < socks.Count; i++)
        {
            float sx = x + size - 10 - i * 9, sy = y + size - 10;
            _batch.Draw(_disc, sx, sy, 8, 8, socks[i] is { } r ? Color.FromHex(TierHex(r.Tier)) : new Color(40, 34, 28, 255));
        }
        if (selected) { var c = Color.FromHex("#F2D25A"); _batch.FillRect(x - 2, y - 2, size + 4, 2, c); _batch.FillRect(x - 2, y + size, size + 4, 2, c); _batch.FillRect(x - 2, y, 2, size, c); _batch.FillRect(x + size, y, 2, size, c); }
    }

    // ------------------------------------------------------------------ the panel

    /// <summary>The bag: gear (paper doll and grid), runes, wallet.</summary>
    private void DrawInventoryPanel(PlayerStats? st, RemoteEntity me)
    {
        const float x = 1040, w = 520;
        Panel(x, w, "INVENTORY", () => _invOpen = false);
        foreach (var (tab, label, bx) in new[] { ("gear", "Gear", 252f), ("runes", "Runes", 330f), ("wallet", "Wallet", 408f) })
            Button(x + bx - 64, 114, 74, 32, label, () => _invTab = tab, tone: _invTab == tab ? "gold" : "plain");
        switch (_invTab)
        {
            case "runes": DrawRunesTab(st, x, w); break;
            case "wallet": DrawWalletTab(st, x, w); break;
            default: DrawGearTab(st, me, x, w); break;
        }
    }

    private void DrawGearTab(PlayerStats? st, RemoteEntity me, float x, float w)
    {
        const float T = 52, step = 58, top = 158;
        for (int i = 0; i < 6; i++)
        {
            DollTile(st, DollLeft[i], x + 22, top + i * step, T);
            DollTile(st, DollRight[i], x + w - 22 - T, top + i * step, T);
        }
        float cx = x + w / 2;
        DrawDollFigure(me, cx, top + 4 * step + 30);
        DollTile(st, ItemSlot.Weapon, cx - T - 8, top + 5 * step, T);
        DollTile(st, ItemSlot.Shield, cx + 8, top + 5 * step, T);

        var cls = Classes.Get(_conn.Class);
        int level = _extras?.SyncLevel is > 0 and var sl ? sl : me.Current.Level;
        string sync = _extras?.SyncLevel > 0 ? $"  {{#9FC6FF}}synced to {_extras.SyncLevel}{{/}}" : "";
        _small.DrawCentered(_batch, $"{{#9A9080}}Damage{{/}} {Progression.Damage(cls, level) + (st?.GearDamage ?? 0):F0}  {{#9A9080}}Defense{{/}} {st?.GearDefense ?? 0}  {{#9A9080}}Block{{/}} {(st?.BlockChance ?? 0) * 100:F0}%{sync}",
            cx, 508, Color.FromHex("#E8E4DA"));

        int count = st?.Inventory.Count ?? 0, limit = _extras?.BagLimit ?? Loot.InventoryLimit;
        _small.Draw(_batch, $"Bag  {{#7A7060}}{count}/{limit}{{/}}   {{#E6C14A}}{st?.Gold ?? 0:N0} gold{{/}}", x + 28, 534, Color.FromHex("#9A9080"));
        _small.Draw(_batch, "{#5A5448}right-click: quick action{/}", x + w - 196, 534, Color.White);

        const int cols = 9;
        const float G = 46, gs = 51, gy0 = 562;
        float gx0 = x + 30;
        int rows = Math.Min(6, (limit + cols - 1) / cols);
        DropZone(gx0 - 4, gy0 - 4, cols * gs + 4, rows * gs + 4, (from, _) => from is DragFrom.Equipped or DragFrom.Stash, (from, it) =>
        {
            if (from == DragFrom.Equipped) _conn.Command("unequip", it.Slot.ToString());
            else _conn.Command(_stash?.Guild == true ? "gstash.take" : "stash.take", it.Id.ToString());
        });
        var bag = st?.Inventory ?? [];
        for (int i = 0; i < rows * cols; i++)
        {
            float gx = gx0 + i % cols * gs, gy = gy0 + i / cols * gs;
            if (i >= limit) continue;
            if (i >= bag.Count) { _batch.FillRect(gx, gy, G, G, new Color(26, 22, 17, 230)); continue; }
            var item = bag[i];
            bool sel = _selected == item.Id || _forgeA == item.Id || _forgeB == item.Id || _athanor.Contains(item.Id);
            ItemTile(item, gx, gy, G, sel);
            var id = item.Id;
            _buttons.Add((gx, gy, G, G, () => { _selected = id; _socketTarget = null; }));
            _rightButtons.Add((gx, gy, G, G, () => QuickAction(item)));
            Draggable(gx, gy, G, G, item, DragFrom.Bag);
            if (Over(gx, gy, G, G)) Tip(item);
        }
        if (count > rows * cols) _small.Draw(_batch, $"{{#7A7060}}+{count - rows * cols} more{{/}}", x + 28, gy0 + rows * gs, Color.White);

        // The selected item: name, sockets (click a rune to take it out) and what can be done with it here.
        var selected = st?.Inventory.Concat(st.Equipped).FirstOrDefault(i => i.Id == _selected);
        float ay = 876;
        _batch.FillRect(x + 16, ay - 4, w - 32, 40, new Color(30, 25, 18, 255));
        if (selected is null) { _small.Draw(_batch, "{#7A7060}Click an item to select it. Z / X drink potions.{/}", x + 28, ay + 4, Color.White); return; }
        bool worn = st!.Equipped.Any(e => e.Id == selected.Id);
        float nx = x + 28;
        string label = selected.Name.Length > 22 ? selected.Name[..21] + "." : selected.Name;
        _small.Draw(_batch, $"{{{NameHex(selected)}}}{label}{{/}}", nx, ay + 4, Color.White);
        float sx = x + 250;
        DrawSockets(selected, sx, ay + 2, 26);
        SelectedActions(selected, worn, x + w - 24, ay);
    }

    private void DrawSockets(ItemInfo item, float sx, float sy, float size)
    {
        var socks = item.SocketList;
        for (int i = 0; i < socks.Count; i++)
        {
            float px = sx + i * (size + 6);
            int idx = i;
            long iid = item.Id;
            if (socks[i] is { } r)
            {
                RuneIcon(r, px, sy, size, level: false);
                _buttons.Add((px, sy, size, size, () => _conn.Command("rune.unsocket", iid.ToString(), idx.ToString())));
                if (Over(px, sy, size, size)) Tip(r);
            }
            else
            {
                bool waiting = _socketTarget is { } t && t.Item == iid && t.Index == idx;
                _batch.Draw(_disc, px, sy, size, size, waiting ? Color.FromHex("#F2D25A") : new Color(60, 52, 42, 255));
                _batch.Draw(_disc, px + 3, sy + 3, size - 6, size - 6, new Color(16, 13, 10, 255));
                _buttons.Add((px, sy, size, size, () => { _socketTarget = (iid, idx); _invTab = "runes"; }));
            }
        }
    }

    /// <summary>Buttons for the selected item, right-aligned at <paramref name="right"/>, depending on the open window.</summary>
    private void SelectedActions(ItemInfo item, bool worn, float right, float y)
    {
        string sid = item.Id.ToString();
        var acts = new List<(string Label, Action Do, string Tone)>();
        switch (_openWindow)
        {
            case Win.Shop when _shop?.BuysGear == true && !worn:
                acts.Add(($"Sell {Math.Max(1, item.Value / 4):N0}g", () => _conn.Command("sell", sid), "gold")); break;
            case Win.Stash when !worn:
                acts.Add(("Store", () => _conn.Command(_stash?.Guild == true ? "gstash.put" : "stash.put", sid), "blue")); break;
            case Win.Trade when !worn:
                acts.Add(("Offer", () => _conn.Command("trade.add", sid), "blue")); break;
            case Win.Forge when _forge?.Mode == "athanor" && !worn:
                acts.Add((_athanor.Contains(item.Id) ? "Take out" : "Into the fire", () => ToggleAthanor(item.Id), "gold")); break;
            case Win.Forge when _forge?.Mode == "forge":
                acts.Add(("Anvil", () => { _forgeA = item.Id; if (_forgeB == item.Id) _forgeB = null; }, _forgeA == item.Id ? "gold" : "plain"));
                if (!worn) acts.Add(("Second", () => { _forgeB = item.Id; if (_forgeA == item.Id) _forgeA = null; }, _forgeB == item.Id ? "gold" : "plain"));
                break;
            case Win.Armoury when !worn:
                acts.Add(("To the rack", () => _conn.Command("armoury.put", sid), "blue")); break;
        }
        if (acts.Count == 0)
        {
            if (worn) acts.Add(("Take off", () => _conn.Command("unequip", item.Slot.ToString()), "plain"));
            else
            {
                acts.Add(("Wear", () => _conn.Command("equip", sid), "blue"));
                if (item.Slot == ItemSlot.Ring) acts.Add(("2nd hand", () => _conn.Command("equip", sid, "Ring2"), "plain"));
                acts.Add(("Drop", () => _conn.Command("discard", sid), "red"));
            }
        }
        float bx = right;
        foreach (var (label, act, tone) in Enumerable.Reverse(acts))
        {
            float bw = Math.Max(64, _small.Measure(label) + 20);
            bx -= bw;
            Button(bx, y, bw, 30, label, act, tone: tone);
            bx -= 6;
        }
    }

    private void QuickAction(ItemInfo item)
    {
        string sid = item.Id.ToString();
        switch (_openWindow)
        {
            case Win.Shop when _shop?.BuysGear == true: _conn.Command("sell", sid); return;
            case Win.Stash: _conn.Command(_stash?.Guild == true ? "gstash.put" : "stash.put", sid); return;
            case Win.Trade: _conn.Command("trade.add", sid); return;
            case Win.Armoury: _conn.Command("armoury.put", sid); return;
            case Win.Forge when _forge?.Mode == "athanor": ToggleAthanor(item.Id); return;
            case Win.Forge when _forge?.Mode == "forge": _forgeA = item.Id; return;
            default: _conn.Command("equip", sid); return;
        }
    }

    private void DollTile(PlayerStats? st, ItemSlot slot, float x, float y, float T)
    {
        var item = st?.Equipped.FirstOrDefault(i => i.Slot == slot);
        var dropSlot = slot;
        DropZone(x - 2, y - 2, T + 4, T + 4, (from, it) => from == DragFrom.Bag && Loot.Home(it.Slot) == Loot.Home(dropSlot),
            (_, it) => { if (dropSlot == ItemSlot.Ring2) _conn.Command("equip", it.Id.ToString(), "Ring2"); else _conn.Command("equip", it.Id.ToString()); });
        if (item is null) { EmptySlot(slot, x, y, T); return; }
        ItemTile(item, x, y, T, _selected == item.Id || _forgeA == item.Id);
        var id = item.Id;
        _buttons.Add((x, y, T, T, () => { _selected = id; _socketTarget = null; }));
        _rightButtons.Add((x, y, T, T, () => _conn.Command("unequip", dropSlot.ToString())));
        Draggable(x, y, T, T, item, DragFrom.Equipped);
        if (Over(x, y, T, T)) Tip(item);
    }

    /// <summary>The knight in the middle of the doll, standing in their current harness.</summary>
    private void DrawDollFigure(RemoteEntity me, float cx, float feetY)
    {
        _batch.Draw(_disc, cx - 70, feetY - 12, 140, 26, new Color(0, 0, 0, 120));
        var key = PlayerSprite(me.Current.PlayerClass, me.Current.PlayerWeapon);
        var sheet = _assets.Sheet(key);
        if (sheet is null) { _batch.Draw(_assets.Texture(key), cx - 110, feetY - 220, 220, 220, Color.White); return; }
        // About 2.6 m of sheet shown 300 px tall, whatever the sheet's own frame size.
        float px = sheet.Info.Metres > 0 ? 300f * sheet.Info.Metres / 2.6f : 300f;
        sheet.Draw(_batch, "idle", 90, AnimNow, cx, feetY, px / sheet.Info.FrameHeight, Color.White);
    }

    // ------------------------------------------------------------------ runes tab

    private void DrawRunesTab(PlayerStats? st, float x, float w)
    {
        var runes = _extras?.Runes ?? [];
        _small.Draw(_batch, $"{{#9A9080}}Rune pouch{{/}} {runes.Count}/{Runes.PouchLimit}", x + 28, 160, Color.White);
        _small.Draw(_batch, "{#7A7060}Every rune starts at level 1 and grows as you fight with it.{/}", x + 28, 186, Color.White);
        _small.Draw(_batch, $"{{#7A7060}}At level {Runes.MaxLevel}, fuse two alike at the forge for a finer tier.{{/}}", x + 28, 210, Color.White);

        // The item being socketed (the selected one).
        var target = st?.Inventory.Concat(st.Equipped).FirstOrDefault(i => i.Id == (_socketTarget?.Item ?? _selected));
        float ty = 244;
        _batch.FillRect(x + 16, ty, w - 32, 72, new Color(30, 25, 18, 255));
        if (target is null) _small.Draw(_batch, "{#7A7060}Select an item on the Gear tab to set runes in it.{/}", x + 28, ty + 24, Color.White);
        else
        {
            ItemTile(target, x + 26, ty + 10, 52, false);
            _small.Draw(_batch, $"{{{NameHex(target)}}}{target.Name}{{/}}", x + 90, ty + 8, Color.White);
            if (target.SocketList.Count == 0) _small.Draw(_batch, "{#7A7060}no sockets{/}", x + 90, ty + 38, Color.White);
            DrawSockets(target, x + 90, ty + 36, 28);
        }

        const int cols = 8;
        const float S = 50, gs = 56, gy0 = 336;
        float gx0 = x + 30;
        for (int i = 0; i < runes.Count && i < cols * 8; i++)
        {
            var r = runes[i];
            float gx = gx0 + i % cols * gs, gy = gy0 + i / cols * gs;
            RuneIcon(r, gx, gy, S);
            if (_selectedRune == r.Id) { var c = Color.FromHex("#F2D25A"); _batch.FillRect(gx, gy + S + 1, S, 3, c); }
            long rid = r.Id;
            _buttons.Add((gx, gy, S, S, () => _selectedRune = rid));
            if (Over(gx, gy, S, S)) Tip(r);
        }
        var sel = runes.FirstOrDefault(r => r.Id == _selectedRune);
        float ay = 876;
        _batch.FillRect(x + 16, ay - 4, w - 32, 40, new Color(30, 25, 18, 255));
        if (sel is null) { _small.Draw(_batch, "{#7A7060}Click a rune.{/}", x + 28, ay + 4, Color.White); return; }
        _small.Draw(_batch, $"{{{TierHex(sel.Tier)}}}{sel.Name}{{/}} {{#9A9080}}L{sel.Level}{{/}}", x + 28, ay + 4, Color.White);
        string srid = sel.Id.ToString();
        Button(x + w - 104, ay, 80, 30, "Discard", () => { _conn.Command("rune.discard", srid); _selectedRune = null; }, tone: "red");
        if (target is not null && target.SocketList.Count > 0)
        {
            int idx = _socketTarget is { } t && t.Item == target.Id ? t.Index : target.SocketList.FindIndex(s => s is null);
            string label = idx >= 0 && idx < target.SocketList.Count && target.SocketList[idx] is not null ? "Swap in" : "Set in socket";
            if (idx < 0) idx = 0;
            string tid = target.Id.ToString(), sidx = idx.ToString();
            Button(x + w - 260, ay, 150, 30, label, () => { _conn.Command("rune.socket", srid, tid, sidx); _socketTarget = null; _selectedRune = null; Sfx("rune_set", 0.7f); }, tone: "blue");
        }
    }

    // ------------------------------------------------------------------ wallet tab

    private void DrawWalletTab(PlayerStats? st, float x, float w)
    {
        float y = 164;
        foreach (var m in Materials.All)
        {
            int n = _extras?.Materials.GetValueOrDefault(m.Id) ?? 0;
            MaterialIcon(m.Id, x + 26, y, 40);
            _ui.Draw(_batch, $"{m.Name}  {{#F2D25A}}{n:N0}{{/}}", x + 80, y - 2, Color.FromHex("#E8E4DA"));
            _small.Draw(_batch, $"{{#7A7060}}{m.Text}{{/}}", x + 80, y + 26, Color.White);
            y += 74;
        }
        y += 8;
        _ui.Draw(_batch, "The Order's affairs", x + 26, y, Color.FromHex("#C9A227"));
        y += 36;
        if (_extras is { } e)
        {
            string mist = e.MistZone.Length == 0 ? "-" : $"{e.MistZone}, {Remaining(e.MistEndsUnix)} left";
            _small.Draw(_batch, $"{{#9A9080}}The Grey Mist holds:{{/}} {mist}", x + 26, y, Color.White); y += 26;
            _small.Draw(_batch, $"{{#9A9080}}La Tarasque rises in:{{/}} {Remaining(e.WorldBossUnix)}", x + 26, y, Color.White); y += 26;
            _small.Draw(_batch, $"{{#9A9080}}Deepest Proving Pit floor:{{/}} {e.PitBest}/10", x + 26, y, Color.White); y += 26;
            _small.Draw(_batch, $"{{#9A9080}}Relic arts:{{/}} {e.Relics.Count}   {{#9A9080}}Companions:{{/}} {e.Pets.Count}   {{#9A9080}}Traits researched:{{/}} {e.Researched.Count}/{Traits.All.Length}", x + 26, y, Color.White);
        }
        _small.Draw(_batch, "{#7A7060}J opens the Book of Deeds: sets, relic arts and companions.{/}", x + 26, 880, Color.White);
    }

    private static string Remaining(long unix)
    {
        var left = DateTimeOffset.FromUnixTimeSeconds(unix) - DateTimeOffset.UtcNow;
        if (left.TotalSeconds <= 0) return "now";
        return left.TotalHours >= 24 ? $"{(int)left.TotalDays}d {left.Hours}h" : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes:D2}m" : $"{left.Minutes}:{left.Seconds:D2}";
    }

    // ------------------------------------------------------------------ tooltips

    private void DrawTooltip(PlayerStats? st)
    {
        if (_dragging) { _tipItem = null; _tipRune = null; return; }
        var lines = new List<(string Text, bool Big)>();
        string edge = "#8A7330";
        if (_tipItem is { } it)
        {
            edge = NameHex(it);
            lines.Add(($"{{{NameHex(it)}}}{it.Name}{{/}}", true));
            string kind = it.Weapon != WeaponType.None ? Weapons.Describe(it.Weapon) : Loot.SlotName(it.Slot);
            lines.Add(($"{{#9A9080}}{(it.Exalted ? "Exalted " : "")}{it.Rarity} {kind.ToLowerInvariant()} · item level {it.ItemLevel}{{/}}", false));
            if (it.Damage > 0) lines.Add(($"Damage {{#E8E4DA}}{it.Damage}{{/}}", false));
            if (it.Defense > 0) lines.Add(($"Defense {{#E8E4DA}}{it.Defense}{{/}}", false));
            if (it.Upgrade > 0) lines.Add((it.Rarity >= Rarity.Exotic ? $"{{#F2D25A}}Empowered, rank {it.Upgrade}{{/}}" : $"{{#F2D25A}}Tempered +{it.Upgrade}{{/}}", false));
            var keys = it.Props.Keys.Concat(it.SocketList.Where(r => r is not null).Select(r => Runes.Kind(r!.Kind)?.Property ?? ""))
                .Concat(Traits.Get(it.Trait) is { } tt ? [tt.Property] : []).Where(k => k.Length > 0).Distinct();
            foreach (var k in keys) lines.Add(($"{{#9FC6FF}}{Loot.DescribeProperty(k, it.Total(k))}{{/}}", false));
            if (Traits.Get(it.Trait) is { } t) lines.Add(($"{{#8FD08F}}{t.Name}:{{/}} {{#9A9080}}{t.Text}{{/}}", false));
            for (int i = 0; i < it.SocketList.Count; i++)
                lines.Add((it.SocketList[i] is { } r ? $"{{{TierHex(r.Tier)}}}» {r.Name} {{/}}{{#9A9080}}L{r.Level}, +{r.Value} {Runes.Kind(r.Kind)?.Property}{{/}}" : "{#5A5448}· empty socket{/}", false));
            if (ItemSets.Get(it.Set) is { } set)
            {
                int worn = st?.Equipped.Count(e => e.Set == set.Id) ?? 0;
                lines.Add(($"{{#6BD66B}}{set.Name}{{/}} {{#9A9080}}({worn}/{set.Pieces.Count} worn){{/}}", false));
                foreach (var b in set.Bonuses)
                    lines.Add(($"{(worn >= b.Pieces ? "{#6BD66B}" : "{#5A6A5A}")}({b.Pieces}) {ItemSets.Describe(b.Props)}{{/}}", false));
                if (it.Exalted) lines.Add(($"{(worn >= 2 ? "{#FFC93C}" : "{#7A6A3A}")}(Exalted) {ItemSets.Describe(set.Exalted)}{{/}}", false));
            }
            if (it.Rarity >= Rarity.Exotic) lines.Add(($"{{#9A9080}}Only one {it.Rarity} piece can be worn at a time.{{/}}", false));
            lines.Add(($"{{#7A7060}}Worth {it.Value:N0}, sells for {Math.Max(1, it.Value / 4):N0}{{/}}", false));
        }
        else if (_tipRune is { } r)
        {
            edge = TierHex(r.Tier);
            var k = Runes.Kind(r.Kind);
            lines.Add(($"{{{TierHex(r.Tier)}}}{r.Name}{{/}}", true));
            lines.Add(($"{{#9A9080}}Level {r.Level}/{Runes.MaxLevel}{{/}}", false));
            lines.Add(($"{{#9FC6FF}}{Loot.DescribeProperty(k?.Property ?? "", r.Value)}{{/}}", false));
            lines.Add((r.Level >= Runes.MaxLevel ? "{#F2D25A}At its peak: fuse two alike at the forge.{/}" : $"{{#9A9080}}{r.Xp:N0} / {r.XpToNext:N0} to the next level{{/}}", false));
            if (k is not null) lines.Add(($"{{#7A7060}}{k.Flavour}{{/}}", false));
        }
        else return;
        float w = Math.Max(260, lines.Max(l => (l.Big ? _ui : _small).Measure(l.Text)) + 32);
        float h = lines.Sum(l => l.Big ? 36f : 25f) + 20;
        float tx = _tipAt.X + 22, ty = _tipAt.Y + 16;
        if (tx + w > VW - 8) tx = _tipAt.X - w - 16;
        if (ty + h > VH - 8) ty = VH - 8 - h;
        _batch.FillRect(tx, ty, w, h, new Color(14, 11, 8, 246));
        var ec = Color.FromHex(edge);
        _batch.FillRect(tx, ty, w, 2, ec); _batch.FillRect(tx, ty + h - 2, w, 2, ec);
        _batch.FillRect(tx, ty, 2, h, ec with { A = 120 }); _batch.FillRect(tx + w - 2, ty, 2, h, ec with { A = 120 });
        float ly = ty + 10;
        foreach (var (text, big) in lines)
        {
            (big ? _ui : _small).Draw(_batch, text, tx + 16, ly, Color.FromHex("#D8D2C4"), shadow: false);
            ly += big ? 36 : 25;
        }
        _tipItem = null;
        _tipRune = null;
    }

    /// <summary>A client-side copy of an item, so previews can use the same rules as the server.</summary>
    private static Item ToItem(ItemInfo i)
    {
        var it = new Item(i.Id, i.Name, i.Slot, i.Rarity, i.ItemLevel, i.Damage, i.Defense, i.Weapon)
        { Properties = new Dictionary<string, int>(i.Props), Upgrade = i.Upgrade, Trait = i.Trait, Set = i.Set, Exalted = i.Exalted, Forged = i.Forged };
        it.Sockets = i.SocketList.Select(r => r is null ? null : new Rune { Id = r.Id, Kind = r.Kind, Tier = r.Tier, Level = r.Level, Xp = r.Xp }).ToList();
        return it;
    }
}
