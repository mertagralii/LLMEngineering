using Anthropic;
using Microsoft.Extensions.AI;

Console.WriteLine("=== Modül 0 — Model ve Temperature Karşılaştırması ===");
Console.WriteLine();
Console.WriteLine("Aynı soru iki modele, üç farklı temperature ile soruluyor.");
Console.WriteLine("Değişen tek şey model ve temperature; geri kalan her şey sabit:");
Console.WriteLine();
Console.WriteLine("  MaxOutputTokens : 1024");
Console.WriteLine("  System prompt   : Kısa ve net cevap veren bir asistansın.");
Console.WriteLine("  User prompt     : Token nedir? Tek cümleyle.");
Console.WriteLine();
Console.WriteLine("Toplam 6 API çağrısı yapılacak. Her satır bir çağrıdır.");

Console.WriteLine();

var client = new AnthropicClient { ApiKey = "Buraya_Anthroic_Console_Sitesinden_Aldığınız_APİKEY_Yapıştırın" };

var claudeModels = new[] // Kullanacağımız Claude Modellerimiz NOT : Sonnet5'yi koymadık yeni modellerde temperature özellikleri kalktı. Onun yerine Effort geldi.
{
    (Name: "claude-haiku-4-5",  InputToken: 1.00m, OutputToken:  5.00m),
    (Name: "claude-sonnet-4-6", InputToken: 3.00m, OutputToken: 15.00m)
};

var temperatures = new[] { 0.0f, 0.7f, 1.0f };

decimal totalPrice = 0;
long totalInputTokens = 0;
long totalOutputTokens = 0;

var messages = new List<ChatMessage>
{
    new(ChatRole.System, "Kısa ve net cevap veren bir asistansın."), // SystemPromptumuzu girdiğimiz yer
    new(ChatRole.User,   "Token nedir? Tek cümleyle.") // Kullanıcının mesajı
};
var answers = new List<string>();

Console.WriteLine($"{"Model",-18}{"Temp",6}{"Input",8}{"Output",8}{"Price",12}");
Console.WriteLine(new string('-', 52));

foreach (var model in claudeModels)
{
    IChatClient chat = client.AsIChatClient(model.Name);

    foreach (var temperature in temperatures)
    {
        var options = new ChatOptions
        {
            Temperature = temperature,
            MaxOutputTokens = 1024
        };

        var response = await chat.GetResponseAsync(messages, options);

        decimal price = (response.Usage?.InputTokenCount ?? 0) / 1_000_000m * model.InputToken
                        + (response.Usage?.OutputTokenCount ?? 0) / 1_000_000m * model.OutputToken;

        Console.WriteLine($"{model.Name,-18}{temperature,6:F1}{response.Usage?.InputTokenCount,8}{response.Usage?.OutputTokenCount,8}{price,12:F6}");

        answers.Add($"{model.Name} | temp {temperature:F1} | {response.Text}");

        totalPrice += price;
        totalInputTokens += response.Usage?.InputTokenCount ?? 0;
        totalOutputTokens += response.Usage?.OutputTokenCount ?? 0;
        
    }
}

Console.WriteLine(new string('-', 52));
Console.WriteLine($"{"TOPLAM",-18}{"",6}{totalInputTokens,8}{totalOutputTokens,8}{totalPrice,12:F6}");

Console.WriteLine();
Console.WriteLine("Cevaplar:");
Console.WriteLine();

foreach (var answer in answers)
{
    Console.WriteLine(answer);
    Console.WriteLine();
}
