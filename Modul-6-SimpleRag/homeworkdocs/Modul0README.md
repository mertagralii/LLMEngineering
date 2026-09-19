# 🧠 LlmBasics — Claude API'ye ilk istek: token, rol, temperature ve maliyet

## 📖 Nedir?

Bu modül LLM Engineering yolculuğunun sıfır noktası: .NET'ten Claude API'ye ilk isteği atmak ve bir LLM çağrısının **neyden oluştuğunu ve ne kadara mal olduğunu** kendi gözümle görmek.

Ama tek bir istek atıp "çalıştı" demek yeterli değildi. Onun yerine küçük bir deney kuruldu: **aynı soru, iki farklı modele, üç farklı temperature ile** soruldu. Toplam 6 çağrı. Değişen tek şey model ve temperature; system prompt, user prompt ve `MaxOutputTokens` altı çağrıda da sabit tutuldu ki farkın nereden geldiği belli olsun.

Neden böyle bir deney? Çünkü LLM mühendisliğinde verilen ilk ve en pahalı karar **model seçimidir**, ve bu karar hisle değil ölçümle verilir. "Sonnet daha iyi" cümlesinin pratikte bir karşılığı yok; "Sonnet bu görevde 3.75 kat pahalıya neredeyse aynı cevabı veriyor" cümlesinin var. Bu modülün asıl öğrettiği şey API çağrısı yapmak değil, **çağrının maliyetini ölçmek ve modeller arasında karşılaştırma yapabilmek**.

Yol boyunca karşılaşılan kavramlar:

- **Token** — modelin işleyebildiği en küçük metin birimi. Bazen tam bir kelime, bazen bir hece, bazen tek bir noktalama işareti. Faturayı belirleyen şey karakter sayısı değil, token sayısıdır.
- **System prompt** — asistanın kim olduğunu ve nasıl davranacağını söyleyen mesaj. Her istekte en başa konur ve sohbet boyunca sabit kalır.
- **User prompt** — kullanıcının sorusu. Modelin cevabı `Assistant` rolüyle döner.
- **Temperature** — modelin cevap üretirken ne kadar "risk alacağını" belirleyen 0–1 arası sayı. 0'a yakın = kararlı ve tekrarlanabilir, 1'e yakın = daha çeşitli ama daha az öngörülebilir.
- **MaxOutputTokens** — modelin en fazla kaç token'lık cevap üretebileceğinin tavanı. Aynı zamanda maliyet freni.
- **Input / output fiyatlandırma** — gönderdiğin ve aldığın token'lar ayrı ayrı fiyatlanır. API sana fiyat döndürmez; sadece token sayısını verir, çarpımı sen yaparsın.

💡 **Kısacası:** LLM'e metin gönderirsin, o metni token'lara böler, cevabı token olarak üretir ve sen her iki yönü de ayrı ayrı ödersin.

## 🧩 Nasıl çalışır?

| Adım | Ne oluyor |
|---|---|
| 1 | `AnthropicClient` API key ile kurulur |
| 2 | `AsIChatClient(model)` ile provider-bağımsız `IChatClient`'a sarılır |
| 3 | `System` + `User` mesajları hazırlanır (6 çağrıda da aynı) |
| 4 | `ChatOptions` ile `Temperature` ve `MaxOutputTokens` ayarlanır |
| 5 | `GetResponseAsync` çağrılır |
| 6 | `response.Usage` içinden token sayıları okunur, maliyet hesaplanır |
| 7 | Sonuçlar hizalı bir tabloya ve toplam satırına yazılır |

## 🔧 1. Gerekli paketler

```bash
dotnet add package Anthropic
```

## ⚙️ 2. Ayarlar

API key `Program.cs` içinde tutuluyor:

```csharp
var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
```

Anthropic Console'dan aldığın key'i bu placeholder'ın yerine yapıştır.

## 💻 3. Kullanım

### İlk istek — en basit hâli

Modülün başladığı yer. Tek model, tek çağrı, cevabı ve token sayısını yazdır:

```csharp
using Anthropic;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
IChatClient chat = client.AsIChatClient("claude-haiku-4-5");

var messages = new List<ChatMessage>
{
    new(ChatRole.System, "Kısa ve net cevap veren bir asistansın."),
    new(ChatRole.User,   "Token nedir? Tek cümleyle.")
};

var options = new ChatOptions
{
    Temperature = 0.2f,
    MaxOutputTokens = 1024
};

var response = await chat.GetResponseAsync(messages, options);

Console.WriteLine(response.Text);
Console.WriteLine($"input: {response.Usage?.InputTokenCount}, output: {response.Usage?.OutputTokenCount}");
```

Çıktısı:

```
Token, bir dil modelinin metni işlerken kullandığı en küçük anlam birimi olup, bir kelime, kelime parçası veya noktalama işareti olabilir.
input: 35, output: 51
```

### Karşılaştırma versiyonu

Maliyet hesabı — input ve output **ayrı** fiyatlandığı için tek çarpım yetmez:

```csharp
decimal price = (response.Usage?.InputTokenCount ?? 0) / 1_000_000m * model.InputToken
              + (response.Usage?.OutputTokenCount ?? 0) / 1_000_000m * model.OutputToken;
```

Modeller ve fiyatları bir tuple listesinde tutulur; yeni bir model eklemek **tek satır**:

```csharp
var claudeModels = new[]
{
    (Name: "claude-haiku-4-5",  InputToken: 1.00m, OutputToken:  5.00m),
    (Name: "claude-sonnet-4-6", InputToken: 3.00m, OutputToken: 15.00m)
};
```

### Örnek çıktı

```
Model               Temp   Input  Output       Price
----------------------------------------------------
claude-haiku-4-5     0,0      35      41    0,000240
claude-haiku-4-5     0,7      35      47    0,000270
claude-haiku-4-5     1,0      35      50    0,000285
claude-sonnet-4-6    0,0      36      60    0,001008
claude-sonnet-4-6    0,7      36      59    0,000993
claude-sonnet-4-6    1,0      36      58    0,000978
----------------------------------------------------
TOPLAM                       213     315    0,003774
```

### Modellerin verdiği cevaplar

```
claude-haiku-4-5 | temp 0,0 | Token, bir dil modelinin işleyebileceği en küçük metin
birimi olup, bir kelime, hece veya sembol olabilir.

claude-haiku-4-5 | temp 0,7 | Token, bir dil modelinin metni işlerken kullandığı en
küçük anlam birimlerinden biridir ve bir kelime, hece veya sembol olabilir.

claude-haiku-4-5 | temp 1,0 | Token, bir metin parçasındaki küçük birim (kelime, harf,
sembol vb.) olup yapay zeka modellerinin metni işlemesi için kullanılır.

claude-sonnet-4-6 | temp 0,0 | Token, bir metin parçasının (kelime, hece veya karakter
grubu) yapay zeka modelleri tarafından işlenebilmesi için bölündüğü en küçük anlamlı
birimdir.

claude-sonnet-4-6 | temp 0,7 | Token, bir metin parçasının (kelime, hece veya karakter
grubu) yapay zeka modelleri tarafından işlenebilmesi için bölündüğü en küçük anlam
birimidir.

claude-sonnet-4-6 | temp 1,0 | Token, bir metnin yapay zeka modelleri tarafından
işlenebilmesi için bölündüğü küçük metin parçacıklarıdır (kelime, hece veya karakter
gibi).
```

Dikkat: Sonnet'in `0,0` ve `0,7` cevapları neredeyse birebir aynı — sadece "anlamlı birimdir" / "anlam birimidir" farkı var. Temperature yükselmesine rağmen model aynı cümleyi kuruyor.

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** Aynı soruyu iki modele üç farklı temperature ile sorup cevap, token ve maliyeti tek bir tabloya dökmek.

### Ne öğrendim?

**Maliyet nasıl hesaplanıyor**

- Bir LLM çağrısının maliyeti iki kalemin toplamı: **giriş (input) maliyeti + çıkış (output) maliyeti.**
- Giriş her zaman çıkıştan ucuz; çıkış ise girişin kat kat üstünde. Haiku 4.5'te giriş $1 / çıkış $5 (5 kat), Sonnet 4.6'da giriş $3 / çıkış $15 (yine 5 kat).
- Fiyatlar dolar cinsinden ve **1 milyon token başına** veriliyor. Bu yüzden tek bir çağrının maliyeti $0,000240 gibi çok küçük sayılar çıkıyor.
- `TotalTokenCount` üzerinden tek çarpım yapmak yanlış sonuç verir; giriş ve çıkış ayrı ayrı çarpılmalı.

**`MaxOutputTokens` bir maliyet kolu**

`MaxOutputTokens` ile modelin en fazla kaç token'lık çıktı üretebileceğini sınırlayabiliyorum. Bu sadece bir sınır değil, maliyetin **tavanını baştan bilmemi** sağlıyor. Maliyetin büyük kısmı çıkıştan geldiği için en etkili fren burası — prompt'u kısaltmak neredeyse hiçbir şey kazandırmıyor, cevabı kısaltmak her şeyi kazandırıyor.

**Temperature ve maliyet ilişkisi**

Temperature'ı yükseltmek çıktıyı uzatabiliyor, çıktı uzayınca da maliyet artıyor. Haiku'da bu net görüldü: output 41 → 47 → 50 token, maliyet $0,000240 → $0,000285. Ama Sonnet 4.6'da aynı şey olmadı (60 → 59 → 58). Yani temperature'ın maliyeti artırması garanti değil — modele ve sorunun ne kadar dar olduğuna bağlı.

**Kavramlar**

Bu modülde `temperature`'ın, `system prompt`'un ve `user prompt`'un ne olduğunu, bir modelin fiyatlandırmasının nasıl yapıldığını ve `MaxOutputTokens` ile maliyetin nasıl sınırlandığını öğrendim.

## ⚠️ Dikkat edilecekler

- **Yeni modellerde `temperature` yok.** Sonnet 5, Opus 5 ve Opus 4.7/4.8 `temperature` gönderirsen `400 — temperature is deprecated for this model` döner. Bu modeller adaptive thinking kullanıyor; rastgelelik yerine `effort` (low/medium/high/xhigh/max) parametresiyle çalışıyorlar. Bu yüzden karşılaştırmada Sonnet 5 yerine **Sonnet 4.6** kullanıldı.

- **Maliyetin ~%85'i output.** Sonnet'in 0.0 satırında input payı $0,000108, output payı $0,000900. Prompt'u kısaltmak neredeyse hiçbir şey kazandırmaz; **cevabı kısaltmak** her şeyi kazandırır.

- **Her modelin tokenizer'ı farklı.** Birebir aynı prompt Haiku'da 35, Sonnet'te 36 input token. Bir modelin token hesabını başka modele taşıma.

- **API fiyat döndürmez.** `response.Usage` sadece token sayısı verir. Fiyat çarpanları Anthropic'in fiyat listesinden gelir ve zamanla değişir.

---

Hazırlayan: Mert Ağralı 👨‍💻
