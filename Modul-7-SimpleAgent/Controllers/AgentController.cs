using System.ComponentModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;

namespace Modul_7_SimpleAgent.Controllers;

[ApiController]
public class AgentController : ControllerBase
{
    // Agent'in cikamayacagi kok klasor. Tek yerde duruyor: hem listeleme hem
    // okuma hem de guvenlik kontrolu ayni degeri kullanmak zorunda.
    private const string DocsRoot = "docs";

    private readonly DocumentStore _store;
    private readonly IChatClient _chat;

    // Constructor injection: Minimal API'de endpoint parametresiydi, burada ctor.
    public AgentController(DocumentStore store, IChatClient chat)
    {
        _store = store;
        _chat = chat;
    }

    #region Version - 1 

    // ============================================================
    //  ORNEK KOD: embedding ile arayan agent
    // ============================================================
    [HttpPost("/ask")]
    public async Task<IActionResult> Ask(AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest("question alanı boş olamaz.");
        }

        // Araclar bu listeyi YAKALAR (closure). Agent'in her adimi buraya dusuyor.
        var steps = new List<object>();

        // ---------- Arac 1: ara, SADECE onizleme dondur ----------
        [Description("Belgelerde arama yapar. Başlık ve kısa önizleme döndürür; tam metin için read_section kullan.")]
        async Task<string> SearchDocs([Description("Aranacak konu, örn: function calling nedir")] string query)
        {
            var results = await _store.SearchAsync(query, 3);

            steps.Add(new { step = steps.Count + 1, tool = "search_docs", args = query, found = results.Count });
            Console.WriteLine($"   >> search_docs(\"{query}\") -> {results.Count} sonuç");

            var lines = new List<string>();

            foreach (var result in results)
            {
                lines.Add($"{result.Source} | {result.Preview}");
            }

            return string.Join("\n", lines);
        }

        // ---------- Arac 2: tam metni getir ----------
        [Description("Bir bölümün tam metnini getirir. source değerini search_docs sonucundan al.")]
        string ReadSection([Description("Bölüm kimliği, search_docs sonucundaki hâliyle, örn: Modul4README.md#1")] string source)
        {
            steps.Add(new { step = steps.Count + 1, tool = "read_section", args = source });
            Console.WriteLine($"   >> read_section(\"{source}\")");

            return _store.Read(source) ?? $"'{source}' bulunamadı. search_docs ile doğru kimliği bul.";
        }

        var options = new ChatOptions
        {
            // Ad acikca verilmezse yerel fonksiyonun derleyici adi gider (Modul 4'un dersi).
            Tools =
            [
                AIFunctionFactory.Create(SearchDocs,  "search_docs"),
                AIFunctionFactory.Create(ReadSection, "read_section")
            ],
            Temperature = 0,
            MaxOutputTokens = 1000
        };

        // ControllerBase'in kendi File(...) metodu var; tam adi yazmak zorundayiz.
        var systemPrompt = await System.IO.File.ReadAllTextAsync(Path.Combine("prompts", "agent.txt"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, request.Question)
        };

        Console.WriteLine();
        Console.WriteLine($"=== Soru: {request.Question}");

        // Tek cagri, ama icinde N tur donuyor: UseFunctionInvocation araclari calistirip
        // sonuclari modele geri veriyor. Biz sadece bitmis cevabi aliyoruz.
        var response = await _chat.GetResponseAsync(messages, options);

        Console.WriteLine($"=== {steps.Count} adımda bitti");

        // response.Text TUM turlarin asistan metnini birlestirir -> ara gerekceler de girer.
        // Kullaniciya giden cevap sadece SON mesaj.
        var answer = response.Text;

        if (response.Messages.Count > 0)
        {
            answer = response.Messages[^1].Text;
        }

        return Ok(new
        {
            answer,
            steps,
            messageCount = response.Messages.Count,
            usage = new { input = response.Usage?.InputTokenCount, output = response.Usage?.OutputTokenCount }
        });
    }

    #endregion
    
    #region Ödev 
    // ============================================================
    //  ODEV: dosya sistemi uzerinde gezen agent
    // ============================================================
    [HttpPost("/explore")]
    public async Task<IActionResult> Explore(AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
        {
            return BadRequest("question alanı boş olamaz.");
        }

        var steps = new List<object>();

        // ---------- Arac 1: dosyalari listele ----------
        [Description("Klasördeki belgelerin adlarını ve boyutlarını listeler. Hangi belgelerin var olduğunu bilmiyorsan ilk bunu çağır. Bir belgenin içeriğini okumak için read_file kullan.")]
        string ListFiles()
        {
            var lines = new List<string>();

            foreach (var result in Directory.GetFiles(DocsRoot, "*.md"))
            {
                var info = new FileInfo(result);
                lines.Add($"{info.Name} | {info.Length / 1024} KB");
            }

            steps.Add(new { step = steps.Count + 1, tool = "list_files", found = lines.Count });
            Console.WriteLine($"   >> list_files -> {lines.Count} sonuç");

            return string.Join("\n", lines);
        }

        // ---------- Arac 2: dosya icerigini getir ----------
        [Description("Bir belgenin içeriğini döndürür. fileName değerini list_files sonucundan al.")]
        string ReadFile([Description("Dosya adı, list_files sonucundaki hâliyle, örn: Modul4README.md")] string fileName)
        {
            steps.Add(new { step = steps.Count + 1, tool = "read_file", args = fileName });
            Console.WriteLine($"   >> read_file(\"{fileName}\")");

            var path = ResolveInsideRoot(fileName);

            if (path is null)
            {
                Console.WriteLine($"   !! ENGELLENDI: '{fileName}' kök klasörün dışına çıkıyor");
                return $"'{fileName}' erişim dışı. Sadece belge klasöründeki dosyalar okunabilir.";
            }

            if (!System.IO.File.Exists(path))
            {
                return $"'{fileName}' bulunamadı. list_files ile mevcut dosyaları gör.";
            }

            var text = System.IO.File.ReadAllText(path);

            if (text.Length > 3000)
            {
                // Kirptigimizi MODELE soyluyoruz, yoksa eksik metni tam sanip yanlis cevap verir.
                return text[..3000]
                       + $"\n\n[... kısaltıldı: toplam {text.Length} karakterin ilk 3000'i ...]";
            }

            return text;
        }

        // ---------- Arac 3: kelimenin gectigi yerleri bul ----------
        [Description("Tüm belgelerde bir kelimeyi arar; geçtiği dosyayı, satır numarasını ve satırın metnini döndürür. Belirli bir kelimenin nerede geçtiğini bulmak için bunu kullan, konuyu anlamak için read_file ile devam et.")]
        string SearchInFiles([Description("Aranacak kelime veya kısa ifade, örn: Ollama")] string keyword)
        {
            steps.Add(new { step = steps.Count + 1, tool = "search_in_files", args = keyword });
            Console.WriteLine($"   >> search_in_files(\"{keyword}\")");

            if (string.IsNullOrWhiteSpace(keyword))
            {
                return "Arama kelimesi boş olamaz.";
            }

            var hits = new List<string>();

            // Bu arac disaridan YOL almiyor, kokun icinde kendisi geziyor.
            // En guvenli arac, yanlis yol verilemeyen aractir.
            foreach (var path in Directory.GetFiles(DocsRoot, "*.md"))
            {
                var fileName = Path.GetFileName(path);
                var lines = System.IO.File.ReadAllLines(path);

                for (int i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    hits.Add($"{fileName}:{i + 1} | {lines[i].Trim()}");

                    if (hits.Count >= 20)
                    {
                        hits.Add("[... 20 eşleşmede kesildi, aramayı daraltabilirsin ...]");
                        return string.Join("\n", hits);
                    }
                }
            }

            if (hits.Count == 0)
            {
                return $"'{keyword}' hiçbir belgede geçmiyor.";
            }

            return string.Join("\n", hits);
        }

        var options = new ChatOptions
        {
            Tools =
            [
                AIFunctionFactory.Create(ListFiles,     "list_files"),
                AIFunctionFactory.Create(ReadFile,      "read_file"),
                AIFunctionFactory.Create(SearchInFiles, "search_in_files")
            ],
            Temperature = 0,
            MaxOutputTokens = 1000
        };

        var systemPrompt = await System.IO.File.ReadAllTextAsync(Path.Combine("homeworkPrompts", "agent.txt"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, request.Question)
        };

        Console.WriteLine();
        Console.WriteLine($"=== Soru: {request.Question}");

        var response = await _chat.GetResponseAsync(messages, options);

        Console.WriteLine($"=== {steps.Count} adımda bitti");

        var answer = response.Text;

        if (response.Messages.Count > 0)
        {
            answer = response.Messages[^1].Text;
        }

        return Ok(new
        {
            answer,
            steps,
            messageCount = response.Messages.Count,
            usage = new { input = response.Usage?.InputTokenCount, output = response.Usage?.OutputTokenCount }
        });
    }
    #endregion

    // Modelin verdigi dosya adini kok klasorun ICINDE cozer.
    // Disari cikiyorsa null doner -> arac dosyayi hic acmaz.
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
}

// POST govdesindeki JSON bu tipe baglanir: {"question": "..."}
public record AskRequest(string Question);
