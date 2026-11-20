namespace TacoFactory.Core.Content;

/// <summary>Finds the game's asset folder so development builds read and hot-reload the source assets directly.</summary>
public static class ContentRoot
{
    public const string FolderName = "assets";

    /// <summary>Walks up from <paramref name="startDirectory"/> until a folder named "assets" is found.</summary>
    public static string? Locate(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, FolderName);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Normalises an asset path to forward slashes, relative to the asset root, for use as a cache key.</summary>
    public static string ToKey(string assetRoot, string fullPath) =>
        Path.GetRelativePath(assetRoot, fullPath).Replace('\\', '/');
}
