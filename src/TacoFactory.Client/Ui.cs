using Silk.NET.Input;
using TacoFactory.Client.Rendering;
using TacoFactory.Core.Sim;
using TacoFactory.Net;
using Color = TacoFactory.Client.Rendering.Color;

namespace TacoFactory.Client;

/// <summary>
/// Windows for the economy: inventory, merchants, stash, Forge, trade, guild and the auction house.
/// A tiny immediate-mode kit: every frame each window registers its buttons, text fields and the
/// screen area it covers (so clicks there don't reach the world).
/// </summary>
public sealed partial class GameClient
{
    private enum Win { None, Shop, Stash, Forge, Trade, Auction, Guild, Menu, Daily, Herald, Armoury, Pets, Book }

    private sealed class Field
    {
        public required string Id { get; init; }
        public string Text = "";
        public int Max = 40;
        public bool Numeric;
        public bool Secret;
        public Action? OnEnter;
    }

    private readonly List<(float X, float Y, float W, float H)> _blockRects = [];
    private Field? _focus;
    private Win _openWindow;
    private ShopState? _shop;
    private StashState? _stash;
    private ForgeState? _forge;
    private TradeState? _trade;
    private AuctionPage? _auction, _auctionMine;
    private GuildState? _guild;
    private string _auctionTab = "browse";
    private long? _selected;                // item selected in the bag (for details and the auction Sell tab)
    private long? _forgeA, _forgeB;
    private bool _forgeKeepFirst = true;
    private int _ahSlot = -1, _ahRarity = -1;

    // ---- drag and drop: rows register as draggable, windows register drop zones, every frame
    private enum DragFrom { Bag, Equipped, Stash }
    private readonly List<(float X, float Y, float W, float H, ItemInfo Item, DragFrom From)> _draggables = [];
    private readonly List<(float X, float Y, float W, float H, Func<DragFrom, ItemInfo, bool> Accepts, Action<DragFrom, ItemInfo> Drop)> _dropZones = [];
    private (ItemInfo Item, DragFrom From, Core.Math.Vec2 At)? _press;
    private bool _dragging;

    private readonly Field _ahSearch = new() { Id = "ah.search", Max = 30 };
    private readonly Field _ahMin = new() { Id = "ah.min", Text = "1", Max = 3, Numeric = true };
    private readonly Field _ahMax = new() { Id = "ah.max", Text = "100", Max = 3, Numeric = true };
    private readonly Field _ahPrice = new() { Id = "ah.price", Max = 8, Numeric = true };
    private readonly Field _tradeGold = new() { Id = "trade.gold", Text = "0", Max = 9, Numeric = true };
    private readonly Field _guildName = new() { Id = "guild.name", Max = 24 };
    private readonly Field _guildInvite = new() { Id = "guild.invite", Max = 16 };

    // ------------------------------------------------------------------ server messages

    private void HandleUi(string kind, string json)
    {
        switch (kind)
        {
            case UiKind.Shop: _shop = UiKind.Deserialize<ShopState>(json); Open(Win.Shop); break;
            case UiKind.Stash: _stash = UiKind.Deserialize<StashState>(json); Open(Win.Stash); break;
            case UiKind.Forge: _forge = UiKind.Deserialize<ForgeState>(json); Open(Win.Forge); break;
            case UiKind.Trade:
                _trade = UiKind.Deserialize<TradeState>(json);
                if (_trade is not null && _focus != _tradeGold) _tradeGold.Text = _trade.MyGold.ToString();
                Open(Win.Trade);
                break;
            case UiKind.Auction: _auction = UiKind.Deserialize<AuctionPage>(json); _auctionTab = "browse"; Open(Win.Auction); break;
            case UiKind.AuctionMine: _auctionMine = UiKind.Deserialize<AuctionPage>(json); _auctionTab = "mine"; Open(Win.Auction); break;
            case UiKind.Guild: _guild = UiKind.Deserialize<GuildState>(json); Open(Win.Guild); break;
            case UiKind.Cutscene or UiKind.Choice or UiKind.Dialogue:
                HandleStoryUi(kind, json);
                break;
            case UiKind.Close:
                if (UiKind.Deserialize<string>(json) == UiKind.Trade && _openWindow == Win.Trade) CloseWindow();
                break;
            default:
                HandleEndgameUi(kind, json);
                break;
        }
    }

    private void Open(Win w)
    {
        if (_openWindow != w) Sfx("ui_open", 0.5f);
        _openWindow = w;
        _charOpen = _skillsOpen = _questsOpen = _waypointsOpen = false;
        // The bag sits beside merchant, stash, forge, trade and armoury windows.
        _invOpen = w is not Win.Auction and not Win.Guild and not Win.Menu and not Win.Daily and not Win.Herald and not Win.Pets and not Win.Book;
        if (w == Win.Forge) _forgeTab = _forge?.Mode == "forge" && _forgeTab is "" ? "temper" : _forgeTab;
    }

    private void CloseWindow()
    {
        if (_openWindow == Win.Trade) _conn.Command("trade.cancel");
        _openWindow = Win.None;
        _focus = null;
        _forgeA = _forgeB = null;
        _fuseA = _fuseB = null;
        _athanor.Clear();
        _athanorRunes.Clear();
    }

    // ------------------------------------------------------------------ kit

    private bool Button(float x, float y, float w, float h, string label, Action onClick, bool enabled = true, string tone = "gold")
    {
        var mouse = MouseVirtual();
        bool hover = enabled && mouse.X >= x && mouse.X <= x + w && mouse.Y >= y && mouse.Y <= y + h;
        var (bg, fg) = tone switch
        {
            "blue" => (Color.FromHex(hover ? "#2A527E" : "#1E3A5A"), "#CFE0FF"),
            "red" => (Color.FromHex(hover ? "#7A2A24" : "#5A1E1A"), "#F4D0C8"),
            "plain" => (Color.FromHex(hover ? "#3A3228" : "#2A241C"), "#E8E4DA"),
            _ => (Color.FromHex(hover ? "#7A6428" : "#5A4A1E"), "#F2D25A"),
        };
        if (!enabled) (bg, fg) = (new Color(40, 36, 30, 255), "#6A6050");
        _batch.FillRect(x, y, w, h, bg);
        _small.DrawCentered(_batch, label, x + w / 2, y + (h - 24) / 2, Color.FromHex(fg), shadow: false);
        if (enabled) _buttons.Add((x, y, w, h, onClick));
        return hover;
    }

    private void Draggable(float x, float y, float w, float h, ItemInfo item, DragFrom from) => _draggables.Add((x, y, w, h, item, from));

    private void DropZone(float x, float y, float w, float h, Func<DragFrom, ItemInfo, bool> accepts, Action<DragFrom, ItemInfo> drop) =>
        _dropZones.Add((x, y, w, h, accepts, drop));

    /// <summary>Mouse pressed over a window: remember the item under it; it becomes a drag once the mouse moves.</summary>
    private void BeginPress()
    {
        var m = MouseVirtual();
        var hit = _draggables.LastOrDefault(d => m.X >= d.X && m.X <= d.X + d.W && m.Y >= d.Y && m.Y <= d.Y + d.H);
        _press = hit.Item is null ? null : (hit.Item, hit.From, m);
        _dragging = false;
    }

    private void UpdateDrag()
    {
        if (_press is not { } p) return;
        if (_input.Mice.Count == 0 || !_input.Mice[0].IsButtonPressed(MouseButton.Left)) { EndPress(); return; }
        if (!_dragging && Core.Math.Vec2.Distance(MouseVirtual(), p.At) > 8) _dragging = true;
    }

    /// <summary>Mouse released: drop onto the zone under the cursor if it takes this item.</summary>
    private void EndPress()
    {
        if (_press is { } p && _dragging)
        {
            var m = MouseVirtual();
            foreach (var z in Enumerable.Reverse(_dropZones))
                if (m.X >= z.X && m.X <= z.X + z.W && m.Y >= z.Y && m.Y <= z.Y + z.H && z.Accepts(p.From, p.Item)) { z.Drop(p.From, p.Item); break; }
        }
        _press = null;
        _dragging = false;
    }

    /// <summary>While dragging: outline every place that takes the item and draw it under the cursor.</summary>
    private void DrawDrag()
    {
        if (!_dragging || _press is not { } p) return;
        var m = MouseVirtual();
        foreach (var z in _dropZones)
        {
            if (!z.Accepts(p.From, p.Item)) continue;
            bool over = m.X >= z.X && m.X <= z.X + z.W && m.Y >= z.Y && m.Y <= z.Y + z.H;
            var c = Color.FromHex(over ? "#F2D25A" : "#8A7330");
            float t = over ? 3 : 1;
            _batch.FillRect(z.X, z.Y, z.W, t, c); _batch.FillRect(z.X, z.Y + z.H - t, z.W, t, c);
            _batch.FillRect(z.X, z.Y, t, z.H, c); _batch.FillRect(z.X + z.W - t, z.Y, t, z.H, c);
        }
        float w = _small.Measure(p.Item.Name) + 24;
        _batch.FillRect(m.X + 14, m.Y + 10, w, 30, new Color(30, 25, 18, 235));
        _batch.FillRect(m.X + 14, m.Y + 10, 3, 30, Color.FromHex(RarityHex(p.Item.Rarity)));
        _small.Draw(_batch, RarityName(p.Item), m.X + 26, m.Y + 13, Color.White, shadow: false);
    }

    private void TextField(Field f, float x, float y, float w, string placeholder = "")
    {
        bool focused = _focus == f;
        _batch.FillRect(x, y, w, 30, Color.FromHex(focused ? "#F4EFE3" : "#E8E2D2"));
        _batch.FillRect(x, y + 28, w, 2, Color.FromHex(focused ? "#C9A227" : "#8A8070"));
        string text = f.Secret ? new string('*', f.Text.Length) : f.Text.Replace('{', '(');
        string shown = f.Text.Length == 0 && !focused ? $"{{#8A8070}}{placeholder}{{/}}" : text + (focused && (int)(Now * 2) % 2 == 0 ? "|" : "");
        _small.Draw(_batch, shown, x + 8, y + 3, Color.FromHex("#1A1612"), shadow: false);
        _buttons.Add((x, y, w, 30, () => _focus = f));
    }

    private void FieldChar(char ch)
    {
        if (_focus is null || char.IsControl(ch) || _focus.Text.Length >= _focus.Max) return;
        if (_focus.Numeric && !char.IsDigit(ch)) return;
        _focus.Text += ch;
    }

    private void FieldKey(Key key)
    {
        if (_focus is null) return;
        switch (key)
        {
            case Key.Backspace when _focus.Text.Length > 0: _focus.Text = _focus.Text[..^1]; break;
            case Key.Enter or Key.KeypadEnter: var f = _focus; _focus = null; f.OnEnter?.Invoke(); break;
            case Key.Tab: if (!FrontTab()) _focus = null; break;
            case Key.Escape: _focus = null; break;
        }
    }

    private Core.Math.Vec2 MouseVirtual()
    {
        if (_input.Mice.Count == 0) return Core.Math.Vec2.Zero;
        var m = _input.Mice[0].Position;
        var (scale, ox, oy) = Letterbox(_window.Size);
        return new((m.X - ox) / scale, (m.Y - oy) / scale);
    }

    private void WinFrame(float x, float y, float w, float h, string title)
    {
        _blockRects.Add((x, y, w, h));
        _batch.FillRect(x, y, w, h, new Color(16, 13, 10, 250));
        _batch.FillRect(x, y, w, 2, Color.FromHex("#C9A227"));
        _batch.FillRect(x, y + h - 2, w, 2, Color.FromHex("#C9A227"));
        _title.Draw(_batch, title, x + 24, y + 16, Color.FromHex("#C9A227"));
        Button(x + w - 44, y + 14, 30, 30, "X", CloseWindow, tone: "plain");
    }

    private static string ItemDetail(ItemInfo i)
    {
        var parts = new List<string> { $"{i.Rarity}", i.Weapon != WeaponType.None ? Weapons.Describe(i.Weapon) : i.Slot.ToString(), $"level {i.ItemLevel}" };
        if (i.Damage > 0) parts.Add($"damage {i.Damage}");
        if (i.Defense > 0) parts.Add($"defense {i.Defense}");
        if (i.Forged) parts.Add("forged");
        return string.Join(" · ", parts);
    }

    private static string PropsLine(ItemInfo i) =>
        i.Props.Count == 0 ? "{#6A6050}no magical properties{/}" : string.Join(", ", i.Props.Select(kv => Loot.DescribeProperty(kv.Key, kv.Value)));

    private string RarityName(ItemInfo i) => $"{{{RarityHex(i.Rarity)}}}{i.Name}{{/}}";

    /// <summary>The rendered icon for an item: its own base item's icon (assets/ui/icons/items), else its slot's.</summary>
    private string IconKey(ItemInfo i)
    {
        if (Loot.BaseNameOf(i.Name) is { } b)
        {
            string key = $"ui/icons/items/{Loot.Slug(b)}.png";
            if (_assets.Exists(key)) return key;
        }
        var slotKey = SlotIconKey(i);
        return _assets.Exists(slotKey) ? slotKey : "sprites/loot_item.png";
    }

    private static string SlotIconKey(ItemInfo i) => i.Slot switch
    {
        ItemSlot.Armor => "ui/icons/armor.png",
        ItemSlot.Helm => "ui/icons/helm.png",
        ItemSlot.Shield => "ui/icons/shield.png",
        ItemSlot.Weapon => i.Weapon switch
        {
            WeaponType.Dagger => "ui/icons/dagger.png",
            WeaponType.LightTwoHanded => "ui/icons/polearm.png",
            WeaponType.HeavyTwoHanded => "ui/icons/great.png",
            WeaponType.Throwing => "ui/icons/throwing.png",
            _ => "ui/icons/sword.png",
        },
        // Newer slots: the first base item of the slot stands in until the piece has its own icon.
        _ => $"ui/icons/items/{Loot.Slug(Loot.NamesFor(i.Slot)[0])}.png",
    };

    /// <summary>Draws an item icon on a small backing tinted by rarity. Returns the width used (icon plus gap).</summary>
    private float ItemIcon(ItemInfo i, float x, float y, float size = 26)
    {
        var tint = Color.FromHex(RarityHex(i.Rarity));
        _batch.FillRect(x, y, size, size, new Color(16, 13, 10, 230));
        _batch.FillRect(x, y + size - 2, size, 2, new Color(tint.R, tint.G, tint.B, (byte)200));
        _batch.Draw(_assets.Texture(IconKey(i)), x + 1, y + 1, size - 2, size - 2, Color.White);
        return size + 6;
    }

    // ------------------------------------------------------------------ dispatch

    private bool _demoTalked, _demoCommanded, _demoMenu, _demoOpened;

    /// <summary>Scripted actions for screenshots and demos (--talk, --cmd, --forge-demo).</summary>
    private void DemoTick(PlayerStats? st)
    {
        if (_options.OpenMenu && !_demoMenu) { _demoMenu = true; Open(Win.Menu); }
        if (_options.OpenOnArrival is string what && !_demoOpened && st is not null && _extras is not null)
        {
            _demoOpened = true;
            switch (what)
            {
                case "bag" or "runes" or "wallet": _invOpen = true; _invTab = what == "bag" ? "gear" : what; _selected = st.Inventory.FirstOrDefault()?.Id; break;
                case "book" or "relics" or "pets": Open(Win.Book); _bookTab = what == "book" ? "sets" : what; break;
            }
        }
        if (_conn.Welcome is not { } wel || st is null) return;
        if (_options.TalkTo is string name && !_demoTalked)
        {
            int index = wel.NpcList.IndexOf(name);
            var npc = _conn.Entities.Values.FirstOrDefault(e => e.Current.Kind == EntityKind.Npc && e.Current.TypeIndex == index);
            if (npc is not null) { _talkTarget = npc.Current.Id; _demoTalked = true; }
        }
        if (_options.SendCommand is string cmd && !_demoCommanded && (_options.TalkTo is null || _openWindow != Win.None))
        {
            foreach (var one in cmd.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (one.StartsWith('/')) { _conn.SendChat(one); continue; }
                var parts = one.Split(' ');
                _conn.Command(parts[0], parts[1..]);
            }
            _demoCommanded = true;
        }
        if (_options.ForgeDemo && _openWindow == Win.Forge && _forgeA is null)
        {
            var weapons = st.Inventory.Where(i => i.Slot == ItemSlot.Weapon).Take(2).ToList();
            if (weapons.Count < 2) weapons = st.Inventory.Take(2).ToList();
            if (weapons.Count == 2) { _forgeA = weapons[0].Id; _forgeB = weapons[1].Id; _selected = weapons[0].Id; }
        }
    }

    private void DrawWindow(PlayerStats? st, RemoteEntity me)
    {
        DemoTick(st);
        UpdateDrag(); // uses last frame's zones, so before they are cleared
        _draggables.Clear();
        _dropZones.Clear();
        if (_invOpen) DrawInventoryPanel(st, me);
        switch (_openWindow)
        {
            case Win.Shop: DrawShop(st); break;
            case Win.Stash: DrawStash(); break;
            case Win.Forge: DrawForge(st); break;
            case Win.Trade: DrawTrade(); break;
            case Win.Auction: DrawAuction(st); break;
            case Win.Guild: DrawGuild(); break;
            case Win.Menu: DrawMenu(); break;
            case Win.Daily: DrawDaily(); break;
            case Win.Herald: DrawHerald(); break;
            case Win.Armoury: DrawArmoury(st); break;
            case Win.Pets: DrawPets(st); break;
            case Win.Book: DrawBook(st); break;
        }
        DrawFps();
        DrawDrag();
        DrawTooltip(st);
        DrawForgeScene((float)_frameDt);
        if (_conn.Welcome is { } wel) TutorialTick(wel, me, st);
        DrawHint();
        DrawStory(me);
        DrawPadFocus();
    }

    // ------------------------------------------------------------------ merchant

    private void DrawShop(PlayerStats? st)
    {
        if (_shop is not { } shop) return;
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, shop.Merchant.ToUpperInvariant());
        if (shop.BuysGear) DropZone(x, y0, w, h, (from, _) => from == DragFrom.Bag, (_, it) => _conn.Command("sell", it.Id.ToString()));
        float y = y0 + 64;
        int gold = st?.Gold ?? 0;
        if (shop.SellsPotions)
        {
            _ui.Draw(_batch, "Potions", x + 24, y, Color.FromHex("#C9A227"));
            y += 36;
            foreach (var (label, kind, price, have) in new[] { ("Health potion", "health", shop.HealthPrice, st?.HealthPotions ?? 0), ("Mana potion", "mana", shop.ManaPrice, st?.ManaPotions ?? 0) })
            {
                _small.Draw(_batch, $"{label}  {{#7A7060}}you carry {have}/{Merchant.MaxPotions}{{/}}", x + 24, y + 3, Color.FromHex("#E8E4DA"));
                Button(x + w - 300, y, 130, 28, $"Buy 1  {price}g", () => _conn.Command("potion", kind, "1"), enabled: gold >= price);
                Button(x + w - 160, y, 136, 28, $"Buy 5  {price * 5}g", () => _conn.Command("potion", kind, "5"), enabled: gold >= price * 5);
                y += 36;
            }
            y += 16;
        }
        if (shop.Stock.Count > 0)
        {
            _ui.Draw(_batch, "For sale", x + 24, y, Color.FromHex("#C9A227"));
            _small.Draw(_batch, "{#7A7060}New stock every 15 minutes. Sell from your bag on the right.{/}", x + 160, y + 6, Color.White);
            y += 36;
            foreach (var e in shop.Stock)
            {
                var it = e.Item;
                float sw = ItemIcon(it, x + 20, y - 2);
                _small.Draw(_batch, $"{RarityName(it)}  {{#7A7060}}i{it.ItemLevel}{{/}}", x + 20 + sw, y, Color.White);
                _small.Draw(_batch, $"{{#7FA0C8}}{(it.Props.Count == 0 ? "" : string.Join(", ", it.Props.Select(kv => $"{kv.Key} {kv.Value}")))}{{/}}", x + 330, y, Color.White);
                string sid = it.Id.ToString();
                Button(x + w - 150, y - 2, 126, 28, $"{e.Price:N0}g", () => _conn.Command("buy", sid), enabled: gold >= e.Price);
                y += 34;
            }
        }
        if (shop.Gambles)
        {
            y = Math.Max(y + 12, y0 + h - 186);
            _ui.Draw(_batch, "Gamble", x + 24, y, Color.FromHex("#C9A227"));
            _small.Draw(_batch, $"{{#7A7060}}{shop.GamblePrice:N0} gold for an unseen item, Uncommon or better{{/}}", x + 130, y + 6, Color.White);
            y += 38;
            int n = 0;
            foreach (var slot in Enum.GetValues<ItemSlot>().Where(sl => sl != ItemSlot.Ring2))
            {
                var sl = slot.ToString();
                Button(x + 24 + n % 6 * 112, y + n / 6 * 38, 104, 30, Loot.SlotName(slot), () => _conn.Command("gamble", sl), enabled: gold >= shop.GamblePrice);
                n++;
            }
        }
    }

    // ------------------------------------------------------------------ stash

    private void DrawStash()
    {
        if (_stash is not { } stash) return;
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, stash.Title.ToUpperInvariant());
        DropZone(x, y0, w, h, (from, _) => from == DragFrom.Bag, (_, it) => _conn.Command(stash.Guild ? "gstash.put" : "stash.put", it.Id.ToString()));
        _small.Draw(_batch, $"{{#7A7060}}{stash.Items.Count}/{stash.Limit} slots{(stash.Guild ? " · shared by every member" : " · only you can open it")}{{/}}", x + 24, y0 + 64, Color.White);
        float y = y0 + 100;
        foreach (var it in stash.Items.Take(20))
        {
            Draggable(x + 20, y - 4, 300, 30, it, DragFrom.Stash);
            float tw = ItemIcon(it, x + 20, y - 2);
            _small.Draw(_batch, $"{RarityName(it)}  {{#7A7060}}i{it.ItemLevel}{{/}}", x + 20 + tw, y, Color.White);
            _small.Draw(_batch, $"{{#7FA0C8}}{string.Join(", ", it.Props.Select(kv => $"{kv.Key} {kv.Value}"))}{{/}}", x + 330, y, Color.White);
            string sid = it.Id.ToString();
            Button(x + w - 130, y - 2, 106, 28, "Take", () => _conn.Command(stash.Guild ? "gstash.take" : "stash.take", sid), tone: "blue");
            y += 34;
        }
        if (stash.Items.Count > 20) _small.Draw(_batch, $"{{#7A7060}}and {stash.Items.Count - 20} more{{/}}", x + 24, y, Color.White);
    }

    // ------------------------------------------------------------------ trade

    private void DrawTrade()
    {
        if (_trade is not { } t) return;
        const float x = 280, w = 720, y0 = 100, h = 820;
        WinFrame(x, y0, w, h, $"TRADE WITH {t.With.ToUpperInvariant()}");
        _small.Draw(_batch, "{#9A9080}Offer items from your bag. Any change clears both acceptances; when both accept, the trade happens.{/}", x + 24, y0 + 64, Color.White);
        float col = (w - 72) / 2;
        foreach (var (title, items, gold, accepted, mine, cx) in new[]
                 { ("You offer", t.Mine, t.MyGold, t.MyAccept, true, x + 24), ($"{t.With} offers", t.Theirs, t.TheirGold, t.TheirAccept, false, x + 48 + col) })
        {
            float y = y0 + 104;
            _batch.FillRect(cx, y, col, 560, new Color(30, 25, 18, 255));
            if (mine) DropZone(cx, y, col, 560, (from, it) => from == DragFrom.Bag && !t.Mine.Any(m => m.Id == it.Id), (_, it) => _conn.Command("trade.add", it.Id.ToString()));
            _batch.FillRect(cx, y, col, 3, accepted ? Color.FromHex("#6BD66B") : Color.FromHex("#5A4A1E"));
            _ui.Draw(_batch, title, cx + 12, y + 10, Color.FromHex("#C9A227"));
            _small.Draw(_batch, accepted ? "{#6BD66B}accepted{/}" : "{#7A7060}not accepted{/}", cx + col - 130, y + 16, Color.White);
            y += 52;
            foreach (var it in items)
            {
                float cw2 = ItemIcon(it, cx + 10, y - 2);
                _small.Draw(_batch, RarityName(it), cx + 10 + cw2, y, Color.White);
                if (mine)
                {
                    string sid = it.Id.ToString();
                    Button(cx + col - 90, y - 2, 78, 26, "Remove", () => _conn.Command("trade.remove", sid), tone: "red");
                }
                y += 32;
            }
            _small.Draw(_batch, $"{{#E6C14A}}{gold:N0} gold{{/}}", cx + 12, y0 + 620, Color.White);
        }
        TextField(_tradeGold, x + 24, y0 + 690, 160, "gold");
        _tradeGold.OnEnter = () => _conn.Command("trade.gold", _tradeGold.Text);
        Button(x + 196, y0 + 690, 140, 30, "Offer gold", () => _conn.Command("trade.gold", _tradeGold.Text), tone: "blue");
        Button(x + w - 360, y0 + h - 64, 160, 40, t.MyAccept ? "Accepted" : "Accept", () => _conn.Command("trade.accept"), enabled: !t.MyAccept);
        Button(x + w - 184, y0 + h - 64, 160, 40, "Cancel", CloseWindow, tone: "red");
    }

    // ------------------------------------------------------------------ guild

    private void DrawGuild()
    {
        const float x = 280, w = 900, y0 = 100, h = 820;
        var g = _guild;
        WinFrame(x, y0, w, h, g?.Name is null ? "COMMANDERIES" : $"COMMANDERY OF {g.Name.ToUpperInvariant()}");
        float y = y0 + 70;
        if (g?.Name is null)
        {
            _small.Draw(_batch, "{#9A9080}A Commandery is a guild: a name, a hall beneath the Sanctuary with a shared strongbox, and guild chat (/g).{/}", x + 24, y, Color.White);
            y += 44;
            TextField(_guildName, x + 24, y, 340, "name of your Commandery");
            Button(x + 380, y, 300, 30, $"Found it ({g?.CreateCost ?? 2000:N0} gold)", () => _conn.SendChat($"/guild create {_guildName.Text}"));
            y += 50;
            _small.Draw(_batch, "{#7A7060}Or ask a Grand Master or Preceptor to invite you: /accept when they do.{/}", x + 24, y, Color.White);
            return;
        }
        if (!string.IsNullOrWhiteSpace(g.Motto)) { _small.Draw(_batch, $"{{#C9B88A}}\"{g.Motto}\"{{/}}", x + 24, y, Color.White); y += 30; }
        _small.Draw(_batch, $"{{#7A7060}}{g.Members.Count} members · {g.Members.Count(m => m.Online)} online · guild strongbox holds {g.StashCount} items · hall: north door of the Sanctuary{{/}}", x + 24, y, Color.White);
        y += 40;
        // Roster in the same table style as the auction house.
        string[] heads = ["Name", "Rank", "Class", "Level", "Status"];
        float[] widths = [260, 180, 160, 100, 140];
        y = TableHeader(x + 24, y, heads, widths, null, false, null);
        int row = 0;
        foreach (var m in g.Members.Take(16))
        {
            TableRow(x + 24, y, widths, row++, false,
                [m.Name, m.Rank, m.Class.ToString(), m.Level.ToString(), m.Online ? "{#6BD66B}online{/}" : "{#7A7060}offline{/}"], [false, false, false, true, false]);
            y += 32;
        }
        y = y0 + h - 64;
        TextField(_guildInvite, x + 24, y, 240, "name to invite");
        Button(x + 276, y, 120, 30, "Invite", () => _conn.SendChat($"/guild invite {_guildInvite.Text}"), tone: "blue");
        Button(x + w - 160, y, 136, 30, "Leave guild", () => _conn.SendChat("/guild leave"), tone: "red");
    }

    // ------------------------------------------------------------------ auction house

    /// <summary>
    /// The auction house is laid out like an HTML table: a header row with sortable column titles,
    /// 1-pixel grid lines, striped rows that light up under the mouse, right-aligned numbers and a pager.
    /// </summary>
    private void DrawAuction(PlayerStats? st)
    {
        const float x = 120, w = 1420, y0 = 90, h = 750;
        WinFrame(x, y0, w, h, "AUCTION HOUSE");
        _small.Draw(_batch, "{#9A9080}one ledger for every town · buyout only · listings run 48 hours{/}", x + 290, y0 + 26, Color.White);
        float tabY = y0 + 64;
        foreach (var (tab, label) in new[] { ("browse", "Browse"), ("mine", "My listings"), ("sell", "Sell") })
        {
            string t = tab;
            Button(x + 24 + (tab == "browse" ? 0 : tab == "mine" ? 148 : 296), tabY, 140, 32, label, () =>
            {
                _auctionTab = t;
                if (t == "mine") _conn.Command("ah.mine");
                if (t == "browse") SearchAuctions(_auction?.Page ?? 1);
            }, tone: _auctionTab == tab ? "gold" : "plain");
        }
        _small.Draw(_batch, $"{{#E6C14A}}{st?.Gold ?? 0:N0} gold{{/}}", x + w - 200, tabY + 4, Color.White);
        float y = tabY + 52;
        switch (_auctionTab)
        {
            case "browse": DrawAuctionBrowse(x, y, w, st); break;
            case "mine": DrawAuctionTable(x + 24, y, _auctionMine, st, mine: true); break;
            case "sell": DrawAuctionSell(x, y, w, st); break;
        }
    }

    private static readonly string[] SlotNames = ["Weapon", "Armor", "Helm", "Shield"];
    private static readonly string[] RarityNames = ["Common", "Uncommon", "Rare", "Elite", "Epic", "Legendary"];

    private void SearchAuctions(int page, string? sort = null, bool? desc = null)
    {
        _conn.Command("ah.search", _ahSearch.Text,
            _ahSlot < 0 ? "" : SlotNames[_ahSlot], _ahRarity < 0 ? "" : RarityNames[_ahRarity],
            string.IsNullOrEmpty(_ahMin.Text) ? "1" : _ahMin.Text, string.IsNullOrEmpty(_ahMax.Text) ? "100" : _ahMax.Text,
            sort ?? _auction?.Sort ?? "price", (desc ?? _auction?.Descending ?? false) ? "1" : "0", page.ToString());
    }

    private void DrawAuctionBrowse(float x, float y, float w, PlayerStats? st)
    {
        // Filter bar, like a form above a table.
        _small.Draw(_batch, "Search", x + 24, y + 4, Color.FromHex("#9A9080"));
        TextField(_ahSearch, x + 96, y, 300, "item or seller");
        _ahSearch.OnEnter = () => SearchAuctions(1);
        Button(x + 410, y, 160, 30, _ahSlot < 0 ? "Any slot" : SlotNames[_ahSlot], () => _ahSlot = _ahSlot >= 3 ? -1 : _ahSlot + 1, tone: "plain");
        Button(x + 580, y, 180, 30, _ahRarity < 0 ? "Any rarity" : RarityNames[_ahRarity] + "+", () => _ahRarity = _ahRarity >= 5 ? -1 : _ahRarity + 1, tone: "plain");
        _small.Draw(_batch, "Level", x + 776, y + 4, Color.FromHex("#9A9080"));
        TextField(_ahMin, x + 836, y, 60);
        _small.Draw(_batch, "to", x + 904, y + 4, Color.FromHex("#9A9080"));
        TextField(_ahMax, x + 932, y, 60);
        _ahMin.OnEnter = _ahMax.OnEnter = () => SearchAuctions(1);
        Button(x + 1010, y, 130, 30, "Search", () => SearchAuctions(1));
        DrawAuctionTable(x + 24, y + 48, _auction, st, mine: false);
    }

    private void DrawAuctionTable(float x, float y, AuctionPage? page, PlayerStats? st, bool mine)
    {
        string[] heads = ["Item", "Lvl", "Rarity", "Slot", "Properties", "Seller", "Ends in", "Price", ""];
        string[] keys = ["name", "level", "rarity", "slot", "", "seller", "ends", "price", ""];
        float[] widths = [300, 56, 110, 100, 340, 150, 110, 120, 86];
        y = TableHeader(x, y, heads, widths, mine ? null : keys, page?.Descending ?? false, page?.Sort);
        if (page is null || page.Rows.Count == 0)
        {
            _batch.FillRect(x, y, widths.Sum(), 44, new Color(28, 24, 18, 255));
            _small.DrawCentered(_batch, mine ? "{#7A7060}You have nothing listed.{/}" : "{#7A7060}No listings match. Be the first to sell one.{/}", x + widths.Sum() / 2, y + 10, Color.White);
            return;
        }
        int gold = st?.Gold ?? 0;
        int r = 0;
        foreach (var row in page.Rows)
        {
            var it = row.Item;
            var left = TimeSpan.FromSeconds(Math.Max(0, row.ExpiresUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            string ends = left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{left.Minutes}m";
            string props = it.Props.Count == 0 ? "{#5A5448}-{/}" : string.Join(", ", it.Props.Select(kv => $"{kv.Key} {kv.Value}"));
            bool hover = TableRow(x, y, widths, r++, row.Mine,
                [RarityName(it), it.ItemLevel.ToString(), $"{{{RarityHex(it.Rarity)}}}{it.Rarity}{{/}}", it.Slot.ToString(), $"{{#9FC6FF}}{props}{{/}}",
                 row.Mine ? "{#F2D25A}you{/}" : row.Seller, ends, $"{{#E6C14A}}{row.Price:N0}{{/}}", ""],
                [false, true, false, false, false, false, true, true, false]);
            float bx = x + widths.Take(8).Sum() + 6;
            string lid = row.ListingId.ToString();
            if (row.Mine) Button(bx, y + 3, widths[8] - 12, 26, "Cancel", () => _conn.Command("ah.cancel", lid), tone: "red");
            else Button(bx, y + 3, widths[8] - 12, 26, "Buy", () => _conn.Command("ah.buy", lid), enabled: gold >= row.Price);
            if (hover) _selected = it.Id;
            y += 32;
        }
        // Pager and caption, like a table footer.
        int from = (page.Page - 1) * 15 + 1, to = from + page.Rows.Count - 1;
        _small.Draw(_batch, $"{{#9A9080}}Showing {from}-{to} of {page.Total}  ·  posting fee {page.PostingFeePercent}%, the house keeps {page.SaleCutPercent}% of each sale{{/}}", x, y + 12, Color.White);
        if (!mine)
        {
            float px = x + widths.Sum() - 280;
            Button(px, y + 8, 90, 28, "< Prev", () => SearchAuctions(page.Page - 1), enabled: page.Page > 1, tone: "plain");
            _small.DrawCentered(_batch, $"page {page.Page} / {page.Pages}", px + 140, y + 10, Color.FromHex("#9A9080"));
            Button(px + 190, y + 8, 90, 28, "Next >", () => SearchAuctions(page.Page + 1), enabled: page.Page < page.Pages, tone: "plain");
        }
    }

    /// <summary>Header row: darker cells, bold titles, sort arrows; clicking a sortable title re-sorts (again to flip).</summary>
    private float TableHeader(float x, float y, string[] heads, float[] widths, string[]? sortKeys, bool desc, string? sortedBy)
    {
        float total = widths.Sum();
        _batch.FillRect(x, y, total, 34, Color.FromHex("#3A2F22"));
        float cx = x;
        for (int i = 0; i < heads.Length; i++)
        {
            string key = sortKeys?[i] ?? "";
            string arrow = key.Length > 0 && key == sortedBy ? (desc ? "  v" : "  ^") : "";
            _small.Draw(_batch, $"{heads[i]}{arrow}", cx + 8, y + 5, Color.FromHex(key == sortedBy && key.Length > 0 ? "#F2D25A" : "#E8D7A0"), shadow: false);
            if (key.Length > 0)
            {
                bool flip = key == sortedBy && !desc;
                _buttons.Add((cx, y, widths[i], 34, () => SearchAuctions(1, key, flip)));
            }
            _batch.FillRect(cx, y, 1, 34, Color.FromHex("#5A4A32"));
            cx += widths[i];
        }
        _batch.FillRect(cx - 1, y, 1, 34, Color.FromHex("#5A4A32"));
        _batch.FillRect(x, y + 33, total, 2, Color.FromHex("#C9A227"));
        return y + 35;
    }

    /// <summary>One table row with zebra striping, grid lines and a hover highlight. Returns whether the mouse is over it.</summary>
    private bool TableRow(float x, float y, float[] widths, int index, bool highlight, string[] cells, bool[] rightAlign)
    {
        float total = widths.Sum();
        var m = MouseVirtual();
        bool hover = m.X >= x && m.X <= x + total && m.Y >= y && m.Y < y + 32;
        var bg = hover ? Color.FromHex("#4A3C26") : highlight ? Color.FromHex("#2E2A1A") : index % 2 == 0 ? Color.FromHex("#221D16") : Color.FromHex("#2A241B");
        _batch.FillRect(x, y, total, 32, bg);
        float cx = x;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i].Length > 0)
            {
                if (rightAlign[i])
                    _small.Draw(_batch, cells[i], cx + widths[i] - 10 - _small.Measure(cells[i]), y + 4, Color.FromHex("#E8E4DA"), shadow: false);
                else
                    _small.Draw(_batch, Clip(cells[i], widths[i] - 16), cx + 8, y + 4, Color.FromHex("#E8E4DA"), shadow: false);
            }
            _batch.FillRect(cx, y, 1, 32, Color.FromHex("#3A3226"));
            cx += widths[i];
        }
        _batch.FillRect(cx - 1, y, 1, 32, Color.FromHex("#3A3226"));
        _batch.FillRect(x, y + 31, total, 1, Color.FromHex("#3A3226"));
        return hover;
    }

    /// <summary>Shortens text to fit a cell, keeping colour codes intact.</summary>
    private string Clip(string text, float width)
    {
        if (_small.Measure(text) <= width) return text;
        string t = text;
        while (t.Length > 4 && _small.Measure(t + "...") > width)
        {
            int cut = t.Length - 1;
            if (t.EndsWith("{/}")) { t = t[..^3]; cut = t.Length - 1; t = t[..cut] + "{/}"; continue; }
            t = t[..cut];
        }
        return t + "...";
    }

    private void DrawAuctionSell(float x, float y, float w, PlayerStats? st)
    {
        _small.Draw(_batch, "{#9A9080}Pick an item from your bag, set a buyout price and post it. Unsold items come back after 48 hours.{/}", x + 24, y, Color.White);
        y += 40;
        float listX = x + 24;
        int i = 0;
        foreach (var it in (st?.Inventory ?? []).Take(16))
        {
            float ry = y + (i % 16) * 32;
            bool sel = _selected == it.Id;
            _batch.FillRect(listX, ry, 640, 30, sel ? Color.FromHex("#4A3C26") : i % 2 == 0 ? Color.FromHex("#221D16") : Color.FromHex("#2A241B"));
            long id = it.Id;
            _buttons.Add((listX, ry, 640, 30, () => { _selected = id; _ahPrice.Text = Math.Max(1, it.Value).ToString(); }));
            _small.Draw(_batch, $"{RarityName(it)}  {{#7A7060}}i{it.ItemLevel} · merchant pays {Math.Max(1, it.Value / 4):N0}{{/}}", listX + 8, ry + 3, Color.White, shadow: false);
            i++;
        }
        var item = st?.Inventory.FirstOrDefault(it => it.Id == _selected);
        float fx = x + 700;
        if (item is null) { _small.Draw(_batch, "{#7A7060}Choose an item on the left.{/}", fx, y, Color.White); return; }
        _small.Draw(_batch, RarityName(item), fx, y, Color.White);
        _small.Draw(_batch, $"{{#9A9080}}{ItemDetail(item)}{{/}}", fx, y + 28, Color.White);
        _small.Draw(_batch, PropsLine(item), fx, y + 56, Color.FromHex("#9FC6FF"));
        _small.Draw(_batch, "Buyout price", fx, y + 104, Color.FromHex("#9A9080"));
        TextField(_ahPrice, fx + 140, y + 100, 180, "gold");
        int price = int.TryParse(_ahPrice.Text, out var pv) ? pv : 0;
        int fee = Math.Max(1, price * 5 / 100);
        _small.Draw(_batch, $"{{#7A7060}}fee {fee:N0} gold now · you receive {price - price / 10:N0} if it sells{{/}}", fx, y + 140, Color.White);
        string sid = item.Id.ToString();
        Button(fx, y + 180, 260, 36, $"Post for {price:N0} gold", () => { _conn.Command("ah.post", sid, _ahPrice.Text); _selected = null; },
            enabled: price > 0 && (st?.Gold ?? 0) >= fee);
    }

    // ------------------------------------------------------------------ game menu and settings

    private int _fpsFrames, _fpsShown;
    private double _fpsSince;

    private void DrawFps()
    {
        _fpsFrames++;
        if (Now - _fpsSince >= 1) { _fpsShown = _fpsFrames; _fpsFrames = 0; _fpsSince = Now; }
        if (_settings.ShowFps) _small.DrawCentered(_batch, $"{{#9A9080}}{_fpsShown} fps · {_conn.Ping} ms{{/}}", VW / 2, 8, Color.White);
    }

    /// <summary>(Re)builds the text fonts at the chosen text size.</summary>
    private void BakeFonts()
    {
        float k = 1 + 0.12f * _settings.TextSize;
        _title?.Dispose(); _ui?.Dispose(); _small?.Dispose();
        _title = new BitmapFont(_gl, $"__font_title{_settings.TextSize}", Path.Combine(_fontRoot, "fonts", "Cinzel.ttf"), 34 * k);
        _ui = new BitmapFont(_gl, $"__font_ui{_settings.TextSize}", Path.Combine(_fontRoot, "fonts", "EBGaramond.ttf"), 26 * k);
        _small = new BitmapFont(_gl, $"__font_small{_settings.TextSize}", Path.Combine(_fontRoot, "fonts", "EBGaramond.ttf"), 20 * k, _settings.TextSize == 0 ? 512 : 1024);
    }

    private void SetFullscreen(bool on)
    {
        _settings.Fullscreen = on;
        _window.WindowState = on ? Silk.NET.Windowing.WindowState.Fullscreen : Silk.NET.Windowing.WindowState.Normal;
        _settings.Save(_settingsPath);
    }

    private void DrawMenu()
    {
        const float w = 640, h = 734, x = (VW - w) / 2, y0 = 150;
        WinFrame(x, y0, w, h, "TEMPLAR KNIGHTS");
        float y = y0 + 80;
        _ui.Draw(_batch, "Settings", x + 32, y, Color.FromHex("#C9A227"));
        y += 48;
        void Row(string label, string value, Action change, string hint = "")
        {
            _small.Draw(_batch, label, x + 40, y + 4, Color.FromHex("#E8E4DA"));
            if (hint.Length > 0) _small.Draw(_batch, $"{{#7A7060}}{hint}{{/}}", x + 230, y + 4, Color.White);
            Button(x + w - 200, y, 160, 30, value, () => { change(); _settings.Save(_settingsPath); }, tone: "plain");
            y += 44;
        }
        Row("Fullscreen", _settings.Fullscreen ? "On" : "Off", () => SetFullscreen(!_settings.Fullscreen), "F11 anywhere");
        Row("Vertical sync", _settings.VSync ? "On" : "Off", () => { _settings.VSync = !_settings.VSync; _window.VSync = _settings.VSync; });
        Row("Text size", ClientSettings.TextSizeNames[_settings.TextSize], () => { _settings.TextSize = (_settings.TextSize + 1) % ClientSettings.TextSizeNames.Length; BakeFonts(); });
        Row("Combat text", _settings.DamageNumbers ? "On" : "Off", () => _settings.DamageNumbers = !_settings.DamageNumbers, "damage, heals, blocks");
        Row("Minimap", _settings.Minimap ? "On" : "Off", () => _settings.Minimap = !_settings.Minimap, "Tab still opens the map");
        Row("Frame rate", _settings.ShowFps ? "Shown" : "Hidden", () => _settings.ShowFps = !_settings.ShowFps, "and ping");
        Row("Lighting and effects", _settings.Effects ? "On" : "Off", () => _settings.Effects = !_settings.Effects, "lights, glow, particles, shake");
        Row("Hints", _settings.Hints ? "On" : "Off", () => { _settings.Hints = !_settings.Hints; if (_settings.Hints) _settings.SeenHints.Clear(); }, "turning on shows them all again");
        Row("Music", $"{_settings.MusicVolume * 100:F0}%", () => _settings.MusicVolume = _settings.MusicVolume >= 0.99f ? 0 : MathF.Round(_settings.MusicVolume + 0.2f, 1), _audio?.Enabled == false ? "no sound device found" : "");
        Row("Sound effects", $"{_settings.EffectsVolume * 100:F0}%", () => _settings.EffectsVolume = _settings.EffectsVolume >= 0.99f ? 0 : MathF.Round(_settings.EffectsVolume + 0.2f, 1));
        _small.Draw(_batch, "{#6A6050}Saved on this PC. A controller works too: see the README.{/}", x + 40, y + 10, Color.White);
        Button(x + 32, y0 + h - 70, 180, 44, "Return", CloseWindow);
        Button(x + 230, y0 + h - 70, 180, 44, "Characters", () => { CloseWindow(); _conn.Command("logout"); }, tone: "plain");
        Button(x + w - 212, y0 + h - 70, 180, 44, "Leave game", () => _window.Close(), tone: "red");
    }
}
