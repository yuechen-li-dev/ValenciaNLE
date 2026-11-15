using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Copeland.TS.Backend.CSharp;
using Copeland.TS.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Valencia.Engine;
using RoslynCompilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation;

namespace Valencia.Scripting;

public sealed record ScriptDiagnostic(string Code, string Message, string Path, int Line, int Column);
public sealed record ScriptResult(bool Success, string? ReturnValue, ImmutableArray<ScriptDiagnostic> Diagnostics, bool CacheHit, double ElapsedMs);

/// <summary>Trusted local CLR execution, not a security sandbox. Bounded compilation cache; no JavaScript/Node execution.</summary>
public sealed class CopelandScriptHost : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, Artifact> cache = [];

    public async Task<ScriptResult> RunFileAsync(string path, EditingEngine engine, string entryPoint = "run", CancellationToken cancellation = default, bool allowProjectReplacement = true)
    {
        string source = await File.ReadAllTextAsync(path, cancellation).ConfigureAwait(false);
        return await RunAsync(source, Path.GetFullPath(path), engine, entryPoint, cancellation, allowProjectReplacement).ConfigureAwait(false);
    }
    public async Task<ScriptResult> RunAsync(string source, string path, EditingEngine engine, string entryPoint = "run", CancellationToken cancellation = default, bool allowProjectReplacement = true)
    {
        var elapsed = Stopwatch.StartNew();
        bool hit = false;
        try
        {
            if (source.Length > 1_048_576) throw new ArgumentException("Script exceeds 1 MiB.");
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            Artifact? artifact;
            try
            {
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + path + "\0" + entryPoint
                    + typeof(CopelandCompiler).Assembly.ManifestModule.ModuleVersionId
                    + typeof(CSharpBackend).Assembly.ManifestModule.ModuleVersionId
                    + typeof(EditingEngine).Assembly.ManifestModule.ModuleVersionId
                    + typeof(ScriptApi).Assembly.ManifestModule.ModuleVersionId)));
                hit = cache.TryGetValue(key, out artifact);
                if (!hit)
                {
                    var compiled = await Task.Run(() => Compile(source, path, entryPoint, cancellation), cancellation).ConfigureAwait(false);
                    if (compiled.Artifact is null) return new(false, null, compiled.Diagnostics, false, elapsed.Elapsed.TotalMilliseconds);
                    artifact = compiled.Artifact;
                    while (cache.Count >= 8)
                    {
                        var old = cache.First();
                        cache.Remove(old.Key);
                        old.Value.Context.Unload();
                    }
                    cache.Add(key, artifact);
                }
            }
            finally { gate.Release(); }
            cancellation.ThrowIfCancellationRequested();
            string? value = await Task.Run(() => (string?)artifact!.Entry.Invoke(null, [new ScriptApi(engine, cancellation, allowProjectReplacement)]), cancellation).ConfigureAwait(false);
            return new(true, value, [], hit, elapsed.Elapsed.TotalMilliseconds);
        }
        catch (Exception error)
        {
            Exception cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            string code = cause is OperationCanceledException ? "SCRIPT_CANCELLED" : "SCRIPT_RUNTIME";
            return new(false, null, [new(code, cause.Message, path, 0, 0)], hit, elapsed.Elapsed.TotalMilliseconds);
        }
    }

    private static (Artifact? Artifact, ImmutableArray<ScriptDiagnostic> Diagnostics) Compile(string source, string path, string entryPoint, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var compilation = CopelandCompiler.CompileToMir(source, new CopelandCompilationOptions
        {
            SourcePath = path,
            ProjectRoot = Path.GetDirectoryName(path),
            BackendTarget = CopelandBackendTarget.Create("csharp", "ryujit", "net10.0"),
            ClrReferences = [new(null, typeof(ScriptApi).Assembly)]
        });
        if (!compilation.Success)
        {
            return (null, [.. compilation.Diagnostics.Select(d =>
            {
                int position = Math.Clamp(d.Position, 0, source.Length);
                int line = source[..position].Count(c => c == '\n') + 1;
                int previous = source.LastIndexOf('\n', Math.Max(0, position - 1), position);
                return new ScriptDiagnostic(d.Id, d.Message, d.SourcePath ?? path, line, position - previous);
            })]);
        }
        var emitted = CSharpBackend.Emit(compilation.MirCompilation!.Program!, new CSharpEmissionOptions { PublicFunctionNames = new HashSet<string> { entryPoint } });
        if (emitted.Diagnostics.Count > 0)
            return (null, [.. emitted.Diagnostics.Select(d => new ScriptDiagnostic(d.Id, d.Message, path, 0, 0))]);
        cancellation.ThrowIfCancellationRequested();
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platform.Concat(new[] { typeof(ScriptApi).Assembly.Location, typeof(EditingEngine).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
        var csharp = RoslynCompilation.Create("ValenciaScript_" + Guid.NewGuid().ToString("N"),
            [CSharpSyntaxTree.ParseText(emitted.SourceText, path: path + ".generated.cs", cancellationToken: cancellation)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, checkOverflow: true));
        using var stream = new MemoryStream();
        var result = csharp.Emit(stream, cancellationToken: cancellation);
        if (!result.Success)
            return (null, [.. result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return new ScriptDiagnostic(d.Id, d.GetMessage(), span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
            })]);
        stream.Position = 0;
        var context = new AssemblyLoadContext("Valencia trusted script", isCollectible: true);
        var assembly = context.LoadFromStream(stream);
        var entry = assembly.GetType("Copeland.Generated.CopelandModule")?.GetMethod(entryPoint, BindingFlags.Public | BindingFlags.Static);
        if (entry is null || entry.ReturnType != typeof(string) || entry.GetParameters().Length != 1 || entry.GetParameters()[0].ParameterType != typeof(ScriptApi))
        {
            context.Unload();
            return (null, [new("SCRIPT_ENTRY", "Entry must be a public function " + entryPoint + "(api: ScriptApi): string.", path, 0, 0)]);
        }
        return (new(context, entry), []);
    }
    public void Dispose()
    {
        foreach (var item in cache.Values) item.Context.Unload();
        cache.Clear();
        gate.Dispose();
    }
    private sealed record Artifact(AssemblyLoadContext Context, MethodInfo Entry);
}
