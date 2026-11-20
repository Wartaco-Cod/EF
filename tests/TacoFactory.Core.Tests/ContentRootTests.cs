using TacoFactory.Core.Content;

namespace TacoFactory.Core.Tests;

public class ContentRootTests
{
    [Fact]
    public void Finds_assets_folder_above_start()
    {
        string root = Path.Combine(Path.GetTempPath(), "tf-" + Guid.NewGuid().ToString("N"));
        string deep = Path.Combine(root, "bin", "Debug", "net8.0");
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        Directory.CreateDirectory(deep);
        try
        {
            Assert.Equal(Path.Combine(root, "assets"), ContentRoot.Locate(deep));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Key_uses_forward_slashes()
    {
        string root = Path.Combine(Path.GetTempPath(), "assets");
        Assert.Equal("sprites/knight.png", ContentRoot.ToKey(root, Path.Combine(root, "sprites", "knight.png")));
    }
}
