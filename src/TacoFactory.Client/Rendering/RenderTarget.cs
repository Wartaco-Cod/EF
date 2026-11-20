using Silk.NET.OpenGL;

namespace TacoFactory.Client.Rendering;

/// <summary>
/// An offscreen colour buffer (framebuffer object + texture). Float targets (RGBA16F) hold light and glow above 1.0
/// so bright spots can bloom; 8-bit targets hold the ordinary picture.
/// </summary>
public sealed unsafe class RenderTarget : IDisposable
{
    private readonly GL _gl;
    public uint Framebuffer { get; }
    public uint Texture { get; }
    public int Width { get; }
    public int Height { get; }

    public RenderTarget(GL gl, int width, int height, bool hdr)
    {
        _gl = gl;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Texture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, Texture);
        gl.TexImage2D(TextureTarget.Texture2D, 0, hdr ? InternalFormat.Rgba16f : InternalFormat.Rgba8, (uint)Width, (uint)Height, 0,
            PixelFormat.Rgba, hdr ? PixelType.HalfFloat : PixelType.UnsignedByte, null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        Framebuffer = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, Texture, 0);
        var status = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException($"Framebuffer incomplete: {status}");
    }

    /// <summary>Draw into this target from now on (viewport covers it all).</summary>
    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void Clear(float r, float g, float b, float a)
    {
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(Framebuffer);
        _gl.DeleteTexture(Texture);
    }
}
