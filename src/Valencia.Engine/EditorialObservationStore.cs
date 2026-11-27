using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Valencia.Analysis;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Engine;

public sealed record EditorialObservationInput(string Start, string Text, string? End = null, double? Confidence = null,
    ObservationBox? Box = null, string? Track = null);
public sealed record EditorialObservationBatch(string Asset, int Stream, string Kind, string Provider, string Version,
    string Configuration, string Evidence, ImmutableArray<EditorialObservationInput> Observations);

public sealed partial class EditingEngine
{
    public async Task<object> StoreEditorialObservationsAsync(ProjectSnapshot snapshot, EditorialObservationBatch batch,
        CancellationToken cancellation = default)
    {
        if (batch.Kind is not ("topics" or "visual" or "speakers" or "subjects" or "beats" or "annotations") ||
            batch.Evidence is not ("human" or "model" or "measured") || batch.Observations.Length is < 1 or > 512 ||
            !ValidText(batch.Provider, 80) || !ValidText(batch.Version, 80) || !ValidText(batch.Configuration, 512))
            throw new ArgumentException("Supply 1–512 observations and bounded provider, version, configuration and evidence fields.");
        var asset = AgentHandles.Asset(snapshot.Document, batch.Asset);
        Policy?.Check(asset.SourcePath);
        var stream = asset.Streams.SingleOrDefault(item => item.Index == batch.Stream)
            ?? throw new ArgumentException("The observation stream is absent.");
        if ((batch.Kind is "visual" or "subjects") && stream.Kind != TrackKind.Video ||
            (batch.Kind is "speakers" or "beats") && stream.Kind != TrackKind.Audio)
            throw new ArgumentException("Observation kind does not match the selected stream.");
        string fingerprint = AnalysisCache.Fingerprint(asset.SourcePath);
        var imported = await Task.Run(() => MediaAssetProbe.Import(asset.Id, asset.SourcePath), cancellation).ConfigureAwait(false);
        var origin = new SourcePosition(imported.ContainerStart);
        var items = batch.Observations.Select(row =>
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ValidText(row.Text, 220) || row.Confidence is { } confidence && !Unit(confidence) ||
                row.Track is { } track && (!ValidText(track, 64) || !track.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) ||
                row.Box is { } box && (!Unit(box.X) || !Unit(box.Y) || !Unit(box.Width) || !Unit(box.Height) ||
                    box.Width == 0 || box.Height == 0 || box.X + box.Width > 1 || box.Y + box.Height > 1) ||
                batch.Kind == "subjects" && (row.Track is null || row.Box is null))
                throw new ArgumentException("Invalid observation text, confidence, track or normalized box.");
            var start = AgentTime.Position(row.Start, stream.FrameRate, source: true);
            var end = row.End is { } to ? AgentTime.Position(to, stream.FrameRate, source: true) : start;
            if (start.Ticks < 0 || end.Ticks < start.Ticks || end.Ticks > imported.Duration.Ticks)
                throw new ArgumentException("Observation must lie within the source container.");
            return new SourceObservation(new(origin.Shift(start), origin.Shift(end), 0), row.Text, batch.Provider, batch.Version,
                batch.Configuration, "external " + batch.Evidence, row.Confidence, row.Box, row.Track);
        }).OrderBy(row => row.Range.Start, Comparer<SourcePosition>.Create((left, right) => left.CompareTo(right))).ToImmutableArray();
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EngineJson.Write(batch))));
        string algorithm = "external-observations-v1/" + batch.Kind;
        string key = AnalysisCache.Key(asset.Id, fingerprint, stream.Index, algorithm, digest);
        var artifact = new AnalysisArtifact(1, key, asset.Id, fingerprint, stream.Index, batch.Kind, algorithm,
            digest, MediaAnalyzer.DecoderPolicy, imported.ContainerStart, "complete", 0, 0, items.Length, [], [], [], [],
            ["External provenance is supplied by the caller."], Observations: items);
        if (!ReferenceEquals(snapshot, Session.Snapshot)) throw new ArgumentException("The project changed during observation import.");
        if (AnalysisCache.Fingerprint(asset.SourcePath) != fingerprint) throw new IOException("Source changed during observation import.");
        await Analysis.Cache.PublishAsync(artifact, cancellation).ConfigureAwait(false);
        return new { key, batch.Kind, batch.Provider, batch.Version, count = items.Length, asset = AgentHandles.For("asset", asset.Id), stream = stream.Index };

        static bool Unit(double value) => double.IsFinite(value) && value >= 0 && value <= 1;
        static bool ValidText(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    }
}
