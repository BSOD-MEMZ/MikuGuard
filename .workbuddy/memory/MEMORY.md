# MikuGuard — 项目长期约定

## 这是什么

ClassIsland 插件（manifest id `mikuguard.camera-guard`），摄像头安全守护：
主界面指示点组件 + 设置页（设备/黑白名单/提醒）+ 带按钮的 Windows Toast + 回退弹窗。
从 `D:\Dev\EntertainingIsland` 的摄像头指示器拆出来单独立项（2026-11-07）。

## 构建 / 部署

- `.\build.ps1` / `.\deploy.ps1` / `.\release.ps1`（都要 **pwsh 7**；本机 Windows PowerShell 是 5.1，会按 GBK 读乱中文注释）
- `release.ps1` 同步 manifest.yml 与 AssemblyInfo.cs 的版本号、校验 cipx 比 dll 新、产物进 `dist\`（.gitignore），
  加 `-Publish` 才 tag + `gh release`
- 部署前必须关宿主，再覆盖 `D:\Dev\ClassIsland\data\Plugins\mikuguard.camera-guard\`（deploy.ps1 自动关）
- cipx 产物在 `cipx\MikuGuard.cipx`，已在 .gitignore
- `GenerateAssemblyInfo=false` 是硬要求（手写 AssemblyInfo.cs）

## 界面约定

- 设置页照宿主原生版式：`StackPanel Classes="settings-container animated-intro"` + 一行一个 `fa:SettingsExpander`
  （Header / Description / Footer）。**不要把 SettingsExpander 当分组卡片**往 Content 塞 Grid，会渲染成嵌套卡片。
- 分组内的多行用 `fa:SettingsExpanderItem`（Content / Description / Footer，末行 `Classes="has-radius"`）。
  FluentAvalonia 2.4.1 确实没有 `SettingsCard`，但有 `SettingsExpanderItem`。
- 分组小标题用 TextBlock（宿主的 `ClassIsland.Controls.IconText` 在宿主程序集里，插件引用不到）。

## 自动化

- 行动：`services.AddAction<T, TControl>()` + `[ActionInfo(id, name, glyph, addDefaultToMenu:false)]` + `ActionBase<TSettings>`；
  菜单树挂 `IActionService.ActionMenuTree`（按 Name 做 Key，重复挂之前先 Contains 判一下）。
- **OnRevert 的旧值必须存外部**（行动实例瞬态）：按 `ActionSet.Guid|行动Id` 存快照。

## 设计约束

- **判断宿主里能干什么，不要用独立 exe 试**：Toast 在独立 console 里必失败（0x80070490），在宿主里正常。验证要起真宿主。
- **空闲时不要探测设备占用**：DirectShow 绑定会短暂打开摄像头（可能让 LED 闪），只在系统记录显示有程序在用摄像头时才探。
- **Toast 三件套缺一不可**（AUMID 注册表 + LocalServer32 + 开始菜单 lnk），注册必须幂等，且提供注销入口。
- **任何提醒都两步走**：先试 Windows Toast，失败自动回退置顶卡片；回退卡片的按钮 arguments 与 Toast 完全同格式，共用一套解析。
- 设置改动即时落盘（`ConfigureFileHelper.SaveConfig`），嵌套对象与名单条目都要挂 PropertyChanged。
