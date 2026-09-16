# 🧠 ChatMemory — LLM'in hafızası yok: sliding window, özetleme ve kalıcılık

## 📖 Nedir?

Modül 0 ve 1'de her çağrı bağımsızdı — tek soru, tek cevap. Bu modülün çıkış noktası şu ve sezgiye ters:

> **LLM'in hafızası yoktur.** Model iki çağrı arasında hiçbir şey hatırlamaz. "Sohbet" dediğimiz şey, her çağrıda **bütün geçmişi baştan göndermektir.**

Doğrudan sonucu: konuşma uzadıkça her çağrı pahalılaşır. 20. mesajda 19 mesajlık geçmişi tekrar gönderir, tekrar ödersin. Bu modül o faturayı önce **gösteriyor**, sonra **sınırlıyor**, sonra **diske yazıyor**.

### Hafızanın üç katmanı

| Katman | Nerede durur | Bu modülde |
|---|---|---|
| Aktif pencere | `List<ChatMessage>` — RAM | Version 1 |
| Sıkıştırılmış geçmiş | özet mesajı — RAM | Version 2 |
| Kalıcı depo | disk (JSON) | Version 3 |

### Üç strateji

**Sliding window (kayan pencere)** — sadece son N mesajı tut, eskileri **at**. `System` mesajı hiç düşmez; mesajlar `User`+`Assistant` çifti hâlinde düşer (tek düşürürsen cevabı olmayan soru kalır).

**Özetleme (summarization)** — bütçe aşılınca eski mesajları ayrı bir LLM çağrısıyla özetle, tek `System` mesajına indir. Bilgi **atılmaz, sıkıştırılır**.

**Kalıcılık (persistence)** — geçmişi JSON'a yaz, açılışta geri yükle. Program kapansa da sohbet devam eder.

💡 **Kısacası:** Geçmiş her çağrıda yeniden gider ve yeniden ödenir. Ya at, ya sıkıştır, ya sakla — ama mutlaka yönet.

## 🧩 Nasıl çalışır?

| Adım | Ne oluyor |
|---|---|
| 1 | Açılışta `json/chat-history.json` varsa yüklenir |
| 2 | Yoksa / boşsa / bozuksa boş geçmişle başlar, **sebebi ekrana yazılır** |
| 3 | Kullanıcı mesajı `messages`'a eklenir, **liste tamamen** gönderilir |
| 4 | Cevap `messages`'a eklenir, token sayısı yazdırılır |
| 5 | `InputTokenCount > tokenBudget` ise eski mesajlar özetlenip tek mesaja iner |
| 6 | Geçmiş diske yazılır (özetleme sonrası, yani küçülmüş hâliyle) |

## 🔧 1. Gerekli paketler

```bash
dotnet add package Anthropic
```

`System.Text.Json` .NET'in içinde geliyor, ayrıca paket gerekmez. Ama `using`'leri elle eklemen gerekir — `ImplicitUsings` bunları kapsamıyor:

```csharp
using System.Text.Json;
using System.Text.Encodings.Web;
```

## ⚙️ 2. Ayarlar

API key `Program.cs` içinde:

```csharp
var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
```

`.csproj`'a üç satır — özetleme prompt'u `bin`'e kopyalansın diye:

```xml
<ItemGroup>
  <None Update="prompts\*.txt" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

Bu satır olmadan derleme geçer ama program **özetleme anında** `FileNotFoundException` ile çöker — yani sohbetin ortasında.

## 💻 3. Kullanım

### En basit hâli — Version 1

Modülün başladığı yer. Geçmiş sadece RAM'de, pencere mesaj sayısıyla yönetiliyor:

```csharp
const int maxHistoryMessages = 6;

var history = new List<ChatMessage>
{
    new(ChatRole.System, "Kısa ve net cevap veren bir asistansın.")
};

while (true)
{
    Console.Write("Sen: ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) break;

    history.Add(new(ChatRole.User, input));

    // Sliding window: system (indeks 0) korunur, en eski User+Assistant çifti düşer
    while (history.Count - 1 > maxHistoryMessages)
    {
        history.RemoveRange(1, 2);
    }

    var response = await chat.GetResponseAsync(history,
        new ChatOptions { Temperature = 0.3f, MaxOutputTokens = 500 });

    history.Add(new(ChatRole.Assistant, response.Text));

    Console.WriteLine($"Asistan: {response.Text}");
    Console.WriteLine($"[geçmiş: {history.Count - 1} mesaj | input: {response.Usage?.InputTokenCount}]");
}
```

**Modül 0/1'den tek yapısal farkı:** `history` döngünün **dışında**. Modül 0'da her çağrıda yeni liste kuruyorduk; burada liste yaşıyor. Sohbeti mümkün kılan tek şey bu.

`RemoveRange(1, 2)` — `1`'den başlar (indeks 0 = system, dokunulmaz), `2` eleman siler (bir soru + bir cevap).

### Özetleme — Version 2

Mesaj sayılı pencere çıkar, token bütçesi girer. Çağrıdan **sonra** kontrol edilir:

```csharp
if (response.Usage?.InputTokenCount > tokenBudget && messages.Count - 1 > keepRecentMessages)
{
    var toSummarize = messages.GetRange(1, messages.Count - 1 - keepRecentMessages);
    var conversationText = string.Join("\n", toSummarize.Select(m => $"{m.Role}: {m.Text}"));

    var summaryTemplate = await File.ReadAllTextAsync(Path.Combine("prompts", "summarize.txt"));
    var summaryPrompt = summaryTemplate.Replace("{konusma}", conversationText);

    var summaryMessages = new List<ChatMessage> { new(ChatRole.User, summaryPrompt) };
    var summaryResponse = await chat.GetResponseAsync(summaryMessages,
        new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 300 });

    messages.RemoveRange(1, toSummarize.Count);
    messages.Insert(1, new ChatMessage(ChatRole.System, $"Önceki konuşmanın özeti: {summaryResponse.Text}"));
}
```

Dört karar:

| Karar | Neden |
|---|---|
| Kontrol çağrıdan **sonra** | Token sayısını ancak `response.Usage` ile öğreniyoruz — bir tur gecikmeli çalışıyor |
| `keepRecentMessages = 2` | Son soru-cevap ham kalsın; kullanıcının az önce dediği şey özet süzgecinden geçmesin |
| Özet rolü `System` | Kullanıcının söylediği bir şey değil, modelin bilmesi gereken arka plan |
| Özet indeks **1**'e | Asıl system prompt'un ardına. Sonraki özetlemede bu özet de girdiye dahil olur — birikerek taşınır |

`history` göndermiyoruz — özetleme **geçmişsiz, tek seferlik** bir çağrı. Kendi maliyeti var: hafızayı sıkıştırmak bedava değil.

### Özetleme prompt'u dosyada

`prompts/summarize.txt` — Modül 1'in 4 parçalı yapısı, `{konusma}` slotu:

```
# 3. Kurallar
- Kullanıcı hakkındaki somut bilgileri (isim, yer, tercih, karar) mutlaka koru.
- Kim ne söyledi ayrımını koru.
- Yorum ekleme, sadece konuşmada geçenleri yaz.
```

Birinci kural kritik: **özette neyin korunacağını sen seçiyorsun.** Bunu yazmazsan modelin "önemli" tanımı seninkiyle aynı olmayabilir.

Dosyada olmasının sebebi: özet kalitesini ayarlamak deneme gerektiriyor. Çalıştır → beğenmedin → dosyayı düzelt → tekrar çalıştır. **Derleme yok.**

### Kalıcılık — Version 3

**`ChatMessage` doğrudan serialize edilmiyor.** `Role` bir struct, `Contents` bir `AIContent` listesi — JSON'a gidip geri gelmiyor. Çözüm kendi tipini yazmak:

```csharp
record SavedMessage(string Role, string Text);
```

Gidiş-dönüş çevirisi:

```csharp
// ChatMessage -> SavedMessage
new SavedMessage(m.Role.Value, m.Text ?? "")

// SavedMessage -> ChatMessage
new ChatMessage(new ChatRole(saved.Role), saved.Text)
```

Serializer ayarları:

```csharp
var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,                                   // okunabilir
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // ş ğ ı bozulmasın
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase       // "role", "text"
};
```

`UnsafeRelaxedJsonEscaping` olmadan Türkçe karakterler `ş` gibi kaçar — dosya çalışır ama okunmaz.

### Dosyanın hâli

`json/chat-history.json`:

```json
[
  {
    "role": "system",
    "text": "Kısa ve net cevap veren bir asistansın. En fazla 3 cümle yaz. Başlık, tablo, madde işareti ve emoji kullanma."
  },
  {
    "role": "system",
    "text": "Önceki konuşmanın özeti: • Mert, İstanbul Nişantaşı Üniversitesinde Yönetim Bilişim Sistemleri 4. sınıf öğrencisi ve bu sene mezun olacak..."
  },
  {
    "role": "user",
    "text": "Başka ne öğrenmeliyim"
  },
  {
    "role": "assistant",
    "text": "Sistem tasarımı ve mimari konularını öğren..."
  }
]
```

İki `system` mesajı: biri asıl prompt, biri özet.

### Dört yükleme durumu

```csharp
Directory.CreateDirectory(historyFolder);          // klasör yoksa oluştur (idempotent)

if (!File.Exists(historyPath))                     // 1. dosya yok
    await File.WriteAllTextAsync(historyPath, "[]");
else
{
    var json = await File.ReadAllTextAsync(historyPath);

    if (string.IsNullOrWhiteSpace(json)) { ... }   // 2. dosya boş
    else
        try
        {
            savedMessages = JsonSerializer.Deserialize<List<SavedMessage>>(json, jsonOptions) ?? new();

            if (savedMessages.Any(s => string.IsNullOrWhiteSpace(s.Role)
                                    || string.IsNullOrWhiteSpace(s.Text)))
                savedMessages = new List<SavedMessage>();   // 3. şema yanlış
        }
        catch (JsonException) { ... }                       // 4. sözdizimi bozuk
}
```

Dördünde de program **çökmüyor** ve sebebi ekrana yazıyor.

### Örnek çıktı

```
[geçmiş dosyası yok — yeni dosya oluşturuldu]
Sen: Ben Mert İstanbul Nişantaşı Üniversitesinde YBS 4. sınıf öğrencisiyim...

Claude AI: LinkedIn profilini güçlendir, üniversitenin kariyer merkezine başvur...

input: 157  output: 145
Sen: üniversitenin kariyer merkezi bir işe yaramıyor

Claude AI: O zaman doğrudan şirketlere başvur, LinkedIn'de aktif ol...

input: 328  output: 119
...
input: 1233  output: 143
[bütçe aşıldı: 1233 > 1200 — geçmiş özetleniyor]
[12 mesaj tek özete indi]

Sen: Hafızanda ne var benle ilgili?

Claude AI: Senin hakkında bildiğim: İstanbul Nişantaşı Üniversitesi YBS 4. sınıf
öğrencisi, .NET backend ve Claude Code ile frontend deneyimli, AI/LLM entegrasyonu
becerilerine sahip...

input: 542  output: 138
```

Son cevaptaki bilgilerin tamamı **özetlenip silinmiş** mesajlardan geliyor.

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** Programı kapatıp açtığında sohbet kaldığı yerden devam etsin — geçmiş JSON'a kaydedilip geri yüklensin.

### Hangi stratejiyi ne zaman?

| Strateji | Seç, eğer… | Bedeli |
|---|---|---|
| **Sliding window** | Eski mesajlar gerçekten gereksizse (ör. tek seferlik komutlar) | Bilgi **kalıcı olarak kaybolur** |
| **Özetleme** | Kullanıcı hakkındaki bilgi korunmalıysa | Ekstra LLM çağrısı + kayıplı sıkıştırma |
| **Kalıcılık** | Oturumlar arası devamlılık gerekiyorsa | Disk I/O + kişisel veri sorumluluğu |

Üçü birbirinin alternatifi değil, **katmanı**. Gerçek bir chat uygulamasında üçü de olur.

### Ne öğrendim

**1. LLM'in hafızası yok, token eğrisi bunun kanıtı.**
Version 1'de input token: `28 → 133 → 248`. Her turda arttı, çünkü geçmiş her çağrıda baştan gidiyor. Model 2. turda kendisi de söyledi: *"Her konuşma benim için yeni başlar."*

**2. Sliding window sadece unutturmaz — modeli kendi cevabına güvenmez hâle getirir.**
Version 1'de "Ben Mert İstanbul'da yaşıyorum" mesajı pencereden düştü. Modelin **kendi cevapları** (*"Adın Mert"*) hâlâ pencerede olmasına rağmen şöyle dedi:
> *"Özür dilerim, yanılmışım... Ben bu bilgileri uydurdum, ki bu yanlış bir davranıştı."*

Kanıtı göremeyince doğru cevabını da inkâr etti. Sohbet özür dileyerek tutarsızlaştı.

**3. Mesaj sayısı yanlış ölçü, token doğru ölçü.**
Version 1'de `geçmiş: 6 mesaj` dört tur sabit kaldı ama input `248 → 227 → 152 → 97` gitti. Token'ı belirleyen mesaj **sayısı** değil **uzunluğu**. Kullanıcı 500 kelimelik metin yapıştırsa mesaj sayısı aynı kalır, token 10 katına çıkar.

**4. Bütçe, bir turun minimum bağlamından büyük olmalı.**
İlk denemede `tokenBudget = 400` yaptım. Özetleme **her turda** tetiklendi — yani her tura fazladan bir API çağrısı bindi, maliyet düşmek yerine arttı.

Sebep matematikseldi: özetlemeden sonra bile elde `system + özet + son soru + son cevap ≈ 685 token` kalıyordu. 400'e inmek **imkânsızdı.**

Bütçeyi 1200'e çıkarıp `MaxOutputTokens`'ı 500 → 150 indirince özetleme 9 turda bir tetiklendi. **Doğru davranış bu.**

**5. Özetleme kayıplı bir sıkıştırmadır — neyi kaybedeceğini sen seçersin.**
12 mesaj tek paragrafa indi, input `1233 → 453` (%63 düşüş). Ama detaylar gitti: "Kaggle/LeetCode pratik yap", "remote staj ara" gibi öneriler özete girmedi.

> Sliding window bilgiyi **atar**, özetleme **sıkıştırır**. İkisi de kayıp — ama özetlemede neyin korunacağı `summarize.txt`'de yazılı, yani senin kontrolünde.

**6. Kütüphane tipini doğrudan kalıcılaştıramazsın.**
`ChatMessage`'ı `JsonSerializer.Serialize` ile yazmak işe yaramadı. Kendi `record SavedMessage(string Role, string Text)` tipimi yazıp iki yönlü çeviri yapmam gerekti. Backend'deki DTO ↔ entity mantığının aynısı: **kalıcılaştırırken ihtiyacın olanı saklarsın, tipin tamamını değil.**

**7. 🔴 Geçerli JSON, geçerli veri demek değil — ve bu bana veri kaybettirdi.**
Test için dosyadaki `"text"` anahtarlarını `"asd"` gibi bozdum. Sonra:

- `Deserialize` **hata atmadı** — dosya geçerli JSON, sadece alan adları yanlıştı
- `Text` alanı `null` oldu, mesajlar boşaldı
- Model hiçbir şey hatırlamadı
- Program tur sonunda **boş hâli aynı dosyanın üzerine yazdı** → geçmiş kalıcı olarak gitti

`catch (JsonException)` bunu yakalamaz; o sadece **sözdizimi** hatasını görür. Şema doğrulaması ayrı bir iştir. Yüklenen mesajlarda boş alan varsa dosyayı reddeden 5 satırlık kontrol ekledim.

İki ayrı ders: **(a)** parse edilebilmek doğru olmak değildir, **(b)** doğrulamadan üzerine yazma.

Bu, Modül 3'ün (`StructuredOutput`) var olma sebeplerinden biri.

**8. Sistem prompt'u ölçülebilir yazmak işe yarıyor.**
Model `MaxOutputTokens = 500`'ü sonuna kadar kullanıp başlıklı, tablolu markdown raporları yazıyordu — oysa prompt *"kısa ve net"* diyordu. *"En fazla 3 cümle. Başlık, tablo, madde işareti ve emoji kullanma."* yazınca uydu. Modül 1'in dersi tekrar: **belirsiz bıraktığın alanı model kendi doldurur.**

## ⚠️ Dikkat edilecekler

- **Geçmiş her çağrıda yeniden gönderilir ve yeniden ödenir.** Uzun sohbet = artan fatura. Yönetmezsen fatura kendiliğinden yönetilmez.

- **Sliding window'da `System` mesajını asla düşürme** ve mesajları **çift çift** düşür. Tek düşürürsen cevabı olmayan soru kalır, model akışı kaybeder.

- **Bütçeyi `MaxOutputTokens`'tan büyük seç.** Aksi hâlde özetleme her turda tetiklenir ve maliyeti azaltmak yerine artırır.

- **Özetleme ücretsiz değil.** Kendi API çağrısı var. Bu örnekte ~2 turda amorti ediyor, ama kısa sohbetlerde zarar.

- **`catch (JsonException)` şema doğrulaması yapmaz.** Alan adları yanlışsa sessizce `null` gelir. Yüklediğin veriyi ayrıca kontrol et.

- **Doğrulamadan üzerine yazma.** Bozuk veriyi yükleyip aynı dosyaya kaydedersen orijinali kaybedersin — bu projede birebir yaşandı.

- **Sohbet geçmişi kişisel veridir.** `json/` mutlaka `.gitignore`'da olmalı.

- **`bin/` altına yazma.** `dotnet clean` veya "Rebuild" o klasörü siler, verin gider. Bu projede `AppContext.BaseDirectory` + `..` ile proje köküne yazılıyor — **geliştirme kolaylığı için**, üretimde kalıcı veri `AppData` / `~/.config` gibi işletim sisteminin gösterdiği yere yazılır.

---

Hazırlayan: Mert Ağralı 👨‍💻
