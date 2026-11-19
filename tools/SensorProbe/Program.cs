using MikuGuard.Services;

GuardLog.Initialize(Path.Combine(Path.GetTempPath(), "sensorprobe-log"));

Console.WriteLine($"Start: {CameraDeviceBusyProbe.Start()}");

for (var i = 0; i < 8; i++)
{
    await Task.Delay(2000);
    var snapshot = CameraDeviceBusyProbe.Snapshot();
    Console.WriteLine($"[{i}] 占用设备 {snapshot.Count} 台");
    foreach (var activity in snapshot)
        Console.WriteLine($"    {activity.DeviceName} | {activity.DeviceId} | pid={string.Join(",", activity.ProcessIds)}");
}

CameraDeviceBusyProbe.Stop();
Console.WriteLine("DONE");
