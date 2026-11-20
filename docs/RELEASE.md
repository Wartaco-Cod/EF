# Making a release

```
release.bat 0.3.0          (or: python tools/release/make_release.py 0.3.0 [--no-zip])
```

This runs the banned-terms check, then builds into `release/`:

| Folder | For |
| --- | --- |
| `TemplarKnights/` | Players: the game (`TemplarKnights.exe`) and launcher (`TemplarKnightsLauncher.exe`), self-contained for Windows x64 (no .NET install needed), with all assets |
| `TemplarKnightsServer-win-x64/` | Your server on Windows |
| `TemplarKnightsServer-linux-x64/` | Your server on Linux |
| `*.zip` | The same, zipped |

## Installer

Open `installer/TemplarKnights.iss` in [Inno Setup](https://jrsoftware.org/isinfo.php) on Windows and press
Compile: it makes `release/TemplarKnightsSetup-<version>.exe`. The setup asks for the server's address, writes
it to `launcher.json`, and puts shortcuts to the launcher on the desktop and Start menu. It installs per user
and needs no administrator rights.

## Updates

Start the server with `--updates <path to release/TemplarKnights>`. The launcher fetches
`http://<server>:7778/manifest.json`, downloads only files whose SHA-256 differs (each checked again after
download), then starts the game connected to that server. A new launcher is swapped in on the next start.
Build a new version, copy it over the folder the server serves, and every player gets it the next time they
start the launcher.

## Before a public release

- Art is rendered by `tools/art/blender/` (see README). Final sound replaces the placeholders under the same file names.
- Sign the executables (an unsigned .exe from the internet triggers Windows SmartScreen warnings).
- Playtest with people; bots die less and never get lost.
