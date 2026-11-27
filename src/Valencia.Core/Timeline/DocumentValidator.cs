namespace Valencia.Core.Timeline;

public static class DocumentValidator
{
    public static void Validate(ProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Require(project.SchemaVersion == ProjectDocument.CurrentSchemaVersion, "Unsupported project schema.");
        Require(project.Name is not null && !project.Assets.IsDefault && !project.Sequences.IsDefault, "Missing project fields.");
        var ids = new HashSet<Guid>();
        AddId(project.Id);
        foreach (var asset in project.Assets)
        {
            Require(asset is not null, "Null asset.");
            AddId(asset!.Id);
            Require(asset.Id != TitleClips.AssetId, "That asset ID is reserved for title clips.");
            Require(asset.Name is not null && asset.Container is not null && !string.IsNullOrWhiteSpace(asset.SourcePath)
                && Path.IsPathFullyQualified(asset.SourcePath) && asset.SourcePath == Path.GetFullPath(asset.SourcePath),
                "Assets require a normalized absolute local source path.");
            Require(!asset.Streams.IsDefaultOrEmpty, "Asset has no editable streams.");
            Require(asset.ProxyKey is null || asset.ProxyKey.Length == 64 && asset.ProxyKey.All(char.IsAsciiHexDigit), "Invalid derived proxy reference.");
            var streams = new HashSet<int>();
            foreach (var stream in asset.Streams)
            {
                Require(stream is not null, "Null stream.");
                Require(stream!.Index >= 0 && streams.Add(stream.Index) && Enum.IsDefined(stream.Kind), "Invalid or duplicate stream identity.");
                Require(stream.Codec is not null && stream.TimeBaseNumerator > 0 && stream.TimeBaseDenominator > 0, "Invalid stream timebase/codec.");
                if (stream.FrameRate is { } frameRate) TimelineTime.RequireRate(frameRate);
                CheckBound(stream.Start); CheckBound(stream.End);
                if (stream.Start is { } start && stream.End is { } end)
                    Require(new SourcePosition(end).CompareTo(new(start)) > 0, "Source bounds must be increasing.");
                Require(stream.Width is null or > 0 && stream.Height is null or > 0 && stream.SampleRate is null or > 0
                    && stream.Channels is null or > 0, "Invalid stream format.");
                void CheckBound(MediaTimestamp? bound)
                {
                    if (bound is { } value) Require(value.Numerator == stream.TimeBaseNumerator && value.Denominator == stream.TimeBaseDenominator,
                        "Bounds must preserve the original stream timebase.");
                }
            }
        }
        var assets = project.Assets.ToDictionary(a => a.Id);
        assets[TitleClips.AssetId] = TitleClips.Asset; // title clips resolve to the reserved, unbounded title source
        foreach (var sequence in project.Sequences)
        {
            Require(sequence is not null, "Null sequence.");
            AddId(sequence!.Id);
            Require(sequence.Name is not null && sequence.Video is not null && !sequence.Tracks.IsDefault, "Missing sequence fields.");
            Require(sequence.Video!.Width > 0 && sequence.Video.Height > 0 && sequence.AudioSampleRate > 0, "Invalid sequence format.");
            TimelineTime.RequireRate(sequence.Video.FrameRate);
            _ = TimelineTime.FromFrames(1, sequence.Video.FrameRate);
            _ = TimelineTime.FromSamples(1, sequence.AudioSampleRate);
            Require(!sequence.Markers.IsDefault, "Missing sequence markers.");
            foreach (var marker in sequence.Markers)
            {
                Require(marker is not null, "Null sequence marker.");
                AddId(marker!.Id);
                Require(marker.At.Ticks >= 0 && marker.Label is { Length: > 0 and <= 256 }
                    && !string.IsNullOrWhiteSpace(marker.Label) && !marker.Label.Any(char.IsControl), "Invalid sequence marker.");
            }
            Require(sequence.LegacyTitles.IsDefaultOrEmpty, "Sequence-layer titles must be migrated to title clips before validation.");
            for (int order = 0; order < sequence.Tracks.Length; order++)
            {
                var track = sequence.Tracks[order];
                Require(track is not null, "Null track.");
                AddId(track!.Id);
                Require(track.Order == order && track.State is not null && track.Name is not null && Enum.IsDefined(track.Kind)
                    && !track.Clips.IsDefault, "Tracks require contiguous order and valid fields.");
                long previousEnd = 0;
                foreach (var clip in track.Clips)
                {
                    Require(clip is not null, "Null clip.");
                    AddId(clip!.Id);
                    if (clip.Retime is { } timing)
                    {
                        Require(track.Kind == TrackKind.Video && clip.SourceOut.Since(clip.SourceIn).Ticks > 0,
                            "Retiming requires a positive video source range.");
                        timing.Validate(clip);
                    }
                    Require(clip.Start.Ticks >= previousEnd && clip.Duration.Ticks > 0, "Clip duration must be positive; clips must be sorted and non-overlapping.");
                    previousEnd = clip.End.Ticks;
                    Require(clip.Properties is not null && clip.Properties.Name is not null && clip.Properties.Opacity is >= 0m and <= 1m
                        && clip.Properties.Gain >= 0m, "Invalid clip properties.");
                    CreatorSemantics.ValidateEffects(clip.Properties!);
                    Require(clip.Properties!.Video?.Region is null || track.Kind == TrackKind.Video, "Region Effects require a video clip.");
                    Require(clip.Properties.Video?.Motion is null || track.Kind == TrackKind.Video, "Clip motion requires a video clip.");
                    Require(clip.Title is null == (clip.AssetId != TitleClips.AssetId), "Only title clips use the title source, and they always carry title content.");
                    if (clip.Title is { } title)
                    {
                        Require(track.Kind == TrackKind.Video && clip.StreamIndex == 0 && clip.Retime is null,
                            "Titles are video clips at their own speed.");
                        TitleMotion.Validate(title, clip.Duration.Ticks);
                    }
                    Require(assets.TryGetValue(clip.AssetId, out var asset), "Clip references an unknown asset.");
                    var stream = asset!.Streams.FirstOrDefault(s => s.Index == clip.StreamIndex);
                    Require(stream is not null && stream.Kind == track.Kind, "Clip references an unknown or incompatible stream.");
                    CheckPosition(clip.SourceIn); CheckPosition(clip.SourceOut);
                    if (clip.Properties.Video?.Transform is { } transform)
                    {
                        Require(track.Kind == TrackKind.Video && transform.Curve is not null, "A clip transform requires a video stream and a curve.");
                        CheckPosition(transform.Origin);
                        _ = clip.SourceIn.Since(transform.Origin);
                        _ = clip.SourceOut.Since(transform.Origin);
                        transform.Curve!.Validate();
                    }
                    if (stream!.Start is { } start) Require(clip.SourceIn.CompareTo(new(start)) >= 0, "Clip starts before source bounds.");
                    if (stream.End is { } end) Require(clip.SourceOut.CompareTo(new(end)) <= 0, "Clip ends after source bounds.");
                    void CheckPosition(SourcePosition p) => Require(p.Timestamp.Numerator == stream!.TimeBaseNumerator &&
                        p.Timestamp.Denominator == stream.TimeBaseDenominator, "Clip timestamp must preserve its stream timebase.");
                }
                Require(!track.Transitions.IsDefault && track.Transitions.Length <= track.Clips.Length, "Invalid track transitions.");
                foreach (var transition in track.Transitions)
                {
                    Require(transition is not null, "Null track transition.");
                    AddId(transition!.Id);
                }
                _ = TransitionRules.Windows(track, assets);
            }
            _ = sequence.Duration;
            if (sequence.Creator is { } creator) CreatorSemantics.Validate(creator, sequence, project.Assets);
        }
        void AddId(Guid id) => Require(id != Guid.Empty && ids.Add(id), "Object IDs must be nonempty and globally unique.");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
