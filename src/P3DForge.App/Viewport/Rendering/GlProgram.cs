using System.Numerics;
using OpenTK.Graphics.OpenGL4;

namespace P3DForge.App.Viewport.Rendering;

/// <summary>A linked GLSL program with cached uniform locations.</summary>
public sealed class GlProgram : IDisposable
{
    public int Handle { get; }
    readonly Dictionary<string, int> uniforms = [];

    /// <summary>Program name for diagnostics.</summary>
    public string Name { get; init; } = "";

    public GlProgram(string vertex, string fragment)
    {
        int vs = Compile(ShaderType.VertexShader, vertex);
        int fs = Compile(ShaderType.FragmentShader, fragment);
        Handle = GL.CreateProgram();
        GL.AttachShader(Handle, vs);
        GL.AttachShader(Handle, fs);
        GL.LinkProgram(Handle);
        GL.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int ok);
        GL.DetachShader(Handle, vs);
        GL.DetachShader(Handle, fs);
        GL.DeleteShader(vs);
        GL.DeleteShader(fs);
        if (ok == 0) throw new InvalidOperationException("Shader link failed: " + GL.GetProgramInfoLog(Handle));
    }

    static int Compile(ShaderType type, string src)
    {
        // The source is passed with its char count as length: a multi-byte UTF-8 character (even in a comment)
        // would cut off the end of the shader. GLSL is ASCII anyway.
        if (src.Any(c => c > 127)) src = new string([.. src.Select(c => c > 127 ? ' ' : c)]);
        int s = GL.CreateShader(type);
        GL.ShaderSource(s, src);
        GL.CompileShader(s);
        GL.GetShader(s, ShaderParameter.CompileStatus, out int ok);
        if (ok == 0) throw new InvalidOperationException($"{type} compile failed: " + GL.GetShaderInfoLog(s));
        return s;
    }

    public void Use() => GL.UseProgram(Handle);

    int Loc(string name)
    {
        if (!uniforms.TryGetValue(name, out int l)) uniforms[name] = l = GL.GetUniformLocation(Handle, name);
        return l;
    }

    /// <summary>Uploads a System.Numerics matrix (row-vector convention) for use as <c>M * vec4(p, 1)</c> in GLSL.</summary>
    public void Set(string name, Matrix4x4 m)
    {
        Span<float> f = stackalloc float[16]
        {
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
        };
        GL.UniformMatrix4(Loc(name), 1, false, ref f[0]);
    }

    public void Set(string name, Vector2 v) => GL.Uniform2(Loc(name), v.X, v.Y);
    public void Set(string name, Vector3 v) => GL.Uniform3(Loc(name), v.X, v.Y, v.Z);
    public void Set(string name, Vector4 v) => GL.Uniform4(Loc(name), v.X, v.Y, v.Z, v.W);
    public void Set(string name, float v) => GL.Uniform1(Loc(name), v);
    public void Set(string name, int v) => GL.Uniform1(Loc(name), v);

    public void Dispose() => GL.DeleteProgram(Handle);
}

