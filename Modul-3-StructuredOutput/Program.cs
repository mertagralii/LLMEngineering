using Anthropic;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };
IChatClient chat = client.AsIChatClient("claude-haiku-4-5");


#region Version - 1
//
// // Ikinci metinde yas bilgisi YOK - validasyonun ne ise yaradigini gosterecek
// var texts = new[]
// {
//     "Ahmet Yılmaz 28 yaşında, Ankara'da yaşıyor.",
//     "Mehmet İstanbul'da yaşıyor, uzun süredir orada."
// };
//
// foreach (var text in texts)
// {
//     Console.WriteLine($"--- {text}");
//
//     var messages = new List<ChatMessage>
//     {
//         new(ChatRole.System, "Verilen metinden kişi bilgisini çıkar."),
//         new(ChatRole.User, text)
//     };
//
//     // 1 ilk deneme + 2 tekrar
//     for (int attempt = 1; attempt <= 3; attempt++)
//     {
//         var options = new ChatOptions { Temperature = 0.0f };
//         var response = await chat.GetResponseAsync<Person>(messages, options);
//
//         if (!response.TryGetResult(out var person))
//         {
//             Console.WriteLine($"  {attempt}. deneme: cevap şemaya uymadı");
//             messages.AddRange(response.Messages);
//             messages.Add(new(ChatRole.User, "Cevabın istenen şemaya uymadı. Tekrar dene."));
//             continue;
//         }
//
//         // Age null = "metinde yoktu". Bu bir hata degil, gecerli bir sonuc.
//         // Modelin duzeltemeyecegi bir sey icin retry yapmak hem para yakar
//         // hem de modeli makul gorunen bir sayi uydurmaya iter.
//         string? error = null;
//         if (string.IsNullOrWhiteSpace(person.Name))
//         {
//             error = "Ad boş olamaz.";
//         }
//         else if (person.Age is < 1 or > 120)
//         {
//             error = $"Yaş {person.Age} olamaz, 1-120 arasında olmalı.";
//         }
//         else if (string.IsNullOrWhiteSpace(person.City))
//         {
//             error = "Şehir boş olamaz.";
//         }
//
//         if (error is null)
//         {
//             var ageText = person.Age?.ToString() ?? "bilinmiyor";
//             Console.WriteLine($"  ✓ {person.Name} | {ageText} | {person.City}   ({attempt}. denemede)");
//             break;
//         }
//
//         Console.WriteLine($"  {attempt}. deneme: {error}");
//         messages.AddRange(response.Messages);
//         messages.Add(new(ChatRole.User, $"Hata: {error} Düzelt ve tekrar ver."));
//
//         if (attempt == 3) Console.WriteLine("  ✗ 3 denemede de geçerli sonuç alınamadı, vazgeçildi");
//     }
//
//     Console.WriteLine();
// }
//
// record Person(string Name, int? Age, string City);

#endregion

#region Ödev

var texts = new[]
{
    "ACME Bilişim Ltd. Şti. tarafından 15.03.2026 tarihinde kesilen A-2026-0147 numaralı fatura. Tutar 12.500,00 TL, KDV 2.250,00 TL dahil.",
    "Yıldız Matbaa'dan aldığım fatura, numara B-881. Toplam 4750 lira. Tarihini göremiyorum, silik.",
    "Fatura no: C-2026-9, Delta Yazılım A.Ş., 01.02.2026. Ara toplam 1000 TL, KDV 1800 TL, genel toplam 2800 TL"
};

foreach (var text in texts)
{
    Console.WriteLine($"--- {text}");
    
    var messages = new List<ChatMessage>
    {
        new(ChatRole.System,
            "Verilen serbest metinden fatura bilgisini çıkar. " +
            "Sayılar Türkçe formatta yazılmış olabilir: nokta binlik ayırıcı, virgül ondalık ayırıcıdır " +
            "(12.500,00 = on iki bin beş yüz). " +
            "Metinde geçmeyen bir alanı uydurma, boş bırak. " +
            "Para birimini üç harfli kodla ver: TRY, USD, EUR."),
        new(ChatRole.User, text)
    };

    for (int attempt = 1; attempt <= 2; attempt++)
    {
        var options = new ChatOptions { Temperature = 0.0f };
        var response = await chat.GetResponseAsync<InvoiceDto>(messages, options);
        if (!response.TryGetResult(out var invoice))
        {
            Console.WriteLine($"{attempt}. deneme: cevap şemaya uymadı");
            messages.AddRange(response.Messages);
            messages.Add(new(ChatRole.User, "Cevabın istenen şemaya uymadı. Tekrar dene."));
            continue;
        }
        // Hatanin kaynagi onemli:
        //   model hatasi  -> retry mantikli (yanlis okumus olabilir)
        //   veri hatasi   -> retry ANLAMSIZ (kaynak metin zaten tutarsiz, model onu uretmedi)
        string? error = null;
        bool isDataError = false;

         if (string.IsNullOrWhiteSpace(invoice.Seller))
         {
             error = "Satıcı ismi olmak zorunda";
         }
         else if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
         {
             error = "Numarasız fatura olmaz.";
         }
         else if (invoice.Total <= 0)
         {
             error = "Toplam sıfır veya eksi olamaz.";
         }
         
         else if (invoice.Subtotal is not null && invoice.TaxAmount is not null && invoice.Subtotal + invoice.TaxAmount != invoice.Total)
         {
             error = $"Ara toplam ({invoice.Subtotal}) + KDV ({invoice.TaxAmount}) = "
                   + $"{invoice.Subtotal + invoice.TaxAmount}, ama genel toplam {invoice.Total} yazıyor.";
             isDataError = true;
         }
         
         else if (invoice.TaxAmount > invoice.Subtotal)
         {
             error = $"KDV ({invoice.TaxAmount}) ara toplamdan ({invoice.Subtotal}) büyük olamaz.";
             isDataError = true;
         }


         if (error is null)
         {
             var dateText = invoice.IssueDate?.ToString("dd.MM.yyyy") ?? "yok";
             var subtotalText = invoice.Subtotal?.ToString() ?? "yok";
             var taxText = invoice.TaxAmount?.ToString() ?? "yok";
             var currencyText = invoice.Currency ?? "yok";

             Console.WriteLine($"  ✓ {invoice.Seller} | {invoice.InvoiceNumber} | {dateText}");
             Console.WriteLine($"    ara toplam: {subtotalText} | KDV: {taxText} | toplam: {invoice.Total} {currencyText}   ({attempt}. denemede)");
             break;
         }

         Console.WriteLine($" {attempt}. deneme: {error}");

         // Kaynak veri tutarsizsa tekrar sormak modeli alani silmeye ya da
         // uydurmaya iter. Retry'a hic girme, insana birak.
         if (isDataError)
         {
             Console.WriteLine("  ⚠ Kaynak metindeki veri tutarsız — model düzeltemez, insan kontrolü gerekiyor.");
             break;
         }

         messages.AddRange(response.Messages);
         messages.Add(new(ChatRole.User, $"Hata: {error} Düzelt ve tekrar ver."));

         if (attempt == 2)
         {
             Console.WriteLine("  ✗ 2 denemede de geçerli sonuç alınamadı, vazgeçildi");
         }
         
         
             
        
    }
}

#endregion

record InvoiceDto(string Seller, string InvoiceNumber, DateTime? IssueDate, decimal? Subtotal, decimal? TaxAmount, decimal Total, string? Currency);

