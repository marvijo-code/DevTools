using System.Globalization;
using System.Text.RegularExpressions;

namespace DevTools.Services.DiskCleanup;

/// <summary>One lane line from the cleanup summary, for example "Caches 3.20 GB (4 items)".</summary>
public sealed record DiskCleanupLaneTotal(string Lane, long? Bytes, int? ItemCount);

/// <summary>What the cleanup report said. Parsed from the script's console output.</summary>
public sealed record DiskCleanupReport(
    IReadOnlyList<DiskCleanupLaneTotal> Lanes,
    long? TotalReclaimableBytes,
    long? TotalReclaimedBytes,
    string? FreeBeforeText,
    string? FreeAfterText)
{
    public static DiskCleanupReport Empty { get; } = new([], null, null, null, null);

    private static readonly Regex LaneLine = new(
        @"^\s*(?<size>[\d.,]+)\s+(?<unit>GB|MB|KB|B)\s+(?<lane>\w+)\s+\((?<count>[\d,]+) items?\)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex ReclaimableLine = new(
        @"^\s*Total (?<label>reclaimable|reclaimed \(measured\)):\s+(?<size>[\d.,]+)\s+(?<unit>GB|MB|KB|B)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex FreeLine = new(
        @"^\s*C: free (?<when>before|after)\s*:\s*(?<text>.+?)\s*$",
        RegexOptions.Compiled);

    /// <summary>Parses the console lines of free-up-disk-space.ps1. Unrecognised lines are ignored.</summary>
    public static DiskCleanupReport Parse(IEnumerable<string> lines)
    {
        var lanes = new List<DiskCleanupLaneTotal>();
        long? reclaimable = null;
        long? reclaimed = null;
        string? freeBefore = null;
        string? freeAfter = null;

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var reclaimableMatch = ReclaimableLine.Match(raw);
            if (reclaimableMatch.Success)
            {
                var bytes = ParseBytes(reclaimableMatch.Groups["size"].Value, reclaimableMatch.Groups["unit"].Value);
                if (reclaimableMatch.Groups["label"].Value.StartsWith("reclaimable", StringComparison.Ordinal))
                {
                    reclaimable = bytes;
                }
                else
                {
                    reclaimed = bytes;
                }

                continue;
            }

            var freeMatch = FreeLine.Match(raw);
            if (freeMatch.Success)
            {
                if (freeMatch.Groups["when"].Value == "before")
                {
                    freeBefore = freeMatch.Groups["text"].Value;
                }
                else
                {
                    freeAfter = freeMatch.Groups["text"].Value;
                }

                continue;
            }

            var laneMatch = LaneLine.Match(raw);
            if (laneMatch.Success)
            {
                lanes.Add(new DiskCleanupLaneTotal(
                    laneMatch.Groups["lane"].Value,
                    ParseBytes(laneMatch.Groups["size"].Value, laneMatch.Groups["unit"].Value),
                    ParseCount(laneMatch.Groups["count"].Value)));
            }
        }

        return new DiskCleanupReport(lanes, reclaimable, reclaimed, freeBefore, freeAfter);
    }

    private static long? ParseBytes(string size, string unit)
    {
        if (!double.TryParse(size.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var multiplier = unit switch
        {
            "GB" => 1024d * 1024 * 1024,
            "MB" => 1024d * 1024,
            "KB" => 1024d,
            _ => 1d
        };

        return (long)Math.Round(value * multiplier);
    }

    private static int? ParseCount(string count)
    {
        return int.TryParse(count.Replace(",", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}

/// <summary>The outcome of one cleanup run of the external script.</summary>
public sealed record DiskCleanupResult(
    bool Started,
    bool Succeeded,
    bool TimedOut,
    int ExitCode,
    string Message,
    DiskCleanupReport Report,
    IReadOnlyList<string> Lines);
