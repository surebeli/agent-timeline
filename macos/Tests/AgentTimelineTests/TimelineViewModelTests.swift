import XCTest
@testable import AgentTimeline

/// 滚到底自动加载（追齐 Windows 侧同名功能，替换掉原来的「加载更多」按钮）。
/// 真实的「滚到底触发」在这里测不了（要起真实窗口），已用真实二进制 + 合成大数据集
/// 实测过（见 windows/SYNC-KICKOFF-PROMPT.md 上一轮 W-1 同款教训：几何/分页这类逻辑
/// 能抽成纯函数就不要只信代码推断）；这里测的是 `loadMore`/`canLoadMore` 本身的边界行为。
final class TimelineViewModelTests: XCTestCase {

    @MainActor
    private func makeStore(nodeCount: Int) throws -> Store {
        let dbPath = NSTemporaryDirectory() + "at-test-\(UUID().uuidString).sqlite"
        let store = try Store(path: dbPath)
        for i in 0..<nodeCount {
            let cmd = UserCommand(
                agent: .claude, project: "p", cwd: nil, sessionId: "s",
                timestamp: Date(timeIntervalSince1970: 1_700_000_000 + Double(i)),
                text: "synthetic \(i)", sourceFile: "/f")
            store.insertNodeIfNew(cmd)
        }
        return store
    }

    /// 初始 fetchLimit 是 500：不足 500 条时不该出现「加载更多」哨兵。
    @MainActor
    func testCanLoadMoreFalseWhenUnderInitialLimit() throws {
        let store = try makeStore(nodeCount: 10)
        let vm = TimelineViewModel(store: store)
        XCTAssertFalse(vm.canLoadMore, "总数不足一页，不该显示加载哨兵")
    }

    /// 超过 500 条时，初始就该能继续加载；点一次（哨兵触发一次）会把 fetchLimit
    /// 推到 1000，但取到的条数受真实总量钳制。
    @MainActor
    func testLoadMoreExtendsFetchLimitAndStopsAtTotal() throws {
        let store = try makeStore(nodeCount: 650)
        let vm = TimelineViewModel(store: store)
        XCTAssertTrue(vm.canLoadMore, "650 条超过首屏 500，应可继续加载")
        XCTAssertEqual(vm.nodes.count, 500)

        vm.loadMore()
        XCTAssertEqual(vm.nodes.count, 650, "总量只有 650，取到头就该停，不多不少")
        XCTAssertFalse(vm.canLoadMore, "已取完全部真实数据，哨兵该消失")
    }

    /// 哨兵可能因为 LazyVStack 的惰性重渲染而对同一次「已经到底」重复触发
    /// （Windows 侧移植时特别提醒的重入场景）；已取完时再调一次必须是无操作，
    /// 不能因为重复触发就把 fetchLimit 越推越高、多发无意义的查询。
    @MainActor
    func testLoadMoreIsNoOpOnceEverythingIsLoaded() throws {
        let store = try makeStore(nodeCount: 300)
        let vm = TimelineViewModel(store: store)
        XCTAssertFalse(vm.canLoadMore, "300 条不足 500，一开始就不该能加载")

        vm.loadMore()
        XCTAssertEqual(vm.nodes.count, 300, "canLoadMore 为假时 loadMore 必须是无操作")
    }
}
