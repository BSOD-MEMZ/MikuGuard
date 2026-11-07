# MikuGuard — 项目长期约定

## 这是什么

ClassIsland 插件（manifest id `mikuguard.camera-guard`），摄像头安全守护：
主界面指示点组件 + 设置页（设备/黑白名单/提醒）+ 带按钮的 Windows Toast + 回退弹窗。
从 `D:\Dev\EntertainingIsland` 的摄像头指示器拆出来单独立项（2026-11-07）。

## 构建 / 部署

- `dotnet build MikuGuard.csproj -c Release`（`net8.0-windows10.0.19041.0`）
- 部署前必须关宿主，再覆盖 `D:\Dev\ClassIsland\data\Plugins\mikuguard.camera-guard\`
- cipx 产物在 `cipx\MikuGuard.cipx`，已在 .gitignore
- `GenerateAssemblyInfo=false` 是硬要求（手写 AssemblyInfo.cs）

## 设计约束

- **判断宿主里能干什么，不要用独立 exe 试**：Toast 在独立 console 里必失败（0x80070490），在宿主里正常。验证要起真宿主。
- **空闲时不要探测设备占用**：DirectShow 绑定会短暂打开摄像头（可能让 LED 闪），只在系统记录显示有程序在用摄像头时才探。
- **Toast 三件套缺一不可**（AUMID 注册表 + LocalServer32 + 开始菜单 lnk），注册必须幂等，且提供注销入口。
- **任何提醒都两步走**：先试 Windows Toast，失败自动回退置顶卡片；回退卡片的按钮 arguments 与 Toast 完全同格式，共用一套解析。
- 设置改动即时落盘（`ConfigureFileHelper.SaveConfig`），嵌套对象与名单条目都要挂 PropertyChanged。
