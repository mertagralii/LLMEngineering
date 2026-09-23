# 🧠 LlmProduction — IChatClient'ı katmanlarla sarmak: retry, cache, log, prompt caching

## 📖 Nedir?

Dokuz modüldür hep şunu yazdık:

```csharp
var response = await chat.GetResponseAsync(messages, options);
```

Bu satır kendi bilgisayarımda çalışıyor. 1000 kullanıcının kullandığı bir sunucuya koyunca dört şey oluyor:

| Olan şey | Bu satırın tepkisi | Olması gereken |
|---|---|---|
| Anthropic "çok istek attın, 429" dedi | **Exception → uygulama çöker** | 1 saniye bekle, tekrar dene |
| 100 kullanıcı aynı soruyu sordu | 100 kez ödersin | 1 kez öde, 99'unu hafızadan ver |
| Bir istek 8 saniye sürdü | Kimse fark etmez | Log'a düşsün, grafikte görünsün |
| System prompt 6000 token ve her istekte aynı | Her seferinde tam ücret | Sunucuda önbelleklensin |

Modül 9 bu dördünü çözüyor. **Yapay zekâyla ilgili değil** — dayanıklılık, önbellek, gözlemlenebilirlik. Normal backend işleri, LLM'e uygulanmış hâli.

### Tek fikir: sarmalama

Bunu ASP.NET Core'dan zaten biliyorsun:

```csharp
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

İstek yukarıdan aşağı iner, cevap aşağıdan yukarı çıkar. `IChatClient` için birebir aynısı var:

```
GetResponseAsync() çağrıldı
   ↓  UseLogging              "istek geldi" diye yaz
   ↓  UseOpenTelemetry        span aç, süreyi ölç
   ↓  UseDistributedCache     bu soruyu daha önce gördüm mü?
   ↓                          ├─ EVET → cevabı ver, DUR
   ↓                          └─ HAYIR → devam
   ↓  RetryChatClient         dene; patlarsa bekle, tekrar dene
   ↓  AnthropicClient         gerçek HTTP isteği
```

Bu neden mümkün? **`IChatClient` bir arayüz.** Onu uygulayan bir sınıf yazıp içine başka bir `IChatClient` koyabiliyorsun. Dışarıdan bakan hâlâ tek bir `IChatClient` görüyor — kaç katman olduğunu bilmiyor.

Buna **decorator pattern** deniyor. Modül 4'te `UseFunctionInvocation()` yazmıştık; o da bu zincirin bir halkasıydı, üzerinde durmamıştık.

### Beş kavram

| Kavram | Ne yapar |
|---|---|
| **Zincir ve SIRA** | İlk yazılan en dışta. Sıra yanlışsa cache hit retry bütçesi yakar |
| **`RetryChatClient`** | Kendi yazdığımız katman. Geçici hatada backoff + jitter, max 3 deneme |
| **Kalıcı/geçici ayrımı** | 429 ve 5xx tekrar denenir; 400 ve 401 **denenmez** |
| **Yanıt önbelleği** | Aynı soru + aynı options → ikinci çağrı ağa hiç çıkmaz |
| **Prompt caching** | Uzun system prompt Anthropic'in sunucusunda önbelleklenir |

💡 **Kısacası:** Modül 0–8'de "çalışıyor mu" diye baktık. Burada ilk kez "**bozulduğunda ne olur, kaça mal olur, nasıl görürüm**" diye sorduk.

## 🧩 Nasıl çalışır?

Dört komutlu bir CLI:

```bash
dotnet run -- retry         # sahte istemciyle retry testi
dotnet run -- chain         # tüm katmanlar açık, tek soru
dotnet run -- cache         # aynı soru iki kez
dotnet run -- promptcache   # uzun system prompt, iki çağrı
```

| Komut | API çağrısı | Ne gösterir |
|---|---|---|
| `retry` | **0** | 429 → 3 deneme, 400 → 1 deneme |
| `chain` | 1 | Log satırları + `gen_ai.*` span etiketleri |
| `cache` | 2 | 1153 ms → 3 ms |
| `promptcache` | 2 | 7.753 token sunucudan okundu |

## 🔧 1. Gerekli paketler

```bash
dotnet new console -n Modul-9-LlmProduction
dotnet add package Anthropic
dotnet add package Microsoft.Extensions.AI
dotnet add package Microsoft.Extensions.Logging.Console      # ILoggerFactory
dotnet add package Microsoft.Extensions.Caching.Memory       # MemoryDistributedCache
dotnet add package OpenTelemetry.Exporter.Console            # span'leri ekranda görmek
```

Son iki paket **sadece görünürlük için**. `UseLogging` bir `ILoggerFactory` istiyor; `UseOpenTelemetry` exporter olmadan span üretir ama kimse okumaz — özellik görünmez kalır.

## ⚙️ 2. Ayarlar

### `.csproj`'da kopyalama satırı yok

Modül 8'deki gibi dosyalar proje kökünde kalıyor, program `AppContext.BaseDirectory`'den yukarı çıkarak ulaşıyor:

```csharp
var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
```

### Çalıştırma

```bash
dotnet run --project Modul-9-LlmProduction -- retry
dotnet run --project Modul-9-LlmProduction -- chain
```

`Properties/launchSettings.json` içinde dört profil var; IDE'de ▶ butonunun yanındaki listeden seçilir:

```json
{
  "profiles": {
    "retry":       { "commandName": "Project", "commandLineArgs": "retry" },
    "chain":       { "commandName": "Project", "commandLineArgs": "chain" },
    "cache":       { "commandName": "Project", "commandLineArgs": "cache" },
    "promptcache": { "commandName": "Project", "commandLineArgs": "promptcache" }
  }
}
```

`retry` profili **API key gerektirmez** — sahte istemciyle çalışır.

## 💻 3. Kullanım

### En basit hâli — çalışan iskelet

Decorator fikrinin tamamı bu kadar. **API key gerekmiyor**: gerçek API yerine yavaş ama sahte bir istemci var.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

IDistributedCache cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

// ZİNCİR: ilk yazılan EN DIŞTA.
IChatClient chat = new SlowChatClient()
    .AsBuilder()
    .Use(inner => new TimingChatClient(inner))   // 1. en DIŞTA — her çağrıyı ölçer
    .UseDistributedCache(cache)                  // 2. en İÇTE  — cache hit burada döner
    .Build();

var soru = new List<ChatMessage> { new(ChatRole.User, "merhaba") };
var options = new ChatOptions { MaxOutputTokens = 50 };

await chat.GetResponseAsync(soru, options);   // 1. çağrı: cache boş
await chat.GetResponseAsync(soru, options);   // 2. çağrı: cache'ten

// --- Kendi katmanımız: çağrının ne kadar sürdüğünü ölçer ---
class TimingChatClient : DelegatingChatClient
{
    public TimingChatClient(IChatClient innerClient) : base(innerClient)
    {
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var started = DateTime.Now;

        var response = await base.GetResponseAsync(messages, options, cancellationToken);

        Console.WriteLine($"[timing] {(DateTime.Now - started).TotalMilliseconds,6:F0} ms   cevap: {response.Text}");

        return response;
    }
}

// --- Gerçek API yerine: yavaş ama sahte istemci ---
class SlowChatClient : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await Task.Delay(800, cancellationToken);   // ağ gecikmesi taklidi
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, "selam"));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    public void Dispose()
    {
    }
}
```

Çıktısı:

```
[timing]    885 ms   cevap: selam
[timing]      2 ms   cevap: selam
```

**İki satırda modülün tamamı var.** İkinci çağrı `SlowChatClient`'a hiç inmedi — `UseDistributedCache` onu durdurdu. `TimingChatClient` yine de çalıştı, çünkü cache'in **dışında**.

`DelegatingChatClient`'tan türeyen `TimingChatClient` 12 satır. Sarmalamak bu kadar ucuz.

`SlowChatClient` ise `IChatClient`'ı **elle** uyguluyor — çünkü zincirin **en dibi**, sarmalayacağı iç katman yok.

### Bu iskeletten tam koda giden yol

| Ne eklendi | Neden |
|---|---|
| `AnthropicClient` | Sahte yerine gerçek API |
| `RetryChatClient` | 429 ve 5xx'te tekrar dene |
| `UseLogging` | İstek/cevap log'a düşsün |
| `UseOpenTelemetry` + console exporter | Süre ve token span'de görünsün |
| `RawRepresentationFactory` + `CacheControlEphemeral` | Sunucu tarafı prompt caching |
| `args` ile komut ayrıştırma | Dört demo tek programda |
| `FlakyChatClient` | Retry'ı bedava ve tekrarlanabilir test etmek |

Tamamı `Program.cs` + `RetryChatClient.cs`.

### 🔴 SIRA — modülün en önemli tablosu

```csharp
client.AsIChatClient("claude-haiku-4-5")
    .AsBuilder()
    .UseLogging(loggerFactory)                        // 1. en DIŞTA
    .UseOpenTelemetry(loggerFactory, SourceName)      // 2.
    .UseDistributedCache(cache)                       // 3.
    .Use(inner => new RetryChatClient(inner, 3))      // 4. en İÇTE
    .Build();
```

**İlk yazılan en dışta.**

| Katman | Nerede | Sebep |
|---|---|---|
| `UseLogging` | en dışta | Cache hit'i de görsün — "3 ms'de döndü" log'a düşsün |
| `UseOpenTelemetry` | 2. | Span, cache kararını da kapsasın |
| `UseDistributedCache` | 3. | **Cache hit retry'a hiç inmesin** — ağa çıkmıyoruz, retry bütçesi yakmanın anlamı yok |
| `RetryChatClient` | en içte | Yalnızca **gerçek ağ çağrısını** sarsın |

Ters kursaydık (`retry` en dışta): cache'ten gelen bir hata 3 kez tekrarlanır, her denemede aynı bozuk kayıt okunurdu.

### `RetryChatClient` — kendi yazdığımız katman

```csharp
public class RetryChatClient : DelegatingChatClient
{
    private readonly int _maxAttempts;

    public RetryChatClient(IChatClient innerClient, int maxAttempts = 3) : base(innerClient)
    {
        _maxAttempts = maxAttempts;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (attempt < _maxAttempts && IsTransient(ex))
            {
                var delay = Backoff(attempt);
                Console.WriteLine($"   [retry] {attempt}. deneme: {ex.GetType().Name} -> {delay.TotalMilliseconds:F0} ms sonra tekrar");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is AnthropicRateLimitException      // 429
            or Anthropic5xxException                  // 500-599
            or AnthropicIOException                   // ağ/bağlantı
            or TaskCanceledException;                 // timeout
    }

    private static TimeSpan Backoff(int attempt)
    {
        var baseMs = Math.Pow(2, attempt) * 250;                  // 500, 1000, 2000
        var jitter = Random.Shared.NextDouble() * baseMs * 0.3;   // %0-30

        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}
```

**`for (var attempt = 1; ; attempt++)`** — koşul kısmı boş, döngü kendiliğinden durmuyor. İki çıkışı var: `return` (başarı) veya exception yukarı fırlar.

**`catch (Exception ex) when (...)`** — exception filter. Koşul tutmazsa exception **hiç yakalanmamış gibi** yukarı çıkar; `throw` yazmaya gerek kalmaz, stack trace bozulmaz. Klasik alternatifi:

```csharp
catch (Exception ex)
{
    if (attempt >= _maxAttempts || !IsTransient(ex))
    {
        throw;          // ← stack trace'i bozar
    }
    ...
}
```

**`Task.Delay` vs `Thread.Sleep`** — `Task.Delay` thread'i bloklamaz; `await` ile thread havuza döner, başka isteklere bakabilir.

### Backoff ve jitter

```
attempt 1 -> 2 * 250 =  500 ms
attempt 2 -> 4 * 250 = 1000 ms
attempt 3 -> 8 * 250 = 2000 ms
```

**Backoff:** sunucu zaten yükün altındaysa sabit aralıkla dürtmek işleri kötüleştirir.

**Jitter:** rastgele %0-30 sapma.

```
Jitter YOK                          Jitter VAR
100 sunucu aynı anda 429 aldı       100 sunucu aynı anda 429 aldı
  → hepsi TAM 500 ms bekler           → 500-650 ms arasına dağılır
  → hepsi AYNI anda tekrar dener      → istekler zamana yayılır
  → sunucu yine devrilir              → sunucu nefes alır
```

Buna **thundering herd** deniyor. Tek istemcide fark etmez, üretimde eder.

### Prompt caching — bu FARKLI bir şey

En çok karıştırılan kısım. **İki ayrı cache var:**

| | `UseDistributedCache` | Anthropic prompt caching |
|---|---|---|
| Nerede | **Senin** sürecinde | **Anthropic'in** sunucusunda |
| Ne saklar | Cevabın tamamı | İsteğin **ön eki** (system prompt) |
| Ne zaman tutar | Soru **birebir** aynıysa | System prompt aynı, **soru farklı olabilir** |
| Kazanç | %100 (çağrı hiç yapılmaz) | ~%90 input maliyeti |

Son satır kritik. RAG'de belge bağlamı sabit, kullanıcı sorusu her seferinde farklı:
- Yanıt cache'i → **hiç tutmaz**
- Prompt caching → **her seferinde tutar**

Nasıl açılıyor:

```csharp
var options = new ChatOptions
{
    Temperature = 0.0f,
    MaxOutputTokens = 50,

    RawRepresentationFactory = _ => new MessageCreateParams
    {
        MaxTokens = 50,
        Model = "claude-haiku-4-5",
        Messages = [],
        CacheControl = new CacheControlEphemeral()
    }
};
```

`Microsoft.Extensions.AI` sağlayıcı-bağımsız olduğu için `cache_control` gibi **Anthropic'e özel** ayarları bilmez. `RawRepresentationFactory` o kapı: "ham isteği şöyle kur" diyorsun, adaptör `Model`/`Messages`/`MaxTokens`'ı kendi değerleriyle ezip üstüne `CacheControl`'ü taşıyor.

### Batch API — kavram olarak

Bu modülde kodlamadık ama bilmen gereken üçüncü maliyet aracı.

**Ne:** İstekleri tek tek değil, bir dosya hâlinde toplu gönderirsin. Anthropic bunları kendi uygun bulduğu zamanda işler (24 saate kadar), sonucu toplu döner.

**Kazanç:** **%50 indirim.**

**Bedeli:** gecikme garantisi yok. Kullanıcı ekranda beklerken kullanamazsın.

| | Normal API | Batch API |
|---|---|---|
| Cevap süresi | saniyeler | dakikalar–saatler |
| Fiyat | tam | **yarı** |
| Nerede | kullanıcı bekliyor | gece işi, toplu etiketleme, rapor |

Modül 8'in eval runner'ı buna **birebir uygun**: 20 vakayı tek tek sormak yerine batch'e atsan yarı fiyatına çalışırdı, kimse beklemiyor.

### Polly — neden kullanmadık

Üretimde retry'ı elle yazmazsın; **Polly** var ve `Microsoft.Extensions.Http.Resilience` paketiyle tek satıra düşüyor (`AddStandardResilienceHandler`). Retry, circuit breaker, timeout, hedging — hepsi hazır ve savaşta test edilmiş.

Burada elle yazdık çünkü **ne yaptığını görmek** için. Backoff'un neden üstel, jitter'ın neden gerekli, hangi hatanın tekrar denenmeyeceğine kimin karar verdiği — bunlar Polly kullanırken de bilmen gereken şeyler. Polly bu kararları senin yerine vermiyor, sadece uygulamasını yazıyor.

## 🧪 Ödev ve öğrendiklerim

Dört demo koşuldu. Toplam maliyet: **~$0,03**.

### 🔴 Bulgu 1 — `AnthropicServiceException` 5xx değil

`IsTransient`'i ilk şöyle yazdım:

```csharp
or AnthropicServiceException      // 5xx sandım
```

Test çıktısı:

```
=== 2. Kalıcı hata (400): hiç tekrar denenmemeli ===
   [retry] 1. deneme: AnthropicBadRequestException -> 584 ms sonra tekrar
   [retry] 2. deneme: AnthropicBadRequestException -> 1254 ms sonra tekrar
Çağrı  : 3   (1 olmalı — tekrar denenmedi)
```

Yansımayla hiyerarşiye baktım:

```
AnthropicException
├── AnthropicIOException
└── AnthropicServiceException          ← TÜM API hatalarının atası
    └── AnthropicApiException
        ├── Anthropic4xxException
        │   ├── AnthropicBadRequestException     400
        │   ├── AnthropicUnauthorizedException   401
        │   └── AnthropicRateLimitException      429
        └── Anthropic5xxException                500-599  ← doğru tip
```

`is` operatörü türetilmiş sınıfları da yakaladığı için 400 de filtreye takıldı. Doğru tip `Anthropic5xxException`.

> **Kod derlendi, çalıştı, sadece yanlış davrandı.** Testte `(1 olmalı)` yazmasaydım `Çağrı: 3` satırı gözümden kaçardı — Modül 8'in refleksi yakaladı.

Üretimde bu hata rate limit'i boşa tüketir ve kullanıcıyı 2 saniye fazladan bekletir.

### 🔴 Bulgu 2 — Cache'ten gelen `Usage` gerçek maliyet değil

```
1. çağrı (cache boş)            1153 ms   token 24/5   cevap: Ankara
2. çağrı (aynı soru)               3 ms   token 24/5   cevap: Ankara
3. çağrı (1 harf farklı)         697 ms   token 25/5   cevap: Ankara
```

Plana "ikinci çağrıda token 0 olur" yazmıştım. **Olmadı.** Cache `ChatResponse`'un tamamını saklıyor — `Usage` alanı dahil.

```csharp
totalCost += response.Usage.InputTokenCount / 1_000_000m;   // ← cache hit'te de sayar
```

1000 istekten 900'ü cache'ten dönse bile fatura raporun 1000 istek gösterir. Maliyet sayacı cache'in **altında** olmalı.

3. çağrı anahtarın hassasiyetini gösteriyor: *"Tek kelime."* → *"Tek kelimeyle."* — tek harf, cache miss. Anahtar **metinden** üretiliyor, anlamdan değil.

### 🔴 Bulgu 3 — Prompt caching'in ilk çağrısı %25 PAHALI

```
1. çağrı      input 7756   cache'ten okunan 0      CacheCreationInputTokens = 7753
2. çağrı      input 7756   cache'ten okunan 7753
```

Anthropic'in çarpanları: cache **yazma** 1,25×, cache **okuma** 0,10×.

| | Cache YOK | Cache VAR |
|---|---|---|
| 1. çağrı | $0,00778 | **$0,00972** ← %25 pahalı |
| 2. çağrı | $0,00778 | **$0,00080** ← %90 ucuz |
| **Toplam** | $0,01556 | **$0,01052** (−%32) |

- Tek çağrı yapacaksan prompt caching **zarar**
- Başa baş: 2. çağrı
- 3.'den sonrası net kâr

> "Cache = ucuz" sanıp tek seferlik işlerde açmak para kaybettirir.

### 🔴 Bulgu 4 — `InputTokenCount` cache'i içeriyor

Her iki çağrıda da **7756**. Bu alan "önbellekten gelenler dahil toplam girdi". Maliyeti buradan hesaplayamazsın:

```
normal input = InputTokenCount - CachedInputTokenCount - CacheCreationInputTokens
```

Bulgu 2'nin kardeşi: **iki cache de `Usage`'ı olduğundan pahalı gösteriyor.** Biri istemci tarafında, biri sunucu tarafında, ikisi de aynı hatayı yaptırıyor.

### Bulgu 5 — Adaptör cache alanlarını iki ayrı yere koyuyor

| Anthropic alanı | `Microsoft.Extensions.AI` karşılığı |
|---|---|
| `cache_read_input_tokens` | `Usage.CachedInputTokenCount` (birinci sınıf alan) |
| `cache_creation_input_tokens` | `Usage.AdditionalCounts["CacheCreationInputTokens"]` |

Okuma sayacı standart alana bağlanmış; yazma sayacı sağlayıcıya özel olduğu için sözlükte. `AdditionalCounts`'u dökmeseydik onu hiç göremezdik.

### Bulgu 6 — `Trace` seviyesi prompt'un tamamını basıyor

```
trce: Microsoft.Extensions.AI.LoggingChatClient[805843669] GetResponseAsync invoked:
[ { "role": "user", "contents": [ { "$type": "text", "text": "Token nedir? Tek cümleyle." } ] } ]...
```

Geliştirirken çok faydalı, üretimde **kullanıcı verisi log'a düşer**. `Information` ve üstünde sadece "çağrı yapıldı/bitti" kalıyor.

### OpenTelemetry standart etiketler veriyor

```
Activity.DisplayName:  chat claude-haiku-4-5
Activity.Duration:     00:00:01.6673060
Activity.Tags:
    gen_ai.request.model: claude-haiku-4-5
    gen_ai.usage.input_tokens: 18
    gen_ai.usage.output_tokens: 49
    gen_ai.usage.cache_read.input_tokens: 0
```

`gen_ai.*` isimleri **OpenTelemetry'nin GenAI standardı** — Microsoft'un uydurduğu isimler değil. Yarın OpenAI'a geçsen aynı etiketlerle aynı grafikler çalışır. Soyutlamanın gözle görülür faydası.

### Kavramlar

Bu modülde decorator zincirini ve sıranın neden davranışı belirlediğini, `DelegatingChatClient`'tan türeyip kendi middleware'imi yazmayı, geçici/kalıcı hata ayrımını, exponential backoff + jitter'ı, iki farklı cache türünü ve en önemlisi **cache'in maliyet ölçümünü nasıl yalancı çıkardığını** öğrendim.

## ⚠️ Dikkat edilecekler

- **🔴 Exception'ı tipine göre sınıflandırırken hiyerarşiyi bilmeden yazma.** Ad yanıltıcı olabilir; `is` türetilmişleri de yakalar. `AnthropicServiceException` 5xx değil, hepsinin atası.

- **🔴 Kalıcı hatayı tekrar deneme.** 400 ve 401 bekleyince düzelmez; sadece kullanıcıyı bekletir ve rate limit'i tüketir.

- **🔴 Maliyet sayacını cache'in altına koy.** Üstte olursa cache hit'leri de sayar, faturayı olduğundan büyük raporlarsın.

- **Sıra davranışı belirler.** Cache retry'ın üstünde olmalı; ters kurarsan cache'ten gelen hata 3 kez tekrarlanır.

- **`InputTokenCount` prompt cache'i içerir.** Maliyet için `CachedInputTokenCount` ve `CacheCreationInputTokens`'ı ayır.

- **Prompt caching tek çağrıda zarar.** Yazma 1,25×, okuma 0,10×. Başa baş 2. çağrıda.

- **Prompt caching'in alt sınırı var** (Haiku: 2048 token). Kısa prompt'ta hiç oluşmaz, "çalışmıyor" sanırsın.

- **`Trace` log seviyesini üretimde kullanma.** Prompt'un tamamı log'a düşer.

- **Jitter'sız backoff yazma.** Aynı anda 429 alan istemciler aynı anda tekrar dener ve sunucuyu yeniden devirir.

- **OpenTelemetry exporter'sız çalışmaz.** Span üretilir ama kimse okumaz; `AddSource` adı `UseOpenTelemetry`'deki adla **birebir aynı** olmalı.

- **Gecikmeye duyarsız işlerde Batch API'yi düşün.** %50 indirim, karşılığında saatlerce bekleme.

- **Üretimde retry'ı Polly ile yaz.** Elle yazmak öğrenmek için; `AddStandardResilienceHandler` aynı işi savaşta test edilmiş hâliyle yapıyor.

---

Hazırlayan: Mert Ağralı 👨‍💻
