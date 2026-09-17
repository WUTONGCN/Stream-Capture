# 第三方声明

根目录 MIT 协议适用于 Stream Capture 自有代码，不覆盖下列第三方组件。

| 组件 | 位置 | 许可依据 |
| --- | --- | --- |
| MinHook | `NativeHook/minhook/`、`NativeHook/MinHook.h` | 保留 [原许可证](NativeHook/minhook/LICENSE.txt)，包含 BSD 条款和其组成部分声明 |
| OBS Multi RTMP | `obs-multi-rtmp/` | 独立插件，保留 [GPL 原文](obs-multi-rtmp/LICENSE) 及各源文件声明；未改为 MIT |
| NuGet 依赖 | `StreamCapture.csproj` | SharpPcap、PacketDotNet、Newtonsoft.Json、NLog、Titanium.Web.Proxy 等沿用各包自带许可证 |
| Npcap | 用户自行安装 | 不在本仓库中分发，适用 Npcap 官方许可证 |

分发二进制时请随附相应第三方许可。OBS 插件单独构建，不链接进 WPF 主程序。
