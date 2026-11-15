using System.Security.Cryptography;
using System.Text.Json;
using Aurelian.Rendering.Contracts.Shaders;
using Aurelian.Shaders.Graphics;
using Copeland.TS.Gpu;
using Copeland.TS.Gpu.VdMir;

namespace Valencia.Rendering;

internal static class VideoShader
{
    private sealed record CachedShader(string SourceHash, string CompilerHash, CompiledGraphicsProgram Program);

    public static CompiledGraphicsProgram Load(string name = "VideoQuad")
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Assets", name + ".v.ts");
        string source = File.ReadAllText(sourcePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        string sourceHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        string packagedPath = Path.Combine(AppContext.BaseDirectory, "Assets", name + ".json");
        if (File.Exists(packagedPath))
        {
            CachedShader? packaged = JsonSerializer.Deserialize<CachedShader>(File.ReadAllText(packagedPath));
            if (packaged?.SourceHash == sourceHash)
            {
                return packaged.Program;
            }
        }
        string compilerHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GpuGraphicsBinder).Assembly.Location)))
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(VdMirGraphicsBackend).Assembly.Location)));
        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Shaders");
        string cachePath = Path.Combine(cacheDirectory, name + ".json");
        if (File.Exists(cachePath))
        {
            CachedShader? cached = JsonSerializer.Deserialize<CachedShader>(File.ReadAllText(cachePath));
            if (cached?.SourceHash == sourceHash && cached.CompilerHash == compilerHash)
            {
                return cached.Program;
            }
        }

        VdMirGraphicsModule module = GpuGraphicsBinder.Compile(new GpuCompilationRequest([new GpuSourceFile(name + ".v.ts", source)]));
        if (!module.Success)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, module.Diagnostics.Select(item => item.Message)));
        }
        VdMirGraphicsBackendResult backend = VdMirGraphicsBackend.Compile(module);
        CompiledGraphicsProgram program = CompiledGraphicsProgramExporter.Export(module, backend);
        Directory.CreateDirectory(cacheDirectory);
        string temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new CachedShader(sourceHash, compilerHash, program)));
        File.Move(temporary, cachePath, overwrite: true);
        return program;
    }
    public static void WriteArtifact(string outputPath, string name = "VideoQuad")
    {
        string sourcePath = Path.Combine(AppContext.BaseDirectory, "Assets", name + ".v.ts");
        string source = File.ReadAllText(sourcePath).Replace("\r\n", "\n", StringComparison.Ordinal);
        string sourceHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
        CompiledGraphicsProgram program = Load(name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(new CachedShader(sourceHash, "precompiled", program)));
    }

}
