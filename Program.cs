using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();

var gatewayUrl = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:8080";
var random = new Random();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, _) => cts.Cancel();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

using var client = new HttpClient();  // ЭТО БЫЛО ПРОПУЩЕНО

logger.LogInformation("Generator started. Gateway: {GatewayUrl}", gatewayUrl);

while (!cts.Token.IsCancellationRequested)
{
    var telemetry = new
    {
        deviceId = random.Next(1, 11),
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        value = Math.Round(20 + random.NextDouble() * 15, 2)
    };

    var json = JsonSerializer.Serialize(telemetry);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    try
    {
        var response = await client.PostAsync($"{gatewayUrl}/api/telemetry", content, cts.Token);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation("Sent: device={DeviceId}, ts={Timestamp}, value={Value}", telemetry.deviceId,
                 telemetry.timestamp, telemetry.value);
        }
        else
        {
            logger.LogWarning("Error from gateway: {StatusCode}", response.StatusCode);
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send telemetry");
    }

    await Task.Delay(random.Next(100, 1000), cts.Token);
}