# 🧠 Embeddings — Metni sayıya çevirmek ve anlamı ölçmek

## 📖 Nedir?

Beş modüldür modele **metin** gönderip **metin** aldık. Bu modül farklı bir şey yapıyor: metni **sayı dizisine** çevirip iki metnin *anlamca* ne kadar yakın olduğunu ölçüyor.

> **Embedding:** bir metni, anlamını temsil eden bir sayı vektörüne çeviren işlem. Benzer anlamlı metinler vektör uzayında birbirine yakın düşer.

Bu modülde **chat modeli yok** — soru sormuyoruz, cevap almıyoruz, sadece ölçüyoruz.

Ve ilk kez **Claude yok.** Anthropic API embedding vermiyor; her şey Ollama ile yerelde çalışıyor:

- **API key gerekmiyor**
- **Maliyet $0.00**
- İnternet bile gerekmiyor
- Provider değişti ama `IEmbeddingGenerator` soyutlaması sayesinde kod kalıbı tanıdık kaldı

Ayrıca ilk **Web API** modülü — Modül 0–4 console'du.

### Üç kavram

| Kavram | Ne yapar |
|---|---|
| **`IEmbeddingGenerator<string, Embedding<float>>`** | `IChatClient`'ın embedding karşılığı. `<girdi tipi, çıktı tipi>` |
| **Corpus embed'leme** | Cümleleri tek çağrıda vektöre çevirmek. `bge-m3` → **1024** boyut |
| **Cosine similarity** | İki vektör arasındaki açının kosinüsü. `1` = aynı anlam, `0` = alakasız |

💡 **Kısacası:** Embedding, anlamı sayıya çevirir. Ama hangi model, hangi dilde işe yarıyor — bunu **ölçmeden** bilemezsin.

## 🧩 Nasıl çalışır?

```
corpus.txt (50 cümle)  →  50 vektör        [açılışta, bir kez]
                              ↓
GET /search?q=...      →  sorgu vektörü    [her istekte]
                              ↓
                      50 cosine hesabı → sırala → ilk N
```

## 🔧 1. Gerekli paketler

```bash
dotnet add package OllamaSharp
```

Tek paket. `Microsoft.Extensions.AI.Abstractions` transitif geliyor; `IEmbeddingGenerator` ve `Embedding<T>` oradan.

**Ollama kurulumu:**
```bash
ollama pull bge-m3
```

`nomic-embed-text` **kullanma** — Türkçe'de çalışmıyor, aşağıda ölçümü var.

## ⚙️ 2. Ayarlar

API key yok. Sadece Ollama'nın açık olması gerekiyor (`localhost:11434`).

`.csproj`'a bir satır — corpus'un `bin/`'e kopyalanması için:

```xml
<None Update="corpus.txt" CopyToOutputDirectory="PreserveNewest" />
```

## 💻 3. Kullanım

### Kurulum ve DI

```csharp
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3"));
```

`<string, Embedding<float>>` = **ne veriyorum, ne alıyorum**. `Dictionary<string, int>` gibi düşün.

`Singleton` çünkü embedder durumsuz — her istekte yeni bağlantı kurmanın anlamı yok.

### Corpus'u bir kez embed'lemek

```csharp
var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var corpusEmbeddings = await embedder.GenerateAsync(corpus);
```

Satır 1: DI kutusuna koyduğumuz nesneyi **geri alıyoruz**. Endpoint içinde olsaydık parametre olarak gelirdi; burada düz kod olduğumuz için elle istiyoruz.

Satır 2: `app.Run()`'dan **önce** çalışır — yani açılışta bir kez. Endpoint'in içinde olsaydı her istekte 50 cümle boşuna yeniden embed'lenirdi.

### Tek sorguyu embed'lemek

```csharp
var queryEmbedding = (await generator.GenerateAsync([q]))[0];
```

Açılımı:
```csharp
var inputs = new[] { q };                                // 1 elemanlı liste
var embeddings = await generator.GenerateAsync(inputs);  // 1 elemanlı sonuç
var queryEmbedding = embeddings[0];                      // tek elemanı al
```

`GenerateAsync` **toplu** çalışır: liste ister, liste döner. Tek cümle göndersen bile listeye koyman gerekir.

### Cosine similarity — elle

```csharp
float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
{
    float dot = 0, magnitudeA = 0, magnitudeB = 0;

    for (int i = 0; i < a.Length; i++)
    {
        dot        += a[i] * b[i];
        magnitudeA += a[i] * a[i];
        magnitudeB += b[i] * b[i];
    }

    return dot / (MathF.Sqrt(magnitudeA) * MathF.Sqrt(magnitudeB));
}
```

1024 boyut hayal edilemez — **2 boyutla** aynı formül:

| Örnek | Hesap | Sonuç |
|---|---|---|
| `a=[3,4]` `b=[6,8]` *(aynı yön, farklı uzunluk)* | `50 / (5 × 10)` | **1.0** — aynı anlam |
| `a=[1,0]` `b=[0,1]` *(dik)* | `0 / (1 × 1)` | **0** — alakasız |

`b`, `a`'nın iki katı uzunlukta ama sonuç yine `1.0`. **Bölme uzunluğu eler, geriye sadece açı kalır** — "uzun olmak alakalı olmak değildir."

> `System.Numerics.Tensors` paketinde hazır `TensorPrimitives.CosineSimilarity` var ve SIMD ile çok daha hızlı. Kasten kullanmadım — formülün 5 satır olduğunu görmek için.

### Endpoint

```csharp
app.MapGet("/search", async (string? q, IEmbeddingGenerator<string, Embedding<float>> generator, int top = 5) =>
```

| Parametre | Nereden |
|---|---|
| `q` | Query string. **Nullable** — `string q` olsaydı ASP.NET kendi 400'ünü döner, bizim mesajımız çalışmazdı |
| `generator` | DI container |
| `top` | Query string. **Varsayılan değerli parametre en sonda olmak zorunda**, yoksa C# derlemez |

### Örnek çıktı

```
GET /search?q=Yağmur yağacak mı&top=5

[{"sentence":"Yarın İstanbul'da sağanak bekleniyor.","score":0.7271},
 {"sentence":"Öğleden sonra gök gürültülü sağanak var.","score":0.7009},
 {"sentence":"Rüzgar akşama doğru şiddetini artıracak.","score":0.6986},
 {"sentence":"Gece sıcaklıkları sıfırın altına düşecek.","score":0.6930},
 {"sentence":"Kar yağışı hafta sonu başlıyor.","score":0.6904}]
```

İlk 5'in tamamı hava durumu grubundan. Sorguda `sağanak` kelimesi hiç geçmiyor — `LIKE` bulamazdı.

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** 50 cümlelik corpus (5 grup × 10 cümle: hava, yazılım, yemek, spor, hayvan) üzerinde arama API'si + iki ölçüm.

**Ölçüm yöntemi:** 5 sorgu, her gruptan biri. Her sorgu için **ilk 5 sonucun kaçı doğru gruptan**. Rastgele seçimin beklentisi: `5 × 10/50 = 1.0` per sorgu, yani **%20**.

### 🔴 Ölçüm 1 — Model seçimi her şeyi değiştirdi

| Model + dil | Doğru oran | Rastgele |
|---|---|---|
| `nomic-embed-text` + **Türkçe** | **%20** | %20 |
| `nomic-embed-text` + **İngilizce** | **%95** | %20 |
| `bge-m3` + **Türkçe** | **%70** | %20 |

Aynı 50 cümle, aynı 5 sorgu, aynı kod. Sadece model ve dil değişti.

**`nomic-embed-text` Türkçe'de rastgele seçimle birebir aynı sonuç verdi.** Yani hiçbir şey öğrenmiyor. Aynı corpus'u İngilizce'ye çevirince %95'e fırladı → **sorun modelin dili, kodum değil.**

`bge-m3` (çok dilli) ile Türkçe %70'e çıktı — model adı değişikliği **tek satır**.

**Neden bazı sorgular "çalışıyor" gibi görünüyordu:**
```
q=test          → "Unit testlerin hepsi başarıyla geçti."   ✓  (test ↔ testlerin)
q=Akşam yemeği  → "Akşama mercimek çorbası yaptım."          ✓  (Akşam ↔ Akşama)
```
Model anlamı değil, **yüzeydeki harf benzerliğini** yakalıyordu. Anlam gerektiren sorgularda çuvallıyordu.

> **"Embedding modeli" diye bir şey yok — senin diline uygun embedding modeli var.** Ve bunu ölçmeden bilemezsin.

### 🔴 Ölçüm 2 — "Yapılması gereken" önek hiçbir şey yapmadı

`nomic-embed-text`'in model kartı görev öneki istiyor: belgeler `search_document: `, sorgular `search_query: `. Kodladım, üç senaryoda da ölçtüm:

| | `prefix=true` | `prefix=false` |
|---|---|---|
| nomic + Türkçe | %16 | %20 |
| nomic + İngilizce | %76 | %80 |
| bge-m3 + Türkçe | %70 | %70 |

**Üç testte de ölçülebilir fark yok** — hatta öneksiz hafifçe daha iyi. Muhtemelen Ollama şablonu öneki kendisi ekliyor ve biz ikinci kez ekleyerek gürültü yaratıyorduk.

Ölçtükten sonra önek kodunu **sildim.**

> Dokümanda yazması, senin durumunda işe yaradığı anlamına gelmiyor. Modül 1'de few-shot'ın skoru düşürmesiyle aynı ders.

### 🔴 Ölçüm 3 — Eşik değerle filtreleme imkânsız

`Şemsiye almalı mıyım` sorgusu, tüm 50 cümle skorlandı:

| | |
|---|---|
| En yüksek skor | 0.6504 |
| En düşük skor | 0.4951 |
| Toplam aralık | **0.1553** |
| `score > 0.5` olan | **49 / 50 (%98)** |

```csharp
if (score > 0.5) { /* alakalı say */ }    // corpus'un %98'ini geçirir
```

**Cosine skorunun mutlak değeri anlamsızdır, anlamlı olan sıralamadır.** Alakasız bir cümle de 0.62 alabiliyor — aynı dil, benzer cümle yapısı vektörleri birbirine yaklaştırıyor.

Bu yüzden RAG'de **top-K** kullanılır, eşik değil: "en yakın 3 tanesini getir, alakalı mı diye modele sor."

### 🔴 Ölçüm 4 — Embedding benzerlik ölçer, akıl yürütmez

`bge-m3` ile 5 sorgudan 4'ü çalıştı. Biri tamamen başarısız:

```
Şemsiye almalı mıyım  → "Kahvaltıda menemen yaptık."  0.5762   ❌  (ilk 5'te 0 doğru)
Yağmur yağacak mı     → "Yarın İstanbul'da sağanak bekleniyor."  0.7271  ✅  (ilk 5'i tam)
```

Aynı corpus, aynı model, aynı an. Fark **sorguda**:

- *"Yağmur yağacak mı"* → **anlamsal komşuluk**: yağmur ↔ sağanak. Model bunu biliyor.
- *"Şemsiye almalı mıyım"* → **çıkarım gerekiyor**: şemsiye → yağmur → sağanak. Embedding bunu yapmaz.

> İki metin *aynı şeyden bahsediyorsa* yakın çıkar. Biri diğerinin *sonucuysa* yakın çıkmaz.

Modül 6'da işine yarayacak: retrieval kullanıcının sorusunu **olduğu gibi** arar. Kullanıcı "şemsiye" derse yağmur belgesini bulamayabilirsin — sorguyu genişletmek (*query expansion*) ayrı bir teknik.

### Kavramlar

Bu modülde embedding'in ne olduğunu, `IEmbeddingGenerator` ile Ollama'ya bağlanmayı, cosine similarity formülünü elle yazmayı, minimal API + DI iskeletini ve en önemlisi **bir embedding modelinin işe yarayıp yaramadığını ölçmeyi** öğrendim.

## ⚠️ Dikkat edilecekler

- **🔴 Embedding modelini dilini test etmeden seçme.** `nomic-embed-text` Türkçe'de rastgele seçim kadar başarılı. Çok dilli iş için `bge-m3`.

- **Ölçüm yöntemi basit olabilir:** corpus'u gruplara böl, "ilk N sonucun kaçı doğru gruptan" say, rastgele beklentiyle karşılaştır. Yarım saatlik iş, kesin cevap.

- **Skor eşiğiyle filtreleme yapma.** Alakasız içerik de yüksek skor alır. **Top-K** kullan.

- **Corpus'u bir kez embed'le**, açılışta. Her istekte tekrar embed'lemek aynı işi boşuna yapmaktır.

- **Embedding akıl yürütmez.** "Şemsiye" ile "yağmur" arasında çıkarım yapmaz; sadece benzerlik ölçer.

- **Brute force bu ölçekte yeterli.** 50 cümle = 50 karşılaştırma. Milyonlarca belgede vektör veritabanı gerekir.

- **Dokümanda yazan her şeyi ölçmeden uygulama.** Model kartındaki önek tavsiyesi bizim durumumuzda hiçbir fark yaratmadı.

- **`corpus.txt`'yi `CopyToOutputDirectory` ile taşı.** Yoksa program çalışma anında dosyayı bulamaz.

---

Hazırlayan: Mert Ağralı 👨‍💻
