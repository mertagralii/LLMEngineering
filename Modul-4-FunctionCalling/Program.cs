using System.ComponentModel;
using Anthropic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

var client = new AnthropicClient { ApiKey = "BURAYA_ANTHROPIC_CONSOLDAN_ALINAN_KEYI_YERLESTIR" };


#region Version - 1

// // Iki metot -> iki arac. AIFunctionFactory metot imzasindan JSON semasi uretir.
// var tools = new List<AITool>
// {
//     // Ad acikca verilmezse yerel fonksiyonun derleyici adi gider: _Main_g_GetWeather_0_0
//     AIFunctionFactory.Create(GetWeather, "get_weather"),
//     AIFunctionFactory.Create(Add, "add")
// };
//
// var options = new ChatOptions
// {
//     Tools = tools,
//
// };
//
// // ---------- ADIM 1: middleware YOK ----------
// Console.WriteLine("=== ADIM 1: ham tool çağrısı (middleware yok) ===");
//
// IChatClient rawChat = client.AsIChatClient("claude-haiku-4-5");
// var response = await rawChat.GetResponseAsync("Ankara'da hava nasıl?", options);
//
// Console.WriteLine($"Cevap metni: '{response.Text}'");
//
// foreach (var content in response.Messages.SelectMany(message => message.Contents))
// {
//     if (content is FunctionCallContent call)
//     {
//         var arguments = string.Join(", ", call.Arguments?.Select(a => $"{a.Key}: {a.Value}") ?? []);
//         Console.WriteLine($"Model şunu çağırmak istiyor -> {call.Name}({arguments})");
//     }
// }
//
// Console.WriteLine("Model metodu ÇALIŞTIRMADI, sadece istedi.");
// Console.WriteLine();
//
// // ---------- ADIM 2: UseFunctionInvocation ----------
// Console.WriteLine("=== ADIM 2: otomatik tool loop ===");
//
// IChatClient autoChat = client.AsIChatClient("claude-haiku-4-5")
//     .AsBuilder() // İstemciyi "sarmalanabilir" hâle getirir
//     .UseFunctionInvocation() // Araç çağırma döngüsünü ekler
//     .Build(); // Zinciri tamamlar, kullanılabilir IChatClient verir
//
// var response2 = await autoChat.GetResponseAsync(
//     "Ankara'da hava nasıl? Bir de 17 ile 25'i topla.", options);
//
// Console.WriteLine($"Cevap: {response2.Text}");
// Console.WriteLine($"[konuşmada {response2.Messages.Count} mesaj oluştu]");
//
// // ---------- Araclar ----------
// [Description("Verilen şehir için güncel hava durumunu döndürür.")] // Model bu cümleyi okuyup aracı seçer
// string GetWeather([Description("Şehir adı, örn. Ankara")] string city) // model parametreyi nasıl dolduracağını söyler
// {
//     Console.WriteLine($"   >> GetWeather çalıştı: {city}");
//     return $"{city}: 18°C, parçalı bulutlu";
// }
//
// [Description("İki sayıyı toplar.")]
// int Add([Description("Birinci sayı")] int a, [Description("İkinci sayı")] int b)
// {
//     Console.WriteLine($"   >> Add çalıştı: {a} + {b}");
//     return a + b;
// }

#endregion

#region Ödev

using (var db = new AppDbContext())
{
    db.Database.EnsureCreated();   // tablo yoksa olusturur, varsa dokunmaz

    if (!db.Customers.Any())
    {
        var mert  = new Customer { Name = "Mert Ağralı" };
        var ayse  = new Customer { Name = "Ayşe Demir" };
        var can   = new Customer { Name = "Can Yıldız" };
        db.Customers.AddRange(mert, ayse, can);

        db.Orders.AddRange(
            new Order { Customer = mert, Product = "Mekanik klavye",   Amount = 2450.00m, OrderDate = new DateTime(2026, 1, 12) },
            new Order { Customer = mert, Product = "27 inch monitör",      Amount = 8900.00m, OrderDate = new DateTime(2026, 2,  3) },
            new Order { Customer = mert, Product = "USB-C hub",         Amount =  780.00m, OrderDate = new DateTime(2026, 2, 21) },
            new Order { Customer = mert, Product = "Kulaklık",          Amount = 1650.00m, OrderDate = new DateTime(2026, 3,  9) },
            new Order { Customer = ayse, Product = "Laptop standı",     Amount =  520.00m, OrderDate = new DateTime(2026, 1, 28) },
            new Order { Customer = ayse, Product = "Webcam",            Amount = 1980.00m, OrderDate = new DateTime(2026, 3,  2) },
            new Order { Customer = can,  Product = "Mouse pad",         Amount =  190.00m, OrderDate = new DateTime(2026, 2, 14) });

        db.SaveChanges();
        Console.WriteLine("[veritabanı oluşturuldu ve örnek verilerle dolduruldu]");
    }
    else
    {
        Console.WriteLine($"[veritabanı hazır: {db.Customers.Count()} müşteri, {db.Orders.Count()} sipariş]");
    }
}

var tools = new List<AITool>
 {
    AIFunctionFactory.Create(CustomerOrders, "customer_orders"),
    AIFunctionFactory.Create(CustomerOrderTotal, "customer_order_total"),
    AIFunctionFactory.Create(CustomerList, "customer_list")
};

var options = new ChatOptions
{
    Tools = tools,
    Temperature = 0,
    MaxOutputTokens = 1024
};

IChatClient chat = client.AsIChatClient("claude-haiku-4-5")
    .AsBuilder()
    .UseFunctionInvocation()
    .Build();

var message = new List<ChatMessage>()
{
    new(ChatRole.System,
        "Sipariş veritabanına erişimi olan bir asistansın. " +
        "Müşteri ve sipariş sorularını yanıtlamak için sana verilen araçları kullan. " +
        "Araçlardan gelmeyen bir bilgiyi uydurma. " +
        "Kısa ve net cevap ver."),
};

Console.WriteLine("=== Sipariş asistanı (otomatik tool loop) ===");
Console.WriteLine("Sormak İstediğiniz Soruyu sorunuz.");
while (true)
{
    Console.Write("Sen: ");
    var userAnswer = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(userAnswer))
    {
        break;
    }
    message.Add( new(ChatRole.User, userAnswer));
    var response = await chat.GetResponseAsync(message, options);
    message.AddRange(response.Messages); 
    Console.WriteLine($"Claude AI:{response.Text}");
}




// ---------- Araçlar ----------


[Description("Verilen isme sahip müşterinin siparişlerini listeler.")]
string CustomerOrders([Description("Müşterinin adı veya adının bir parçası, örn: Mert Ağralı")] string customerName)
{
    Console.WriteLine();
    Console.WriteLine($"   >> customer_orders çalıştı: {customerName}");
    Console.WriteLine();
    using var db = new AppDbContext();

    var customer = db.Customers.FirstOrDefault(c => c.Name.Contains(customerName));
    if (customer is null)
    {
        return $"'{customerName}' adında bir müşteri bulunamadı.";
    }

    var orders = db.Orders
        .Where(o => o.CustomerId == customer.Id)
        .Select(o => $"{o.Product} - {o.Amount} TL ({o.OrderDate:dd.MM.yyyy})")
        .ToList();

    return orders.Count == 0
        ? $"{customer.Name} müşterisinin siparişi yok."
        : $"{customer.Name} - {orders.Count} sipariş:\n" + string.Join("\n", orders);
}

[Description("Verilen isme sahip müşterinin tüm siparişlerinin toplam tutarını hesaplar.")]
string CustomerOrderTotal([Description("Müşterinin adı veya adının bir parçası, örn: Ayşe Demir")] string customerName)
{
    Console.WriteLine();
    Console.WriteLine($"   >> customer_order_total çalıştı: {customerName}");
    Console.WriteLine();
    using var db = new AppDbContext();

    var customer = db.Customers.FirstOrDefault(c => c.Name.Contains(customerName));
    if (customer is null)
    {
        return $"'{customerName}' adında bir müşteri bulunamadı.";
    }

    var total = db.Orders.Where(o => o.CustomerId == customer.Id).Sum(o => o.Amount);
    return $"{customer.Name} toplam {total} TL harcamış.";
}

[Description("Sistemdeki müşterilerin adlarını listeler.")]
string CustomerList()
{
    Console.WriteLine();
    Console.WriteLine("   >> customer_list çalıştı");
    Console.WriteLine();
    using var db = new AppDbContext();

    var names = db.Customers.Select(c => c.Name).ToList();
    return names.Count == 0 ? "Kayıtlı müşteri yok." : string.Join(", ", names);
}



#endregion




class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Order> Orders { get; set; } = [];
}

class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Product { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime OrderDate { get; set; }
}

class AppDbContext : DbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        var projectRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
        options.UseSqlite($"Data Source={Path.Combine(projectRoot, "orders.db")}");
    }
}
