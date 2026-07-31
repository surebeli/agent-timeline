namespace AgentTimeline.Core;

/// <summary>开机自启动项该做的动作。</summary>
public enum LoginItemAction
{
    /// <summary>写入（或按新路径重写）自启动项。</summary>
    Register,

    /// <summary>删除自启动项。</summary>
    Unregister,

    /// <summary>已经一致，什么都不做——**不要**无条件重复写注册表。</summary>
    None,
}

/// <summary>
/// 开机自启动的纯判据（对齐 mac <c>LoginItem.action(desired:current:)</c>）。
///
/// **纯函数、只用基元类型、放在 Core 下**——同折叠轮 W-1 的教训：<c>CoreSmokeTest</c> 是
/// net7.0、不引 Windows App SDK 也不引注册表，签名里出现平台类型就编不过。真正读写
/// <c>HKCU\...\Run</c> 的副作用在 <c>Interop/StartupRegistry.cs</c>，那部分靠实机验证。
///
/// **机制为什么是 Run 注册表项而不是 <c>Windows.ApplicationModel.StartupTask</c>**：后者是
/// WinRT API，要求 app 有包身份（MSIX）并在包清单里声明 <c>StartupTask</c> 扩展；本工程是
/// **非打包**分发（自包含、解压到任意目录直接跑 exe），没有包身份。<c>HKCU</c> 下的 Run 项
/// 不需要管理员权限、立即生效，是非打包 exe 最朴素可靠的做法。
/// </summary>
public static class LoginItem
{
    /// <summary>Run 键的路径（HKCU 下，不是 HKLM——HKLM 要管理员权限）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>值名。用户在任务管理器「启动」页看到的就是这个名字。</summary>
    public const string RunValueName = "AgentTimeline";

    /// <summary>
    /// 把 exe 路径包成 Run 项的命令行。
    ///
    /// **必须加引号**：默认安装位置就带空格（<c>C:\Users\…\AppData\Local\Programs\…</c>），
    /// 不加引号时 Windows 会按空格拆出 <c>C:\Users\foo\AppData\Local\Programs\Agent</c>
    /// 这样的可执行名去找，静默启动失败——而且失败发生在开机时、没有任何界面反馈。
    /// </summary>
    public static string FormatCommand(string exePath) => "\"" + exePath + "\"";

    /// <summary>
    /// 给定期望状态与注册表里现有的命令行，判断该做什么。
    ///
    /// 为什么要比对**命令行内容**而不是像 mac 那样只看"注册了没有"：本工程是解压即用的
    /// 非打包分发，用户随时可能把目录挪走或换一份构建。只判有无的话，注册表里会留着一条
    /// 指向旧路径的自启动项，开机时静默拉起一个已经不存在或过期的副本。路径对不上就重写。
    ///
    /// 反过来也要防：判等时剥掉引号并忽略大小写（Windows 路径本就不区分大小写），
    /// 否则历史上写过的不带引号的值会被判成"不同"，每次启动都白写一次注册表。
    /// </summary>
    /// <param name="currentCommand">注册表里现有的值；null 或空白视为未注册。</param>
    public static LoginItemAction Decide(bool desired, string? currentCommand, string desiredCommand)
    {
        var registered = !string.IsNullOrWhiteSpace(currentCommand);

        if (!desired) return registered ? LoginItemAction.Unregister : LoginItemAction.None;
        if (!registered) return LoginItemAction.Register;

        return SameCommand(currentCommand!, desiredCommand)
            ? LoginItemAction.None
            : LoginItemAction.Register;
    }

    private static bool SameCommand(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string command) => command.Trim().Trim('"').Trim();
}
