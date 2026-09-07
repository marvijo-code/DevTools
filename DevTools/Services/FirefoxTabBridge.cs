using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DevTools.Services;

/// <summary>Reads tab names from Firefox's local, explicitly enabled developer connection.</summary>
public static class FirefoxTabBridge
{
    private const int MaximumPacketBytes = 4 * 1024 * 1024;
    private const string TabQuery = "JSON.stringify(Array.from(Services.wm.getEnumerator('navigator:browser')).flatMap(w => Array.from(w.gBrowser.tabs, t => ({title:t.label, processId:t.linkedBrowser.browsingContext?.currentWindowGlobal?.osPid, browsingContextId:t.linkedBrowser.browsingContext?.id, discarded:t.hasAttribute('pending'), isPrivate:t.linkedBrowser.browsingContext?.usePrivateBrowsing}))))";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static TcpClient? _client;
    private static NetworkStream? _stream;
    private static string? _consoleActor;
    private static int _parentProcessId;
    public static bool IsConnected => _client is not null && _consoleActor is not null;
    public static string Status { get; private set; } = "Firefox tab names are not connected.";

    public static async Task ConnectAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        await Gate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            DisconnectCore();
            _client = new TcpClient();
            await _client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            _stream = _client.GetStream();
            var greeting = await ReceiveAsync(timeout.Token);
            if (greeting.GetProperty("applicationType").GetString() != "browser")
                throw new InvalidDataException("The local endpoint is not a Firefox debugger.");
            var descriptor = await RequestAsync("root", "getProcess", new() { ["id"] = 0 }, timeout.Token);
            var actor = descriptor.GetProperty("processDescriptor").GetProperty("actor").GetString()!;
            var target = (await RequestAsync(actor, "getTarget", null, timeout.Token)).GetProperty("process");
            _parentProcessId = target.GetProperty("processID").GetInt32();
            using var parent = System.Diagnostics.Process.GetProcessById(_parentProcessId);
            if (!parent.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The endpoint does not identify a running Firefox process.");
            _consoleActor = target.GetProperty("consoleActor").GetString()!;
            var tabs = await CaptureCoreAsync(timeout.Token);
            Status = $"Firefox connected: {tabs.Count} tab(s). Memory belongs to each process and may be shared.";
        }
        catch
        {
            DisconnectCore();
            Status = "Firefox is not connected. Enable its local developer server and allow the connection in Firefox.";
            throw;
        }
        finally { Gate.Release(); }
    }

    public static async Task<IReadOnlyDictionary<int, string>> ReadProcessTitlesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected) return new Dictionary<int, string>();
        await Gate.WaitAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!IsConnected) return new Dictionary<int, string>();
            var tabs = await CaptureCoreAsync(timeout.Token);
            Status = $"Firefox connected: {tabs.Count} tab(s). Memory belongs to each process and may be shared.";
            return GroupTitles(tabs);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            DisconnectCore();
            Status = "Firefox connection closed. Reconnect to refresh tab names.";
            return new Dictionary<int, string>();
        }
        finally { Gate.Release(); }
    }

    public static IReadOnlyDictionary<int, string> GroupTitles(IEnumerable<FirefoxTab> tabs) => tabs
        .Where(tab => tab.ProcessId > 0 && !tab.IsPrivate && !tab.Discarded)
        .GroupBy(tab => tab.ProcessId)
        .ToDictionary(group => group.Key, group => string.Join(Environment.NewLine, group.Select(tab => tab.Title)));

    public static IReadOnlyList<FirefoxTab> ParseTabs(string json, int parentProcessId)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(tab => new FirefoxTab(
            tab.GetProperty("title").GetString() ?? "Untitled tab",
            tab.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt32(out var id)
                ? (id == -1 ? parentProcessId : id) : 0,
            tab.TryGetProperty("discarded", out var discarded) && discarded.ValueKind == JsonValueKind.True,
            tab.TryGetProperty("isPrivate", out var isPrivate) && isPrivate.ValueKind == JsonValueKind.True)).ToList();
    }

    private static async Task<IReadOnlyList<FirefoxTab>> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        var acknowledgement = await RequestAsync(_consoleActor!, "evaluateJSAsync", new() { ["text"] = TabQuery }, cancellationToken);
        var resultId = acknowledgement.GetProperty("resultID").GetString();
        JsonElement response;
        do { response = await ReceiveAsync(cancellationToken); }
        while (!response.TryGetProperty("resultID", out var id) || id.GetString() != resultId ||
               !response.TryGetProperty("type", out var type) || type.GetString() != "evaluationResult");
        if (response.TryGetProperty("hasException", out var exception) && exception.ValueKind == JsonValueKind.True)
            throw new InvalidDataException("Firefox could not provide tab information.");
        var result = response.GetProperty("result");
        string json;
        if (result.ValueKind == JsonValueKind.String) json = result.GetString()!;
        else if (result.TryGetProperty("type", out var kind) && kind.GetString() == "longString")
        {
            var length = result.GetProperty("length").GetInt32();
            if (length > MaximumPacketBytes) throw new InvalidDataException("Firefox tab data is too large.");
            var full = await RequestAsync(result.GetProperty("actor").GetString()!, "substring",
                new() { ["start"] = 0, ["end"] = length }, cancellationToken);
            json = full.GetProperty("substring").GetString()!;
        }
        else throw new InvalidDataException("Firefox returned an unsupported tab response.");
        return ParseTabs(json, _parentProcessId);
    }

    private static async Task<JsonElement> RequestAsync(string actor, string type, Dictionary<string, object>? fields, CancellationToken cancellationToken)
    {
        fields ??= new();
        fields["to"] = actor;
        fields["type"] = type;
        var payload = JsonSerializer.SerializeToUtf8Bytes(fields);
        await _stream!.WriteAsync(Encoding.ASCII.GetBytes(payload.Length + ":"), cancellationToken);
        await _stream.WriteAsync(payload, cancellationToken);
        while (true)
        {
            var response = await ReceiveAsync(cancellationToken);
            if (!response.TryGetProperty("from", out var from) || from.GetString() != actor) continue;
            if (response.TryGetProperty("error", out _)) throw new InvalidDataException("Firefox rejected the tab query.");
            return response;
        }
    }

    private static async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
    {
        var byteBuffer = new byte[1];
        var header = new StringBuilder();
        while (true)
        {
            await _stream!.ReadExactlyAsync(byteBuffer, cancellationToken);
            if (byteBuffer[0] == ':') break;
            if (byteBuffer[0] < '0' || byteBuffer[0] > '9' || header.Length >= 8)
                throw new InvalidDataException("Firefox sent an invalid packet header.");
            header.Append((char)byteBuffer[0]);
        }
        if (!int.TryParse(header.ToString(), out var length) || length is <= 0 or > MaximumPacketBytes)
            throw new InvalidDataException("Firefox sent an invalid packet length.");
        var payload = new byte[length];
        await _stream!.ReadExactlyAsync(payload, cancellationToken);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static void DisconnectCore()
    {
        _client?.Dispose();
        _client = null;
        _stream = null;
        _consoleActor = null;
    }
}

public sealed record FirefoxTab(string Title, int ProcessId, bool Discarded, bool IsPrivate);
