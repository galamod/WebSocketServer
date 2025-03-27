using System.Net.WebSockets;
using System.Text;
using Microsoft.EntityFrameworkCore;

// Создаём приложение
var builder = WebApplication.CreateBuilder(args);

string[] allowedOrigins =
{
    "https://galabot.netlify.app",
    "https://galasoft.netlify.app",
    "https://galaweb.netlify.app"
};

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowSpecificOrigins",
        policy =>
        {
            policy.WithOrigins(allowedOrigins)
                  .AllowAnyHeader()
                  .AllowAnyMethod();
        });
});

var dbUrl = builder.Configuration.GetConnectionString("DefaultConnection")
          ?? Environment.GetEnvironmentVariable("DATABASE_URL");

Console.WriteLine($"DATABASE_URL from environment: {dbUrl}");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dbUrl));

var app = builder.Build();
app.UseCors("AllowSpecificOrigins");
app.UseHttpsRedirection();
app.UseWebSockets();

builder.Services.AddEndpointsApiExplorer();

// Список подключённых клиентов
var clients = new List<WebSocket>();

app.Use(async (context, next) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        clients.Add(webSocket);
        await HandleWebSocketAsync(webSocket, app.Services);
        clients.Remove(webSocket);
    }
    else
    {
        await next();
    }
});

app.Run();

async Task HandleWebSocketAsync(WebSocket webSocket, IServiceProvider services)
{
    var buffer = new byte[1024 * 4];
    var dbContext = services.GetRequiredService<AppDbContext>();

    // Периодически пингуем клиента
    var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(30));

    async Task PingClient()
    {
        while (await pingTimer.WaitForNextTickAsync())
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else
            {
                return;
            }
        }
    }

    _ = PingClient(); // Запускаем пинг в фоне

    while (webSocket.State == WebSocketState.Open)
    {
        var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client", CancellationToken.None);
        }
        else
        {
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"Received: {message}");

            if (message.StartsWith("CHECK_KEY:"))
            {
                var key = message.Replace("CHECK_KEY:", "").Trim();
                var license = await dbContext.Licenses.FirstOrDefaultAsync(l => l.Key == key);

                string response;
                if (license == null)
                {
                    response = "INVALID_KEY";
                }
                else if (license.ExpiresAt < DateTime.UtcNow)
                {
                    response = "EXPIRED_KEY";
                }
                else
                {
                    response = "VALID_KEY";
                }

                await webSocket.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
    }
}

// БД-модель
public class License
{
    public int Id { get; set; }
    public string Key { get; set; }
    public DateTime ExpiresAt { get; set; }
}

// Контекст базы данных
public class AppDbContext : DbContext
{
    public DbSet<License> Licenses { get; set; }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
}
