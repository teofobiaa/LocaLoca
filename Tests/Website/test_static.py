from __future__ import annotations

import unittest
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit

import html5lib
import tinycss2


REPO_ROOT = Path(__file__).resolve().parents[2]
SITE_DIR = REPO_ROOT / "site"
HTML_FILE = SITE_DIR / "index.html"
CSS_FILE = SITE_DIR / "styles.css"


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

        cls.html = HTML_FILE.read_text(encoding="utf-8")
        cls.css = CSS_FILE.read_text(encoding="utf-8")
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
