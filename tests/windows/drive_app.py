"""Drive the installed Tempo dashboard (real WebView2) over its test-only remote debugging port.

Start Tempo with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=<port>, then:
    python drive_app.py <port> info
    python drive_app.py <port> check            -> prints the update panel text after the check
    python drive_app.py <port> install          -> presses "ダウンロードして更新", prints states until the app exits
    python drive_app.py <port> shot <file.png> [overview|settings] [light|dark|system]
"""
import sys
import time
from playwright.sync_api import sync_playwright

import os
port, command = sys.argv[1], sys.argv[2]
if os.environ.get("DRIVE_LOG"):
    sys.stdout = open(os.environ["DRIVE_LOG"], "a", encoding="utf-8", buffering=1)
print("==", " ".join(sys.argv[2:]), time.strftime("%H:%M:%S"))


def click(page, selector):
    # DOM click: works while the dashboard window is behind other windows.
    page.wait_for_selector(selector, state="attached", timeout=15000)
    page.evaluate("s => document.querySelector(s).click()", selector)
    page.wait_for_timeout(300)


def connect(pw):
    for _ in range(60):
        try:
            browser = pw.chromium.connect_over_cdp(f"http://127.0.0.1:{port}")
            for ctx in browser.contexts:
                for page in ctx.pages:
                    if page.url.startswith("https://tempo.invalid/"):
                        page.wait_for_selector("#content[aria-busy=false]", timeout=15000)
                        return browser, page
        except Exception:
            pass
        time.sleep(0.5)
    raise SystemExit("dashboard not reachable")


with sync_playwright() as pw:
    browser, page = connect(pw)
    if command == "info":
        click(page, "[data-page=settings]")
        print("version:", page.locator(".group:has(h2:text('ソフトウェアアップデート')) strong").first.inner_text())
        print("notice:", page.locator("#notice").inner_text() if page.locator("#notice").is_visible() else "")
        print("record:", page.locator("#record-text").inner_text())
    elif command == "check":
        click(page, "[data-page=settings]")
        click(page, "[data-update-check]")
        page.wait_for_timeout(1000)
        for _ in range(160):  # the page CSP forbids eval, so poll instead of wait_for_function
            if page.evaluate("() => { const u=document.getElementById('update'); return !!u && !!u.innerText.trim() && !u.querySelector('.spinner'); }"):
                break
            time.sleep(0.25)
        print(page.locator("#update").inner_text().replace("\n", " | "))
    elif command == "install":
        click(page, "[data-update-install]")
        seen = []
        try:
            for _ in range(240):
                text = page.locator("#update").inner_text().replace("\n", " | ")
                if not seen or seen[-1] != text:
                    seen.append(text)
                    print("state:", text[:160])
                if page.locator("#update .error").count():
                    break
                time.sleep(0.25)
        except Exception as e:
            print("dashboard closed:", type(e).__name__)
    elif command == "shot":
        target = sys.argv[4] if len(sys.argv) > 4 else "overview"
        theme = sys.argv[5] if len(sys.argv) > 5 else None
        if theme:
            click(page, "[data-page=settings]")
            page.evaluate("t => { const s=document.querySelector('[data-select=theme]'); s.value=t; s.dispatchEvent(new Event('change',{bubbles:true})); }", theme)
            page.wait_for_timeout(400)
        click(page, f"[data-page={target}]")
        page.wait_for_timeout(600)
        page.screenshot(path=sys.argv[3])
        print("saved", sys.argv[3])
