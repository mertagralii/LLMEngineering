// ================================================================================
//  MODUL 9 — LlmProduction
//
//  Bu dosya 4 ayri demo barindiriyor. Hangisinin calisacagini komut satiri
//  argumani belirliyor:  dotnet run -- retry | chain | cache | promptcache
//
//  Dosyanin yapisi (yukaridan asagi okunur):
//    1) using'ler          — hangi kutuphaneleri kullaniyoruz
//    2) sabitler           — key ve OpenTelemetry kaynak adi
//    3) komut secimi       — hangi demo calisacak
//    4) yerel fonksiyonlar — demolarin govdeleri
//    5) tipler             — FlakyChatClient sinifi
//
//  DIKKAT: 4. ve 5. bolum dosyanin ALTINDA ama bu onemli degil. C#'ta yerel
//  fonksiyonlar "hoisted" olur: tanimlandigi yerden once cagrilabilir.
// ================================================================================

// --- using = "bu ad alanindaki tipleri kisa adiyla kullanacagim" ---
using System.Net;                              // HttpStatusCode (429, 400 gibi HTTP kodlari)
using Anthropic;                               // AnthropicClient — Claude API istemcisi
using Anthropic.Exceptions;                    // AnthropicRateLimitException vb.
using Anthropic.Models.Messages;               // MessageCreateParams, CacheControlEphemeral
using Microsoft.Extensions.AI;                 // IChatClient, ChatMessage, ChatOptions, Use* uzantilari
using Microsoft.Extensions.Caching.Distributed; // IDistributedCache arayuzu
using Microsoft.Extensions.Caching.Memory;     // MemoryDistributedCache — arayuzun bellek uygulamasi
using Microsoft.Extensions.Logging;            // ILoggerFactory, LogLevel
using Microsoft.Extensions.Options;            // Options.Create(...) yardimcisi
using Modul_9_LlmProduction;                   // RetryChatClient — kendi yazdigimiz sinif
using OpenTelemetry;                           // Sdk.CreateTracerProviderBuilder
using OpenTelemetry.Trace;                     // AddConsoleExporter uzantisi

// ================================================================================
//  2) SABITLER
// ================================================================================

// const = derleme aninda sabit. Degeri kodun icine gomulur, calisma aninda degismez.
// Gercek projede bu deger appsettings.json'dan okunur; burada basit tutuyoruz.
const string ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR";

// OpenTelemetry'de her span bir "kaynak" (ActivitySource) adina uretilir.
// Asagida iki yerde kullaniliyor ve IKISININ AYNI OLMASI sart:
//   - UseOpenTelemetry(..., SourceName)        -> span'i bu adla uretir
//   - Sdk...AddSource(SourceName)              -> bu adla uretilenleri dinler
// Farkli yazarsan span uretilir ama kimse dinlemez, ekranda hicbir sey gormezsin.
const string SourceName = "Modul9";

// ================================================================================
//  3) KOMUT SECIMI
//
//  args nereden geliyor? Top-level statements kullandigimiz icin derleyici
//  arka planda "static async Task Main(string[] args)" uretiyor. args o parametre.
//
//  "dotnet run -- cache"  ->  args = ["cache"]
//  "dotnet run"           ->  args = []          (bos dizi, null degil)
// ================================================================================

// Ucluk operator:  kosul ? dogruysa : yanlissa
// args.Length > 0  -> en az bir arguman verilmis mi?
// Verilmemisse varsayilan "retry" — cunku o demo API key gerektirmiyor.
var command = args.Length > 0 ? args[0] : "retry";

if (command == "retry")
{
    await RetryDemoAsync();

    // return; top-level statements icinde Main'den CIKMAK demek.
    // Bu satir olmasa asagidaki if'ler de kontrol edilir ve en sonda
    // "Bilinmeyen komut" mesaji basilirdi.
    return;
}

if (command == "chain")
{
    await ChainDemoAsync();
    return;
}

if (command == "cache")
{
    await CacheDemoAsync();
    return;
}

if (command == "promptcache")
{
    await PromptCacheDemoAsync();
    return;
}

// Buraya ancak hicbir if tutmadiysa gelinir.
Console.WriteLine($"Bilinmeyen komut: '{command}'. Kullanılabilir: retry, chain, cache, promptcache");
return;

// ================================================================================
//  URETIM ZINCIRI
//
//  Modulun kalbi burasi. Dort katmani ic ice geciren fonksiyon.
//
//  Parametreler:
//    loggerFactory : log satirlarini nereye yazacagimizi bilen fabrika.
//                    "Fabrika" cunku her katman kendine bir ILogger uretiyor.
//    cache         : cevaplarin saklanacagi yer. IDistributedCache ARAYUZU
//                    aldigimiz icin bellek/Redis/SQL fark etmiyor.
//
//  Donen deger: IChatClient — ama icinde 4 katman var. Cagiran kod bunu bilmiyor.
// ================================================================================
IChatClient BuildProductionClient(ILoggerFactory loggerFactory, IDistributedCache cache)
{
    // Ham Anthropic istemcisi. Henuz IChatClient degil, Anthropic'e ozel bir tip.
    // { ApiKey = ApiKey } = object initializer: nesneyi olusturup ozelligini set eder.
    var client = new AnthropicClient { ApiKey = ApiKey };

    return client
        // AsIChatClient(model): Anthropic'e ozel istemciyi saglayici-bagimsiz
        // IChatClient arayuzune cevirir. Parametre hangi modele soracagimiz.
        .AsIChatClient("claude-haiku-4-5")

        // AsBuilder(): "bu istemciyi sarmalamaya hazir hale getir".
        // Bundan sonraki Use* cagrilari zincire katman ekler.
        .AsBuilder()

        // --- SIRA BURADA BELIRLENIYOR: ILK YAZILAN EN DISTA OLUR ---

        // 1. EN DISTA. Istegi ve cevabi log'a yazar.
        //    Cache hit'i de gorur — "3 ms'de dondu" bilgisi buradan cikar.
        //    Parametre: loglari uretecek fabrika.
        .UseLogging(loggerFactory)

        // 2. Her cagri icin bir OpenTelemetry span'i uretir (sure, model, token).
        //    Parametreler: (loggerFactory, span'in uretilecegi kaynak adi)
        .UseOpenTelemetry(loggerFactory, SourceName)

        // 3. Yanit onbellegi. Mesajlardan + options'tan bir ANAHTAR uretir.
        //    Anahtar daha once gorulduyse cevabi buradan dondurur ve
        //    ASAGIYA HIC INMEZ. Bu yuzden retry'in USTUNDE olmali:
        //    cache hit zaten aga cikmiyor, retry butcesi yakmasin.
        .UseDistributedCache(cache)

        // 4. EN ICTE. Sadece GERCEK ag cagrisini sarar.
        //    Use(...) kendi middleware'ini zincire sokma noktasi.
        //    inner = bir alt katman (burada ham AnthropicClient).
        //    3 = en fazla 3 deneme.
        .Use(inner => new RetryChatClient(inner, 3))

        // Build(): zinciri kapatir, kullanilabilir tek bir IChatClient dondurur.
        .Build();
}

// ================================================================================
//  DEMO 1 — chain
//  Tum katmanlar acik, tek soru. Amac: log satirlarini ve span'i GORMEK.
// ================================================================================
async Task ChainDemoAsync()
{
    // LoggerFactory.Create(...) bir yapilandirma blogu aliyor.
    // "using var" = fonksiyon bitince otomatik Dispose edilir (log'lar bosaltilir).
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        // AddSimpleConsole: loglari konsola yaz.
        // SingleLine = true: her log tek satirda kalsin (yoksa cok satira yayilir).
        builder.AddSimpleConsole(o => o.SingleLine = true);

        // Trace = en ayrintili seviye. UseLogging bu seviyede PROMPT'UN TAMAMINI basar.
        // URETIMDE KULLANMA: kullanici verisi log'a duser. Orada Information yeter.
        builder.SetMinimumLevel(LogLevel.Trace);
    });

    // OpenTelemetry boru hatti. Bu olmadan UseOpenTelemetry span uretir
    // ama span'i dinleyen/yazan kimse olmaz -> ekranda hicbir sey gormezsin.
    using var tracer = Sdk.CreateTracerProviderBuilder()
        .AddSource(SourceName)      // SADECE bu adla uretilen span'leri dinle
        .AddConsoleExporter()       // dinlediklerini konsola bas
        .Build();

    // MemoryDistributedCache'in kurucusu IOptions<T> istiyor (ASP.NET Core aliskanligi).
    // Options.Create(...) elimizdeki nesneyi o sarmalayiciya koyan kisa yol.
    // Adi "distributed" ama bellekte calisiyor; UYGULADIGI ARAYUZ IDistributedCache.
    // Uretimde bu satir RedisCache olur, asagidaki hicbir satir degismez.
    var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    // Zinciri kur. Bu satirda HICBIR SEY CALISMAZ — sadece 4 katmanli nesne olusur.
    var chat = BuildProductionClient(loggerFactory, cache);

    // Mesaj listesi. ChatRole.User = kullanicinin sorusu.
    // new(...) = "target-typed new": tipi zaten List<ChatMessage> oldugu icin
    // "new ChatMessage(...)" yazmaya gerek yok.
    var messages = new List<ChatMessage> { new(ChatRole.User, "Token nedir? Tek cümleyle.") };

    var options = new ChatOptions
    {
        Temperature = 0.0f,      // 0 = en olasi kelimeyi sec, yaratici olma
        MaxOutputTokens = 200    // cevap en fazla 200 token olsun (maliyet tavani)
    };

    Console.WriteLine("=== Tam zincir: logging -> otel -> cache -> retry -> API ===");
    Console.WriteLine();

    // ISTEK BURADA BASLIYOR. Katmanlardan asagi iner, cevap yukari cikar.
    var response = await chat.GetResponseAsync(messages, options);

    Console.WriteLine();

    // response.Text = tum asistan mesajlarinin metni birlestirilmis hali.
    Console.WriteLine($"Cevap : {response.Text}");

    // response.Usage nullable (saglayici token bilgisi vermeyebilir).
    // ?. = "null degilse eris, null ise null don" — NullReferenceException korumasi.
    Console.WriteLine($"Token : {response.Usage?.InputTokenCount} in / {response.Usage?.OutputTokenCount} out");
}

// ================================================================================
//  DEMO 2 — cache
//  Ayni soruyu iki kez sorup ikincisinin aga CIKMADIGINI gostermek.
// ================================================================================
async Task CacheDemoAsync()
{
    using var loggerFactory = LoggerFactory.Create(builder =>
    {
        builder.AddSimpleConsole(o => o.SingleLine = true);

        // Burada Warning: Trace seviyesi uc cagrida ekrani prompt metniyle doldurur.
        builder.SetMinimumLevel(LogLevel.Warning);
    });

    var cache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
    var chat = BuildProductionClient(loggerFactory, cache);

    var options = new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 50 };
    var soru = new List<ChatMessage> { new(ChatRole.User, "Türkiye'nin başkenti neresi? Tek kelime.") };

    Console.WriteLine("=== Yanıt önbelleği ===");
    Console.WriteLine();

    // 1. cagri: cache bos -> aga cikar -> ~1150 ms
    await AskAndReportAsync(chat, soru, options, "1. çağrı (cache boş)");

    // 2. cagri: AYNI liste, AYNI options -> ayni anahtar -> cache'ten -> ~3 ms
    await AskAndReportAsync(chat, soru, options, "2. çağrı (aynı soru)");

    // Anahtar mesaj METNINDEN uretiliyor, anlamdan degil.
    // "Tek kelime." -> "Tek kelimeyle."  = TEK HARF farki = BASKA anahtar = cache MISS.
    var benzerSoru = new List<ChatMessage> { new(ChatRole.User, "Türkiye'nin başkenti neresi? Tek kelimeyle.") };

    await AskAndReportAsync(chat, benzerSoru, options, "3. çağrı (1 harf farklı)");
}

// Yardimci: soruyu sorar, gecen sureyi olcer, tek satirda raporlar.
//
// Parametreler:
//   chat     : sorunun sorulacagi istemci (zincirin tamami)
//   messages : gonderilecek mesajlar — CACHE ANAHTARININ bir parcasi
//   options  : sicaklik, token tavani — bu da anahtarin parcasi
//   label    : ekranda gorunecek etiket
async Task AskAndReportAsync(IChatClient chat, List<ChatMessage> messages, ChatOptions options, string label)
{
    // Sureyi olcmek icin baslangic damgasi.
    var started = DateTime.Now;

    var response = await chat.GetResponseAsync(messages, options);

    // Iki DateTime'in farki bir TimeSpan verir.
    var elapsed = DateTime.Now - started;

    // Bicimlendirme sozdizimi:  {deger,genislik:bicim}
    //   {label,-28}                  -> 28 karakter genislik, SOLA yasli (eksi isareti)
    //   {elapsed.TotalMilliseconds,7:F0} -> 7 karakter, saga yasli, ondalik yok
    // Amac: satirlarin alt alta hizali gorunmesi.
    Console.WriteLine($"{label,-28} {elapsed.TotalMilliseconds,7:F0} ms" +
                      $"   token {response.Usage?.InputTokenCount}/{response.Usage?.OutputTokenCount}" +
                      $"   cevap: {response.Text}");
}

// ================================================================================
//  DEMO 3 — retry
//  GERCEK API'YE HIC CIKMAZ. Sahte istemciyle retry davranisini test eder.
//  Bu yuzden API key gerektirmez ve bedavadir.
// ================================================================================
async Task RetryDemoAsync()
{
    var question = new List<ChatMessage> { new(ChatRole.User, "merhaba") };
    var options = new ChatOptions { MaxOutputTokens = 50 };

    // ---------- 1. GECICI hata ----------
    Console.WriteLine("=== 1. Geçici hata (429): 2 kez patlar, 3.'de başarılı ===");

    // failTimes: 2  -> ilk 2 cagri hata firlatir, 3. basarili olur
    // RateLimitError -> hata uretecek fonksiyonun ADI (parantez YOK!)
    //    Parantez koysaydik fonksiyonu HEMEN calistirip sonucunu gonderirdik.
    //    Parantezsiz yazinca fonksiyonun KENDISINI gonderiyoruz; FlakyChatClient
    //    onu her hata aninda yeniden cagiracak.
    var flaky = new FlakyChatClient(failTimes: 2, RateLimitError);

    // Zincir: sadece retry. Log/cache yok, olcumu bozmasin.
    IChatClient withRetry = flaky
        .AsBuilder()
        .Use(inner => new RetryChatClient(inner, 3))
        .Build();

    var started = DateTime.Now;
    var response = await withRetry.GetResponseAsync(question, options);
    var elapsed = DateTime.Now - started;

    Console.WriteLine($"Cevap  : {response.Text}");

    // CallCount: sahte istemcinin kac kez cagrildigi. BEKLENEN DEGERI de yaziyoruz
    // ki gozle dogrulayabilelim — Modul 8'in dersi.
    Console.WriteLine($"Çağrı  : {flaky.CallCount}   (3 olmalı)");
    Console.WriteLine($"Süre   : {elapsed.TotalMilliseconds:F0} ms   (beklemeler dahil)");

    // ---------- 2. KALICI hata ----------
    Console.WriteLine();
    Console.WriteLine("=== 2. Kalıcı hata (400): hiç tekrar denenmemeli ===");

    // failTimes: 99 -> her zaman hata firlatir. Retry calissa 3 cagri olurdu.
    var broken = new FlakyChatClient(failTimes: 99, BadRequestError);

    IChatClient withRetry2 = broken
        .AsBuilder()
        .Use(inner => new RetryChatClient(inner, 3))
        .Build();

    try
    {
        await withRetry2.GetResponseAsync(question, options);

        // Buraya gelinmemeli; gelinirse retry yanlis calisiyordur.
        Console.WriteLine("BEKLENMEDİK: hata fırlatılmadı");
    }
    catch (Exception ex)
    {
        // ex.GetType().Name = exception sinifinin adi ("AnthropicBadRequestException")
        Console.WriteLine($"Hata   : {ex.GetType().Name}");
        Console.WriteLine($"Çağrı  : {broken.CallCount}   (1 olmalı — tekrar denenmedi)");
    }
}

// --- Sahte hata uretecleri ---
//
// Anthropic SDK'sinin exception'lari HttpRequestException'i sarmalar ve iki
// "required" uyesi vardir. required = C# 11 ozelligi: nesne olusturulurken
// bu ozellikler ATANMAK ZORUNDA, yoksa derlenmez.
AnthropicRateLimitException RateLimitError()
{
    return new AnthropicRateLimitException(new HttpRequestException("429 simülasyon"))
    {
        StatusCode = HttpStatusCode.TooManyRequests,   // 429
        ResponseBody = """{"type":"error","error":{"type":"rate_limit_error"}}"""
        // """...""" = raw string literal: icindeki cift tirnaklari kacis
        // karakteriyle (\") yazmaya gerek kalmaz.
    };
}

AnthropicBadRequestException BadRequestError()
{
    return new AnthropicBadRequestException(new HttpRequestException("400 simülasyon"))
    {
        StatusCode = HttpStatusCode.BadRequest,        // 400
        ResponseBody = """{"type":"error","error":{"type":"invalid_request_error"}}"""
    };
}

// ================================================================================
//  DEMO 4 — promptcache
//  ANTHROPIC'IN SUNUCUSUNDAKI onbellegi olcer. Yukaridaki cache'ten FARKLI:
//    UseDistributedCache      -> bizim surecimizde, CEVABI saklar
//    Anthropic prompt caching -> Anthropic'te, ISTEGIN ON EKINI saklar
// ================================================================================
async Task PromptCacheDemoAsync()
{
    // Program bin/Debug/net10.0 icinden calisir; dosyalar proje kokunde durur.
    // AppContext.BaseDirectory = calisan dll'in klasoru. Uc seviye yukari = proje koku.
    // GetFullPath, icindeki ".." ifadelerini sadelestirip gercek yolu verir.
    // Goreli yol ("docs/...") yazsaydik, nereden calistirdigina gore kirilirdi.
    var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    var uzunPrompt = await File.ReadAllTextAsync(Path.Combine(projectRoot, "docs", "uzun-prompt.md"));

    // Bu demoda ZINCIR YOK — bilerek.
    // UseDistributedCache acik olsaydi 2. cagri aga hic cikmazdi ve
    // SUNUCU tarafi onbellegi olcemezdik.
    var client = new AnthropicClient { ApiKey = ApiKey };
    IChatClient chat = client.AsIChatClient("claude-haiku-4-5");

    var messages = new List<ChatMessage>
    {
        // System = modele verilen talimat/baglam. Onbelleklenecek UZUN kisim bu.
        new(ChatRole.System, uzunPrompt),

        // User = degisken kisim. Gercek hayatta her istekte farkli olur.
        new(ChatRole.User,   "Bu dokümanda kaç bulgu var? Sadece sayı yaz.")
    };

    var options = new ChatOptions
    {
        Temperature = 0.0f,
        MaxOutputTokens = 50,

        // RawRepresentationFactory: M.E.AI saglayici-BAGIMSIZ oldugu icin
        // "cache_control" gibi Anthropic'e OZEL ayarlari bilmez.
        // Bu geri cagirim (callback) "ham istegi soyle kur" demenin yolu.
        // Adaptor Model/Messages/MaxTokens'i kendi degerleriyle EZER;
        // biz sadece CacheControl'u eklemis oluyoruz.
        //   _  = gelen parametreyi (IChatClient) kullanmiyoruz demek
        RawRepresentationFactory = _ => new MessageCreateParams
        {
            // Bu uc alan "required" oldugu icin yazmak zorundayiz,
            // ama adaptor uzerine kendi degerlerini yazacak.
            MaxTokens = 50,
            Model = "claude-haiku-4-5",
            Messages = [],

            // ASIL AMAC BU SATIR: "bu istegin on ekini onbellekle".
            // Ephemeral = kisa omurlu (varsayilan 5 dakika).
            CacheControl = new CacheControlEphemeral()
        }
    };

    Console.WriteLine($"=== Prompt caching — system prompt {uzunPrompt.Length} karakter (~{uzunPrompt.Length / 3} token) ===");
    Console.WriteLine();

    // 1. cagri: onbellege YAZAR   (normal fiyatin 1,25 kati — daha PAHALI)
    await AskAndReportCacheAsync(chat, messages, options, "1. çağrı (cache yazılır)");

    // 2. cagri: onbellekten OKUR  (normal fiyatin 0,10 kati — %90 ucuz)
    await AskAndReportCacheAsync(chat, messages, options, "2. çağrı (cache okunur)");
}

// Yardimci: cagriyi yapar ve token sayaclarini ayrintili doker.
async Task AskAndReportCacheAsync(IChatClient chat, List<ChatMessage> messages, ChatOptions options, string label)
{
    var started = DateTime.Now;
    var response = await chat.GetResponseAsync(messages, options);
    var elapsed = DateTime.Now - started;

    Console.WriteLine($"{label,-26} {elapsed.TotalMilliseconds,7:F0} ms");

    // DIKKAT: InputTokenCount onbellekten gelenleri DE icerir.
    // Iki cagrida da ayni sayiyi gorursun; maliyeti buradan hesaplayamazsin.
    Console.WriteLine($"     input            : {response.Usage?.InputTokenCount}");

    // Standart alan: kac token onbellekten OKUNDU.
    Console.WriteLine($"     cache'ten okunan : {response.Usage?.CachedInputTokenCount}");

    Console.WriteLine($"     output           : {response.Usage?.OutputTokenCount}");

    // AdditionalCounts = saglayiciya ozel, standart alani olmayan sayaclar.
    // Anthropic'in "cache_creation_input_tokens" degeri buraya dusuyor.
    //
    // "is { Count: > 0 } extra" = property pattern:
    //   null degil mi? VE Count > 0 mi? Oyleyse degiskeni 'extra' adiyla yakala.
    // Tek satirda uc is: null kontrolu + kosul + atama.
    if (response.Usage?.AdditionalCounts is { Count: > 0 } extra)
    {
        // Sozluk uzerinde donunce her eleman KeyValuePair olur.
        foreach (var pair in extra)
        {
            Console.WriteLine($"     ek sayaç         : {pair.Key} = {pair.Value}");
        }
    }

    Console.WriteLine();
}

// ================================================================================
//  5) TIPLER
//
//  Top-level statements kullanan bir dosyada tip tanimlari EN SONDA olmak zorunda.
//  Arasina koyarsan derleyici CS1022 verir (Modul 1'de yasamistik).
// ================================================================================

// Test double = gercek bir bagimliligin yerine gecen sahte nesne.
// Burada gercek API yerine geciyor: kasitli hata firlatiyor, sonra basarili oluyor.
//
// Neden DelegatingChatClient'tan turemedi?
//   Cunku sarmalayacagi bir IC katman yok. Bu sinif zincirin EN DIBI.
//   O yuzden IChatClient'in dort uyesini de elle yaziyoruz.
class FlakyChatClient : IChatClient
{
    // readonly = sadece kurucuda atanabilir, sonra degistirilemez.
    private readonly int _failTimes;        // kac cagri hata firlatacak
    private readonly Func<Exception> _error; // hangi hatayi firlatacak

    // Func<Exception> = "parametre almayan, Exception donduren fonksiyon" tipi.
    // Neden hazir bir Exception NESNESI degil de fonksiyon?
    //   Her denemede TAZE bir exception uretilsin diye. Ayni nesneyi tekrar
    //   firlatmak stack trace'i karistirir.

    // { get; private set; } = disaridan OKUNUR, sadece bu sinif YAZABILIR.
    // Testte "kac kez cagrildi" diye bakacagiz.
    public int CallCount { get; private set; }

    public FlakyChatClient(int failTimes, Func<Exception> error)
    {
        _failTimes = failTimes;
        _error = error;
    }

    // IChatClient'in ana metodu. Gercek istemcide burasi HTTP istegi atar;
    // burada sadece sayac artirip karar veriyoruz.
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (CallCount <= _failTimes)
        {
            // _error() = fonksiyonu CALISTIR, donen exception'i firlat.
            throw _error();
        }

        // Task.FromResult: zaten hazir olan bir degeri Task'e sarar.
        // async/await gerekmiyor cunku beklenecek bir is yok.
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "(sahte) tamam")));
    }

    // Streaming (kelime kelime akan cevap) bu demoda kullanilmiyor.
    // Arayuzu uygulamak zorunda oldugumuz icin metot var ama govdesi hata firlatiyor.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Bu sahte istemci streaming desteklemiyor.");
    }

    // GetService: "sende su tipte bir servis var mi?" sorusunun cevabi.
    // Ust katmanlar (orn. UseOpenTelemetry) alt katmandan metadata istemek icin kullanir.
    // Sahte istemcide verecek bir sey yok -> null.
    //
    // System.Type diye yaziyoruz cunku Anthropic.Models.Messages ad alaninda da
    // "Type" adinda bir tip var; duz "Type" yazinca derleyici hangisini
    // kastettigimizi bilemiyor (CS0104 belirsiz basvuru).
    public object? GetService(System.Type serviceType, object? serviceKey = null)
    {
        return null;
    }

    // IDisposable geregi. Gercek istemcide HTTP baglantisi kapatilir;
    // burada serbest birakilacak kaynak yok, bos birakiyoruz.
    public void Dispose()
    {
    }
}
