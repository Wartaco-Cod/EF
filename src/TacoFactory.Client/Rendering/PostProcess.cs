using Silk.NET.OpenGL;

namespace TacoFactory.Client.Rendering;

/// <summary>Look of a scene after lighting: colour grade, vignette, bloom and film grain.</summary>
public struct GradeSettings
{
    /// <summary>Multiplies the lit picture (warm candlelight, cold dawn...).</summary>
    public float TintR, TintG, TintB;
    public float Saturation, Contrast, Exposure;
    public float Vignette, Grain, Bloom, BloomThreshold;
    /// <summary>0..1: red pulse at the screen edges when the player is badly hurt.</summary>
    public float Hurt;
    /// <summary>0..1: brief white wash (level up, boss death).</summary>
    public float Flash;
    public float Time;

    public static GradeSettings Neutral => new()
    {
        TintR = 1, TintG = 1, TintB = 1, Saturation = 1, Contrast = 1, Exposure = 1,
        Vignette = 0.35f, Grain = 0.03f, Bloom = 0.6f, BloomThreshold = 0.85f,
    };
}

/// <summary>
/// The Taco Factory frame pipeline. The world is drawn into three targets: the picture (scene), the light reaching
/// the ground (light, cleared to the zone's ambient level, lights added on top) and things that shine by themselves
/// (emissive: sparks, flames, magic). The composite multiplies picture by light, adds the glow, blooms whatever is
/// brighter than the threshold, then grades, vignettes and adds grain. The HUD is drawn afterwards, untouched.
/// </summary>
public sealed unsafe class PostProcess : IDisposable
{
    private const string QuadVs = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        out vec2 vUv;
        void main() { vUv = aPos * 0.5 + 0.5; gl_Position = vec4(aPos, 0.0, 1.0); }
        """;

    // Bright pass: light the picture, keep only what is above the threshold (soft knee), at half resolution.
    private const string BrightFs = """
        #version 330 core
        in vec2 vUv; out vec4 o;
        uniform sampler2D uScene, uLight, uEmissive;
        uniform float uThreshold;
        void main() {
            vec3 c = texture(uScene, vUv).rgb * texture(uLight, vUv).rgb + texture(uEmissive, vUv).rgb;
            float l = max(c.r, max(c.g, c.b));
            float k = clamp((l - uThreshold) / max(l, 1e-4), 0.0, 1.0);
            o = vec4(c * k, 1.0);
        }
        """;

    // Separable 9-tap Gaussian.
    private const string BlurFs = """
        #version 330 core
        in vec2 vUv; out vec4 o;
        uniform sampler2D uTex; uniform vec2 uStep;
        void main() {
            vec3 s = texture(uTex, vUv).rgb * 0.2270270270;
            s += texture(uTex, vUv + uStep * 1.3846153846).rgb * 0.3162162162;
            s += texture(uTex, vUv - uStep * 1.3846153846).rgb * 0.3162162162;
            s += texture(uTex, vUv + uStep * 3.2307692308).rgb * 0.0702702703;
            s += texture(uTex, vUv - uStep * 3.2307692308).rgb * 0.0702702703;
            o = vec4(s, 1.0);
        }
        """;

    private const string CompositeFs = """
        #version 330 core
        in vec2 vUv; out vec4 o;
        uniform sampler2D uScene, uLight, uEmissive, uBloomA, uBloomB;
        uniform vec3 uTint; uniform float uSat, uContrast, uExposure, uVignette, uGrain, uBloom, uHurt, uFlash, uTime;
        uniform vec2 uAspect;
        float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
        void main() {
            vec3 c = texture(uScene, vUv).rgb * texture(uLight, vUv).rgb + texture(uEmissive, vUv).rgb;
            c += (texture(uBloomA, vUv).rgb + texture(uBloomB, vUv).rgb * 0.8) * uBloom;
            c *= uExposure * uTint;
            // Filmic shoulder: highlights roll off instead of clipping.
            c = c / (1.0 + max(c - 0.85, 0.0) * 0.9);
            float luma = dot(c, vec3(0.2126, 0.7152, 0.0722));
            c = mix(vec3(luma), c, uSat);
            c = (c - 0.5) * uContrast + 0.5;
            vec2 d = (vUv - 0.5) * uAspect;
            float r = length(d);
            c *= 1.0 - uVignette * smoothstep(0.35, 1.0, r * 1.25);
            float edge = smoothstep(0.45, 1.0, r * 1.3);
            c = mix(c, vec3(0.55, 0.02, 0.0), uHurt * edge * 0.75);
            c += uFlash;
            c += (hash(vUv * 1024.0 + fract(uTime) * 61.0) - 0.5) * uGrain;
            o = vec4(clamp(c, 0.0, 1.0), 1.0);
        }
        """;

    private readonly GL _gl;
    private readonly ShaderProgram _bright, _blur, _composite;
    private readonly uint _vao, _vbo;
    private RenderTarget? _scene, _light, _emissive, _halfA, _halfB, _quarterA, _quarterB;

    public RenderTarget Scene => _scene!;
    public RenderTarget Light => _light!;
    public RenderTarget Emissive => _emissive!;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public PostProcess(GL gl)
    {
        _gl = gl;
        _bright = new ShaderProgram(gl, QuadVs, BrightFs);
        _blur = new ShaderProgram(gl, QuadVs, BlurFs);
        _composite = new ShaderProgram(gl, QuadVs, CompositeFs);
        float[] quad = [-1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1];
        _vao = gl.GenVertexArray();
        gl.BindVertexArray(_vao);
        _vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = quad) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), p, BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
        gl.BindVertexArray(0);
    }

    /// <summary>(Re)creates the targets when the picture size changes. Light renders at half size: it is soft anyway.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height && _scene is not null) return;
        DisposeTargets();
        Width = width; Height = height;
        _scene = new RenderTarget(_gl, width, height, hdr: false);
        _light = new RenderTarget(_gl, width / 2, height / 2, hdr: true);
        _emissive = new RenderTarget(_gl, width, height, hdr: true);
        _halfA = new RenderTarget(_gl, width / 2, height / 2, hdr: true);
        _halfB = new RenderTarget(_gl, width / 2, height / 2, hdr: true);
        _quarterA = new RenderTarget(_gl, width / 4, height / 4, hdr: true);
        _quarterB = new RenderTarget(_gl, width / 4, height / 4, hdr: true);
    }

    private void Bind(int unit, uint texture)
    {
        _gl.ActiveTexture(TextureUnit.Texture0 + unit);
        _gl.BindTexture(TextureTarget.Texture2D, texture);
    }

    private void Quad()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private void Blur(RenderTarget src, RenderTarget tmp, int passes)
    {
        _blur.Use();
        _blur.SetInt("uTex", 0);
        int loc = _gl.GetUniformLocation(_blur.Handle, "uStep");
        for (int i = 0; i < passes; i++)
        {
            tmp.Bind(); Bind(0, src.Texture); _gl.Uniform2(loc, 1f / src.Width, 0f); Quad();
            src.Bind(); Bind(0, tmp.Texture); _gl.Uniform2(loc, 0f, 1f / src.Height); Quad();
        }
    }

    /// <summary>Lights, blooms and grades the three targets onto the screen area (x, y, w, h) of the window.</summary>
    public void Composite(GradeSettings g, int x, int y, int w, int h)
    {
        _gl.Disable(EnableCap.Blend);
        // Bloom: bright pass at half size, blurred there (tight glow) and again at quarter size (wide halo).
        _bright.Use();
        _bright.SetInt("uScene", 0); _bright.SetInt("uLight", 1); _bright.SetInt("uEmissive", 2);
        _gl.Uniform1(_gl.GetUniformLocation(_bright.Handle, "uThreshold"), g.BloomThreshold);
        _halfA!.Bind(); Bind(0, _scene!.Texture); Bind(1, _light!.Texture); Bind(2, _emissive!.Texture); Quad();
        Blur(_halfA, _halfB!, 1);
        _blur.Use(); _blur.SetInt("uTex", 0);
        _quarterA!.Bind(); Bind(0, _halfA.Texture);
        _gl.Uniform2(_gl.GetUniformLocation(_blur.Handle, "uStep"), 0f, 0f); Quad(); // plain downsample
        Blur(_quarterA, _quarterB!, 2);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(x, y, (uint)w, (uint)h);
        _composite.Use();
        _composite.SetInt("uScene", 0); _composite.SetInt("uLight", 1); _composite.SetInt("uEmissive", 2);
        _composite.SetInt("uBloomA", 3); _composite.SetInt("uBloomB", 4);
        Bind(0, _scene.Texture); Bind(1, _light.Texture); Bind(2, _emissive.Texture); Bind(3, _halfA.Texture); Bind(4, _quarterA.Texture);
        void F(string n, float v) => _gl.Uniform1(_gl.GetUniformLocation(_composite.Handle, n), v);
        _gl.Uniform3(_gl.GetUniformLocation(_composite.Handle, "uTint"), g.TintR, g.TintG, g.TintB);
        _gl.Uniform2(_gl.GetUniformLocation(_composite.Handle, "uAspect"), w / (float)Math.Max(w, h) * 1.0f, h / (float)Math.Max(w, h));
        F("uSat", g.Saturation); F("uContrast", g.Contrast); F("uExposure", g.Exposure); F("uVignette", g.Vignette);
        F("uGrain", g.Grain); F("uBloom", g.Bloom); F("uHurt", g.Hurt); F("uFlash", g.Flash); F("uTime", g.Time);
        Quad();
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(0);
        _gl.Enable(EnableCap.Blend);
    }

    private void DisposeTargets()
    {
        foreach (var t in new[] { _scene, _light, _emissive, _halfA, _halfB, _quarterA, _quarterB }) t?.Dispose();
        _scene = _light = _emissive = _halfA = _halfB = _quarterA = _quarterB = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        _bright.Dispose(); _blur.Dispose(); _composite.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
