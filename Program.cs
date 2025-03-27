using System.Collections.Concurrent;
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

builder.Services.AddEndpointsApiExplorer();

builder.Services.AddDbContext<LicenseDbContext>(options =>
    options.UseNpgsql(dbUrl));

var app = builder.Build();
app.UseCors("AllowSpecificOrigins");
app.UseHttpsRedirection();
app.UseWebSockets();

// Список подключённых клиентов
var clients = new ConcurrentDictionary<WebSocket, bool>();

app.Use(async (context, next) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        clients.TryAdd(webSocket, true); // Добавляем WebSocket в список

        try
        {
            await HandleWebSocketAsync(webSocket, app.Services);
        }
        finally
        {
            clients.TryRemove(webSocket, out _); // Удаляем при отключении
        }
    }
    else
    {
        await next();
    }
});

// ✅ Keep-alive эндпоинт
app.MapGet("/api/ping", () => Results.Ok("Server is alive"));

// ✅ Получить все ключи
app.MapGet("/api/licenses", async (LicenseDbContext db) =>
    await db.LicenseKeys.ToListAsync());

// ✅ Проверка ключа
app.MapGet("/api/licenses/check/{key}", async (string key, LicenseDbContext db) =>
{
    var license = await db.LicenseKeys.FirstOrDefaultAsync(l => l.Key == key);
    if (license == null)
        return Results.NotFound("Ключ не найден.");

    if (license.IsUnlimited || license.ExpirationDate > DateTime.UtcNow)
        return Results.Ok(license);

    return Results.BadRequest("Ключ истёк.");
});

// ✅ Добавление ключа
app.MapPost("/api/licenses", async (LicenseKey license, LicenseDbContext db) =>
{
    db.LicenseKeys.Add(license);
    await db.SaveChangesAsync();
    return Results.Created($"/api/licenses/{license.Id}", license);
});

// ✅ Обновление ключа (изменение данных)
app.MapPut("/api/licenses/{id}", async (int id, LicenseKey updatedLicense, LicenseDbContext db) =>
{
    var license = await db.LicenseKeys.FindAsync(id);
    if (license == null)
        return Results.NotFound("Ключ не найден.");

    // Обновляем данные
    license.Key = updatedLicense.Key;
    license.AppName = updatedLicense.AppName;
    license.ExpirationDate = updatedLicense.ExpirationDate;
    license.IsUnlimited = updatedLicense.IsUnlimited;

    await db.SaveChangesAsync();
    return Results.Ok(license);
});

// ✅ Удаление ключа
app.MapDelete("/api/licenses/{id}", async (int id, LicenseDbContext db) =>
{
    var license = await db.LicenseKeys.FindAsync(id);
    if (license == null)
        return Results.NotFound();

    db.LicenseKeys.Remove(license);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
    db.Database.Migrate();
}

app.Run();

async Task HandleWebSocketAsync(WebSocket webSocket, IServiceProvider services)
{
    var buffer = new byte[1024 * 4];
    var dbContext = services.GetRequiredService<LicenseDbContext>();

    var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(30)); // Пинг каждые 30 секунд
    var cts = new CancellationTokenSource(); // Контроллер отмены
    var token = cts.Token;
    var pongReceived = true;

    clients.TryAdd(webSocket, true); // Добавляем клиента

    async Task PingClient()
    {
        while (await pingTimer.WaitForNextTickAsync(token))
        {
            if (webSocket.State == WebSocketState.Open)
            {
                if (!pongReceived)
                {
                    Console.WriteLine("PONG не получен. Закрываем соединение.");
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "PONG not received", CancellationToken.None);
                    clients.TryRemove(webSocket, out _);
                    return;
                }

                pongReceived = false;
                await webSocket.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            else
            {
                return;
            }
        }
    }

    var pingTask = PingClient(); // Запускаем пинг

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine("Клиент сам закрыл соединение.");
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Console.WriteLine($"Received: {message}");

            if (message == "pong")
            {
                pongReceived = true;
                continue;
            }

            if (message == "QUIT")
            {
                Console.WriteLine("Клиент отключился по команде QUIT.");
                break;
            }

            if (message.StartsWith("CHECK_KEY:"))
            {
                var data = message.Replace("CHECK_KEY:", "").Trim().Split(",");
                if (data.Length != 2) continue;

                string appName = data[0].Trim();
                string key = data[1].Trim();

                var license = await dbContext.LicenseKeys
                    .FirstOrDefaultAsync(l => l.Key == key && l.AppName == appName);

                string response;
                if (license == null)
                {
                    response = "INVALID_KEY";
                }
                else if (!license.IsUnlimited && license.ExpirationDate < DateTime.UtcNow)
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
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка WebSocket: {ex.Message}");
    }
    finally
    {
        clients.TryRemove(webSocket, out _);
        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Connection closed", CancellationToken.None);
        cts.Cancel();
    }
}
