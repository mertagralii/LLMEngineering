# 🧠 SimpleRag — Belgeye dayalı cevap: modelin bilmediğini ona vermek

## 📖 Nedir?

Modül 5'te arama motorunu kurduk: soru → en yakın cümleler. Ama iş orada bitiyordu, ham cümleler dönüyordu. Bu modül o aramayı bir **cevaba** dönüştürüyor.

> **RAG (Retrieval-Augmented Generation):** "getirmeyle güçlendirilmiş üretim". Modelin eğitim verisinde olmayan bilgiyi, **soru anında** ona vererek cevaplatmak.

Claude senin şirket politikanı, README'lerini, sözleşmelerini bilmiyor. Ama sen ona verirsen üzerine konuşabiliyor — fine-tuning'e gerek yok.

İlk kez **iki sağlayıcı aynı uygulamada** çalışıyor:

| Katman | Nerede | Maliyet |
|---|---|---|
| Arama (embedding) | Ollama `bge-m3`, yerelde | **$0.00** |
| Cevap üretimi | Claude API `claude-haiku-4-5` | ücretli |

### Dört kavram

| Kavram | Ne yapar |
|---|---|
| **Chunking** | Belgeyi anlamlı parçalara bölmek. `##` başlıklarından, elle — kütüphane yok |
| **Kaynak takibi** | Her parçanın nereden geldiği: `dosya.md#Başlık`. Cevapla birlikte döner |
| **RAG prompt'u** | Bulunan parçaları `<chunk id="1" source="...">` etiketleriyle modele vermek |
| **İki provider** | `IEmbeddingGenerator` → Ollama, `IChatClient` → Anthropic |

💡 **Kısacası:** Embedding "getir" der, model "bu işe yarar mı" der. RAG bu iş bölümünün adı.

## 🧩 Nasıl çalışır?

```
docs/*.md  →  ## başlıklarından parçala  →  vektörler     [açılışta, bir kez]
                                                ↓
POST /ask  →  soruyu embed'le  →  cosine hesabı           [her istekte]
                                                ↓
                                   en yakın 4 parça
                                                ↓
                       <chunk id="1" source="..."> ile paketle
                                                ↓
                              Claude  →  cevap + sources
```

## 🔧 1. Gerekli paketler

```bash
dotnet add package Anthropic
dotnet add package OllamaSharp
```

İkisi de `Microsoft.Extensions.AI.Abstractions`'ı transitif getiriyor — `IChatClient` ve `IEmbeddingGenerator` oradan.

**Ollama:**
```bash
ollama pull bge-m3
```

## ⚙️ 2. Ayarlar

Claude API key gerekiyor (embedding tarafı yerelde, ücretsiz).

`.csproj`'a kopyalama satırları — **bu adım atlanırsa program çalışma anında çöker:**

```xml
<None Update="docs\*.md"         CopyToOutputDirectory="PreserveNewest" />
<None Update="prompts\*.txt"     CopyToOutputDirectory="PreserveNewest" />
<None Update="homeworkdocs\*.md" CopyToOutputDirectory="PreserveNewest" />
```

Program `bin/Debug/net10.0/` içinden çalışır. Oraya kopyalanmayan hiçbir klasör görünmez:

```
System.IO.DirectoryNotFoundException: Could not find a part of the path
'.../bin/Debug/net10.0/homeworkdocs'
```

### Çalıştırma

```bash
ollama serve                              # açık değilse
dotnet run --project Modul-6-SimpleRag
```

Konsolda parça listesini görünce hazır:

```
42 parça embed'lendi:
  - Modul5README.md#📖 Nedir?
  ...
```

Port `Properties/launchSettings.json`'da tanımlı: **5058**. (Binary'yi `bin/` içinden doğrudan çalıştırırsan `launchSettings` okunmaz, varsayılan **5000** olur.)

```bash
curl -X POST http://localhost:5058/ask -H "Content-Type: application/json" \
  -d '{"question":"Yıllık izin kaç gün?"}'
```

`Program.cs` iki `#region` içeriyor:

| Region | Kaynak klasör | Durum |
|---|---|---|
| `Version` | `docs/` — Nova Yazılım örnek belgeleri | yorum satırı |
| `Ödev` | `homeworkdocs/` — kendi README'lerim | **aktif** |

Örnek belgelerle denemek istersen ikisini takas et.

## 💻 3. Kullanım

### En basit hâli — çalışan iskelet

RAG'in tamamı aşağıdaki kadar. Tek belge, tek soru, **tek parça** getiriyor — Web API bile değil, düz console. Ama modülün fikri burada:

```csharp
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;

IEmbeddingGenerator<string, Embedding<float>> embedder =
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3");

IChatClient chat = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" }
    .AsIChatClient("claude-haiku-4-5");

// 1. Belgeyi ## başlıklarından parçala
var parts = (await File.ReadAllTextAsync("docs/ik-politikasi.md")).Split("\n## ");
var chunks = parts.Skip(1).ToList();

// 2. Parçaları BİR KEZ embed'le
var chunkEmbeddings = await embedder.GenerateAsync(chunks);

// 3. Soruyu embed'le
var question = "Yıllık izin kaç gün?";
var questionEmbedding = (await embedder.GenerateAsync([question]))[0];

// 4. En yakın parçayı bul
var bestIndex = 0;
var bestScore = -1f;

for (int i = 0; i < chunks.Count; i++)
{
    var score = CosineSimilarity(questionEmbedding.Vector.Span, chunkEmbeddings[i].Vector.Span);

    if (score > bestScore)
    {
        bestScore = score;
        bestIndex = i;
    }
}

// 5. Bulunan parçayı Claude'a ver
var messages = new List<ChatMessage>
{
    new(ChatRole.System, $"Sadece şu belgeye dayanarak cevapla. Belgede yoksa \"bilgi yok\" de.\n\n{chunks[bestIndex]}"),
    new(ChatRole.User, question)
};

var response = await chat.GetResponseAsync(messages, new ChatOptions { Temperature = 0.0f });

Console.WriteLine(response.Text);
Console.WriteLine($"kaynak skoru: {bestScore:F4}");

// Modül 5'ten aynen geliyor — dosyanın sonunda durmalı
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

Çıktısı:

```
Belgede belirtilen yıllık izin süresi çalışanın kıdemine göre değişmektedir:

- **Yeni çalışanlar (5 yıldan az):** 22 iş günü
- **5 yılını dolduran çalışanlar:** 27 iş günü
kaynak skoru: 0,7441
```

**Modül 5'ten tek farkı 3. blok.** Orada `sources` listesi dönüyordu ve iş bitiyordu; burada bulunan parça **system prompt'a konup** modele veriliyor. RAG'in tamamı bu.

Cevabın markdown listesi hâlinde gelmesi de öğretici: bu iskeletin prompt'unda **çıktı formatı kuralı yok.** `prompts/rag.txt`'in 4. bölümü (*"En fazla 4 cümle. Başlık, tablo ve emoji kullanma."*) tam olarak bu yüzden var.

### Bu iskeletten tam koda giden yol

Yukarıdaki kod çalışıyor ama tek belgeyle, tek parçayla ve HTTP'siz. Üstüne sırayla şunlar eklendi:

| Ne eklendi | Neden |
|---|---|
| `WebApplication` + DI | Soruyu HTTP'den almak (Modül 5'ten beri Web API) |
| `Directory.GetFiles("docs")` | Tek dosya değil, klasördeki tüm belgeler |
| `docTitle` ön eki + `Source` alanı | Kaynak takibi: `dosya.md#Başlık` |
| `OrderByDescending` + `Take(4)` | Tek parça çoğu soruya yetmiyor |
| `<chunk id="N" source="...">` | Model bilginin hangi parçadan geldiğini söyleyebilsin |
| `prompts/rag.txt` | "Bilmiyorsan söyle" kuralı + çıktı formatı |
| `sources` + `usage` alanları | Doğrulanabilirlik ve maliyet takibi |

Tamamı `Program.cs` içinde, ~120 satır. Aşağıda kritik parçalar tek tek.

### İki sağlayıcı yan yana

```csharp
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3"));

builder.Services.AddSingleton<IChatClient>(
    new AnthropicClient { ApiKey = "..." }.AsIChatClient("claude-haiku-4-5"));
```

İki farklı servis, iki farklı arayüz, tek DI container. Endpoint ikisini de parametre olarak alıyor.

### Chunking — `##` başlıklarından bölmek

```csharp
foreach (var path in Directory.GetFiles("docs", "*.md"))
{
    var fileName = Path.GetFileName(path);
    var parts = (await File.ReadAllTextAsync(path)).Split("\n## ");

    // parts[0] = ilk ##'den önceki kısım, yani "# Belge Başlığı"
    var docTitle = parts[0].Trim('#', ' ', '\n', '\r');

    foreach (var part in parts.Skip(1))   // Skip(1): H1 bölümü parça değil
    {
        var heading = part.Split('\n')[0].Trim();
        var text = $"{docTitle} > {part.Trim()}";

        chunks.Add(($"{fileName}#{heading}", text));
        chunkTexts.Add(text);
    }
}
```

Dosyayı satır satır gezip başlık takip etmeye gerek yok — `Split("\n## ")` tam istenen yerlerden kesiyor:

```
# Nova Yazılım — İK Politikası     ← parts[0]  (Skip(1) ile atlanır)

## İzin Politikası                 ← parts[1]
Tüm tam zamanlı çalışanların...

## Çalışma Saatleri                ← parts[2]
Mesai 09:00'da başlar...
```

**İlk yazdığım hâli satır satır geziyordu ve 40 satır tutuyordu.** `Split` + `Skip(1)` hem 16 satıra indirdi, hem de aşağıdaki "başlıksız parça" hatasını kendiliğinden çözdü.

### Başlık ön eki — ölçülmüş bir karar

Her parça `"Belge Başlığı > Bölüm Başlığı"` satırıyla başlıyor:

```
Nova Yazılım — İK Politikası > İzin Politikası
Tüm tam zamanlı çalışanların yıllık ücretli izin hakkı 22 iş günüdür.
```

Ölçüm (İK sorusu, 4 parça getiriliyor):

| | Ön ek yokken | Ön ek varken |
|---|---|---|
| Doğru belgeden gelen parça | **3/4** | **4/4** |
| En yüksek skor | 0.742 | 0.719 |
| Input token | 866 | 955 (**+%10**) |

Ön ek, İK sorusunu İK belgesine çekti; listeye sızan alakasız teknik parça düştü. Karşılığında her parça bir satır uzadı → **token faturası %10 arttı.** Skorlar da hafif düştü, çünkü "Nova Yazılım" artık *her* parçada geçiyor ve parçalar birbirine benziyor.

### Retrieval — eşik değil, top-K

```csharp
var top = scored.OrderByDescending(result => result.Score).Take(4).ToList();
```

Modül 5'te ölçmüştük: `score > 0.5` corpus'un %98'ini geçiriyordu. **Cosine skorunun mutlak değeri anlamsız, anlamlı olan sıralama.** Alakalı mı diye kodun karar vermesi mümkün değil — o kararı modele bırakıyoruz.

### Parçaları paketlemek

```csharp
var context = "";
for (int i = 0; i < top.Count; i++)
{
    context += $"<chunk id=\"{i + 1}\" source=\"{top[i].Source}\">\n{top[i].Text}\n</chunk>\n\n";
}
```

Modül 1'deki XML delimiter fikrinin ta kendisi. Numara sayesinde model *"[2] numaralı parçaya göre..."* diyebiliyor.

### RAG prompt'u — `prompts/rag.txt`

```
# 1. Rol
Sen sadece verilen belgelere dayanarak soru cevaplayan bir asistansın.

# 2. Görev
<belgeler> etiketleri arasında numaralı belge parçaları verilecek.
Kullanıcının sorusunu SADECE bu parçalara dayanarak cevapla.

# 3. Kurallar
- Cevap parçalarda yoksa "Verilen belgelerde bu bilgi yok." de. Tahmin etme, uydurma.
- Genel bilginle değil, sadece parçalarda yazanla cevap ver.
- Kullandığın her bilgi için parça numarasını [1] gibi belirt.
- Parçalar çelişiyorsa çelişkiyi söyle, birini seçme.

# 4. Çıktı formatı
En fazla 4 cümle. Başlık, tablo ve emoji kullanma.

<belgeler>
{baglam}
</belgeler>
```

3. bölümdeki **ilk kural RAG'in tamamı: model bilmediğini söyleyebilmeli.** Modül 3'te şemaya "bilinmiyor" koymayı öğrenmiştik; aynı fikir burada prompt seviyesinde.

`Temperature = 0.0f` — belgeden okuma işinde yaratıcılık istemiyoruz.

### Örnek çıktı

**Belgede olan soru:**
```
POST /ask  {"question": "Yıllık izin kaç gün?"}

{"answer":"Tüm tam zamanlı çalışanların yıllık ücretli izin hakkı 22 iş günüdür [1].
           Beş yılını dolduran çalışanlarda bu süre 27 iş gününe çıkar [1].",
 "sources":[{"source":"ik-politikasi.md#İzin Politikası","score":0.7187},
            {"source":"ik-politikasi.md#Uzaktan Çalışma","score":0.5913},
            {"source":"ik-politikasi.md#Çalışma Saatleri","score":0.5188},
            {"source":"ik-politikasi.md#Ekipman ve Masraf","score":0.4394}],
 "usage":{"input":955,"output":72}}
```

**Belgede olmayan soru** — modülün asıl sınavı:
```
POST /ask  {"question": "Şirketin kuruluş yılı nedir?"}

{"answer":"Verilen belgelerde bu bilgi yok. Belgeler Nova Yazılım'ın İK politikası ve
           teknik kurallarını içeriyor, ancak şirketin kuruluş yılından bahsetmiyor.",
 "sources":[{"source":"ik-politikasi.md#Ekipman ve Masraf","score":0.3667}, ...],
 "usage":{"input":947,"output":60}}
```

Belgeler uydurma bir şirkete ait — Claude bu bilgileri eğitim verisinden bilemez. Doğru cevap verdiyse gerçekten belgeden okumuştur.

**`sources` her zaman dönüyor.** Cevap doğru olsa da olmasa da kullanıcı kaynağa bakıp doğrulayabilsin — RAG'in güven mekanizması bu.

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** Kendi yazdığım Modül 0–5 README'lerini RAG kaynağı yapmak. Örnek kodu bozmamak için `homeworkdocs/` ve `homeworkprompts/` diye ayrı klasörler açtım, `Program.cs`'e ikinci bir `#region` ekledim.

6 README × 7 `##` başlığı = **42 parça.**

| # | Soru | Sonuç | `sources` | input token |
|---|---|---|---|---|
| 1 | Few-shot vs zero-shot ölçümüm ne çıktı? | ✅ Doğru | 4/4 Modül 1 | 8.389 |
| 2 | Embedding modelini neden değiştirdim? | ✅ Doğru | 4/4 Modül 5 | 4.740 |
| 3 | Modül 9'da ne öğrendim? | ✅ "bilgi yok" | 4 farklı modül | 7.464 |
| 4 | Embedding ile function calling farkı? | ❌ **Eksik** | 4/4 Modül 5 | 4.732 |

### 🔴 Bulgu 1 — Getirme hatası, üretim hatasından daha sinsi

Soru 4'ün cevabı:

> *"Verilen belgelerde function calling hakkında bilgi yok. Belgeler sadece embedding modülünü (Modül 5) anlatıyor."*

Modül 4 README'si **oradaydı**, embed'lenmişti. Ama 4 slotun dördünü de Modül 5 kaptı.

Model yalan söylemedi — elindekine bakıp "bende yok" dedi, prompt'un işini yaptı. **Hata üretimde değil, getirmede.**

**Sebebi:** soruyu tek bir vektöre çeviriyoruz. "Embedding ile function calling farkı" iki konu, ama elimizde tek bir 1024 boyutlu nokta var ve o nokta embedding tarafına düşüyor. Cosine similarity *"bu soru iki konu içeriyor, ikisinden de getir"* diyemez.

> **RAG'de en tehlikeli arıza tipi budur: sistem dürüst davranır, cevap yine de yanlıştır.** Kullanıcı "bilgi yok" cevabını görünce belgede gerçekten olmadığını sanır.

### 📝 Not — Soru 4'ü çözmenin yolları

Bu modülde uygulamadım, ama seçenekler şunlar:

| Yol | Ne yapar | Bedel |
|---|---|---|
| **`Take(8)`** | daha çok parça getir | ~2 kat token, alakasız içerik artar |
| **Belge başına kota** | her belgeden en iyi 1-2 parça al | orta; tek belgelik sorularda kalite düşer |
| **Soruyu parçalara ayır** *(query decomposition)* | modele "bu soruyu 2 alt soruya böl" dedirt, her biri için ayrı arama yap | +1 LLM çağrısı, gecikme |
| **Daha küçük chunk** | `##` yerine sabit uzunluk + örtüşme (*overlap*) | ucuz; parça bağlamdan kopabilir |
| **Hibrit arama** | embedding + anahtar kelime (BM25) skorlarını birleştir | ek altyapı |

En ucuzu `Take(8)`, en doğrusu query decomposition. İkincisi Modül 7'nin (Agent) konusu — model kendi arama sorgusunu üretmeye başlayınca bu sorun kendiliğinden çözülüyor.

### 🔴 Bulgu 2 — Başlıkla eşleşme, içerikle değil

Soru 3 (`"Modül 9'da ne öğrendim?"`) şunları getirdi:

```
Modul4README.md#🧪 Ödev ve öğrendiklerim   0.528
Modul5README.md#🧪 Ödev ve öğrendiklerim   0.511
Modul2README.md#🧪 Ödev ve öğrendiklerim   0.477
Modul3README.md#🧪 Ödev ve öğrendiklerim   0.463
```

"ne **öğrendim**" sorusu, dört farklı modülün "**öğrendiklerim**" başlığını çekti — içerikle değil, **başlıkla** eşleşti. Bulgu 1'de işe yarayan başlık ön eki burada aleyhime çalıştı.

Bu seferlik zararsız (cevap zaten yoktu), ama şablonla yazılmış belgelerde her dosyanın aynı başlıkları taşıması retrieval'ı bozabiliyor.

### 🔴 Bulgu 3 — Chunk boyutu = token faturası

| Kaynak | Parça sayısı | Soru başına input token |
|---|---|---|
| Nova belgeleri (küçük) | 8 | ~950 |
| README'lerim (uzun) | 42 | **4.700 – 8.400** |

457 satırlık bir README'yi 7 başlıktan bölünce parça başına ~65 satır düşüyor. 4 parça göndermek 8.000 token demek — yaklaşık **8 kat** fark.

`##`'den bölmek küçük belgede doğru, uzun belgede parçaları şişiriyor. Sabit uzunlukta bölme (*fixed-size chunking*) tam olarak bu yüzden var.

### 🔴 Bulgu 4 — Chunking'deki sessiz çöp

İlk sürümde her dosyanın ilk `##`'den önceki kısmı da bir parça oluyordu:

```
ik-politikasi.md#(başlıksız)   ← içinde sadece "# Nova Yazılım — İK Politikası"
```

İçinde bilgi yok, ama **top-4'te bir slot işgal ediyordu.** Düzelttikten sonra o slota gerçek bir parça geldi ve cevap düzeldi:

| | Önce | Sonra |
|---|---|---|
| Cevap | *"Belgeler **İK politikasını** içeriyor"* | *"Belgeler **İK politikası ve teknik kurallarını** içeriyor"* |

Model ancak gönderdiğin kadarını görür. Boş bir parça, göremediği bir belge demek.

### Kavramlar

Bu modülde belgeyi elle chunk'lamayı, kaynak takibini (`dosya.md#Başlık`), parçaları XML etiketleriyle paketlemeyi, iki farklı sağlayıcıyı tek uygulamada kullanmayı ve en önemlisi **retrieval hatasıyla üretim hatasını ayırt etmeyi** öğrendim.

## ⚠️ Dikkat edilecekler

- **🔴 Cevap yanlışsa önce `sources`'a bak.** Model uydurmuş mu, yoksa doğru parça hiç gelmemiş mi? İkisi tamamen farklı problem, çözümleri de farklı.

- **`sources`'ı her zaman döndür.** Kullanıcı cevabı kaynağa bakıp doğrulayabilmeli. RAG'in güven mekanizması bu.

- **Prompt'a "bilmiyorsan söyle" kuralını mutlaka koy.** Bu kural olmadan model boşluğu genel bilgisiyle doldurur ve nereden geldiği anlaşılmaz.

- **Top-K kullan, eşik değil.** (Modül 5, Ölçüm 3) Alakalı mı kararını koda değil modele bırak.

- **Tek vektör iki konuyu temsil edemez.** Çok konulu sorular tek aramayla çözülmez.

- **Chunk'ı ne çok büyük ne çok küçük tut.** Büyük parça = token faturası, küçük parça = kopuk bağlam.

- **Kalite artıran her şey token'a mal olur.** Başlık ön eki, daha fazla parça, daha uzun prompt — hepsi fatura. Ölçüp karar ver.

- **`CopyToOutputDirectory`'yi unutma.** `docs/` ve `prompts/` `bin/`'e kopyalanmazsa program açılışta `DirectoryNotFoundException` ile çöker.

- **Test belgelerini uydurma tut.** Model gerçek bir konuyu zaten biliyorsa, belgeden mi okudu yoksa hatırladı mı ayırt edemezsin.

---

Hazırlayan: Mert Ağralı 👨‍💻
