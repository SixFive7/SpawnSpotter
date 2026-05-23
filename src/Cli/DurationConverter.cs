using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SpawnSpotter.Cli;

/// <summary>
/// Parses human-friendly durations: <c>90s</c>, <c>45m</c>, <c>2h</c>, <c>1d</c>, compound like <c>2h30m</c>.
/// Rejects zero and negatives.
/// </summary>
public sealed partial class DurationConverter : TypeConverter
{
    [GeneratedRegex(@"(?<value>\d+)\s*(?<unit>d|h|m|s)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComponentRegex();

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string raw)
        {
            return base.ConvertFrom(context, culture, value);
        }

        if (TryParse(raw, out var span, out var error))
        {
            return span;
        }

        throw new FormatException(error ?? $"Invalid duration: '{raw}'.");
    }

    public static bool TryParse(string input, out TimeSpan result, [NotNullWhen(false)] out string? error)
    {
        result = TimeSpan.Zero;
        error = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Duration value is empty.";
            return false;
        }

        var trimmed = input.Trim();
        if (trimmed.StartsWith('-'))
        {
            error = $"Duration cannot be negative: '{input}'.";
            return false;
        }

        var matches = ComponentRegex().Matches(trimmed);
        if (matches.Count == 0)
        {
            error = $"Duration '{input}' has no recognizable components. Expected e.g. '90s', '45m', '2h', '1d', '2h30m'.";
            return false;
        }

        // Ensure all characters were consumed by matches (no stray content like '2x').
        var totalMatched = 0;
        foreach (Match m in matches)
        {
            totalMatched += m.Length;
        }
        // Strip whitespace from input before length comparison
        var noWhitespace = trimmed.Replace(" ", "", StringComparison.Ordinal);
        var matchedNoWhitespace = 0;
        foreach (Match m in matches)
        {
            matchedNoWhitespace += m.Value.Replace(" ", "", StringComparison.Ordinal).Length;
        }
        if (matchedNoWhitespace != noWhitespace.Length)
        {
            error = $"Duration '{input}' contains unrecognized characters. Use d/h/m/s units only.";
            return false;
        }

        var total = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var num = int.Parse(m.Groups["value"].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups["unit"].Value.ToLowerInvariant();
            total += unit switch
            {
                "d" => TimeSpan.FromDays(num),
                "h" => TimeSpan.FromHours(num),
                "m" => TimeSpan.FromMinutes(num),
                "s" => TimeSpan.FromSeconds(num),
                _ => TimeSpan.Zero,
            };
        }

        if (total <= TimeSpan.Zero)
        {
            error = $"Duration must be positive: '{input}'.";
            return false;
        }

        result = total;
        return true;
    }
}
