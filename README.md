# 剑星 MOD 管理器（SB Mod Manager）

基于 **WinUI 3 + .NET 8** 的《Stellar Blade（剑星）》模组管理器，原生 Fluent + Mica 界面。

当前版本 **v1.13.3** ｜ 协议 **GPL-3.0** ｜ [下载最新版](https://github.com/xiangfa-test/Stellar-Blade-Mod-Manager/releases/latest)

![模组管理](docs/screenshots/mods.png)

## 功能

- 自动检测 Steam 游戏目录（AppID 3489700），也可手动指定
- 导入 `.zip` / `.7z` / `.rar` / 文件夹 / `.pak`，内置 SharpCompress，无需 7-Zip，支持拖拽
- 启用 / 停用自动部署与撤销；库与游戏同盘时使用硬链接
- 自动识别模组类型并部署到正确位置：PAK、散装资源、CNS、UE4SS 运行库 / 脚本、INI、游戏根目录合并、主页视频
- CNS 服装识别：解析 json 显示服装名 / 角色 / 类型，支持搜索
- 加载顺序页：拖拽排序，点「应用顺序」按新顺序重建部署
- Nexus 合集：粘贴合集链接解析、切换版本、批量下载导入、按合集启用 / 停用
- 内置「本地安装」合集：手动导入的模组自动归入，可与他人合集来回切换
- UE4SS 一键安装：下载最新 Release 或选本地 zip，自动放置 `ue4ss` + `dwmapi.dll`
- 标签 / 备注（回车添加、快速添加已有标签）、多版本切换、更新检查、冲突链分析、资源级冲突分析（实验）
- 方案（Profiles）：保存「启用哪些模组 + 加载顺序」，一键切换
- 调试页：环境信息、运行自检、复制诊断、清理临时文件
- 预览图：自动读取模组包内图片并在详情显示
- 窗口大小记忆：首次调整后询问是否记住
- 首次启动可添加开始菜单快捷方式（之后可在「设置 → 高级」开关）
- 主页视频：含 `.bk2` 的压缩包自动识别为视频模组，部署到 `SB/Content/Movies`
- PAK 顺序前缀：给 `~mods` 里的 pak 加 `0001_` 序号，让加载顺序真正影响覆盖（可在设置关闭）
- FOMOD 选项安装：识别 `fomod/ModuleConfig.xml` 并弹出选项，只安装选中的文件
- CNS 依赖识别：读取 json 的 `Requirement` 字段并在详情中显示
- 应用内更新：检查 GitHub Release，下载并校验 SHA256 后自动替换文件并重启；下载时显示进度条
- 启动时自动检查更新（可在设置关闭），发现新版本时在顶部提示
- Windows 设置风格界面：页面 / 通知动画、滚动条自动隐藏、可拖动分隔条、亮暗主题

## 使用

1. 解压后运行 `SBModManager.exe`
2. 需要 [Windows App Runtime 1.8](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
3. 默认请求管理员权限；可在「设置」关闭，或启动时加 `--no-elevate`
4. 数据目录默认 `%LOCALAPPDATA%\SBModManager`，不可写时自动回退；也可用 `--data-dir` 或环境变量 `SBMM_DATA_DIR` 指定
5. 首次使用：确认游戏目录 → 导入模组或解析 Nexus 合集 → 需要脚本模组时先安装 UE4SS
6. 启用 / 停用前建议先退出游戏

## 从源码构建

需要 .NET SDK 8+、Windows 10 1809+、x64：

```powershell
.\build.ps1
```

首次运行会用 `tools\fetch_nuget.py` 把依赖下载到仓库内 `_nuget\feed`（离线包源），产物输出到 `dist\SBModManager`。

## 已知限制

- 仅支持 Windows x64；框架依赖模式，需要 Windows App Runtime 1.8
- 暂未做代码签名，SmartScreen 首次运行会提示；每个 Release 提供 SHA256 校验
- 不支持带密码的压缩包，请先用 7-Zip 解压
- 冲突检测以目标文件路径为单位，不做 PAK 合并，最终覆盖顺序由游戏决定
- 只有「纯视频包」（`.bk2` + 说明文件）才会按视频处理；与 pak/脚本混合的包按游戏根目录或原类型处理
- Nexus 更新检查只对带模组 ID（nxm 导入）的模组有效；合集批量下载需要 Premium，暂不支持 FOMOD 选项
- 以管理员身份运行时，Windows 会阻止从资源管理器拖拽文件（UIPI），需要拖拽请关闭管理员模式

## 作者

1. **[xiangfa-test](https://github.com/xiangfa-test)**（作者）
2. **[DeepSeek](https://github.com/deepseek-ai)**（辅助开发）

## 协议

本项目采用 **GPL-3.0**，详见 [LICENSE](LICENSE)。

第三方组件：[Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK)（MIT）、[SharpCompress](https://github.com/adamhathcock/sharpcompress)（MIT）。
