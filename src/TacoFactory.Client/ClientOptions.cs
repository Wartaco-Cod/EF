using TacoFactory.Core.Sim;
using TacoFactory.Net;

namespace TacoFactory.Client;

/// <summary>Command-line options. --frames, --screenshot and --autoplay exist for automated smoke tests and demos.</summary>
public sealed record ClientOptions(
    int Width, int Height, bool VSync, int? ExitAfterFrames, string? ScreenshotPath,
    string Host, int Port, string? Name, ClassId Class, bool Autoplay, bool OpenPanels = false,
    string? TalkTo = null, string? SendCommand = null, bool ForgeDemo = false, string? SettingsPath = null, bool OpenMenu = false)
{
    public bool HostGiven { get; init; }
    /// <summary>Demo: open a panel on arrival (bag, runes, wallet, book, relics, pets).</summary>
    public string? OpenOnArrival { get; init; }
    public bool QuickPlay => Name is not null;

    public static ClientOptions Parse(string[] args)
    {
        int width = 1600, height = 900, port = Protocol.DefaultPort;
        bool vsync = true, autoplay = false, panels = false;
        int? frames = null;
        string? shot = null, talk = null, command = null;
        bool forgeDemo = false, menu = false, hostGiven = false;
        string? settings = null, open = null;
        string host = "127.0.0.1";
        string? name = null; // set: quick play as this character (open servers); unset: the login screen
        var cls = ClassId.Knight;
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--width": width = int.Parse(Next()); break;
                case "--height": height = int.Parse(Next()); break;
                case "--no-vsync": vsync = false; break;
                case "--frames": frames = int.Parse(Next()); break;
                case "--screenshot": shot = Next(); break;
                case "--host": host = Next(); hostGiven = true; break;
                case "--port": port = int.Parse(Next()); break;
                case "--name": name = Next(); break;
                case "--class": cls = Enum.Parse<ClassId>(Next(), ignoreCase: true); break;
                case "--autoplay": autoplay = true; break;
                case "--panels": panels = true; break;
                case "--talk": talk = Next(); break;            // demo: walk to this NPC and talk
                case "--cmd": command = Next(); break;          // demo: send this window request once in town
                case "--forge-demo": forgeDemo = true; break;   // demo: put the first two weapons on the anvils
                case "--settings": settings = Next(); break;    // use this settings file instead of the one in AppData
                case "--menu": menu = true; break;              // demo: open the game menu
                case "--open": open = Next(); break;            // demo: open bag, runes, wallet, book, relics or pets
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return new ClientOptions(width, height, vsync, frames, shot, host, port, name, cls, autoplay, panels, talk, command, forgeDemo, settings, menu) { HostGiven = hostGiven, OpenOnArrival = open };
    }
}
