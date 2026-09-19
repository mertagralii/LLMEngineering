using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

// Iki saglayici: arama yerelde (bedava), cevap Claude'da (ucretli).
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3"));

builder.Services.AddSingleton<IChatClient>(
    new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" }
        .AsIChatClient("claude-haiku-4-5"));

var app = builder.Build();

#region Version
//
// // ---------- 1. Chunking: her ## basligi bir parca ----------
// var chunks = new List<(string Source, string Text)>();
// var chunkTexts = new List<string>();
//
// foreach (var path in Directory.GetFiles("docs", "*.md"))
// {
//     var fileName = Path.GetFileName(path);
//     var parts = (await File.ReadAllTextAsync(path)).Split("\n## ");
//
//     // parts[0] = ilk ##'den onceki kisim, yani "# Belge Basligi"
//     var docTitle = parts[0].Trim('#', ' ', '\n', '\r');
//
//     foreach (var part in parts.Skip(1))   // Skip(1): H1 bolumu parca degil
//     {
//         var heading = part.Split('\n')[0].Trim();
//         var text = $"{docTitle} > {part.Trim()}";
//
//         chunks.Add(($"{fileName}#{heading}", text));
//         chunkTexts.Add(text);
//     }
// }
//
// // Parcalar acilista BIR KEZ embed'lenir.
// var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
// var chunkEmbeddings = await embedder.GenerateAsync(chunkTexts);
//
// Console.WriteLine($"{chunks.Count} parça embed'lendi:");
// foreach (var chunk in chunks)
// {
//     Console.WriteLine($"  - {chunk.Source}");
// }
//
// // ---------- 2. POST /ask ----------
// app.MapPost("/ask", async (AskRequest request, IEmbeddingGenerator<string, Embedding<float>> generator, IChatClient chat) =>
// {
//     if (string.IsNullOrWhiteSpace(request.Question))
//     {
//         return Results.BadRequest("question alanı boş olamaz.");
//     }
//
//     // Soruyu embed'le, her parcayla karsilastir
//     var questionEmbedding = (await generator.GenerateAsync([request.Question]))[0];
//     var scored = new List<(string Source, string Text, float Score)>();
//
//     for (int i = 0; i < chunks.Count; i++)
//     {
//         var score = CosineSimilarity(questionEmbedding.Vector.Span, chunkEmbeddings[i].Vector.Span);
//         scored.Add((chunks[i].Source, chunks[i].Text, score));
//     }
//
//     // En yakin 4 parca. Esik degil top-K: Modul 5'te olctuk, esikle filtreleme calismiyor.
//     var top = scored.OrderByDescending(result => result.Score).Take(4).ToList();
//
//     // Parcalari numarali etiketlerle paketle (Modul 1'deki delimiter fikri)
//     var context = "";
//     for (int i = 0; i < top.Count; i++)
//     {
//         context += $"<chunk id=\"{i + 1}\" source=\"{top[i].Source}\">\n{top[i].Text}\n</chunk>\n\n";
//     }
//
//     // RAG prompt'unu doldur, Claude'a sor
//     var template = await File.ReadAllTextAsync(Path.Combine("prompts", "rag.txt"));
//     var messages = new List<ChatMessage>
//     {
//         new(ChatRole.System, template.Replace("{baglam}", context)),
//         new(ChatRole.User, request.Question)
//     };
//
//     var response = await chat.GetResponseAsync(messages,
//         new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 500 });
//
//     // sources her zaman doner: kullanici cevabi kaynaga bakip dogrulayabilsin
//     var sources = new List<object>();
//     foreach (var result in top)
//     {
//         sources.Add(new { source = result.Source, score = result.Score });
//     }
//
//     return Results.Ok(new
//     {
//         answer = response.Text,
//         sources,
//         usage = new { input = response.Usage?.InputTokenCount, output = response.Usage?.OutputTokenCount }
//     });
// });

#endregion

#region Ödev

// Chunck Listesi (Kodu ve Yazısı) -> Embedlenecek
var chunks = new List<(string Source, string Text)>();

// Chunckların metinlerinin listeleneceği yer
var chunkTexts = new List<string>();



foreach (var path in Directory.GetFiles("homeworkdocs", "*.md"))
{
    var fileName = Path.GetFileName(path);
    var parts = (await File.ReadAllTextAsync(path)).Split("\n## ");
    
    var docTitle = parts[0].Trim('#', ' ', '\n', '\r');

    foreach (var part in parts.Skip(1)) 
    {
        var heading = part.Split('\n')[0].Trim();
        var text = $"{docTitle} > {part.Trim()}";

        chunks.Add(($"{fileName}#{heading}", text));
        chunkTexts.Add(text);
    }
}

// Emmedleme 
var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
var chunkEmbeddings = await embedder.GenerateAsync(chunkTexts);  // Metin olarak tutulan chunchlar embedleniyor

Console.WriteLine($"{chunks.Count} parça embed'lendi:");
foreach (var chunk in chunks)
{
    Console.WriteLine($"  - {chunk.Source}");
}

//  POST /ask

app.MapPost("/ask", async (AskRequest request, IEmbeddingGenerator<string, Embedding<float>> generator, IChatClient chat) =>
{
    if (string.IsNullOrWhiteSpace(request.Question))
    {
        return Results.BadRequest("question alanı boş olamaz.");
    }
    
    var questionEmbedding = (await generator.GenerateAsync([request.Question]))[0];
    var scored = new List<(string Source, string Text, float Score)>();

    for (int i = 0; i < chunks.Count; i++)
    {
        var score = CosineSimilarity(questionEmbedding.Vector.Span, chunkEmbeddings[i].Vector.Span);
        scored.Add((chunks[i].Source, chunks[i].Text, score));
    }
    
    var top = scored.OrderByDescending(result => result.Score).Take(4).ToList();
    
    var context = "";
    for (int i = 0; i < top.Count; i++)
    {
        context += $"<chunk id=\"{i + 1}\" source=\"{top[i].Source}\">\n{top[i].Text}\n</chunk>\n\n";
    }
    
    var template = await File.ReadAllTextAsync(Path.Combine("homeworkprompts", "Readmerag.txt"));
    
    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, template.Replace("{baglam}", context)),
        new(ChatRole.User, request.Question)
    };

    var options = new ChatOptions
    {
        Temperature = 0.0f, MaxOutputTokens = 500
    };
    
    var response = await chat.GetResponseAsync(messages,options);
       
    
    var sources = new List<object>();
    
    foreach (var result in top)
    {
        sources.Add(new { source = result.Source, score = result.Score });
    }

    return Results.Ok(new
    {
        answer = response.Text,
        sources,
        usage = new { input = response.Usage?.InputTokenCount, output = response.Usage?.OutputTokenCount }
    });
});

#endregion

app.Run();

// Iki vektorun acisinin kosinusu: 1 = ayni yon, 0 = alakasiz.
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

// POST govdesindeki JSON bu tipe baglanir: {"question": "..."}
record AskRequest(string Question);
