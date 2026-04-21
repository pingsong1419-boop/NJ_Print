using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ScanModule;

public static class BarcodeScannerModuleExtensions
{
    public static IServiceCollection AddBarcodeScannerModule(this IServiceCollection services)
    {
        services.AddSingleton<BarcodeScannerRuntime>();
        return services;
    }

    public static IEndpointRouteBuilder MapBarcodeScannerModule(this IEndpointRouteBuilder endpoints)
    {
        var runtime = endpoints.ServiceProvider.GetRequiredService<BarcodeScannerRuntime>();

        var appLifetime = endpoints.ServiceProvider.GetService<IHostApplicationLifetime>();
        appLifetime?.ApplicationStopping.Register(() => runtime.Stop());

        var group = endpoints.MapGroup("/barcodeScanner");

        group.MapPost("/start", (BarcodeScannerStartRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.ScannerIp) || req.ScannerPort <= 0)
            {
                return Results.BadRequest(new { running = false, connected = false, message = "扫码枪IP或端口无效" });
            }

            var result = runtime.Start(req);
            return Results.Ok(result);
        });

        group.MapPost("/stop", () => Results.Ok(runtime.Stop()));
        group.MapGet("/status", () => Results.Ok(runtime.GetStatus()));
        group.MapGet("/pull", (long afterId) => Results.Ok(runtime.Pull(afterId)));

        return endpoints;
    }
}

public record BarcodeScannerStartRequest(string ScannerIp, int ScannerPort, string BarcodeRegex);
public record BarcodeScanEvent(long Id, string Code, string Time);

public class BarcodeScannerRuntime
{
    private const int WelcomeBytesLength = 100;
    private const int BarcodeReadBufferSize = 256;
    private const int BarcodeReadIdleWindowMs = 120;
    private const int BarcodeReadIdleStepMs = 40;
    private const int IoLogIntervalMs = 60_000;
    private readonly object _sync = new();
    private readonly object _ioLogSync = new();
    private readonly ConcurrentQueue<BarcodeScanEvent> _events = new();
    private readonly ConcurrentQueue<string> _ioLogs = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private BarcodeScannerStartRequest? _config;
    private long _lastId;
    private long _lastIoLogAtMs;
    private int _ioLogSkippedCount;
    private bool _running;
    private bool _connected;
    private string _lastError = string.Empty;

    public object Start(BarcodeScannerStartRequest cfg)
    {
        lock (_sync)
        {
            StopInternal();
            ClearScannerBuffersUnsafe();
            _config = cfg;
            _cts = new CancellationTokenSource();
            _running = true;
            _connected = false;
            _lastError = string.Empty;
            _worker = Task.Run(() => RunLoop(_cts.Token));
            return GetStatusUnsafe();
        }
    }

    public object Stop()
    {
        lock (_sync)
        {
            StopInternal();
            return GetStatusUnsafe();
        }
    }

    public object GetStatus()
    {
        lock (_sync)
        {
            return GetStatusUnsafe();
        }
    }

    public object Pull(long afterId)
    {
        BarcodeScanEvent[] all;
        bool running;
        bool connected;
        string scannerIp;
        int scannerPort;
        string barcodeRegex;
        string lastError;
        lock (_sync)
        {
            all = _events.ToArray();
            running = _running;
            connected = _connected;
            scannerIp = _config?.ScannerIp ?? string.Empty;
            scannerPort = _config?.ScannerPort ?? 0;
            barcodeRegex = _config?.BarcodeRegex ?? string.Empty;
            lastError = _lastError;
        }

        var events = all.Where(x => x.Id > afterId)
                        .OrderBy(x => x.Id)
                        .Take(100)
                        .ToList();

        TrimQueueIfNeeded();

        return new
        {
            running,
            connected,
            scannerIp,
            scannerPort,
            barcodeRegex,
            lastError,
            ioLogs = _ioLogs.ToArray().TakeLast(30).ToArray(),
            events
        };
    }

    private void StopInternal()
    {
        try { _cts?.Cancel(); } catch { }
        _running = false;
        _connected = false;
    }

    private void ClearScannerBuffersUnsafe()
    {
        while (_events.TryDequeue(out _))
        {
        }

        while (_ioLogs.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _lastId, 0);

        lock (_ioLogSync)
        {
            _lastIoLogAtMs = 0;
            _ioLogSkippedCount = 0;
        }
    }

    private object GetStatusUnsafe()
    {
        return new
        {
            running = _running,
            connected = _connected,
            scannerIp = _config?.ScannerIp ?? string.Empty,
            scannerPort = _config?.ScannerPort ?? 0,
            barcodeRegex = _config?.BarcodeRegex ?? string.Empty,
            lastError = _lastError,
            ioLogs = _ioLogs.ToArray().TakeLast(30).ToArray()
        };
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            BarcodeScannerStartRequest? cfg;
            lock (_sync)
            {
                cfg = _config;
            }

            if (cfg is null)
            {
                await Task.Delay(1000, ct);
                continue;
            }

            try
            {
                using var client = new TcpClient();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(5000);
                await client.ConnectAsync(cfg.ScannerIp, cfg.ScannerPort, connectCts.Token);
                LogIo($"TX CONNECT {cfg.ScannerIp}:{cfg.ScannerPort}");

                using var stream = client.GetStream();
                var welcomeBytes = await ReadWelcomeHandshakeAsync(stream, ct);
                if (welcomeBytes.Length > 0)
                {
                    var welcomeText = NormalizeBarcode(Encoding.ASCII.GetString(welcomeBytes));
                    LogIo($"RX WELCOME({welcomeBytes.Length}): {welcomeText}");
                    if (IsScannerBusyMessage(welcomeText))
                    {
                        lock (_sync)
                        {
                            _connected = false;
                            _lastError = $"扫码枪通道占用: {welcomeText}";
                        }

                        LogIo("RX ERROR: 扫码枪通道被占用，1秒后重试");
                        await Task.Delay(1000, ct);
                        continue;
                    }
                }
                else
                {
                    LogIo("RX WELCOME(0): 未读取到欢迎语，继续等待条码");
                }

                lock (_sync)
                {
                    _connected = true;
                    _lastError = string.Empty;
                }

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var codeBytes = await ReadBarcodePayloadAsync(stream, ct);
                        var raw = Encoding.ASCII.GetString(codeBytes);
                        var code = NormalizeBarcode(raw);
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        if (IsScannerSystemMessage(code)) continue;

                        var id = Interlocked.Increment(ref _lastId);
                        _events.Enqueue(new BarcodeScanEvent(id, code, DateTime.Now.ToString("HH:mm:ss")));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!ct.IsCancellationRequested)
                {
                    lock (_sync)
                    {
                        _lastError = "扫码枪连接超时";
                    }

                    LogIo("RX ERROR: 扫码枪连接超时");
                    try
                    {
                        await Task.Delay(1000, ct);
                    }
                    catch
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_sync)
                {
                    _lastError = $"扫码枪连接异常: {ex.Message}";
                }

                LogIo($"RX ERROR: {ex.Message}");

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch
                {
                }
            }
            finally
            {
                lock (_sync)
                {
                    _connected = false;
                }
            }
        }
    }

    private void TrimQueueIfNeeded()
    {
        if (_events.Count <= 2000) return;

        while (_events.Count > 1500 && _events.TryDequeue(out _))
        {
        }
    }

    private static string NormalizeBarcode(string raw)
    {
        var cleaned = new string(raw.Where(c => !char.IsControl(c)).ToArray());
        return cleaned.Trim();
    }

    private static bool IsScannerSystemMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();
        if (t.StartsWith("Welcome to Socket Channel", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.StartsWith("Connected", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool IsScannerBusyMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Contains("already in use", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("no slot is available", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Contains("unable to complete connect", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static async Task<byte[]> ReadBarcodePayloadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[BarcodeReadBufferSize];
        var firstRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (firstRead == 0)
        {
            throw new IOException("扫码枪连接已断开（读取条码时返回0字节）");
        }

        if (stream is not NetworkStream networkStream)
        {
            return buffer.AsSpan(0, firstRead).ToArray();
        }

        using var ms = new MemoryStream();
        ms.Write(buffer, 0, firstRead);

        var idleElapsedMs = 0;
        while (!ct.IsCancellationRequested)
        {
            if (networkStream.DataAvailable)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, read);
                idleElapsedMs = 0;
                continue;
            }

            await Task.Delay(BarcodeReadIdleStepMs, ct);
            idleElapsedMs += BarcodeReadIdleStepMs;
            if (idleElapsedMs >= BarcodeReadIdleWindowMs)
            {
                break;
            }
        }

        return ms.ToArray();
    }

    private static async Task<byte[]> ReadWelcomeHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[WelcomeBytesLength];
        var offset = 0;

        using var firstWaitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstWaitCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var firstRead = await stream.ReadAsync(buffer.AsMemory(0, WelcomeBytesLength), firstWaitCts.Token);
            if (firstRead <= 0) return Array.Empty<byte>();
            offset += firstRead;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte>();
        }

        while (offset < WelcomeBytesLength)
        {
            if (stream is not NetworkStream ns || !ns.DataAvailable)
            {
                await Task.Delay(120, ct);
                if (stream is not NetworkStream ns2 || !ns2.DataAvailable) break;
            }

            var read = await stream.ReadAsync(buffer.AsMemory(offset, WelcomeBytesLength - offset), ct);
            if (read <= 0) break;
            offset += read;
        }

        return buffer.AsSpan(0, offset).ToArray();
    }

    private void LogIo(string message)
    {
        var nowMs = Environment.TickCount64;
        var skippedCount = 0;
        var shouldWrite = false;

        lock (_ioLogSync)
        {
            if (nowMs - _lastIoLogAtMs >= IoLogIntervalMs)
            {
                shouldWrite = true;
                _lastIoLogAtMs = nowMs;
                skippedCount = _ioLogSkippedCount;
                _ioLogSkippedCount = 0;
            }
            else
            {
                _ioLogSkippedCount++;
            }
        }

        if (!shouldWrite) return;

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [扫码枪通讯] {message}";
        if (skippedCount > 0)
        {
            line += $" | Skipped={skippedCount}";
        }

        _ioLogs.Enqueue(line);
        while (_ioLogs.Count > 300 && _ioLogs.TryDequeue(out _))
        {
        }

        Console.WriteLine(line);
    }
}
