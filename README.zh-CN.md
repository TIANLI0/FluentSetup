# FluentSetup

**轻量级、原生 Windows 11 风格的安装包制作工具。** 用一个 JSON 文件描述你的应用，FluentSetup 生成单个 `Setup.exe`。它负责安装、升级、回滚和卸载，界面是 Fluent 风格的 WinUI 3。安装时不需要 .NET、VC++ 运行库、WebView2，也不联网。

[English](README.md)

![FluentSetup 安装界面（WinUI 3）](docs/images/setup-winui.png)

## 特性

- **体积小**：典型应用的安装器开销不到 1 MiB（stub + WinUI 界面 + 元数据），CI 中按 5 MiB 预算自动检查。详见 [docs/size-report.md](docs/size-report.md)。
- **原生、自包含**：C++20 引导程序，静态链接 VC 运行时，内置 zstd 解码器。系统装有 Windows App Runtime 时使用 WinUI 3 界面，否则自动切换到布局相同的 Win32 界面，不会循环重启，也不会重复执行有副作用的步骤。
- **安装可靠**：预写日志加被替换文件的备份，失败或取消时逐字节回滚；拒绝不安全的安装路径；逐文件 SHA-256 和 Authenticode 校验，能发现损坏或被篡改的安装包；提权由独立的 worker 进程完成，它只安装自身内嵌的安装包。
- **完整生命周期**：按用户或按机器安装，升级、重新安装、防降级，注册到“应用和功能”，快捷方式，可自删除的 `uninstall.exe`，静默模式，还能接管由其他安装程序安装的同一应用。
- **可直接替换 Inno Setup 和 NSIS**：原地接管它们的安装，并认得应用内更新已在用的命令行（`/SILENT /RELAUNCH=1`、`/S /UPDATE`、`/DIR=`、`/TASKS=` 等），应用发一个小版本即可切换。见[四个真实的迁移案例](docs/thrm.md)。
- **前置条件版本检查**：.NET Framework、.NET 运行时、WebView2、Windows App Runtime 都可设置最低版本；缺失或版本过低时，用随包安装程序或可选的 https 下载（SHA-256 校验）安装/升级，否则停止并给出帮助链接。
- **声明式扩展**：关闭正在运行的进程；必需（锁定）组件与可选组件；安装后或卸载前运行安装包内的程序（支持失败后的备用方式）；NGen；PATH；清理旧版本的计划任务、Run 启动项、快捷方式、文件、注册表项和卸载项。
- **内置更新**：在项目里指定 GitHub Releases 或 JSON 源，已安装的应用即可自行更新：`uninstall.exe /install-update` 负责检查、询问、下载、校验（SHA-256、同一应用、更高版本、可选的签名者）并安装；`/check-update` 只检查。应用里无需编写更新代码。
- **多变体**：一个安装包可同时带自包含版和依赖共享运行时的版本（相同文件只存一份），安装时由用户选择，或用 `--variant` 只打其中一种。
- **无障碍与本地化**：内置英文、简体中文、日文、韩文，跟随 Windows 显示语言；其他语言可由安装包自行补齐（`fluentsetup locale add fr-FR`）；UI 自动化名称、键盘导航、浅色/深色/高对比度主题、按显示器 DPI 缩放。
- **三种制作方式**：图形化 Builder 应用、`fluentsetup` 命令行（也提供 GitHub Action），或直接调用 `Builder.Core` 库。
- **便于 AI 编写**：`fluentsetup guide` 输出完整的编写指南，`fluentsetup schema` 输出 JSON Schema，`fluentsetup validate --json` 给出带代码和字段路径的诊断。

## 快速开始

### 1. 安装 FluentSetup

从[最新发布](https://github.com/TIANLI0/FluentSetup/releases/latest)下载：

- **`FluentSetup-<版本>-Setup.exe`**：Builder 图形界面加 `fluentsetup` 命令行（自动加入 PATH）。缺少 .NET 10 运行时或 Windows App Runtime 时安装程序会自动安装。需要 Windows 10 1809 或更高版本。
- **`fluentsetup-<版本>-win-x64.zip`**：只有命令行，自包含（不需要 .NET），解压到任意位置即可，适合构建服务器和脚本。

FluentSetup 会自动更新：Builder 每天检查一次新版本（可在“设置 > 更新”中关闭），点击**更新**即可安装；命令行用 `fluentsetup update` 更新，`fluentsetup update --check` 只检查不安装。下载的文件会用发布附带的 `SHA256SUMS.txt` 校验。

### 2. 描述你的应用

在 **Builder** 中选择“新建项目”，选中应用的构建输出文件夹（例如 `bin\Release\publish`），填写名称、ID 和发布者，然后依次设置文件、安装选项、必备组件、选项与动作、升级、卸载和外观。输入时问题会实时列出。

也可以用命令行：

```powershell
fluentsetup init my-app --name "My App" --id Contoso.MyApp --source C:\build\MyApp\bin --publisher Contoso
```

两种方式都会生成 `fluentsetup.json`。最简单的项目文件如下：

```json
{
  "$schema": "https://raw.githubusercontent.com/TIANLI0/FluentSetup/main/shared/schemas/fluentsetup.project.schema.json",
  "schemaVersion": 1,
  "app": { "id": "Contoso.MyApp", "name": "My App", "publisher": "Contoso", "version": "1.0.0", "mainExecutable": "MyApp.exe" },
  "files": { "source": "bin", "include": ["**/*"], "exclude": ["**/*.pdb"] },
  "install": { "scope": "userOrMachine", "defaultScope": "user", "desktopShortcut": "optionalOff", "launchAfterInstall": "optionalOn" },
  "ui": { "license": "LICENSE.txt" },
  "output": { "fileName": "{AppName}-{Version}-Setup.exe", "compression": "normal" }
}
```

所有设置项见 [docs/project-file.md](docs/project-file.md)（英文）；`fluentsetup guide` 会输出完整的编写指南，也适合交给 AI 助手。

### 3. 生成 Setup.exe

在 Builder 中打开“构建”页并开始构建，或者：

```powershell
fluentsetup validate my-app\fluentsetup.json          # 列出错误和警告（带代码和字段路径）
fluentsetup build my-app\fluentsetup.json             # -> my-app\dist\My App-1.0.0-Setup.exe
fluentsetup inspect "my-app\dist\My App-1.0.0-Setup.exe" --verify
```

直接运行生成的安装包试一试，或无人值守测试：`Setup.exe /quiet /log setup.log`，再用 `"<安装目录>\uninstall.exe" /uninstall /quiet` 卸载。

### 4. 在 CI 中打包

GitHub Action 会在 Windows 构建机上安装命令行工具（下载后校验 SHA-256）：

```yaml
- uses: TIANLI0/FluentSetup@v1.0.1
- run: fluentsetup build installer/fluentsetup.json -D Version=1.2.3
```

`-D Version=1.2.3` 会填入项目文件里的 `$(Version)`（例如 `"version": "$(Version)"`），版本号由发布流程决定。

### 从 Inno Setup 或 NSIS 切换过来

保留应用原来的卸载项（`install.adoptExistingInstall` 或 `install.legacyUninstallKeys`）、应用内更新下载的安装包文件名（`output.fileName`）和它使用的命令行，已有安装（包括从旧版本内发起的更新）就会原地升级。[THRM](https://github.com/TIANLI0/THRM)、[EncoDeck](https://github.com/TIANLI0/EncoDeck)、[PinyinFlux](https://github.com/TIANLI0/PinyinFlux) 和 [MediaDuet](https://github.com/TIANLI0/MediaDuet) 的做法见 [docs/thrm.md](docs/thrm.md)。

## Setup.exe 命令行

供最终用户和管理员使用：

```
Setup.exe [/quiet | /S | /passive] [/uninstall] [/dir <路径>] [/scope user|machine] [/relaunch]
          [/desktop-shortcut yes|no] [/start-menu-shortcut yes|no] [/option <id>=yes|no]
          [/log <文件>] [/lang en-US|zh-CN|ja-JP|ko-KR] [/ui auto|winui|win32]
uninstall.exe /check-update | /install-update [/passive | /quiet] [/relaunch]   （配置了更新来源时）
```

退出代码 0 表示成功，其余代码见 [docs/diagnostics.md](docs/diagnostics.md)。也支持 Inno Setup 和 NSIS 的常用参数（[详情](docs/project-file.md#setupexe-command-line)）。

## 文档

（详细文档为英文。）

- [项目文件参考](docs/project-file.md)
- [从 Inno Setup / NSIS 迁移：案例](docs/thrm.md)
- [诊断与退出代码](docs/diagnostics.md)
- [安全](docs/security.md)
- [架构](docs/architecture.md)
- [安装包格式](docs/package-format.md)
- [界面/worker 通信协议](shared/protocols/ui-protocol.md)
- [体积报告](docs/size-report.md)
- [更新日志](CHANGELOG.md)

## 开发

从源码构建 FluentSetup 需要 Windows 10 1809 及以上（推荐 Windows 11）、Visual Studio 2022 或更高版本（含“使用 C++ 的桌面开发”工作负载）以及 .NET 10 SDK。

```powershell
git clone https://github.com/TIANLI0/FluentSetup.git
cd FluentSetup
.\scripts\build.ps1                       # 原生运行时、WinUI 界面、命令行、Builder、测试
.\scripts\package-release.ps1             # 生成发布文件，输出到 out\release
```

源码构建的命令行位于 `src\CLI\bin\Release\net10.0-windows10.0.19041.0\fluentsetup.exe`。测试打包使用的示例应用是 [samples/DemoApp](samples/DemoApp)。

### 测试

```powershell
.\out\bin\x64\Release\FluentSetup.Tests.Native.exe
dotnet test tests\Builder.Core.Tests -c Release
.\tests\Integration\e2e.ps1          # 真实的按用户安装：升级、回滚、卸载等
.\tests\UI\ui-tests.ps1              # 用 UI 自动化驱动两套界面
.\scripts\size-report.ps1
```

端到端测试和 UI 测试只为当前用户安装并移除一个测试应用，不需要管理员权限。想在测试时继续正常使用电脑，可以让它们在一个看不见的桌面上以最低优先级运行：窗口不会出现在你的屏幕上，也不会抢鼠标和键盘（UI 测试只通过 UI 自动化和窗口消息操作界面）：

```powershell
.\scripts\run-hidden.ps1 -Command 'powershell -NoProfile -File tests\UI\ui-tests.ps1' -Log out\ui-tests.log
```

发布：推送 `vX.Y.Z` 标签，并在 [CHANGELOG.md](CHANGELOG.md) 中写好对应的 `## [X.Y.Z]` 段落，发布流程会自动构建、测试并发布上述文件。

## 许可

[MIT](LICENSE)。第三方组件见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
