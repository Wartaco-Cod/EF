using Silk.NET.OpenGL;
using StbImageSharp;

namespace TacoFactory.Client.Rendering;

/// <summary>
/// A 32-bit RGBA texture. The object stays the same across hot reloads; only its GL handle and pixels change,
/// so anything holding a reference picks up new art automatically.
/// </summary>
public sealed class Texture2D : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Key { get; }

    private Texture2D(GL gl, string key) { _gl = gl; Key = key; }

    public static Texture2D FromFile(GL gl, string key, string path)
    {
        var t = new Texture2D(gl, key);
        t.Reload(path);
        return t;
    }

    public static Texture2D FromPixels(GL gl, string key, int width, int height, byte[] rgba)
    {
        var t = new Texture2D(gl, key);
        t.Upload(width, height, rgba);
        return t;
    }

    /// <summary>Reads the file again and replaces the pixels. Throws IOException if the file is still being written.</summary>
    public void Reload(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageResult img = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        Upload(img.Width, img.Height, img.Data);
    }

    private unsafe void Upload(int width, int height, byte[] rgba)
    {
        if (Handle != 0) _gl.DeleteTexture(Handle);
        Handle = _gl.GenTexture();
        Width = width;
        Height = height;
        _gl.BindTexture(TextureTarget.Texture2D, Handle);
        fixed (byte* p = rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    public void Dispose()
    {
        if (Handle != 0) _gl.DeleteTexture(Handle);
        Handle = 0;
    }
}
