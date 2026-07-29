using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentTimeline.Core;

namespace AgentTimeline;

/// <summary>
/// 进程入口。取代 XAML 生成的 Main（csproj 定义 <c>DISABLE_XAML_GENERATED_MAIN</c> 关掉那份），
/// 唯一的增量是**单实例闸**——下面 Main 里除第一行外与生成版逐字一致。
///
/// 为什么要在这里拦、而不是在 <c>App.OnLaunched</c> 里拦：与 mac
/// <c>macos/Sources/AgentTimeline/App/main.swift</c> 同一位置、同一语义——在任何
/// 应用对象存在之前就退出，不留半个初始化过的进程。
///
/// 两个实例并存会撞在这些地方（实测对过代码，不是理论担忧）：
///   · 摘要引擎各自挑 `summary_pending` 的行去跑 CLI，**同一批节点跑两遍**，配额与
///     耗时双倍；`summary_attempts` 被两边各加一次，重试上限提前耗尽；
///   · <c>App.OnLaunched</c> 注释所说「replay 与 watcher 不并发写 codenames 表」是
///     **进程内**保证，跨进程失效；
///   · <c>AppSettings.Save</c> 的锁同样是进程内的：跨进程只保证读不到半个文件，
///     不保证不丢更新（窗口位置互相打架、重放标记可能被回退）；
///   · 两个托盘图标，「退出」只关掉一个——用户以为退了其实还在跑。
///
/// 唯独 SQLite 本身是自愈的（WAL + 默认 busy 重试 + `UNIQUE(agent, session_id, ts,
/// command_hash)` 去重 + file_offsets 走 UPSERT），所以 mac 注释里那句
/// 「lose writes silently」在 Windows 这套姿态下并不完全成立，此处如实记下。
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern void XamlCheckProcessRequirements();

    /// <summary>整个进程存活期间持有；被回收就等于把闸放开了。</summary>
    private static Mutex? _singleInstance;

    [STAThread]
    private static void Main(string[] args)
    {
        if (!TryAcquireSingleInstance()) return;   // 与 mac 同语义：拒绝当第二个

        XamlCheckProcessRequirements();

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    /// <summary>
    /// 闸的粒度是**一个数据库**，不是「一台机器」也不是「一个登录会话」——要防的正是
    /// 「两个进程共用一份 store」。名字取 <see cref="AppPaths.DatabaseFile"/> 的哈希：
    ///
    ///   · 不同用户各有自己的 <c>%LOCALAPPDATA%</c> → 名字不同 → 互不阻塞（用
    ///     <c>Global\</c> 固定名会误伤，这是常见写法里的坑）；
    ///   · 同一用户的两个会话（RDP + 控制台）共用同一份 store → 名字相同 → 被拦下，
    ///     而 <c>Local\</c> 前缀在这种情况下拦不住（它按会话隔离）。
    ///
    /// 故首选 <c>Global\</c>；某些强化环境会收走 SeCreateGlobalPrivilege，那时退到
    /// <c>Local\</c>（能拦住同会话重复启动这个最常见的情形）。两者都建不出来时**放行**
    /// ——宁可多一个实例，也不能让应用起不来。
    /// </summary>
    private static bool TryAcquireSingleInstance()
    {
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.DatabaseFile.ToLowerInvariant())))[..16];

        foreach (var name in new[] { $@"Global\AgentTimeline-{key}", $@"Local\AgentTimeline-{key}" })
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: true, name, out var isFirst);
                if (isFirst)
                {
                    _singleInstance = mutex;
                    return true;
                }
                mutex.Dispose();
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 建不了/开不了这个命名空间下的对象，换下一个前缀再试。
            }
            catch (IOException)
            {
                // 同上（名字被同名非 Mutex 对象占用等罕见情形）。
            }
        }
        return true;
    }
}
