using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aurelian.Shaders.Compute;
using Copeland.TS.Gpu;

namespace Valencia.Media;

internal sealed record ClipEffectShader(string SourceHash, string Entry, byte[] Spirv)
{
    private const string Resource = "Valencia.Media.Assets.ClipEffects";
    private static readonly Lazy<ClipEffectShader> Kernel = new(() => Read(false));
    public static ClipEffectShader Load() => Kernel.Value;

    internal static void Prepare(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Read(true)));

    private static ClipEffectShader Read(bool compile)
    {
        var assembly = typeof(ClipEffectShader).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream(Resource + ".v.ts")!);
        string source = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        if (!compile && assembly.GetManifestResourceStream(Resource + ".json") is { } stream)
        {
            using (stream)
                if (JsonSerializer.Deserialize<ClipEffectShader>(stream) is { } packaged && packaged.SourceHash == hash) return packaged;
        }
        var program = GpuComputeBinder.Compile(new([new("ClipEffects.v.ts", source)]));
        if (!program.Success) throw new InvalidOperationException(string.Join("\n", program.Diagnostics.Select(d => d.Message)));
        var compiled = VdMirComputeBackend.Compile(program);
        if (compiled.Spirv.Length == 0 || compile && !compiled.SpirvValidated)
            throw new InvalidOperationException("Clip Effects shader compilation/validation failed. " + compiled.DxcOutput + compiled.SpirvValidationOutput);
        return new(hash, program.EntryPoint!.EmittedName, compiled.Spirv);
    }
}
