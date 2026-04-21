# SCAN 可移植模块

本目录提供扫码枪后端模块，来自 `PACK_Onlie` 项目拆分。

## 目录
- `Backend/BarcodeScannerModule.cs`：扫码枪核心运行时 + 最小 API 路由扩展
- `Backend/README.md`：接入步骤

## 当前导出的接口
- `POST /barcodeScanner/start`
- `POST /barcodeScanner/stop`
- `GET /barcodeScanner/status`
- `GET /barcodeScanner/pull?afterId=0`
