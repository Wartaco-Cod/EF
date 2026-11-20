# Hosting the Templar Knights server

The game server runs on your own machine. Players connect over **UDP port 7777**. If you also hand out updates
through the launcher, it serves them on **TCP port 7778**.

## Run it

```
run_server.bat [options]                                     (Windows, from the source tree)
TacoFactory.Server.exe [options]                             (Windows, from release/TemplarKnightsServer-win-x64)
./TacoFactory.Server [options]                               (Linux, from release/TemplarKnightsServer-linux-x64)
```

| Option | What it does |
| --- | --- |
| `--open` | Anyone may make an account (playtests). Without it, a purchase key is needed to sign up |
| `--make-keys 10` | Prints 10 new purchase keys, saves them in `saves/keys.json` and exits |
| `--name "My Server"` | The name shown to players |
| `--updates <folder>` | Serves that folder (a released game, `release/TemplarKnights`) to players' launchers |
| `--update-port 7778` | TCP port for updates (default: game port + 1) |
| `--port 7777` | UDP port for the game |
| `--saves <folder>` | Where characters, accounts, guilds and the auction house live (default `saves`) |

Testing only: `--start-zone <zone>`, `--start-level <n>`, `--exit-on-clear`, `--exit-on-unique <id>`, `--ticks <n>`, `--seed <n>`.

## Accounts and purchase keys

- Templar Knights is a one-time purchase: each purchase key makes one account. Make keys with `--make-keys`, or type `keys 10` in the server window, and give one to each buyer.
- An account holds up to 8 characters. Passwords are stored only as salted PBKDF2 hashes (`saves/accounts.json`).
- Logins are encrypted: the server has its own key (`saves/server_key.pem`, made on first start). Players' games remember it the first time they connect and refuse a server whose key changes, so **back that file up and keep it**: if you lose it, every player must delete the server's line from `%APPDATA%\TemplarKnights\known_servers.json`.
- **The first account made on a server is its administrator.** `admin <account>` in the server window makes more (`admin <account> off` removes one).
- Characters made before accounts existed are claimed by an account with the same name (spaces become `_`).

## Moderation

Type these in the server window, or in game chat with a `/` if you are an administrator:

| Command | |
| --- | --- |
| `who` | Who is online, where |
| `announce <text>` | Message everyone |
| `kick <name> [reason]` | Disconnect a character (or account) |
| `ban <name> [reason]` / `unban <name>` | Ban the account behind a character or account name |
| `mute <name> [minutes]` / `unmute <name>` | Stop someone talking (default 30 minutes) |
| `keys <count>` | New purchase keys |
| `admin <account> [off]` | Grant or remove administrator |
| `save` | Save every online character now |
| `shutdown` | Save and stop |

The server also limits how fast each client may send (inputs, requests and chat) and drops anyone flooding it,
locks an account out for a minute after five wrong passwords, and disconnects connections that never sign in.

## Open the ports

- **Windows firewall:**
  `netsh advfirewall firewall add rule name="Templar Knights" dir=in action=allow protocol=UDP localport=7777`
  `netsh advfirewall firewall add rule name="Templar Knights updates" dir=in action=allow protocol=TCP localport=7778`
- **Router:** forward UDP 7777 (and TCP 7778 for updates) to the server machine.
- Players use the launcher (`TemplarKnightsLauncher.exe --server <your address>` once) or `run_client.bat --host <your address>`.

## Updates

1. `release.bat 0.3.1` builds `release/TemplarKnights` (see `docs/RELEASE.md`).
2. Copy that folder to the server machine and start the server with `--updates <that folder>`.
3. Players' launchers compare every file's SHA-256 with yours and download only what changed. Copy in a new build at any time; the server notices within seconds.

Updating the game protocol (a new server and client together) means every player needs the new build before they can connect, which the launcher handles.

## Keep it running

- **Linux:** run it as a systemd service with `Restart=always` (and `StandardInput=null`; type moderation commands through an admin account in game instead).
- **Windows:** run it from Task Scheduler at startup ("Run whether user is logged on or not"), or wrap it with NSSM as a service.

## Back up

Everything lives in the `saves` folder, written atomically: one JSON file per character, `accounts.json`, `keys.json`, `server_key.pem`, `guilds.json`, `auctions.json` and `duel_rewards.json`. Deleted characters are moved to `saves/deleted`. Back the folder up nightly; restoring it restores everything.

## How the world is hosted

- One server process holds everyone: a shared town per act and a private copy of each field or dungeon per party. Up to 120 players per process.
- A party's copy of a zone closes 2 minutes after its last member leaves; monsters are scaled to the party size when the copy opens.
- Characters save on leaving, on every zone clear and every minute.
- Each guild gets its own copy of the Commandery Halls; only members can enter.
