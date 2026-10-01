# 图吧工具箱 PE 兼容版

独立的 MewUI 工具箱前端，使用 Win32 平台宿主和 GDI 渲染后端；目录扫描、工具元数据、架构变体、图标缓存、收藏/设置存储和 WMI 硬件信息沿用现有兼容版服务。

## 构建

需要 .NET 10 SDK。项目面向 Windows 10 19041 或更高版本，发布为自包含单文件，因此目标 PE 不需要预装 .NET。

```powershell
dotnet build TubaWinUi3.PECompatible/TubaWinUi3.PECompatible.csproj
dotnet publish TubaWinUi3.PECompatible/TubaWinUi3.PECompatible.csproj -c Release -r win-x64
```

其他架构可将 RID 替换为 `win-x86` 或 `win-arm64`。该项目不启用裁剪或 NativeAOT，以保留现有 WMI 查询行为。

## 便携目录

将 `图吧工具箱PE兼容版.exe` 与原便携包的 `src/` 目录放在同一目录。应用会自动查找 `src/Tools/` 和 `src/Metadata/`；也支持直接放置 `Tools/` 与 `Metadata/`。如果 PE 镜像未包含 WMI 组件，硬件页面可能只能显示可用的部分信息，工具目录和启动功能不依赖 WMI。

包含的兼容版功能：全部工具/分类浏览、名称/路径/标签搜索、工具启动、架构变体、打开工具目录、复制路径、收藏、深浅主题、硬件信息刷新与截图复制。
