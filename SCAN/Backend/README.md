# Backend 接入说明

## 1. 引入源码
把 `BarcodeScannerModule.cs` 复制到你的 ASP.NET Core 项目（建议放到 `Modules/Scan`）。

## 2. Program.cs 注册
```csharp
using ScanModule;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBarcodeScannerModule();

var app = builder.Build();
app.MapBarcodeScannerModule();
```

## 3. 前端调用
按以下接口调用：
- `POST /barcodeScanner/start`，Body: `{ scannerIp, scannerPort, barcodeRegex }`
- `POST /barcodeScanner/stop`
- `GET /barcodeScanner/status`
- `GET /barcodeScanner/pull?afterId={lastId}`

## 4. 运行机制
- 启动时后台建立 TCP 连接到扫码枪
- 支持欢迎包读取、占用检测、断线重连
- 进程停止时自动执行 `Stop()`，释放连接与轮询任务
