---
title: "How to Stream Speech-to-Text via WebSocket"
description: "Connect to the Hipcall STT service via WebSocket, listen to live conversations during a call, and stream the transcript to your own system."
slug: how-to-stream-speech-to-text-via-websocket
lang: en
locales: [en, tr]
pubDate: 2026-10-07
categories: [developers]
intent: informational
translationKey: how-to-stream-speech-to-text-via-websocket
tags: [api, stt, websocket, transcription, dotnet]
authors: [hipcall-team]
featured: false
draft: true
task: 11
status: draft
---

## Overview

Hipcall sends active phone calls to its speech-to-text (STT) engine and streams the resulting text to clients over a WebSocket channel. Through this channel you can receive the conversation text in real time, write it to your CRM, or display a live preview on screen.

In this guide you will connect to the WebSocket stream with a C# (.NET 8) console application, merge incoming segments into a complete transcript, and handle possible error scenarios.

The overall flow works as follows:

1. Open the WebSocket connection and subscribe.
2. The server sends a `transcript.segment` event for each speech fragment.
3. The client merges fragments by `segment_id` key to build the live text.
4. When the call ends, a `transcript.completed` event arrives and the final text is ready.

## Before you start

Ensure the following prerequisites are met:

- **.NET 8 SDK** or later is installed.
- You have administrator privileges on your Hipcall account.

**Enable transcript settings in the Hipcall dashboard:**

1. Navigate to **Settings → All Settings → AI → Transcription**.
2. Turn on **"Transcribe calls"**.
3. Select the **direction** you want to transcribe (inbound, outbound, or both).
4. Set **"When should the text be ready"** to **"During the call"**.
5. Turn on **"Live transcript stream (For developers)"**.

**Obtain your API token:**

1. Go to **Settings → Developer → API Tokens**.
2. Create a new token or copy an existing one.
3. Assign the token to an environment variable:

```bash
export HIPCALL_API_TOKEN="your_token_here"

$env:HIPCALL_API_TOKEN = "your_token_here"
```

## Step 1: Connection and subscription

WebSocket endpoint:

```
wss://stream.hipcall.com/v1/stream
```

Do not append credentials to the URL. Send the token in the `Authorization` header during the HTTP upgrade request:

```
Authorization: Bearer <api_token>
```

### Open the connection

```csharp
using var ws = new ClientWebSocket();
ws.Options.SetRequestHeader("Authorization", $"Bearer {apiToken}");
await ws.ConnectAsync(new Uri("wss://stream.hipcall.com/v1/stream"), ct);
```

### Subscribe

Send the subscription message to the server immediately after the connection is established:

```json
{"action": "subscribe", "v": 1}
```

```csharp
await SendMessageAsync(ws, "{\"action\": \"subscribe\", \"v\": 1}", ct);
```

### Keep the connection alive

The server drops the connection if it receives no messages within a certain period. To prevent this, send a ping every **20 seconds** in a separate background `Task`:

```json
{"action": "ping"}
```

```csharp
private static async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
{
    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct);
        await SendMessageAsync(ws, "{\"action\": \"ping\"}", ct);
    }
}
```

## Step 2: Reconstructing the transcript

The server delivers transcript data through three event types:

| Event | When it arrives | Meaning |
|---|---|---|
| `transcript.started` | When the call begins | A new transcript session has opened. |
| `transcript.segment` | While the conversation continues | A text fragment has been produced or updated. |
| `transcript.completed` | When the call ends | The transcript is complete and the final text is ready. |

To correctly construct the text from the incoming data, you must follow two golden rules: **Group by call** and **Upsert segments**.

### Rule 1: Group by `call_uuid`

Your WebSocket connection listens to all calls on the account simultaneously. This means that within the same second, text segments from two completely different active phone calls (e.g., Call A and Call B) can arrive sequentially over the same stream.

If you do not group the incoming data by the `call_uuid` value, the text from different calls will mix, resulting in a completely nonsensical dialogue. Therefore, you must track all incoming data in a `Dictionary` using `call_uuid` as the key.

### Rule 2: Overwrite by `segment_id` (Upsert)

The `data` field of each `transcript.segment` event carries the following properties:
- **`segment_id`** (int): The unique sequence number of the fragment.
- **`text`** (string): The current text for that fragment.
- **`is_final`** (bool): When `true`, this fragment is finalized.

The STT engine will send multiple updates for the same word or sentence using the same `segment_id`. If you concatenate the incoming `text` data end to end, you will end up with corrupted overlapping text like this:
> *"SSatSaturSaturday."*

To prevent this, whenever a payload with an existing `segment_id` arrives, you must **overwrite the previous text (upsert).** This allows the speech recognition engine to perfectly correct its temporary results (words that haven't been finalized yet).

```csharp
private static readonly Dictionary<string, Dictionary<int, string>> _calls = new();

string callUuid = root.GetProperty("call_uuid").GetString();
int segmentId = data.GetProperty("segment_id").GetInt32();
string text = data.GetProperty("text").GetString() ?? "";

if (!_calls.TryGetValue(callUuid, out var segments))
{
    segments = new Dictionary<int, string>();
    _calls[callUuid] = segments;
}

segments[segmentId] = text;
```

When you want to display the live text, join all segments of the respective call (`call_uuid`) ordered by `segment_id`:

```csharp
var fullText = string.Join(" ", segments.OrderBy(k => k.Key).Select(v => v.Value));
Console.WriteLine($"> {callUuid[..8]}: {fullText}");
```

### Speaker diarization

The `data` field of incoming `transcript.segment` events also contains a `speaker` property. If the provider supports speaker diarization, this field carries labels like `"1"` or `"2"`; otherwise, it returns `null`.

These labels are **not** unique user identifiers. They are only meaningful within that specific call to distinguish one side from the other (for instance, `"1"` might be the caller and `"2"` the agent, while in another call it could be reversed). Do not carry these labels across different calls. You can use this data to store the conversation in a dialogue format (like chat bubbles) rather than a flat text block.

To group the printed text by speaker instead of a flat concatenation, you can store your segments as a `Dictionary<int, (string Speaker, string Text)>` and apply a grouping algorithm like this:

```csharp
var grouped = new List<string>();
string currentSpeaker = null;
List<string> currentTexts = new();

foreach (var seg in segments.OrderBy(k => k.Key).Select(v => v.Value))
{
    if (seg.Speaker != currentSpeaker)
    {
        if (currentSpeaker != null)
            grouped.Add($"[{currentSpeaker}]: {string.Join(" ", currentTexts)}");
        
        currentSpeaker = seg.Speaker;
        currentTexts.Clear();
    }
    currentTexts.Add(seg.Text);
}

if (currentSpeaker != null)
    grouped.Add($"[{currentSpeaker}]: {string.Join(" ", currentTexts)}");

Console.WriteLine(string.Join("\n", grouped));
```

### When the call completes

The `transcript.completed` event contains the definitive list of all segments along with a `cdr_uuid` that references the call detail record. Read the **server’s authoritative segment list** instead of relying on your local buffer, because you may have missed segments if your connection momentarily dropped:

```csharp
if (type == "transcript.completed" && root.TryGetProperty("data", out var compData))
{
    string cdrUuid = compData.TryGetProperty("cdr_uuid", out var cdrProp) ? cdrProp.GetString() ?? "" : "";
    Console.WriteLine($"[CALL COMPLETED] {callUuid} (CDR: {cdrUuid})");

    if (compData.TryGetProperty("segments", out var segsArr))
    {
        var finalSegments = new Dictionary<int, (string Speaker, string Text)>();
        foreach (var seg in segsArr.EnumerateArray())
        {
            int sid = seg.GetProperty("segment_id").GetInt32();
            string stxt = seg.GetProperty("text").GetString() ?? "";
            string sspk = seg.TryGetProperty("speaker", out var sskP) && sskP.ValueKind != JsonValueKind.Null ? sskP.GetString() ?? "?" : "?";
            finalSegments[sid] = (sspk, stxt);
        }
        PrintSegments(callUuid, finalSegments);
    }
    _calls.Remove(callUuid);
}
```

When `transcript.failed` arrives, also remove that call from the dictionary to prevent memory bloat on long-running clients:

```csharp
if (type == "transcript.failed")
{
    Console.WriteLine($"[TRANSCRIPT FAILED] {callUuid}");
    _calls.Remove(callUuid);
}
```

## The full script

Below is the complete Program.cs file containing all the steps above, ready to run with `dotnet run`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Hipcall.SttStreamClient;

class Program
{
    private const string WsUrl = "wss://stream.hipcall.com/v1/stream";
    private static readonly Dictionary<string, Dictionary<int, (string Speaker, string Text)>> _calls = new();

    static async Task Main(string[] args)
    {
        string? apiToken = Environment.GetEnvironmentVariable("HIPCALL_API_TOKEN");

        if (string.IsNullOrWhiteSpace(apiToken))
        {
            Console.WriteLine("ERROR: HIPCALL_API_TOKEN environment variable not found.");
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
            Console.WriteLine($"Connecting: {WsUrl}");
            await ws.ConnectAsync(new Uri(WsUrl), cts.Token);
            Console.WriteLine("Connection successful. Listening to STT stream (Press Ctrl+C to exit)...");

            await SendMessageAsync(ws, "{\"action\": \"subscribe\", \"v\": 1}", cts.Token);

            var pingTask = PingLoopAsync(ws, cts.Token);
            var receiveTask = ReceiveLoopAsync(ws, cts.Token);

            await Task.WhenAny(pingTask, receiveTask);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nClient stopped.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nConnection Error: {ex.Message}");
        }
        finally
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
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
                    Console.WriteLine("Server closed the connection.");
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
            Console.WriteLine($"Receive Error: {ex.Message}"); 
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
                Console.WriteLine($"[SERVER]: {message}");
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
                Console.WriteLine($"\n[CALL STARTED] {callUuid} ({dir}, {lang}, {from} → {to})");
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
                Console.WriteLine($"\n[TRANSCRIPT FAILED] {callUuid}");
                _calls.Remove(callUuid);
            }
            else if (type == "transcript.completed" && root.TryGetProperty("data", out var compData))
            {
                string cdrUuid = compData.TryGetProperty("cdr_uuid", out var cdrProp) ? cdrProp.GetString() ?? "" : "";
                Console.WriteLine($"\n[CALL COMPLETED] {callUuid} (CDR: {cdrUuid}) Final Text:");

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
        string currentSpeaker = null;
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
```

## When it fails

### Close code 1008

If the server closes the connection with `1008 Policy Violation`, the token has been revoked or has expired. Do not attempt to reconnect; generate a new token from the dashboard.

### 401 / 403 connection rejection

These errors occur during the HTTP upgrade phase. The token is invalid, missing, or the feature is not active on your account. The reason is included in the response body; read the message to identify the issue.

### `seq` gaps or rollbacks

Every WebSocket envelope carries an incrementing `seq` (sequence number).

- **`seq` goes backward:** The server has re-delivered the same message. Process the message but do not duplicate output; the upsert logic handles this automatically.
- **`seq` has a gap:** The live preview connection dropped temporarily. The completed text (`transcript.completed`) will still arrive, so there is no final data loss.

### No text on screen while connection is open

If the connection is successful (`action: subscribed`) but no text appears during a call, the Hipcall server is keeping the connection open but not sending a stream. The most common reasons are:

1. **Internal Calls:** If you are calling an internal extension and "Internal calls" is not checked in the Hipcall dashboard settings, the call will not be transcribed.
2. **Call Not Answered:** The transcript session does not start while the phone is ringing or an IVR menu is playing; it only starts when two human parties are connected.
3. **Total Silence:** If the call is connected but both parties remain silent (or muted), the STT engine will not produce a `transcript.segment` until human speech is detected.

### Server Error Codes (Action Errors)

While the connection is open, if the server encounters an issue processing your request, it returns an error in the format `{"action": "error", "code": "...", "message": "..."}`. Possible error codes are:

| Error Code (`code`) | Meaning |
|---|---|
| `malformed_json` | The frame sent is not valid JSON. |
| `missing_action` | The `action` field is missing from the sent JSON. |
| `unknown_action` | The `action` value is not recognized by the server. |
| `invalid_version` | The `v` field must be an integer. |
| `unsupported_version` | The requested `v` version is unsupported by the server. |
| `already_subscribed` | The connection is already subscribed. One connection equals one subscription. |
| `unsupported_frame` | A binary frame was sent instead of a text frame. |
| `feed_unavailable` | The subscription could not be established right now; back off and try again. |
| `too_slow` | The client fell too far behind (not reading messages fast enough) and the connection was closed. |
| `forbidden` | The account is not authorized for live streaming (check your dashboard settings). |
| `revoked` | The API token is no longer valid. |
| `expired` | The API token has expired. |

## Parameter reference

The table below describes all fields in the WebSocket envelope:

| Field | Type | Description |
|---|---|---|
| `v` | int | Protocol version. The only valid value is `1`. |
| `type` | string | The event type. One of `transcript.started`, `transcript.segment`, `transcript.failed`, or `transcript.completed`. Control messages (ping/pong) do not include this field; they carry an `action` field instead. |
| `seq` | int | Server-assigned incrementing sequence number. Use it for message ordering and gap detection. |
| `call_uuid` | string | The unique identifier of the call (UUID v4). Use it to group segments by call. |
| `account_id` | int | The unique identifier of the account. Use it to separate data when listening across multiple accounts. |
| `ts` | string | The timestamp when the event was created (ISO 8601 format). |
| `data` | object | The payload, which varies by event type. Detailed below for each event type. |

### `transcript.started` — data fields

| Field | Type | Description |
|---|---|---|
| `language` | string | The target language for speech recognition (e.g., `"tr"`, `"en"`). |
| `direction` | string | The direction of the call: `"inbound"`, `"outbound"`, or `"internal"`. |
| `from` | string | The calling number. |
| `to` | string | The called number. |

### `transcript.segment` — data fields

| Field | Type | Description |
|---|---|---|
| `segment_id` | int | The unique sequence number of the fragment. |
| `speaker` | string\|null | Speaker label (`"1"`, `"2"`, etc.) or `null`. |
| `start_ms` | int | The fragment’s start time relative to the call start (milliseconds). |
| `end_ms` | int | The fragment’s end time relative to the call start (milliseconds). |
| `text` | string | The current text for the fragment. |
| `is_final` | bool | When `true`, the fragment is finalized and will not change again. |

### `transcript.completed` — data fields

| Field | Type | Description |
|---|---|---|
| `cdr_uuid` | string | The unique identifier of the Call Detail Record (CDR). Use it to link the text to the call record in your database. |
| `segments` | array | The definitive segment list. Each element contains `segment_id`, `speaker`, `start_ms`, `end_ms`, `text`, and `is_final`. |

## Next steps

- **Persist to a database:** Write the final text to PostgreSQL, MongoDB, or your preferred database when the `transcript.completed` event arrives.
- **Sentiment analysis:** Run an NLP model on the completed transcript to measure customer satisfaction.
- **Reconnection strategy:** In production, implement an automatic reconnection mechanism with exponential backoff to handle connection drops.
