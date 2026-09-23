using System.Text.Encodings.Web;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
IChatClient chat = client.AsIChatClient("claude-haiku-4-5");

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

var projectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

// ---------- Komut secimi ----------
var command = args.Length > 0 ? args[0] : "run";

if (command == "compare")
{
    Compare(args[1], args[2]);
    return;
}

if (command == "judge")
{
    await JudgeAsync(args[1]);
    return;
}

// dotnet run -- run <prompt dosyasi> <etiket>
var promptFile = args.Length > 1 ? args[1] : "1-zero-shot.txt";
var label      = args.Length > 2 ? args[2] : "zero-shot";

// ---------- Eval setini oku ----------
var suiteJson = await File.ReadAllTextAsync(Path.Combine(projectRoot, "evals", "sentiment.json"));
var suite = JsonSerializer.Deserialize<EvalSuite>(suiteJson, jsonOptions)!;

var template = await File.ReadAllTextAsync(Path.Combine(projectRoot, "prompts", promptFile));
var options = new ChatOptions
{
    Temperature = 0.0f,
    MaxOutputTokens = 1024
};

var cases = new List<CaseResult>();
long inputTokens = 0;
long outputTokens = 0;

Console.WriteLine($"=== {suite.Name} · {promptFile} · {suite.Cases.Count} vaka ===");

// ---------- Her vakayi calistir ----------
foreach (var evalCase in suite.Cases)
{
    var messages = new List<ChatMessage>
    {
        new(ChatRole.System, template.Replace("{yorum}", evalCase.Input)),
        new(ChatRole.User,   "Bu yorumu sınıflandır.")
    };

    var response = await chat.GetResponseAsync(messages, options);

    // CoT gerekce yazdigi icin etiket SON satirda; digerlerinde zaten tek satir (Modul 1'in dersi)
    var lines = (response.Text ?? "")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var actual = lines.Length > 0 ? lines[^1].ToLower() : "(boş)";
    var pass = actual == evalCase.Expected;

    cases.Add(new CaseResult(evalCase.Id, evalCase.Input, evalCase.Expected, actual, pass));

    inputTokens  += response.Usage?.InputTokenCount  ?? 0;
    outputTokens += response.Usage?.OutputTokenCount ?? 0;

    Console.Write(pass ? "." : "x");   // 20 vaka boyunca ilerleme cubugu
}

Console.WriteLine();
Console.WriteLine();

// ---------- Skorla ----------
var passed = 0;

foreach (var result in cases)
{
    if (result.Pass)
    {
        passed++;
    }
}

// Modul 1'deki fiyatlandirma: input $1/M token, output $5/M token
var cost = inputTokens / 1_000_000m * 1.00m + outputTokens / 1_000_000m * 5.00m;

Console.WriteLine($"skor    : {passed}/{cases.Count}  (%{passed * 100 / cases.Count})");
Console.WriteLine($"token   : {inputTokens} in / {outputTokens} out");
Console.WriteLine($"maliyet : ${cost:F6}");

// Dogrulari yazdirmiyoruz: 20 satir dogru cikti okunmaz, 3 satir yanlis okunur.
Console.WriteLine();
Console.WriteLine("YANLIŞLAR:");

foreach (var result in cases)
{
    if (result.Pass)
    {
        continue;
    }

    Console.WriteLine($"  {result.Id,-8} beklenen: {result.Expected,-8} model: {result.Actual}");
    Console.WriteLine($"           \"{result.Input}\"");
}

// ---------- Sonucu dosyaya yaz ----------
// bin/ icine degil proje kokune yaziyoruz: sonuclar commit'lenebilsin, gozle acilabilsin.
var resultsDir = Path.Combine(projectRoot, "results");
Directory.CreateDirectory(resultsDir);

var run = new EvalRun(label, promptFile, DateTime.Now, cases.Count, passed,
                      inputTokens, outputTokens, cost, cases);

var outPath = Path.Combine(resultsDir, $"{DateTime.Now:yyyy-MM-dd}_{label}.json");
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(run, jsonOptions));

Console.WriteLine();
Console.WriteLine($"-> {outPath}");

// ---------- compare komutu ----------
void Compare(string fileA, string fileB)
{
    var a = LoadRun(fileA);
    var b = LoadRun(fileB);

    var diff = b.Passed - a.Passed;
    var sign = diff >= 0 ? "+" : "";

    Console.WriteLine($"{a.Label} {a.Passed}/{a.Total}  ->  {b.Label} {b.Passed}/{b.Total}   ({sign}{diff})");
    Console.WriteLine();

    // Vakalari SIRA ile degil KIMLIK ile eslestiriyoruz: sete yeni vaka
    // eklendiginde eski karsilastirmalar kaymasin.
    var previous = new Dictionary<string, CaseResult>();

    foreach (var result in a.Cases)
    {
        previous[result.Id] = result;
    }

    var repaired = new List<string>();
    var broken = new List<string>();

    foreach (var current in b.Cases)
    {
        if (!previous.TryGetValue(current.Id, out var old))
        {
            continue;   // sete sonradan eklenmis vaka, karsilastirilacak esi yok
        }

        if (!old.Pass && current.Pass)
        {
            repaired.Add($"  {current.Id,-8} {old.Actual} -> {current.Actual}");
        }

        if (old.Pass && !current.Pass)
        {
            broken.Add($"  {current.Id,-8} {old.Actual} -> {current.Actual}   (beklenen: {current.Expected})");
        }
    }

    Console.WriteLine($"DÜZELEN ({repaired.Count}):");

    foreach (var line in repaired)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine($"🔴 BOZULAN ({broken.Count}):");

    foreach (var line in broken)
    {
        Console.WriteLine(line);
    }

    Console.WriteLine();
    Console.WriteLine($"token   : {a.InputTokens}+{a.OutputTokens} -> {b.InputTokens}+{b.OutputTokens}");
    Console.WriteLine($"maliyet : ${a.Cost:F6} -> ${b.Cost:F6}   ({b.Cost / a.Cost:F1}x)");
}

// ---------- judge komutu ----------
// Exact match "yanlis" der ama neden yanlis oldugunu soylemez.
// Hakem, yanlisin ne kadar yanlis oldugunu puanlar.
async Task JudgeAsync(string file)
{
    var run = LoadRun(file);
    var template = await File.ReadAllTextAsync(Path.Combine(projectRoot, "prompts", "judge.txt"));
    var judgeOptions = new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 300 };

    var failed = new List<CaseResult>();

    foreach (var result in run.Cases)
    {
        if (!result.Pass)
        {
            failed.Add(result);
        }
    }

    if (failed.Count == 0)
    {
        Console.WriteLine("Yanlış vaka yok, hakeme gerek kalmadı.");
        return;
    }

    Console.WriteLine($"=== {run.Label} · {failed.Count} yanlış vaka hakeme gidiyor ===");
    Console.WriteLine();

    foreach (var result in failed)
    {
        var systemPrompt = template
            .Replace("{yorum}",    result.Input)
            .Replace("{beklenen}", result.Expected)
            .Replace("{model}",    result.Actual);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User,   "Bu cevabı puanla.")
        };

        // Modul 3'un structured output'u: sema modele gonderiliyor, cevap tipe baglaniyor
        var response = await chat.GetResponseAsync<Verdict>(messages, judgeOptions);

        Console.WriteLine($"{result.Id,-8} beklenen: {result.Expected}   model: {result.Actual}");
        Console.WriteLine($"         \"{result.Input}\"");

        if (response.TryGetResult(out var verdict))
        {
            Console.WriteLine($"         hakem: {verdict.Score}/5 — {verdict.Reason}");
        }
        else
        {
            Console.WriteLine("         hakem cevabı şemaya uymadı.");
        }

        Console.WriteLine();
    }
}

EvalRun LoadRun(string path)
{
    var full = Path.IsPathRooted(path) ? path : Path.Combine(projectRoot, path);
    return JsonSerializer.Deserialize<EvalRun>(File.ReadAllText(full), jsonOptions)!;
}

// ---------- Tipler ----------
record EvalSuite(string Name, List<EvalCase> Cases);
record EvalCase(string Id, string Input, string Expected);
record CaseResult(string Id, string Input, string Expected, string Actual, bool Pass);
record EvalRun(string Label, string Prompt, DateTime RunAt, int Total, int Passed,
               long InputTokens, long OutputTokens, decimal Cost, List<CaseResult> Cases);
record Verdict(int Score, string Reason);
