# 如何连接 Clash

适用版本：温度球 1.6.0。本文前半部分介绍已经实现的功能，最后一节是下一版设计方案，尚未实装。

## 先理解连接方式

温度球通过 Clash 的本机 HTTP 控制接口读取状态、切换模式和节点。它不内置 Clash，不提供订阅或节点，也不需要导入整份订阅。

连接链路：温度球 → 本机 Clash 控制接口 → 用户自己的策略组和节点。

控制接口端口与代理端口不是同一个东西。例如控制地址可能是 127.0.0.1:9090，而代理端口可能是 7890；这里只是示例，必须使用自己客户端的实际值。

## 1.6.0 如何自动连接

1. 先启动自己的 Clash for Windows，加载自己的订阅，并确认在 Clash 中可以正常使用节点。
2. 用同一个 Windows 账户启动温度球。若管理员提示要求输入另一账户，程序读取的用户目录可能随之改变。
3. 温度球自动查找当前运行账户目录下的文件：%USERPROFILE%\.config\clash\config.yaml。
4. 从这个文件读取 external-controller 和 secret；通过本机控制接口取得模式、代理端口、策略组和节点。端口或密钥不写死在程序里。
5. 点击小球旁边的蓝色网络点，等待约 5 秒。能够显示模式和节点，说明控制连接已经建立。

当前只有这一处配置文件的自动发现入口。安装包位于 C 盘还是 D 盘不影响发现路径。1.6.0 没有手动填写地址、选择其他配置文件或导入订阅的页面。

其他 Clash / Mihomo 客户端即便提供相似 API，也可能使用不同的配置目录、命名管道或认证方式，不能直接承诺安装即用。本版只验证了 Clash for Windows；不建议为了兼容温度球而更换正在使用的客户端。

### 配置字段示意

以下仅用于理解字段，不是完整配置，也不要用它覆盖自己的配置文件：

    external-controller: 127.0.0.1:9090
    secret: "替换成自己设置的控制密钥"

地址采用“主机:端口”格式。密钥与控制接口必须匹配。配置变更应通过客户端支持的设置方式完成，客户端可能重新生成配置文件。

温度球仅向本机回环地址发送控制请求，支持 127.0.0.1、localhost 和 IPv6 回环地址。它不支持远程控制地址、HTTPS 控制接口或仅命名管道接口。控制请求不走代理、不跟随重定向；密钥仅用于本机认证，不写入日志或随安装包分发。无需为此开启局域网访问或把控制端口暴露到公网。

## 模式按钮具体做什么

| 按钮 | 实际行为 |
| --- | --- |
| 全局 | 将 Clash 模式设为 global，并开启当前用户的 Windows 系统代理，指向 Clash 返回的本机 HTTP / mixed 端口。 |
| 规则 | 将 Clash 模式设为 rule，并开启相同的系统代理；具体请求由 Clash 规则分流。 |
| 关闭 | 将 Clash 模式设为 direct，同时关闭当前用户默认 LAN 连接的 Windows 系统代理/PAC/自动检测标志；Clash 进程留在后台。 |

程序不会因打开网络页而切换模式。点击后会重新读取状态，确认后显示结果。关闭不等于退出 Clash，也不等于关闭所有第三方 VPN、TUN 或应用自带代理。本版检测到 Clash 的 TUN 开启时会禁用切换，请在客户端中自行管理 TUN。

切换不强制断开现有连接，新连接按新的配置处理；退出温度球不会恢复之前的代理设置。客户端若同时修改设置，以实际回读结果为准。

## 为什么我没有“美国 Y01 / Y02”

这两个名字不是随软件附送的节点。

1.6.0 从用户自己的节点名称中匹配“美国”、United States、USA 或美国国旗标记，最多取两个，优先匹配美国 Y01 / Y02。节点名称只用于筛选，不代表软件验证了实际地理位置。

快捷切换还要求 GLOBAL 与主要规则 Selector 组都包含这两个节点。主要规则组从最后的 Match / Final 规则出发，沿当前选择链寻找可切换的组。满足条件时，一个按钮会同时修改这两个组；其他应用专用组不直接修改。

因此，节点命名或策略组结构不同，按钮可能不可用。这是本版的适配限制，不代表用户的 Clash 配置有问题。暂时可以继续在 Clash 内切换自己的节点。代理关闭时点击可用的节点按钮，仅预选节点，不会开启代理。

## 网速和延时分别代表什么

- 网速：读取一个主网络接口的上传/下载计数，每秒更新。不是“所有流量都经过代理”的证明。
- 延时：约每 15 秒通过当前解析到的节点请求 https://www.gstatic.com/generate_204；它是到测试站点的连接请求用时，不是所有网站或游戏的延时。
- 规则模式下默认沿兜底策略组解析节点，其他网站可能走不同路线。
- 暂停延时检查后，网速仍然更新。失败或无法确定唯一节点时显示不可用，不用 0 ms 代替。

## 常见问题

| 现象 | 排查方法 |
| --- | --- |
| Clash 控制暂不可用 | 检查客户端和内核是否运行、当前账户下的上述配置文件是否存在、控制地址和密钥是否是当前值。 |
| Clash 可上网，但温度球无法连接 | 代理端口能工作不等于控制 API 可用。检查 external-controller，不要把 mixed-port 当作控制端口。 |
| 换一个 Clash 客户端就不识别 | 1.6.0 只自动查找固定的用户目录，尚无通用手动连接入口。 |
| 模式按钮可用，节点按钮灰色 | 检查是否有两个匹配的美国节点，以及 GLOBAL 和主要规则组是否同时包含它们。 |
| 提示 TUN 不支持 | 当前版只控制默认 LAN 系统代理，不能据此判断 TUN 已关闭。请在 Clash 中管理 TUN。 |
| 模式显示“状态待确认” | Clash 模式与 Windows 系统代理设置可能不一致，或系统代理指向其他软件；检查两侧实际状态。 |
| 节点延时不可用 | 测试站点或节点可能暂时不可达，也可能当前组为 DIRECT、REJECT 或无法解析唯一节点。 |

## 下一版建议：增加“连接设置”（尚未实现）

建议在“网络与节点”页增加一个小齿轮，采用以下流程：

1. **选择连接方式**：默认自动发现，也允许手动连接本机控制接口。自动发现失败时直接展示手动入口。
2. **填写控制地址与密钥**：地址示例 http://127.0.0.1:9090；密钥使用密码输入框。提供“测试连接”，只读取版本、配置和策略组，不改变代理。
3. **选择策略组**：成功后显示客户端实际返回的可手动选择组，让用户明确指定规则模式控制哪个组。全局模式显示 GLOBAL。提供“同时更新全局和规则组”的明确选项。
4. **选择常用节点**：从所选组的真实成员中搜索、固定两个或更多快捷项，取消美国地区和 Y01/Y02 命名限制；不允许选择该组不包含的节点。
5. **保存连接**：控制地址和组名保存在本机；密钥使用 Windows 当前用户范围的 DPAPI 加密保存，支持清除。导出配置时默认排除密钥。
6. **显示连接状态**：区分未启动、端口不通、认证失败、API 不兼容、策略组失效；订阅更新后重新验证已固定节点，不偷偷切换到其他节点。
7. **按能力开放控制**：读取和测速可独立启用；模式/节点控制只在确认兼容后开放。TUN、命名管道、远程控制先不纳入首版手动连接范围。

可增加“从本地配置文件读取连接信息”的便捷入口，但只提取控制地址和密钥，不上传、不复制完整订阅、不替用户修改 Clash 文件。

这样每个人仍在自己的 Clash 中管理订阅，温度球负责展示状态和提供快捷控制。实现验收应覆盖错误密钥、客户端重启后端口变化、组/节点消失、断网、切换失败、密钥加密及日志脱敏，并用不同客户端实际验证兼容范围。

## 技术参考

当前行为以本仓库 1.6.0 的 src/ClashMonitor.cs、src/ProxyControl.cs 和 src/SystemProxy.cs 为准。

控制字段可参照 [Mihomo 官方全局配置文档](https://wiki.metacubex.one/config/general/)；接口定义参照 [Mihomo 官方 API 文档](https://wiki.metacubex.one/api/)。这些资料解释协议，不代表温度球已完成所有客户端兼容。
## 自己修改源码 ZIP 并生成 EXE

如果你的客户端无法自动识别，可以下载源码自行适配。本项目提供源码和构建说明；不同客户端的适配、调试和后续维护由修改者自行完成。

### 1. 下载和解压

在本 Release 的 Assets 中下载 **ThermalDot-1.6.0-Source-With-Guide.zip**，这是补充本说明后的源码快照。原始 ThermalDot-1.6.0-Source.zip 及 GitHub 自动生成的 Source code (zip) 对应原发布内容，不含后来补充的说明。

把源码包完整解压到例如 D:\Software\ThermalDot-Source。确认该目录下直接有 Build.ps1、setup.iss、src 和 components。不要在压缩软件预览窗口里编辑，也不要修改安装包中的 EXE。

修改的是解压后的 C# 源码，完成后重新编译；改 ZIP 或 YAML 本身不会自动生成新的程序。

### 2. 按自己的需求改对应文件

| 需求 | 文件与位置 |
| --- | --- |
| 更换自动发现配置文件的位置 | src/ClashMonitor.cs 的 ClashEndpoint.Discover() |
| 调整控制地址/密钥的读取方式 | 同文件的 Parse()、Scalar()、Discover() |
| 更换快捷节点筛选方式 | src/ProxyControl.cs 的 Parse() 中 state.Nodes 和 NodeLabel() |
| 改全局、规则策略组选择逻辑 | src/ProxyControl.cs 的 Selectable、ruleGroup、Selections |
| 增加连接设置、节点按钮或文案 | src/NetworkView.cs |
| 为自定义逻辑增加验证 | src/ProxyControlTests.cs、src/NetworkTests.cs |

**最小改动：客户端已有本地 YAML，且包含实际生效的 external-controller 与 secret。**

在 Discover() 中找到计算 file 的那一行，用自己的真实配置文件位置替换，例如：

~~~csharp
string file = Environment.ExpandEnvironmentVariables(
    @"%APPDATA%\YourClashClient\config.yaml");
~~~

YourClashClient 是占位名称，不是任何客户端的已验证路径。先在自己的客户端中查清实际文件位置；保留后面的存在性、大小检查和 Parse 调用。只复制一份可能过期的配置会造成端口或密钥变化后失联。

若客户端没有符合格式的文件，可在 Discover() 中改为读取自己保存在本机的独立“连接信息文件”，文件只包含控制地址和密钥。例如将该方法原本的 file 路径改成：

~~~csharp
string file = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ThermalDot", "clash-connection.yaml");
~~~

然后在这个目录自行创建 clash-connection.yaml：

~~~yaml
external-controller: 127.0.0.1:9090
secret: "这里填自己的实际控制密钥"
~~~

这只是**自己修改源码后的接入方法**，官方 1.6.0 不会自动读取这个文件。填写地址不会在 Clash 内开启 API，必须先确认客户端确实监听此地址。不要把个人密钥、订阅地址或节点凭据写死在源码、上传 GitHub 或装进分享 ZIP；上述独立文件属于本机私有文件。通用设置界面的加密保存方案仍见上一节。

这里只支持现有代码能处理的本机 HTTP API。保留回环地址校验、禁用代理及重定向的保护。HTTPS、命名管道以及不同结构的配置格式需要额外实现，不能只换路径就宣称兼容。

**如果只想换快捷节点：**在 state.Nodes 的查询中将美国名称匹配改为自己的明确名称集合，保持最多两个，并同步调整 NodeLabel() 的显示文字。还需确认 GLOBAL 与主要规则 Selector 都包含所选节点；只改按钮文字不会改变实际目标。若要支持任意数量，需要同时修改 NetworkView.cs 的固定两按钮布局和事件处理。改完补充对应测试，不要让测试仍只验证旧名称。

### 3. 准备构建工具

在 Windows x64 上安装 **.NET 10 SDK**，不要只装 Runtime。项目发布时使用 SDK 10.0.400；源码没有锁定 SDK 补丁号，其他 .NET 10 SDK 需自行验证。首次构建需要联网恢复 NuGet 包。

- 官方 .NET SDK 下载：https://dotnet.microsoft.com/download/dotnet/10.0
- 仅生成单文件 EXE 不需要 Inno Setup。
- 需要安装向导时另装 Inno Setup 6；原发布使用 6.7.3。官方站点：https://jrsoftware.org/isinfo.php

在 PowerShell 中运行 dotnet --list-sdks，确认能看到 10.0.x。若命令找不到，重新打开 PowerShell，或使用 dotnet.exe 的完整路径。

### 4. 只生成可执行文件

以下示例假设源码完整解压在 D:\Software\ThermalDot-Source。在 PowerShell 中执行：

~~~powershell
Set-Location 'D:\Software\ThermalDot-Source'
$env:DOTNET_CLI_HOME = "$PWD\.build-cache\dotnet-home"
$env:NUGET_PACKAGES = "$PWD\.build-cache\nuget-packages"
$env:NUGET_HTTP_CACHE_PATH = "$PWD\.build-cache\nuget-http-cache"
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = "$PWD\.build-cache\bundle-cache"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

dotnet restore .\src\ThermalDot.csproj --locked-mode
if ($LASTEXITCODE -ne 0) { throw '依赖恢复失败，请先处理错误' }

dotnet publish .\src\ThermalDot.csproj --no-restore -c Release -o .\publish
if ($LASTEXITCODE -ne 0) { throw '编译失败，请先处理错误' }
~~~

输出文件：**publish\ThermalDot.exe**。

项目已经配置 win-x64、自包含运行时和单文件发布，接收者无需另装 .NET。不要从 src\bin 中随意挑一个 EXE 分享。CPU 温度所需的 PawnIO 和硬件兼容条件仍然存在，单文件并不意味着免驱动。

### 5. 验证自己的修改

先退出旧温度球，否则同账户单实例逻辑可能只唤回旧进程。自检可以这样执行：

~~~powershell
$p = Start-Process '.\publish\ThermalDot.exe' -ArgumentList '--self-test' -Wait -PassThru
if ($p.ExitCode -ne 0) { throw '自检失败' }
~~~

自检主要验证逻辑，不代替真实客户端测试。再启动自己的新 EXE，确认控制连接、模式回读、正确的组/节点、错误密钥提示和客户端重启后恢复。测试切换前保存当前代理状态，完成后自行恢复需要的状态；不要只看到按钮变色就认定成功。

若将代码改成不同的节点/组规则，应同步修改测试用例验证新行为，而不是删除失败的检查。不要把测试产生的 preferences.json、network.json、日志或连接密钥打进源码包。

### 6. 生成安装包（可选）

确认自己的 Inno Setup 实际安装路径，以下只是常见路径示例。在源码根目录执行：

~~~powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -DotNet "dotnet" -InnoCompiler "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" -CacheRoot "D:\Software\ThermalDot-Source\.build-cache"
~~~

这是针对本次构建进程的执行策略，不修改系统全局执行策略。Build.ps1 会先校验附带的官方 PawnIO 安装程序，再恢复依赖、发布 EXE 和编译安装包。

输出：**release\温度球-1.6.0-安装版.exe**。

找不到 ISCC.exe 时，修正实际路径；缺少组件或校验不匹配时，重新取得完整源码包，不要删掉校验来绕过问题。修改 C# 源码不会自动更新版本号；若要另发自定义版本，应同步调整 csproj、Program.cs、Distribution.cs、setup.iss、Build.ps1 和说明中的版本信息，并明确注明为自行修改版。

原 setup.iss 使用固定 AppId。自用更新可以保留；如果希望与官方版并存，需要另行调整安装 AppId、程序身份、数据目录和单实例名称，不要以为仅改 EXE 文件名即可并存。分享时保留随包的第三方版权和许可说明。

### 构建失败时

- 没有 .NET 10 SDK：安装正确 SDK，再打开一个新的 PowerShell。
- NuGet 恢复失败：检查网络与错误信息；保留 packages.lock.json，不要随意升级依赖绕过问题。
- EXE 被占用：退出正在运行的旧程序再构建。
- 编译成功但无法连接：重新检查实际控制地址、密钥、配置格式与客户端 API；这属于适配问题，不是压缩包问题。
- 双击仍显示原来的界面：确认启动的是新生成的 publish\ThermalDot.exe，且旧进程已退出。