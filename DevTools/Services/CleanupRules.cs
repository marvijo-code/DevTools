using System.Globalization;
using System.Text.RegularExpressions;

namespace DevTools.Services;

/// <summary>
/// The decisions behind the cleanup options, kept free of UI types so they can be tested on their own.
/// </summary>
public static partial class CleanupRules
{
    /// <summary>
    /// Temp files touched more recently than this are left alone: they usually belong to something still running,
    /// such as an installer, a build, or an open AI session's scratch folder.
    /// </summary>
    public static readonly TimeSpan TempKeepWindow = TimeSpan.FromHours(48);

    // Images whose stopped containers usually hold data that exists nowhere else.
    private static readonly string[] DataHoldingImageMarkers =
    [
        "mssql", "sqlserver", "postgres", "mysql", "mariadb", "mongo", "oracle", "elasticsearch", "opensearch", "nextcloud", "minio"
    ];

    /// <summary>Removes matching files under a folder (or only adds them up when <paramref name="dryRun"/> is set).</summary>
    /// <remarks>
    /// Junctions and symbolic links are never followed, so a link into a repository or another drive cannot be
    /// emptied by accident. Locked files are skipped and counted. Folders left empty are removed only when they are
    /// as old as the files the caller asked for, so a folder a running process just created stays in place.
    /// </remarks>
    public static CleanupTally DeleteFiles(string rootPath, Func<FileSystemInfo, bool> shouldDelete, bool dryRun, CancellationToken cancellationToken)
    {
        var tally = new CleanupTally();
        if (!Directory.Exists(rootPath))
        {
            return tally;
        }

        DeleteFilesCore(new DirectoryInfo(rootPath), shouldDelete, dryRun, tally, cancellationToken);
        return tally;
    }

    private static void DeleteFilesCore(DirectoryInfo directory, Func<FileSystemInfo, bool> shouldDelete, bool dryRun, CleanupTally tally, CancellationToken cancellationToken)
    {
        IEnumerable<FileSystemInfo> children;
        try
        {
            children = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            tally.SkippedCount++;
            return;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((child.Attributes & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (child is DirectoryInfo childDirectory)
                {
                    // Decide before emptying it: deleting the files inside updates the folder's own timestamp.
                    var removeWhenEmpty = shouldDelete(childDirectory);
                    DeleteFilesCore(childDirectory, shouldDelete, dryRun, tally, cancellationToken);
                    if (!dryRun && removeWhenEmpty && !childDirectory.EnumerateFileSystemInfos().Any())
                    {
                        childDirectory.Delete();
                    }

                    continue;
                }

                var file = (FileInfo)child;
                if (!shouldDelete(file))
                {
                    continue;
                }

                var length = file.Length;
                if (!dryRun)
                {
                    if (file.IsReadOnly)
                    {
                        file.IsReadOnly = false;
                    }

                    file.Delete();
                }

                tally.Bytes += length;
                tally.FileCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tally.SkippedCount++;
            }
        }
    }

    /// <summary>True when the item was last written before the cutoff.</summary>
    public static bool IsOlderThan(FileSystemInfo item, DateTime cutoffUtc)
    {
        return item.LastWriteTimeUtc < cutoffUtc;
    }

    /// <summary>
    /// Picks the Playwright browser folders that a newer download of the same browser has replaced, for example
    /// chromium-1208 when chromium-1217 is also present. The newest of each browser, single installs such as
    /// ffmpeg, and the Playwright MCP profile folders (mcp-*) are always kept.
    /// </summary>
    public static IReadOnlyList<string> FindSupersededBrowserFolders(IEnumerable<string> folderNames)
    {
        return folderNames
            .Where(name => !name.StartsWith("mcp-", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Name: name, Match: BrowserFolderPattern().Match(name)))
            .Where(item => item.Match.Success)
            .Select(item => (item.Name, Family: item.Match.Groups["family"].Value, Revision: int.Parse(item.Match.Groups["revision"].Value, CultureInfo.InvariantCulture)))
            .GroupBy(item => item.Family, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.OrderByDescending(item => item.Revision).Skip(1).Select(item => item.Name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True for images such as SQL Server or Postgres, whose stopped containers often hold real data.</summary>
    public static bool IsDataHoldingImage(string image)
    {
        return DataHoldingImageMarkers.Any(marker => image.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reads a Docker size such as "21.94GB", "2.002kB", or "5.303GB (24%)". Docker counts in powers of 1000.
    /// </summary>
    public static long? TryParseDockerSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = DockerSizePattern().Match(text.Trim());
        if (!match.Success || !double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => double.NaN
        };

        // Rounded, because 2.002 * 1000 is 2001.999... in floating point.
        return double.IsNaN(multiplier) ? null : (long)Math.Round(value * multiplier);
    }

    public static string FormatBytes(long bytes)
    {
        const double scale = 1024d;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var suffixIndex = 0;
        double displayValue = Math.Abs(bytes);
        while (displayValue >= scale && suffixIndex < suffixes.Length - 1)
        {
            displayValue /= scale;
            suffixIndex++;
        }

        var sign = bytes < 0 ? "-" : string.Empty;
        return suffixIndex == 0 ? $"{sign}{displayValue:0} {suffixes[suffixIndex]}" : $"{sign}{displayValue:0.00} {suffixes[suffixIndex]}";
    }

    [GeneratedRegex(@"^(?<family>[a-z][a-z0-9_\-]*?)-(?<revision>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BrowserFolderPattern();

    [GeneratedRegex(@"^(?<value>\d+(\.\d+)?)\s*(?<unit>[kKMGT]?B)", RegexOptions.CultureInvariant)]
    private static partial Regex DockerSizePattern();
}

/// <summary>What a delete pass removed, or would remove in a dry run.</summary>
public sealed class CleanupTally
{
    public long Bytes { get; set; }

    public int FileCount { get; set; }

    /// <summary>Files or folders that were locked or denied and so were left in place.</summary>
    public int SkippedCount { get; set; }
}
