# 脚本索引

[返回工程首页](../README.md) · [开发指南](../docs/开发指南.md)

在工程根目录使用 Windows PowerShell 执行。

| 脚本 | 用途 |
| --- | --- |
| `Start-Dev.ps1` | 本机编译和启动；`-NoLaunch` 仅编译，`-SkipBuild` 复用已有构建 |
| `Build.ps1` | 测试并更新 `HJFY-Portable/` 和 `HJFY-Full/` 两份最新版，不生成 ZIP |
| `Package.ps1` | 独立压缩已有便携目录，需要分发时才执行 |
| `Prepare-TeX.ps1` | 初始化本机 Windows TeX Live |
| `Verify-Integration.ps1` | 验证编译环境和翻译链路，需指定新的输出目录 |
| `Verify-Portable.ps1` | 使用便携目录内的 TeX 编译中文样例 |
| `New-PortableArchive.ps1` | 独立打包脚本调用的 ZIP 辅助工具 |
| `capture-reference.py` | 从 reference 采集参考行为 |
| `capture-rule-reference.py` | 从 reference 采集规则参考行为 |
| `summarize-paper-batch.py` | 汇总真实论文批测记录，检查 PDF 页数、中文文本及残留占位符 |

```powershell
.\scripts\Start-Dev.ps1 -NoLaunch
.\scripts\Build.ps1
```

`Build.ps1` 默认执行测试，可用 `-Dotnet`、`-TexRoot` 和 `-OutputDirectory` 指定工具与产物父目录。默认更新两份成品；`-Edition Portable` 单独更新轻便版，`-Edition Full`（兼容 `-IncludeTex`）单独更新完整版，只有完整版复制现成 TeX。成功后删除旧程序并保留用户数据。分发目录不使用外部目录联接。
