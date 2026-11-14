# HJFY · 幻觉翻译

C# / .NET 10 / WinUI 3 桌面程序，将 LaTeX 论文源码翻译并生成 PDF。

## 工程结构

```text
HJFY/
├── NoHalluTrans.slnx              解决方案
├── Directory.Build.props  共享配置与集中产物路径
├── global.json            .NET SDK 版本
├── src/                   Core、Infrastructure、Desktop 产品源码
├── tests/                 行为测试、桌面检查与参考数据
├── samples/               离线 LaTeX 示例
├── scripts/               开发、验证、便携目录构建与独立打包
├── tools/                 原生启动器源码、程序与许可
├── reference/             Python 参考实现、规则、研究资料与证据
├── docs/                  使用说明、开发指南、进度和验证记录
└── artifacts/             所有生成产物
    ├── bin/               各项目编译输出
    ├── obj/               各项目中间文件
    ├── NoHalluTrans-Portable/     最新轻便版（不含 TeX，可在软件内下载）
    ├── NoHalluTrans-Full/         最新完整版（含 TeX，可离线使用）
    ├── dev/               按需生成的开发启动入口
    └── verification/      当前验证记录
```

可运行的便携目录位于 `artifacts/NoHalluTrans-Portable/` 和 `artifacts/NoHalluTrans-Full/`。根目录仅放工程源码目录、说明和必要构建配置；旧版本、临时文件和根目录启动快捷文件已清理。

## 构建

在工程根目录执行：

```powershell
# 本机开发编译，临时输出均在 artifacts 内
.\scripts\Start-Dev.ps1 -NoLaunch

# 测试并更新轻便版和完整版，不创建压缩包
.\scripts\Build.ps1

# 以后需要压缩分发时再单独执行
.\scripts\Package.ps1
```

轻便分发目录仅携带程序与 .NET/WinUI 运行库；编译器、中文字体和 TeX 宏包在首次使用时按需下载到 `runtime/texlive/`。完整版包含本机已准备好的 TeX。可用 `-Edition Portable` 或 `-Edition Full` 单独更新其中一份。构建成功后移除被替换的旧程序，保留用户数据。

便携版启动：双击 `artifacts\NoHalluTrans-Portable\NoHalluTrans.exe`；离线使用选择 `artifacts\NoHalluTrans-Full\NoHalluTrans.exe`。开发时运行 `scripts/Start-Dev.ps1` 编译并启动，或使用 `-SkipBuild` 打开已有开发构建。开发入口按需生成在 `artifacts/dev/`，清理中间产物后需重新编译。

[使用说明](docs/使用说明.md) · [开发指南](docs/开发指南.md) · [源码分层](src/README.md) · [脚本索引](scripts/README.md) · [参考资料](reference/00-README.md) · [验证记录](docs/VERIFICATION.md)
