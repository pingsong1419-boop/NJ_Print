# BarTender 打印测试逻辑模板

本目录用于固化“打印测试”逻辑模板，后续无需再改动业务逻辑。

## 当前模板逻辑
1. 读取配置中的 `barTenderExePath` 与 `barTenderTemplatePath1`。
2. 用户输入单条“打印条码”。
3. 调用 `/printLabelsByBarTender` 执行打印。
4. 输出成功/失败提示及执行命令。

## 备注
- 运行态配置统一落盘到 `Config/app_config.json`。
- MES 正式打印阶段使用双模板顺序执行（模板1 -> 模板2）。
