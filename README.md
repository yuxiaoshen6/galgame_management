# 樱笺 · Galgame 管理器

樱笺是一个 Windows 桌面游戏库，可离线整理和启动已安装或已解压的 Galgame。

## 开始使用

1. 将 `GalgameManager-0.2.1-win-x64.zip` 解压到一个目录，双击 `GalgameManager.exe`。压缩包自带运行时，普通用户即可打开。
2. 在「游戏库」选择「添加扫描目录」，选中游戏所在的文件夹，再从扫描页点击「扫描全部」或「扫描此目录」。
3. 核对导入预览，按需调整启动入口和勾选状态，再确认导入。入口还没确定的条目可以先以「待配置」状态入库。
4. 回到游戏库，按名称搜索。点击卡片进入详情，或点击「启动」直接打开已就绪的游戏。
5. 漏扫的游戏可手动添加。移动过的游戏可在详情中「重新定位」。自定义工作目录留空时使用入口文件所在目录，启动参数按项录入。
6. 误导入的游戏可在详情页点击「从库中移除」，核对名称和路径后确认。该操作只删除游戏库记录，原游戏文件仍可在后续扫描中找到。

## 补充封面和简介

导入预览会从游戏安装目录读取 `cover.jpg`、`cover.png` 或 `cover.jpeg`，以及 `metadata.json` 中的 `summary` 或 `description` 字段。也可以用 UTF-8 编码的 `description.txt` 存放简介。封面支持 8 MB 以内的 JPEG 或 PNG。

缺少资料时，在预览中点击「联网查找封面和简介」，选择 Bangumi 或 VNDB，核对候选作品后点击「使用此条目」。查询使用输入的游戏名，匹配结果补齐缺失字段。无资料时仍可确认导入。已入库的游戏可在详情页选择封面、编辑简介，或联网补齐资料，完成后点击「保存配置」。

游戏库卡片的封面按原比例铺满展示框，超出边框的部分居中裁切。详情页显示完整封面和可滚动的简介。保存后的图片和资料随本地游戏库离线可用。

`Ctrl+F` 聚焦名称搜索，`Esc` 返回并取消详情编辑。系统关闭动画效果时，界面直接切换。扫描目录和游戏配置默认保存在 `%LOCALAPPDATA%\GalgameManager\library.db`，诊断记录保存在同一目录。

## 从源码构建

在 Windows 上安装 .NET 10 SDK x64，在仓库根目录执行。

```powershell
dotnet build GalgameManager.slnx -c Release
dotnet run --project src/GalgameManager.Desktop -c Release
dotnet run --project tests/GalgameManager.Smoke -c Release -- --benchmark
.\scripts\Publish-Windows.ps1
```

发布脚本把自包含 ZIP 写入 `artifacts`。开发和测试时可向桌面程序传入 `--data-directory "<绝对路径>"`，使用单独的游戏库。

- [版本变更记录](CHANGELOG.md)
- [首版验收记录](docs/MVP-ACCEPTANCE.md)
- [需求规格说明书](docs/需求规格说明书.md)
- [系统设计文档](docs/系统设计文档.md)
- [开发计划](docs/开发计划.md)
