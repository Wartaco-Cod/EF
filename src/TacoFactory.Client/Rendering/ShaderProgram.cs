using Silk.NET.OpenGL;

namespace TacoFactory.Client.Rendering;

/// <summary>A compiled and linked GLSL program.</summary>
public sealed class ShaderProgram : IDisposable
{
    private readonly GL _gl;
    public uint Handle { get; }

    public ShaderProgram(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);
        Handle = _gl.CreateProgram();
        _gl.AttachShader(Handle, vs);
        _gl.AttachShader(Handle, fs);
        _gl.LinkProgram(Handle);
        _gl.GetProgram(Handle, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException("Shader link failed: " + _gl.GetProgramInfoLog(Handle));
        _gl.DetachShader(Handle, vs);
        _gl.DetachShader(Handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + _gl.GetShaderInfoLog(shader));
        return shader;
    }

    public void Use() => _gl.UseProgram(Handle);

    public unsafe void SetMatrix(string name, ReadOnlySpan<float> m4x4)
    {
        int loc = _gl.GetUniformLocation(Handle, name);
        fixed (float* p = m4x4) _gl.UniformMatrix4(loc, 1, false, p);
    }

    public void SetInt(string name, int value) => _gl.Uniform1(_gl.GetUniformLocation(Handle, name), value);

    public void Dispose() => _gl.DeleteProgram(Handle);
}
