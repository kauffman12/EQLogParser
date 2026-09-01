# Builds the static site under dist/ (see website/requirements.txt for the venv).
#
#   python build.py            full build: HTML pages, sitemap.xml, release notes RTF
#   python build.py sitemap    regenerate dist/sitemap.xml only

from pathlib import Path
from bs4 import BeautifulSoup
import datetime
import json
import markdown
import pypandoc  # Requires Pandoc installed
import re
import subprocess
import sys
from xml.sax.saxutils import escape

# Constants
INNO_FILE = Path('../EQLogParserInstall/EQLogParserInstall.iss')
DIST_DIR = Path('dist')
RTF_OUT = Path('../EQLogParser/data/releasenotes.rtf')
SITEMAP_OUT = DIST_DIR / 'sitemap.xml'
SITE_BASE_URL = 'https://eqlogparser.kizant.net'
CSS_VERSION = '14'
GA_MEASUREMENT_ID = "G-8QSZ1NGK54"  # GA4 measurement ID for eqlogparser.kizant.net (public value)

# Pages advertised in sitemap.xml, as (url path, source file whose change date
# becomes <lastmod>). status.html is excluded because robots.txt disallows it.
SITEMAP_PAGES = [
    ('/', Path('index.tmpl')),
    ('/getting-started.html', Path('getting-started.md')),
    ('/documentation.html', Path('triggers.md')),
    ('/faq.html', Path('faq.md')),
    ('/releasenotes.html', Path('releasenotes.md')),
    ('/download.html', INNO_FILE),  # generated inline; its content tracks the version
    ('/policy.html', Path('policy.md')),
]

# <title> and meta description for each page, in one place: search results are how anyone
# new finds this site, and every page used to share one generic description. Keep titles
# under ~60 characters and descriptions under ~160 so Google does not truncate them, and
# avoid raw '&' (it belongs escaped in HTML attributes).
PAGE_META = {
    'index.html': ('EQLogParser - EverQuest Combat Log Analyzer and DPS Meter',
              'Free Windows tool that turns your EverQuest combat log into a real-time '
              'damage meter, raid event list, timer overlays and audio triggers.'),
    'getting-started.html': ('Getting Started - Install and Setup Guide | EQLogParser',
                             'Install EQLogParser, open your first EverQuest log, set up audio triggers and overlays, '
                             'import GINA triggers, migrate from NAG and avoid the common gotchas.'),
    'documentation.html': ('Triggers and Regex Reference | EQLogParser',
                           'Regex basics plus the EQLogParser trigger reference: patterns, capture variables, timers, '
                           'text overlays, sounds, custom colours and .NET regex performance tips.'),
    'faq.html': ('FAQ and Support | EQLogParser',
                 'Answers to common EQLogParser problems: spells missing from counts, triggers that never fire, '
                 'overlay colours, OBS capture, EMU servers and Linux support.'),
    'releasenotes.html': ('Release Notes | EQLogParser',
                          'What changed in every EQLogParser release: damage meter fixes, trigger features, timer '
                          'overlays, NAG migration and quality of life updates.'),
    'policy.html': ('Privacy Policy | EQLogParser',
                    'How the EQLogParser website uses cookies and Google AdSense, what is collected, and how to '
                    'reach us about privacy or business questions.'),
    # The download page interpolates the current version into both strings.
    'download.html': ('Download {version} for Windows | EQLogParser',
                      'Download EQLogParser {version} for Windows 10 and 11: free real-time EverQuest combat log '
                      'analyzer with a damage meter, audio triggers and timer overlays.'),
}


def page_meta(output_name: str, version: str) -> tuple:
    """Return (title, description) for a built page, formatted with the release version."""
    title, description = PAGE_META[output_name]
    return title.format(version=version), description.format(version=version)


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

  // Restore the saved table-of-contents preference the same way, before the first paint.
  // Collapsed is the CSS default, so the page never shifts while scripts load.
  try {
    if (localStorage.getItem('toc-open') === '1') {
      document.documentElement.classList.add('toc-open');
    }
  } catch (e) {
    // private browsing: stay with the CSS default
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

  // TOC toggle for narrow screens. CSS keeps it collapsed by default and the head
  // script applies the saved preference before paint, so only a click ever moves
  // content (user-initiated shifts are not penalised as layout shift).
  const tocBtn = document.getElementById('toc-toggle-btn');
  function syncTocLabel() {
    const open = document.documentElement.classList.contains('toc-open');
    tocBtn.textContent = open ? '❌ Hide Contents' : '\u25B6\ufe0f Show Contents';
    tocBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  }
  if (tocBtn) {
    tocBtn.addEventListener('click', function() {
      document.documentElement.classList.toggle('toc-open');
      const open = document.documentElement.classList.contains('toc-open');
      try {
        localStorage.setItem('toc-open', open ? '1' : '0');
      } catch (e) {
        // private browsing: the preference just will not stick
      }
      syncTocLabel();
    });
    syncTocLabel();
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


def build_head(title: str, description: str, version: str, url: str, canonical: str = '') -> str:
    """Build the shared HTML <head> section used by all pages.

    `title` is the complete <title> text (see PAGE_META) and `canonical` is the page's own
    path (empty for the home page) so every URL points at exactly one preferred address;
    `url` stays the installer download link.
    """
    return f"""  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{title}</title>
  <meta name="description" content="{description}" />
  <meta name="robots" content="index, follow" />
  <meta name="google-adsense-account" content="ca-pub-4428145487599357" />
  <meta name="version" content="{version}" />
  <meta name="download" content="{url}" />
  <link rel="shortcut icon" href="/favicon.ico" />
  <link rel="canonical" href="{SITE_BASE_URL}/{canonical}" />
  <link rel="sitemap" type="application/xml" href="{SITE_BASE_URL}/sitemap.xml" />
  <meta name="theme-color" content="#000000" media="(prefers-color-scheme: light)" />
  <meta name="theme-color" content="#111111" media="(prefers-color-scheme: dark)" />
  {PRECONNECT_LINKS}
  <meta property="og:title" content="{title}" />
  <meta property="og:description" content="{description}" />
  <meta property="og:url" content="{SITE_BASE_URL}/{canonical}" />
  <meta property="og:image" content="https://eqlogparser.kizant.net/img/logo.png" />
  <meta property="og:image:width" content="400" />
  <meta property="og:image:height" content="211" />
  <meta property="og:site_name" content="EQLogParser" />
  <meta property="og:type" content="website" />
  <meta name="twitter:card" content="summary_large_image" />
  <meta name="twitter:image" content="https://eqlogparser.kizant.net/img/logo.png" />
  {THEME_HEAD_SCRIPT}
  <link rel="stylesheet" href="css/style.css?v={CSS_VERSION}" />
  {GA_SCRIPT}"""


def build_structured_data(version: str, url: str) -> str:
    """JSON-LD describing the application, for rich results on the home/download pages.

    Version and installer URL come from the same values the rest of the build uses, so the
    markup cannot drift out of date. No aggregateRating: we have no ratings to describe and
    inventing one is a structured-data penalty.
    """
    data = {
        '@context': 'https://schema.org',
        '@type': 'SoftwareApplication',
        'name': 'EQLogParser',
        'url': f'{SITE_BASE_URL}/',
        'image': f'{SITE_BASE_URL}/img/logo.png',
        'description': PAGE_META['index.html'][1],
        'operatingSystem': 'Windows 10, Windows 11',
        'applicationCategory': 'Game',
        'softwareVersion': version,
        'downloadUrl': url,
        'codeRepository': 'https://github.com/kauffman12/EQLogParser',
        'license': 'https://www.apache.org/licenses/LICENSE-2.0',
        'isAccessibleForFree': True,
        'featureList': ['Real-time damage meter', 'Raid event tracking', 'Audio triggers',
                        'Timer and text overlays', 'Trigger import from GINA and NAG'],
        'offers': {'@type': 'Offer', 'price': '0', 'priceCurrency': 'USD'},
    }
    # '<' stays escaped so a '</script>' inside a string cannot end the block early.
    payload = json.dumps(data, ensure_ascii=False, separators=(',', ':')).replace('</', '<\\/')
    return f'<script type="application/ld+json">{payload}</script>'


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

def wrap_docs_html(version: str, url: str, title: str, description: str, nav_header_html: str,
                   toc: str, content: str, canonical: str = '') -> str:
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
{build_head(title, description, version, url, canonical)}
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

# ============================================================
# sitemap.xml
# ============================================================
def get_last_modified(source: Path) -> str:
    """Return an ISO-8601 timestamp (with offset) for the last real change to source.

    Uses the git commit date so a fresh checkout does not report every page as
    freshly changed, and falls back to the file mtime for uncommitted files or
    when git is unavailable.
    """
    mtime = datetime.datetime.fromtimestamp(source.stat().st_mtime).astimezone()
    try:
        status = subprocess.run(['git', 'status', '--porcelain', '--', str(source)],
                                capture_output=True, text=True, timeout=10)
        if status.returncode != 0 or status.stdout.strip():
            return format_iso_timestamp(mtime)  # uncommitted edit: newer than the last commit
        log = subprocess.run(['git', 'log', '-1', '--format=%cI', '--', str(source)],
                             capture_output=True, text=True, timeout=10)
        if log.returncode == 0 and log.stdout.strip():
            return format_iso_timestamp(datetime.datetime.fromisoformat(log.stdout.strip()))
    except (OSError, ValueError, subprocess.SubprocessError):
        pass
    return format_iso_timestamp(mtime)


def format_iso_timestamp(when: datetime.datetime) -> str:
    """Format a datetime the way <lastmod> wants it (W3C date-time, second precision)."""
    return when.replace(microsecond=0).isoformat()


def build_sitemap(pages=SITEMAP_PAGES) -> None:
    """Write dist/sitemap.xml for Google Search Console."""
    DIST_DIR.mkdir(exist_ok=True)
    entries = []
    for path, source in pages:
        built = DIST_DIR / ('index.html' if path == '/' else Path(path).name)
        if not built.exists():
            print(f'⚠️  Warning: {built} not built yet; the sitemap still lists {path}')
        entries.append('  <url>\n'
                       f'    <loc>{escape(SITE_BASE_URL + path)}</loc>\n'
                       f'    <lastmod>{get_last_modified(source)}</lastmod>\n'
                       '  </url>')

    xml = ('<?xml version="1.0" encoding="UTF-8"?>\n'
           '<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'
           + '\n'.join(entries) + '\n</urlset>\n')
    SITEMAP_OUT.write_text(xml, encoding='utf-8')
    print(f'✅ Sitemap generated: {SITEMAP_OUT.resolve()} ({len(entries)} URLs)')


# ============================================================
# Layout shift (CLS) helpers
# ============================================================
def read_image_size(image_path: Path):
    """Return (width, height) for a PNG, GIF or JPEG, or None if not measurable."""
    try:
        data = image_path.read_bytes()
    except OSError:
        return None
    if data[:8] == b'\x89PNG\r\n\x1a\n' and len(data) >= 24:
        return int.from_bytes(data[16:20], 'big'), int.from_bytes(data[20:24], 'big')
    if data[:6] in (b'GIF87a', b'GIF89a'):
        return int.from_bytes(data[6:8], 'little'), int.from_bytes(data[8:10], 'little')
    if data[:2] == b'\xff\xd8':  # JPEG: walk the segment headers to a start-of-frame marker
        start_of_frame = {0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF}
        i = 2
        while i + 9 < len(data):
            if data[i] != 0xFF:
                i += 1
                continue
            marker = data[i + 1]
            if marker in start_of_frame:
                height = int.from_bytes(data[i + 5:i + 7], 'big')
                width = int.from_bytes(data[i + 7:i + 9], 'big')
                return (width, height) if width and height else None
            if marker in (0x01, 0xD8) or 0xD0 <= marker <= 0xD7:
                i += 2
                continue
            segment_length = int.from_bytes(data[i + 2:i + 4], 'big')
            if segment_length < 2:
                return None
            i += 2 + segment_length
    return None


def parse_pixels(value) -> int:
    """Return the pixel count of an HTML length attribute, or 0 when absent or odd."""
    match = re.fullmatch(r'\s*(\d+(?:\.\d+)?)\s*(?:px)?\s*', str(value if value is not None else ''))
    return round(float(match.group(1))) if match else 0


def stamp_image_dimensions(page_path: Path) -> int:
    """Add matching width/height attributes to the images of a built page.

    A browser reserves an image's box as soon as it sees both attributes, so the
    screenshots on the home page no longer push the rest of the page down when
    they finish loading (that shift was the source of the desktop CLS failure).
    An existing width/height pair is left alone; a lone attribute is completed
    from the file's aspect ratio so the rendered box keeps its size.
    """
    soup = BeautifulSoup(page_path.read_text(encoding='utf-8'), 'html.parser')
    stamped = 0
    for img in soup.find_all('img'):
        src = (img.get('src') or '').split('#')[0].split('?')[0]
        if not src or src.startswith(('http://', 'https://', 'data:')):
            continue
        if 'alt' not in img.attrs:
            print(f'⚠️  Warning: {page_path.name}: <img src="{src}"> has no alt text '
                  f'(alt="" is correct for decorative images)')
        declared_width = parse_pixels(img.get('width'))
        declared_height = parse_pixels(img.get('height'))
        if declared_width and declared_height:
            continue
        size = read_image_size(DIST_DIR / src)
        if size is None or not all(size):
            print(f'⚠️  Warning: cannot measure {src} (missing or unsupported format); '
                  f'{page_path.name} will reserve no space for it')
            continue
        width, height = size
        if declared_height:  # honour the author's sizing, derive the matching partner
            width, height = round(width * declared_height / height), declared_height
        elif declared_width:
            width, height = declared_width, round(height * declared_width / width)
        img['width'], img['height'] = width, height
        stamped += 1
    if stamped:
        page_path.write_text(str(soup), encoding='utf-8')
    return stamped


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

def process_markdown_to_html(version: str, url: str, input_path: Path, output_path: Path, title: str, description: str, toc_title: str, nav_header_html: str, decorate_h2=False, toc_builder=None):
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

    final_html = wrap_docs_html(version, url, title, description, nav_header_html, toc, str(soup), output_path.name)
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
    # <title> and description come from PAGE_META so the copy lives in exactly one place;
    # the template keeps a fallback for anyone previewing index.tmpl directly. status.html
    # has no entry, so it keeps whatever its template says.
    if output_path.name in PAGE_META:
        title, description = page_meta(output_path.name, version)
        if soup.title:
            soup.title.string = title
        for property_name in ('og:title', 'og:description'):
            expected = title if property_name == 'og:title' else description
            meta_tag = soup.find('meta', attrs={'property': property_name})
            if meta_tag:
                meta_tag['content'] = expected

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

    # Theme restoration goes first in <head>, before the CSS link, so there is no flash of
    # the wrong theme; the GA tag follows it inside <head> exactly where Google tells you to
    # put it (a body placement fires later and can miss quick bounces).
    head = soup.find('head')
    if head:
        for script in BeautifulSoup(GA_SCRIPT, 'html.parser').find_all('script'):
            head.append(script)
        if output_path.name in PAGE_META:
            for tag in BeautifulSoup(build_structured_data(version, url), 'html.parser').contents:
                head.append(tag)
        for script in BeautifulSoup(THEME_HEAD_SCRIPT, 'html.parser').find_all('script'):
            head.insert(0, script)

    # The theme/menu/TOC handlers need the DOM, so they stay before </body>
    body = soup.find('body')
    if body:
        for script in BeautifulSoup(THEME_SCRIPT, 'html.parser').find_all('script'):
            body.append(script)
    output_path.write_text(str(soup), encoding='utf-8')
    print(f"✅ HTML updated: {output_path.resolve()}")

def build_download_page(version: str, url: str, nav_header_html: str) -> str:
    """Generate the download landing page with auto-download and tracking."""
    title, description = page_meta('download.html', version)
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
{build_head(title, description, version, url, 'download.html')}
  {build_structured_data(version, url)}
  <link rel="preload" as="image" href="img/logo.png" fetchpriority="high" />
</head>
<body>
  <nav class="topbar">{nav_header_html}</nav>
  <div class="layout no-toc">
    <main class="container">
      <!-- Hero Section (same as home page, without download button) -->
      <section class="hero center-section">
        <img src="img/logo.png" alt="EQLogParser — real-time combat analyzer for EverQuest" class="hero-logo" loading="eager" fetchpriority="high" width="400" height="211" />
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

def main(argv):
    if 'sitemap' in argv[1:]:
        build_sitemap()
        return

    version = get_version_from_inno(INNO_FILE)
    home_header_html = build_nav_header()
    header_html = build_nav_header()
    url = f'https://github.com/kauffman12/EQLogParser/releases/download/{version}/EQLogParser-install-{version}.exe'

    DIST_DIR.mkdir(exist_ok=True)

    def meta(page_name):
        return page_meta(page_name, version)

    title, description = meta('releasenotes.html')
    process_markdown_to_html(version, url, Path('releasenotes.md'), DIST_DIR / 'releasenotes.html', title, description, None, header_html, toc_builder=build_releasenotes_year_toc)
    title, description = meta('getting-started.html')
    process_markdown_to_html(version, url, Path('getting-started.md'), DIST_DIR / 'getting-started.html', title, description, 'Contents', header_html, decorate_h2=True)
    title, description = meta('documentation.html')
    process_markdown_to_html(version, url, Path('triggers.md'), DIST_DIR / 'documentation.html', title, description, 'Contents', header_html, decorate_h2=True)
    title, description = meta('faq.html')
    process_markdown_to_html(version, url, Path('faq.md'), DIST_DIR / 'faq.html', title, description, 'Contents', header_html, decorate_h2=True)
    title, description = meta('policy.html')
    process_markdown_to_html(version, url, Path('policy.md'), DIST_DIR / 'policy.html', title, description, 'Contents', header_html)

    update_index_html(version, url, Path('index.tmpl'), DIST_DIR / 'index.html', home_header_html)
    update_index_html(version, url, Path('status.tmpl'), DIST_DIR / 'status.html', header_html)

    # Generate download landing page with auto-download
    download_html = build_download_page(version, url, header_html)
    (DIST_DIR / 'download.html').write_text(download_html, encoding='utf-8')
    print(f'✅ HTML generated: {(DIST_DIR / "download.html").resolve()}')

    # Reserve space for every image so late-loading screenshots cannot shift the page
    stamped = sum(stamp_image_dimensions(page) for page in sorted(DIST_DIR.glob('*.html')))
    print(f'✅ Image dimensions stamped on {stamped} <img> tags')

    build_sitemap()

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
    main(sys.argv)
