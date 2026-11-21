# 文件卫士 FileGuard

[![CI](https://github.com/qxkt222/FileGuard-Windows/actions/workflows/ci.yml/badge.svg)](https://github.com/qxkt222/FileGuard-Windows/actions/workflows/ci.yml)

监视文件夹里被写入的文件，超过上限直接删除，用来硬性约束 AI 别把代码塞进一个巨型文件。

## 现在能做什么

- 建多个监视条目，一个条目接管一个文件夹
- 两种拦截模式，每个条目独立选择：
  - **监视模式（默认）**：写入完成后发现超限就删除，可配监视事件与豁免目录
  - **虚拟盘模式**：用 WinFsp 把目录挂成虚拟盘，超限写入**当场返回磁盘已满**，文件根本落不了盘
- 自动识别子文件夹，可多选豁免目录（仅监视模式）；默认只列直接子级，可展开全层级
- 单文件上限（默认 20 KB），监视模式超限文件立即删除并复制到隔离区；虚拟盘模式直接拒绝写入
- 可选监视事件：写入/修改（默认）、新建、删除、重命名（仅监视模式）
- 观察模式：不勾「限制」只记录不删（仅监视模式）
- 默认忽略 `.git`、`node_modules`、`dist`、`bin` 等目录和 `*.lock`、`*.tmp` 等文件；只对文本类扩展名生效，二进制文件不管
- 默认只拦「条目生效后新建的文件」，可在条目里开启「已存在的文件也检查」
- 托盘图标显示限制中/已暂停，超限弹通知（同一路径 3 秒节流；虚拟盘记录动作为 `Rejected`）
- 「暂停全部限制」会把虚拟盘一并卸载、目录还原成普通目录，恢复后重新挂载
- 拦截日志只保留最近 5 条在面板，文件按 1 MB 滚动
- 建条目时可在监视目录写 `.guard-rules.md`，让 AI 事先知道限额
- 设置：默认阈值、开机自启、启动最小化、非管理员限制、隔离区保留数、一键恢复默认
- 可选的进程守护服务（SYSTEM）：主程序被结束后 2 秒内自动拉起，普通用户停不掉服务，退出需要管理员批准

## 跑起来

- 安装包：`dist\FileGuard-0.1.1-setup.exe`（自带 .NET 运行时，捆绑 WinFsp；装到 Program Files）
- 已发布的程序：`publish\FileGuard.exe`（依赖本机 .NET 8 Desktop Runtime）
- 守护服务（可选）：`publish\service\FileGuard.Service.exe`
- 桌面快捷方式：`文件卫士.lnk`
- 数据目录：`C:\ProgramData\FileGuard\`
  - `config.json` 配置（`mode` 写成 `watch` / `virtualDisk`，旧配置缺省按 `watch` 读）
  - `quarantine\` 隔离区
  - `logs\intercept.log` 拦截日志
  - `logs\app.log` 运行日志（虚拟盘挂载/卸载/恢复失败等）

## 开发

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
& $dotnet build FileGuard.sln
& $dotnet test  FileGuard.sln
& $dotnet publish src/FileGuard.App/FileGuard.App.csproj -c Release -r win-x64 `
    --self-contained false -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

## 结构

```
src/FileGuard.Core/     监视引擎、虚拟盘生命周期、规则、隔离区、日志、配置、控制管道协议
src/FileGuard.WinFsp/   WinFsp 虚拟盘（挂载/卸载/残留恢复）
src/FileGuard.Service/  SYSTEM 守护服务（进程拉起 + 退出审批）
src/FileGuard.App/      WPF 界面 + 托盘
tests/FileGuard.Core.Tests/      监视引擎 + 虚拟盘集成测试
tests/FileGuard.WinFsp.Tests/    虚拟盘模块自身测试
tests/FileGuard.Service.Tests/   守护决策、管道协议、真实身份检查
installer/              Inno Setup 安装包脚本与构建脚本
```

## 安装包

```powershell
# 需要 Inno Setup 6；WinFsp 的 msi 会自动下载到 installer\redist\
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

产出 `dist\FileGuard-<版本>-setup.exe`，单文件自带 .NET 运行时，安装时按需静默安装 WinFsp。

- 装到 `C:\Program Files\FileGuard`，需要管理员权限
- 「进程守护服务」是安装向导里的复选框，**默认不勾**
- WinFsp 只在检测不到时安装，已装过的机器不会重装
- 卸载时按「停服务 → 结束程序 → 卸服务」的顺序清场；配置和隔离区会询问是否保留
- 捆绑分发的 WinFsp 是 GPLv2，安装目录里带 `WinFsp-LICENSE.txt`

## 进程守护服务

装了之后，非管理员从任务管理器结束 `FileGuard.exe`，服务会在 2 秒内把它拉回来；
服务本身普通用户停不掉（需要管理员）；退出程序要服务批准，非管理员批准不了。

发布服务：

```powershell
& $dotnet publish src/FileGuard.Service/FileGuard.Service.csproj -c Release -o publish\service
```

安装 / 卸载（需要管理员，或直接在「设置 → 进程守护」里点按钮走 UAC）：

```powershell
# 安装：把主程序绝对路径写进服务，并写入 HKLM\SOFTWARE\FileGuard\AppPath
publish\service\FileGuard.Service.exe --install "<仓库目录>\publish\FileGuard.exe"

# 卸载
publish\service\FileGuard.Service.exe --uninstall
```

服务名 `FileGuardGuard`，显示名「文件卫士守护」，`start= auto`。
运行日志在 `C:\ProgramData\FileGuard\logs\service.log`。

**注意**：UAC 过滤后的管理员（普通双击启动）在服务眼里也是非管理员，退不出去。
要退出请右键「以管理员身份运行」主程序后再点退出。

## 虚拟盘模式：手工验证步骤

前置：本机已安装 WinFsp（`C:\Program Files (x86)\WinFsp\`），并且 WinFsp.Launcher 服务在运行。
目录挂载不需要管理员权限。

1. 新建一个测试目录，例如 `D:\fg-test`，里面放一个 `seed.txt`。
2. 打开文件卫士 →「建立监视条目」→ 选 `D:\fg-test`。
3. 在第三步顶部选「虚拟盘模式（写入当场失败）」，上限填 `4` KB，确认。
4. 回到首页：条目卡片应出现「虚拟盘」标签，状态是绿色「拦截中」，明细显示「已挂载」。
   同时 `D:\fg-test` 会短暂改名：目录还在原路径，但属性里能看到它是 junction，
   磁盘上多出 `D:\fg-test.__guard_store`。
5. 用记事本或 PowerShell 写入一个 5 KB 文件：
   ```powershell
   [IO.File]::WriteAllBytes('D:\fg-test\big.bin', (New-Object byte[] 5120))
   ```
   预期报错「磁盘空间不足」（ERROR_DISK_FULL），`big.bin` 不会留下。
6. 托盘应弹出「文件超重，写入已拒绝」，首页「最近拦截」里这条记录的动作是 `Rejected`，
   `logs\intercept.log` 也应出现 `[Rejected]`。
7. 写一个 3 KB 文件应成功；卸载后文件仍在。
8. 点条目的「停用」：虚拟盘卸载，`D:\fg-test` 恢复成普通目录，`.__guard_store` 消失，
   原有文件都还在。再点「启用」应重新挂载。
9. 点「进入」→ 把模式改回「监视模式」，确认重建提示。之后超限文件的行为回到
   「写入后极短时间内删除」。
10. 挂载失败路径（不静默）：手工造一个「无法接管」的目录——
    在 `D:\fg-fail` 里放一个文件，另建 `D:\fg-fail.__guard_store` 也放一个文件
    （模拟另一个实例在占用或上次崩溃后残留）。
    然后在文件卫士里新建条目指向 `D:\fg-fail`，模式选虚拟盘，确认。
    预期：卡片红色「挂载失败」并给出原因（backing store 已存在），托盘弹错误气泡，
    `logs\app.log` 出现 ERROR 行；目录内容不被破坏。
11. 残留恢复路径：正常挂载后，在任务管理器结束进程（模拟崩溃），此时目录只剩
    `.__guard_store`。重新启动程序：会自动 `Recover` 再挂载，卡片显示「拦截中」，
    文件内容不丢。
12. 退出程序：所有虚拟盘卸载，磁盘上不应残留 `*.__guard_store`。

## 已知边界

- 监视模式下，用户态无法在写入发生前同步拦截。实际行为是「写入结束后极短时间内删除」：写入方多数已经拿到成功返回，文件随后消失。AI 下次访问该路径会报文件不存在。
- 需要「写入当场失败」请用虚拟盘模式；它要求目标目录可以被改名（不能是盘符根目录，也不能被其他进程独占）。
- 写入方如果还持有句柄（共享模式不含 Delete），监视模式的删除会重试最多 30 秒。
- 虚拟盘挂载期间目录是指向 `xxx.__guard_store` 的 junction；有不认识这个约定的程序可能会困惑。退出或停用条目后自动还原。
- 守护服务只守护进程，不接管拦截逻辑：主程序被结束后到重新拉起之间有一到两秒的空窗（服务每 2 秒巡检一次），这段时间里监视不生效。
- 虚拟盘条目在这空窗期目录处于中间状态（挂载点消失、`.__guard_store` 还在），重新拉起后会自动恢复并重新挂载。
- 未提权的管理员（UAC 过滤后的普通双击启动）在服务眼里是非管理员，退不出去；需要「以管理员身份运行」后再退出。
- 服务只拉活动控制台会话（`WTSGetActiveConsoleSessionId`），没有多会话 / RDP 支持。
- 开机自启写的是当前用户 Run 键；守护服务本身由 SCM 开机启动。

## 许可证

[Apache License 2.0](LICENSE)
