using System.ComponentModel;
using System.Diagnostics;
using GalgameManager.Application;

namespace GalgameManager.Infrastructure;

public sealed class WindowsGameLauncher(DiagnosticLog log) : IGameProcessLauncher
{
    public void Start(LaunchSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Executable,
            WorkingDirectory = spec.WorkDirectory,
            UseShellExecute = false
        };
        foreach (var argument in spec.Arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                throw new LibraryException("Windows 未返回新进程。请重试或在详情中检查启动配置。");
        }
        catch (Win32Exception ex)
        {
            log.Write("create-process", ex);
            var reason = ex.NativeErrorCode switch
            {
                2 or 3 => "入口或工作目录已不存在",
                5 => "Windows 拒绝访问",
                193 or 216 => "此文件无法在当前系统运行",
                740 => "游戏要求提升权限",
                _ => "Windows 无法创建游戏进程"
            };
            throw new LibraryException($"{reason}（系统错误 {ex.NativeErrorCode}）。请在详情中检查入口和工作目录后重试。");
        }
    }
}
