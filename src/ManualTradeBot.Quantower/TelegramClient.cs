using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ManualTradeBot.Quantower;

internal sealed class TelegramClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };
    private readonly Action<string> _log;
    private readonly Func<string, Task> _reply;
    private readonly string _token, _prepChat, _entryChat;
    private CancellationTokenSource? _cts;
    private long _offset;

    public TelegramClient(string configPath, Action<string> log, Func<string, Task> reply)
    {
        _log = log; _reply = reply;
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var r = doc.RootElement;
        _token = Text(r, "bot_token");
        _prepChat = Text(r, "prep_chat_id");
        _entryChat = Text(r, "entry_chat_id");
        if (string.IsNullOrWhiteSpace(_entryChat)) _entryChat = _prepChat;
        if (string.IsNullOrWhiteSpace(_token) || string.IsNullOrWhiteSpace(_prepChat))
            throw new InvalidOperationException("Telegram config requires bot_token and prep_chat_id.");
    }

    public void Start() { _cts = new(); _ = Task.Run(() => Poll(_cts.Token)); }
    public void Prep(string message) => Send(_prepChat, message);
    public void Entry(string message) => Send(_entryChat, message);
    public void Status(string message) => Send(_entryChat, message);

    private void Send(string chat, string message) => _ = Task.Run(async () =>
    {
        try
        {
            var body = JsonSerializer.Serialize(new { chat_id = chat, text = message, disable_notification = false });
            using var c = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"https://api.telegram.org/bot{_token}/sendMessage", c);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception e) { _log($"Telegram send failed: {e.Message}"); }
    });

    private async Task Poll(CancellationToken ct)
    {
        await GetUpdates(0, ct, drain: true);
        while (!ct.IsCancellationRequested)
        {
            try { await GetUpdates(25, ct, drain: false); }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { _log($"Telegram poll failed: {e.Message}"); await Task.Delay(5000, ct); }
        }
    }

    private async Task GetUpdates(int timeout, CancellationToken ct, bool drain)
    {
        using var response = await _http.GetAsync($"https://api.telegram.org/bot{_token}/getUpdates?timeout={timeout}&offset={_offset}", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("result", out var result)) return;
        foreach (var update in result.EnumerateArray())
        {
            _offset = Math.Max(_offset, update.GetProperty("update_id").GetInt64() + 1);
            if (drain || !update.TryGetProperty("message", out var msg)) continue;
            string chat = msg.GetProperty("chat").GetProperty("id").GetInt64().ToString();
            if (chat != _prepChat && chat != _entryChat) continue;
            if (msg.TryGetProperty("text", out var text)) await _reply(text.GetString() ?? "");
        }
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p)
        ? p.ValueKind == JsonValueKind.Number ? p.GetInt64().ToString() : p.GetString() ?? "" : "";
    public void Dispose() { _cts?.Cancel(); _cts?.Dispose(); _http.Dispose(); }
}
