# NoHalluTrans · 幻觉翻译

C# / .NET 10 / WinUI 3 桌面程序，用于翻译 LaTeX 论文源码并编译生成 PDF。

## 版本说明

- **Portable（轻便版）**：仅包含主程序与 .NET / WinUI 运行库，首次使用时可在软件内按需下载 TeX 运行环境。
- **Full（完整版）**：内置 TeX Live 运行环境，解压即用，支持完全离线编译。

## 目录结构

```text
NoHalluTrans/
├── NoHalluTrans.slnx      解决方案
├── Directory.Build.props  共享构建配置
├── global.json            .NET SDK 版本
├── src/                   Core、Infrastructure、Desktop 源码
├── samples/               离线 LaTeX 示例
├── scripts/               开发、构建与打包脚本
├── tools/launcher/        原生启动器源码与程序
└── docs/                  使用说明与开发指南
```

## 构建

在工程根目录使用 PowerShell 执行：

```powershell
# 本地开发编译（仅编译）
.\scripts\Start-Dev.ps1 -NoLaunch

# 构建便携目录（artifacts/NoHalluTrans-Portable 与 NoHalluTrans-Full）
.\scripts\Build.ps1

# 打包为 ZIP 分发包
.\scripts\Package.ps1
```

## 文档

- [使用说明](docs/使用说明.md)
- [开发指南](docs/开发指南.md)
- [源码分层](src/README.md)
