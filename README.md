# iBMC KVM

iBMC KVM 是一个面向 Windows x64 的 Huawei iBMC 远程控制台客户端，使用
.NET 9 和 WPF 构建。项目提供 KVM 视频、键盘鼠标、虚拟媒体、机箱刀片管理、
录像和断线恢复等常用运维能力。

> 电源控制、虚拟媒体写入和 USB 重置会改变服务器状态。执行前请确认当前目标、
> 账户权限和业务影响。

## 功能

### 远程控制台

- 解析 64x64 JPEG/RLE 视频块、差分帧和分辨率变化。
- 支持 8/7/6/4 位颜色深度和运行时图像清晰度调整。
- 支持绝对、相对和捕获鼠标模式，以及本地指针显示和鼠标同步。
- 使用有序 USB HID 键盘报告，支持快速输入、长按、修饰键、锁定键状态灯、
  组合键、自定义六键组合和键盘复位。
- 提供美国、日语和法语键盘布局。
- 支持截图、全屏显示、`.rep` 录像和 Motion JPEG AVI 导出。

### 会话与安全

- 支持共享控制、独占控制和只读监视会话。
- 支持 HTTPS、RMCP+/OEM、加密 KVM、iMana 会话和运行时能力协商。
- 提供有界的自动重连，并在 KVM 恢复后恢复已挂载的虚拟媒体。
- 自签名证书必须经过指纹确认；服务器证书和 CA 信任可以检查和撤销。
- 可选择记住连接。凭据仅以当前 Windows 用户可解密的 DPAPI 数据保存。
- 不安装系统级键盘或鼠标钩子，网络、解码和文件任务不阻塞 WPF 界面线程。

### 虚拟媒体与机箱

- 软驱支持镜像和物理驱动器，默认启用写保护。
- 光驱支持 ISO、物理驱动器和本地目录；目录会生成临时 Joliet ISO。
- 软驱和光驱可以同时挂载，并支持更换、弹出和重连恢复。
- 支持 UFI 和 SFF-8020i 命令、介质变更状态及可选 AES-CBC 数据保护。
- 支持最多 14 个机箱槽位、四路并发会话、刀片标签页和只读 2x2 分屏。
- 电源操作和 USB 重置都需要显式确认，并受当前账户权限约束。

## 兼容性

项目包含多种 iBMC 和 iMana 协议配置，具体功能由登录协商、固件能力和账户权限
共同决定。不同服务器型号和固件版本可能只支持上述功能的一部分。

提交兼容性问题时，请提供服务器型号、固件版本、连接方式、可复现步骤和脱敏后的
错误信息。不要附带密码、会话密钥、证书私钥或可访问的管理地址。

## 环境要求

- Windows 10 或 Windows 11 x64
- .NET 9 SDK
- 可访问的 iBMC 管理地址
- 具备所需 KVM、虚拟媒体或电源权限的账户

## 构建与运行

还原依赖并构建整个解决方案：

```powershell
dotnet restore IbmcKvm.slnx
dotnet build IbmcKvm.slnx --configuration Release
```

从源码运行桌面客户端：

```powershell
dotnet run --project src/IbmcKvm.App/IbmcKvm.App.csproj --configuration Release
```

生成自包含的 Windows x64 发布目录：

```powershell
dotnet publish src/IbmcKvm.App/IbmcKvm.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true
```

默认构建产物位于
`src/IbmcKvm.App/bin/Release/net9.0-windows/win-x64/`。

## 使用说明

1. 在登录窗口输入 iBMC 主机名、IP 地址或 HTTPS 地址及账户凭据。
2. 选择共享控制或独占控制。遇到自签名证书时，先核对主题、颁发者、有效期和
   SHA-256 指纹。
3. 连接后将鼠标移入远端画面并使画面获得焦点。左下角输入状态变为绿色后，键盘
   和鼠标输入才会发送。
4. 使用顶部工具栏调整鼠标模式、清晰度、颜色深度、键盘、录像、虚拟媒体和电源。
5. 移出画面、窗口失焦或切换刀片时，客户端会释放远端按键和鼠标按钮。

保存连接不是默认行为。启用“记住此连接”后，配置写入
`%LOCALAPPDATA%\IbmcKvm\connection-settings.bin`，并由 Windows DPAPI
按当前用户加密。取消该选项或使用“清除已保存设置”会删除文件。

关闭虚拟媒体窗口不会自动弹出介质；断开 KVM 会话会关闭虚拟媒体连接并清理临时
目录镜像。

## 项目结构

```text
src/
  IbmcKvm.Protocol/   登录、协议帧、密码学和传输
  IbmcKvm.Core/       会话、视频、输入、录像和虚拟媒体
  IbmcKvm.App/        WPF 桌面界面
tests/
  IbmcKvm.Protocol.Tests/
  IbmcKvm.Core.Tests/
  IbmcKvm.App.Tests/
  IbmcKvm.DesktopSmoke/
docs/
  adr/                已采用的架构决策
```

## 测试

运行自动化测试：

```powershell
dotnet test IbmcKvm.slnx --configuration Release
```

桌面冒烟测试会打开本地 WPF 窗口并生成截图：

```powershell
dotnet build tests/IbmcKvm.DesktopSmoke/IbmcKvm.DesktopSmoke.csproj `
  --configuration Release
./tests/IbmcKvm.DesktopSmoke/bin/Release/net9.0-windows/win-x64/IbmcKvm.DesktopSmoke.exe
```

该测试只连接 `127.0.0.1` 的环回服务，并拒绝测试过程中出现的电源或 USB
重置命令。截图和报告写入 `.artifacts/desktop-smoke/`。

## 参与贡献

- 修改前先创建独立分支，并保持改动范围明确。
- 协议、输入、密码学和虚拟媒体行为变更需要相应的自动化测试。
- 提交前运行 Release 构建和相关测试。
- Issue、日志、测试夹具和截图必须脱敏。
- 不要提交真实凭据、密钥、令牌、私钥、管理地址或生产环境配置。

## 许可证

本仓库目前未包含许可证文件。公开发布或接受外部贡献前，请先添加明确的开源
许可证。
