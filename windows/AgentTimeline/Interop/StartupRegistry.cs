using AgentTimeline.Core;
using Microsoft.Win32;

namespace AgentTimeline.Interop;

/// <summary>
/// 开机自启动的副作用面：读写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// 判据在 <see cref="LoginItem"/>（纯函数、有冒烟断言），这里只负责真的去动注册表。
///
/// 全程吞异常并记日志：注册表可能被组策略锁住、被安全软件拦下。**自启动配不上不该
/// 让应用起不来**——这是个设置项，不是启动依赖。
/// </summary>
internal static class StartupRegistry
{
    /// <summary>把设置里的期望值应用到注册表；已经一致就不写。</summary>
    public static void Apply(bool desired)
    {
        try
        {
            var exe = ExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                Log.Warn("开机自启动：取不到自身 exe 路径，跳过");
                return;
            }

            var wanted = LoginItem.FormatCommand(exe);
            var current = ReadCommand();
            var action = LoginItem.Decide(desired, current, wanted);

            switch (action)
            {
                case LoginItemAction.Register:
                    Write(wanted);
                    Log.Info($"开机自启动：已写入 {wanted}" +
                             (current is null ? "" : $"（原值 {current}）"));
                    break;
                case LoginItemAction.Unregister:
                    Remove();
                    Log.Info("开机自启动：已移除注册表项");
                    break;
                default:
                    break; // 一致，不写
            }
        }
        catch (Exception ex)
        {
            Log.Error("开机自启动：注册表操作失败", ex);
        }
    }

    /// <summary>
    /// 自身 exe 的完整路径。**运行时动态取**——非打包分发，用户解压到哪个目录未知，
    /// 写死任何路径都会在下一次搬家后变成一条指向空气的自启动项。
    /// </summary>
    private static string? ExecutablePath()
    {
        // Environment.ProcessPath 给的是 apphost（AgentTimeline.exe）本身，
        // 正是开机要拉起的东西；取不到时退回 MainModule。
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)) return path;
        return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
    }

    private static string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LoginItem.RunKeyPath);
        return key?.GetValue(LoginItem.RunValueName) as string;
    }

    private static void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(LoginItem.RunKeyPath, writable: true);
        key?.SetValue(LoginItem.RunValueName, command, RegistryValueKind.String);
    }

    private static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LoginItem.RunKeyPath, writable: true);
        key?.DeleteValue(LoginItem.RunValueName, throwOnMissingValue: false);
    }
}
