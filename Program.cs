using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Подключаем WebSockets
app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await HandleWebSocketAsync(webSocket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.Run();

async Task HandleWebSocketAsync(WebSocket webSocket)
{
    var buffer = new byte[1024 * 4];

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

            var response = $"Echo: {message}";
            var responseBytes = Encoding.UTF8.GetBytes(response);

            await webSocket.SendAsync(new ArraySegment<byte>(responseBytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
    }
}
