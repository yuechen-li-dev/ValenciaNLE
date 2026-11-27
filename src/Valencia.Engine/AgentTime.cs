using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Valencia.Core;
using Valencia.Core.Timeline;

namespace Valencia.Engine;

public sealed record AgentIssue(string Code, string Message, string? Suggestion = null, string? Subject = null,
    int? OperationIndex = null, object? Constraints = null);

public sealed class AgentRequestException(AgentIssue issue) : ArgumentException(issue.Message)
{
    public AgentIssue Issue { get; } = issue;
}

public sealed record ReadableTime(long Ticks, string Seconds, string Space, long? Frame, string? Timecode, FrameRate? Rate);

/// <summary>Text notation at the domain boundary. Neither decimal seconds nor timecode are storage formats.</summary>
public static partial class AgentTime
{
    [GeneratedRegex(@"^([0-9]{2,}):([0-9]{2}):([0-9]{2})([:;])([0-9]{2,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex TimecodePattern();
    [GeneratedRegex(@"^([0-9]{2,}):([0-9]{2}):([0-9]{2})(\.[0-9]{1,18})?$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();
    [GeneratedRegex(@"^([0-9]+)(?:\.([0-9]{1,18}))?(ms|us|ns|s|m|h)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitsPattern();

    public static SequenceTime Position(string value, FrameRate? rate = null, SequenceDocument? sequence = null, bool source = false)
    {
        ValidateText(value);
        if (value.StartsWith("marker:", StringComparison.Ordinal))
        {
            if (source || sequence is null) throw Error("TIME_CONTEXT", "Marker references require a sequence, not a source stream.", value);
            string selector = value[7..];
            long offset = 0;
            if (selector.StartsWith('"'))
            {
                int close = QuotedEnd(selector);
                string name;
                try { name = JsonSerializer.Deserialize<string>(selector[..(close + 1)])!; }
                catch (JsonException) { throw Error("INVALID_TIMECODE", "Marker labels must be JSON-quoted.", value); }
                var matching = sequence.Markers.Where(m => string.Equals(m.Label, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0) throw Error("UNKNOWN_HANDLE", $"No marker named '{name}' in this sequence.", name);
                if (matching.Length > 1)
                    throw new AgentRequestException(new("AMBIGUOUS_REFERENCE", $"More than one marker is named '{name}'.",
                        "Use marker:<handle> to identify exactly one marker.", name, Constraints: matching.Select(m => AgentHandles.For("marker", m.Id)).ToArray()));
                if (close + 1 < selector.Length) offset = Offset(selector[(close + 1)..], rate ?? sequence.Video.FrameRate);
                return Nonnegative(checked(matching[0].At.Ticks + offset), value);
            }
            int separator = selector.IndexOfAny(['+', '-'], Math.Min(24, selector.Length)); // handle is prefix + 22 chars; GUIDs are also accepted without offsets
            if (separator >= 0 && !Guid.TryParse(selector, out _))
            {
                offset = Offset(selector[separator..], rate ?? sequence.Video.FrameRate);
                selector = selector[..separator];
            }
            var id = AgentHandles.Resolve(selector, "marker", sequence.Markers.Select(m => m.Id));
            return Nonnegative(checked(sequence.Markers.First(m => m.Id == id).At.Ticks + offset), value);
        }
        if (value[0] is '+' or '-') throw Error("TIME_CONTEXT", "An absolute position cannot be signed without an explicit anchor.", value);
        return Nonnegative(ParseMagnitude(value, rate ?? sequence?.Video.FrameRate, position: true, negative: false), value);
    }

    public static SequenceTime Duration(string value, FrameRate? rate = null, bool signed = false)
    {
        ValidateText(value);
        bool sign = value[0] is '+' or '-';
        if (sign && !signed) throw Error("TIME_CONTEXT", "This duration must be unsigned.", value);
        return new(ParseMagnitude(sign ? value[1..] : value, rate, position: false, negative: sign && value[0] == '-'));
    }

    private static long Offset(string value, FrameRate? rate)
    {
        if (value.Length < 2 || value[0] is not ('+' or '-')) throw Error("INVALID_TIMECODE", "A marker offset needs an explicit + or -.", value);
        return Duration(value, rate, signed: true).Ticks;
    }

    private static int QuotedEnd(string text)
    {
        for (int i = 1; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '"') return i;
        }
        throw Error("INVALID_TIMECODE", "Unterminated quoted marker label.", text);
    }

    private static SequenceTime Nonnegative(long ticks, string input) => ticks >= 0 ? new(ticks)
        : throw Error("TIME_RANGE", "Resolved position is before the coordinate origin.", input);

    private static void ValidateText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value != value.Trim())
            throw Error("INVALID_TIMECODE", "A time reference must be nonempty, bounded and have no surrounding whitespace.", null);
    }

    private static long ParseMagnitude(string value, FrameRate? rate, bool position, bool negative)
    {
        int sign = negative ? -1 : 1;
        if (value.Length > 0 && value.All(char.IsAsciiDigit)
            && BigInteger.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            return ToTicks(sign * ticks, value);
        if (value.StartsWith('f') && value.Length > 1 && value[1..].All(char.IsAsciiDigit))
            return FromFrame(ToTicks(sign * Parse(value[1..]), value), RequireRate(rate, value), value);

        var match = TimecodePattern().Match(value);
        if (match.Success)
        {
            if (!position) throw Error("TIME_CONTEXT", "SMPTE labels are positions. Express durations as frame counts, seconds or ticks.", value);
            var actual = RequireRate(rate, value);
            int nominal = Nominal(actual);
            int minute = (int)Parse(match.Groups[2].Value), second = (int)Parse(match.Groups[3].Value), frame = (int)Parse(match.Groups[5].Value);
            if (minute >= 60 || second >= 60 || frame >= nominal)
                throw Error("INVALID_TIMECODE", $"Timecode needs minutes/seconds 00..59 and frame labels 0..{nominal - 1} at this rate.", value);
            BigInteger minutes = 60 * Parse(match.Groups[1].Value) + minute;
            BigInteger index = (minutes * 60 + second) * nominal + frame;
            if (match.Groups[4].Value == ";")
            {
                int drop = Drop(actual);
                if (drop == 0) throw Error("INVALID_TIMECODE", "Drop-frame labels require a 30000/1001 or 60000/1001 clock.", value);
                if (minute % 10 != 0 && second == 0 && frame < drop)
                    throw new AgentRequestException(new("INVALID_TIMECODE", "That drop-frame label is skipped.",
                        $"Use frame label {drop:D2} or later in this second, or choose another time.", value));
                index -= drop * (minutes - minutes / 10);
            }
            return FromFrame(ToTicks(index, value), actual, value);
        }

        match = ClockPattern().Match(value);
        if (match.Success)
        {
            int m = (int)Parse(match.Groups[2].Value), s = (int)Parse(match.Groups[3].Value);
            if (m >= 60 || s >= 60) throw Error("INVALID_TIMECODE", "Minutes and seconds must be below 60.", value);
            return DecimalTicks(Parse(match.Groups[1].Value) * 3600 + m * 60 + s,
                match.Groups[4].Value.TrimStart('.'), sign, 1, 1, value);
        }
        match = UnitsPattern().Match(value);
        if (match.Success)
        {
            (int mul, int div) = match.Groups[3].Value switch
            {
                "h" => (3600, 1), "m" => (60, 1), "ms" => (1, 1000), "us" => (1, 1_000_000),
                "ns" => (1, 1_000_000_000), _ => (1, 1)
            };
            return DecimalTicks(Parse(match.Groups[1].Value), match.Groups[2].Value, sign, mul, div, value);
        }
        throw Error("INVALID_TIMECODE", "Use exact integer ticks, HH:MM:SS:FF, HH:MM:SS.fff, 62.5s, f1875, or a sequence marker.", value);
    }

    private static long DecimalTicks(BigInteger whole, string digits, int sign, int mul, int div, string input)
    {
        var scale = BigInteger.Pow(10, digits.Length);
        var wholeTicks = BigInteger.DivRem((whole * scale + (digits.Length == 0 ? 0 : Parse(digits)))
            * mul * SequenceTime.TicksPerSecond, scale * div, out var remainder);
        return ToTicks(sign * (wholeTicks + (remainder * 2 >= scale * div ? 1 : 0)), input);
    }

    public static ReadableTime Describe(SequenceTime time, string space, FrameRate? rate = null, bool? drop = null)
    {
        long? frame = null;
        string? label = null;
        if (rate is { Numerator: > 0, Denominator: > 0 } clock && time.Ticks >= 0)
        {
            frame = TimelineTime.FrameIndex(time, clock, TimeRounding.Floor);
            label = FormatFrame(frame.Value, clock, drop ?? Drop(clock) > 0);
        }
        return new(time.Ticks, ((decimal)time.Ticks / SequenceTime.TicksPerSecond).ToString("0.############", CultureInfo.InvariantCulture), space, frame, label, rate);
    }

    public static string FormatFrame(long frame, FrameRate rate, bool drop)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        int nominal = Nominal(rate);
        BigInteger label = frame;
        int skipped = drop ? Drop(rate) : 0;
        if (drop && skipped == 0) throw Error("INVALID_TIMECODE", "Drop-frame formatting is unavailable for this clock.", null);
        if (skipped != 0)
        {
            int minuteFrames = nominal * 60 - skipped;
            int ten = nominal * 600 - skipped * 9;
            long rest = frame % ten;
            label += (BigInteger)skipped * (9 * (frame / ten) + Math.Max(0, (rest - skipped) / minuteFrames));
        }
        var seconds = BigInteger.DivRem(label, nominal, out var f);
        return FormattableString.Invariant($"{seconds / 3600:D2}:{seconds / 60 % 60:D2}:{seconds % 60:D2}{(drop ? ';' : ':')}{f:D2}");
    }

    private static int Nominal(FrameRate rate)
    {
        if (rate.Numerator <= 0 || rate.Denominator <= 0) throw Error("TIME_CONTEXT", "Timecode requires a positive frame clock.", null);
        long nominal = ((long)rate.Numerator + rate.Denominator / 2) / rate.Denominator;
        if (nominal is < 1 or > 999) throw Error("TIME_CONTEXT", "Timecode supports nominal rates from 1 to 999 fps.", null);
        return (int)nominal;
    }
    private static int Drop(FrameRate rate) => (long)rate.Numerator * 1001 == (long)rate.Denominator * 30000 ? 2
        : (long)rate.Numerator * 1001 == (long)rate.Denominator * 60000 ? 4 : 0;
    private static FrameRate RequireRate(FrameRate? rate, string input) => rate is { Numerator: > 0, Denominator: > 0 } clock ? clock
        : throw Error("TIME_CONTEXT", "Frame references require a sequence or a video stream with a known frame rate.", input);
    private static long FromFrame(long frame, FrameRate rate, string input)
    {
        try { return TimelineTime.FromFrames(frame, rate).Ticks; }
        catch (ArgumentException) { throw Error("TIME_GRID", "This frame is not exactly representable on the editing tick grid.", input); }
        catch (OverflowException) { throw Error("TIME_RANGE", "Frame exceeds the signed 64-bit tick range.", input); }
    }
    private static BigInteger Parse(string value) => BigInteger.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    private static long ToTicks(BigInteger value, string input) => value >= long.MinValue && value <= long.MaxValue
        ? (long)value : throw Error("TIME_RANGE", "Time exceeds the signed 64-bit tick range.", input);
    private static AgentRequestException Error(string code, string message, string? subject) => new(new(code, message, Subject: subject));
}
