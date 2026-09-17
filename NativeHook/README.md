# Native Hook（实验模块）

用于在明确获授权的本机直播客户端中观察网络 API 数据。主界面目前默认隐藏快手平台；该模块不是常规抓包的必需依赖。

## 构建

Windows x64、Visual Studio 2022 C++ 桌面开发组件、CMake 3.15+。MinHook 源码已随附，无需再次克隆。

```powershell
cmake -S NativeHook -B NativeHook/build -A x64
cmake --build NativeHook/build --config Release
```

输出为 `NativeHook/build/bin/Release/KuaishouHook.dll`，后续 .NET 构建会将其嵌入程序。
当前环境未做实际客户端注入验收；仅在获授权的设备和进程上使用。

MinHook 的 [原许可证](minhook/LICENSE.txt) 保持不变。自有封装代码使用根目录 MIT 协议。
