from __future__ import annotations

import unittest
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
from urllib.request import Request, urlopen

import html5lib
import tinycss2


REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_DIR = REPO_ROOT / "site"
HTML_FILE = SITE_DIR / "index.html"
CSS_FILE = SITE_DIR / "styles.css"
PRIVACY_FILE = SITE_DIR / "privacy.html"
PRIVACY_CSS_FILE = SITE_DIR / "styles-privacy.css"


class SiteMarkupParser(HTMLParser):
    """Collect the parts of the page that can be checked without a browser."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.ids: list[str] = []
        self.duplicate_ids: list[str] = []
        self.headings: list[int] = []
        self.images: list[dict[str, str | None]] = []
        self.links: list[str] = []
        self.local_resources: list[str] = []
        self.tags: list[str] = []
        self.meta: list[dict[str, str | None]] = []
        self.nav_labels: list[str] = []
        self.html_attrs: dict[str, str | None] = {}

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        self.tags.append(tag)
        if tag == "html":
            self.html_attrs = values

        element_id = values.get("id")
        if element_id:
            if element_id in self.ids:
                self.duplicate_ids.append(element_id)
            self.ids.append(element_id)

        if tag in {"h1", "h2", "h3", "h4", "h5", "h6"}:
            self.headings.append(int(tag[1]))

        if tag == "img":
            self.images.append(values)

        if tag == "a" and values.get("href"):
            self.links.append(values["href"] or "")

        if tag in {"img", "script", "source"} and values.get("src"):
            self.local_resources.append(values["src"] or "")

        if tag == "link" and values.get("href"):
            self.local_resources.append(values["href"] or "")

        if tag == "meta":
            self.meta.append(values)

        if tag == "nav" and values.get("aria-label"):
            self.nav_labels.append(values["aria-label"] or "")


def _css_parse_errors(rules: list[object], source: str) -> list[str]:
    errors: list[str] = []

    for rule in rules:
        rule_type = getattr(rule, "type", None)
        if rule_type == "error":
            errors.append(f"{source}:{rule.source_line}: {rule.message}")
            continue

        if rule_type == "qualified-rule":
            declarations = tinycss2.parse_declaration_list(
                rule.content,
                skip_whitespace=True,
                skip_comments=True,
            )
            for declaration in declarations:
                if declaration.type == "error":
                    errors.append(
                        f"{source}:{declaration.source_line}: {declaration.message}"
                    )
            continue

        if rule_type == "at-rule" and rule.content is not None:
            if rule.at_keyword in {
                "media",
                "supports",
                "layer",
                "container",
                "document",
                "keyframes",
                "-webkit-keyframes",
                "starting-style",
            }:
                nested_rules = tinycss2.parse_rule_list(
                    rule.content,
                    skip_whitespace=True,
                    skip_comments=True,
                )
                errors.extend(_css_parse_errors(nested_rules, source))
            else:
                declarations = tinycss2.parse_declaration_list(
                    rule.content,
                    skip_whitespace=True,
                    skip_comments=True,
                )
                for declaration in declarations:
                    if declaration.type == "error":
                        errors.append(
                            f"{source}:{declaration.source_line}: {declaration.message}"
                        )

    return errors


class WebsiteStaticTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        if not HTML_FILE.is_file():
            raise FileNotFoundError(f"Website entry point not found: {HTML_FILE}")
        if not CSS_FILE.is_file():
            raise FileNotFoundError(f"Website stylesheet not found: {CSS_FILE}")
        if not PRIVACY_FILE.is_file():
            raise FileNotFoundError(f"Privacy policy page not found: {PRIVACY_FILE}")
        if not PRIVACY_CSS_FILE.is_file():
            raise FileNotFoundError(f"Privacy stylesheet not found: {PRIVACY_CSS_FILE}")

        cls.html = HTML_FILE.read_text(encoding="utf-8")
        cls.css = CSS_FILE.read_text(encoding="utf-8")
        cls.privacy_html = PRIVACY_FILE.read_text(encoding="utf-8")
        cls.privacy_css = PRIVACY_CSS_FILE.read_text(encoding="utf-8")
        cls.parser = SiteMarkupParser()
        cls.parser.feed(cls.html)
        cls.parser.close()

    def test_html_is_valid_html5(self) -> None:
        parser = html5lib.HTMLParser(strict=True, namespaceHTMLElements=False)
        parser.parse(self.html)

    def test_required_document_metadata_exists(self) -> None:
        self.assertIn("html", self.parser.tags)
        self.assertIn("main", self.parser.tags)
        self.assertEqual(self.html.count("<h1"), 1, "The page must have one primary heading.")

        self.assertEqual(self.parser.html_attrs.get("lang"), "ru")

        title = self.html.split("<title>", 1)[-1].split("</title>", 1)[0].strip()
        self.assertTrue(title, "The document title must not be empty.")

        viewport = [
            item
            for item in self.parser.meta
            if item.get("name", "").lower() == "viewport"
        ]
        description = [
            item
            for item in self.parser.meta
            if item.get("name", "").lower() == "description"
        ]
        self.assertTrue(viewport, "A responsive viewport declaration is required.")
        self.assertTrue(description, "A search description is required.")
        self.assertTrue(description[0].get("content", "").strip())

    def test_ids_are_unique_and_local_fragments_resolve(self) -> None:
        self.assertEqual(
            self.parser.duplicate_ids,
            [],
            f"Duplicate HTML id attributes: {self.parser.duplicate_ids}",
        )

        known_ids = set(self.parser.ids)
        missing_fragments: list[str] = []
        for href in self.parser.links:
            parsed = urlsplit(href)
            if not parsed.fragment:
                continue
            if parsed.scheme or parsed.netloc:
                continue
            if parsed.path not in {"", "/", "index.html"}:
                continue
            if unquote(parsed.fragment) not in known_ids:
                missing_fragments.append(href)

        self.assertEqual(
            missing_fragments,
            [],
            f"Links point to missing page sections: {missing_fragments}",
        )

    def test_local_assets_exist(self) -> None:
        missing: list[str] = []

        for reference in self.parser.local_resources:
            parsed = urlsplit(reference)
            if parsed.scheme or parsed.netloc or reference.startswith("data:"):
                continue

            relative_path = unquote(parsed.path)
            if not relative_path:
                continue

            target = SITE_DIR / relative_path.lstrip("/")
            if not target.is_file():
                missing.append(reference)

        self.assertEqual(missing, [], f"Missing local site assets: {missing}")


    def test_privacy_commitments_are_visible_on_homepage(self) -> None:
        self.assertIn("Не продаём персональные данные", self.html)
        self.assertIn("Не используем данные для рекламы", self.html)
        self.assertIn('href="privacy.html"', self.html)

    def test_latest_release_installer_download_resolves(self) -> None:
        """The public CTA must resolve directly to the latest-release installer."""
        download_url = (
            "https://github.com/teofobiaa/LocaLoca/releases/latest/download/"
            "LocaLoca-Setup.exe"
        )
        headers = {
            "Accept": "application/octet-stream",
            "User-Agent": "LocaLoca-Website-CI",
            "Range": "bytes=0-0",
        }

        # The latest/download endpoint itself resolves the release asset. Avoid
        # the separate unauthenticated Releases API request: its low anonymous
        # rate limit made this test fail intermittently with HTTP 403.
        request = Request(download_url, headers=headers)
        with urlopen(request, timeout=60) as response:
            self.assertIn(
                response.status,
                {200, 206},
                "The latest-release installer URL must return a successful response.",
            )
            self.assertEqual(
                response.headers.get_filename(),
                "LocaLoca-Setup.exe",
                "The download response must identify the expected installer filename.",
            )
            response.read(1)

    def test_images_have_alt_attributes(self) -> None:
        missing_alt = [
            image.get("src", "<image without src>")
            for image in self.parser.images
            if "alt" not in image
        ]
        self.assertEqual(
            missing_alt,
            [],
            f"Every img element must have an alt attribute: {missing_alt}",
        )

    def test_heading_levels_do_not_skip(self) -> None:
        self.assertTrue(self.parser.headings, "The page must contain headings.")
        self.assertEqual(self.parser.headings[0], 1, "The first heading must be h1.")

        skipped_levels = [
            (previous, current)
            for previous, current in zip(
                self.parser.headings,
                self.parser.headings[1:],
            )
            if current > previous + 1
        ]
        self.assertEqual(
            skipped_levels,
            [],
            f"Heading hierarchy skips levels: {skipped_levels}",
        )

    def test_primary_navigation_has_an_accessible_name(self) -> None:
        self.assertIn(
            "Основная навигация",
            self.parser.nav_labels,
            "The primary navigation landmark must have an accessible name.",
        )


    def test_privacy_policy_contains_core_disclosures(self) -> None:
        for phrase in (
            "не продаёт",
            "не сдаёт в аренду",
            "рекламного профилирования",
            "рекламным сетям",
            "16 лет",
            "10 рабочих дней",
            "24 часов",
            "72 часов",
        ):
            self.assertIn(phrase, self.privacy_html.lower())

    def test_privacy_policy_is_complete_and_matches_full_structure(self) -> None:
        expected_headings = (
            "## 1. Общие положения",
            "### 1.1. Основные термины и определения",
            "## 2. Категории обрабатываемых персональных данных",
            "### 2.1. Данные учётной записи",
            "### 2.2. Данные профиля",
            "### 2.3. Данные дневника и пользовательский контент",
            "### 2.4. Данные социальных функций",
            "### 2.5. Технические данные",
            "## 3. Цели обработки персональных данных",
            "## 4. Правовые основания обработки",
            "## 5. Способы и действия по обработке персональных данных",
            "## 6. Хранение и инфраструктура",
            "## 7. Передача персональных данных третьим лицам",
            "### 7.1. Запрет продажи и использования персональных данных в рекламных целях",
            "## 8. Трансграничная передача персональных данных",
            "## 9. Видимость профиля и пользовательского контента",
            "## 10. Сроки хранения персональных данных",
            "## 11. Права пользователей",
            "## 12. Защита персональных данных",
            "## 13. Несовершеннолетние",
            "## 14. Изменение Политики",
            "## 15. Контактная информация",
        )
        for heading in expected_headings:
            self.assertIn(heading, self.privacy_html)
        for technical_term in (
            "Хеширование пароля",
            "Хеш пароля",
            "Пользовательская сессия",
            "IP-адрес",
            "OAuth",
            "Инцидент безопасности персональных данных",
        ):
            self.assertIn(technical_term, self.privacy_html)
        self.assertNotIn("cdn.jsdelivr.net", self.privacy_html)

    def test_privacy_page_is_valid_html5(self) -> None:
        parser = html5lib.HTMLParser(strict=True, namespaceHTMLElements=False)
        parser.parse(self.privacy_html)

    def test_privacy_page_css_has_no_parse_errors(self) -> None:
        rules = tinycss2.parse_stylesheet(self.privacy_css, skip_whitespace=True, skip_comments=True)
        errors = _css_parse_errors(rules, str(PRIVACY_CSS_FILE.relative_to(REPO_ROOT)))
        self.assertEqual(errors, [], "\\n".join(errors))

    def test_css_has_no_parse_errors(self) -> None:
        rules = tinycss2.parse_stylesheet(
            self.css,
            skip_whitespace=True,
            skip_comments=True,
        )
        errors = _css_parse_errors(rules, str(CSS_FILE.relative_to(REPO_ROOT)))
        self.assertEqual(errors, [], "\n".join(errors))


if __name__ == "__main__":
    unittest.main(verbosity=2)
