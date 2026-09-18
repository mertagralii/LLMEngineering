using Microsoft.Extensions.AI;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

// <string, Embedding<float>> = ne veriyorum, ne aliyorum. IChatClient'in embedding karsiligi.
// Singleton: durumsuz, her istekte yeni baglanti kurmaya gerek yok.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3"));

var app = builder.Build();

// Cumleler koddan ayri: eklemek icin yeniden derlemeye gerek yok.
var corpus = (await File.ReadAllLinesAsync("corpus.txt"))
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .ToArray();

// DI kutusuna koyduk (satir 8), simdi geri aliyoruz. Endpoint disindayiz, kimse vermiyor.
var embedder = app.Services.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

// Acilista BIR KEZ. Endpoint icinde olsaydi her istekte 50 cumle bosuna embed'lenirdi.
var corpusEmbeddings = await embedder.GenerateAsync(corpus);

Console.WriteLine($"{corpus.Length} cümle embed'lendi -> her biri {corpusEmbeddings[0].Vector.Length} boyutlu vektör");

// q: query string'den, generator: DI'dan, top: query string'den (varsayilanli oldugu icin en sonda)
app.MapGet("/search", async (string? q, IEmbeddingGenerator<string, Embedding<float>> generator, int top = 5) =>
{
    if (string.IsNullOrWhiteSpace(q))
    {
        return Results.BadRequest("q parametresi boş olamaz. Örnek: /search?q=yağmur yağacak mı&top=5");
    }

    // Disaridan gelen sayi: 0 veya negatif bos sonuc dondururdu.
    top = Math.Clamp(top, 1, corpus.Length);

    // GenerateAsync toplu calisir: liste ister, liste doner. Tek sorgu gonderdik, [0] ile aldik.
    var queryEmbedding = (await generator.GenerateAsync([q]))[0];

    // Sorguyu 50 cumlenin HEPSIYLE karsilastir. Bu olcekte hizli; milyonlarda vektor DB gerekir.
    var results = new List<(string Sentence, float Score)>();
    for (int i = 0; i < corpus.Length; i++)
    {
        // Iki vektor (her biri 1024 sayi) -> tek benzerlik skoru
        var score = CosineSimilarity(queryEmbedding.Vector.Span, corpusEmbeddings[i].Vector.Span);
        results.Add((corpus[i], score));
    }

    return Results.Ok(results
        .OrderByDescending(result => result.Score)   // buyukten kucuge SIRALA
        .Take(top)                                   // ilk N tanesini al
        .Select(result => new { sentence = result.Sentence, score = result.Score }));
});

app.Run();

// Iki vektorun acisinin kosinusu: 1 = ayni yon, 0 = dik/alakasiz, -1 = zit.
// Ornek: a=[3,4] b=[6,8] -> dot=50, |a|=5, |b|=10 -> 50/(5*10) = 1.0 (b uzun ama yon ayni)
float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
{
    float dot = 0, magnitudeA = 0, magnitudeB = 0;

    for (int i = 0; i < a.Length; i++)
    {
        dot        += a[i] * b[i];     // ayni yone ne kadar bakiyorlar
        magnitudeA += a[i] * a[i];     // a'nin uzunlugunun karesi
        magnitudeB += b[i] * b[i];     // b'nin uzunlugunun karesi
    }

    // Bolme uzunlugu eler, geriye sadece aci kalir
    return dot / (MathF.Sqrt(magnitudeA) * MathF.Sqrt(magnitudeB));
}
