import XCTest
@testable import AgentTimeline

/// 落库值与显示标签的分离（对齐 Windows `UI/UiText.cs`）。
/// 这是 `design/strings.json` meta 里写死的约束：语言切换**只换渲染**，不动库里一个字节。
final class UiTextTests: XCTestCase {

    override func tearDown() { Strings.load(.system); super.tearDown() }

    /// 键表长度必须与枚举档数一致——少一档会让界面悄悄回显键名。
    func testKeyTablesCoverEveryCase() {
        Strings.load(.zhHans)
        for kind in NodeKind.allCases {
            XCTAssertNotEqual(UiText.kind(kind.rawValue), "", "\(kind) 没有显示标签")
            XCTAssertFalse(UiText.kind(kind.rawValue).hasPrefix("kind."), "\(kind) 回显了键名")
        }
        for status in CodenameStatus.allCases {
            XCTAssertFalse(UiText.status(status).hasPrefix("status."), "\(status) 回显了键名")
        }
    }

    /// 落库值是中文 rawValue，渲染随语言变——但**枚举本身一个字节不动**。
    func testRenderingFollowsLanguageWhileStoredValueDoesNot() {
        Strings.load(.zhHans)
        XCTAssertEqual(UiText.kind("任务"), "任务")
        Strings.load(.en)
        XCTAssertEqual(UiText.kind("任务"), "Task")
        Strings.load(.ja)
        XCTAssertEqual(UiText.kind("任务"), "タスク")

        // 落库值全程不变
        XCTAssertEqual(NodeKind.task.rawValue, "任务")
        XCTAssertEqual(CodenameStatus.completed.rawValue, "完成")
    }

    /// 未知值原样回显——LLM 输出不可信，不能因为不认识就显示空白。
    func testUnknownValueEchoes() {
        Strings.load(.en)
        XCTAssertEqual(UiText.kind("未知档"), "未知档")
        XCTAssertEqual(UiText.status("瞎写的"), "瞎写的")
        XCTAssertEqual(UiText.kind(nil), "")
        XCTAssertEqual(UiText.kind(""), "")
    }

    /// 过滤器「全部」用哨兵串，不用中文词——它同时是选项值和比较判据，
    /// 用显示文本会让比较逻辑随语言漂。哨兵与 Windows 逐字相同。
    func testFilterSentinels() {
        XCTAssertEqual(UiText.allProjects, "::all-projects::")
        XCTAssertEqual(UiText.allKinds, "::all-kinds::")

        Strings.load(.zhHans)
        XCTAssertEqual(UiText.projectOption(UiText.allProjects, compact: true), "全部")
        XCTAssertEqual(UiText.projectOption(UiText.allProjects, compact: false), "全部项目")
        Strings.load(.en)
        XCTAssertEqual(UiText.projectOption(UiText.allProjects, compact: false), "All projects")
        // 真实项目名原样透传，不进翻译
        XCTAssertEqual(UiText.projectOption("agent-timeline", compact: false), "agent-timeline")
    }

    func testKindOptionMapsThroughKindTable() {
        Strings.load(.en)
        XCTAssertEqual(UiText.kindOption(UiText.allKinds, compact: true), "Type")
        XCTAssertEqual(UiText.kindOption("修复", compact: false), "Fix")
    }
}
