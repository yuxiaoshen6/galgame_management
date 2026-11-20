# v0.1.0 首版验收记录

验收日期为 2026-11-20。开发机为 Windows 11 家庭中文版 25H2 x64（26200.9974），Intel Core i5-12500H（16 个逻辑处理器）、16 GiB 内存和本地 NVMe SSD。构建使用 .NET SDK 10.0.404 的 Release 配置。

| 开发计划 | 样本与操作 | 本机结果 |
| --- | --- | --- |
| T01 扫描与取消 | 重叠的扫描根、已消失的目录、指向范围外的目录联接、扫描期间令牌取消 | 通过。返回有效分支的结果及失败原因，取消的扫描抛弃本次结果。 |
| T02 导入 | 五个合成安装目录，覆盖多入口、辅助程序、嵌套入口、入口在预览后被移走 | 通过。确认前库为空，入口被移走后整批暂停，恢复后可重试。 |
| T03 搜索与持久化 | 重新打开数据库，运行 WPF 游戏库并输入中文、日文、英文和无匹配词 | 通过。记录与参数恢复，名称搜索忽略英文字母大小写。 |
| T04 启动参数 | 实际启动测试程序，核对默认与自定义工作目录、空参数、引号、空格和参数顺序 | 通过。测试程序回传值与保存配置一致。 |
| T05 路径修复 | 移动含中文、日文和空格的安装目录，按旧相对路径重新定位并再启动 | 通过。原 ID、名称与参数保留，已占用路径有冲突提示。真实移动磁盘断开和恢复待测。 |
| T06 去重与数据一致性 | 大小写路径、重叠根、从 `bin` 重扫、导入中途写入失败及数据库迁移失败 | 通过。已有配置保留，失败操作回滚，恢复后可重试。 |
| T07 双系统发布 | Windows x64 自包含 ZIP 解压到含中文、日文及空格的路径 | Windows 11 普通账户能打开发布包，宿主诊断确认使用包内的 .NET 10.0.15。Windows 10 22H2 x64 干净环境及双系统完整回归待测。 |
| T08 扫描基线 | 5,001 个目录和 1,000 个合成入口，连续三次调用 Release 扫描服务 | 最终一轮为 289、267、280 毫秒，中位数 280 毫秒。实测范围为扫描、归组、评分和读取已有记录。 |
| UI01–UI06 桌面流程 | 系统选择器、WPF 预览、入库、搜索、配置校验和保存、卡片展开至整窗并收回、紧凑布局 | 系统目录选择器能打开。WPF 冒烟验证通过，覆盖 1240 × 690 和 920 × 568 两种窗口尺寸。 |

合成样本覆盖首版识别规则和异常恢复。五款真实 Galgame 的启动验收、Windows 10 干净环境验证，以及 P01、P02、P04、P05 的完整计时仍待执行。本记录用于首版试用，双系统正式发布验收仍以开发计划为准。

## 资源采样

Windows 11 本机解压启动，使用五条合成记录的游戏库。单次空闲采样的工作集为 163.1 MiB，专用内存为 116.2 MiB，10 秒内进程 CPU 时间无可测增量。自包含 ZIP 约 63.7 MiB。

扫描和文件状态检查在后台执行。游戏库和预览按可视区域创建列表项，主题插画随包存储。详情和页面切换动效响应 Windows 动画设置。

## 复现命令

```powershell
dotnet build GalgameManager.slnx -c Release
dotnet run --project tests/GalgameManager.Smoke -c Release --no-build -- --benchmark
.\scripts\Publish-Windows.ps1
```

## 界面插画

`src/GalgameManager.Desktop/Assets/Station.jpg` 由内置 ImageGen 生成，经尺寸和 JPEG 压缩后随应用打包。

> Use case: illustration-story. Asset type: wide desktop visual novel library hero background, to be cropped to a low banner. Original modern Japanese visual novel illustration. Quiet dusk at a Japanese coastal railway platform, cherry blossom petals, navy blue and mauve pink sky, distant power lines, one modestly clothed young adult woman with long dark hair in a pale lavender spring jacket reading a small book at the far right. Place face and most visual interest at x=75%, y=42%, medium shot, head fully visible. The entire left 52% goes into very dark midnight indigo shadows with sparse pale petals and remains quiet, to support white UI text added later. Luminous linework, inked anime background painting, refined cel shading, subtle film grain, cinematic magenta rim light. Landscape wide 3:1 aspect ratio if available. No text, typography, logo, watermark, or interface.
