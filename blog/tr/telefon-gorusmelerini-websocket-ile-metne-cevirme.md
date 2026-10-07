---
title: "Telefon görüşmelerini WebSocket ile canlı metne çevirme"
description: "Hipcall STT servisine WebSocket ile bağlanın, çağrı sırasındaki konuşmaları canlı olarak dinleyin ve kendi veritabanınıza aktarın."
slug: telefon-gorusmelerini-websocket-ile-metne-cevirme
lang: tr
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

## Genel bakış

Hipcall, aktif bir telefon görüşmesini konuşma tanıma (STT) motoruna gönderir ve üretilen metni WebSocket kanalı üzerinden istemcilere akıtır. Bu kanal sayesinde çağrı sürerken konuşma metnini anlık olarak alabilir, CRM'e yazabilir veya ekranda canlı önizleme gösterebilirsiniz.

Bu makalede bir C# (.NET 8) konsol uygulamasıyla WebSocket akışına bağlanacak, gelen parçaları birleştirip tam bir transkript oluşturacak ve olası hata senaryolarını ele alacaksınız.

Akışın genel döngüsü şöyledir:

1. WebSocket bağlantısını açın ve abone olun.
2. Sunucu, her konuşma parçası için `transcript.segment` olayı gönderir.
3. İstemci, parçaları `segment_id` anahtarıyla birleştirerek canlı metni oluşturur.
4. Çağrı bittiğinde `transcript.completed` olayı gelir ve nihai metin hazır olur.

## Başlamadan önce

Aşağıdaki gereksinimleri sağlayın:

- **.NET 8 SDK** veya üstü yüklü olmalıdır.
- Hipcall hesabınızda yönetici yetkisine sahip olmalısınız.

**Hipcall panelinde transkript ayarlarını etkinleştirin:**

1. **Ayarlar → Tüm Ayarlar → Yapay Zeka → Transkript** menüsünü açın.
2. **"Çağrıların transkriptini çıkar"** seçeneğini etkinleştirin.
3. Transkripti almak istediğiniz **yönü** seçin (gelen, giden veya her ikisi).
4. **"Metin ne zaman hazır olsun"** ayarını **"Çağrı sürerken"** olarak belirleyin.
5. **"Canlı transkript akışı (Geliştiriciler için)"** seçeneğini açın.

**API token'ınızı alın:**

1. **Ayarlar → Geliştirici → API Token'ları** sayfasına gidin.
2. Yeni bir token oluşturun veya mevcut bir token'ı kopyalayın.
3. Token'ı bir ortam değişkenine atayın:

```bash
export HIPCALL_API_TOKEN="buraya_tokeniniz"

$env:HIPCALL_API_TOKEN = "buraya_tokeniniz"
```

## Adım 1: Bağlantı ve abonelik

WebSocket uç noktası:

```
wss://stream.hipcall.com.tr/v1/stream
```

Kimlik bilgisini URL'ye parametre olarak eklemeyin. Token'ı HTTP yükseltme (upgrade) isteğinde `Authorization` başlığı ile gönderin:

```
Authorization: Bearer <api_token>
```

### Bağlantıyı açın

```csharp
using var ws = new ClientWebSocket();
ws.Options.SetRequestHeader("Authorization", $"Bearer {apiToken}");
await ws.ConnectAsync(new Uri("wss://stream.hipcall.com.tr/v1/stream"), ct);
```

### Abone olun

Bağlantı kurulduktan hemen sonra sunucuya abonelik mesajını gönderin:

```json
{"action": "subscribe", "v": 1}
```

```csharp
await SendMessageAsync(ws, "{\"action\": \"subscribe\", \"v\": 1}", ct);
```

### Bağlantıyı canlı tutun

Sunucu, belirli bir süre mesaj almazsa bağlantıyı düşürür. Bunu önlemek için arka planda ayrı bir `Task` olarak her **20 saniyede bir** ping gönderin:

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

## Adım 2: Transkripti yeniden kurma

Sunucu, transkript verilerini üç olay türüyle iletir:

| Olay | Ne zaman gelir | Anlamı |
|---|---|---|
| `transcript.started` | Çağrı başladığında | Yeni bir transkript oturumu açıldı. |
| `transcript.segment` | Konuşma devam ederken | Bir metin parçası üretildi veya güncellendi. |
| `transcript.completed` | Çağrı bittiğinde | Transkript tamamlandı, nihai metin hazır. |

Gelen veriyi doğru bir şekilde birleştirmek için iki altın kurala uymanız gerekir: **Çağrıları ayırmak** ve **segmentleri üzerine yazmak**.

### 1. Kural: Çağrıları `call_uuid` ile ayırın (Gruplama)

WebSocket bağlantınız hesaptaki tüm çağrıları aynı anda dinler. Yani aynı saniye içinde birbirinden tamamen farklı iki telefon görüşmesinin metinleri (örneğin A çağrısı ve B çağrısı) aynı akış üzerinden ardışık olarak gelebilir.

Eğer gelen verileri `call_uuid` değerine göre gruplamazsanız, farklı çağrıların metinleri birbirine karışır ve tamamen anlamsız bir diyalog ortaya çıkar. Bu yüzden tüm verileri bir `Dictionary` içinde anahtar olarak `call_uuid` kullanarak izlemelisiniz.

### 2. Kural: `segment_id` ile üzerine yazın (Upsert)

Her `transcript.segment` olayının `data` alanı şu bilgileri taşır:
- **`segment_id`** (int): Parçanın benzersiz sıra numarası.
- **`text`** (string): O parçaya ait güncel metin.
- **`is_final`** (bool): `true` ise bu parça kesinleşmiştir.

STT motoru, aynı kelimenin veya cümlenin gelişen hallerini aynı `segment_id` ile defalarca gönderir. Eğer gelen `text` verilerini doğrudan uç uca eklerseniz (concatenate), elinizde şöyle bozuk bir metin kalır:
> *"CCumCumarCumartesi."*

Bunun önüne geçmek için aynı `segment_id` geldiğinde **önceki metnin üzerine yazmalısınız (upsert).** Bu, konuşma tanıma motorunun geçici sonuçlarını (henüz kesinleşmemiş kelimeleri) mükemmel bir şekilde düzeltmesini sağlar.

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

Canlı metni ekranda göstermek istediğinizde, ilgili çağrının (`call_uuid`) tüm segment'lerini `segment_id` sırasına göre uç uca birleştirin:

```csharp
var fullText = string.Join(" ", segments.OrderBy(k => k.Key).Select(v => v.Value));
Console.WriteLine($"> {callUuid[..8]}: {fullText}");
```

### Konuşmacı ayrımı (Diarization)

Gelen `transcript.segment` olaylarının `data` kısmında `speaker` alanı da bulunur. Sağlayıcı konuşmacı ayrımı yapabiliyorsa bu alan `"1"`, `"2"` gibi etiketler taşır; yapamıyorsa `null` döner. 

Bu etiketler benzersiz bir kullanıcı kimliği **değildir** ve yalnızca o çağrı içinde bir tarafı diğerinden ayırmak için kullanılır (örneğin `"1"` arayan, `"2"` temsilci olabilir; başka bir çağrıda bu durum tam tersi olabilir). Etiketleri çağrılar arasında taşımayın. Bu veriyi kullanarak çağrıyı düz bir metin bloğu yerine karşılıklı bir diyalog (sohbet baloncukları) formatında saklayabilirsiniz.

Metni ekranda düz uç uca eklemek yerine, konuşmacıya göre gruplayarak bastırmak isterseniz segment sözlüğünüzü `Dictionary<int, (string Speaker, string Text)>` yapısında kurup şöyle bir algoritma kullanabilirsiniz:

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

### Çağrı tamamlandığında

`transcript.completed` olayı geldiğinde sunucu, nihai segmentlerin tamamını ve çağrı kaydına referans veren `cdr_uuid` değerini içerir. Bellekte tuttuğunuz yerel segmentler yerine **sunucunun yolladığı kesin listeyi** okuyun; çünkü bağlantınız anlık olarak kopmuşsa belirli segmentleri kaçırmış olabilirsiniz:

```csharp
if (type == "transcript.completed" && root.TryGetProperty("data", out var compData))
{
    string cdrUuid = compData.TryGetProperty("cdr_uuid", out var cdrProp) ? cdrProp.GetString() ?? "" : "";
    Console.WriteLine($"[ÇAĞRI TAMAMLANDI] {callUuid} (CDR: {cdrUuid})");

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

`transcript.failed` olayı geldiğinde de o çağrıya ait sözlüğü bellekten temizleyin; aksi halde uzun süre çalışan istemcilerde bellek şişer:

```csharp
if (type == "transcript.failed")
{
    Console.WriteLine($"[TRANSKRİPT BAŞARISIZ] {callUuid}");
    _calls.Remove(callUuid);
}
```

## Betiğin tamamı

Aşağıda tüm adımları içeren, `dotnet run` ile doğrudan çalıştırabileceğiniz tam Program.cs dosyasını bulabilirsiniz:

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

## Hata aldığınızda

### 1008 kapanma kodu

Sunucu `1008 Policy Violation` kodu ile bağlantıyı kapatırsa, token iptal edilmiş veya süresi dolmuştur. Bu durumda yeniden bağlanmayı denemeyin; panelden yeni bir token oluşturun.

### 401 / 403 bağlantı reddi

Bu hatalar HTTP yükseltme (upgrade) aşamasında döner. Token geçersiz, eksik veya ilgili özellik hesabınızda aktif değil demektir. Reddin sebebi yanıt gövdesinde yer alır; mesajı okuyarak sorunu tespit edin.

### `seq` boşluğu veya geriye gitme

Her WebSocket zarfı artan bir `seq` (sıra numarası) taşır.

- **`seq` geriye giderse:** Sunucu aynı mesajı tekrar teslim etmiştir (re-delivery). Bu durumda mesajı işleyin ancak çıktıyı çoğaltmayın; upsert mantığı bunu otomatik olarak çözer.
- **`seq` arasında boşluk varsa:** Canlı önizleme bağlantısı geçici olarak düşmüştür. Tamamlanmış metin (`transcript.completed`) yine de ulaşacaktır, dolayısıyla nihai veri kaybı yaşanmaz.

### Ekrana metin akmıyor ancak bağlantı açık

Eğer bağlantı başarılı (`action: subscribed`) yanıtı alıyorsanız ancak çağrı sırasında ekrana metin düşmüyorsa, Hipcall sunucusu bağlantıyı açık tutsa da akış göndermiyor demektir. Bunun başlıca sebepleri şunlardır:

1. **Dahili (Internal) Çağrılar:** Eğer kendi ofis içi dahilinizden başka bir dahiliyi arıyorsanız ve Hipcall panelinde "Dahili Çağrılar" seçeneği işaretli değilse, bu çağrılar dinlenmez.
2. **Karşı Taraf Açmadıysa:** Transkript oturumu telefon çalarken veya robot (IVR) konuşurken başlamaz; taraflar birbirine bağlandığında (karşı taraf telefonu açtığında) başlar.
3. **Sessizlik:** Eğer telefonu açıp hiç konuşmazsanız (veya sessize alırsanız), STT motoru insan sesi duyana kadar `transcript.segment` (parça) üretmez.

### Sunucu Hata Kodları (Action Errors)

Bağlantı açıkken sunucu bir işlem yapamazsa `{"action": "error", "code": "...", "message": "..."}` formatında hata döner. Karşılaşabileceğiniz hata kodları şunlardır:

| Hata Kodu (`code`) | Anlamı |
|---|---|
| `malformed_json` | Gönderdiğiniz çerçeve geçerli JSON değil. |
| `missing_action` | Gönderilen JSON içinde `action` alanı eksik. |
| `unknown_action` | `action` değeri sunucu tarafından tanınmadı. |
| `invalid_version` | `v` değeri tam sayı (integer) olmalıdır. |
| `unsupported_version` | İstenen `v` sürümü sunucunun desteklediği sürümden büyük veya küçük. |
| `already_subscribed` | Bağlantı zaten abone. Her bağlantı için yalnızca bir abonelik açılabilir. |
| `unsupported_frame` | Metin (text) yerine ikili (binary) çerçeve gönderildi. |
| `feed_unavailable` | Abonelik şu an kurulamadı; kısa bir süre bekleyip (geri çekilerek) tekrar deneyin. |
| `too_slow` | İstemci çok geride kaldı, mesajları okumadığınız için sunucu bağlantıyı kapattı. |
| `forbidden` | Hesap canlı akışa yetkili değil (Panel ayarlarını kontrol edin). |
| `revoked` | Kullanılan API token iptal edilmiş. |
| `expired` | Kullanılan API token'ın süresi dolmuş. |

## Parametre listesi

Aşağıdaki tablo, WebSocket zarfındaki (envelope) tüm alanları açıklar:

| Alan | Tür | Açıklama |
|---|---|---|
| `v` | int | Protokol sürümü. Şu an tek geçerli değer `1` dir. |
| `type` | string | Olayın türü. `transcript.started`, `transcript.segment`, `transcript.failed` veya `transcript.completed` değerlerinden birini alır. Kontrol mesajlarında (ping/pong) bu alan bulunmaz; yerine `action` alanı gelir. |
| `seq` | int | Sunucu tarafından atanan artan sıra numarası. Mesaj sıralaması ve kayıp tespiti için kullanın. |
| `call_uuid` | string | Çağrının benzersiz kimliği (UUID v4). Segment'leri hangi çağrıya ait olduğuna göre gruplamak için kullanın. |
| `account_id` | int | Hesabın benzersiz kimliği. Birden fazla hesap dinliyorsanız verileri ayırmak için kullanın. |
| `ts` | string | Olayın oluşturulma zamanı (ISO 8601 formatında). |
| `data` | object | Olay türüne göre değişen yük (payload). Aşağıda her olay tipi için ayrıntılanmıştır. |

### `transcript.started` — data alanları

| Alan | Tür | Açıklama |
|---|---|---|
| `language` | string | Konuşma tanımanın hedef dili (örn. `"tr"`, `"en"`). |
| `direction` | string | Çağrının yönü: `"inbound"`, `"outbound"` veya `"internal"`. |
| `from` | string | Arayan numara. |
| `to` | string | Aranan numara. |

### `transcript.segment` — data alanları

| Alan | Tür | Açıklama |
|---|---|---|
| `segment_id` | int | Parçanın benzersiz sıra numarası. |
| `speaker` | string\|null | Konuşmacı etiketi (`"1"`, `"2"` vb.) veya `null`. |
| `start_ms` | int | Parçanın çağrı başlangıcına göre başlama zamanı (milisaniye). |
| `end_ms` | int | Parçanın çağrı başlangıcına göre bitiş zamanı (milisaniye). |
| `text` | string | Parçaya ait güncel metin. |
| `is_final` | bool | `true` ise parça kesinleşmiştir ve bir daha değişmez. |

### `transcript.completed` — data alanları

| Alan | Tür | Açıklama |
|---|---|---|
| `cdr_uuid` | string | Çağrı kaydının (CDR) benzersiz kimliği. Metni kendi veritabanınızdaki çağrı kaydına bağlamak için kullanın. |
| `segments` | array | Nihai segment listesi. Her eleman `segment_id`, `speaker`, `start_ms`, `end_ms`, `text` ve `is_final` içerir. |

## Sonraki adımlar

- **Veritabanına kayıt:** `transcript.completed` olayında nihai metni PostgreSQL, MongoDB veya tercih ettiğiniz veritabanına yazın.
- **Duygu analizi:** Tamamlanmış transkript üzerinde bir NLP modeli çalıştırarak müşteri memnuniyetini ölçün.
- **Yeniden bağlanma stratejisi:** Üretim ortamında bağlantı kopmalarına karşı üstel geri çekilme (exponential backoff) ile otomatik yeniden bağlanma mekanizması ekleyin.
