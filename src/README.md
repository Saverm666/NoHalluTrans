# 源码分层

[返回工程首页](../README.md)

| 项目 | 职责 |
| --- | --- |
| `NoHalluTrans.Core/` | LaTeX 语法、扫描、占位符、校验、调度及修复规则 |
| `NoHalluTrans.Infrastructure/` | 获取源码、准备工程、模型接口、编译、缓存与存储 |
| `NoHalluTrans.Desktop/` | WinUI 3 界面、视图模型与桌面交互 |

解决方案位于 `../NoHalluTrans.slnx`。核心项目嵌入 `../../reference/03-rules/ported-rules.json`（相对于 `NoHalluTrans.Core.csproj`），项目间引用保持相对路径。

编译结果集中到 `../artifacts/bin/`，中间文件集中到 `../artifacts/obj/`，不再写入源码目录。运行 `../scripts/Start-Dev.ps1 -NoLaunch` 完成本机编译。
