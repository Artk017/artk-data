# Artk Tools

WinUI 3 桌面工具箱（原项目名 Nova），六个页面 **Windows / 垃圾清理 / 软件 / Game / Safe / 手机工具**，外加设置页。
软件清单不再内置：增删改都写自建清单服务器 **Artk Server**（工程 `NovaServer/`），客户端只负责展示、下载和安装。

## 环境

| 项 | 版本 |
|---|---|
| Visual Studio | Community 2026 18.11（Windows App SDK C# 工作负载） |
| .NET | 10.0（SDK 10.0.402） |
| Windows App SDK | 2.5.1 |
| 目标框架 | `net10.0-windows10.0.19041.0` |

项目以**非打包（`WindowsPackageType=None`）**方式运行，调试时直接启动 exe，不需要 MSIX 部署。
发布是**自包含**的（.NET 运行时 + Windows App SDK 一起打进安装包），目标机器不用另装任何运行时；
开发机仍需已安装 Windows App Runtime 2.5。

工程只引用 Windows App SDK 的 **WinUI 分体包**（`Microsoft.WindowsAppSDK.WinUI` + `InteractiveExperiences` + `Runtime`），
不引用元包，避免把 AI / ML / Search 运行时（onnxruntime、DirectML 等约 50 MB）带进安装包。

> **改名说明**：工程名、命名空间与主程序 exe 已统一为 `ArtkTools`（旧版是 `WinUIDemo.exe`，升级安装时会自动清理旧 exe）。
> 用户数据目录仍是 `%LOCALAPPDATA%\Nova`，升级不会丢设置；服务端 Velopack packId 仍是 `ArtkServer`。
> 客户端安装包使用 Inno Setup（`installer.iss`），卸载走 Inno 的 `unins000.exe`。

## 构建与运行

```powershell
dotnet build -c Debug -p:Platform=x64
.\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\ArtkTools.exe
```

或在 Visual Studio 2026 打开 `ArtkTools.sln`，平台选 **x64**，F5 启动。

## 打包成 exe 安装包

双击根目录 `一键构建.bat`（`一键构建.bat 1.2.0` 可指定版本），它调用 `build-installer.ps1`：

1. 自包含发布客户端到 `dist\app`（`--self-contained true`、`WindowsAppSDKSelfContained=true`）；
2. 自包含发布 Artk Server 到 `dist\server`；
3. `ISCC`（Inno Setup 6）按 `installer.iss` 编译客户端安装包，向导风格与 tubatools 一致；
4. `vpk pack` + `ServerSetup/` 只用于服务端安装包（套自定义向导）；
5. 版本号自动 +1（记在 `dist\version.txt`，也可用 `-Version` 指定）；
6. 收尾清理中间产物：`dist\app`、`dist\server`、`dist\setupgen`、`dist\serversetupgen`、`*-old-*` 全部删掉。

> 打包需要额外安装 Inno Setup 6：`winget install JRSoftware.InnoSetup`（或把 `ISCC.exe` 放进 PATH）。

构建完成后 `dist` 里只保留：

| 路径 | 内容 |
|---|---|
| `dist\release` | 客户端 `ArtkTools-win-Setup.exe`、`ArtkTools-win-Portable.zip` 等 |
| `dist\release-server` | 服务端 `ArtkServer-win-Setup.exe`、`ArtkServer-win-Portable.zip` |
| `dist\version.txt` | 上次构建版本号，供下次自动递增 |

当前体积参考：客户端 Setup 约 **45 MB**、便携包约 **66 MB**；服务端 Setup 约 **139 MB**、便携包约 **82 MB**。
服务端不再额外保留 `dist\server` 文件夹，部署请直接用安装包或便携包解压。

### Artk Tools 安装包（`installer.iss`，Inno Setup 6）

- 和 `_ref\tubatools-master` 同款：Inno Setup 6 modern 向导（纯白底、右下角「上一步 / 下一步 / 取消」）。
- 流程：目标位置 → 开始菜单文件夹 → 附加任务（桌面快捷方式）→ 准备安装 → 正在安装 → 完成（可勾选运行 Artk Tools）。
- 安装到 `Program Files\Artk Tools`，需要管理员授权；快捷方式、卸载器、卸载项都由 Inno 生成。
- 安装 / 升级前 Inno 会自动结束正在运行的旧版本（Restart Manager），不会装出第二份。
- 卸载时询问是否删除 `%LOCALAPPDATA%\Nova` 的用户数据，默认保留。
- 中文文案来自 `ChineseSimplified.isl`，页面用语在 `installer.iss` 的 `[Messages]`。
- 旧的 `Installer/` 工程（`NovaSetup.exe`）不再用于客户端，只作为 `ServerSetup` 的共享源码保留。

### Artk Server 安装向导（`ServerSetup/`）

- 同样的路径 / 快捷方式 / 安装后打开选项，多一个**是否注册开机自启后台服务**（会提权创建计划任务 `ArtkServer` 并放行防火墙端口）。
- 支持 `--silent [--installto <目录>] [--no-desktop] [--no-launch] [--service|--no-service]`。
- packId 是独立的 `ArtkServer`，和客户端的 `NovaWorkbench` 不冲突；默认安装到 `%LOCALAPPDATA%\Artk Server`。

## 目录结构

```
App.xaml(.cs)          应用入口 + 全局资源（配色、卡片/文字样式、深浅主题字典）
MainWindow.xaml(.cs)   窗口外壳：自绘标题栏、Mica、左侧导航（Windows / 垃圾清理 / 软件 / Game / Safe / 手机工具）+ 设置、托盘图标
Assets/app.ico         应用图标（由 宇宙公主2.png 生成，16–256 多尺寸）
Assets/Winapp2.ini     Winapp2 清理规则数据库（CC-BY-SA-4.0，4,000+ 条规则）
Assets/CleanerExtra.ini 国产软件补充规则（QQ / QQ 音乐 / 微信 / 网易云音乐 / WeGame / 360 等）
Services/
  AppSettings.cs       设置模型与持久化（%LOCALAPPDATA%\Nova\settings.json）
  AppCatalog.cs        清单模型（名称 / 分类 / 固定底色 / 官网 / 直链 / 图标地址 IconUrl / 自动解析方式）
  CatalogServer.cs     清单服务器客户端（读 / 增 / 改 / 删，服务器地址来自设置）
  AppIconLoader.cs     图标图片下载与缓存（内存 + 磁盘 icons\，BitmapImage 解码）
  AppDownloads.cs      安装包下载（进度 / 取消 / Referer / 远端体积探测）、打开文件夹、运行安装包、开浏览器
  DownloadRegistry.cs  已下载安装包的持久记录（%LOCALAPPDATA%\Nova\downloads.json）
  DownloadTracker.cs   跟踪进行中的下载，退出前据此提示
  AppInstall.cs        识别是否已安装（扫卸载注册表）+ 静默安装 + 找主程序 exe
  AppUninstaller.cs    卸载自身（Inno 的 unins*.exe；老 Velopack 版本用 Update.exe --uninstall）
  AppLinkResolver.cs   现场解析直链：KOOK 版本接口 / NVIDIA 与奇游抓下载页
  AppLinks.cs          旧版直链工具留下的 links.json 读取（兼容已有用户数据）
  AppAutoFill.cs       旧版添加流程的链接自动识别（已被「添加软件」取代，当前未引用）
  AutostartHelper.cs   安装完清掉该软件的开机自启
  UninstallCleanup.cs  卸载前清理自身数据目录与残留磁贴注册表项
  Toast.cs             全局右上角通知（Success / Error / Info / Busy）
  SystemTweaks.cs      系统开关（指针精度 / 电源计划 / 背景 / 主题 / 任务栏）
  ProcessMonitor.cs    每进程 CPU 增量 + 工作集采样
  CleanerLibrary.cs    加载 Winapp2.ini + CleanerExtra.ini、中文分类、只保留已安装应用
  CleanerParser.cs     解析 Winapp2 格式（FileKey / RegKey / ExcludeKey / Detect / Warning）
  CleanerDetector.cs   判断规则对应的应用是否已安装
  CleanerPathExpander.cs 展开 %环境变量% 与路径段通配符
  CleanerEngine.cs     两阶段扫描 / 清理（先出清单，再删除；ExcludeKey 与用户数据目录保护）
  CleanerNameTranslator.cs 项目名与风险提示中文化（精确词典 + 短语替换）
  CleanerViewModels.cs 清理页的分类 / 项目 / 结果视图模型
  …                    （AltTabTabs / DisplaySettings / DesktopIcons / ContextMenuStyle / FirewallControl /
                        UacNotify / DefenderPolicy / DefenderRegistryTweak / LocalGroupPolicy /
                        SecurityCenterGuard / SecurityCenterNotify / ActivationStatus / KmsActivation /
                        RegistryAcl / HklmKey / KnownFolderLocations / RegApplier / PhoneTools / PinyinVocab /
                        TrayIcon / QuietHours / AutostartHelper 等）
Helpers/
  ProcessIconLoader.cs 从 exe 提取图标并缓存
  UiMotion.cs          轻量动效：淡入滑动、悬停缩放
  WindowSizeLimits.cs  窗口最小 / 最大尺寸
  ToolWindowSlot.cs    弹窗外壳的懒创建 / 复用
  …                    （ScreenPlacement / SystemFacts / SliderCommit / WindowFocus）
Controls/
  ProcessRowView       进程行：图标 + 名称 + CPU + 内存
Views/
  WindowsPage          系统页：内嵌系统设置开关与系统信息，另含文件位置 / 微软词汇两个入口
  JunkCleanPage        垃圾清理：Winapp2 + 国产补充规则、分类勾选、扫描预览、风险提示与单项清理
  SoftwarePage         软件：服务器清单卡片、图标、下载 / 安装 / 取消、右键编辑删除
  AddSoftwareWindow    内置浏览器（WebView2）：输入网址或关键词，点下载按钮抓直链和图标
  AppEditorDialog      添加 / 编辑确认框：名称、官网、直链 + 抓到的图标单选
  GamePage             游戏：检测已安装、上传 .reg 并应用
  SafePage             安全：系统与权限开关、安全中心批量设置
  PhoneToolsPage       手机工具：adb / fastboot 设备状态与信息
  SettingsPage         设置：主题 / 云母 / 启动页 / 软件安装位置 / 关于 / 卸载
  ToolWindow           所有弹窗共用的外壳：自绘标题栏 + 图钉置顶 + 记住尺寸
  …                    （ProcessDetailView / SystemSettingsView / SystemInfoView / FileLocationsDialog /
                        PinyinVocabView / CommandConsoleDialog）
installer.iss          Artk Tools 的 Inno Setup 6 安装脚本（和 tubatools 同款向导）
ChineseSimplified.isl  Inno 安装向导的中文文案
NovaServer/            Artk Server：WinUI 设置界面 + Kestrel 清单 API + 数据同步
Installer/             旧的自定义安装向导源码（客户端已改用 Inno，只剩 ServerSetup 复用）
ServerSetup/           Artk Server 安装向导（WinForms），多后台服务选项
LinkGrabber/           已停用的旧直链工具工程，不参与构建，可删除
```

## 软件页

左侧导航「软件」，卡片网格，数据**只来自服务器**（`GET /api/catalog`）：拿不到就清空列表并显示「没拿到清单 + 原因 + 重试」。
本地不再缓存清单快照，也不再用内置数组兜底。旧的分类筛选（全部 / 通讯 / 游戏 / 工具）和「直链工具」入口都已移除。

每张卡片包含：图标、名称、状态行（官方直链 / 已抓取直链 / 官网下载页 / 进度与速度）、主按钮（下载 / 取消 / 安装 / 前往官网）、
「官网」次按钮（只有直链时才显示）、右上角「⋯」菜单（编辑 / 删除，右键卡片同样可以）。

### 图标

- 清单条目带 `IconUrl` 时，卡片从网上拉图并缓存（内存 + `%LOCALAPPDATA%\Nova\icons\`）；
- 有图片图标时**不再画固定底色**，图标透明底直接透出卡片背景；
- 没有 `IconUrl` 或图片加载失败时，回落到「名称首字母 + 固定紫 `#6C4CF1`」。

### 添加软件（`AddSoftwareWindow`）

1. 点页头「添加软件」，打开内置 WebView2 浏览器窗口（同一时间只开一个）；
2. 地址栏输入网址，或直接输入关键词（不像网址就交给必应搜索 `https://www.bing.com/search?q=…`）；
3. 在网页里点厂商自己的「下载」按钮，`DownloadStarting` 被拦截（`e.Cancel = true`，只取真实地址）；
4. 弹出确认框：自动预填名称（页面标题 / 文件名推断）、官网地址（当前页面）、下载直链；
5. 同时从当前页面抓图标候选（apple-touch-icon / favicon / og:image / 页面大图，最多 12 个），
   以缩略图单选：可选任意一张，也可以选「不用图片，用名称首字母」；默认选第一个抓到的图标；
6. 保存即写入服务器清单，窗口关闭；软件页自动重新加载。

QQ、QQ音乐这类下载地址是页面里现算的签名链接，裸地址会 403，必须真开页面点一次按钮才能拿到——
这也是「添加软件」改成内置浏览器的原因。

### 下载与安装

- 有直链：应用内下载（进度 / 速度 / 取消），完成后自动静默安装；静默失败转图形安装；
- 没有直链：主按钮变成「前往官网」，打开浏览器；仍可用「添加软件」在网页里抓一次直链；
- 已下载但没装：点主按钮直接安装；装完可清理残留安装包；
- 安装完成后自动清掉该软件的开机自启（Run / RunOnce 值 + 启动文件夹快捷方式）。

### 编辑 / 删除

编辑框只保留名称、官网地址、下载直链三项（可保留已选图标）；删除会直接写服务器，其它电脑同步后也看不到。

## 清单服务器（Artk Server）

- 可执行文件 `Artk Server.exe`：WinUI 设置界面 + 内置 Kestrel 服务；`--headless` 参数以无窗口后台服务运行。
- 清单存 `data\catalog.json`（安装根目录），启动时若不存在会自动从数据仓库下载 `catalog.json` 与 `.reg` 资源；
  下载失败回退内置清单，不阻塞启动；`--refresh-data` 或 `server.json` 的 `refreshDataOnStart` 强制重新下载。
- `server.json` 可配置端口、数据源 `dataBaseUrl`、私有仓库 token `dataToken`、清单自动发布 `autoPublish` 等。
- `install-service.ps1` 注册计划任务 / 服务；安装包勾选「注册后台服务」时会自动执行。
- 客户端里服务器地址是写死的常量，设置页不再展示也不提供连接检查；软件页拿不到清单时会提示原因并支持重试。
- 新增的 `IconUrl` 字段需要**新版 Artk Server** 才会持久化，旧服务端会忽略该字段。

## 垃圾清理

规则来自两份随程序输出的 INI：

| 文件 | 内容 |
|---|---|
| `Assets\Winapp2.ini` | 社区维护的通用数据库（4,000+ 条规则，CC-BY-SA-4.0），以国外软件为主 |
| `Assets\CleanerExtra.ini` | 自建国产软件补充规则：腾讯 QQ（缓存 / 临时文件）、QQ 音乐、微信（新版）、腾讯通用日志、网易云音乐、WeGame、360 安全卫士 |

- **只显示已安装的应用**：按 `DetectFile` / `Detect` / `SpecialDetect` 判断，本机当前识别约 134 项。
- **中文显示**：项目名与风险提示由 `CleanerNameTranslator` 翻译；品牌名保留英文（如 `Microsoft Edge 自动填充数据和搜索引擎设置`）。搜索框同时匹配中文名和英文原名。
- **两阶段执行**：扫描阶段只生成删除清单并计算体积，不删除任何文件；清理阶段才按清单删除，正在使用的文件自动跳过。
- **安全网**：除规则自带的 `ExcludeKey` 外，代码里还硬编码保护浏览器扩展 IndexedDB，以及 `Documents\Tencent Files`、`Documents\WeChat Files`、`Documents\xwechat_files`、`Documents\WXWork` 等聊天记录目录。
- **清理前确认**：勾选项涉及正在运行的浏览器时先提示关闭；规则带 `Warning` 的项目会汇总成「风险提示」弹窗，取消即不会执行。
- **勾选记忆**：勾选状态写入 `settings.json`；勾选变化会使已扫描结果失效，需要重新扫描。
- 新增国内应用时，直接往 `CleanerExtra.ini` 追加同格式条目即可；只收录缓存、日志、崩溃报告等可再生成数据。

## 其它页面

| 页面 | 内容 |
|---|---|
| Windows | 直接内嵌「系统设置」（指针精度 / 高性能电源 / 桌面背景 / 深色模式 / 任务栏靠左 / Alt+Tab 标签页 / 应用通知 / 勿扰 / 刷新率 / 桌面图标 / 右键菜单等开关）与「系统信息」（设备名、处理器、内存、显卡、系统版本）；下方保留文件位置、微软词汇两个工具入口 |
| 垃圾清理 | 加载 `Assets\Winapp2.ini` 与 `Assets\CleanerExtra.ini`，只显示本机已安装的项目；左侧按中文分类折叠勾选（搜索支持中英文名、全选 / 全不选 / 恢复默认 / 全部展开），右侧为扫描结果，点击可查看将删除的文件与注册表项并单项清理 |
| Game | 检测「崩坏：星穹铁道」「PotPlayer 64 bit」是否已安装；上传本地 `.reg`（一个软件可多个），齿轮一键应用全部配置 |
| Safe | 「系统与权限」（关闭防火墙 / 关闭防病毒 / 关闭防病毒(注册表) / 系统激活 / 注册表权限 + 防火墙通知）与「安全中心设置」（一个开关批量关/开 13 项防护） |
| 手机工具 | adb / fastboot 检测（系统 / fastboot / fastbootd / recovery / sideload / 未授权 / 离线）与设备信息，每 4 秒刷新 |
| 设置 | 主题 / 云母材质 / 启动页 / 软件安装位置 / 关于（版本、恢复默认）/ 卸载 |

设置保存在 `%LOCALAPPDATA%\Nova\settings.json`，缺失或损坏回落默认值；应用没有账号体系，全部数据只在本机与自建服务器之间流转。

## 设计约定

- **配色**：品牌紫 `#6C4CF1` → 青 `#22B8CF` 渐变；成功 `#0F9D58`、警告 `#B45309`、危险 `#DC2626`。
- **卡片**：14px 圆角、1px 描边、统一走 `CardStyle`；图标 40×40、11px 圆角。
- **字号**：页面标题 28 / 卡片标题 15 / 正文 13 / 说明 12 / 指标数值 30。
- **图标**：界面图标用 Segoe Fluent Icons；软件图标用服务器给的真实图片，失败回落首字母 + 固定紫。
- **动效**：只动 `Opacity` / `RenderTransform`，卡片悬停 1.015 缩放。
- **页面缓存**：`NavigationCacheMode="Required"`，来回切页保留滚动位置。

## 现状

- 六个模块 + 设置页可正常导航，深浅主题完整；窗口标题、安装包、托盘提示均为 Artk Tools。
- 软件页完全由服务器清单驱动，支持添加（浏览器抓直链 + 图标多选）、编辑、删除、下载、取消、安装、卸载；
  图标有网图就透明显示，没有就回落首字母。
- Windows 页已移除 CPU / 内存 / GPU / 带宽指标与负载曲线，改为内嵌系统设置开关和系统信息。
- 垃圾清理页接入 Winapp2 + 国产补充规则，实测识别 134 项、可清理约 11.6 GB；支持中英文搜索、分类勾选记忆、扫描预览、风险提示与单项清理。
- Game / Safe / 手机工具 的操作（应用 .reg、系统开关、adb 检测）是真实的；部分展示内容仍是静态。
- 已知限制：窗口宽度小于约 1100px 时双列卡片会挤压，尚未做响应式断点；清理规则以 Winapp2 为主，国内软件覆盖依赖 `CleanerExtra.ini` 持续补充；软件图标依赖外网图片地址，加载失败会回落首字母。
