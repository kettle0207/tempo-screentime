"""Exercise the packaged dashboard sources in Chromium/Edge with the production history classes.

Runs twice: with seeded history and with an empty first-run database. Screenshots go to
docs/screens when TEMPO_SCREENS=1. Uses Microsoft Edge (same engine as WebView2) by default;
set CHROMIUM to a browser executable to override.
"""
import json
import os
import subprocess
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

root = Path(os.path.abspath(__file__)).parents[1]
sdk = os.environ.get("DOTNET", "dotnet")
screens = root / "docs/screens"
shoot = bool(os.environ.get("TEMPO_SCREENS"))
subprocess.run([sdk, "build", str(root / "tests/Tempo.Tests.csproj"), "-c", "Release", "-v", "q",
                "-p:UseSharedCompilation=false"], check=True)
CSP = ("default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src data:; "
       "connect-src 'none'; frame-src 'none'; base-uri 'none'; form-action 'none'")
FILES = {"https://tempo.invalid/": ("index.html", "text/html"),
         "https://tempo.invalid/style.css": ("style.css", "text/css"),
         "https://tempo.invalid/app.js": ("app.js", "text/javascript")}


class Driver:
    def __init__(self, empty):
        args = [sdk, str(root / "tests/bin/Release/net8.0/Tempo.Tests.dll"), "--ui"] + (["--empty"] if empty else [])
        self.proc = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")

    def query(self, message):
        if message.get("action", "").startswith("update-"):
            return {"request": message["request"], "ok": True}
        self.proc.stdin.write(json.dumps(message) + "\n")
        self.proc.stdin.flush()
        return json.loads(self.proc.stdout.readline())

    def close(self):
        self.proc.stdin.close()
        self.proc.wait(timeout=15)


def open_page(browser, driver, errors, outbound, **context):
    ctx = browser.new_context(locale="ja-JP", **context)
    page = ctx.new_page()
    page.on("pageerror", lambda err: errors.append(str(err)))
    page.on("console", lambda msg: errors.append(msg.text) if msg.type == "error" else None)
    page.expose_function("invokeNative", driver.query)
    page.add_init_script("""let callback;
      window.__push = m => callback({data:m});
      window.chrome = window.chrome || {};
      window.chrome.webview={addEventListener:(_,fn)=>callback=fn,
      postMessage: async m=>callback({data:await window.invokeNative(m)})};""")

    def respond(route):
        url = route.request.url
        if url not in FILES:
            outbound.append(url)
            route.abort()
            return
        file, mime = FILES[url]
        route.fulfill(path=str(root / "src/UI" / file), content_type=mime + "; charset=utf-8",
                      headers={"Content-Security-Policy": CSP})
    page.route("**/*", respond)
    page.goto("https://tempo.invalid/")
    expect(page.locator("#total")).to_be_visible()
    page.wait_for_timeout(250)
    return ctx, page


def snap(page, name):
    if shoot:
        screens.mkdir(parents=True, exist_ok=True)
        page.wait_for_timeout(250)
        page.screenshot(path=str(screens / (name + ".png")), full_page=True)


def set_theme(page, theme):
    page.locator("[data-page=settings]").click()
    page.locator("[data-select=theme]").select_option(theme)
    expect(page.locator("html")).to_have_attribute("data-theme", theme)
    page.locator("[data-page=overview]").click()


def no_overflow(page, label):
    for panel in ("overview", "apps", "sites", "settings"):
        page.locator(f"[data-page={panel}]").click()
        assert page.evaluate("document.documentElement.scrollWidth <= innerWidth"), (label, panel)
        # Times and names stay on one line and inside their rows.
        bad = page.evaluate("""[...document.querySelectorAll('.row')].filter(r=>{
            const t=r.querySelector('.time').getBoundingClientRect(), b=r.getBoundingClientRect();
            return t.right>b.right+0.5 || t.height>30;}).length""")
        assert bad == 0, (label, panel, bad)
    page.locator("[data-page=overview]").click()


def seeded(browser):
    driver = Driver(empty=False)
    errors, outbound = [], []
    try:
        ctx, page = open_page(browser, driver, errors, outbound, viewport={"width": 1100, "height": 820})
        expect(page.locator(".bar")).to_have_count(24)
        expect(page.locator("[data-list=apps] .row")).to_have_count(5)
        expect(page.locator(".legend span")).not_to_have_count(0)
        expect(page.locator(".compare")).to_contain_text("前日より")
        expect(page.locator("#next")).to_be_disabled()
        # Rows align: every time column shares one right edge.
        edges = page.evaluate("[...document.querySelectorAll('.time')].map(t=>Math.round(t.getBoundingClientRect().right))")
        assert len(set(edges)) == 1, edges
        icons = page.evaluate("[...document.querySelectorAll('.row .appicon')].map(t=>Math.round(t.getBoundingClientRect().left))")
        assert len(set(icons)) == 1, icons
        set_theme(page, "light"); snap(page, "light-overview-day")
        for period, expected in [("week", 7), ("year", 12), ("month", None), ("day", 24)]:
            page.locator(f"[data-period={period}]").click()
            if expected:
                expect(page.locator(".bar")).to_have_count(expected)
            else:
                expect(page.locator(".bar")).not_to_have_count(12)
                assert 28 <= page.locator(".bar").count() <= 31
            if period == "week":
                expect(page.locator(".compare")).to_contain_text("1日平均")
                expect(page.locator(".avg")).to_be_visible()
                snap(page, "light-overview-week")
        page.locator("[data-period=week]").click()
        page.locator(".bar[data-day]").first.click()
        expect(page.locator("[data-period=day]")).to_have_attribute("aria-pressed", "true")
        page.locator("#today").click() if page.locator("#today").is_visible() else None
        page.locator("#back").click()
        expect(page.locator("#next")).to_be_enabled()
        page.locator("#today").click()
        expect(page.locator("#next")).to_be_disabled()
        page.locator("#date").fill("2020-01-01")
        expect(page.locator(".emptyplot")).to_be_visible()
        expect(page.locator(".compare")).to_contain_text("この期間の記録はありません")
        page.locator("#today").click()
        expect(page.locator("[data-list=apps] .row")).to_have_count(5)
        page.locator("[data-all]").click()
        snap(page, "light-apps")
        page.locator("#filter").fill("visual")
        expect(page.locator(".row")).to_have_count(1)
        page.locator("[data-edit]").click()
        page.locator("input[name=category][value=study]").check()
        page.locator("#save").click()
        expect(page.locator("dialog")).not_to_be_visible()
        expect(page.locator(".categorylabel")).to_have_text("勉強")
        page.locator("[data-page=sites]").click()
        expect(page.locator(".row")).to_have_count(2)
        page.locator("[data-page=settings]").click()
        expect(page.locator(".group h2", has_text="ソフトウェアアップデート")).to_be_visible()
        expect(page.locator(".settingrow strong", has_text="Tempo 1.2.0")).to_be_visible()
        page.locator("[data-update-check]").click()
        page.evaluate("__push({update:{state:'checking'}})")
        expect(page.locator("[data-update-check]")).to_be_disabled()
        page.evaluate("__push({update:{state:'latest',current:'1.2.0'}})")
        expect(page.locator("#update")).to_contain_text("最新です")
        page.evaluate("""__push({update:{state:'available',current:'1.2.0',version:'1.3.0',date:'2026-12-10',size:742000,
            notes:'- 週の表示で日を選べるようになりました\\n- 起動時間を短縮しました\\n- <script>alert(1)</script> はそのまま表示',portable:false}})""")
        expect(page.locator("#update h3")).to_have_text("Tempo 1.3.0")
        expect(page.locator("#update .notes li")).to_have_count(3)
        assert page.locator("#update script").count() == 0
        expect(page.locator("[data-update-install]")).to_be_visible()
        snap(page, "light-settings-update-available")
        page.evaluate("__push({update:{state:'downloading',version:'1.3.0',size:742000,received:371000,total:742000}})")
        expect(page.locator(".progress")).to_have_attribute("aria-valuenow", "50")
        snap(page, "light-settings-update-downloading")
        page.evaluate("__push({update:{state:'error',version:'1.3.0',message:'ダウンロードが途中で切断されました。現在の版を引き続き使用できます。'}})")
        expect(page.locator("#update .error")).to_contain_text("現在の版")
        page.evaluate("__push({update:{state:'error',message:'更新情報の署名が正規の配布元と一致しません。更新を中止しました。'}})")
        expect(page.locator("#update .error")).to_contain_text("署名")
        expect(page.locator("[data-update-check]")).to_be_enabled()
        page.evaluate("__push({update:{state:'idle'}})")
        page.locator("[data-toggle=paused]").click()
        expect(page.locator("#record-text")).to_have_text("一時停止中")
        page.locator("[data-toggle=paused]").click()
        page.locator("[data-toggle=startup]").click()
        expect(page.locator("[data-toggle=startup]")).to_have_attribute("aria-checked", "false")
        page.locator("[data-select=idle]").select_option("180")
        expect(page.locator("[data-select=idle]")).to_have_value("180")
        expect(page.locator("[data-select=idle] option")).to_have_text(["1 分", "3 分", "5 分", "30 分", "1 時間"])
        for value in ("1800", "3600"):
            page.locator("[data-select=idle]").select_option(value)
            expect(page.locator("[data-select=idle]")).to_have_value(value)
        snap(page, "light-settings")

        set_theme(page, "dark")
        snap(page, "dark-overview-day")
        page.locator("[data-period=week]").click(); snap(page, "dark-overview-week"); page.locator("[data-period=day]").click()
        page.locator("[data-page=apps]").click(); snap(page, "dark-apps")
        page.locator("[data-page=settings]").click()
        page.evaluate("""__push({update:{state:'available',current:'1.2.0',version:'1.3.0',date:'2026-12-10',size:742000,
            notes:'- 週の表示で日を選べるようになりました\\n- 起動時間を短縮しました',portable:false}})""")
        snap(page, "dark-settings-update-available")
        page.locator("[data-page=overview]").click()
        for theme in ("light", "dark"):
            set_theme(page, theme)
            for width in (610, 780, 1100):
                page.set_viewport_size({"width": width, "height": 760})
                no_overflow(page, (theme, width))
        page.set_viewport_size({"width": 610, "height": 900})
        set_theme(page, "light"); snap(page, "light-narrow-overview")
        set_theme(page, "dark"); snap(page, "dark-narrow-overview")
        ctx.close()

        # High DPI: 150% and 200% device scale, as WebView2 renders on scaled monitors.
        for scale in (1.5, 2):
            ctx, page = open_page(browser, driver, errors, outbound, viewport={"width": 900, "height": 760}, device_scale_factor=scale)
            no_overflow(page, ("dpi", scale))
            if scale == 2:
                set_theme(page, "light"); snap(page, "light-overview-200pct")
            ctx.close()
    finally:
        driver.close()
    assert not errors, errors
    assert not outbound, outbound
    print("PASS seeded: 4 periods, day drill-down, history, ranking alignment, search, category edit, sites, settings, "
          "update states (checking/latest/available/downloading/error, notes escaped), light/dark, 3 widths x 4 pages x 2 themes, 150%/200% DPI, zero external requests")


def empty(browser):
    driver = Driver(empty=True)
    errors, outbound = [], []
    try:
        for theme in ("light", "dark"):
            ctx, page = open_page(browser, driver, errors, outbound, viewport={"width": 1100, "height": 820},
                                  color_scheme=theme)
            expect(page.locator("#total")).to_have_text("0分")
            expect(page.locator(".compare")).to_contain_text("記録を開始しました")
            expect(page.locator(".emptyplot")).to_be_visible()
            expect(page.locator(".bar")).to_have_count(24)
            expect(page.locator(".legend")).to_have_count(0)
            expect(page.locator(".empty")).to_contain_text("アプリの記録はありません")
            assert page.locator("[data-all]").count() == 0
            expect(page.locator("html")).to_have_attribute("data-theme", theme)
            snap(page, f"{theme}-empty-overview")
            for period in ("week", "month", "year"):
                page.locator(f"[data-period={period}]").click()
                expect(page.locator(".emptyplot")).to_be_visible()
                assert "NaN" not in page.locator("#content").inner_text()
            page.locator("[data-period=day]").click()
            page.locator("[data-page=apps]").click()
            expect(page.locator(".empty")).to_be_visible()
            page.locator("[data-page=sites]").click()
            expect(page.locator(".empty")).to_contain_text("拡張機能")
            if theme == "light":
                snap(page, "light-empty-sites")
            for width in (610, 1100):
                page.set_viewport_size({"width": width, "height": 760})
                no_overflow(page, ("empty", theme, width))
            ctx.close()
    finally:
        driver.close()
    assert not errors, errors
    assert not outbound, outbound
    print("PASS empty first run: 0分, first-run message, empty chart for 4 periods, empty lists, light/dark, 2 widths, no NaN")


with sync_playwright() as pw:
    exe = os.environ.get("CHROMIUM")
    browser = pw.chromium.launch(executable_path=exe or None, channel=None if exe else "msedge",
                                 args=["--disable-background-networking", "--force-color-profile=srgb"])
    seeded(browser)
    empty(browser)
    browser.close()
