using System.Text;
using System.Text.Json;

var gatewayUrl = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://gateway:8080";
var deviceCount = 100;
var random = new Random();
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, _) => cts.Cancel();

var tasks = Enumerable.Range(1, deviceCount).Select(deviceId => Task.Run(async () =>
{
    var rnd = new Random(deviceId);
    using var client = new HttpClient();
    while (!cts.Token.IsCancellationRequested)
    {
        var telemetry = new
        {
            deviceId,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            value = 20 + rnd.NextDouble() * 15
        };
        var json = JsonSerializer.Serialize(telemetry);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        try
        {
            await client.PostAsync($"{gatewayUrl}/api/telemetry", content, cts.Token);
        }
        catch { }
        await Task.Delay(rnd.Next(100, 1000), cts.Token);
    }
})).ToArray();

Console.WriteLine($"Generator started. Devices: {deviceCount}, Gateway: {gatewayUrl}");
await Task.WhenAll(tasks);