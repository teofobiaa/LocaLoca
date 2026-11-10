# Website tests

The static website is published from site/ with GitHub Pages. Its checks live here so test files are not copied into the published site.

## What is checked

- HTML5 parsing, required page metadata, unique IDs and valid in-page navigation targets.
- Existence of local assets and alt attributes on every image.
- Heading hierarchy and an accessible name for the primary navigation.
- CSS syntax, including rules nested inside media queries.
- Chromium smoke tests with Playwright: page load, stylesheet response, section navigation, FAQ, theme persistence, keyboard activation and mobile overflow.
- axe-core accessibility scans in light and dark themes against WCAG 2.0/2.1/2.2 A/AA rules and axe best practices.

The axe audit is an automated aid, not a statement of full WCAG conformance. Keyboard and screen-reader review still requires manual testing.

## Run locally

From the repository root:

~~~~bash
python -m venv .venv
# Windows PowerShell: .venv\Scripts\Activate.ps1
# macOS/Linux: source .venv/bin/activate
python -m pip install -r Tests/Website/requirements.txt
python -m playwright install chromium

# Install axe-core into a local tools directory.
npm install --prefix ./.website-test-tools --no-save --no-package-lock axe-core@4.14.0

# Set AXE_CORE_PATH:
# Windows PowerShell:
$env:AXE_CORE_PATH = (Resolve-Path ./.website-test-tools/node_modules/axe-core/axe.min.js).Path
# macOS/Linux:
export AXE_CORE_PATH="$(pwd)/.website-test-tools/node_modules/axe-core/axe.min.js"

python -m unittest discover -s Tests/Website -p "test_*.py" -v
~~~~
