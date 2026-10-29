# AGENTS.md — 剑星 MOD 管理器 开发交接

> 本文件是给后续会话/开发者的工作提示词：项目状态、构建、测试、架构、坑、发布流程。
> 会话归档时版本：**v1.14.2**（已发布；源码 `0f60186`，tag `v1.14.2`）。

## 1. 项目概览

- 仓库：`xiangfa-test/Stellar-Blade-Mod-Manager`（GPL-3.0）
- 技术栈：WinUI 3（Windows App SDK 1.8）+ .NET 8（`net8.0-windows10.0.19041.0`）
- 目标：管理《Stellar Blade（剑星）》模组：导入/部署/停用、N 网浏览与下载、合集、方案、崩溃捕获
- 用户偏好：中文界面与回答；README 保持精简；发版说明只写本版改动、不带作者
- 数据目录：默认 `%LOCALAPPDATA%\SBModManager`，可用 `--data-dir` 或环境变量 `SBMM_DATA_DIR` 覆盖

## 2. 目录结构

```
src/SBModManager/
  Models/Models.cs            ModRecord / DeployedMod / AppConfig / ModType 等
  Services/                   业务层（导入、部署、N 网、更新、崩溃、本地化…）
  ViewModels/                 ModItem 等 UI 模型
  Views/                      XAML 页面 + code-behind（部分按功能拆 partial）
  Ui/                         WrapPanel / Markdown / 动画 / UiTask 等 UI 辅助
  MainWindow.xaml(.cs)        导航、标题栏、更新提示
  strings.json                翻译表（随 exe 发布，可编辑）
_tools/shot_existing.py       截取已运行窗口（窗口标题：剑星 MOD 管理器）
_scratch/                     本机临时目录（**git 忽略**）：测试数据、发布脚本、截图
```

## 3. 构建

优先使用仓库自带脚本（会处理离线 NuGet 与 MakePri 权限问题）：

```powershell
.\build.ps1
```

等价命令：

```powershell
dotnet build src/SBModManager/SBModManager.csproj -c Release -p:Platform=x64 `
  -p:MakePriExeFullPath='C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe'
```

注意：

- 依赖来自仓库内离线源 `_nuget/feed`（`NuGet.config` 已配置），不要删除 `_nuget/`
- 运行中的 `SBModManager.exe` 会锁住 `bin`，构建报 MSB3027 时先结束进程
- `dist/` 可能被正在运行的实例锁住，发布时应从 `bin` 打包

## 4. 运行 / 测试

```powershell
# 常规启动（默认请求管理员权限）
src\SBModManager\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\SBModManager.exe

# 不请求提权 + 指定页面
...\SBModManager.exe --no-elevate --nav settings
```

常用参数：`--nav <tag>`（mods/nexus/collections/loadorder/profiles/runtime/debug/settings/about）、
`--page <tag>`、`--collection <slug>`、`--select-first`、`--data-dir <path>`、`--self-update`、
`--nexus-detail <id>`（打开 N 网详情页，用于截图/调试）。

自检（必须全 PASS）：

```powershell
$env:SBMM_DATA_DIR="$pwd\_scratch\appdata"
$env:SBMM_INSTANCE_SUFFIX="-qa"
...\SBModManager.exe --selftest "$pwd\_scratch\st.txt"
# 报告在 _scratch\st.txt，grep "FAIL|RESULT"
```

**项数随功能增长，不要记死数字**：基线 121 项，`SBMM_TEST_UE4SS_ZIP` 打开时 +7，另有原子写 4 项、
停用日志 3 项、同组版本 4 项、MAIN 优先 2 项、翻译表一致性 1 项等。实测 142 项（含 fixture）。
判断标准是 `RESULT: ALL PASS` 且 `FAIL=0`，不是某个具体数字。

一键验收入口（本机脚本，`_scratch` 已 gitignore）：`pwsh -File _scratch\verify.ps1`（含构建 + 自检）。

可选环境变量：

- `SBMM_TEST_UE4SS_ZIP=<zip>`：用真实 UE4SS 包跑安装/部署测试（`_scratch/ue4ss-fixture.zip`）
- `SBMM_TEST_UPDATE=1` + `SBMM_TEST_UPDATE_PREPARE=1`：真实下载最新 Release 并校验/解压
- `SBMM_TEST_GITHUB=owner/repo`：解析 GitHub Release

测试用假游戏目录：`_scratch/fakegame`（需含 `SB.exe` 与 `SB/Content/Paks`），测试配置指向它，
避免动真游戏目录。截图：`python _tools/shot_existing.py _scratch/x.png`。

## 5. 架构要点

### 状态与依赖

- `AppState.Instance` 是唯一状态对象，实现 `IAppState`
- 服务通过 `AppServices.State` 取状态（组合根），**不要在服务里直接引用 `AppState.Instance`**
- 服务里的写法固定为 `private static IAppState State => AppServices.State;`（属性**实时读取**）。
  不要写成 `= AppServices.State` 的字段初始化 —— 那是初始化时抓快照，`AppServices.WithState`
  换掉的状态对服务不生效，测试会悄悄操作真实状态。
- 测试用 `AppServices.WithState(fake, () => ...)` 临时替换；视图页面统一
  `private static IAppState State => AppServices.State;`

### 状态文件写入（Services/FileSafe.cs）

- **所有用户可见的状态 JSON/文本都必须用 `FileSafe.WriteAllText`**，不要用 `File.WriteAllText`：
  后者先截断再写，进程被杀会留下半截 JSON，而读取端是严格反序列化 + 静默降级，
  表现就是「设置重置 / 方案消失 / 模组元数据被跳过」。
- `FileSafe` 的做法：同目录写 `*.tmp-xxxx` → `Flush(flushToDisk: true)` → `File.Replace`
  （不支持时回退 `File.Move(overwrite: true)`），并对杀软/索引器占用做退避重试。
- 覆盖范围：`config.json`、`deployed.json`、`.sbmm.json`、`profiles/*.json`、`deploy-journal.json`、
  `ue4ss/Mods/mods.json` 与 `mods.txt`、断点续传 `.meta`。
- 启动时 `FileSafe.CleanupStale(AppPaths.Root)` 清理被杀进程遗留的 `*.tmp-*`（只删超过 1 小时的）。
- 自检有 4 项守着这条约定（覆盖写入 / 不残留临时文件 / 清理陈旧 / 不误删在途文件）。

### 部署管线（Services/Deployer.cs）

- 模组类型 → 目标目录：Pak/Loose/Cns → `SB/Content/Paks/~mods`；Ue4ss、Ue4ssRuntime →
  `SB/Binaries/Win64`（ue4ss/Mods + dwmapi.dll）；IniTweak → `%LOCALAPPDATA%\SB\Saved\Config`；
  GameRoot → 游戏根目录；Video → `SB/Content/Movies`；LogicMod → `SB/Content/Paks/LogicMods`
- PAK 顺序前缀：`AppConfig.PakOrderPrefixEnabled` 时文件名加 `0001_`
- 同盘优先硬链接，失败回退复制；覆盖他人文件先备份，停用时还原
- 所有部署/停用/重建走同一把 `FileLock`；`Deployer.DeploymentChanged` 事件通知缓存失效
- `DeployJournal`：**部署和停用都写日志**（`Entry.Kind` = `deploy` / `undeploy`），启动时回放。
  - deploy 回放：目标还在 + 库里有该 Id → 接管进 `Deployed`；库记录没了 → 删残留
  - undeploy 回放：删残留文件 → 还原备份 → 修正 `deployed.json`（并把文件还给原主人）
  - 新增任何「先动文件后存状态」的操作，都必须套进 journal，否则崩溃后无法收尾
- 冲突预检：`Deployer.CheckConflicts(mod, gameDir, ignoreModIds?)` 只读、不改状态。同组版本之间
  的覆盖是预期行为，用 `ignoreModIds` 传入即将被替换的兄弟版本 id 予以忽略。
- 部署前校验游戏目录存在（不存在直接失败）

### 启用/停用状态机（Services/ModActions.cs）

- `Enable` 顺序：依赖检查 → **冲突预检（先于停用兄弟版本）** → 停用同 `GroupKey` 的其他版本 →
  部署。部署失败时按优先级把刚停用的旧版本重新部署回去，不会出现「新版本没装上、旧版本也没了」。

### 模组库 / 分组

- 库目录 `AppPaths.ModsRoot/<Id>`，元数据 `.sbmm.json`（`ModStore`）
- `GroupKey`：Nexus 模组用 `nxm:<id>`，否则名称归一化；同组只允许启用一个版本

### 更新（Services/AppUpdater.cs + Ui/DownloadFallbackController）

- 启动检查 GitHub Release（HTML 优先，API 兜底），下载 zip → SHA256 校验 → 解压 → PowerShell 替换后重启
- `ResumableDownloader`：`.part` + `.meta` 断点续传、3 次重试、416 自动重下、**先关闭文件流再 Move**
- 更新提示文案：只说明管理器自身更新，不要提示“关闭游戏”

### N 网访问

- REST v3：`https://api.nexusmods.com/v3`，需要 `apikey`；详情/文件/下载链接
- GraphQL：`https://api.nexusmods.com/v2/graphql`，**搜索、文件列表、标签可用匿名**
  - `game(domainName:)` 的 `id` 是**数字**，解析要兼容字符串/数字（曾导致文件列表为空）
  - `mods(filter: { gameDomainName, tag, name })`，`sort` 是**列表** `[ModsSort!]`
  - `modFiles(modId, gameId)` 可匿名取文件列表
- 详情描述是 **BBCode**，`Ui/HtmlToMarkdown` 同时处理 HTML 与 BBCode
- 标签筛选**区分大小写、完全匹配**；列表建议来自热门模组用过的标签
- 免费账号 API 下载返回 403 → 显示提醒条，3 秒后自动打开网页文件页（可“立即打开”）
- 已被作者删除的模组详情返回 403，属正常现象

### 崩溃捕获（GameCrashWatcher + CrashAnalyzer + CrashExporter）

- 运行时监控 `SB` / `SB-Win64-Shipping` 进程；退出后收集 WER（`Report.wer` 是 **UTF-16**）、
  转储、游戏日志，给出简要分析并保存到数据目录 `crashes/`
- 弹窗带免责声明；可导出 zip（源文件、模组环境、游戏日志、管理器日志）
- 非管理员创建开始菜单快捷方式会请求提权（`StartMenuShortcut`）

### 本地化

- `strings.json` 结构：`{ "zh-CN": { "中文原文": "中文原文" }, "en-US": { "中文原文": "English" } }`
  —— **中文原文就是 key**，改表即可改文案（含中文），无需改 XAML
- `Localization.Apply(root)` 遍历视觉树替换 `TextBlock/Button/InfoBar/ComboBoxItem/菜单/ToggleSwitch…`
- 时机很重要：`ContentFrame.Navigated` 后要 `page.Loaded` + `DispatcherQueue.TryEnqueue` 各应用一次；
  标题栏/导航要 `Apply(RootGrid)`（只 Apply NavView 会漏标题栏）
- **动态内容必须用 `Localization.ApplyDeferred(root)`**：DataTemplate 里的控件、代码重建的面板、
  以及 `Collapsed` 的 ScrollViewer 内容，在导航时那一次 Apply 里**还不存在**。
  `ApplyDeferred` = 立即 Apply + Low 优先级再 Apply 一次（等布局把子控件实例化）。
  已接入：模组详情面板（`UpdateDetails`）、合集页「已安装合集」列表（`RefreshInstalled`）；
  新增模板列表或代码重建 UI 时，填完数据要调一次，否则英文界面会残留中文。
- **`Apply` 逐节点隔离异常**：WinUI 对个别元素会抛 `ComboBox` 的 `COMException`（消息为空），
  早期用一个大 try/catch 包整棵子树，一处异常就让其后所有控件保持中文、日志只有一行空消息。
- ComboBox 选项、ToggleSwitch 的「开/关」需要单独处理（已实现）
- **代码里拼出来的 UI 文案不会自动翻译**：对话框标题/正文、`ShowMessage`、状态栏等要用
  `Localization.T("中文原文")` 包一层；含变量的用 `string.Format(Localization.T("…{0}…"), v)`
  （占位符按顺序编号）；原文同时补进 `zh-CN`（identity）与 `en-US`。
  `Log.*` 的字符串**不要**包（日志保持中文，便于排障）。
- **不要翻译「数据」**：持久化标签（`合集:` 前缀、`CollectionInstaller.LocalTag`）、`GroupKey`、
  文件路径、注册表键、URL、配置值，以及**用于比较的哨兵字符串**
  （如筛选下拉里的 `"全部角色"`，代码用 `StartsWith("全部")` 判断「不过滤」——翻了筛选就失效）。
- **ComboBox 的选中项要单独刷新**：收起状态的下拉框由缓存的 `SelectionBoxItem` 渲染，只改内部项
  `Content` 不会刷新可见文字。`Apply` 在检测到「当前选中项的文本被翻译过」后会重设一次
  `SelectedIndex`（-1 → 原值）来刷新；因此**下拉框的 `SelectionChanged` 处理器必须能容忍
  `SelectedItem == null` / `SelectedIndex < 0` 这一次瞬时事件**（否则会被误当成「跟随系统」「默认值」写回配置）。
- **运行时切换语言**：`MainWindow.ApplyLanguage` 会重新 `Initialize` 表 → 更新窗口标题 →
  `Apply(NavView)` → 让当前页实现 `ILanguageAwarePage` 的 `OnLanguageChanged()` 重渲染代码文案 →
  最后 `Apply(RootGrid)`（覆盖自绘标题栏）。
  新增页面如果在代码里拼文案（`XxxText.Text = ...`），**实现 `ILanguageAwarePage`** 并在钩子里调用
  该页自己的 `Refresh()/Reload()/LoadSettings()`，否则切语言后这些文字仍是旧语言。
- **`Original` 缓存只保留可翻译的原文**：切语言时页面会先用新语言重设文案，紧随其后的 `Apply`
  若沿用第一次缓存的旧字符串，会把旧语言写回去。现在缓存项不在表里时会被丢弃并重新采样。
- 已知残留（v1.14.2）：筛选下拉的**哨兵项**（「全部角色/全部类型/全部标签」）保持中文——代码用
  `StartsWith("全部")` 判断「不过滤」，翻译会破坏逻辑。
- `strings.json` 的 `zh-CN` 与 `en-US` 键集合必须**完全一致**（自检有一项专门守着）；
  只加 `zh-CN` 不加 `en-US` 会让英文界面回退显示中文。
- 新增文案：直接改 XAML/代码后，把中文原文加入 `zh-CN`（identity），需要英文时补 `en-US`

## 6. 常见开发任务

**新增页面**：`Views/XxxPage.xaml(.cs)` → `MainWindow.PageTypeForTag` 加 tag 映射 → 侧边栏加
`NavigationViewItem`（主页面的放 `MenuItems`，关于/反馈放 `FooterMenuItems`）。

**新增设置项**：`AppConfig` 加属性（带默认值，旧配置反序列化自动取默认）→ SettingsPage 加行 →
`_loading` 守卫下读/写并 `State.SaveConfig()`。

**新增服务**：放 `Services/`，状态用 `AppServices.State`，日志 `Log.Info/Warn/Error`，
网络/文件操作必须 try/catch + 超时；UI 不要从服务反引用（服务层禁止 `Views.`）。

**新增模组类型**：`Models.cs` 的 `ModType` 枚举 + `DisplayType` → `ModImporter` 识别 →
`Deployer.ComputeTargets` 目标映射 → `ModsPage` 类型筛选。

## 7. 高风险坑（务必先看）

1. **不要用 Python heredoc 生成 C#**：`\n`/`\r\n` 在多层引号里会被写成真实换行，导致
   “常量中有换行符”。优先用 `edit` 工具；必须脚本生成时用字节级替换并显式拼 `chr(92)`。
2. **文件是 CRLF**：逐行处理时注意 `\r`，用二进制读写避免意外转行。
3. 运行中的实例会锁 exe/`dist`：构建或打包前先结束 `SBModManager.exe`。
4. **沙箱/非管理员写不了开始菜单与 `%LOCALAPPDATA%`**：测试一律用 `SBMM_DATA_DIR`；
   开始菜单自检在不可写时会被跳过（真实环境才验证）。
5. 弹 `ContentDialog` 前要确认 `XamlRoot` 非空，否则 `ShowAsync` 抛“参数错误”；
   并且同一时刻只能有一个 ContentDialog（已有的用 `UiTask.Safe` 包裹）。
6. `async void` 事件处理器：新增时尽量 `try/catch` 或把工作交给已 catch 的方法；
   全局 `UnhandledException` / `TaskScheduler.UnobservedTaskException` 只兜底，不代替处理。
7. 翻译应用时机：只应用一次会漏掉懒加载控件（ComboBox 选项、模板内文字）。
8. 标题栏布局（改之前先读代码）：`DragRegion Padding=16` 对齐侧边栏图标；
   返回按钮 `Margin=10, Width=32`，标题让位 `TitleBackInset=34`，动画 220ms；
   返回按钮只在“非根页面且 CanGoBack”时显示。
9. `File.Move` 前必须释放写入流（否则“文件被占用”），并保留重试给杀软/索引器。
10. 不要在仓库提交任何 API Key / token；测试配置在 `_scratch/appdata*`，已 gitignore。
11. **写状态文件一律走 `FileSafe.WriteAllText`**，别用 `File.WriteAllText`：截断式写入 + 严格反序列化
    的组合会让一次崩溃变成「设置/方案/元数据消失」。新加的持久化文件也要遵守。
12. **服务里别用 `= AppServices.State` 初始化字段**：那是快照，`WithState` 换状态对它无效，
    自检会静默操作真实数据。统一写成 `=> AppServices.State` 表达式属性。
13. 动文件之前先写 journal（`DeployJournal.Begin` / `BeginUndeploy`），改完再 `Complete`；
    不要新增「改了文件但崩溃后无法回放」的路径。

## 8. 发布流程（每次都要走完）

1. 升版本：`SBModManager.csproj` 的 `<Version>`、`AboutPage.xaml`、`README.md`
2. 写 `_scratch/release/notes-vX.Y.Z.md`：**只写本版改动**，不带作者、不带历史
   - 体例：`## 剑星 MOD 管理器 vX.Y.Z` → `### 新增` / `### 修复` / `### 说明`
     （个别版本可加 `### 改进`、`### 内部改进`）
   - **总长 10～20 行**，一条一行；写「用户看到的现象 → 现在怎样」，
     不写实现细节（`Entry.Kind`、`ApplyDeferred`、`SelectionBoxItem` 这类留给自己看）
   - 只有主干功能加 `**粗体**`，其余不加
   - 可直接参考 `notes-v1.13.1.md`（6 行）、`notes-v1.13.3.md`（12 行）、`notes-v1.14.0.md`（18 行）
3. `.\build.ps1`（或上面的 dotnet build）→ 0 warning / 0 error
4. 暂存：把 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\*` 复制到
   `_scratch/distXYZ/SBModManager`，排除 `SBMM-Data`、`navcol.png`
5. 对暂存目录跑自检（必须 `RESULT: ALL PASS`、`FAIL=0`；项数会随功能增长，别卡死数字）
6. 打包 zip：顶层目录必须是 `SBModManager/`；生成 `SHA256SUMS.txt`
7. 提交版本号与说明（`chore(vX.Y.Z): 版本号与发布说明`）
8. 推送：`_scratch/release/GitAuth.ps1` 提供 `$global:GitAuthUrl`（token 内嵌 URL），
   `git push $global:GitAuthUrl main`，`git tag -a vX.Y.Z` 后 `git push -f $global:GitAuthUrl vX.Y.Z`
9. 创建 Release：环境变量 `SBMM_TAG` / `SBMM_TITLE` / `SBMM_NOTES`，
   运行 `_scratch/release/create_release_v110.py`（需要 `GH_TOKEN`，由 GitAuth 设置）
10. 校验：GitHub API 对比 asset 大小与本地 SHA256；`git ls-remote` 确认 main/tag 指向同一提交
11. 发布说明同时作为用户可见的 Release notes

`GitAuth.ps1` 若丢失（`_scratch` 被清理），重建逻辑：
优先 `gh auth token`，否则 `git credential fill` 取 github.com 密码，赋值 `$env:GH_TOKEN`，
并设置 `$global:GitAuthUrl = "https://x-access-token:$env:GH_TOKEN@github.com/xiangfa-test/Stellar-Blade-Mod-Manager.git"`。

## 9. 发版前 QA 清单

- [ ] 编译 0 warning / 0 error
- [ ] `--selftest` 全部 PASS（含 UE4SS fixture）
- [ ] 损坏数据：config.json / 模组元数据 / 部署日志 / strings.json / 无效 zip 均不崩溃
- [ ] 中英文界面各截图一次：导航、设置、模组列表、N 网详情
- [ ] 更新流程：检查更新 → 下载 → SHA256 → 解压 → 重启（可临时自建本地 Release 测）
- [ ] 部署/停用一次真实或假游戏目录，确认备份还原与 journal 正常
- [ ] 仓库无 `_scratch`、无 bin/obj、无密钥
- [ ] Release assets 大小/SHA256 与本地一致

## 10. 已知限制 / 待办

- 未做代码签名：SmartScreen 会提示；Release 提供 SHA256，可考虑 SignPath Foundation（GPL 免费）
- 没有单元测试框架，只有端到端自检；核心服务仍以 static 为主（`AppServices` 已收口状态，仍无 DI/接口）
- `FileLock` 只是进程内锁：便携版（`SBMM-INSTANCE_SUFFIX` 不同）与安装版可以同时跑，并同时写同一
  游戏目录 —— 需要跨进程锁时再处理（当前靠单实例 + 用户不要同时开两份来规避）
- 仍有多处 `async void` 事件处理器（53 处），统一 try/catch 已做，但可继续收敛为 `UiTask.Safe`
- 代码内 UI 文案（含插值串）已接入 `Localization.T`，运行时切换语言可整页刷新；仅剩筛选下拉的哨兵项保持中文（见「本地化」一节）
- FOMOD 不执行脚本安装器；资源冲突分析是近似（只读 `.utoc` 明文）
- 合集/批量下载逻辑在 `Views/ModsPage.Nexus.cs` 与 `Services/CollectionInstaller.cs` 各有一份且已分叉，
  改其中一处时**必须同步另一处**（待合并为单一实现）
- 下载免费账号限制依赖网页兜底，不做 key 抓取
- 更新检查串行；模组很多时较慢
- 无 CI（每次本地构建 + 手工发布）

## 11. 提交与协作约定

- 提交信息用中文，类型前缀：`feat:` / `fix:` / `refactor:` / `perf:` / `chore(...)`
- 一次提交只做一件事；大重构前先 `git commit` 存档
- 发版前确认工作树干净；`_scratch` 永不提交
- 用户要求“先存着”时只提交不发版，攒够再统一出补丁版
