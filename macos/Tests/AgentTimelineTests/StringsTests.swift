import XCTest
@testable import AgentTimeline

/// 多语言地基（design/strings.json 双端共享表）。覆盖点对齐 Windows `AppStrings`
/// 的行为契约——两端任何一处不同都会让同一份表在两端译出不同界面。
final class StringsTests: XCTestCase {

    override func tearDown() {
        Strings.load(.system)   // 别把语言状态漏给其它测试
        super.tearDown()
    }

    func testLanguageSelection() {
        Strings.load(.zhHans)
        XCTAssertEqual(Strings.current.language, "zh-Hans")
        XCTAssertEqual(Strings.s("tray.exit"), "退出")

        Strings.load(.en)
        XCTAssertEqual(Strings.s("tray.exit"), "Quit")

        Strings.load(.ja)
        XCTAssertEqual(Strings.s("tray.exit"), "終了")

        Strings.load(.ko)
        XCTAssertEqual(Strings.s("tray.exit"), "종료")
    }

    /// 平台覆盖：先查 `键名@mac` 再回退基准键。macOS 是菜单栏应用，没有托盘。
    func testPlatformOverride() {
        Strings.load(.zhHans)
        XCTAssertEqual(Strings.s("header.hideToTray"), "收进菜单栏")   // @mac 覆盖生效
        Strings.load(.en)
        XCTAssertEqual(Strings.s("header.hideToTray"), "Hide to menu bar")

        // 没有 @mac 覆盖的键走基准值
        Strings.load(.zhHans)
        XCTAssertEqual(Strings.s("tray.showHide"), "显示 / 隐藏")
    }

    /// 查不到键**回显键名**，不返回空串——空串会让界面看起来「少了个控件」，
    /// 键名一眼看出漏了哪个键。
    func testMissingKeyEchoesKeyName() {
        Strings.load(.zhHans)
        XCTAssertEqual(Strings.s("no.such.key"), "no.such.key")
        XCTAssertFalse(Strings.s("no.such.key").isEmpty)
    }

    /// 占位符是 `{0}`/`{1}` 序号式，与 Windows `Format` 逐字同语义。
    func testOrdinalPlaceholders() {
        Strings.load(.zhHans)
        XCTAssertEqual(Strings.f("app.settingsTitle", "0.6.0"), "Agent Timeline 设置 · v0.6.0")
        XCTAssertEqual(Strings.f("timeline.todayWithCount", 7), "今天 · 7 条")

        Strings.load(.en)
        XCTAssertEqual(Strings.f("app.settingsTitle", "0.6.0"), "Agent Timeline Settings · v0.6.0")

        // 替换后不得残留占位符
        for key in ["app.settingsTitle", "timeline.todayWithCount"] {
            XCTAssertFalse(Strings.f(key, "x").contains("{0}"), "\(key) 占位符未被替换")
        }
    }

    /// 表里每个键四语齐全（CI 门禁也查，这里再守一道：门禁校的是 JSON，
    /// 这里校的是**加载后**的运行时视图，能抓住解析层吃掉字段的情况）。
    func testEveryKeyResolvesInEveryLanguage() {
        guard let data = StringsData.json.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let strings = root["strings"] as? [String: [String: String]] else {
            return XCTFail("嵌入副本解析失败")
        }
        XCTAssertGreaterThanOrEqual(strings.count, 69)
        for language in [AppLanguage.zhHans, .en, .ja, .ko] {
            Strings.load(language)
            for key in strings.keys where !key.contains("@") {
                XCTAssertNotEqual(
                    Strings.s(key), key,
                    "\(language.rawValue) 缺键 \(key)（回显了键名）")
            }
        }
    }

    /// 语言切换要发通知，供代码构建的 UI（菜单栏菜单等）重建。
    func testChangeNotification() {
        let fired = expectation(description: "stringsDidChange")
        let token = NotificationCenter.default.addObserver(
            forName: Strings.didChangeNotification, object: nil, queue: nil) { _ in fired.fulfill() }
        defer { NotificationCenter.default.removeObserver(token) }
        Strings.load(.ja)
        wait(for: [fired], timeout: 1)
    }

    /// 设置存的是**字符串** rawValue，不是序号——加语言时旧值语义不会被挪位。
    func testLanguageSettingIsStringBacked() {
        XCTAssertEqual(AppLanguage.system.rawValue, "System")
        XCTAssertEqual(AppLanguage.zhHans.rawValue, "ZhHans")
        XCTAssertEqual(AppLanguage(rawValue: "Ko"), .ko)
        XCTAssertNil(AppLanguage(rawValue: "0"))
    }
}
