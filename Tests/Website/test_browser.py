from __future__ import annotations

import functools
import os
import threading
import unittest
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

from playwright.sync_api import Browser, Page, Playwright, sync_playwright


REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_DIR = REPO_ROOT / "site"
AXE_CORE_PATH = os.environ.get("AXE_CORE_PATH")


class QuietRequestHandler(SimpleHTTPRequestHandler):
    def log_message(self, format: str, *args: object) -> None:
        pass


class WebsiteBrowserTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        handler = functools.partial(QuietRequestHandler, directory=str(SITE_DIR))
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), handler)
        cls.server_thread = threading.Thread(
            target=cls.server.serve_forever,
            name="local-site-server",
            daemon=True,
        )
        cls.server_thread.start()
        cls.base_url = f"http://127.0.0.1:{cls.server.server_port}"

        cls.playwright: Playwright = sync_playwright().start()
        cls.browser: Browser = cls.playwright.chromium.launch(headless=True)

    @classmethod
    def tearDownClass(cls) -> None:
        cls.browser.close()
        cls.playwright.stop()
        cls.server.shutdown()
        cls.server.server_close()
        cls.server_thread.join(timeout=5)

    def setUp(self) -> None:
        self.context = self.browser.new_context(
            viewport={"width": 1365, "height": 900},
            color_scheme="light",
        )
        self.page: Page = self.context.new_page()
        self.page_errors: list[str] = []
        self.page.on("pageerror", lambda error: self.page_errors.append(str(error)))

    def tearDown(self) -> None:
        self.context.close()

    def open_homepage(self) -> None:
        response = self.page.goto(
            self.base_url,
            wait_until="domcontentloaded",
            timeout=20_000,
        )
        self.assertIsNotNone(response, "The browser did not receive a navigation response.")
        self.assertEqual(response.status, 200)

    def test_homepage_loads_with_expected_content_and_stylesheet(self) -> None:
        self.open_homepage()

        self.assertIn("Loca Loca", self.page.title())
        self.assertTrue(self.page.get_by_role("heading", level=1).is_visible())
        self.assertTrue(self.page.locator("main").is_visible())

        stylesheet = self.page.request.get(f"{self.base_url}/styles.css")
        self.assertEqual(stylesheet.status, 200)
        self.assertIn("text/css", stylesheet.headers.get("content-type", ""))

        self.assertEqual(
            self.page_errors,
            [],
            f"JavaScript errors occurred while loading the page: {self.page_errors}",
        )

    def test_main_navigation_points_to_existing_sections(self) -> None:
        self.open_homepage()

        expected_targets = ("features", "workflow", "data", "install")
        for target in expected_targets:
            link = self.page.locator(f'.main-nav a[href="#{target}"]')
            self.assertEqual(link.count(), 1, f"Missing navigation link for #{target}.")
            link.click()
            self.assertEqual(
                self.page.evaluate("window.location.hash"),
                f"#{target}",
            )

    def test_theme_toggle_is_accessible_and_persistent(self) -> None:
        self.page.add_init_script(
            "if (!localStorage.getItem('loca-loca-theme')) localStorage.setItem('loca-loca-theme', 'light');"
        )
        self.open_homepage()

        toggle = self.page.get_by_role(
            "button",
            name="Тёмная тема",
        )
        toggle.click()

        self.assertEqual(
            self.page.locator("html").get_attribute("data-theme"),
            "dark",
        )
        self.assertEqual(
            self.page.get_by_role(
                "button",
                name="Светлая тема",
            ).get_attribute("aria-pressed"),
            "true",
        )
        self.assertEqual(
            self.page.evaluate("localStorage.getItem('loca-loca-theme')"),
            "dark",
        )

        self.page.reload(wait_until="domcontentloaded")
        self.assertEqual(
            self.page.locator("html").get_attribute("data-theme"),
            "dark",
            "The selected theme must survive a page reload.",
        )

    def test_theme_toggle_works_with_keyboard(self) -> None:
        self.page.add_init_script(
            "localStorage.setItem('loca-loca-theme', 'light');"
        )
        self.open_homepage()

        toggle = self.page.get_by_role(
            "button",
            name="Тёмная тема",
        )
        toggle.focus()
        self.page.keyboard.press("Enter")

        self.assertEqual(
            self.page.locator("html").get_attribute("data-theme"),
            "dark",
        )

    def test_faq_can_be_opened(self) -> None:
        self.open_homepage()

        faq_item = self.page.locator("details").nth(1)
        faq_item.locator("summary").click()
        self.assertTrue(faq_item.evaluate("(element) => element.open"))

    def test_mobile_layout_has_no_horizontal_overflow(self) -> None:
        self.open_homepage()
        self.page.set_viewport_size({"width": 390, "height": 844})

        dimensions = self.page.evaluate(
            """() => ({
                viewport: document.documentElement.clientWidth,
                document: document.documentElement.scrollWidth
            })"""
        )
        self.assertLessEqual(
            dimensions["document"],
            dimensions["viewport"],
            f"Mobile layout overflows horizontally: {dimensions}",
        )
        self.assertFalse(
            self.page.locator(".main-nav").is_visible(),
            "The desktop navigation should collapse on a narrow screen.",
        )

    def test_accessibility_in_light_and_dark_themes(self) -> None:
        self.assertTrue(
            AXE_CORE_PATH and Path(AXE_CORE_PATH).is_file(),
            "AXE_CORE_PATH must point to the installed axe-core/axe.min.js file.",
        )
        # Accessibility checks must inspect stable final colors, not an in-flight
        # CSS transition between the two themes.
        self.page.emulate_media(reduced_motion="reduce")
        self.open_homepage()
        self.page.add_script_tag(path=AXE_CORE_PATH)

        def run_audit() -> list[dict[str, object]]:
            return self.page.evaluate(
                """async () => {
                    const result = await axe.run(document, {
                        runOnly: {
                            type: "tag",
                            values: [
                                "wcag2a",
                                "wcag2aa",
                                "wcag21a",
                                "wcag21aa",
                                "wcag22aa",
                                "best-practice"
                            ]
                        }
                    });
                    return result.violations.map((violation) => ({
                        id: violation.id,
                        impact: violation.impact,
                        help: violation.help,
                        nodes: violation.nodes.map((node) => ({
                            target: node.target,
                            summary: node.failureSummary
                        }))
                    }));
                }"""
            )

        # Let the browser finish applying the current theme before axe reads
        # computed foreground/background colors.
        self.page.evaluate(
            """() => new Promise((resolve) => {
                requestAnimationFrame(() => requestAnimationFrame(resolve));
            })"""
        )
        light_violations = run_audit()
        self.assertEqual(
            light_violations,
            [],
            f"axe-core found accessibility violations in light theme: {light_violations}",
        )

        self.page.get_by_role(
            "button",
            name="Тёмная тема",
        ).click()
        self.assertEqual(
            self.page.locator("html").get_attribute("data-theme"),
            "dark",
            "The dark-theme accessibility audit must run with dark theme enabled.",
        )
        self.page.evaluate(
            """() => new Promise((resolve) => {
                requestAnimationFrame(() => requestAnimationFrame(resolve));
            })"""
        )
        dark_violations = run_audit()
        self.assertEqual(
            dark_violations,
            [],
            f"axe-core found accessibility violations in dark theme: {dark_violations}",
        )


if __name__ == "__main__":
    unittest.main(verbosity=2)
