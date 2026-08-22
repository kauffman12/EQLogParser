from pathlib import Path
from bs4 import BeautifulSoup
import markdown
import pypandoc  # Requires Pandoc installed
import re

# Constants
INNO_FILE = Path('../EQLogParserInstall/EQLogParserInstall.iss')
DIST_DIR = Path('dist')
RTF_OUT = Path('../EQLogParser/data/releasenotes.rtf')
CSS_VERSION = '12'
GA_MEASUREMENT_ID = "G-XXXXXXXXXX"  # Replace with your actual GA4 Measurement ID

# Inline script to restore theme preference before CSS loads (prevents flash of wrong theme)
THEME_HEAD_SCRIPT = '''<script>
(function() {
  // Use cookie for cross-page persistence (works with file:// protocol too)
  function getTheme() {
    var match = document.cookie.match(/(?:^|; )theme=([^;]+)/);
    return match ? decodeURIComponent(match[1]) : null;
  }
  var savedTheme = getTheme();
  if (savedTheme === 'dark') {
    document.documentElement.classList.add('dark');
  }
})();
</script>'''

# Google Analytics 4 tracking script (injected into all pages)
GA_SCRIPT = f'''<script async src="https://www.googletagmanager.com/gtag/js?id={GA_MEASUREMENT_ID}"></script>
<script>
  window.dataLayer = window.dataLayer || [];
  function gtag(){{dataLayer.push(arguments);}}
  gtag('js', new Date());
  gtag('config', '{GA_MEASUREMENT_ID}');
</script>'''

# Dark mode toggle + mobile menu script (injected into all pages)
THEME_SCRIPT = '''<script>
(function() {
  const toggle = document.getElementById('theme-toggle');
  const menuToggle = document.getElementById('menu-toggle');
  const navLinks = document.getElementById('nav-links');

  // Restore saved theme preference — default to light mode
  function getTheme() {
    var match = document.cookie.match(/(?:^|; )theme=([^;]+)/);
    return match ? decodeURIComponent(match[1]) : null;
  }
  function setTheme(theme) {
    document.cookie = 'theme=' + encodeURIComponent(theme) + '; path=/';
  }
  const savedTheme = getTheme();
  if (savedTheme === 'dark') {
    document.documentElement.classList.add('dark');
    if (toggle) toggle.textContent = '\\u2600\\ufe0f';
  }

  if (toggle) {
    toggle.addEventListener('click', function() {
      document.documentElement.classList.toggle('dark');
      const isDark = document.documentElement.classList.contains('dark');
      setTheme(isDark ? 'dark' : 'light');
      toggle.textContent = isDark ? '\\u2600\\ufe0f' : '\\ud83c\\udf1c';
    });
  }

  if (menuToggle && navLinks) {
    menuToggle.addEventListener('click', function() {
      navLinks.classList.toggle('open');
    });
  }

  // TOC toggle for narrow screens — collapse by default, expand on button click
  const toc = document.querySelector('.toc');
  const tocBtn = document.getElementById('toc-toggle-btn');
  if (toc && tocBtn) {
    // Collapse TOC by default on narrow screens
    if (window.innerWidth <= 900)
      document.body.classList.add('toc-collapsed');

    tocBtn.addEventListener('click', function() {
      document.body.classList.toggle('toc-collapsed');
      this.textContent = document.body.classList.contains('toc-collapsed')
        ? '\u25B6\ufe0f Show Contents'
        : '❌ Hide Contents';
    });

    // Restore saved TOC preference
    const tocState = localStorage.getItem('toc-collapsed');
    if (tocState === 'true') {
      document.body.classList.add('toc-collapsed');
      tocBtn.textContent = '\u25B6\ufe0f Show Contents';
    }
  }
})();
</script>'''

# Preconnect hints for external domains (AdSense, GA4) — improves load performance
PRECONNECT_LINKS = '''<link rel="preconnect" href="https://www.googletagmanager.com" />
<link rel="preconnect" href="https://pagead2.googlesyndication.com" />
<link rel="preconnect" href="https://www.google-analytics.com" />'''

# AdSense right-rail skyscraper ad unit (reused across pages)
def adsense_skyscraper():
    return '''<script async src="https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js?client=ca-pub-4428145487599357" crossorigin="anonymous"></script>
      <ins class="adsbygoogle"
        style="display:inline-block; width:160px; height:600px"
        data-ad-client="ca-pub-4428145487599357"
        data-ad-slot="9757256233"></ins>
      <script>(adsbygoogle = window.adsbygoogle || []).push({});</script>'''


def build_head(title: str, description: str, version: str, url: str) -> str:
    """Build the shared HTML <head> section used by all pages."""
    return f"""  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EQLogParser - {title}</title>
  <meta name="description" content="{description}" />
  <meta name="robots" content="index, follow" />
  <meta name="google-adsense-account" content="ca-pub-4428145487599357" />
  <meta name="version" content="{version}" />
  <meta name="download" content="{url}" />
  <link rel="shortcut icon" href="/favicon.ico" />
  {PRECONNECT_LINKS}
  <meta property="og:title" content="EQLogParser - {title}" />
  <meta property="og:description" content="{description}" />
  <meta property="og:image" content="https://eqlogparser.kizant.net/img/logo.png" />
  <meta property="og:type" content="website" />
  <meta name="twitter:card" content="summary_large_image" />
  {THEME_HEAD_SCRIPT}
  <link rel="stylesheet" href="css/style.css?v={CSS_VERSION}" />
  {GA_SCRIPT}"""

def get_version_from_inno(file_path: Path) -> str:
    content = file_path.read_text(encoding='utf-8')
    match = re.search(r'#define\s+MyAppVersion\s+"([^"]+)"', content)
    if not match:
        raise ValueError("Version not found in Inno Setup file.")
    return match.group(1)

def slugify(text: str) -> str:
    return re.sub(r'\W+', '-', text.strip().lower()).strip('-')

def convert_markdown_to_html(md_text: str) -> str:
    return markdown.markdown(md_text, extensions=["extra"])

def wrap_docs_html(version: str, url: str, title: str, nav_header_html: str, toc: str, content: str) -> str:
    description = "EQLogParser is a real-time combat analyzer and damage parsing application built specifically for the EverQuest MMO. It monitors and processes in-game log files to provide detailed statistics as well as various utility functions"
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
{build_head(title, description, version, url)}
</head>
<body>
  <nav class="topbar">{nav_header_html}</nav>
  <div class="layout">
  <button id="toc-toggle-btn" class="toc-toggle-btn">Show Contents</button>
  {toc}
    <main class="docs-container">
      <section>{content}</section>
    </main>
    <nav class="rightad">
      {adsense_skyscraper()}
    </nav>
  </div>
  <footer class="site-footer">
    <a href="policy.html">Privacy Policy</a> | © 2025 EQLogParser
  </footer>
  {THEME_SCRIPT}
</body>
</html>"""

def build_toc(toc_title: str, toc_items: str) -> str:
    return f"""<nav class="toc">
<h1>{toc_title}</h1>
<ul>{toc_items}</ul>
</nav>"""

def build_empty_toc() -> str:
    return """<nav class="toc"></nav>"""

# Release notes H1 format: "2.3.60 | 08/18/26" — used to group releases by year
RELEASE_NOTES_HEADER_RE = re.compile(r'^\S+\s*\|\s*\d{2}/\d{2}/(\d{2})$')

def build_releasenotes_year_toc(soup) -> str:
    """Build a 'browse by year' nav for the release notes page.

    A full TOC would list 60+ releases, so instead we anchor to the first
    release of each year. Existing H1 id slugs are left untouched so the
    version-hash anchor compatibility script keeps working."""
    first_h1_by_year = {}
    for h1 in soup.find_all('h1'):
        match = RELEASE_NOTES_HEADER_RE.match(h1.get_text().strip())
        if not match:
            continue
        year = 2000 + int(match.group(1))
        if year not in first_h1_by_year:
            anchor = soup.new_tag('span', attrs={'id': f'year-{year}'})
            h1.insert_before(anchor)
            first_h1_by_year[year] = h1
    if not first_h1_by_year:
        return build_empty_toc()

    items = ''.join(f'<li><a href="#year-{year}">{year}</a></li>' for year in sorted(first_h1_by_year, reverse=True))
    older = soup.find('h1', string=lambda s: s and s.strip().startswith('2.2.x'))
    if older is not None:
        items += f'<li><a href="#{older["id"]}">Older versions (2.1.x–2.2.x)</a></li>'
    return build_toc('Browse by Year', items)

def build_nav_header() -> str:
    """Build the inner nav content (without outer <nav> wrapper).
    The outer <nav class="topbar"> is added by the template or wrap_docs_html."""
    links_start = '<div class="nav-container">\n<ul class="nav-links" id="nav-links">\n'
    all_links = """<li><a href="index.html">Home</a></li>
  <li><a href="getting-started.html">Getting Started</a></li>
  <li><a href="documentation.html">Triggers &amp; Regex</a></li>
  <li><a href="faq.html">FAQ &amp; Support</a></li>
  <li><a href="releasenotes.html">Release Notes</a></li>
  <li>|</li>
  <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/discussions">Discussion</a></li>
  <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/issues">Issues</a></li>"""
    nav_end = """</ul>
<button id="theme-toggle" class="theme-toggle" aria-label="Toggle dark mode">\U0001F31C</button>
<button id="menu-toggle" class="menu-toggle" aria-label="Toggle navigation menu">\u2630</button>
</div>"""
    return links_start + all_links + nav_end

def process_markdown_to_html(version: str, url: str, input_path: Path, output_path: Path, title: str, toc_title: str, nav_header_html: str, decorate_h2=False, toc_builder=None):
    md_text = input_path.read_text(encoding='utf-8')
    html_body = convert_markdown_to_html(md_text)
    soup = BeautifulSoup(html_body, 'html.parser')

    toc_items = ''
    for h1 in soup.find_all('h1'):
        item = h1.get_text()
        anchor_id = slugify(item)
        h1['id'] = anchor_id
        toc_items += f'<li><a href="#{anchor_id}">{item}</a></li>'

    # Ensure H2 elements have IDs for anchor linking (but don't add to TOC)
    for h2 in soup.find_all('h2'):
        if not h2.get('id'):
            h2['id'] = slugify(h2.get_text())

    if decorate_h2:
        for h2 in soup.find_all('h2'):
            span = soup.new_tag('span', attrs={"class": 'var'})
            span.string = h2.text
            h2.clear()
            h2.append(span)

    toc = ''
    if toc_builder is not None:
      toc = toc_builder(soup)
    elif toc_title != None and toc_items != '':
      toc = build_toc(toc_title, toc_items)
    else:
      toc = build_empty_toc()

    final_html = wrap_docs_html(version, url, title, nav_header_html, toc, str(soup))
    output_path.write_text(final_html, encoding='utf-8')
    print(f'✅ HTML generated: {output_path.resolve()}')

def convert_md_to_rtf(md_file: Path, rtf_file: Path):
    pypandoc.convert_file(md_file, to='rtf', format='md', outputfile=str(rtf_file), extra_args=['-s'])
    print(f"✅ RTF generated: {rtf_file.resolve()}")

def patch_rtf_in_place(file_path: Path):
    new_header = r'{\rtf1\ansi\ansicpg1252\deff0\nouicompat{\fonttbl{\f0\fnil\fcharset0 Segoe UI;}{\f1\fnil Segoe UI;}}'
    lines = file_path.read_text(encoding='cp1252').splitlines()

    modified = []
    for i, line in enumerate(lines):
        if i == 0:
            modified.append(new_header)
            continue
        if r'\fs36' in line:
            line = line.replace(r'\fs36', r'\fs24')
        elif r'\f0' in line and not re.search(r'\\fs\d+', line):
            line = line.replace(r'\f0', r'\f0 \fs20', 1)
            line = line.replace(r'\li720', r'\li1080', 1)
            line = line.replace(r'\li360', r'\li720', 1)
        modified.append(line)

    file_path.write_text('\n'.join(modified), encoding='cp1252')
    print(f"✅ RTF patched: {file_path.resolve()}")

def update_index_html(version: str, url: str, index_path: Path, output_path: Path, nav_header_html: str):
    soup = BeautifulSoup(index_path.read_text(encoding='utf-8'), 'html.parser')
    version_meta = soup.find("meta", attrs={"name": "version"})
    if version_meta:
        version_meta["content"] = version
    download_meta = soup.find("meta", attrs={"name": "download"})
    if download_meta:
       download_meta["content"] = url
    nav_bar = soup.find('nav', id='nav-bar')
    if nav_bar:
        nav_bar.clear()
        nav_bar.append(BeautifulSoup(nav_header_html, 'html.parser'))
    version_text = soup.find('span', id='version-text')
    if version_text:
        version_text.string = version

    # Update CSS version in template (keeps cache-busting in sync with build_head)
    css_link = soup.find('link', attrs={'rel': 'stylesheet'})
    if css_link and css_link.get('href'):
        css_link['href'] = f'css/style.css?v={CSS_VERSION}'

    # Note: the home page download button points to download.html directly in the template.
    # The actual GitHub URL is used on the download landing page itself.

    # Inject theme restoration script into <head> (before CSS) to prevent flash of wrong theme
    head = soup.find('head')
    if head:
        head_script = BeautifulSoup(THEME_HEAD_SCRIPT, 'html.parser')
        for script in head_script.find_all('script'):
            head.insert(0, script)

    # Inject GA tracking and theme scripts before </body> using BeautifulSoup
    body = soup.find('body')
    if body:
        script_tags = BeautifulSoup(GA_SCRIPT + THEME_SCRIPT, 'html.parser')
        for script in script_tags.find_all('script'):
            body.append(script)
    output_path.write_text(str(soup), encoding='utf-8')
    print(f"✅ HTML updated: {output_path.resolve()}")

def build_download_page(version: str, url: str, nav_header_html: str) -> str:
    """Generate the download landing page with auto-download and tracking."""
    title = f"Download v{version}"
    description = f"Download EQLogParser v{version} for Windows. Real-time combat analyzer and damage parser for EverQuest."
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
{build_head(title, description, version, url)}
</head>
<body>
  <nav class="topbar">{nav_header_html}</nav>
  <div class="layout no-toc">
    <main class="container">
      <!-- Hero Section (same as home page, without download button) -->
      <section class="hero center-section">
        <img src="img/logo.png" class="hero-logo" loading="lazy" />
        <p class="hero-subtitle">Real-time combat analyzer &amp; damage parser for EverQuest</p>
      </section>

      <!-- Download + System Requirements (compact two-column layout) -->
      <section class="download-layout">
        <div class="download-col download-main">
          <div class="download-card">
            <h2>Downloading <span class="version-badge-inline">{version}</span></h2>
            <p class="muted">The installer is starting in a few seconds...</p>
            <div class="download-btn-wrapper">
              <a id="auto-download" class="btn btn-download" href="{url}">
                <span id="countdown">Starting download in 3...</span>
              </a>
            </div>
            <p class="small muted">
              <a href="{url}" id="manual-download">Click here</a> if your download doesn't start.
            </p>
          </div>
        </div>
        <div class="download-col sys-reqs">
          <div class="info-section">
            <h2>System Requirements</h2>
            <ul>
              <li><strong>Windows 10/11</strong> (64-bit only)</li>
              <li><strong>.NET 8.0 Desktop Runtime</strong> — the installer will prompt you to install it if missing</li>
              <li><strong>EverQuest</strong> — installed and configured with logging enabled</li>
              <li>Approximately 50 MB of disk space</li>
            </ul>
            <p class="center"><a href="releasenotes.html" class="btn">View release notes</a></p>
          </div>
        </div>
      </section>
    </main>
    <nav class="rightad">
      {adsense_skyscraper()}
    </nav>
  </div>

  <script>
  (function() {{
    const DOWNLOAD_URL = "{url}";
    let seconds = 3;
    const countdownEl = document.getElementById('countdown');
    const manualLink = document.getElementById('manual-download');
    const autoDownload = document.getElementById('auto-download');

    function trackDownload(method) {{
      if (typeof gtag !== 'undefined') {{
        gtag('event', 'download', {{
          'event_category': 'engagement',
          'event_label': method,
          'version': '{version}'
        }});
      }}
    }}

    const timer = setInterval(function() {{
      seconds--;
      if (seconds <= 0) {{
        clearInterval(timer);
        trackDownload('auto');
        window.location.href = DOWNLOAD_URL;
      }} else {{
        countdownEl.textContent = 'Starting download in ' + seconds + '...';
      }}
    }}, 1000);

    manualLink.href = DOWNLOAD_URL;
    manualLink.onclick = function() {{ trackDownload('manual'); }};
    autoDownload.onclick = function(e) {{ e.preventDefault(); trackDownload('button-click'); window.location.href = DOWNLOAD_URL; }};
  }})();
  </script>
  <footer class="site-footer">
    <a href="policy.html">Privacy Policy</a> | © 2025 EQLogParser
  </footer>
  {THEME_SCRIPT}
</body>
</html>"""

def main():
    version = get_version_from_inno(INNO_FILE)
    home_header_html = build_nav_header()
    header_html = build_nav_header()
    url = f'https://github.com/kauffman12/EQLogParser/releases/download/{version}/EQLogParser-install-{version}.exe'

    DIST_DIR.mkdir(exist_ok=True)

    process_markdown_to_html(version, url, Path('releasenotes.md'), DIST_DIR / 'releasenotes.html', 'Release Notes', None, header_html, toc_builder=build_releasenotes_year_toc)
    process_markdown_to_html(version, url, Path('getting-started.md'), DIST_DIR / 'getting-started.html', 'Getting Started', 'Contents', header_html, decorate_h2=True)
    process_markdown_to_html(version, url, Path('triggers.md'), DIST_DIR / 'documentation.html', 'Triggers & Regex Reference', 'Contents', header_html, decorate_h2=True)
    process_markdown_to_html(version, url, Path('faq.md'), DIST_DIR / 'faq.html', 'FAQ & Support', 'Contents', header_html, decorate_h2=True)
    process_markdown_to_html(version, url, Path('policy.md'), DIST_DIR / 'policy.html', 'Privacy Policy', 'Contents', header_html)

    update_index_html(version, url, Path('index.tmpl'), DIST_DIR / 'index.html', home_header_html)
    update_index_html(version, url, Path('status.tmpl'), DIST_DIR / 'status.html', header_html)

    # Generate download landing page with auto-download
    download_html = build_download_page(version, url, header_html)
    (DIST_DIR / 'download.html').write_text(download_html, encoding='utf-8')
    print(f'✅ HTML generated: {(DIST_DIR / "download.html").resolve()}')



    # Inject backward-compatible version hash resolution into releasenotes.html
    # The app generates URLs like #2-3-58 but the actual anchors include dates (e.g. #2-3-58-07-25-26)
    rn_path = DIST_DIR / 'releasenotes.html'
    if rn_path.exists():
        rn_script = '''<script>
(function() {
  var hash = window.location.hash;
  if (!hash) return;
  // If the exact anchor doesn't exist, try matching by version prefix
  if (!document.getElementById(hash.substring(1))) {
    var id = hash.substring(1);
    var match = document.querySelector('[id^="' + id + '"]');
    if (match) {
      window.location.hash = match.id;
    }
  }
})();
</script>'''
        soup = BeautifulSoup(rn_path.read_text(encoding='utf-8'), 'html.parser')
        body = soup.find('body')
        if body:
            script_tag = soup.new_tag('script')
            script_tag.string = '''
(function() {
  var hash = window.location.hash;
  if (!hash) return;
  // If the exact anchor doesn't exist, try matching by version prefix
  if (!document.getElementById(hash.substring(1))) {
    var id = hash.substring(1);
    var match = document.querySelector('[id^="' + id + '"]');
    if (match) {
      window.location.hash = match.id;
    }
  }
})();'''
            body.append(script_tag)
        rn_path.write_text(str(soup), encoding='utf-8')

    convert_md_to_rtf(Path('releasenotes.md'), RTF_OUT)
    patch_rtf_in_place(RTF_OUT)

if __name__ == "__main__":
    main()
