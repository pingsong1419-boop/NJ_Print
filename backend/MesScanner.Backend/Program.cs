using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ScanModule;

public record PrintedHistoryRecord(string Code, string Type, DateTime PrintedAt);

public static class PrintedHistoryStorage
{
    private static readonly string HistoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "printed_history.json");
    private static readonly object _lock = new();

    public static bool Exists(string code)
    {
        if (!File.Exists(HistoryPath)) return false;
        lock (_lock)
        {
            var json = File.ReadAllText(HistoryPath);
            var history = JsonSerializer.Deserialize<List<PrintedHistoryRecord>>(json) ?? new();
            return history.Any(h => h.Code == code);
        }
    }

    public static void Save(string code, string type)
    {
        lock (_lock)
        {
            var history = new List<PrintedHistoryRecord>();
            if (File.Exists(HistoryPath))
            {
                var json = File.ReadAllText(HistoryPath);
                history = JsonSerializer.Deserialize<List<PrintedHistoryRecord>>(json) ?? new();
            }
            
            if (!history.Any(h => h.Code == code))
            {
                history.Add(new PrintedHistoryRecord(code, type, DateTime.Now));
                var dir = Path.GetDirectoryName(HistoryPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }
}

var builder = WebApplication.CreateBuilder(args);
var fileJsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true)
              .AllowCredentials();
    });
});

builder.Services.AddBarcodeScannerModule();

var app = builder.Build();
var interactionLogThrottler = new InteractionLogThrottler();

app.UseCors("AllowAll");
app.Use(async (context, next) =>
{
    var stopwatch = Stopwatch.StartNew();
    string requestBody = string.Empty;

    if (context.Request.ContentLength > 0 &&
        (HttpMethods.IsPost(context.Request.Method) ||
         HttpMethods.IsPut(context.Request.Method) ||
         HttpMethods.IsPatch(context.Request.Method)))
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        requestBody = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
    }

    await next();
    stopwatch.Stop();

    if (ShouldLogInteraction(context.Request.Path))
    {
        var key = $"{context.Request.Method}:{context.Request.Path}";
        var intervalMs = GetInteractionLogIntervalMs(context.Request.Path);
        if (interactionLogThrottler.ShouldWrite(key, intervalMs, out var skippedCount))
        {
            var bodyText = string.IsNullOrWhiteSpace(requestBody) ? "{}" : ClipForLog(requestBody, 600);
            var skippedText = skippedCount > 0 ? $" | Skipped={skippedCount}" : string.Empty;
            Console.WriteLine(
                $"[API交互] {context.Request.Method} {context.Request.Path}{context.Request.QueryString} | Status={context.Response.StatusCode} | Cost={stopwatch.ElapsedMilliseconds}ms{skippedText} | Body={bodyText}");
        }
    }
});

app.MapGet("/", () => "PACK Material Station Backend Running...");

app.MapPost("/saveLogs", async (LogSaveRequest req) =>
{
    try
    {
        var savePath = ResolveConfigDirectoryPath(req.Path, "Logs");
        if (!Directory.Exists(savePath))
        {
            Directory.CreateDirectory(savePath);
        }

        var fileName = string.IsNullOrWhiteSpace(req.FileName)
            ? $"scan_log_{DateTime.Now:yyyyMMdd_HHmmss}.log"
            : req.FileName;
        var fullPath = Path.Combine(savePath, fileName);
        await File.WriteAllTextAsync(fullPath, req.Content);
        return Results.Ok(new { message = "Save success", path = fullPath });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/pathPicker/select", (PathPickerRequest req) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.Target))
        {
            return Results.BadRequest(new { success = false, message = "target is required" });
        }

        var selectedPath = SelectPathByTarget(req.Target.Trim());
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return Results.Ok(new { success = false, cancelled = true, path = "" });
        }

        return Results.Ok(new { success = true, cancelled = false, path = selectedPath });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, cancelled = false, path = "", message = $"路径选择失败: {ex.Message}" });
    }
});

app.MapGet("/appConfig", async () =>
{
    try
    {
        var configFile = GetAppConfigFilePath();
        if (!File.Exists(configFile))
        {
            return Results.Json(new { exists = false });
        }

        var content = await File.ReadAllTextAsync(configFile);
        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.Json(new { exists = false });
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement.Clone();
        return Results.Json(root);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/appConfig", async (JsonElement req) =>
{
    try
    {
        var configFile = GetAppConfigFilePath();
        var directory = Path.GetDirectoryName(configFile)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = req.GetRawText();
        await File.WriteAllTextAsync(configFile, json);
        return Results.Ok(new { message = "App config saved", path = configFile });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/printers", () =>
{
    try
    {
        var printers = GetInstalledPrinters();
        var defaultPrinter = printers.FirstOrDefault(x => x.IsDefault)?.Name ?? string.Empty;
        return Results.Ok(new
        {
            success = true,
            defaultPrinter,
            printers
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            success = false,
            defaultPrinter = string.Empty,
            printers = Array.Empty<object>(),
            message = $"读取打印机列表失败: {ex.Message}"
        });
    }
});

app.MapGet("/orderStatusSelection", async () =>
{
    try
    {
        var stateFile = GetOrderStatusStateFilePath();
        if (!File.Exists(stateFile))
        {
            return Results.Json(new { exists = false });
        }

        var content = await File.ReadAllTextAsync(stateFile);
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var selectedCode = ReadStringOrNumber(root, "selectedCode") ?? ReadStringOrNumber(root, "SelectedCode") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedCode))
        {
            return Results.Json(new { exists = false });
        }

        var orderStatus = ReadStringOrNumber(root, "orderStatus") ?? ReadStringOrNumber(root, "OrderStatus") ?? string.Empty;
        if (orderStatus == "2")
        {
            orderStatus = "下发中";
        }
        if (string.IsNullOrWhiteSpace(orderStatus))
        {
            orderStatus = "下发中";
        }

        var updatedAt = ReadStringOrNumber(root, "updatedAt") ?? ReadStringOrNumber(root, "UpdatedAt") ?? string.Empty;

        return Results.Json(new
        {
            exists = true,
            selectedCode,
            orderStatus,
            updatedAt
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/orderStatusSelection", async (OrderStatusSelectionState req) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.SelectedCode))
        {
            return Results.BadRequest(new { message = "selectedCode is required" });
        }

        var payload = req with
        {
            OrderStatus = string.IsNullOrWhiteSpace(req.OrderStatus) ? "下发中" : req.OrderStatus,
            UpdatedAt = string.IsNullOrWhiteSpace(req.UpdatedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : req.UpdatedAt
        };

        var stateFile = GetOrderStatusStateFilePath();
        var directory = Path.GetDirectoryName(stateFile)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(payload, fileJsonOptions);
        await File.WriteAllTextAsync(stateFile, json);

        return Results.Ok(new { message = "Order status selection saved", path = stateFile });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapBarcodeScannerModule();

app.MapPost("/printLabelsByBarTender", async (PrintByBarTenderRequest req) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(req.BarTenderExePath) ||
            string.IsNullOrWhiteSpace(req.TemplatePath))
        {
            return Results.BadRequest(new { success = false, message = "BarTender配置不完整，请检查EXE/模板路径" });
        }

        if (!File.Exists(req.BarTenderExePath))
        {
            return Results.BadRequest(new { success = false, message = $"BarTender 程序不存在: {req.BarTenderExePath}" });
        }

        if (!File.Exists(req.TemplatePath))
        {
            return Results.BadRequest(new { success = false, message = $"模板文件不存在: {req.TemplatePath}" });
        }

        var validLabels = (req.Labels ?? new List<PrintLabelItem>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .ToList();

        string? dataFilePath = null;
        string barTenderArgs;
        if (validLabels.Count > 0)
        {
            var databasePath = ResolveConfigFilePath(req.DatabasePath, "pack_labels.csv");
            var dataDir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(dataDir) && !Directory.Exists(dataDir))
            {
                Directory.CreateDirectory(dataDir);
            }

            string content;
            if (databasePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                // 如果是 .txt 后缀，仅写入纯条码（不含表头），多条码换行分隔
                content = string.Join(Environment.NewLine, validLabels.Select(x => x.Code));
            }
            else
            {
                // 默认 CSV 格式（包含表头）
                var lines = new List<string> { "Code,CodeType,CodeTypeName" };
                lines.AddRange(validLabels.Select(x => $"{EscapeCsv(x.Code)},{EscapeCsv(x.Type)},{EscapeCsv(x.TypeName)}"));
                content = string.Join(Environment.NewLine, lines);
            }

            await File.WriteAllTextAsync(databasePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            dataFilePath = databasePath;
            barTenderArgs = $"/F=\"{req.TemplatePath}\" /D=\"{databasePath}\" /P /X";
        }
        else
        {
            // 兼容“仅通过模板内固定文本文件驱动”的打印模式（无需数据库CSV）
            barTenderArgs = $"/F=\"{req.TemplatePath}\" /P /X";
        }

        var printerName = string.IsNullOrWhiteSpace(req.PrinterName)
            ? GetDefaultPrinterName()
            : req.PrinterName.Trim();
        if (!string.IsNullOrWhiteSpace(printerName))
        {
            barTenderArgs = $"{barTenderArgs} /PRN=\"{printerName}\"";
        }

        var psi = new ProcessStartInfo
        {
            FileName = req.BarTenderExePath,
            Arguments = barTenderArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(req.BarTenderExePath) ?? AppContext.BaseDirectory
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return Results.Ok(new
            {
                success = false,
                message = "BarTender 进程启动失败",
                command = $"{psi.FileName} {psi.Arguments}",
                dataFilePath,
                printerUsed = printerName
            });
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            return Results.Ok(new
            {
                success = false,
                message = "BarTender 执行超时(120s)",
                command = $"{psi.FileName} {psi.Arguments}",
                dataFilePath,
                printerUsed = printerName
            });
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        if (process.ExitCode != 0)
        {
            var rawError = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            var knownError = TryMapBarTenderKnownError(rawError, req.TemplatePath);
            return Results.Ok(new
            {
                success = false,
                message = knownError ?? (string.IsNullOrWhiteSpace(rawError) ? "BarTender 执行失败" : rawError.Trim()),
                exitCode = process.ExitCode,
                command = $"{psi.FileName} {psi.Arguments}",
                output = stdout,
                rawError = string.IsNullOrWhiteSpace(rawError) ? string.Empty : ClipForLog(rawError, 800),
                dataFilePath,
                printerUsed = printerName
            });
        }

        try
        {
            // 成功执行后，将条码保存到历史记录中
            foreach (var label in validLabels)
            {
                PrintedHistoryStorage.Save(label.Code, label.Type);
            }

            return Results.Ok(new
            {
                success = true,
                message = "BarTender 打印命令已执行（已提交到打印队列）",
                exitCode = process.ExitCode,
                command = $"{psi.FileName} {psi.Arguments}",
                output = stdout,
                dataFilePath,
                printerUsed = printerName
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { success = false, message = $"打印成功但记录保存失败: {ex.Message}" });
        }
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, message = $"系统处理打印请求时发生异常: {ex.Message}" });
    }
});

app.MapGet("/api/PrintedHistory/Check", (string code) =>
{
    var exists = PrintedHistoryStorage.Exists(code);
    return Results.Ok(new { exists });
});

app.Run();

static string GetOrderStatusStateFilePath()
{
    return Path.Combine(GetConfigDirectoryPath(), "order_status_selection.json");
}

static string GetAppConfigFilePath()
{
    return Path.Combine(GetConfigDirectoryPath(), "app_config.json");
}

static string GetConfigDirectoryPath()
{
    var projectRoot = ResolveProjectRootPath();
    var configPath = Path.Combine(projectRoot, "Config");
    Directory.CreateDirectory(configPath);
    return configPath;
}

static string SelectPathByTarget(string target)
{
    var normalized = string.IsNullOrWhiteSpace(target)
        ? string.Empty
        : target.Trim().ToLowerInvariant();

    return normalized switch
    {
        "bartenderexe" or "bartender_exe" or "bartender-exe" =>
            ExecutePowerShellPathDialog(BuildOpenFileDialogScript(
                "选择 BarTender EXE 文件",
                "应用程序 (*.exe)|*.exe|所有文件 (*.*)|*.*")),

        "template" or "templatepath" or "template_path" or "template-btw" =>
            ExecutePowerShellPathDialog(BuildOpenFileDialogScript(
                "选择 BarTender 模板文件",
                "BarTender 模板 (*.btw)|*.btw|所有文件 (*.*)|*.*")),

        "database" or "databasepath" or "database_path" =>
            ExecutePowerShellPathDialog(BuildSaveFileDialogScript(
                "选择数据库文件路径",
                "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "pack_labels.csv")),

        "qrfile" or "qrfilepath" or "qr_file" or "qr_file_path" =>
            ExecutePowerShellPathDialog(BuildSaveFileDialogScript(
                "选择二维码文件路径",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "二维码.txt")),

        "barfile" or "barfilepath" or "bar_file" or "bar_file_path" =>
            ExecutePowerShellPathDialog(BuildSaveFileDialogScript(
                "选择一维码文件路径",
                "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                "一维码.txt")),

        "folder" or "directory" or "logpath" or "logsavepath" =>
            ExecutePowerShellPathDialog(BuildFolderDialogScript("选择文件夹")),

        _ => ExecutePowerShellPathDialog(BuildOpenFileDialogScript(
            "选择路径",
            "所有文件 (*.*)|*.*"))
    };
}

static string ResolveProjectRootPath()
{
    var probePaths = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
    foreach (var path in probePaths)
    {
        var dir = new DirectoryInfo(path);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "package.json")) &&
                Directory.Exists(Path.Combine(dir.FullName, "backend")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
    }

    return Directory.GetCurrentDirectory();
}

static List<PrinterInfo> GetInstalledPrinters()
{
    var script =
        "$ErrorActionPreference = 'Stop'\n" +
        "$list = Get-CimInstance Win32_Printer | Select-Object " +
        "@{n='Name';e={$_.Name}}," +
        "@{n='Default';e={[bool]$_.Default}}," +
        "@{n='WorkOffline';e={[bool]$_.WorkOffline}}," +
        "@{n='PrinterStatus';e={$_.PrinterStatus}}\n" +
        "$list | ConvertTo-Json -Compress";

    var json = ExecutePowerShellScript(script);
    if (string.IsNullOrWhiteSpace(json))
    {
        return new List<PrinterInfo>();
    }

    using var doc = JsonDocument.Parse(json);
    var result = new List<PrinterInfo>();

    if (doc.RootElement.ValueKind == JsonValueKind.Array)
    {
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            result.Add(ParsePrinterInfo(element));
        }
        return result;
    }

    if (doc.RootElement.ValueKind == JsonValueKind.Object)
    {
        result.Add(ParsePrinterInfo(doc.RootElement));
    }

    return result;
}

static string GetDefaultPrinterName()
{
    return GetInstalledPrinters().FirstOrDefault(x => x.IsDefault)?.Name ?? string.Empty;
}

static PrinterInfo ParsePrinterInfo(JsonElement element)
{
    var name = element.TryGetProperty("Name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
    var isDefault = element.TryGetProperty("Default", out var defaultEl) && defaultEl.ValueKind == JsonValueKind.True;
    var workOffline = element.TryGetProperty("WorkOffline", out var offlineEl) && offlineEl.ValueKind == JsonValueKind.True;
    var printerStatus = string.Empty;
    if (element.TryGetProperty("PrinterStatus", out var statusEl))
    {
        if (statusEl.ValueKind == JsonValueKind.String) printerStatus = statusEl.GetString() ?? string.Empty;
        else if (statusEl.ValueKind == JsonValueKind.Number) printerStatus = statusEl.GetRawText();
    }

    return new PrinterInfo(name, isDefault, workOffline, printerStatus);
}

static string ExecutePowerShellScript(string script)
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var psi = new ProcessStartInfo
    {
        FileName = "powershell",
        Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process is null)
    {
        throw new InvalidOperationException("无法启动 PowerShell");
    }

    process.WaitForExit(60000);
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"PowerShell ExitCode={process.ExitCode}" : stderr.Trim());
    }

    return stdout.Trim();
}

static string ExecutePowerShellPathDialog(string script)
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var psi = new ProcessStartInfo
    {
        FileName = "powershell",
        Arguments = $"-NoProfile -STA -ExecutionPolicy Bypass -EncodedCommand {encoded}",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(psi);
    if (process is null)
    {
        throw new InvalidOperationException("无法启动 PowerShell 选择窗口");
    }

    process.WaitForExit(120000);
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();

    if (process.ExitCode != 0)
    {
        var errMsg = string.IsNullOrWhiteSpace(stderr) ? $"PowerShell ExitCode={process.ExitCode}" : stderr.Trim();
        throw new InvalidOperationException(errMsg);
    }

    var lines = stdout
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.Trim())
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .ToArray();
    if (lines.Length == 0) return string.Empty;
    return lines[^1];
}

static string BuildOpenFileDialogScript(string title, string filter)
{
    return
        "$ErrorActionPreference = 'Stop'\n" +
        "Add-Type -AssemblyName System.Windows.Forms\n" +
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" +
        "$dlg = New-Object System.Windows.Forms.OpenFileDialog\n" +
        $"$dlg.Title = '{EscapePowerShellSingleQuoted(title)}'\n" +
        $"$dlg.Filter = '{EscapePowerShellSingleQuoted(filter)}'\n" +
        "$dlg.Multiselect = $false\n" +
        "$dlg.CheckFileExists = $true\n" +
        "$dlg.CheckPathExists = $true\n" +
        "if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dlg.FileName }";
}

static string BuildSaveFileDialogScript(string title, string filter, string fileName)
{
    return
        "$ErrorActionPreference = 'Stop'\n" +
        "Add-Type -AssemblyName System.Windows.Forms\n" +
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" +
        "$dlg = New-Object System.Windows.Forms.SaveFileDialog\n" +
        $"$dlg.Title = '{EscapePowerShellSingleQuoted(title)}'\n" +
        $"$dlg.Filter = '{EscapePowerShellSingleQuoted(filter)}'\n" +
        $"$dlg.FileName = '{EscapePowerShellSingleQuoted(fileName)}'\n" +
        "$dlg.OverwritePrompt = $false\n" +
        "$dlg.CheckPathExists = $true\n" +
        "$dlg.AddExtension = $true\n" +
        "if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dlg.FileName }";
}

static string BuildFolderDialogScript(string title)
{
    return
        "$ErrorActionPreference = 'Stop'\n" +
        "Add-Type -AssemblyName System.Windows.Forms\n" +
        "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n" +
        "$dlg = New-Object System.Windows.Forms.FolderBrowserDialog\n" +
        $"$dlg.Description = '{EscapePowerShellSingleQuoted(title)}'\n" +
        "$dlg.UseDescriptionForTitle = $true\n" +
        "if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { Write-Output $dlg.SelectedPath }";
}

static string EscapePowerShellSingleQuoted(string value)
{
    return (value ?? string.Empty).Replace("'", "''");
}

static string ResolveConfigDirectoryPath(string? requestedPath, string defaultSubDirectory)
{
    var configRoot = GetConfigDirectoryPath();
    if (string.IsNullOrWhiteSpace(requestedPath))
    {
        return EnsureDirectory(Path.Combine(configRoot, defaultSubDirectory));
    }

    var rawPath = requestedPath.Trim();
    if (Path.IsPathRooted(rawPath))
    {
        return EnsureDirectory(rawPath);
    }

    return EnsureDirectory(Path.Combine(configRoot, rawPath));
}

static string ResolveConfigFilePath(string? requestedPath, string defaultFileName)
{
    var configRoot = GetConfigDirectoryPath();
    if (string.IsNullOrWhiteSpace(requestedPath))
    {
        return Path.Combine(configRoot, defaultFileName);
    }

    var rawPath = requestedPath.Trim();
    if (Path.IsPathRooted(rawPath))
    {
        return rawPath;
    }

    return Path.Combine(configRoot, rawPath);
}

static string EnsureDirectory(string path)
{
    Directory.CreateDirectory(path);
    return path;
}

static string? ReadStringOrNumber(JsonElement root, string propertyName)
{
    if (!root.TryGetProperty(propertyName, out var value))
    {
        return null;
    }

    return value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };
}

static string EscapeCsv(string value)
{
    if (string.IsNullOrEmpty(value)) return string.Empty;
    if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
    {
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
    return value;
}

static bool ShouldLogInteraction(PathString path)
{
    return path.StartsWithSegments("/barcodeScanner", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/saveLogs", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/pathPicker", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/printers", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/appConfig", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/orderStatusSelection", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/printLabelsByBarTender", StringComparison.OrdinalIgnoreCase);
}

static int GetInteractionLogIntervalMs(PathString path)
{
    if (path.StartsWithSegments("/barcodeScanner/pull", StringComparison.OrdinalIgnoreCase)) return 3000;
    if (path.StartsWithSegments("/barcodeScanner/status", StringComparison.OrdinalIgnoreCase)) return 3000;
    if (path.StartsWithSegments("/barcodeScanner", StringComparison.OrdinalIgnoreCase)) return 1000;
    return 0;
}

static string ClipForLog(string text, int limit)
{
    if (string.IsNullOrEmpty(text)) return string.Empty;
    if (text.Length <= limit) return text;
    return $"{text[..limit]}...(truncated)";
}

static string? TryMapBarTenderKnownError(string? errorText, string templatePath)
{
    if (string.IsNullOrWhiteSpace(errorText)) return null;
    var text = errorText.Trim();

    if (text.Contains("#3600", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("未引用任何数据库字段", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("did not reference any database fields", StringComparison.OrdinalIgnoreCase))
    {
        return $"BarTender 模板错误(#3600)：模板未绑定数据库字段。请在模板 {templatePath} 中将条码对象的数据源改为数据库字段（Code/CodeType/CodeTypeName）后重试。";
    }

    return null;
}

public sealed class InteractionLogThrottler
{
    private readonly ConcurrentDictionary<string, long> _lastLoggedAtMs = new();
    private readonly ConcurrentDictionary<string, int> _skippedCount = new();

    public bool ShouldWrite(string key, int intervalMs, out int skippedCount)
    {
        skippedCount = 0;
        if (intervalMs <= 0) return true;

        var nowMs = Environment.TickCount64;
        while (true)
        {
            var lastMs = _lastLoggedAtMs.GetOrAdd(key, 0);
            if (nowMs - lastMs >= intervalMs)
            {
                if (_lastLoggedAtMs.TryUpdate(key, nowMs, lastMs))
                {
                    _skippedCount.TryRemove(key, out skippedCount);
                    return true;
                }

                continue;
            }

            _skippedCount.AddOrUpdate(key, 1, (_, current) => current + 1);
            return false;
        }
    }
}

public record LogSaveRequest(string FileName, string Content, string Path);
public record PathPickerRequest(string Target);
public record OrderStatusSelectionState(string SelectedCode, string OrderStatus, string UpdatedAt);
public record PrintByBarTenderRequest(string BarTenderExePath, string TemplatePath, string? DatabasePath, List<PrintLabelItem>? Labels, string? PrinterName);
public record PrintLabelItem(string Code, string Type, string TypeName);
public record PrinterInfo(string Name, bool IsDefault, bool WorkOffline, string PrinterStatus);


