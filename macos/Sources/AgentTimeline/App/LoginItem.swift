import ServiceManagement

/// 开机自启动（登录项）。用 `SMAppService.mainApp`——macOS 13+ 起注册主 app 本身
/// 不需要另起一个 helper bundle/LoginItems target，是当前推荐做法（`LSSharedFileList`
/// / `SMLoginItemSetEnabled` 都已废弃）。Windows 侧等价机制见
/// `windows/SYNC-KICKOFF-PROMPT.md`（本轮任务书）。
enum LoginItem {
    enum Action: Equatable { case register, unregister, none }

    /// 纯函数：给定「用户想要的状态」与「系统当前实际状态」，决定要不要调用
    /// `register()`/`unregister()`。单独抽出来是因为 `SMAppService.Status` 在真实环境里
    /// 会跟我们自己存的偏好脱钩——用户可能在「系统设置 > 通用 > 登录项」里手动关掉，
    /// 不能假设它总跟 `AppSettings.launchAtLogin` 一致，也不该无条件重复调用。
    static func action(desired: Bool, current: SMAppService.Status) -> Action {
        switch (desired, current) {
        case (true, .notRegistered), (true, .notFound):
            return .register
        case (false, .enabled), (false, .requiresApproval):
            return .unregister
        default:
            return .none
        }
    }

    /// 把 `AppSettings.launchAtLogin` 同步到系统实际状态。失败不阻塞启动、不弹
    /// 应用侧的 alert——最常见的失败原因是用户还没在系统设置里批准，
    /// `SMAppService` 自己会弹系统级提示，这里只保证不崩、不打断启动流程。
    @discardableResult
    static func sync(desired: Bool) -> Action {
        let current = SMAppService.mainApp.status
        let act = action(desired: desired, current: current)
        do {
            switch act {
            case .register: try SMAppService.mainApp.register()
            case .unregister: try SMAppService.mainApp.unregister()
            case .none: break
            }
        } catch {
            // 见上：常见于「需要用户批准」，不是需要应用侧处理的错误。
        }
        return act
    }
}
