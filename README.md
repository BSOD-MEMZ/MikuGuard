# MikuGuard

ClassIsland 的**摄像头安全守护**插件：谁碰了摄像头，就在信息岛上亮一个点，并且弹一条带按钮的 Windows 通知。

功能从 [EntertainingIsland](https://github.com/BSOD-MEMZ/EntertainingIsland) 里拆出来单独做，另外加了设备选择、程序黑白名单和带操作按钮的系统通知。

## 功能

**摄像头安全指示器（主界面组件）**

- 摄像头被调用时：放大亮出图标 → 收缩成小圆点常亮 → 摄像头释放后淡出
- 颜色、描边、图标字形、图标颜色、圆点/展开尺寸、展开停留时间、呼吸闪烁全部可配
- 点亮时可以播放一次扩散效果，两种样式：
  - **岛内涟漪**：从指示点扩散到整个信息岛，主题色填充，MD3 fast-out-slow-in 曲线，按岛的形状裁剪
  - **全屏花火**：以指示点为起点，在整块屏幕上放一朵 maimai 风格的烟花（透明置顶、鼠标穿透的叠加层）
- 悬停能看到「是哪个程序在用哪台摄像头」

**摄像头守护（设置 → 摄像头守护）**

- 监视指定摄像头，或「任意摄像头」
- 选定了某台摄像头时，默认**只提示它自己被占用**：别的摄像头被调用既不亮指示点也不弹提醒
- 程序过滤：黑名单（名单内不提醒）/ 白名单（只提醒名单内）/ 全部提醒；被忽略的程序一律不亮指示点也不弹提醒
- 名单可以手填完整路径、exe 文件名或 UWP 包族名，也能直接浏览 exe、或从「正在使用摄像头的程序」一键加入
- 提醒方式：自动（优先 Windows 通知，失败回退插件内弹窗）/ 仅 Windows 通知 / 仅插件内弹窗
- 提醒配图：Toast 用 hero 大图、插件内弹窗用顶部大图，默认 `Assets/monitoring.jpg`，可换成任意图片
- 提醒冷却时间、标题文字、三个按钮各自可开关
- 「摄像头关闭后提示」：报过警之后，摄像头被释放时再发一条带 `Assets/monitoring_off.jpg` 的「危机解除」通知；
  没报过警的话，平时关摄像头完全不打扰

**提醒里的按钮**

| 按钮 | 行为 |
| --- | --- |
| 消除提醒 | 撤掉这条提示，本次使用期间不再打扰（摄像头释放后恢复） |
| 关闭应用 | 结束正在使用摄像头的进程 |

**自动化（ClassIsland 自动化 → 添加行动 → MikuGuard 摄像头守护）**

把守护接进课表规则里，比如「上课前关掉提醒、下课后自动恢复」：

| 分组 | 行动 |
| --- | --- |
| 摄像头守护 | 启用守护 / 停用守护 |
| 提醒 | 打开提醒 / 关闭提醒（只影响弹窗，指示点照常） |
| 提醒方式 | 自动 / 仅 Windows 通知 / 仅插件内弹窗（= 关掉 Toast） |
| 操作 | 重启监控 / 发送测试提醒 / 重新注册通知身份 |

前三个分组都支持「恢复」：行动组结束时自动回到触发前的状态，恢复的是
守护开关 + 提醒开关 + 提醒方式的完整快照。

## 安装

**用发版包（推荐）**：把 `dist\MikuGuard-<版本>.cipx` 拖到 ClassIsland 的 `data\Plugins\`，或在应用的插件页里安装。

**从源码**：

```powershell
.\build.ps1          # 只编译 + 打 cipx
.\deploy.ps1         # 编译 → 关宿主 → 覆盖插件目录 → 启动
.\release.ps1 -Version 1.1.0.2   # 改版本号 → 编译 → 归集 dist\（加 -Publish 走 tag + gh release）
```

> 脚本要 PowerShell 7（`pwsh`）跑：含中文注释，Windows PowerShell 5.1 会按 GBK 读成乱码。

1. 启动 ClassIsland → 主界面编辑 → 添加「摄像头安全指示器」组件
2. 设置 → 摄像头守护 → 按需配置

> 部署前必须先关掉 ClassIsland，否则 DLL 被占用，改了等于没改（`deploy.ps1` 会自动关）。

## 工作原理

**谁在用摄像头、用的是哪台**：走 **Media Foundation 的「传感器活动监控」**（`IMFSensorActivityMonitor`）。
摄像头活动一变化，系统就回调插件，报告里带着：

- 设备（`SymbolicLink`，和设置里选的设备 id 完全一致）
- 使用它的进程 PID，以及 `GetStreamingState()`（是否正在取流）

插件把 `(设备, PID)` 记下来，再把 PID 还原成程序身份（桌面程序取 exe 路径，UWP 取包族名），
黑白名单、指示点、提醒都基于这批数据。这套接口不碰硬件、不取流，也不会亮 LED。

> **为什么不再读注册表**：2026-11 实测，Windows 11 24H2（build 26100）之后，桌面程序用摄像头时
> `HKCU\...\CapabilityAccessManager\ConsentStore\webcam` 的 `LastUsedTimeStart/Stop` 不再更新
> （MediaCapture 取流 25 秒，一条记录都没有）；设备级的「`CreateFile` 独占打开」也判不出来——
> 摄像头现在由 FrameServer 统一取流，客户端进程根本不持有设备句柄。
> 旧逻辑只留作兜底：MF 监控初始化失败时才退回 ConsentStore。

**摄像头预览 / 设备列表**：设备枚举走 cfgmgr32 + 注册表（纯读），预览走 WinRT `MediaCapture`。

**Windows 通知**：未打包的桌面程序要发 Toast，需要三样东西，插件启动时自动注册（幂等）：

- `HKCU\Software\Classes\AppUserModelId\xxtsoft.MikuGuard.CameraGuard`（显示名 + 图标 + 激活器 CLSID）
- `HKCU\Software\Classes\CLSID\{...}\LocalServer32` 指向 `ClassIsland.Desktop.exe`
- 开始菜单快捷方式 `MikuGuard 摄像头守护.lnk`（带 `System.AppUserModel.ID`）

按钮点击通过 `INotificationActivationCallback` 回调到宿主进程里处理。不想留这些东西时，
设置页底部有「注销通知身份」。

## 已知限制

- **占用判定依赖 MF 传感器监控**：设备与 PID 都由系统（FrameServer）上报，正常驱动都能拿到；
  极个别驱动不上报时，只能退回 ConsentStore，而 24H2 之后那个记录已经不再更新，表现为「读不到」。
  设置页的「测试设备判定」会直接显示当前这台摄像头有没有活动、PID 是多少，方便对照。
- Windows 通知被系统关掉（专注助手、通知总开关）时发不出去，插件会自动改用置顶小弹窗。
- 本插件只在 Windows 上工作（设备枚举、通知、权限都依赖 Win32/WinRT）。
- 花火的粒子模拟是纯 CPU 的（55 个粒子左右），4K 全屏时帧率会掉一点；素材版权见下。

## 花火素材

`Assets\Hanabi\` 里的贴图、参数表来自 [thock-maimai](https://github.com/wzk112/thock-maimai)（游戏衍生素材，
**不在 MIT 范围内**，版权归原游戏权利方，详见该目录的 `ASSET-LICENSE.txt`）。粒子推进与曲线求值照搬了它的
CPU 侧实现，`tools/make_hanabi_assets.py` 把原贴图烘成「亮度 → alpha」遮罩供 Avalonia 着色用。

## 调试

日志在 `ClassIsland\data\Config\Plugins\mikuguard.camera-guard\mikuguard.log`，
配置在同目录的 `Settings.json`。设备枚举、占用探测、通知注册、按钮回调都会写日志。

## 许可

插件代码 MIT。`Assets\Hanabi\` 下的游戏衍生素材与参数不适用 MIT，见上文「花火素材」。
