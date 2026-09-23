using Microsoft.Extensions.AI;

namespace Modul_7_SimpleAgent;

// Modul 6'daki chunking + cosine kodunun sinif hali.
// Singleton: parcalar ve vektorler acilista BIR KEZ uretilir, her istekte degil.
public class DocumentStore
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embedder;
    private readonly List<(string Source, string Text)> _chunks = [];
    private GeneratedEmbeddings<Embedding<float>> _embeddings = null!;

    public DocumentStore(IEmbeddingGenerator<string, Embedding<float>> embedder)
    {
        _embedder = embedder;
    }

    public int Count => _chunks.Count;

    // Acilista bir kez cagrilir: belgeleri parcala, hepsini embed'le.
    public async Task InitializeAsync()
    {
        var texts = new List<string>();

        foreach (var path in Directory.GetFiles("docs", "*.md"))
        {
            var fileName = Path.GetFileName(path);
            var parts = (await File.ReadAllTextAsync(path)).Split("\n## ");
            var docTitle = parts[0].Trim('#', ' ', '\n', '\r');
            var index = 0;

            foreach (var part in parts.Skip(1))
            {
                index++;
                var text = $"{docTitle} > {part.Trim()}";

                // Kimlik SADECE dosya adi + sira no. Basligi kimlige koymuyoruz:
                _chunks.Add(($"{fileName}#{index}", text));
                texts.Add(text);
            }
        }

        _embeddings = await _embedder.GenerateAsync(texts);
    }

    // search_docs aracinin arkasi: skorla, sirala, SADECE onizleme dondur.
    public async Task<List<(string Source, string Preview)>> SearchAsync(string query, int top)
    {
        var queryEmbedding = (await _embedder.GenerateAsync([query]))[0];
        var scored = new List<(string Source, string Preview, float Score)>();

        for (int i = 0; i < _chunks.Count; i++)
        {
            var score = CosineSimilarity(queryEmbedding.Vector.Span, _embeddings[i].Vector.Span);
            scored.Add((_chunks[i].Source, Preview(_chunks[i].Text), score));
        }

        var results = new List<(string Source, string Preview)>();

        foreach (var item in scored.OrderByDescending(result => result.Score).Take(top))
        {
            results.Add((item.Source, item.Preview));
        }

        return results;
    }

    // read_section aracinin arkasi: kaynak kimligine gore TAM metin.
    public string? Read(string source)
    {
        var needle = source.Trim();

        foreach (var chunk in _chunks)
        {
            if (string.Equals(chunk.Source, needle, StringComparison.OrdinalIgnoreCase))
            {
                return chunk.Text;
            }
        }

        // Model kimligin yanina fazladan metin yapistirabilir ("...#1 | onizleme").
        // En UZUN eslesmeyi seciyoruz, yoksa "#12" arayan "#1"e duserdi.
        (string Source, string Text)? best = null;

        foreach (var chunk in _chunks)
        {
            if (needle.Contains(chunk.Source, StringComparison.OrdinalIgnoreCase))
            {
                if (best is null || chunk.Source.Length > best.Value.Source.Length)
                {
                    best = chunk;
                }
            }
        }

        return best?.Text;
    }

    // Modelin arama sonucunda gorecegi kadari: tek satir, 350 karakter.
    private static string Preview(string text)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');

        if (flat.Length <= 350)
        {
            return flat;
        }

        return flat[..350] + "...";
    }

    // Modul 5'ten aynen: iki vektorun acisinin kosinusu.
    private static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
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
}
