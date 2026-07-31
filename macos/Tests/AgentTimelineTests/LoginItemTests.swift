import ServiceManagement
import XCTest
@testable import AgentTimeline

/// 开机自启动的判据。`SMAppService.register()`/`unregister()` 本身在测试环境里没法
/// 安全断言（真会改系统登录项，且 SPM 测试二进制不是正经 .app bundle，SMAppService
/// 对它的行为没有保证）——所以只测抽出来的纯判据 `LoginItem.action`，副作用调用
/// (`LoginItem.sync`) 靠实机验证（见 macos/SYNC-KICKOFF-PROMPT.md）。
final class LoginItemTests: XCTestCase {

    func testRegistersWhenDesiredButNotRegistered() {
        XCTAssertEqual(LoginItem.action(desired: true, current: .notRegistered), .register)
        XCTAssertEqual(LoginItem.action(desired: true, current: .notFound), .register)
    }

    func testUnregistersWhenNotDesiredButStillActive() {
        XCTAssertEqual(LoginItem.action(desired: false, current: .enabled), .unregister)
        // 待批准也算「系统那边还挂着」，用户关掉开关时应该一并撤销，
        // 不能因为还没批准就当作「反正没生效，不用管」。
        XCTAssertEqual(LoginItem.action(desired: false, current: .requiresApproval), .unregister)
    }

    func testNoActionWhenAlreadyMatchingDesiredState() {
        XCTAssertEqual(LoginItem.action(desired: true, current: .enabled), .none)
        XCTAssertEqual(LoginItem.action(desired: true, current: .requiresApproval), .none, "已经在等批准，不必重复 register")
        XCTAssertEqual(LoginItem.action(desired: false, current: .notRegistered), .none)
        XCTAssertEqual(LoginItem.action(desired: false, current: .notFound), .none)
    }
}
