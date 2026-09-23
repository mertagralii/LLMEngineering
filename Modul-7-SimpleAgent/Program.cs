using Anthropic;
using Microsoft.Extensions.AI;
using Modul_7_SimpleAgent;
using OllamaSharp;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Arama yerelde (bedava)
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(new Uri("http://localhost:11434"), "bge-m3"));

// Cevap Claude'da. AsBuilder -> UseFunctionInvocation -> Build zinciri Modul 4'ten.
builder.Services.AddSingleton<IChatClient>(
    new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" }
        .AsIChatClient("claude-haiku-4-5")
        .AsBuilder()
        .UseFunctionInvocation(configure: agent =>
        {
            // Varsayilani 40. Durma kosulu: agent en fazla 8 tur donebilir.
            agent.MaximumIterationsPerRequest = 8;
        })
        .Build());

// Parcalar ve vektorler tek kopya olmali -> Singleton
builder.Services.AddSingleton<DocumentStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Chunk'lama + embedding acilista BIR KEZ, ilk istek gelmeden once.
var store = app.Services.GetRequiredService<DocumentStore>();
await store.InitializeAsync();

Console.WriteLine($"{store.Count} parça hazır. POST /ask");

// Controller'lari tarayip yollarini kaydeder: AgentController -> POST /ask
app.MapControllers();

app.Run();
