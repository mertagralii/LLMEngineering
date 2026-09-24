# 🧠 LLM Engineering — Sıfırdan öğrenme yolculuğu

.NET backend geliştirici olarak LLM Engineering'i **sıfırdan** öğrendiğim çalışma alanı.

Her modül çalışan bir projedir: kendi `README.md`'si, kendi ölçümleri ve kendi hatalarıyla. Hiçbir modül "şunu yapmak lazım" demiyor — **ölçtüğüm sonucu** yazıyor. Yanlış çıkan tahminlerim de içinde.

```bash
git clone https://github.com/mertagralii/LLMEngineering.git
cd LLMEngineering
dotnet build
```

---

## Modüller

| # | Modül | Konu | Bu modülde öğrendiğim tek cümle |
|---|---|---|---|
| 0 | [LlmBasics](Modul-0-LlmBasics) | Token, rol, temperature, maliyet | Metin gönderirsin, token'a bölünür, **her iki yönü de ayrı ödersin** |
| 1 | [PromptEngineering](Modul-1-PromptEngineering) | Zero-shot / few-shot / CoT | Hangi tekniğin işe yaradığını **tahminle değil ölçerek** belirle |
| 2 | [ChatMemory](Modul-2-ChatMemory) | Sliding window, özetleme, kalıcılık | Geçmiş her çağrıda **yeniden gider ve yeniden ödenir** |
| 3 | [StructuredOutput](Modul-3-StructuredOutput) | Şema, validasyon, retry | Şema **tipi** garanti eder, **anlamı** etmez |
| 4 | [FunctionCalling](Modul-4-FunctionCalling) | Araçlar ve yetkinin sınırı | Araca verdiğin yetki, **modelin yetkisidir** |
| 5 | [Embeddings](Modul-5-Embeddings) | Metni sayıya çevirmek | Hangi modelin **hangi dilde** işe yaradığını ölçmeden bilemezsin |
| 6 | [SimpleRag](Modul-6-SimpleRag) | Belgeye dayalı cevap | Embedding "getir" der, **model "işe yarar mı" der** |
| 7 | [SimpleAgent](Modul-7-SimpleAgent) | Araç döngüsü, durma koşulu | Modele cevabı değil, **arama yeteneğini** ver |
| 8 | [LlmEvals](Modul-8-LlmEvals) | Eval seti, regresyon, LLM-as-judge | **Toplam skor regresyonu gizler** |
| 9 | [LlmProduction](Modul-9-LlmProduction) | Retry, cache, log, prompt caching | "Çalışıyor mu" değil, **"bozulunca ne olur, kaça mal olur"** |

Her modülün README'si aynı düzende: *Nedir → Nasıl çalışır → Paketler → Ayarlar → Kullanım (çalışan iskelet dahil) → Ölçümler → Dikkat edilecekler*.

---

## Stack

| Katman | Seçim | Neden |
|---|---|---|
| Dil / runtime | C# / .NET 10 | Ana stack'im |
| LLM soyutlaması | `Microsoft.Extensions.AI` (`IChatClient`, `IEmbeddingGenerator`) | Sağlayıcı-bağımsız; provider değişse kod değişmiyor |
| Chat modeli | Claude API — resmî `Anthropic` NuGet paketi | `claude-haiku-4-5` (ucuz ve hızlı) |
| Embedding | Ollama `bge-m3` — yerel, ücretsiz | Anthropic embedding vermiyor; ayrıca Türkçe'de `nomic`'ten çok daha iyi (Modül 5'te ölçüldü) |
| Veritabanı | EF Core + SQLite | Modül 4'te function calling için |

## Proje tipleri

| Modül | Tip |
|---|---|
| 0–4 | Console |
| 5, 6 | Web API (Minimal API) |
| 7 | Web API (Controller) |
| 8, 9 | Console (CLI — komutla çalışır, dosyaya yazar) |

Modül 8 ve 9 bilerek console: eval runner ve middleware demoları doğası gereği CLI.

---

## Çalıştırma

Her modülün kendi README'sinde ayrıntısı var. Genel akış:

```bash
# 1. API key
#    Modül klasöründeki Program.cs'te "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR"
#    yazan yere kendi key'ini yapıştır.

# 2. Embedding kullanan modüller için (5, 6, 7)
ollama pull bge-m3
ollama serve

# 3. Çalıştır
dotnet run --project Modul-6-SimpleRag
```

**API key gerektirmeyen iki demo:**

```bash
dotnet run --project Modul-9-LlmProduction -- retry                    # sahte istemciyle retry testi
dotnet run --project Modul-8-LlmEvals -- compare results/A.json results/B.json   # dosya okur, model çağırmaz
```

---

## Ölçümler

Modüller boyunca ölçtüklerim. Hepsi ilgili README'de ham çıktısıyla duruyor.

| Ölçüm | Sonuç | Nerede |
|---|---|---|
| Zero-shot / few-shot / CoT, 10 vaka | 9/10 · 7/10 · 9/10 | [Modül 1](Modul-1-PromptEngineering) |
| Aynı üçlü, 20 vaka | 16/20 · 15/20 · 18/20 | [Modül 8](Modul-8-LlmEvals) |
| `nomic-embed-text` vs `bge-m3`, Türkçe | %20 (rastgele) → %70 | [Modül 5](Modul-5-Embeddings) |
| Chunk'a belge başlığı eklemek | Doğru belgeden gelen parça 3/4 → 4/4 | [Modül 6](Modul-6-SimpleRag) |
| Agent: çok konulu soru | Modül 6'da ❌, Modül 7'de ✅ (2 ayrı arama) | [Modül 7](Modul-7-SimpleAgent) |
| Araç kimliğinden emoji çıkarmak | 12 adım → 4 adım, token −%45 | [Modül 7](Modul-7-SimpleAgent) |
| Yanıt önbelleği | 1153 ms → **3 ms** | [Modül 9](Modul-9-LlmProduction) |
| Prompt caching, 2. çağrı | 7.753 token sunucudan okundu, %90 ucuz | [Modül 9](Modul-9-LlmProduction) |

---

## Yanlış çıkan tahminlerim

Bunlar README'lerde duruyor çünkü **öğrendiğim asıl şeyler bunlar.**

**"Few-shot daha iyidir."** Değilmiş. Modül 1'de 7/10 ile en kötüsü çıktı, Modül 8'de 20 vakada da: *düzelen sıfır*, bozulan bir, üstelik 1,8 kat pahalı.

**"Skor eşiğiyle alakasız sonuçları eleyebilirim."** Elenmiyor. `score > 0.5` corpus'un **%98'ini** geçirdi. Cosine skorunun mutlak değeri anlamsız, anlamlı olan sıralama.

**"Toplam skor arttıysa iyileşme vardır."** Yok. CoT 16/20 → 18/20 çıktı ama **bir vaka bozuldu** — ortalama onu yuttu.

**"`Temperature = 0` aynı çıktıyı verir."** Vermiyor. Aynı skor, aynı yanlışlar, ama **farklı metin**. Etiket kararlı, serbest metin değil.

**"Cache hit'te token 0 görünür."** Görünmüyor. Cache `Usage`'ı da saklıyor — maliyet sayacını yanlış yere koyarsan faturayı olduğundan büyük raporlarsın.

**"Cache = ucuz."** Prompt caching'in **ilk çağrısı %25 pahalı**. Başa baş 2. çağrıda; tek seferlik işte açmak zarar.

**"`AnthropicServiceException` = 5xx demek."** Değilmiş, **tüm API hatalarının atası**. 400 ve 401 de ona takıldı, kalıcı hatalar 3 kez denendi. Kod derlendi, çalıştı, sessizce yanlış davrandı.

---

## Tekrar eden üç ders

**1. Ölç, tahmin etme.** Modül 1'den 9'a kadar her modülde en az bir tahminim yanlış çıktı. Hiçbiri kod okuyarak anlaşılmadı — hepsi çalıştırıp sayıya bakınca çıktı.

**2. Beklenen değeri yaz.** Modül 9'da `Çağrı: 3 (1 olmalı)` satırındaki parantez olmasaydı hata gözümden kaçacaktı. Çıktıya beklentiyi yazmak, bakmayı zorunlu kılıyor.

**3. Soyutlama gerçekten işe yarıyor.** `IChatClient` bir arayüz olduğu için Modül 9'da retry/cache/log katmanlarını üstüne takabildim ve çağıran kod hiç değişmedi. OpenTelemetry etiketleri de (`gen_ai.*`) standart — sağlayıcı değişse grafikler çalışmaya devam eder.

---

Hazırlayan: Mert Ağralı 👨‍💻
