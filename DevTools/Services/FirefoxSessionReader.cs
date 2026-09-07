using System.Buffers.Binary;
using System.Text.Json;
using DevTools.Models;

namespace DevTools.Services;

/// <summary>Read-only tab titles from Firefox's current session backup. Never reads closed tabs or form data.</summary>
public static class FirefoxSessionReader
{
    private const int MaxBytes = 64 * 1024 * 1024;

    public static IReadOnlyList<AppActivityEntry> ReadCurrentSessions()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(root)) return [];
        var result = new List<AppActivityEntry>();
        foreach (var profile in Directory.EnumerateDirectories(root))
        {
            // parent.lock alone can survive a crash. Require an active exclusive lock as well.
            if (!IsProfileInUse(profile)) continue;
            var file = Path.Combine(profile, "sessionstore-backups", "recovery.jsonlz4");
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > MaxBytes) continue;
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var bytes = new MemoryStream();
                input.CopyTo(bytes);
                var json = Decode(bytes.ToArray());
                result.AddRange(ParseTabs(json, Path.GetFileName(profile), File.GetLastWriteTime(file)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or InvalidOperationException) { }
        }
        return result;
    }

    private static bool IsProfileInUse(string profile)
    {
        var file = Path.Combine(profile, "parent.lock");
        if (!File.Exists(file)) return false;
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static IReadOnlyList<AppActivityEntry> ParseTabs(byte[] json, string profile, DateTime updated)
    {
        using var document = JsonDocument.Parse(json);
        var result = new List<AppActivityEntry>();
        if (!document.RootElement.TryGetProperty("windows", out var windows)) return result;
        var windowIndex = 0;
        foreach (var window in windows.EnumerateArray())
        {
            windowIndex++;
            if (window.TryGetProperty("isPrivate", out var privacy) && privacy.ValueKind == JsonValueKind.True) continue;
            if (!window.TryGetProperty("tabs", out var tabs)) continue;
            var tabIndex = 0;
            foreach (var tab in tabs.EnumerateArray())
            {
                tabIndex++;
                if (tab.TryGetProperty("isPrivate", out privacy) && privacy.ValueKind == JsonValueKind.True) continue;
                if (!tab.TryGetProperty("entries", out var entries) || entries.GetArrayLength() == 0) continue;
                var index = tab.TryGetProperty("index", out var selected) && selected.TryGetInt32(out var value) ? value - 1 : entries.GetArrayLength() - 1;
                if (index < 0 || index >= entries.GetArrayLength()) continue;
                var page = entries[index];
                var url = page.TryGetProperty("url", out var address) ? address.GetString() ?? "" : "";
                var title = page.TryGetProperty("title", out var label) ? label.GetString() : null;
                if (string.IsNullOrWhiteSpace(title)) title = string.IsNullOrWhiteSpace(url) ? "Untitled tab" : url;
                result.Add(new($"firefox:{profile}:{windowIndex}:{tabIndex}:{url}", title, "Firefox tab",
                    $"Saved {updated:t} · Window {windowIndex}",
                    $"From Firefox's saved session at {updated:g}. Changes since then may not be included. Connect Firefox for live titles and shared process IDs. Per-tab memory is not available from this snapshot.", url, null));
            }
        }
        return result;
    }

    public static byte[] Decode(byte[] data)
    {
        if (data.Length < 12 || !data.AsSpan(0, 8).SequenceEqual("mozLz40\0"u8)) throw new InvalidDataException("Invalid Firefox session header.");
        var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        if (length < 0 || length > MaxBytes) throw new InvalidDataException("Firefox session is too large.");
        var output = new byte[length];
        var source = 12;
        var target = 0;
        int ReadLength(int initial)
        {
            var count = initial;
            if (initial == 15)
            {
                int extra;
                do
                {
                    if (source >= data.Length) throw new InvalidDataException("Truncated Firefox session.");
                    extra = data[source++];
                    if (count > MaxBytes - extra) throw new InvalidDataException("Invalid block size.");
                    count += extra;
                } while (extra == 255);
            }
            return count;
        }
        while (source < data.Length)
        {
            var token = data[source++];
            var literalLength = ReadLength(token >> 4);
            if (literalLength > data.Length - source || literalLength > output.Length - target) throw new InvalidDataException("Invalid literal block.");
            data.AsSpan(source, literalLength).CopyTo(output.AsSpan(target));
            source += literalLength;
            target += literalLength;
            if (source == data.Length) break;
            if (source + 2 > data.Length) throw new InvalidDataException("Truncated match offset.");
            var offset = data[source] | data[source + 1] << 8;
            source += 2;
            var matchLength = ReadLength(token & 15) + 4;
            if (offset == 0 || offset > target || matchLength > output.Length - target) throw new InvalidDataException("Invalid match block.");
            for (var i = 0; i < matchLength; i++) { output[target] = output[target - offset]; target++; }
        }
        if (target != output.Length) throw new InvalidDataException("Incomplete Firefox session.");
        return output;
    }
}
