# Town economy

Everything is server-authoritative: the client only asks, the server checks you are standing next to the right NPC, then answers with the window's new state.

## Merchants
- **Eleanor of Troyes** sells 12 items at your level (new stock every 15 minutes), buys anything from your bag for a quarter of its value, and gambles: pick a slot, pay 150 + 30 x your level, receive an unseen item of Uncommon rarity or better.
- **Sister Ysabeau** sells health and mana potions, 30 gold each, carry up to 20 of each. Z drinks health, X drinks mana.

## The Forge (Master Aubry, after the Smith's Hammer quest)
Put two items from your bag on Anvil A and Anvil B and choose whose type to keep. The result has the average item level, the average rarity rounded up, and every magical property of both; a property both items have is averaged. The window previews the result before you strike the anvil.

## Stashes
- **The Order's Strongbox** in the Sanctuary: 60 slots, yours alone.
- **The Guild Strongbox** in your Commandery hall: 100 slots, shared by every member.
- The bag holds 40 items.

## Trade
`/trade name` while in the same place, the other player types `/accept`. Both sides offer items and gold. Any change clears both acceptances; when both accept, everything moves at once (the server checks bag space and gold first).

## Auction house (Jehan the Broker)
- Buyout only. Posting costs 5% of the price up front; the house keeps 10% of each sale. Listings run 48 hours, 20 per seller.
- The Browse tab is a table: Item, Lvl, Rarity, Slot, Properties, Seller, Ends in, Price, and a Buy button. Click a column title to sort (click again to reverse). Filter by text (item or seller), slot, minimum rarity and level range; 15 rows per page.
- My listings shows your own, with Cancel. Sell lists your bag; pick an item, set the price, post.
- Gold from sales goes straight to an online seller; an offline seller collects it, and any expired items, on next login.

## Guilds (Commanderies)
- `/guild create name` costs 2,000 gold and makes you Grand Master. Ranks: Grand Master, Preceptor, Brother.
- Grand Masters and Preceptors invite (`/guild invite name`); `/guild leave` leaves. `/g text` is guild chat. G opens the roster.
- Members enter the Commandery Halls through the north door of the Sanctuary; each guild has its own hall with the Steward and the Guild Strongbox.

## Testing
`run_bots.bat --economy-seed <saves folder>` writes two test characters (add `--market` to fill the auction house with 40 listings), then start the server on that folder and run `run_bots.bat --economy-test`. It checks 31 things end to end: buying, selling, gambling, potions, forging, the stash, posting, searching, sorting, buying and cancelling auctions, a full trade, founding and joining a guild, entering the hall and using the guild strongbox.
