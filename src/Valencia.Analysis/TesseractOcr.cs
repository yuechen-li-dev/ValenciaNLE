using System.Globalization;
using System.Security.Cryptography;
using Valencia.Core;
using Valencia.Core.Timeline;
using Valencia.Media;

namespace Valencia.Analysis;

public sealed record OcrSettings(int StartMs = 0, int CadenceMs = 2000, int MaximumFrames = 24,
    int MaximumEdge = 1024, string Language = "eng", int MinimumConfidence = 55)
{
    public void Validate()
    {
        if (StartMs < 0 || CadenceMs is < 100 or > 600_000 || MaximumFrames is < 1 or > 120 ||
            MaximumEdge is < 320 or > 1024 || MinimumConfidence is < 0 or > 100 ||
            Language.Length is < 3 or > 12 || !Language.All(c => char.IsAsciiLetter(c) || c == '_'))
            throw new ArgumentException("Invalid OCR sampling bounds or language.");
    }
}

public sealed record OcrProvider(string Executable, string DataDirectory, string Version, string ExecutableSha256, string ModelSha256)
{
    public const string EnvironmentKey = "VALENCIA_TESSERACT_EXE";
    public string Algorithm => "tesseract-tsv-v1/" + Version;

    public static OcrProvider Open(string language)
    {
        string? configured = Environment.GetEnvironmentVariable(EnvironmentKey);
        if (configured is null || !Path.IsPathFullyQualified(configured) || !File.Exists(configured))
            throw new NotSupportedException($"Set {EnvironmentKey} to the absolute path of an installed Tesseract executable.");
        string data = Environment.GetEnvironmentVariable("VALENCIA_TESSDATA_DIR")
            ?? Path.Combine(Path.GetDirectoryName(configured)!, "tessdata");
        if (!Path.IsPathFullyQualified(data)) throw new ArgumentException("VALENCIA_TESSDATA_DIR must be absolute.");
        string model = Path.Combine(data, language + ".traineddata");
        if (!File.Exists(model)) throw new NotSupportedException($"Install the optional Tesseract language data for '{language}'.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var report = MediaProcess.CaptureAsync(configured, ["--version"], timeout.Token).GetAwaiter().GetResult();
        string version = report.Output.Split('\n')[0].Trim();
        if (!version.StartsWith("tesseract ", StringComparison.OrdinalIgnoreCase) || version.Length > 80)
            throw new NotSupportedException("The configured OCR executable did not identify itself as Tesseract.");
        return new(configured, data, version, Hash(configured), Hash(model));
    }

    private static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
}

public static class TesseractOcr
{
    private sealed record Word(string Line, string Text, int Left, int Top, int Width, int Height, double Confidence);

    public static async Task<AnalysisArtifact> RunAsync(MediaAsset asset, int streamIndex, OcrSettings settings, OcrProvider provider,
        string key, string fingerprint, string parameters, IProgress<AnalysisProgress>? progress, CancellationToken cancellation)
    {
        var imported = MediaAssetProbe.Import(asset.Id, asset.SourcePath);
        var stream = imported.Asset.Streams.SingleOrDefault(s => s.Index == streamIndex && s.Kind == TrackKind.Video)
            ?? throw new ArgumentException("OCR requires a video stream.");
        long duration = stream.End is { } end && stream.Start is { } start
            ? new SourcePosition(end).Since(new(start), TimeRounding.Floor).Ticks
            : imported.Duration.Ticks;
        var samples = new List<SourceObservation>();
        var working = Directory.CreateTempSubdirectory("Valencia-Ocr-");
        string image = Path.Combine(working.FullName, "frame.png");
        int scanned = 0;
        try
        {
            var previous = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < settings.MaximumFrames; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var offset = AnalysisTime.Milliseconds(checked(settings.StartMs + i * settings.CadenceMs));
                if (offset.Ticks >= duration) break;
                var frame = SourceFrameThumbnail.Write(asset, streamIndex, offset, settings.MaximumEdge, image, cancellation: cancellation);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var tsv = await MediaProcess.CaptureAsync(provider.Executable,
                    [image, "stdout", "--tessdata-dir", provider.DataDirectory, "-l", settings.Language, "--psm", "11", "tsv"], timeout.Token)
                    .ConfigureAwait(false);
                File.Delete(image);
                var at = new SourcePosition(frame.PresentationTimestamp);
                var recent = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var line in Parse(tsv.Output, settings.MinimumConfidence).GroupBy(word => word.Line).Take(32))
                {
                    string text = string.Join(' ', line.Select(w => w.Text));
                    if (text.Length > 220) text = text[..220];
                    int x = line.Min(w => w.Left), y = line.Min(w => w.Top);
                    int right = line.Max(w => w.Left + w.Width), bottom = line.Max(w => w.Top + w.Height);
                    if (x < 0 || y < 0 || right > frame.ImageWidth || bottom > frame.ImageHeight || right <= x || bottom <= y) continue;
                    var box = new ObservationBox((double)x / frame.ImageWidth, (double)y / frame.ImageHeight,
                        (double)(right - x) / frame.ImageWidth, (double)(bottom - y) / frame.ImageHeight);
                    string identity = FormattableString.Invariant($"{text.ToUpperInvariant()}|{Math.Round(box.X, 2)}|{Math.Round(box.Y, 2)}");
                    if (previous.TryGetValue(identity, out int retained))
                    {
                        var old = samples[retained];
                        if (at.CompareTo(old.Range.End) > 0) samples[retained] = old with { Range = old.Range with { End = at } };
                        recent[identity] = retained;
                        continue;
                    }
                    recent[identity] = samples.Count;
                    samples.Add(new(new(at, at, 0), text, "Tesseract", provider.Version, parameters,
                        "OCR inference on sampled decoded frames", line.Average(w => w.Confidence) / 100, box));
                }
                previous = recent;
                scanned++;
                progress?.Report(new(scanned, (double)scanned / settings.MaximumFrames));
            }
        }
        finally
        {
            if (File.Exists(image)) File.Delete(image);
            working.Delete();
        }
        if (scanned == 0) throw new ArgumentException("The OCR sampling window is outside the video stream.");
        return new(1, key, asset.Id, fingerprint, streamIndex, "ocr", provider.Algorithm, parameters, MediaAnalyzer.DecoderPolicy,
            imported.ContainerStart, "complete", 0, 0, scanned, [], [], [], [],
            ["Static lines observed at consecutive samples are collapsed into one interval."], Observations: [.. samples]);
    }

    private static IEnumerable<Word> Parse(string tsv, int minimumConfidence)
    {
        foreach (string row in tsv.Split('\n'))
        {
            var values = row.TrimEnd('\r').Split('\t', 12);
            if (values.Length != 12 || values[0] != "5" || string.IsNullOrWhiteSpace(values[11])) continue;
            if (!int.TryParse(values[6], CultureInfo.InvariantCulture, out int left) ||
                !int.TryParse(values[7], CultureInfo.InvariantCulture, out int top) ||
                !int.TryParse(values[8], CultureInfo.InvariantCulture, out int width) ||
                !int.TryParse(values[9], CultureInfo.InvariantCulture, out int height) ||
                !double.TryParse(values[10], CultureInfo.InvariantCulture, out double confidence) ||
                !double.IsFinite(confidence) || confidence < minimumConfidence || confidence > 100) continue;
            yield return new(string.Join('/', values[1..5]), values[11].Trim(), left, top, width, height, confidence);
        }
    }
}
