# Stream Capture

Windows 本地直播推流地址采集工具。主程序采用 C# / WPF / .NET 8，可从本机获授权的直播流量中提取推流地址和流密钥，复制结果或填写到 OBS 多路推流配置。

[源码](https://github.com/WUTONGCN/Stream-Capture) · [历史下载](https://github.com/WUTONGCN/Stream-Capture/releases) · [问题反馈](https://github.com/WUTONGCN/Stream-Capture/issues)

## 当前源码与历史下载

2026-09-17 发布本地社区版源码：移除已退役的账号登录、商业授权、设备指纹和自动升级流程，无需连接原商业后端。
历史 Release 是以前的构建，**不代表当前源码构建结果**。本次没有上传新的二进制 Release。

现有解析代码覆盖抖音、小红书、哔哩哔哩及京东；平台协议可能变化，实际兼容性需在 Windows 上验证。快手 Native Hook 源码保留为实验模块，当前界面默认不开放该平台。不要把源码存在或编译成功理解为各平台均已完成实机验收。

## 环境与构建

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Npcap](https://npcap.com/#download)，按其许可从官方单独安装，并启用 WinPcap 兼容模式；仓库不分发安装包。
- 可选：Visual Studio 2022 的 C++ 桌面开发组件和 CMake，用于 Native Hook。

```powershell
git clone https://github.com/WUTONGCN/Stream-Capture.git
cd Stream-Capture
dotnet restore -r win-x64
dotnet build -c Release --no-restore
dotnet publish -c Release -r win-x64 -o artifacts/community
```

从 `artifacts/community/StreamCapture.exe` 启动。抓包通常需要管理员权限；程序的 manifest 会请求提升权限。
macOS/Linux 可以交叉编译托管代码（必要时添加 `-p:PublishReadyToRun=false`），但不能运行 WPF 或验证 Npcap/OBS 集成。

### 可选 Native Hook

```powershell
cmake -S NativeHook -B NativeHook/build -A x64
cmake --build NativeHook/build --config Release
dotnet publish -c Release -r win-x64 -o artifacts/community
```

DLL 存在时会自动嵌入主程序；未构建 DLL 时可使用常规抓包。详见 [NativeHook](NativeHook/README.md)。
`obs-multi-rtmp/` 是独立的第三方 OBS 插件源码，**不参与 .NET 主程序构建**；使用 OBS 集成功能时需自行安装兼容版本的 OBS 和该插件。

## 使用

1. 选择网卡及自己要捕获的平台（默认仅选择抖音）。
2. 点击“开始捕获”，再启动自己的直播客户端推流。
3. 在结果列表复制推流地址、密钥或完整地址。
4. 如需写入 OBS，点击对应的“填写到 OBS”；这会把密钥存入本机 OBS 配置，请保护该目录。
5. 用完点击停止捕获并关闭程序。

配置默认嵌入程序。可把仓库的 `platforms.json` 放在 exe 旁修改以覆盖默认值。

### HTTPS 捕获（可选）

默认 `general.enableSSLDecrypt=false`，不自动开启 HTTPS 代理。京东等加密接口的解析需将其设为 `true` 并重启；每次启动该模式时还会显示确认提示。
该模式会安装本地受信任代理证书、修改系统 HTTP/HTTPS 代理，并可能看到其他应用通过代理的流量，只应在明确授权的设备上使用。代理仅监听 `127.0.0.1:8888`，不忽略上游 TLS 证书错误。
停止捕获或正常关闭时关闭代理；异常退出后请检查 Windows 代理设置。已安装的代理根证书不会自动删除，不再使用时请在 Windows 证书管理器中移除。

## 隐私

- 社区版不包含原账号/授权服务、机器码、硬件指纹收集代码。
- 不持久化捕获日志；原始报文不包含在 JSON 结果导出中。
- 构建产物、抓包、日志、凭据与私人配置均由 `.gitignore` 排除。
- 当前分支移除个人联系方式、收款二维码及可能包含推流信息的旧截图；历史提交与历史 Release 未重写。
- 结果列表、剪贴板及主动保存的 OBS 配置仍含真实流密钥，不要公开分享。

## 贡献和许可

见 [贡献指南](CONTRIBUTING.md)、[安全说明](SECURITY.md)。本项目自有代码按 [MIT](LICENSE) 授权。
MinHook 保留原 BSD 条款；独立 OBS 插件保留 GPL 条款，见 [第三方声明](THIRD_PARTY_NOTICES.md)。平台名称与图标属于各自权利人。

仅在你拥有或获得明确授权的直播账号、设备和网络上使用，并遵守平台规则。
