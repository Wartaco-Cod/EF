using LiteNetLib;
using TacoFactory.Core.Data;
using TacoFactory.Core.Math;
using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Server;

/// <summary>Equipment, merchants, stash, Forge, trades, the auction house and guilds.</summary>
public sealed partial class GameServer
{
    private void SendUi<T>(Session s, string kind, T state)
    {
        Msg.Ui(_w, kind, UiKind.Serialize(state));
        s.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Window actions only work while standing near the NPC who provides them.</summary>
    private NpcDef? NearService(Session s, string service)
    {
        if (s.Entity is not { } p || s.Instance is null) return null;
        foreach (var e in s.Instance.World.Entities)
        {
            if (e.Kind != EntityKind.Npc) continue;
            var npc = s.Instance.Zone.Npcs[e.TypeIndex];
            if (npc.Services.Contains(service) && Vec2.Distance(e.Pos, p.Pos) <= NpcRadius * 1.6f) return npc;
        }
        return null;
    }

    private static bool TryId(string[] a, int i, out long id) { id = 0; return a.Length > i && long.TryParse(a[i], out id); }
    private static int IntArg(string[] a, int i, int fallback = 0) => a.Length > i && int.TryParse(a[i], out var v) ? v : fallback;

    private void HandleCommand(Session s, string verb, string[] a)
    {
        if (s.Entity is not { } p) return;
        var world = s.Instance!.World;
        string? error = null;
        if (EndgameCommand(s, p, verb, a, out var endgameError))
        {
            if (endgameError is not null) Tell(s, endgameError);
            SendStats(s);
            SendExtras(s);
            return;
        }
        switch (verb)
        {
            // ---- equipment and potions (anywhere)
            case "equip":
                error = TryId(a, 0, out var eid)
                    ? world.Equip(p, eid, Enum.TryParse<ItemSlot>(a.ElementAtOrDefault(1), true, out var into) ? into : null)
                    : "Which item?";
                break;
            case "unequip":
                error = a.Length > 0 && Enum.TryParse<ItemSlot>(a[0], true, out var slot) ? world.Unequip(p, slot) : "Which slot?";
                break;
            case "drink":
                error = world.Drink(p, a.FirstOrDefault() != "mana");
                break;
            case "discard":
                if (TryId(a, 0, out var did) && world.FindInventory(p, did) is { } junk) { Reclaim(s, p, junk); p.Inventory.Remove(junk); Tell(s, $"Dropped {junk.DisplayName}."); }
                break;

            // ---- merchants
            case "buy": error = Buy(s, p, a); break;
            case "sell": error = Sell(s, p, a); break;
            case "potion": error = BuyPotion(s, p, a); break;
            case "gamble": error = Gamble(s, p, a); break;

            // ---- stashes
            case "stash.put": error = MoveToStash(s, p, a, guild: false); break;
            case "stash.take": error = TakeFromStash(s, p, a, guild: false); break;
            case "gstash.put": error = MoveToStash(s, p, a, guild: true); break;
            case "gstash.take": error = TakeFromStash(s, p, a, guild: true); break;

            // ---- forge
            case "forge": error = DoForge(s, p, a); break;

            // ---- trade window
            case "trade.add": error = TradeItem(s, p, a, add: true); break;
            case "trade.remove": error = TradeItem(s, p, a, add: false); break;
            case "trade.gold": error = TradeGold(s, p, IntArg(a, 0)); break;
            case "trade.accept": error = TradeAccept(s); break;
            case "trade.cancel": CancelTrade(s, $"{s.Name} cancelled the trade."); break;

            // ---- auction house
            case "ah.search":
                if (NearService(s, "auction") is null) { error = "Talk to Jehan the Broker in town."; break; }
                SendAuctionPage(s, a.ElementAtOrDefault(0) ?? "",
                    Enum.TryParse<ItemSlot>(a.ElementAtOrDefault(1), true, out var fs) ? fs : null,
                    Enum.TryParse<Rarity>(a.ElementAtOrDefault(2), true, out var fr) ? fr : null,
                    IntArg(a, 3, 1), IntArg(a, 4, 100), a.ElementAtOrDefault(5) ?? "price", a.ElementAtOrDefault(6) == "1", IntArg(a, 7, 1));
                break;
            case "ah.post": error = PostAuction(s, p, a); break;
            case "ah.buy": error = BuyAuction(s, p, a); break;
            case "ah.cancel": error = CancelAuction(s, p, a); break;
            case "ah.mine": SendMyAuctions(s); break;

            // ---- guild
            case "guild.info": SendGuild(s); break;
            case "logout": LogOut(s); return;
            case "vote": CastVote(s, a.ElementAtOrDefault(0) ?? ""); break;
            default: error = $"Unknown request '{verb}'."; break;
        }
        if (error is not null) Tell(s, error);
        SendStats(s);
    }

    // ------------------------------------------------------------------ merchants

    private void OpenShop(Session s, NpcDef npc)
    {
        bool gear = npc.Services.Contains("buy");
        if (gear && Clock - s.ShopStockAt > 900) // new stock every 15 minutes
        {
            s.ShopStock = Merchant.Stock(Random.Shared, s.Entity?.Level ?? 1, s.Unlocks.Contains("eleanor_rare_stock"));
            s.ShopStockAt = Clock;
        }
        var level = s.Entity?.Level ?? 1;
        SendUi(s, UiKind.Shop, new ShopState(npc.Name,
            gear ? s.ShopStock.Select(i => new ShopEntry(Msg.Info(i), Loot.Value(i))).ToList() : [],
            npc.Services.Contains("potions"), Merchant.HealthPotionPrice, Merchant.ManaPotionPrice,
            npc.Services.Contains("gamble"), Merchant.GamblePrice(level), npc.Services.Contains("sell")));
    }

    private NpcDef? Merchant_(Session s, string service) => NearService(s, service);

    private string? Buy(Session s, Entity p, string[] a)
    {
        if (Merchant_(s, "buy") is not { } npc) return "Talk to Eleanor in town.";
        if (!TryId(a, 0, out var id) || s.ShopStock.FirstOrDefault(i => i.Id == id) is not { } item) return "That's no longer for sale.";
        int price = Loot.Value(item);
        if (p.Gold < price) return $"You need {price} gold.";
        if (p.Inventory.Count >= Gear.BagLimit(p)) return "Your bag is full.";
        p.Gold -= price;
        s.ShopStock.Remove(item);
        p.Inventory.Add(item);
        Tell(s, $"Bought {item.DisplayName} for {price} gold.");
        OpenShop(s, npc);
        return null;
    }

    private string? Sell(Session s, Entity p, string[] a)
    {
        if (Merchant_(s, "sell") is not { } npc) return "Talk to Eleanor in town.";
        if (!TryId(a, 0, out var id) || s.Instance!.World.FindInventory(p, id) is not { } item) return "That item isn't in your bag.";
        int price = Loot.SellPrice(item);
        Reclaim(s, p, item);
        p.Inventory.Remove(item);
        p.Gold += price;
        Tell(s, $"Sold {item.DisplayName} for {price} gold.");
        OpenShop(s, npc);
        return null;
    }

    private string? BuyPotion(Session s, Entity p, string[] a)
    {
        if (Merchant_(s, "potions") is null) return "Sister Ysabeau sells potions.";
        bool health = a.FirstOrDefault() != "mana";
        int count = System.Math.Clamp(IntArg(a, 1, 1), 1, Merchant.MaxPotions);
        int have = health ? p.HealthPotions : p.ManaPotions;
        count = System.Math.Min(count, Merchant.MaxPotions - have);
        if (count <= 0) return $"You can carry {Merchant.MaxPotions} of each potion.";
        int price = count * (health ? Merchant.HealthPotionPrice : Merchant.ManaPotionPrice);
        if (p.Gold < price) return $"You need {price} gold.";
        p.Gold -= price;
        if (health) p.HealthPotions += count; else p.ManaPotions += count;
        Tell(s, $"Bought {count} {(health ? "health" : "mana")} potion{(count == 1 ? "" : "s")} for {price} gold.");
        return null;
    }

    private string? Gamble(Session s, Entity p, string[] a)
    {
        if (Merchant_(s, "gamble") is not { } npc) return "Eleanor runs the gambling table.";
        if (!Enum.TryParse<ItemSlot>(a.FirstOrDefault(), true, out var slot)) return "Gamble on which kind of item?";
        int price = Merchant.GamblePrice(p.Level);
        if (p.Gold < price) return $"You need {price} gold.";
        if (p.Inventory.Count >= Gear.BagLimit(p)) return "Your bag is full.";
        p.Gold -= price;
        var item = Merchant.Gamble(Random.Shared, p.Level, slot);
        p.Inventory.Add(item);
        Tell(s, $"You gamble {price} gold and receive: {item.Rarity} {item.DisplayName}.");
        OpenShop(s, npc);
        return null;
    }

    // ------------------------------------------------------------------ stash

    private void SendStash(Session s) =>
        SendUi(s, UiKind.Stash, new StashState("The Order's Strongbox", s.Stash.Select(Msg.Info).ToList(), Loot.StashLimit, false));

    private void SendGuildStash(Session s)
    {
        if (s.Guild is null || !_guilds.Guilds.TryGetValue(s.Guild, out var g)) { Tell(s, "You are not in a guild."); return; }
        SendUi(s, UiKind.Stash, new StashState($"{g.Name} strongbox", g.Stash.Select(Msg.Info).ToList(), Loot.GuildStashLimit, true));
    }

    private string? MoveToStash(Session s, Entity p, string[] a, bool guild)
    {
        if (NearService(s, guild ? "guild_stash" : "stash") is null) return guild ? "Use the guild strongbox in your hall." : "Use the strongbox in town.";
        if (!TryId(a, 0, out var id) || s.Instance!.World.FindInventory(p, id) is not { } item) return "That item isn't in your bag.";
        var target = guild ? _guilds.Guilds[s.Guild!].Stash : s.Stash;
        if (target.Count >= (guild ? Loot.GuildStashLimit : Loot.StashLimit)) return "The strongbox is full.";
        p.Inventory.Remove(item);
        target.Add(item);
        if (guild) { _guilds.Save(); SendGuildStash(s); } else SendStash(s);
        return null;
    }

    private string? TakeFromStash(Session s, Entity p, string[] a, bool guild)
    {
        if (NearService(s, guild ? "guild_stash" : "stash") is null) return guild ? "Use the guild strongbox in your hall." : "Use the strongbox in town.";
        var source = guild ? _guilds.Guilds[s.Guild!].Stash : s.Stash;
        if (!TryId(a, 0, out var id) || source.FirstOrDefault(i => i.Id == id) is not { } item) return "That item isn't in the strongbox.";
        if (p.Inventory.Count >= Gear.BagLimit(p)) return "Your bag is full.";
        source.Remove(item);
        p.Inventory.Add(item);
        if (guild) { _guilds.Save(); SendGuildStash(s); } else SendStash(s);
        return null;
    }

    // ------------------------------------------------------------------ forge

    private string? DoForge(Session s, Entity p, string[] a)
    {
        if (AtForge(s) is string where) return where;
        var world = s.Instance!.World;
        if (!TryId(a, 0, out var ia) || !TryId(a, 1, out var ib)) return "Put two items on the anvil.";
        if (world.FindInventory(p, ia) is not { } first || world.FindInventory(p, ib) is not { } second) return "Both items must be in your bag.";
        if (Forge.CanCombine(first, second) is string why) return why;
        var result = Forge.Combine(first, second, keepFirstType: a.ElementAtOrDefault(2) != "0");
        Reclaim(s, p, first);
        Reclaim(s, p, second);
        p.Inventory.Remove(first);
        p.Inventory.Remove(second);
        p.Inventory.Add(result);
        Fx(s, "combine", result.DisplayName, $"{result.Rarity}, level {result.ItemLevel}", result, result.Rarity);
        Tell(s, $"{{#F2D25A}}The forge rings.{{/}} {result.Rarity} {result.DisplayName}, level {result.ItemLevel}: " +
                string.Join(", ", result.Properties.Select(kv => Loot.DescribeProperty(kv.Key, kv.Value))));
        return null;
    }

    // ------------------------------------------------------------------ trade

    private void RequestTrade(Session s, string name)
    {
        if (Online(name) is not { } who || who == s) { Tell(s, $"No one called '{name}' is online."); return; }
        if (who.Instance != s.Instance) { Tell(s, "You must be in the same place to trade."); return; }
        if (_trades.ContainsKey(s) || _trades.ContainsKey(who)) { Tell(s, "One of you is already trading."); return; }
        who.Pending = (s.Name, "trade", Clock);
        Tell(who, $"{s.Name} wants to trade. Type /accept or /decline.");
        Tell(s, $"Trade request sent to {who.Name}.");
    }

    private void OpenTrade(Session a, Session b)
    {
        if (a.Instance != b.Instance) { Tell(b, "You must be in the same place to trade."); return; }
        if (_trades.ContainsKey(a) || _trades.ContainsKey(b)) { Tell(b, "One of you is already trading."); return; }
        var t = new Trade { A = a, B = b };
        _trades[a] = t;
        _trades[b] = t;
        PushTrade(t);
    }

    private void PushTrade(Trade t)
    {
        foreach (var me in new[] { t.A, t.B })
        {
            var other = t.Other(me);
            SendUi(me, UiKind.Trade, new TradeState(other.Name,
                Offered(me, t.ItemsOf(me)), me == t.A ? t.GoldA : t.GoldB, me == t.A ? t.AcceptA : t.AcceptB,
                Offered(other, t.ItemsOf(other)), other == t.A ? t.GoldA : t.GoldB, other == t.A ? t.AcceptA : t.AcceptB));
        }
    }

    private static List<ItemInfo> Offered(Session s, List<long> ids) =>
        s.Entity is { } p ? p.Inventory.Where(i => ids.Contains(i.Id)).Select(Msg.Info).ToList() : [];

    private string? TradeItem(Session s, Entity p, string[] a, bool add)
    {
        if (!_trades.TryGetValue(s, out var t)) return "You aren't trading.";
        if (!TryId(a, 0, out var id)) return "Which item?";
        var list = t.ItemsOf(s);
        if (add)
        {
            if (s.Instance!.World.FindInventory(p, id) is null) return "That item isn't in your bag.";
            if (list.Count >= 12) return "You can offer up to 12 items at once.";
            if (!list.Contains(id)) list.Add(id);
        }
        else list.Remove(id);
        t.ResetAccept();
        PushTrade(t);
        return null;
    }

    private string? TradeGold(Session s, Entity p, int gold)
    {
        if (!_trades.TryGetValue(s, out var t)) return "You aren't trading.";
        gold = System.Math.Clamp(gold, 0, p.Gold);
        if (s == t.A) t.GoldA = gold; else t.GoldB = gold;
        t.ResetAccept();
        PushTrade(t);
        return null;
    }

    private string? TradeAccept(Session s)
    {
        if (!_trades.TryGetValue(s, out var t)) return "You aren't trading.";
        if (s == t.A) t.AcceptA = true; else t.AcceptB = true;
        if (!(t.AcceptA && t.AcceptB)) { PushTrade(t); return null; }

        // Both accepted: check everything is still there and fits, then swap in one go.
        if (t.A.Entity is not { } pa || t.B.Entity is not { } pb) { CancelTrade(s, "Trade failed."); return null; }
        var giveA = pa.Inventory.Where(i => t.ItemsA.Contains(i.Id)).ToList();
        var giveB = pb.Inventory.Where(i => t.ItemsB.Contains(i.Id)).ToList();
        string? problem =
            giveA.Count != t.ItemsA.Count || giveB.Count != t.ItemsB.Count ? "An offered item is gone." :
            pa.Gold < t.GoldA || pb.Gold < t.GoldB ? "Not enough gold." :
            pa.Inventory.Count - giveA.Count + giveB.Count > Gear.BagLimit(pa) ? $"{t.A.Name}'s bag is too full." :
            pb.Inventory.Count - giveB.Count + giveA.Count > Gear.BagLimit(pb) ? $"{t.B.Name}'s bag is too full." : null;
        if (problem is not null) { t.ResetAccept(); PushTrade(t); Tell(t.A, problem); Tell(t.B, problem); return null; }

        foreach (var i in giveA) { pa.Inventory.Remove(i); pb.Inventory.Add(i); }
        foreach (var i in giveB) { pb.Inventory.Remove(i); pa.Inventory.Add(i); }
        pa.Gold += t.GoldB - t.GoldA;
        pb.Gold += t.GoldA - t.GoldB;
        _trades.Remove(t.A);
        _trades.Remove(t.B);
        foreach (var x in new[] { t.A, t.B })
        {
            SendUi(x, UiKind.Close, UiKind.Trade);
            Tell(x, "{#6BD66B}Trade complete.{/}");
            _store.Save(x.Record = Snapshot(x, x.Entity!));
            SendStats(x);
        }
        Log($"trade: {t.A.Name} gave {giveA.Count} items + {t.GoldA}g, {t.B.Name} gave {giveB.Count} items + {t.GoldB}g");
        return null;
    }

    private void CancelTrade(Session s, string reason)
    {
        if (!_trades.TryGetValue(s, out var t)) return;
        _trades.Remove(t.A);
        _trades.Remove(t.B);
        foreach (var x in new[] { t.A, t.B })
        {
            SendUi(x, UiKind.Close, UiKind.Trade);
            if (_sessions.ContainsValue(x)) Tell(x, reason);
        }
    }

    // ------------------------------------------------------------------ auction house

    private void SendAuctionPage(Session s, string text, ItemSlot? slot, Rarity? rarity, int minLevel, int maxLevel, string sort, bool desc, int page)
    {
        const int perPage = 15;
        var all = _auctions.Search(text.Trim(), slot, rarity, minLevel, maxLevel, sort, desc).ToList();
        int pages = System.Math.Max(1, (all.Count + perPage - 1) / perPage);
        page = System.Math.Clamp(page, 1, pages);
        var rows = all.Skip((page - 1) * perPage).Take(perPage)
            .Select(l => new AuctionRow(l.Id, Msg.Info(l.Item), l.Seller, l.Price, new DateTimeOffset(l.ExpiresUtc).ToUnixTimeSeconds(),
                string.Equals(l.Seller, s.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        SendUi(s, UiKind.Auction, new AuctionPage(rows, all.Count, page, pages, sort, desc, AuctionHouse.PostingFeePercent, AuctionHouse.SaleCutPercent));
    }

    private void SendMyAuctions(Session s)
    {
        var mine = _auctions.Listings.Where(l => string.Equals(l.Seller, s.Name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(l => l.ExpiresUtc)
            .Select(l => new AuctionRow(l.Id, Msg.Info(l.Item), l.Seller, l.Price, new DateTimeOffset(l.ExpiresUtc).ToUnixTimeSeconds(), true))
            .ToList();
        SendUi(s, UiKind.AuctionMine, new AuctionPage(mine, mine.Count, 1, 1, "ends", false, AuctionHouse.PostingFeePercent, AuctionHouse.SaleCutPercent));
    }

    private string? PostAuction(Session s, Entity p, string[] a)
    {
        if (NearService(s, "auction") is null) return "Talk to Jehan the Broker in town.";
        if (!TryId(a, 0, out var id) || s.Instance!.World.FindInventory(p, id) is not { } item) return "That item isn't in your bag.";
        int price = IntArg(a, 1);
        if (price < 1 || price > AuctionHouse.MaxPrice) return $"Set a price between 1 and {AuctionHouse.MaxPrice:N0} gold.";
        if (_auctions.Listings.Count(l => string.Equals(l.Seller, s.Name, StringComparison.OrdinalIgnoreCase)) >= AuctionHouse.MaxListingsPerSeller)
            return $"You can have {AuctionHouse.MaxListingsPerSeller} listings at once.";
        int fee = AuctionHouse.PostingFee(price);
        if (p.Gold < fee) return $"Posting costs {fee} gold ({AuctionHouse.PostingFeePercent}%).";
        p.Gold -= fee;
        p.Inventory.Remove(item);
        var now = DateTime.UtcNow;
        _auctions.Listings.Add(new Listing { Id = Loot.NewId(), Seller = s.Name, Item = item, Price = price, PostedUtc = now, ExpiresUtc = now + AuctionHouse.Duration });
        _auctions.Save();
        _store.Save(s.Record = Snapshot(s, p)); // the item has left the character: save now
        Tell(s, $"Listed {item.DisplayName} for {price:N0} gold (fee {fee}). It runs for 48 hours.");
        SendMyAuctions(s);
        return null;
    }

    private string? BuyAuction(Session s, Entity p, string[] a)
    {
        if (NearService(s, "auction") is null) return "Talk to Jehan the Broker in town.";
        if (!TryId(a, 0, out var id) || _auctions.Listings.FirstOrDefault(l => l.Id == id) is not { } listing) return "That listing is gone.";
        if (string.Equals(listing.Seller, s.Name, StringComparison.OrdinalIgnoreCase)) return "That's your own listing.";
        if (p.Gold < listing.Price) return $"You need {listing.Price:N0} gold.";
        if (p.Inventory.Count >= Gear.BagLimit(p)) return "Your bag is full.";
        p.Gold -= listing.Price;
        p.Inventory.Add(listing.Item);
        _auctions.Listings.Remove(listing);
        int proceeds = listing.Price - listing.Price * AuctionHouse.SaleCutPercent / 100;
        _auctions.MailFor(listing.Seller).Gold += proceeds;
        _auctions.Save();
        _store.Save(s.Record = Snapshot(s, p));
        Tell(s, $"Bought {listing.Item.DisplayName} from {listing.Seller} for {listing.Price:N0} gold.");
        if (Online(listing.Seller) is { } seller) DeliverMail(seller);
        Log($"auction: {s.Name} bought {listing.Item.DisplayName} from {listing.Seller} for {listing.Price}");
        SendAuctionPage(s, "", null, null, 1, 100, "price", false, 1);
        return null;
    }

    private string? CancelAuction(Session s, Entity p, string[] a)
    {
        if (NearService(s, "auction") is null) return "Talk to Jehan the Broker in town.";
        if (!TryId(a, 0, out var id) || _auctions.Listings.FirstOrDefault(l => l.Id == id) is not { } listing) return "That listing is gone.";
        if (!string.Equals(listing.Seller, s.Name, StringComparison.OrdinalIgnoreCase)) return "That isn't your listing.";
        _auctions.Listings.Remove(listing);
        _auctions.MailFor(s.Name).Items.Add(listing.Item);
        _auctions.Save();
        DeliverMail(s);
        SendMyAuctions(s);
        return null;
    }

    /// <summary>Pays out auction gold and returns unsold items: to the bag if there's room, else the stash, else it waits.</summary>
    private void DeliverMail(Session s)
    {
        if (s.Entity is not { } p || !_auctions.Mailbox.TryGetValue(s.Name, out var mail)) return;
        bool changed = false;
        if (mail.Gold > 0)
        {
            p.Gold += mail.Gold;
            Tell(s, $"{{#E6C14A}}Jehan the Broker sends you {mail.Gold:N0} gold from your sales.{{/}}");
            mail.Gold = 0;
            changed = true;
        }
        foreach (var item in mail.Items.ToList())
        {
            if (p.Inventory.Count < Gear.BagLimit(p)) p.Inventory.Add(item);
            else if (s.Stash.Count < Loot.StashLimit) s.Stash.Add(item);
            else continue;
            mail.Items.Remove(item);
            Tell(s, $"Returned from the auction house: {item.DisplayName}.");
            changed = true;
        }
        if (mail.Gold == 0 && mail.Items.Count == 0) _auctions.Mailbox.Remove(s.Name);
        if (changed) { _auctions.Save(); _store.Save(s.Record = Snapshot(s, p)); }
    }

    // ------------------------------------------------------------------ guilds

    private void SendGuild(Session s)
    {
        var g = s.Guild is null ? null : _guilds.Guilds.GetValueOrDefault(s.Guild);
        var members = g?.Members.Select(m =>
        {
            var online = Online(m);
            var rec = online?.Record ?? _store.Load(m);
            return new GuildMemberInfo(m, rec?.Class ?? ClassId.Knight, online?.Entity?.Level ?? rec?.Level ?? 1, online is not null, g.RankOf(m));
        }).OrderByDescending(m => m.Online).ThenBy(m => m.Name).ToList() ?? [];
        SendUi(s, UiKind.Guild, new GuildState(g?.Name, g?.Motto, members, g?.Stash.Count ?? 0, GuildRegistry.CreateCost,
            g is not null && string.Equals(g.Leader, s.Name, StringComparison.OrdinalIgnoreCase)));
    }

    private void GuildCommand(Session s, string arg)
    {
        var parts = arg.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "", rest = parts.ElementAtOrDefault(1) ?? "";
        var g = s.Guild is null ? null : _guilds.Guilds.GetValueOrDefault(s.Guild);
        bool leader = g is not null && string.Equals(g.Leader, s.Name, StringComparison.OrdinalIgnoreCase);
        bool officer = leader || (g?.Officers.Contains(s.Name, StringComparer.OrdinalIgnoreCase) ?? false);
        switch (sub)
        {
            case "create":
                if (g is not null) { Tell(s, $"You already belong to {g.Name}."); return; }
                if (!GuildRegistry.IsValidName(rest)) { Tell(s, "Guild names are 3-24 letters, spaces, ' or -."); return; }
                if (_guilds.Guilds.ContainsKey(rest)) { Tell(s, "That name is taken."); return; }
                if (s.Entity is not { } p || p.Gold < GuildRegistry.CreateCost) { Tell(s, $"Founding a Commandery costs {GuildRegistry.CreateCost:N0} gold."); return; }
                p.Gold -= GuildRegistry.CreateCost;
                _guilds.Guilds[rest] = new Guild { Name = rest, Leader = s.Name, Members = [s.Name], CreatedUtc = DateTime.UtcNow };
                _guilds.Save();
                s.Guild = rest;
                Tell(s, $"{{#F2D25A}}You found the Commandery of {rest}.{{/}} Your hall lies through the north door of the Sanctuary.");
                break;
            case "invite":
                if (!officer) { Tell(s, "Only the Grand Master and Preceptors can invite."); return; }
                if (Online(rest) is not { } who) { Tell(s, $"No one called '{rest}' is online."); return; }
                if (who.Guild is not null) { Tell(s, $"{who.Name} already belongs to {who.Guild}."); return; }
                if (g!.Members.Count >= GuildRegistry.MaxMembers) { Tell(s, "Your guild is full."); return; }
                who.Pending = (s.Name, "guild", Clock);
                Tell(who, $"{s.Name} invites you to join {g.Name}. Type /accept or /decline.");
                Tell(s, $"Invitation sent to {who.Name}.");
                break;
            case "leave":
                if (g is null) { Tell(s, "You are not in a guild."); return; }
                if (leader && g.Members.Count > 1) { Tell(s, "Promote a new Grand Master first: /guild leader name"); return; }
                g.Members.RemoveAll(m => string.Equals(m, s.Name, StringComparison.OrdinalIgnoreCase));
                g.Officers.RemoveAll(m => string.Equals(m, s.Name, StringComparison.OrdinalIgnoreCase));
                if (g.Members.Count == 0) _guilds.Guilds.Remove(g.Name);
                _guilds.Save();
                GuildBroadcast(g, $"{s.Name} has left the Commandery.");
                s.Guild = null;
                Tell(s, $"You left {g.Name}.");
                break;
            case "kick":
                if (!officer || g is null) { Tell(s, "Only the Grand Master and Preceptors can remove members."); return; }
                var target = g.Members.FirstOrDefault(m => string.Equals(m, rest, StringComparison.OrdinalIgnoreCase));
                if (target is null || string.Equals(target, g.Leader, StringComparison.OrdinalIgnoreCase)) { Tell(s, "You can't remove them."); return; }
                g.Members.Remove(target);
                g.Officers.Remove(target);
                _guilds.Save();
                GuildBroadcast(g, $"{target} was removed from the Commandery.");
                if (Online(target) is { } kicked) { kicked.Guild = null; Tell(kicked, $"You were removed from {g.Name}."); }
                break;
            case "promote" or "demote":
                if (!leader) { Tell(s, "Only the Grand Master can do that."); return; }
                var m2 = g!.Members.FirstOrDefault(m => string.Equals(m, rest, StringComparison.OrdinalIgnoreCase));
                if (m2 is null) { Tell(s, "Not a member."); return; }
                if (sub == "promote") { if (!g.Officers.Contains(m2)) g.Officers.Add(m2); } else g.Officers.Remove(m2);
                _guilds.Save();
                GuildBroadcast(g, $"{m2} is now a {g.RankOf(m2)}.");
                break;
            case "leader":
                if (!leader) { Tell(s, "Only the Grand Master can do that."); return; }
                var heir = g!.Members.FirstOrDefault(m => string.Equals(m, rest, StringComparison.OrdinalIgnoreCase));
                if (heir is null) { Tell(s, "Not a member."); return; }
                g.Leader = heir;
                _guilds.Save();
                GuildBroadcast(g, $"{heir} is the new Grand Master.");
                break;
            case "motto":
                if (!leader) { Tell(s, "Only the Grand Master can do that."); return; }
                g!.Motto = new string(rest.Where(c => !char.IsControl(c) && c != '{' && c != '}').Take(80).ToArray());
                _guilds.Save();
                break;
            default:
                Tell(s, "/guild create name (2,000 gold), /guild invite name, /guild leave, /guild kick name, /guild promote name, /guild leader name, /guild motto text, /g message");
                return;
        }
        if (s.Guild is not null || sub == "leave") SendGuild(s);
    }

    private void JoinGuild(Session s, string inviter)
    {
        var g = _guilds.Of(inviter);
        if (g is null || s.Guild is not null) { Tell(s, "That invitation is no longer valid."); return; }
        g.Members.Add(s.Name);
        _guilds.Save();
        s.Guild = g.Name;
        GuildBroadcast(g, $"{s.Name} joins the Commandery of {g.Name}.");
        SendGuild(s);
    }

    private void GuildChat(Session s, string text)
    {
        if (s.Guild is null || !_guilds.Guilds.TryGetValue(s.Guild, out var g)) { Tell(s, "You are not in a guild."); return; }
        if (text.Length == 0) return;
        Msg.Chat(_w, new ChatLine("guild", s.Name, text));
        foreach (var m in _sessions.Values.Where(x => x.Guild == g.Name)) m.Peer.Send(_w, DeliveryMethod.ReliableOrdered);
    }

    private void GuildBroadcast(Guild g, string text)
    {
        foreach (var m in _sessions.Values.Where(x => x.Guild == g.Name)) Tell(m, $"{{#9FD68A}}[{g.Name}]{{/}} {text}");
    }
}
