# MikuGuard

ClassIsland 的**摄像头安全守护**插件：谁碰了摄像头，就在信息岛上亮一个点，并且弹一条带按钮的 Windows 通知。

功能从 [EntertainingIsland](https://github.com/BSOD-MEMZ/EntertainingIsland) 里拆出来单独做，另外加了设备选择、程序黑白名单和带操作按钮的系统通知。

## 功能

**摄像头安全指示器（主界面组件）**

- 摄像头被调用时：放大亮出图标 → 收缩成小圆点常亮 → 摄像头释放后淡出
- 颜色、描边、图标字形、图标颜色、圆点/展开尺寸、展开停留时间、呼吸闪烁全部可配
- 点亮时可以播放一次涟漪：从指示点扩散到整个信息岛，主题色填充，MD3 fast-out-slow-in 曲线，按岛的形状裁剪
- 悬停能看到「是哪个程序在用哪台摄像头」

**摄像头守护（设置 → 摄像头守护）**

- 监视指定摄像头，或「任意摄像头」
- 程序过滤：黑名单（名单内不提醒）/ 白名单（只提醒名单内）/ 全部提醒
- 名单可以手填完整路径、exe 文件名或 UWP 包族名，也能直接浏览 exe、或从「正在使用摄像头的程序」一键加入
- 提醒方式：自动（优先 Windows 通知，失败回退插件内弹窗）/ 仅 Windows 通知 / 仅插件内弹窗
- 提醒配图：Toast 用 hero 大图、插件内弹窗用顶部大图，默认 `Assets/monitoring.jpg`，可换成任意图片
- 提醒冷却时间、标题文字、三个按钮各自可开关

**提醒里的按钮**

| 按钮 | 行为 |
| --- | --- |
| 消除提醒 | 撤掉这条提示，本次使用期间不再打扰（摄像头释放后恢复） |
| 关闭摄像头 | 禁用摄像头设备；没有管理员权限时改为撤销该应用的摄像头访问权限 |
| 关闭应用 | 结束正在使用摄像头的进程 |

## 安装

1. `dotnet build MikuGuard.csproj -c Release`
2. 关闭 ClassIsland，把 `bin\Release\net8.0-windows10.0.19041.0\` 里的文件复制到
   `ClassIsland\data\Plugins\mikuguard.camera-guard\`
3. 启动 ClassIsland → 主界面编辑 → 添加「摄像头安全指示器」组件
4. 设置 → 摄像头守护 → 按需配置

> 部署前必须先关掉 ClassIsland，否则 DLL 被占用，改了等于没改。

## 工作原理

**谁在用摄像头**：读 Windows 的摄像头访问记录
`HKCU\Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam`，
`LastUsedTimeStart > 0 && LastUsedTimeStop == 0` 即「正在使用」。桌面程序还会核对进程是否真的在跑，
避免程序崩溃留下的脏记录让指示器一直亮着。这一步纯读取，不碰硬件。

**是哪台摄像头**：系统访问记录是"能力级"的，不区分设备，所以**检测只看系统记录**；
设备下拉框用于「预览」和「关闭摄像头」定位到具体某台设备。
（试过用 DirectShow 绑定和 MediaCapture 独占试开来判断设备占用，本机上都不可靠：
空闲设备绑定时同样报错、同一用户下独占试开也不报占用。宁可少报，不用假信号压掉真实告警。）

**Windows 通知**：未打包的桌面程序要发 Toast，需要三样东西，插件启动时自动注册（幂等）：

- `HKCU\Software\Classes\AppUserModelId\xxtsoft.MikuGuard.CameraGuard`（显示名 + 图标 + 激活器 CLSID）
- `HKCU\Software\Classes\CLSID\{...}\LocalServer32` 指向 `ClassIsland.Desktop.exe`
- 开始菜单快捷方式 `MikuGuard 摄像头守护.lnk`（带 `System.AppUserModel.ID`）

按钮点击通过 `INotificationActivationCallback` 回调到宿主进程里处理。不想留这些东西时，
设置页底部有「注销通知身份」。

## 已知限制

- **「关闭摄像头」需要管理员权限**。ClassIsland 不是以管理员身份跑的时候禁用设备会失败，
  这时会自动退化成「撤销该应用的摄像头权限」（对该应用立即生效，但已经在跑的画面上不一定马上断）。
  设置页里有「恢复摄像头设备」可以把设备重新启用。
- **不区分是哪台摄像头在用**：Windows 只暴露"某个程序用了摄像头"，不暴露"用的哪台"。
  设备选择只影响预览与关闭目标，不影响是否告警。
- Windows 通知被系统关掉（专注助手、通知总开关）时发不出去，插件会自动改用置顶小弹窗。
- 本插件只在 Windows 上工作（设备枚举、通知、权限都依赖 Win32/WinRT）。

## 调试

日志在 `ClassIsland\data\Config\Plugins\mikuguard.camera-guard\mikuguard.log`，
配置在同目录的 `Settings.json`。设备枚举、占用探测、通知注册、按钮回调都会写日志。

## 许可

MIT
