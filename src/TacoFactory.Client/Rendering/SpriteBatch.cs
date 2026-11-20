using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace TacoFactory.Client.Rendering;

/// <summary>
/// Batches textured quads into one draw call per texture change. All drawing is in virtual 1920x1080 space,
/// origin top-left, and the projection maps that onto whatever window size the player has.
/// </summary>
/// <summary>How a batch's quads combine with what is already drawn.</summary>
public enum BlendMode { Alpha, Additive, Multiply }

public sealed unsafe class SpriteBatch : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Vertex
    {
        public float X, Y, U, V;
        public uint Rgba;
    }

    private const int MaxSprites = 4096;
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec4 aColor;
        uniform mat4 uProjection;
        out vec2 vUv;
        out vec4 vColor;
        void main() {
            vUv = aUv;
            vColor = aColor;
            gl_Position = uProjection * vec4(aPos, 0.0, 1.0);
        }
        """;
    private const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec4 vColor;
        uniform sampler2D uTexture;
        out vec4 FragColor;
        void main() {
            FragColor = texture(uTexture, vUv) * vColor;
        }
        """;

    private readonly GL _gl;
    private readonly ShaderProgram _shader;
    private readonly uint _vao, _vbo, _ebo;
    private readonly Vertex[] _vertices = new Vertex[MaxSprites * 4];
    private int _spriteCount;
    private Texture2D? _currentTexture;
    private bool _begun;
    private BlendMode _blend = BlendMode.Alpha;

    /// <summary>Draw calls issued in the last Begin/End pair. Shown in the debug readout.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>A 1x1 white texture for solid rectangles (tinted by color).</summary>
    public Texture2D Pixel { get; }

    public SpriteBatch(GL gl)
    {
        _gl = gl;
        _shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        Pixel = Texture2D.FromPixels(gl, "__pixel", 1, 1, [255, 255, 255, 255]);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertices.Length * sizeof(Vertex)), null, BufferUsageARB.DynamicDraw);

        var indices = new ushort[MaxSprites * 6];
        for (int i = 0, v = 0; i < indices.Length; i += 6, v += 4)
        {
            indices[i] = (ushort)v; indices[i + 1] = (ushort)(v + 1); indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 2); indices[i + 4] = (ushort)(v + 3); indices[i + 5] = (ushort)v;
        }
        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        fixed (ushort* p = indices)
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), p, BufferUsageARB.StaticDraw);

        uint stride = (uint)sizeof(Vertex);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);
        _gl.BindVertexArray(0);
    }

    public void Begin(float virtualWidth, float virtualHeight)
    {
        if (_begun) throw new InvalidOperationException("Begin called twice without End.");
        _begun = true;
        DrawCalls = 0;
        // Orthographic projection, origin top-left, y down. Column-major for GL.
        Span<float> m = stackalloc float[16];
        m[0] = 2f / virtualWidth;
        m[5] = -2f / virtualHeight;
        m[10] = -1f;
        m[12] = -1f;
        m[13] = 1f;
        m[15] = 1f;
        _shader.Use();
        _shader.SetMatrix("uProjection", m);
        _shader.SetInt("uTexture", 0);
        _gl.Enable(EnableCap.Blend);
        _blend = BlendMode.Alpha;
        ApplyBlend();
    }

    /// <summary>
    /// Changes how the following quads blend: Alpha (normal), Additive (light and glow: colours add up, past 1.0 in
    /// float targets) or Multiply (darkens: shadows on a light map).
    /// </summary>
    public void SetBlend(BlendMode mode)
    {
        if (mode == _blend) return;
        Flush();
        _blend = mode;
        ApplyBlend();
    }

    private void ApplyBlend()
    {
        switch (_blend)
        {
            case BlendMode.Additive: _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); break;
            case BlendMode.Multiply: _gl.BlendFunc(BlendingFactor.DstColor, BlendingFactor.OneMinusSrcAlpha); break;
            default: _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); break;
        }
    }

    /// <summary>Draws a texture centred on (cx, cy), turned by <paramref name="radians"/> (sparks, streaks, beams).</summary>
    public void DrawRotated(Texture2D texture, float cx, float cy, float width, float height, float radians, Color tint)
    {
        if (!_begun) throw new InvalidOperationException("Draw called outside Begin/End.");
        if (_currentTexture is not null && (_currentTexture != texture || _currentTexture.Handle != texture.Handle)) Flush();
        if (_spriteCount == MaxSprites) Flush();
        _currentTexture = texture;
        float c = MathF.Cos(radians), s = MathF.Sin(radians), hw = width / 2, hh = height / 2;
        uint col = tint.Packed;
        int i = _spriteCount * 4;
        _vertices[i] = new Vertex { X = cx + (-hw * c - -hh * s), Y = cy + (-hw * s + -hh * c), U = 0, V = 0, Rgba = col };
        _vertices[i + 1] = new Vertex { X = cx + (hw * c - -hh * s), Y = cy + (hw * s + -hh * c), U = 1, V = 0, Rgba = col };
        _vertices[i + 2] = new Vertex { X = cx + (hw * c - hh * s), Y = cy + (hw * s + hh * c), U = 1, V = 1, Rgba = col };
        _vertices[i + 3] = new Vertex { X = cx + (-hw * c - hh * s), Y = cy + (-hw * s + hh * c), U = 0, V = 1, Rgba = col };
        _spriteCount++;
    }

    /// <summary>Draws a whole texture into a destination rectangle.</summary>
    public void Draw(Texture2D texture, float x, float y, float width, float height, Color tint) =>
        Draw(texture, x, y, width, height, 0, 0, 1, 1, tint);

    /// <summary>Draws a sub-rectangle of a texture (normalised UVs), e.g. one frame of a sprite sheet.</summary>
    public void Draw(Texture2D texture, float x, float y, float width, float height,
                     float u0, float v0, float u1, float v1, Color tint)
    {
        if (!_begun) throw new InvalidOperationException("Draw called outside Begin/End.");
        if (_currentTexture is not null && (_currentTexture != texture || _currentTexture.Handle != texture.Handle))
            Flush();
        if (_spriteCount == MaxSprites) Flush();
        _currentTexture = texture;

        uint c = tint.Packed;
        int i = _spriteCount * 4;
        _vertices[i] = new Vertex { X = x, Y = y, U = u0, V = v0, Rgba = c };
        _vertices[i + 1] = new Vertex { X = x + width, Y = y, U = u1, V = v0, Rgba = c };
        _vertices[i + 2] = new Vertex { X = x + width, Y = y + height, U = u1, V = v1, Rgba = c };
        _vertices[i + 3] = new Vertex { X = x, Y = y + height, U = u0, V = v1, Rgba = c };
        _spriteCount++;
    }

    public void FillRect(float x, float y, float width, float height, Color color) =>
        Draw(Pixel, x, y, width, height, color);

    public void End()
    {
        if (!_begun) throw new InvalidOperationException("End called without Begin.");
        Flush();
        _begun = false;
        _currentTexture = null;
        if (_blend != BlendMode.Alpha) { _blend = BlendMode.Alpha; ApplyBlend(); }
    }

    private void Flush()
    {
        if (_spriteCount == 0 || _currentTexture is null) return;
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (Vertex* p = _vertices)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(_spriteCount * 4 * sizeof(Vertex)), p);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _currentTexture.Handle);
        _gl.DrawElements(PrimitiveType.Triangles, (uint)(_spriteCount * 6), DrawElementsType.UnsignedShort, (void*)0);
        DrawCalls++;
        _spriteCount = 0;
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        Pixel.Dispose();
        _shader.Dispose();
    }
}
