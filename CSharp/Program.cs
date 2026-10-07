using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HipcallSTTWebsocketExample;

class Program
{
    private const string WsUrl = "wss://stream.hipcall.com.tr/v1/stream";
    private static readonly Dictionary<string, Dictionary<int, (string Speaker, string Text)>> _calls = new();

    static async Task Main(string[] args)
    {
        string? apiToken = Environment.GetEnvironmentVariable("HIPCALL_API_TOKEN");

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            Console.WriteLine("HATA: HIPCALL_API_TOKEN çevre değişkeni bulunamadı.");
            return;
        }

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {apiToken}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => 
        { 
            e.Cancel = true; 
            cts.Cancel(); 
        };

        try
        {
            Console.WriteLine($"Bağlanılıyor: {WsUrl}");
            await ws.ConnectAsync(new Uri(WsUrl), cts.Token);
            Console.WriteLine("Bağlantı başarılı. STT akışı dinleniyor (Çıkmak için Ctrl+C)...");

            await SendMessageAsync(ws, "{\"action\": \"subscribe\", \"v\": 1}", cts.Token);

            var pingTask = PingLoopAsync(ws, cts.Token);
            var receiveTask = ReceiveLoopAsync(ws, cts.Token);

            await Task.WhenAny(pingTask, receiveTask);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nİstemci durduruldu.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nBağlantı Hatası: {ex.Message}");
        }
        finally
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Kapanıyor", CancellationToken.None);
            }
        }
    }

    private static async Task SendMessageAsync(ClientWebSocket ws, string message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private static async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                await SendMessageAsync(ws, "{\"action\": \"ping\"}", ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private static async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine("Sunucu bağlantıyı kapattı.");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    ProcessMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            Console.WriteLine($"Dinleme Hatası: {ex.Message}"); 
        }
    }

    private static void ProcessMessage(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;

            if (root.TryGetProperty("action", out var actionProp))
            {
                Console.WriteLine($"[SUNUCU YANITI]: {message}");
                return;
            }

            if (!root.TryGetProperty("type", out var typeProp) || 
                !root.TryGetProperty("call_uuid", out var callUuidProp))
            {
                return;
            }

            string type = typeProp.GetString() ?? "";
            string callUuid = callUuidProp.GetString() ?? "";

            if (type == "transcript.started" && root.TryGetProperty("data", out var startData))
            {
                string lang = startData.TryGetProperty("language", out var lp) ? lp.GetString() ?? "" : "";
                string dir  = startData.TryGetProperty("direction", out var dp) ? dp.GetString() ?? "" : "";
                string from = startData.TryGetProperty("from", out var fp) ? fp.GetString() ?? "" : "";
                string to   = startData.TryGetProperty("to", out var tp) ? tp.GetString() ?? "" : "";
                Console.WriteLine($"\n[ÇAĞRI BAŞLADI] {callUuid} ({dir}, {lang}, {from} → {to})");
                _calls[callUuid] = new Dictionary<int, (string Speaker, string Text)>();
            }
            else if (type == "transcript.segment" && root.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("segment_id", out var idProp) && 
                    data.TryGetProperty("text", out var textProp))
                {
                    int segmentId = idProp.GetInt32();
                    string text = textProp.GetString() ?? "";

                    string speaker = data.TryGetProperty("speaker", out var spProp) && spProp.ValueKind != JsonValueKind.Null 
                        ? spProp.GetString() ?? "?" 
                        : "?";

                    if (!_calls.TryGetValue(callUuid, out var segments))
                    {
                        segments = new Dictionary<int, (string Speaker, string Text)>();
                        _calls[callUuid] = segments;
                    }

                    segments[segmentId] = (speaker, text);
                    PrintSegments(callUuid, segments);
                }
            }
            else if (type == "transcript.failed")
            {
                Console.WriteLine($"\n[TRANSKRİPT BAŞARISIZ] {callUuid}");
                _calls.Remove(callUuid);
            }
            else if (type == "transcript.completed" && root.TryGetProperty("data", out var compData))
            {
                string cdrUuid = compData.TryGetProperty("cdr_uuid", out var cdrProp) ? cdrProp.GetString() ?? "" : "";
                Console.WriteLine($"\n[ÇAĞRI TAMAMLANDI] {callUuid} (CDR: {cdrUuid}) Nihai Metin:");

                if (compData.TryGetProperty("segments", out var segsArr) && segsArr.ValueKind == JsonValueKind.Array)
                {
                    var finalSegments = new Dictionary<int, (string Speaker, string Text)>();
                    foreach (var seg in segsArr.EnumerateArray())
                    {
                        int sid = seg.TryGetProperty("segment_id", out var sidP) ? sidP.GetInt32() : 0;
                        string stxt = seg.TryGetProperty("text", out var stP) ? stP.GetString() ?? "" : "";
                        string sspk = seg.TryGetProperty("speaker", out var sskP) && sskP.ValueKind != JsonValueKind.Null ? sskP.GetString() ?? "?" : "?";
                        finalSegments[sid] = (sspk, stxt);
                    }
                    PrintSegments(callUuid, finalSegments);
                }
                else if (_calls.TryGetValue(callUuid, out var fallbackSegs))
                {
                    PrintSegments(callUuid, fallbackSegs);
                }
                _calls.Remove(callUuid);
                Console.WriteLine("--------------------------------------------------\n");
            }
        }
        catch (JsonException) { }
    }

    private static void PrintSegments(string callUuid, Dictionary<int, (string Speaker, string Text)> segments)
    {
        var grouped = new List<string>();
        string? currentSpeaker = null;
        List<string> currentTexts = new();

        foreach (var seg in segments.OrderBy(k => k.Key).Select(v => v.Value))
        {
            if (seg.Speaker != currentSpeaker)
            {
                if (currentSpeaker != null)
                {
                    grouped.Add($"[{currentSpeaker}]: {string.Join(" ", currentTexts)}");
                }
                currentSpeaker = seg.Speaker;
                currentTexts.Clear();
            }
            currentTexts.Add(seg.Text);
        }

        if (currentSpeaker != null)
        {
            grouped.Add($"[{currentSpeaker}]: {string.Join(" ", currentTexts)}");
        }

        string shortId = callUuid.Length >= 8 ? callUuid[..8] : callUuid;
        Console.WriteLine($"> {shortId}: {string.Join(" | ", grouped)}");
    }
}
