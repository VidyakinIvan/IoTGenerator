using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole();

var gatewayUrl = Environment.GetEnvironmentVariable("GATEWAY_URL") ?? "http://iot-gateway:8080";
var mode = Environment.GetEnvironmentVariable("LOAD_MODE") ?? "static";
var deviceCount = int.Parse(Environment.GetEnvironmentVariable("DEVICE_COUNT") ?? "100");
var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, _) => cts.Cancel();

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

logger.LogInformation("Generator started. Gateway: {GatewayUrl}, Mode: {Mode}, Devices: {DeviceCount}", gatewayUrl, mode, deviceCount);

using var client = new HttpClient();

if (mode == "static")
{
    await RunStaticMode(client, gatewayUrl, deviceCount, logger, cts.Token);
}
else if (mode == "peak")
{
    await RunPeakMode(client, gatewayUrl, logger, cts.Token);
}
else if (mode == "step")
{
    await RunStepMode(client, gatewayUrl, logger, cts.Token);
}
else if (mode == "warmup")
{
    var durationSec = int.Parse(Environment.GetEnvironmentVariable("WARMUP_DURATION_SEC") ?? "120");
    var targetRps = int.Parse(Environment.GetEnvironmentVariable("WARMUP_TARGET_RPS") ?? "10000");
    await RunWarmupMode(client, gatewayUrl, targetRps, durationSec, logger, cts.Token);
}
else
{
    logger.LogError("Unknown mode: {Mode}", mode);
}

async Task RunStaticMode(HttpClient client, string url, int devices, ILogger logger, CancellationToken token)
{
    var random = new Random();
    var tasks = Enumerable.Range(1, devices).Select(deviceId => Task.Run(async () =>
    {
        var rnd = new Random(deviceId);
        while (!token.IsCancellationRequested)
        {
            var telemetry = GenerateTelemetry(deviceId, rnd);
            var success = await SendTelemetry(client, url, telemetry, logger);
            if (success)
            {
                logger.LogDebug("Device {DeviceId}: sent value {Value}", telemetry.deviceId, telemetry.value);
            }
            await Task.Delay(rnd.Next(100, 1000), token);
        }
    }, token)).ToArray();

    await Task.WhenAll(tasks);
}

async Task RunPeakMode(HttpClient client, string url, ILogger logger, CancellationToken token)
{
    var pauseSeconds = int.Parse(Environment.GetEnvironmentVariable("PEAK_PAUSE_SEC") ?? "60");
    var burstSize = int.Parse(Environment.GetEnvironmentVariable("PEAK_BURST_SIZE") ?? "100");
    var cycles = int.Parse(Environment.GetEnvironmentVariable("PEAK_CYCLES") ?? "5");
    var random = new Random();

    for (int cycle = 0; cycle < cycles && !token.IsCancellationRequested; cycle++)
    {
        logger.LogInformation("Peak cycle {Cycle}/{Cycles}: accumulating {BurstSize} messages", cycle + 1, cycles, burstSize);

        var buffer = new List<TelemetryData>();
        for (int i = 0; i < burstSize; i++)
        {
            var deviceId = random.Next(1, 11);
            var telemetry = GenerateTelemetry(deviceId, random);
            buffer.Add(telemetry);
            await Task.Delay(10, token);
        }

        logger.LogInformation("Peak cycle {Cycle}: pause for {Pause} seconds", cycle + 1, pauseSeconds);
        await Task.Delay(pauseSeconds * 1000, token);

        logger.LogInformation("Peak cycle {Cycle}: sending {Count} messages in burst", cycle + 1, buffer.Count);
        foreach (var telemetry in buffer)
        {
            await SendTelemetry(client, url, telemetry, logger);
        }

        if (cycle < cycles - 1)
        {
            await Task.Delay(5000, token);
        }
    }
}

async Task RunStepMode(HttpClient client, string url, ILogger logger, CancellationToken token)
{
    int[] steps = { 100, 500, 1000, 5000, 10000 };
    var stepDuration = int.Parse(Environment.GetEnvironmentVariable("STEP_DURATION_SEC") ?? "120");

    foreach (var step in steps)
    {
        if (token.IsCancellationRequested) break;

        logger.LogInformation("Step mode: scaling to {DeviceCount} devices", step);

        var ctsStep = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = Enumerable.Range(1, step).Select(deviceId => Task.Run(async () =>
        {
            var rnd = new Random(deviceId);
            while (!ctsStep.Token.IsCancellationRequested)
            {
                var telemetry = GenerateTelemetry(deviceId, rnd);
                await SendTelemetry(client, url, telemetry, logger);
                await Task.Delay(rnd.Next(100, 1000), ctsStep.Token);
            }
        }, ctsStep.Token)).ToArray();

        logger.LogInformation("Step mode: running {DeviceCount} devices for {Duration} seconds", step, stepDuration);
        await Task.Delay(stepDuration * 1000, token);

        ctsStep.Cancel();
        await Task.WhenAll(tasks);
        logger.LogInformation("Step mode: completed {DeviceCount} devices", step);

        await Task.Delay(5000, token);
    }
}
async Task RunWarmupMode(HttpClient client, string url, int targetRps, int durationSec, ILogger logger, CancellationToken token)
{
    var intervalMs = 1000.0 / targetRps;
    var random = new Random();
    var startTime = DateTime.UtcNow;
    var requestCount = 0;

    logger.LogInformation("Warmup mode started: target {TargetRps} RPS for {Duration} seconds", targetRps, durationSec);

    while ((DateTime.UtcNow - startTime).TotalSeconds < durationSec && !token.IsCancellationRequested)
    {
        var deviceId = random.Next(1, 11);
        var telemetry = GenerateTelemetry(deviceId, random);
        await SendTelemetry(client, url, telemetry, logger);
        requestCount++;

        var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
        var expectedRequests = targetRps * elapsed / 1000.0;
        if (expectedRequests > requestCount)
        {
            var delay = (expectedRequests - requestCount) * intervalMs;
            if (delay > 0 && delay < 1000)
            {
                await Task.Delay((int)delay, token);
            }
        }

        if (requestCount % 1000 == 0)
        {
            logger.LogInformation("Warmup: {RequestCount} requests sent, current RPS: {CurrentRps:F0}",
                requestCount, requestCount / ((DateTime.UtcNow - startTime).TotalSeconds));
        }
    }

    logger.LogInformation("Warmup mode completed: {RequestCount} total requests in {Duration:F0} seconds",
        requestCount, (DateTime.UtcNow - startTime).TotalSeconds);
}


TelemetryData GenerateTelemetry(int deviceId, Random rnd)
{
    var hourOfDay = DateTime.UtcNow.Hour;
    var baseTemp = 20 + Math.Sin(hourOfDay / 24.0 * 2 * Math.PI) * 5;
    var noise = (rnd.NextDouble() - 0.5) * 4; // -2..+2
    var temperature = baseTemp + noise;

    if (rnd.Next(100) == 0)
    {
        temperature += 15;
        Console.WriteLine($"Emergency spike from device {deviceId}: {temperature:F1}°C");
    }

    temperature = Math.Round(Math.Clamp(temperature, -50, 150), 1);

    return new TelemetryData
    {
        deviceId = deviceId,
        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        value = temperature
    };
}

async Task<bool> SendTelemetry(HttpClient client, string url, TelemetryData telemetry, ILogger logger)
{
    var json = JsonSerializer.Serialize(telemetry);
    var content = new StringContent(json, Encoding.UTF8, "application/json");

    try
    {
        var response = await client.PostAsync($"{url}/api/telemetry", content);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }
        else
        {
            logger.LogWarning("Error from gateway: {StatusCode}", response.StatusCode);
            return false;
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to send telemetry");
        return false;
    }
}

public class TelemetryData
{
    public int deviceId { get; set; }
    public long timestamp { get; set; }
    public double value { get; set; }
}