# 🧠 StructuredOutput — Şema, validasyon ve retry: model neden uydurur?

## 📖 Nedir?

Önceki iki modül aynı yaranın iki yüzünü gösterdi:

- **Modül 1:** prompt'a *"tek kelime, açıklama yok"* yazdık; model injection testinde üçünü birden çiğnedi → **prompt bir ricadır, şema değil.**
- **Modül 2:** dosyadaki alan adı bozulunca `Deserialize` hata atmadı, sessizce `null` verdi → **geçerli JSON, geçerli veri demek değil.**

Modül 3 ikisini de kapatıyor — ve kapatırken **yeni bir sorun** ortaya çıkarıyor: model, doğruyu bulmaya değil **validasyonu geçmeye** çalışır.

### Dört kavram

| Kavram | Ne yapar |
|---|---|
| **Şema** (`record` + `GetResponseAsync<T>`) | C# tipini verirsin, kütüphane ondan JSON şeması üretip API'ye gönderir, cevabı tipe geri çevirir |
| **`TryGetResult`** | Cevap şemaya uymadıysa exception yerine `false` döner (`int.TryParse` kalıbı) |
| **Validasyon** | Şema *"yaş bir sayıdır"* der, *"yaş 1–120 arasındadır"* diyemez. O senin işin |
| **Retry** | Validasyon düşerse hatayı konuşmaya ekleyip tekrar sor — **ama her hata için değil** |

💡 **Kısacası:** Şema tipi garanti eder, anlamı etmez. Anlamı sen doğrularsın — ve doğrulama biçimin modelin ne uyduracağını belirler.

## 🧩 Nasıl çalışır?

| Adım | Ne oluyor |
|---|---|
| 1 | Serbest metin `User` mesajı olarak gönderilir |
| 2 | `GetResponseAsync<InvoiceDto>` — şema modele gider |
| 3 | `TryGetResult` — parse oldu mu? |
| 4 | İş kuralları kontrol edilir, hata **sınıflandırılır** |
| 5 | *Model hatası* → hatayı konuşmaya ekle, tekrar sor |
| 6 | *Veri hatası* → retry yok, insana işaretle |

## 🔧 1. Gerekli paketler

```bash
dotnet add package Anthropic
dotnet add package Microsoft.Extensions.AI
```

🔴 **İkinci paket bu modülde ilk kez gerekiyor.** Modül 0–2 tek paketle (`Anthropic`) yürüdü; `Microsoft.Extensions.AI.Abstractions` transitif geliyordu. Ama structured output orada değil:

| Sembol | `...AI.Abstractions` | `Microsoft.Extensions.AI` |
|---|---|---|
| `ChatResponse<T>` · `TryGetResult` · `ChatClientStructuredOutputExtensions` | ❌ | ✅ |

## ⚙️ 2. Ayarlar

```csharp
var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
```

Bu modülde dosya okunmuyor; `prompts/` klasörü ve `.csproj` kopyalama ayarı yok.

## 💻 3. Kullanım

### En basit hâli — çalışan iskelet

```csharp
using Anthropic;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_KEY" };
IChatClient chat = client.AsIChatClient("claude-haiku-4-5");

var messages = new List<ChatMessage>
{
    new(ChatRole.System, "Verilen metinden kişi bilgisini çıkar."),
    new(ChatRole.User, "Ahmet Yılmaz 28 yaşında, Ankara'da yaşıyor.")
};

var response = await chat.GetResponseAsync<Person>(messages,
    new ChatOptions { Temperature = 0.0f });

if (response.TryGetResult(out var person))
{
    Console.WriteLine($"{person.Name} | {person.Age} | {person.City}");
}

record Person(string Name, int? Age, string City);
```

**Modül 1/2'den farkı tek satırda:** `GetResponseAsync<Person>`. Prompt'ta *"JSON ver"* diye yalvarmıyorsun — tip veriyorsun, kütüphane şemayı üretiyor.

`record` **dosyanın en sonunda** olmak zorunda: C#'ta top-level statement'lardan sonra tip tanımı gelir, tersi olmaz. (Modül 2'deki `SavedMessage` ile aynı kural.)

### `TryGetResult` — neden doğrudan sonuç değil

```csharp
if (!response.TryGetResult(out var person)) { /* şemaya uymadı */ }
```

Şema garanti değil; model yine de uymayan bir şey döndürebilir. `TryGetResult` bu durumda exception fırlatmaz, `false` döner. `int.TryParse` ile aynı kalıp — hata normal akışın parçası, istisna değil.

### Validasyon — şemanın yapamadığı

```csharp
if (string.IsNullOrWhiteSpace(invoice.Seller))        error = "Satıcı ismi olmak zorunda";
else if (invoice.Total <= 0)                          error = "Toplam sıfır veya eksi olamaz.";
else if (invoice.Subtotal + invoice.TaxAmount != invoice.Total) { ... }
else if (invoice.TaxAmount > invoice.Subtotal)        { ... }
```

Şema `decimal` der, `> 0` diyemez. **İş kuralı her zaman senin kodunda kalır.**

Hata mesajları **sayıları içeriyor** — çünkü bu mesaj modele gidiyor. *"Değer yanlış"* modele hiçbir şey söylemez; *"KDV (1800) ara toplamdan (1000) büyük"* söyler.

### Hata sınıflandırma + retry

```csharp
string? error = null;
bool isDataError = false;      // kaynak metin tutarsiz -> model duzeltemez
...
if (isDataError)
{
    Console.WriteLine("  ⚠ Kaynak metindeki veri tutarsız — insan kontrolü gerekiyor.");
    break;                      // retry YOK
}

messages.AddRange(response.Messages);   // modelin kendi hatali cevabi
messages.Add(new(ChatRole.User, $"Hata: {error} Düzelt ve tekrar ver."));
```

Son iki satırın **sırası kritik**: önce modelin kendi cevabı, sonra hata. Model neyi düzelteceğini göremezse düzeltemez — Modül 2'nin dersi (model sadece kendisine gönderilen listeyi görür).

### Örnek çıktı

```
--- ACME Bilişim Ltd. Şti. tarafından 15.03.2026 tarihinde kesilen A-2026-0147 numaralı fatura. Tutar 12.500,00 TL, KDV 2.250,00 TL dahil.
  ✓ ACME Bilişim Ltd. Şti. | A-2026-0147 | 15.03.2026
    ara toplam: 10250,00 | KDV: 2250,00 | toplam: 12500,00 TRY   (1. denemede)

--- Yıldız Matbaa'dan aldığım fatura, numara B-881. Toplam 4750 lira. Tarihini göremiyorum, silik.
  ✓ Yıldız Matbaa | B-881 | yok
    ara toplam: yok | KDV: yok | toplam: 4750 TRY   (1. denemede)

--- Fatura no: C-2026-9, Delta Yazılım A.Ş., 01.02.2026. Ara toplam 1000 TL, KDV 1800 TL, genel toplam 2800 TL
 1. deneme: KDV (1800) ara toplamdan (1000) büyük olamaz.
  ⚠ Kaynak metindeki veri tutarsız — model düzeltemez, insan kontrolü gerekiyor.
```

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** Serbest metinden fatura bilgisi çıkaran parser: `InvoiceDto` şeması, en az 3 iş kuralı, düzeltilebilir hatalarda retry.

### Ne zaman nullable, ne zaman retry?

| Soru | Cevap |
|---|---|
| Alan metinde olmayabilir mi? | **Evet → nullable.** Değilse model uydurmak zorunda kalır |
| Hatayı model düzeltebilir mi? | **Evet → retry.** Hayır → insana işaretle |
| Eksik veri hata mı? | **Hayır.** "Bilinmiyor" geçerli bir sonuçtur |

### Ne öğrendim

**1. 🔴 Şema modeli her alanı doldurmaya zorlar — olmayan veriyi de.**

`record Person(string Name, int Age, string City)` ile denedim. Metinde yaş yoktu:

```
1. deneme: Yaş 0 olamaz          ← model "bilmiyorum" demeye çalıştı
✓ Mehmet | 30 | İstanbul (2.)    ← model 30 UYDURDU
```

`int` zorunlu olduğu için model boş bırakamadı. Validasyon `0`'ı reddedince elinde tek çıkış kaldı: makul görünen bir sayı uydurmak. Ve uydurulmuş veri doğrulamadan geçtiği için artık **güvenilir görünüyor** — hiç doğrulama yapmamaktan daha tehlikeli.

**2. `int` → `int?` tek değişiklikle model dürüstleşti.**

Aynı model, aynı prompt, aynı temperature. Değişen tek şey şemanın ifade gücü:

| | `int Age` | `int? Age` |
|---|---|---|
| Sonuç | `30` — uydurma | `null` — dürüst |

> **Şemanı "bilinmiyor" durumunu ifade edebilecek şekilde tasarla.** Modele `null` deme imkânı vermezsen, doğruyu söylemesini imkânsız kılmış olursun.

**3. Validasyondan geçmek, doğru olmak değildir.**

Modül 1: *"prompt bir ricadır"*. Modül 2: *"geçerli JSON, geçerli veri değil"*. Buradaki üçüncüsü: **validasyondan geçmek doğru olmak değil.**

**4. 🔴 Model validasyonu geçmeye çalışır, doğruyu bulmaya değil.**

Fatura ödevinde bunun aynadaki hâlini gördüm. 3. metinde `KDV 1800 TL` **açıkça yazıyor**, ama ara toplamdan (1000) büyük olduğu için kuralı düşürüyor:

```
1. deneme: KDV (1800) ara toplamdan (1000) büyük olamaz.
2. deneme: ✓ ara toplam: 1000 | KDV: yok | toplam: 2800
```

Model 2. denemede o alanı **sildi** ve geçti. Bilgi kaybolmadı — model tarafından yok edildi.

| | Validasyon ne dedi | Model ne yaptı |
|---|---|---|
| Kişi (`int`) | "0 geçersiz" | Boşluğu **uydurdu** |
| Fatura (`decimal?`) | "1800 geçersiz" | Değeri **sildi** |

> **Retry'ın optimize ettiği hedef "geçerli çıktı"dır, "doğru çıktı" değil.** Bir kapı kapatırsan başka kapı arar.

Üstelik system prompt'taki *"uydurma, boş bırak"* talimatını **silmek için** kullandı. İyi niyetli bir kural, kaçış yolu hâline geldi.

**5. Retry yalnızca modelin düzeltebileceği hatalar için mantıklıdır.**

Hataları ikiye ayırdım:

```
model hatası  → retry   (yanlış okumuş olabilir)
veri hatası   → retry YOK, insana işaretle
```

3. metindeki sorun kaynak metinde: `1000 + 1800 = 2800` diye bir fatura fiziksel olarak yanlış. Model bu veriyi üretmedi, düzeltemez de. Ona tekrar sormak sadece silmeye/uydurmaya iter.

Çözümden sonra: **1 çağrı, bilgi kaybı yok, tutarsızlık raporlandı.**

**6. Toplamlar tutuyor diye veri doğru değildir.**

3. metinde `1000 + 1800 = 2800` — aritmetik **tutuyor**. Anormallik başka yerde: KDV, ara toplamın 1.8 katı. Türkiye'de KDV en fazla %20.

> Aritmetik tutarlılık ayrı, iş kuralına uygunluk ayrı şey. İkisini de kontrol et.

**7. Türkçe sayı formatını system prompt'a yazmak gerekti.**

`12.500,00` — nokta binlik, virgül ondalık. Model bunu `12.5` okuyabilirdi. System prompt'a açıkça yazınca doğru okudu.

**8. Model sana sormadan aritmetik yapabiliyor.**

1. metinde ara toplam yazmıyor; model `12500 − 2250 = 10250` hesaplayıp yazdı. Uydurma değil, **türetme** — ve doğru. Ama şunu bil: şemadaki boş bir alanı model hesaplamaya çalışabilir ve türetilmiş değerle kaynaktan okunmuş değeri ayırt edemezsin.

## ⚠️ Dikkat edilecekler

- **Olabilecek her eksik alanı nullable yap.** `?` koymazsan model uydurur; gereksiz koyarsan eksik veriyi fark etmezsin. Her `?` bir karardır, gerekçesini yorumda yaz.

- **Eksik veri hata değildir.** "Bilinmiyor" geçerli bir sonuçtur. Hata sayarsan modeli uydurmaya itersin.

- **Retry'dan önce hatayı sınıflandır.** Model düzeltebilir mi? Düzeltemeyeceği bir şeyi tekrar sormak hem para yakar hem veriyi bozar.

- **Hata mesajlarını modele yazıyormuş gibi yaz** — sayıları, alan adlarını ver. Model o mesajı okuyacak.

- **Retry'ı sınırla.** Sınırsız retry, bozuk girdide sonsuz döngü ve sonsuz fatura demek.

- **Şema tipi garanti eder, anlamı etmez.** `decimal` negatif olabilir, `DateTime` gelecekte olabilir, `string` boş olabilir. İş kuralı senin kodunda.

- **`double` değil `decimal`** — para hesabında `double` yuvarlama hatası yapar.

- **Bulgular bu modele ve bu metinlere ait.** Farklı model, farklı kaçış yolu bulabilir.

---

Hazırlayan: Mert Ağralı 👨‍💻
