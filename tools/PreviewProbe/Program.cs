using MikuGuard.Services;
using MikuGuard.Services.Preview;

Console.WriteLine("=== 1. ConsentStore 里正在用摄像头的程序 ===");
var active = CameraConsentReader.ReadActive();
if (active.Count == 0) Console.WriteLine("  （空）");
foreach (var app in active)
    Console.WriteLine($"  {app.DisplayName} | packaged={app.IsPackaged} | {app.Key} | 开始 {app.StartedAt:HH:mm:ss}");

Console.WriteLine("=== 2. 枚举到的摄像头 ===");
var devices = CameraDeviceService.Enumerate();
foreach (var d in devices)
    Console.WriteLine($"  {d.Name} | {d.Id}");
if (devices.Count == 0)
{
    Console.WriteLine("没有摄像头，跳过预览测试");
    return;
}

Console.WriteLine("=== 3. 用枚举出来的 id 打开预览 ===");
var service = new CameraPreviewService();
var frames = 0;
var lastSize = "";
service.FrameReady += (pixels, w, h) =>
{
    frames++;
    lastSize = $"{w}x{h}";
};

var target = devices[0];
var ok = await service.StartAsync(target.Id, target.Name);
Console.WriteLine($"  StartAsync = {ok}, LastError = {service.LastError ?? "（无）"}");

await Task.Delay(1500);
await Task.Delay(1500);
Console.WriteLine($"  3 秒内收到 {frames} 帧，最后一帧 {lastSize}");

await service.StopAsync();
Console.WriteLine("=== 完成 ===");
