using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Media;

/// <summary>Regenerable, bounded cache of the original decoder's sample-count anchors.</summary>
internal static class AudioSeekStorage
{
    internal const int MaximumAnchors = 12000;
    private const int MaximumEntries = 24;
    private const int MaximumBytes = 1024 * 1024;
    private const int HashBytes = 32;
    private const int FormatVersion = 1;

    internal sealed record Snapshot(IReadOnlyList<AudioSeekIndex.Anchor> Anchors, SourcePosition Through, bool Ended);

    private static string Root => Path.Combine(ValenciaData.Root, "AudioSeek-v1");

    internal static string? Key(MediaAsset asset, AssetStream stream)
    {
        try
        {
            string key = string.Create(CultureInfo.InvariantCulture,
                $"audio-seek-{FormatVersion}|{MediaProxy.Identity(asset.SourcePath)}|{stream.Index}|{stream.Codec}|{stream.SampleRate}|{stream.TimeBaseNumerator}/{stream.TimeBaseDenominator}|{FfmpegRuntime.DecoderIdentity}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static Snapshot? Read(string key)
    {
        try
        {
            using var file = new FileStream(Path.Combine(Root, key + ".idx"), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (file.Length is <= HashBytes or > MaximumBytes) return null;
            byte[] buffer = new byte[checked((int)file.Length)];
            file.ReadExactly(buffer);
            int payload = buffer.Length - HashBytes;
            if (!SHA256.HashData(buffer.AsSpan(0, payload)).AsSpan().SequenceEqual(buffer.AsSpan(payload))) return null;
            using var reader = new BinaryReader(new MemoryStream(buffer, 0, payload, writable: false));
            if (reader.ReadInt32() != FormatVersion || reader.ReadString() != key) return null;
            int count = reader.ReadInt32();
            if (count is < 1 or > MaximumAnchors) return null;
            bool ended = reader.ReadBoolean();
            SourcePosition through = ReadPosition(reader);
            var anchors = new AudioSeekIndex.Anchor[count];
            for (int index = 0; index < count; index++)
            {
                var anchor = new AudioSeekIndex.Anchor(ReadTimestamp(reader), ReadPosition(reader), ReadPosition(reader), reader.ReadInt64());
                if (anchor.Samples < 0 || (index > 0 && anchors[index - 1].Position.CompareTo(anchor.Position) >= 0))
                    return null;
                anchors[index] = anchor;
            }
            if (reader.BaseStream.Position != payload || through.CompareTo(anchors[^1].Position) < 0
                || (!ended && !through.EquivalentTo(anchors[^1].Position))) return null;
            return new(anchors, through, ended);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
            or ArgumentException or OverflowException) { return null; }
    }

    internal static bool Write(string key, Snapshot snapshot)
    {
        string? temporary = null;
        try
        {
            string root = Root;
            Directory.CreateDirectory(root);
            // Multiple package/CLI processes may be open. Readers see an atomic snapshot,
            // and the short commit lock prevents replacement by a less complete writer.
            using var commit = new FileStream(Path.Combine(root, ".write.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            if (Read(key) is { } previous && (previous.Ended || previous.Anchors.Count > snapshot.Anchors.Count
                || (previous.Anchors.Count == snapshot.Anchors.Count && !snapshot.Ended))) return true;
            using var payload = new MemoryStream();
            using var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true);
            writer.Write(FormatVersion);
            writer.Write(key);
            writer.Write(snapshot.Anchors.Count);
            writer.Write(snapshot.Ended);
            WritePosition(writer, snapshot.Through);
            foreach (var anchor in snapshot.Anchors)
            {
                WriteTimestamp(writer, anchor.Pts);
                WritePosition(writer, anchor.Position);
                WritePosition(writer, anchor.EpochOrigin);
                writer.Write(anchor.Samples);
            }
            writer.Flush();
            var bytes = payload.GetBuffer().AsSpan(0, checked((int)payload.Length));
            if (bytes.Length + HashBytes > MaximumBytes) return false;
            temporary = Path.Combine(root, $".{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Write(SHA256.HashData(bytes));
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path.Combine(root, key + ".idx"), overwrite: true);
            temporary = null;
            foreach (var old in new DirectoryInfo(root).EnumerateFiles("*.idx")
                .Where(file => file.Name.Length == 68 && file.Name[..64].All(char.IsAsciiHexDigit))
                .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumEntries))
                old.Delete();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static MediaTimestamp ReadTimestamp(BinaryReader reader)
    {
        var timestamp = new MediaTimestamp(reader.ReadInt64(), reader.ReadInt32(), reader.ReadInt32());
        if (timestamp.Numerator <= 0 || timestamp.Denominator <= 0)
            throw new InvalidDataException("Invalid audio seek timestamp.");
        return timestamp;
    }

    private static SourcePosition ReadPosition(BinaryReader reader) => new(ReadTimestamp(reader), new(reader.ReadInt64()));

    private static void WriteTimestamp(BinaryWriter writer, MediaTimestamp timestamp)
    {
        writer.Write(timestamp.Value);
        writer.Write(timestamp.Numerator);
        writer.Write(timestamp.Denominator);
    }

    private static void WritePosition(BinaryWriter writer, SourcePosition position)
    {
        WriteTimestamp(writer, position.Timestamp);
        writer.Write(position.Offset.Ticks);
    }
}
