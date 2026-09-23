// ================================================================================
//  RetryChatClient — Modulun kalbi
//
//  NE YAPAR?
//    IChatClient'i sarar. Alttaki istemci gecici bir hatayla patlarsa bekler ve
//    tekrar dener. Kalici hatada hic beklemeden yukari firlatir.
//
//  NEDEN VAR?
//    Modul 0-8'de "chat.GetResponseAsync(...)" tek satirdi. Anthropic 429
//    dondugu anda exception firliyor ve program coküyordu. Uretimde bu kabul
//    edilemez: 429 gecici bir durum, 1 saniye beklesen gececek.
//
//  ZINCIRDE NEREDE?
//    EN ICTE. Sadece gercek ag cagrisini sarar:
//      UseLogging -> UseOpenTelemetry -> UseDistributedCache -> [BURASI] -> API
//    Cache hit oldugunda bu sinif HIC calismaz, cunku cevap ustteki katmandan
//    geri doner. Dogrusu da bu: aga cikmayan bir istek icin retry butcesi
//    yakmanin anlami yok.
// ================================================================================

using Anthropic.Exceptions;      // AnthropicRateLimitException, Anthropic5xxException vb.
using Microsoft.Extensions.AI;   // IChatClient, DelegatingChatClient, ChatMessage, ChatResponse

// Dosya kapsamli ad alani (file-scoped namespace, C# 10).
// Klasik "namespace X { ... }" yerine tek satir; tum dosya bu ad alaninda sayilir,
// bir seviye girinti kazanirsin.
namespace Modul_9_LlmProduction;

// ================================================================================
//  DelegatingChatClient NEDIR?
//
//  Icinde bir IChatClient tutan hazir bir taban sinif. IChatClient'in TUM
//  uyelerini varsayilan olarak "icerideki istemciye pasla" seklinde uygular.
//
//  Boylece biz sadece ILGILENDIGIMIZ metodu override ediyoruz (GetResponseAsync).
//  Digerleri -- GetStreamingResponseAsync, GetService, Dispose -- dokunulmadan
//  calismaya devam ediyor.
//
//  Bu olmasaydi IChatClient'in dort uyesini de elle yazmak zorundaydik.
//  (FlakyChatClient'ta tam olarak bunu yaptik, cunku o zincirin EN DIBI --
//   sarmalayacagi bir ic katman yok.)
//
//  Buna DECORATOR PATTERN deniyor: ayni arayuzu uygulayan bir nesneyi, yine
//  ayni arayuzu uygulayan baska bir nesneyle sarmalamak. ASP.NET Core'daki
//  app.UseAuthentication() / app.UseAuthorization() zinciriyle ayni fikir.
// ================================================================================
public class RetryChatClient : DelegatingChatClient
{
    // readonly = sadece kurucuda atanabilir. Nesne olustuktan sonra degismez.
    // Neden onemli: bu sinif ayni anda birden fazla istekte kullanilabilir
    // (singleton olarak DI'ya konabilir). Degisken alan olsaydi yaris durumu olurdu.
    private readonly int _maxAttempts;

    // KURUCU
    //
    // innerClient : sarmalayacagimiz alt katman. Zincirde bizden sonra gelen sey.
    //               Program.cs'te ".Use(inner => new RetryChatClient(inner, 3))"
    //               yazdigimizda "inner" buraya geliyor.
    //
    // maxAttempts : TOPLAM deneme sayisi (ilk cagri dahil). Varsayilan 3.
    //               3 = 1 ilk deneme + 2 tekrar.
    //
    // : base(innerClient)
    //   Taban sinifin (DelegatingChatClient) kurucusunu cagirir ve ona
    //   ic istemciyi verir. Taban sinif onu "InnerClient" ozelliginde saklar.
    //   Bunu yazmazsak derleyici hata verir: DelegatingChatClient'in
    //   parametresiz kurucusu yok.
    public RetryChatClient(IChatClient innerClient, int maxAttempts = 3) : base(innerClient)
    {
        _maxAttempts = maxAttempts;
    }

    // ============================================================================
    //  ANA METOT
    //
    //  override : taban siniftaki ayni metodun yerine gecer. Zincirde bir ust
    //             katman "GetResponseAsync" cagirdiginda BU govde calisir.
    //
    //  Parametreler (ucu de IChatClient arayuzunun sozlesmesi):
    //    messages          : gonderilecek mesaj listesi (system + user + ...)
    //    options           : sicaklik, token tavani, araclar... Nullable, verilmeyebilir.
    //    cancellationToken : "islemi iptal et" sinyali. Kullanici sayfayi kapatirsa
    //                        veya timeout olursa buradan haber gelir.
    //                        Task.Delay'e de geciriyoruz ki BEKLERKEN de iptal olabilsin.
    //
    //  Donus : Task<ChatResponse> — asenkron oldugu icin Task ile sarmali.
    // ============================================================================
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        // SONSUZ GORUNEN AMA SONSUZ OLMAYAN DONGU
        //
        // for (baslangic; KOSUL; artis) yapisinda KOSUL kismi BOS.
        // Bos kosul "her zaman devam et" demek. Yani dongu kendiliginde durmuyor.
        //
        // Peki nasil cikiyoruz? Iki yol var:
        //   1) try icindeki "return" -- cagri basarili oldu
        //   2) catch filtresi tutmadi -- exception yukari firladi
        //
        // attempt 1'den baslar: birinci deneme, ikinci deneme, ucuncu deneme...
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // base.GetResponseAsync = "ICERIDEKI istemciye sor".
                // "base" taban sinifi isaret eder; DelegatingChatClient'in
                // bu metodu InnerClient'a paslar.
                //
                // Zincirde bir ALT katmana inmek demek. Basarili olursa
                // sonucu oldugu gibi yukari donduruyoruz -- dongu biter.
                return await base.GetResponseAsync(messages, options, cancellationToken);
            }

            // EXCEPTION FILTER  --  catch (...) when (kosul)
            //
            // C#'in az bilinen ozelligi. "when" kosulu TUTMAZSA exception
            // hic yakalanmamis gibi yukari cikar.
            //
            // Klasik alternatif soyle olurdu:
            //     catch (Exception ex)
            //     {
            //         if (attempt >= _maxAttempts || !IsTransient(ex))
            //         {
            //             throw;          // <- stack trace'i bozar, debugger sasirir
            //         }
            //         ...
            //     }
            //
            // Filtre ile yazinca "throw" yazmaya gerek kalmiyor ve orijinal
            // stack trace bozulmuyor.
            //
            // Iki kosul birden aranıyor:
            //   attempt < _maxAttempts  -> hakkim kaldi mi?  (3. denemede false olur)
            //   IsTransient(ex)         -> bu hata tekrar denemeye deger mi?
            catch (Exception ex) when (attempt < _maxAttempts && IsTransient(ex))
            {
                // Kacinci denemede oldugumuza gore bekleme suresi hesaplaniyor.
                var delay = Backoff(attempt);

                // ex.GetType().Name = exception sinifinin adi, orn "AnthropicRateLimitException".
                // {delay.TotalMilliseconds:F0} = ondalik basamak olmadan, orn "637".
                Console.WriteLine($"   [retry] {attempt}. deneme: {ex.GetType().Name}" +
                                  $" -> {delay.TotalMilliseconds:F0} ms sonra tekrar");

                // Task.Delay = "su kadar bekle" ama THREAD'I BLOKLAMADAN.
                // Thread.Sleep olsaydi thread bos bos beklerdi; await ile
                // thread havuza geri doner, baska isteklere bakabilir.
                //
                // cancellationToken'i geciriyoruz: beklerken iptal gelirse
                // bosuna 2 saniye beklemeyelim.
                await Task.Delay(delay, cancellationToken);

                // catch blogu bitti -> dongunun basina donulur -> attempt++ -> tekrar dene
            }
        }
    }

    // ============================================================================
    //  HANGI HATA TEKRAR DENENIR?
    //
    //  Bu metot modulun en kritik karari. "Tekrar denemek ISE YARAR MI?" sorusunu
    //  cevapliyor:
    //
    //    429 rate limit   -> EVET. Biraz bekle, kotan acilir.
    //    500-599 sunucu   -> EVET. Gecici, sunucu toparlar.
    //    ag/baglanti      -> EVET. Baglanti geri gelir.
    //    timeout          -> EVET. Belki bu sefer hizli doner.
    //
    //    400 bozuk istek  -> HAYIR. Istegin yanlis; 10 kez gonder, 10 kez ayni hata.
    //    401 yanlis key   -> HAYIR. Key kendiliginden duzelmez.
    //    404 yok          -> HAYIR. Model adi yanlis, beklemekle bulunmaz.
    //
    //  Kalici hatayi tekrar denemek SADECE kullaniciyi bekletir ve rate limit'i tuketir.
    //
    //  ---------------------------------------------------------------------------
    //  🔴 BURADA GERCEK BIR HATA YAPTIK, NOT DUSUYORUZ:
    //
    //  Ilk yazdigimizda "AnthropicServiceException" vardi ve adina bakip
    //  "5xx demek" sandik. Testte kalici 400 hatasi da 3 kez denendi.
    //  Hiyerarsiye bakinca sebep ortaya cikti:
    //
    //      AnthropicException
    //      ├── AnthropicIOException
    //      └── AnthropicServiceException        <- TUM API hatalarinin atasi!
    //          └── AnthropicApiException
    //              ├── Anthropic4xxException
    //              │   ├── AnthropicBadRequestException    400
    //              │   ├── AnthropicUnauthorizedException  401
    //              │   └── AnthropicRateLimitException     429
    //              └── Anthropic5xxException               500-599  <- dogru tip
    //
    //  "is" operatoru TURETILMIS siniflari da yakalar. AnthropicServiceException
    //  yazinca 400 de 401 de filtreye takildi.
    //
    //  DERS: exception'i tipine gore siniflandiriyorsan hiyerarsiyi bilmeden yazma.
    //        Ad yanıltici olabilir. Kod derlendi, calisti, sadece YANLIS davrandi.
    // ============================================================================
    //
    //  static : nesneye ihtiyac duymuyor, sinifin kendisine ait. Hicbir alan
    //           okumuyor, sadece parametresine bakiyor.
    private static bool IsTransient(Exception ex)
    {
        // "is ... or ... or ..." = pattern matching (C# 9).
        // Klasik yazimi: ex is A || ex is B || ex is C
        // "or" ile tek "is" yazip tipleri alt alta dizebiliyoruz.
        return ex is AnthropicRateLimitException      // 429  — kota doldu
            or Anthropic5xxException                  // 500-599 — sunucu hatasi
            or AnthropicIOException                   // ag/baglanti koptu
            or TaskCanceledException;                 // timeout
    }

    // ============================================================================
    //  NE KADAR BEKLENECEK?
    //
    //  Iki fikrin birlesimi:
    //
    //  1) EXPONENTIAL BACKOFF (ustel geri cekilme)
    //     Her denemede bekleme IKIYE KATLANIR: 500 -> 1000 -> 2000 ms
    //     Neden: sunucu zaten yukun altindaysa sabit arayla durtmek
    //     durumu kotulestirir. Her seferinde daha uzun bekleyerek
    //     "toparlanmasina zaman taniyoruz".
    //
    //  2) JITTER (rastgele sapma)
    //     Hesaplanan sureye %0-30 arasi rastgele ekleme yapilir.
    //
    //     Jitter YOK                          Jitter VAR
    //     100 sunucu ayni anda 429 aldi       100 sunucu ayni anda 429 aldi
    //       -> hepsi TAM 500 ms bekler          -> 500-650 ms arasina dagilir
    //       -> hepsi AYNI anda tekrar dener     -> istekler zamana yayilir
    //       -> sunucu yine devrilir             -> sunucu nefes alir
    //
    //     Buna "thundering herd" (guruh etkisi) deniyor. Tek istemcide fark
    //     etmez, uretimde cok fark eder.
    // ============================================================================
    //
    //  attempt : kacinci deneme (1, 2, 3...)
    //  Donus   : beklenecek sure. TimeSpan = .NET'in sure tipi.
    private static TimeSpan Backoff(int attempt)
    {
        // Math.Pow(2, attempt) = 2 uzeri attempt
        //   attempt 1 -> 2   * 250 =  500 ms
        //   attempt 2 -> 4   * 250 = 1000 ms
        //   attempt 3 -> 8   * 250 = 2000 ms
        var baseMs = Math.Pow(2, attempt) * 250;

        // Random.Shared = .NET 6+ ile gelen, her yerden kullanilabilen ortak
        // rastgele uretici. "new Random()" yazip her cagride yeni nesne
        // uretmek kotu pratik: cok hizli ard arda cagrilirsa ayni tohumla
        // ayni sayilari uretebilir.
        //
        // NextDouble() = 0.0 ile 1.0 arasinda ondalik sayi.
        // * baseMs * 0.3  -> 0 ile baseMs'in %30'u arasinda bir ek sure.
        var jitter = Random.Shared.NextDouble() * baseMs * 0.3;

        // TimeSpan.FromMilliseconds: double'i TimeSpan'e cevirir.
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }
}
