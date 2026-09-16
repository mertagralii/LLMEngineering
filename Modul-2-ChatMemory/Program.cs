using System.Text.Encodings.Web;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
IChatClient chat = client.AsIChatClient("claude-haiku-4-5");

#region Version - 1

// // Pencere boyutu: system haric tutulacak mesaj sayisi
// const int maxHistoryMessages = 6;
//
// var history = new List<ChatMessage>
// {
//     new(ChatRole.System, "Kısa ve net cevap veren bir asistansın.")
// };
//
// Console.WriteLine("Sohbet başladı. Çıkmak için boş satır bırak.");
// Console.WriteLine();
//
// while (true)
// {
//     Console.Write("Sen: ");
//     var input = Console.ReadLine();
//     if (string.IsNullOrWhiteSpace(input)) break;
//
//     history.Add(new(ChatRole.User, input));
//
//     // Sliding window: system (indeks 0) korunur, en eski User+Assistant cifti dusulur
//     while (history.Count - 1 > maxHistoryMessages)
//     {
//         history.RemoveRange(1, 2);
//     }
//
//     var options = new ChatOptions
//     {
//         Temperature = 0.3f,
//         MaxOutputTokens = 500
//     };
//
//     var response = await chat.GetResponseAsync(history, options);
//
//     history.Add(new(ChatRole.Assistant, response.Text));
//
//     Console.WriteLine($"Asistan: {response.Text}");
//     Console.WriteLine($"[geçmiş: {history.Count - 1} mesaj | input: {response.Usage?.InputTokenCount} | output: {response.Usage?.OutputTokenCount}]");
//     Console.WriteLine();
// }

#endregion


#region Version - 2

//
// const int tokenBudget = 1200;
//
// const int keepRecentMessages = 2;
//
// var history = new List<ChatMessage>
// {
//     new(ChatRole.System, "Kısa ve net cevap veren bir asistansın. En fazla 3 cümle yaz. " +
//                          "Başlık, tablo, madde işareti ve emoji kullanma.")
// };
//
// Console.WriteLine("Sohbet başladı. Çıkmak için boş satır bırak.");
// Console.WriteLine();
//
// while (true)
// {
//     Console.Write("Sen: ");
//     var input = Console.ReadLine();
//     if (string.IsNullOrWhiteSpace(input)) break;
//
//     history.Add(new(ChatRole.User, input));
//
//     var options = new ChatOptions
//     {
//         Temperature = 0.3f,
//         MaxOutputTokens = 150
//     };
//
//     var response = await chat.GetResponseAsync(history, options);
//
//     history.Add(new(ChatRole.Assistant, response.Text));
//
//     Console.WriteLine($"Asistan: {response.Text}");
//     Console.WriteLine($"[geçmiş: {history.Count - 1} mesaj | input: {response.Usage?.InputTokenCount} | output: {response.Usage?.OutputTokenCount}]");
//     Console.WriteLine();
//
//     // Context budget: token esigi asildiysa eski mesajlari tek ozete indir
//     if (response.Usage?.InputTokenCount > tokenBudget && history.Count - 1 > keepRecentMessages)
//     {
//         Console.WriteLine($"[bütçe aşıldı: {response.Usage?.InputTokenCount} > {tokenBudget} — geçmiş özetleniyor]");
//
//         // system (indeks 0) ve son keepRecentMessages mesaj haric kalan her sey
//         var toSummarize = history.GetRange(1, history.Count - 1 - keepRecentMessages);
//         var conversationText = string.Join("\n", toSummarize.Select(message => $"{message.Role}: {message.Text}"));
//
//         var summaryTemplate = await File.ReadAllTextAsync(Path.Combine("prompts", "summarize.txt"));
//         var summaryPrompt = summaryTemplate.Replace("{konusma}", conversationText);
//
//         var summaryMessages = new List<ChatMessage> { new(ChatRole.User, summaryPrompt) };
//         var summaryResponse = await chat.GetResponseAsync(summaryMessages,
//             new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 300 });
//
//         history.RemoveRange(1, toSummarize.Count);
//         history.Insert(1, new ChatMessage(ChatRole.System, $"Önceki konuşmanın özeti: {summaryResponse.Text}"));
//
//         Console.WriteLine($"[{toSummarize.Count} mesaj tek özete indi]");
//         Console.WriteLine();
//     }
// }

#endregion

#region Version - 3

var projectRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");                                                                                                      
var historyFolder = Path.Combine(projectRoot, "json"); 
var historyPath = Path.Combine(historyFolder, "chat-history.json");


Directory.CreateDirectory(historyFolder);

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,                                   // okunabilir dosya
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // Turkce karakterler bozulmasin
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase       // "role", "text"
};

var savedMessages = new List<SavedMessage>();

if (!File.Exists(historyPath))
{

    await File.WriteAllTextAsync(historyPath, "[]");
    Console.WriteLine("[geçmiş dosyası yok — yeni dosya oluşturuldu]");
}
else
{
    var json = await File.ReadAllTextAsync(historyPath);

    if (string.IsNullOrWhiteSpace(json))
    {
        Console.WriteLine("[geçmiş dosyası boş — yeni sohbet]");
    }
    else
    {
        try
        {
            savedMessages = JsonSerializer.Deserialize<List<SavedMessage>>(json, jsonOptions) ?? new List<SavedMessage>();
            
            if (savedMessages.Any(saved => string.IsNullOrWhiteSpace(saved.Role)
                                        || string.IsNullOrWhiteSpace(saved.Text)))
            {
                Console.WriteLine("[geçmiş dosyası eksik alan içeriyor, yok sayılıyor]");
                savedMessages = new List<SavedMessage>();
            }
            else
            {
                Console.WriteLine($"[geçmiş yüklendi: {savedMessages.Count} mesaj]");
            }
        }
        catch (JsonException exception)
        {

            Console.WriteLine($"[geçmiş dosyası bozuk, yok sayılıyor: {exception.Message}]");
        }
    }
}


var messages = new List<ChatMessage>();

if (savedMessages.Count == 0)
{
    messages.Add(new(ChatRole.System, "Kısa ve net cevap veren bir asistansın. En fazla 3 cümle yaz. " +
                                      "Başlık, tablo, madde işareti ve emoji kullanma."));
}
else
{
    foreach (var saved in savedMessages)
    {
        messages.Add(new ChatMessage(new ChatRole(saved.Role), saved.Text));
    }
}

var options = new ChatOptions
{
    Temperature = 0.0f,
    MaxOutputTokens = 1024
};
const int tokenBudget = 1200;

const int keepRecentMessages = 2;

while (true)
{
    Console.Write("Sen: ");
    var userMessages = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(userMessages))
    {
        break;
    }
    messages.Add(new(ChatRole.User, userMessages));
    var response = await chat.GetResponseAsync(messages, options);
    if (response.Text == null)
    {
        Console.WriteLine("[modelden boş cevap geldi, tur atlanıyor]");
        continue;
    }
    Console.WriteLine();
    Console.WriteLine($"Claude AI: {response.Text}");
    Console.WriteLine();
    Console.WriteLine($"input: {response.Usage?.InputTokenCount}  output: {response.Usage?.OutputTokenCount}");
    messages.Add(new(ChatRole.Assistant, response.Text));
    
    if (response.Usage?.InputTokenCount > tokenBudget && messages.Count - 1 > keepRecentMessages)
     {
        Console.WriteLine($"[bütçe aşıldı: {response.Usage?.InputTokenCount} > {tokenBudget} — geçmiş özetleniyor]");
        
         var toSummarize = messages.GetRange(1, messages.Count - 1 - keepRecentMessages);
         var conversationText = string.Join("\n", toSummarize.Select(message => $"{message.Role}: {message.Text}"));

         var summaryTemplate = await File.ReadAllTextAsync(Path.Combine("prompts", "summarize.txt"));
         var summaryPrompt = summaryTemplate.Replace("{konusma}", conversationText);

         var summaryMessages = new List<ChatMessage> { new(ChatRole.User, summaryPrompt) };
         var summaryResponse = await chat.GetResponseAsync(summaryMessages, 
             new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 300 });

         messages.RemoveRange(1, toSummarize.Count);
         messages.Insert(1, new ChatMessage(ChatRole.System, $"Önceki konuşmanın özeti: {summaryResponse.Text}"));

         Console.WriteLine($"[{toSummarize.Count} mesaj tek özete indi]");
        Console.WriteLine();
     }
    var toSave = messages.Select(m => new SavedMessage(m.Role.Value, m.Text ?? "")).ToList();
    await File.WriteAllTextAsync(historyPath, JsonSerializer.Serialize(toSave, jsonOptions));
    
}

#endregion


record SavedMessage(string Role, string Text);