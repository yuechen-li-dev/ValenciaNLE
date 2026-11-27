using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Analysis;

public static class MediaResourceAnalyzer
{
    public static string Algorithm(string kind) => "media-resource-v2/" + kind;

    public static AnalysisArtifact Run(MediaAsset asset, int stream, string kind, string key, string fingerprint, CancellationToken cancellation)
    {
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        return new(1, key, asset.Id, fingerprint, stream, kind, Algorithm(kind), "original-clocks", MediaAnalyzer.DecoderPolicy,
            imported.ContainerStart, "complete", 0, 0, 0, [], [], [], [], [],
            Proxy: kind == "proxy" ? MediaProxy.GenerateAsync(asset, stream, cancellation).GetAwaiter().GetResult() : null,
            Index: kind == "index" ? MediaIndex.Build(asset, stream, cancellation) : null);
    }
}
