namespace TacoFactory.Core;

/// <summary>Identity of the engine build. Shown in window titles and server logs.</summary>
public static class EngineInfo
{
    public const string Name = "Taco Factory";
    public const string Version = "0.8.2";

    /// <summary>Base UI and world layout resolution. Everything is authored against this and scaled.</summary>
    public const int VirtualWidth = 1920;
    public const int VirtualHeight = 1080;

    /// <summary>Authoritative simulation rate shared by client prediction and the server.</summary>
    public const int TickRate = 20;
}
