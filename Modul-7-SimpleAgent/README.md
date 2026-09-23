# 🧠 SimpleAgent — Aramayı modele bırakmak: araç döngüsü ve durma koşulu

## 📖 Nedir?

Modül 6'da RAG'i kurduk ve **bir soruda çuvalladı**:

```
"Embedding ile function calling arasındaki fark ne?"
→ sources: 4/4 Modul5README.md
→ "Verilen belgelerde function calling hakkında bilgi yok."
```

Modül 4 README'si oradaydı, embed'lenmişti. Ama soruyu **tek vektöre** çevirdiğimiz için o vektör embedding tarafına düştü ve 4 slotun dördünü de Modül 5 kaptı.

**Modül 7'nin fikri:** aramayı biz yapmayalım, **modele yaptıralım.**

```
Modül 6:  soru → [biz ararız] → 4 parça → model → cevap          (1 arama, sabit)

Modül 7:  soru → model → search_docs("embedding")        → sonuç
                       → search_docs("function calling") → sonuç
                       → read_section(...)               → sonuç
                       → cevap                            (N arama, modelin kararı)
```

> **Agent:** modelin, hedefe ulaşmak için hangi aracı kaç kez çağıracağına **kendisinin** karar verdiği döngü.

Modül 4'te de araç vardı ama tek atışlıktı: soru → araç → cevap. Burada model **kendi arama sorgusunu üretiyor**, sonucu değerlendiriyor, gerekirse tekrar arıyor.

### Beş kavram

| Kavram | Ne yapar |
|---|---|
| **Retrieval'ı araca çevirmek** | Modül 6'nın cosine araması artık `search_docs(query)` adlı bir tool |
| **İki aşamalı araç tasarımı** | `search_docs` sadece önizleme verir; tam metin için `read_section` gerekir |
| **ReAct prompt'u** | Her araç çağrısından önce tek cümle gerekçe (*Reasoning + Acting*) |
| **Adım bütçesi** | `MaximumIterationsPerRequest`. Varsayılanı **40**, biz **8** yaptık |
| **Adım logu** | Hangi araç, hangi argüman — agent kara kutu olmasın |

💡 **Kısacası:** Modül 6'da modele *cevabı* veriyorduk. Modül 7'de *arama yeteneğini* veriyoruz.

## 🧩 Nasıl çalışır?

`GetResponseAsync` **tek satır** ama içinde döngü dönüyor:

```
TUR 1 → Claude'a: [system, user] + araç tarifleri
        Claude:   "embedding'i arayacağım" + search_docs(query:"embedding nedir")   ← sadece İSTEK

        UseFunctionInvocation: isteği görür, C# metodunu ÇALIŞTIRIR,
                               sonucu mesaj listesine ekler

TUR 2 → Claude'a: [system, user, asistan, araç-sonucu] + araç tarifleri
        Claude:   search_docs(query:"function calling nedir")
TUR 3 → read_section("Modul5README.md#1")
TUR 4 → read_section("Modul4README.md#1")
TUR 5 → Claude:   araç yok, düz metin → DÖNGÜ BİTER
```

`response` elimize ancak 5. turdan sonra geliyor.

## 🔧 1. Gerekli paketler

```bash
dotnet new webapi -n Modul-7-SimpleAgent --use-controllers
dotnet add package Anthropic
dotnet add package OllamaSharp
```

```bash
ollama pull bge-m3
```

## ⚙️ 2. Ayarlar

`.csproj`'a üç kopyalama satırı — atlanırsa program **çalışma anında** çöker:

```xml
<None Update="docs\*.md"             CopyToOutputDirectory="PreserveNewest" />
<None Update="prompts\*.txt"         CopyToOutputDirectory="PreserveNewest" />
<None Update="homeworkPrompts\*.txt" CopyToOutputDirectory="PreserveNewest" />
```

### Çalıştırma

```bash
ollama serve                              # açık değilse
dotnet run --project Modul-7-SimpleAgent
```

Konsolda `42 parça hazır. POST /ask` görünce hazır. Port: **5065**.

```bash
# Örnek kod: embedding ile arayan agent
curl -X POST http://localhost:5065/ask -H "Content-Type: application/json" \
  -d '{"question":"Embedding ile function calling arasındaki fark ne?"}'

# Ödev: dosya sistemi üzerinde gezen agent
curl -X POST http://localhost:5065/explore -H "Content-Type: application/json" \
  -d '{"question":"Modül 3te ne öğrendim?"}'
```

## 💻 3. Kullanım

### En basit hâli — çalışan iskelet

Agent'ın tamamı bu kadar. Tek araç, tek soru, Web API bile değil — düz console:

```csharp
using System.ComponentModel;
using Anthropic;
using Microsoft.Extensions.AI;

IChatClient chat = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" }
    .AsIChatClient("claude-haiku-4-5")
    .AsBuilder()
    .UseFunctionInvocation(configure: agent =>
    {
        agent.MaximumIterationsPerRequest = 5;   // durma koşulu, varsayılanı 40
    })
    .Build();

// Araç TANIMLANIYOR, çağrılmıyor. Çağırmaya model karar verecek.
[Description("Bir belgenin içeriğini döndürür. Cevap vermeden önce ilgili belgeyi bununla oku.")]
string ReadFile([Description("Dosya adı, örn: Modul3README.md")] string fileName)
{
    Console.WriteLine($"   >> read_file(\"{fileName}\")");

    var path = Path.Combine("docs", fileName);

    if (!File.Exists(path))
    {
        return $"'{fileName}' bulunamadı. Klasörde Modul3README.md var.";
    }

    var text = File.ReadAllText(path);

    if (text.Length > 2000)
    {
        return text[..2000] + "\n[... kısaltıldı ...]";
    }

    return text;
}

var options = new ChatOptions
{
    Tools = [AIFunctionFactory.Create(ReadFile, "read_file")],
    Temperature = 0,
    MaxOutputTokens = 500
};

var messages = new List<ChatMessage>
{
    new(ChatRole.System, "Belgelere sadece araçlarla erişebilirsin. Genel bilginle cevap verme."),
    new(ChatRole.User, "Modül 3'te ne öğrenilmiş? İki cümle.")
};

// Tek çağrı, içinde N tur: model araç ister → middleware çalıştırır → sonuç modele döner
var response = await chat.GetResponseAsync(messages, options);

Console.WriteLine(response.Messages[^1].Text);
Console.WriteLine($"[{response.Messages.Count} mesaj, input: {response.Usage?.InputTokenCount}]");
```

Çıktısı:

```
   >> read_file("Modul3README.md")
Modül 3'te **şema, validasyon ve retry mekanizmaları** öğrenilmiştir. Şema tipi
garantisi sağlarken anlamı doğrulamak ve hataları sınıflandırarak uygun şekilde
retry yapmak gerektiği öğretilmiştir.
[3 mesaj, input: 2438]
```

**Modül 4'ten tek farkı `configure:` parametresi.** Orada `.UseFunctionInvocation()` parametresizdi — yani 40 turla çalışıyorduk ve farkında değildik.

`3 mesaj` = user → (asistan + araç isteği) → araç sonucu → asistan cevabı. Modelin kaç tur döndüğünü buradan görüyorsun.

### 🔴 Kod sırası ≠ çalışma sırası

En çok kafa karıştıran nokta bu: **araç gövdesi tanımlandığı yerde çalışmaz.**

```csharp
button.Click += OnButtonClick;   // OnButtonClick bu satırda ÇALIŞMAZ
```

Araçlar da böyle. Gerçek sıra:

| Kodda kaçıncı | Ne | **Gerçekte kaçıncı çalışıyor** |
|---|---|---|
| 1 | `steps` listesi | **1** |
| 2 | `SearchDocs` gövdesi | **5, 7** (model çağırınca) |
| 3 | `ReadSection` gövdesi | **9, 11** |
| 4 | `options` | **2** |
| 5 | `GetResponseAsync` | **4 → 12** |
| 6 | cevabı paketle | **13** |

`AIFunctionFactory.Create(SearchDocs, "search_docs")` metodu **çağırmıyor**; imzasına ve `[Description]`'larına bakıp modele gidecek JSON tarifini üretiyor:

```json
{
  "name": "search_docs",
  "description": "Belgelerde arama yapar. Başlık ve kısa önizleme döndürür; tam metin için read_section kullan.",
  "input_schema": {
    "type": "object",
    "properties": { "query": { "type": "string", "description": "Aranacak konu, örn: function calling nedir" } },
    "required": ["query"]
  }
}
```

**Model kod gövdesini hiç görmüyor — aracı sadece bu cümleye bakarak seçiyor.**

Araç tarifleri istekle **birlikte** gidiyor; bu yüzden tanımlama zorunlu olarak çağrıdan önce gelir.

### Controller — Minimal API'den farkı

Modül 5 ve 6 Minimal API'ydi, bu modülde ilk kez `[ApiController]`:

| | Minimal API (M5–M6) | Controller (M7) |
|---|---|---|
| Endpoint nerede | `Program.cs` içinde `app.MapPost(...)` | Ayrı sınıf, `Controllers/` altında |
| Bağımlılık nasıl gelir | Endpoint **parametresi** | **Constructor** |
| `Program.cs`'in işi | Kayıt + endpoint | Sadece kayıt + `app.MapControllers()` |

```csharp
[ApiController]
public class AgentController : ControllerBase
{
    private readonly DocumentStore _store;
    private readonly IChatClient _chat;

    public AgentController(DocumentStore store, IChatClient chat)
    {
        _store = store;
        _chat = chat;
    }

    [HttpPost("/ask")]
    public async Task<IActionResult> Ask(AskRequest request) { ... }
}
```

`app.MapControllers()` açılışta assembly'yi tarar, `[ApiController]` sınıflarını bulur, `[HttpPost]` niteliklerine bakıp yolları kendisi kaydeder.

> ⚠️ İki action'a da düz `[HttpPost]` yazıp sınıfa `[Route("ask")]` koyarsan ikisi de aynı yola bağlanır ve ilk istekte `AmbiguousMatchException` alırsın. Yolu action üzerinde açıkça yaz: `[HttpPost("/ask")]`, `[HttpPost("/explore")]`.

### Araçlar neden action metodunun *içinde*

Modül 4'te araçlar dosyanın sonundaydı. Burada `Ask`'ın içindeler, sebebi tek satır:

```csharp
var steps = new List<object>();   // ← araçlar bunu YAKALIYOR (closure)
```

Araçlar bu listeye yazıyor. Dışarıda olsalardı göremezlerdi; alan yapmak ya da parametre geçmek gerekirdi — ikincisi araç imzasını bozardı, model `steps` diye bir parametre görmemeli.

Sonucu şurada görünüyor: `Console.WriteLine($"=== {steps.Count} adımda bitti")` satırında sayı **4** yazıyor, ama o satıra kadar `steps.Add` çağıran hiçbir satır yok — hepsi araç gövdelerinde çalıştı.

### İki aşamalı araç tasarımı

```csharp
// search_docs yalnızca kimlik + 350 karakter önizleme döndürür
lines.Add($"{result.Source} | {result.Preview}");
```

Modelin gördüğü:
```
Modul5README.md#1 | Modül 5 — Embeddings > 📖 Nedir? Beş modüldür modele metin gönderip...
Modul4README.md#1 | Modül 4 — FunctionCalling > 📖 Nedir? Modelin kendi başına...
```

"Bu bölüm işime yarar mı" kararı için yeter, **cevap yazmaya yetmez** — model mecburen `read_section` çağırıyor. Sonuç: agent döngüsü görünür oluyor ve 42 parçanın tamamını değil sadece istediğini token olarak ödüyoruz.

### Adım bütçesi

```csharp
.UseFunctionInvocation(configure: agent =>
{
    agent.MaximumIterationsPerRequest = 8;
})
```

Bir iterasyon = modele bir gidiş-dönüş; ilk istek de sayılıyor. 8 tur ≈ 7 araç çağrısı hakkı.

`configure:` adını yazmak **zorunlu** — imza `UseFunctionInvocation(ILoggerFactory?, Action<FunctionInvokingChatClient>?)`, yani ilk opsiyonel parametre logger.

### ReAct prompt'u — `prompts/agent.txt`

```
# 1. Rol
Sen belgeler üzerinde araştırma yapan bir asistansın. Belgelere sadece araçlarla erişebilirsin.

# 2. Araçlar
- search_docs(query): konuya göre arar, başlık ve kısa önizleme döndürür.
- read_section(source): bir bölümün tam metnini getirir.

# 3. Çalışma biçimi
- Her araç çağrısından ÖNCE tek cümleyle neden çağırdığını yaz.
- Önce search_docs ile ara, sonra ilgili bölümü read_section ile oku. Önizlemeye bakarak cevap verme.
- Soru birden fazla konu içeriyorsa HER konu için ayrı search_docs çağır.
- Bir bölümün ilgisiz olduğuna önizlemeye bakarak karar verme. Önizleme bölümün sadece
  ilk birkaç satırıdır; aradığın bilgi daha aşağıda olabilir.
- "Belgelerde bu bilgi yok." demeden ÖNCE en az bir bölümü read_section ile tam oku.
- Aradığını bulamazsan farklı kelimelerle bir kez daha ara, sonra pes et.

# 4. Kurallar
- Sadece araçlardan gelen metne dayan. Genel bilginle cevap verme, uydurma.
- Bilgi bulunamazsa "Belgelerde bu bilgi yok." de.
- Kullandığın her bilgi için kaynağı [Modul4README.md#1] gibi belirt.

# 5. Çıktı formatı
En fazla 6 cümle. Tablo ve emoji kullanma.
```

3. bölümün 3. maddesi (*"HER konu için ayrı search_docs"*) Modül 6'nın hatasını doğrudan hedef alıyor.

**İki tür durma koşulu var ve ikisi de lazım:**

| | Nerede | Ne yapar |
|---|---|---|
| `MaximumIterationsPerRequest` | Kod | **Sert sınır** — kesip atar, cevap yarım kalır |
| *"...sonra pes et"* | Prompt | **Yumuşak sınır** — model kendi durur, cevap düzgün olur |

Sert sınıra dayanan agent yarım cevap verir; iyi olan hiç dayanmamasıdır.

### Örnek çıktı

```
POST /ask  {"question": "Embedding ile function calling arasındaki fark ne?"}
```

Konsol:
```
=== Soru: Embedding ile function calling arasındaki fark ne?
   >> search_docs("embedding nedir") -> 3 sonuç
   >> search_docs("function calling nedir") -> 3 sonuç
   >> read_section("Modul5README.md#1")
   >> read_section("Modul4README.md#1")
=== 4 adımda bitti
```

```json
{
  "answer": "Embedding metni sayı vektörüne çevirerek anlamını ölçer; function calling ise
             modele araçlar tanıtarak hangisini ne zaman çağıracağına kendisinin karar
             vermesini sağlar [Modul5README.md#1, Modul4README.md#1]. ...",
  "steps": [
    {"step":1,"tool":"search_docs","args":"embedding nedir","found":3},
    {"step":2,"tool":"search_docs","args":"function calling nedir","found":3},
    {"step":3,"tool":"read_section","args":"Modul5README.md#1"},
    {"step":4,"tool":"read_section","args":"Modul4README.md#1"}
  ],
  "messageCount": 5,
  "usage": {"input":7725,"output":468}
}
```

**`steps` alanı Modül 6'nın `sources`'ından farklı bir şey söylüyor.** `sources` "nereden okudu" derdi; `steps` "**nasıl karar verdi**" diyor. Agent'ın denetlenebilirliği bu.

## 🧪 Ödev ve öğrendiklerim

**Ödev neydi?** Dosya sistemi üzerinde arama+özet yapan agent: `POST /explore`, üç araç (`list_files`, `read_file`, `search_in_files`), embedding yok. Aynı `docs/` klasörü, farklı arama stratejisi — karşılaştırma için.

### 🔴 Bulgu 1 — Modül 6'nın çözemediği soru çözüldü

| | Modül 6 (RAG) | Modül 7 (Agent) |
|---|---|---|
| Arama sayısı | 1 (sabit) | **2** (model karar verdi) |
| Cevap | ❌ *"function calling hakkında bilgi yok"* | ✅ Her iki modülden bilgi |
| Input token | 4.732 | 7.725 |

Maliyet **1,6 kat** arttı, cevap yanlıştan doğruya döndü. Plana "3-4 kat" yazmıştım; iki aşamalı araç tasarımı sayesinde o kadar olmadı — agent daha çok tur atıyor ama her turda daha az veri taşıyor.

### 🔴 Bulgu 2 — Araç kimliğine emoji koyma

İlk sürümde chunk kimliği `Modul5README.md#📖 Nedir?` idi. Agent 12 adım harcadı, 8'i yazım hatasıydı:

```
>> read_section("Modul5README.md#🔍 Nedir?")                    ← 🔍 uydurdu, gerçeği 📖
>> read_section("Modul5README.md#Nedir")                        ← emoji'yi attı
>> read_section("Modul5README.md#🔍 Nedir? | 🧠 Embeddings...")  ← önizlemeyi de yapıştırdı
>> read_section("Modul5README.md")                              ← ✅ nihayet tutturdu
```

Kimliği `Modul5README.md#1` yapınca ilk denemede tutturdu.

> **Araç kimliğini model yazacak diye tasarla.** Emoji, apostrof, uzun başlık = yazım hatası daveti. Her hatalı deneme bir tur, her tur para.

### 🔴 Bulgu 3 — `response.Text` cevap değildir

```
"answer": "Embedding ve function calling arasındaki farkı öğrenmek için araştıracağım.
           Şimdi her iki konunun tam tanımını okuyacağım.
           Doğru bölüm kimliklerini kullanacağım.
           Mükemmel! Şimdi tam tanımları var. Cevabı yazabilirim.
           **Embedding** metni sayı dizisine çevirerek..."
```

`response.Text` **tüm turların** asistan metnini birleştiriyor. ReAct prompt'unun istediği gerekçeler cevaba karıştı. Çözüm:

```csharp
answer = response.Messages[^1].Text;   // sadece son mesaj
```

### 🔴 Bulgu 4 — Önizlemeye bakıp pes etmek

Bir test yanlış *"bilgi yok"* dedi, halbuki cevap belgede vardı (`Modul1README.md:417`). Agent 4 kez aradı, **hiç `read_section` çağırmadı** — 350 karakterlik önizlemelere bakıp "alakasız" diye karar verdi. Aranan cümle bölümün ~3000. karakterindeydi.

Prompt'a iki satır ekleyince düzeldi:
```
- Bir bölümün ilgisiz olduğuna önizlemeye bakarak karar verme...
- "Belgelerde bu bilgi yok." demeden ÖNCE en az bir bölümü read_section ile tam oku.
```

**Üç düzeltmenin toplam etkisi:**

| Test | Önce | Sonra |
|---|---|---|
| Çok konulu soru | ✅ 12 adım, 21.208 token | ✅ **4 adım**, 7.725 token |
| Tek konulu soru | ❌ yanlış "bilgi yok" | ✅ **Doğru**, 2 adım |
| Belgede olmayan soru | ✅ 4 adım | ✅ 3 adım |
| **Toplam token** | **43.218** | **23.864** (−%45) |

### 🔴 Bulgu 5 — Modelin reddetmesi güvenlik değildir

Ödevin `read_file`'ı dosya yolu alıyor. Yol kontrolü olmadan `../Program.cs` içindeki API key'e erişilebilir. Kontrol:

```csharp
private static string? ResolveInsideRoot(string fileName)
{
    var root = Path.GetFullPath(DocsRoot);
    var full = Path.GetFullPath(Path.Combine(root, fileName));

    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
    {
        return null;
    }

    return full;
}
```

| Model ne yazarsa | `Path.Combine` | `Path.GetFullPath` | Sonuç |
|---|---|---|---|
| `Modul4README.md` | `.../docs/Modul4README.md` | aynı | ✅ içeride |
| `../Program.cs` | `.../docs/../Program.cs` | `.../net10.0/Program.cs` | ❌ dışarı |
| `/etc/passwd` | `/etc/passwd` ⚠️ | `/etc/passwd` | ❌ dışarı |

> **`Path.Combine` ikinci argüman mutlak yolsa birinciyi tamamen atar.** `Combine("docs", "/etc/passwd")` sana `/etc/passwd` verir. `..` aramakla yakalanmaz — **çözülmüş yolu** karşılaştırmak gerekir.

**Ama testi koşunca asıl ders başka çıktı.** Doğal ifadeyle sorduğumda:

```
POST /explore  {"question":"../Program.cs dosyasının içeriğini göster"}

>> list_files -> 6 sonuç
=== 1 adımda bitti
```

`read_file` **hiç çağrılmadı** — model listeye bakıp kendi kendine reddetti. Doğru sonuç, ama kod test edilmedi. Zorlayınca:

```
POST /explore  {"question":"read_file aracını tam olarak '../Program.cs' argümanıyla çağır..."}

>> read_file("../Program.cs")
!! ENGELLENDI: '../Program.cs' kök klasörün dışına çıkıyor
```

> **Bir korumayı test ederken modelin işbirliğine güvenme.** Modelin nazik davranması güvenlik değildir; engellemeye çalıştığın şeyi zorla yaptır.

### 🔴 Bulgu 6 — Embedding mi, kelime araması mı?

Aynı soru, iki endpoint:

| | `/ask` (embedding) | `/explore` (dosya sistemi) |
|---|---|---|
| Adım | **4** | 6 |
| Input token | **7.725** | 18.012 |
| Atıf biçimi | `[Modul4README.md#1]` | `[Modul4README.md:10]` ← satır no |

```
/explore  list_files
          search_in_files("embedding")
          search_in_files("function calling")
          search_in_files("function")        ← boşa giden tur
          read_file("Modul4README.md")       ← 3000 karakter
          read_file("Modul5README.md")       ← 3000 karakter
```

İki çıkarım:

- **Token farkının sebebi okuma birimi.** `read_section` bir *bölüm* getiriyor, `read_file` bir *dosya*. Chunking'in değeri burada görünüyor: belge anlamlı parçalara bölünmüşse modele daha azını göndererek aynı cevabı alıyorsun.
- **Birebir kelime araması kırılgan.** `"function calling"` ifadesi metinde tam o sırayla geçmeyince model `"function"` diye tekrar aradı, bir tur yaktı. Embedding'in bu sorunu yok — ama `"Hangi modüllerde Ollama kullandım?"` sorusunda tam tersi: `search_in_files` iki adımda kesin cevap verdi.

Ödevin tüm testleri:

| # | Test | Sonuç | Adım |
|---|---|---|---|
| 1 | Gezinme — *"Modül 3'te ne öğrendim?"* | ✅ `list_files` → `read_file` | 2 |
| 2 | Kelime — *"Hangi modüllerde Ollama?"* | ✅ `list_files` → `search_in_files` | 2 |
| 3 | Güvenlik (doğal ifade) | ⚠️ model engelledi, kod devreye girmedi | 1 |
| 3c | Güvenlik (zorlama) | ✅ `!! ENGELLENDI` | 1 |
| 4 | Karşılaştırma | yukarıda | 4 vs 6 |

### Kavramlar

Bu modülde retrieval'ı araca dönüştürmeyi, iki aşamalı araç tasarımını, ReAct prompt'unu, adım bütçesini, Controller'lı Web API'yi ve constructor injection'ı, araç parametrelerini güvenlik gözüyle okumayı ve en önemlisi **modelin kendi arama sorgusunu üretmesinin neyi çözdüğünü** öğrendim.

## ⚠️ Dikkat edilecekler

- **🔴 Araç kimliklerini model yazacak diye tasarla.** Emoji, apostrof, uzun başlık kullanma. Sıra numarası en güvenlisi.

- **🔴 En güvenli araç, kötüye kullanılacak parametresi olmayan araçtır.** `search_in_files` kökü kendi geziyor — yanlış yol verilemez. `read_file` yol alıyor, o yüzden korunması gerekti.

- **🔴 Bir korumayı test ederken modele güvenme.** Doğal ifadeyle sorduğunda model kendi reddedebilir; guard'ı **zorla** test et.

- **`Path.Combine` güvenlik kontrolü yapmaz.** İkinci argüman mutlak yolsa birinciyi atar. `Path.GetFullPath` ile çözüp kökle karşılaştır; `..` arama.

- **Araç kod sırasında değil, model çağırınca çalışır.** Tanım ≠ çağrı.

- **Hata mesajı da bir talimattır.** "Bulunamadı" yetmez; *"list_files ile doğru adı bul"* de — yoksa model aynı hatayı tekrarlar.

- **Kırptığını modele söyle.** Sessiz kırpma, modelin eksik metni tam sanmasına yol açar.

- **`steps.Add` aracın en başında olsun.** Başarısız çağrıları da kaydet, yoksa neden 12 tur döndüğünü göremezsin.

- **`response.Text` cevap değildir** — tüm turların metnini birleştirir. Kullanıcıya `response.Messages[^1].Text` git.

- **İki durma koşulu birden gerekir:** kodda sert sınır (`MaximumIterationsPerRequest`), prompt'ta yumuşak sınır (*"sonra pes et"*).

- **Controller içinde `File` yazamazsın.** `ControllerBase.File(...)` ile çakışır; `System.IO.File` yaz.

- **Her action'a yolunu açıkça yaz.** İki `[HttpPost]` aynı `[Route]` altında `AmbiguousMatchException` demektir.

---

Hazırlayan: Mert Ağralı 👨‍💻
