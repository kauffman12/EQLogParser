"""Checks over website/dist so broken output cannot be deployed silently.

Run as part of build.py, or on its own:  python sitecheck.py [dist]
Exits non-zero when a problem is found, and returns the list of messages.
"""

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from urllib.robotparser import RobotFileParser

from bs4 import BeautifulSoup

# Utility/error pages stay out of search and the sitemap, but must remain crawlable so
# search engines can read their noindex tags. A robots.txt block cannot prevent indexing.
NON_INDEXABLE = {'status.html', '404.html'}

# Keep in sync with the client id used by build.py and dist/ads.txt.
ADSENSE_PUBLISHER_ID = 'ca-pub-4428145487599357'

SITEMAP_NS = '{http://www.sitemaps.org/schemas/sitemap/0.9}'
ATOM_NS = '{http://www.w3.org/2005/Atom}'


def find_problems(dist_dir: Path) -> list:
    """Return human readable problems found in the built site (empty means healthy)."""
    problems = []
    pages = sorted(dist_dir.glob('*.html'))
    if not pages:
        return [f'no HTML pages found in {dist_dir}']

    for page in pages:
        soup = BeautifulSoup(page.read_text(encoding='utf-8'), 'html.parser')
        problems += _page_problems(page, soup)
        problems += _indexing_problems(page, soup)
        problems += _link_problems(page, soup, dist_dir)

    problems += _robots_problems(dist_dir)
    problems += _sitemap_problems(dist_dir)
    problems += _feed_problems(dist_dir)
    problems += _adstxt_problems(dist_dir)
    return problems


def _page_problems(page: Path, soup: BeautifulSoup) -> list:
    """Title, description, nav, image and ad-slot checks for a single page."""
    problems = []
    title_tag = soup.find('title')
    title = title_tag.get_text().strip() if title_tag else ''

    # The whole site would be useless without its nav bar; the templates rebuild it
    # from build.py, so a template edit can silently drop it.
    if soup.find(id='nav-links') is None:
        problems.append(f'{page.name}: navigation header is missing (no #nav-links)')

    if not title:
        problems.append(f'{page.name}: no <title>')
    if soup.select('meta[name="description"]') == [] and page.name not in NON_INDEXABLE:
        problems.append(f'{page.name}: no meta description')

    for img in soup.find_all('img'):
        src = img.get('src') or '(no src)'
        if 'alt' not in img.attrs:
            problems.append(f'{page.name}: <img src="{src}"> has no alt text '
                            f'(alt="" is correct for decorative images)')
        if not (img.get('width') and img.get('height')):
            problems.append(f'{page.name}: <img src="{src}"> has no width/height, so the '
                            f'layout shifts while it loads (CLS)')

    for ins in soup.select('ins.adsbygoogle'):
        if ins.get('data-full-width-responsive') != 'false':
            problems.append(f'{page.name}: ad slot allows full-width resizing; a unit that '
                            f'grows after render is layout shift')
        style = (ins.get('style') or '').replace(' ', '')
        if 'width' not in style or 'height' not in style:
            problems.append(f'{page.name}: ad slot has no fixed inline width/height (CLS)')

    return problems


def _link_problems(page: Path, soup: BeautifulSoup, dist_dir: Path) -> list:
    """Every local href/src and in-page anchor must resolve."""
    problems = []
    ids = {tag.get('id') for tag in soup.find_all(attrs={'id': True})}

    for anchor in soup.select('a[href^="#"]'):
        target = anchor['href'][1:]
        if target and target not in ids:
            problems.append(f'{page.name}: link to #{target}, but no element has that id')

    referenced = [(tag, tag['href']) for tag in soup.find_all(['a', 'link', 'script'], href=True)]
    referenced += [(tag, tag['src']) for tag in soup.find_all(['img', 'source'], src=True)]

    for tag, raw in referenced:
        target = raw.split('#')[0].split('?')[0].strip()
        if not target or _is_external(target):
            continue
        path = (dist_dir / target.lstrip('/')).resolve()
        if not path.exists():
            problems.append(f'{page.name}: <{tag.name}> points at {raw}, which is not in dist/')

    return problems


def _is_external(url: str) -> bool:
    """True for anything we do not serve ourselves (http:, mailto:, data:, //host)."""
    return url.startswith('//') or re.match(r'^[a-zA-Z][a-zA-Z0-9+.-]*:', url) is not None


def _indexing_problems(page: Path, soup: BeautifulSoup) -> list:
    """Public pages may be indexed; utility pages must tell crawlers not to index them."""
    noindex_agents = set()
    for tag in soup.find_all('meta', attrs={'name': True}):
        agent = tag['name'].lower()
        if agent not in {'robots', 'googlebot', 'bingbot'}:
            continue
        directives = set(re.split(r'[,\s]+', tag.get('content', '').lower()))
        if directives & {'noindex', 'none'}:
            noindex_agents.add(agent)

    if page.name in NON_INDEXABLE:
        return [] if 'robots' in noindex_agents else [f'{page.name}: utility page needs a robots noindex tag']
    return [f'{page.name}: public page has a noindex directive for {agent}' for agent in sorted(noindex_agents)]


def _robots_problems(dist_dir: Path) -> list:
    """Crawlers need access both to public content and to utility pages' noindex tags."""
    robots = dist_dir / 'robots.txt'
    if not robots.exists():
        return ['robots.txt is missing']
    rules = RobotFileParser()
    rules.parse(robots.read_text(encoding='utf-8').splitlines())
    paths = {'/'} | {'/' + page.name for page in dist_dir.glob('*.html')}
    return [f'robots.txt blocks {agent} from {path}; crawlers must be able to read page indexing directives'
            for path in sorted(paths) for agent in ('Googlebot', 'Bingbot', '*')
            if not rules.can_fetch(agent, path)]


def _sitemap_problems(dist_dir: Path) -> list:
    sitemap = dist_dir / 'sitemap.xml'
    if not sitemap.exists():
        return ['sitemap.xml was not generated']
    try:
        root = ET.parse(sitemap).getroot()
    except ET.ParseError as error:
        return [f'sitemap.xml is not valid XML: {error}']

    locations = [node.text.strip() for node in root.findall(f'.//{SITEMAP_NS}loc')]
    if not locations:
        return ['sitemap.xml lists no URLs']

    problems = []
    listed = set()
    for loc in locations:
        relative = loc.split('://', 1)[1].split('/', 1)[1] if '://' in loc else loc
        name = relative or 'index.html'  # the bare site root stands for index.html
        listed.add(name)
        if name in NON_INDEXABLE:
            problems.append(f'sitemap.xml lists {name}, which must stay out of search')
        if not (dist_dir / name).exists():
            problems.append(f'sitemap.xml lists {loc}, but that page is not in dist/')

    for page in sorted(dist_dir.glob('*.html')):
        if page.name in NON_INDEXABLE or page.name == '404.html':
            continue
        if page.name not in listed:
            problems.append(f'{page.name} is built but missing from sitemap.xml')
    return problems


def _feed_problems(dist_dir: Path) -> list:
    feed = dist_dir / 'feed.xml'
    if not feed.exists():
        return ['feed.xml was not generated']
    try:
        root = ET.parse(feed).getroot()
    except ET.ParseError as error:
        return [f'feed.xml is not valid XML: {error}']

    entries = root.findall(f'{ATOM_NS}entry')
    if not entries:
        return ['feed.xml contains no entries']

    problems = []
    notes = dist_dir / 'releasenotes.html'
    ids = {tag.get('id') for tag in BeautifulSoup(notes.read_text(encoding='utf-8'),
                                                 'html.parser').find_all(attrs={'id': True})} if notes.exists() else set()
    for entry in entries:
        link = entry.find(f'{ATOM_NS}link')
        href = link.get('href') if link is not None else ''
        if '#' in href and href.rsplit('#', 1)[1] not in ids:
            problems.append(f'feed.xml entry links to {href}, whose anchor is not in releasenotes.html')
    return problems


def _adstxt_problems(dist_dir: Path) -> list:
    ads_txt = dist_dir / 'ads.txt'
    if not ads_txt.exists():
        return ['ads.txt is missing; AdSense will not serve ads without it']
    content = ads_txt.read_text(encoding='utf-8')
    problems = []
    # ads.txt lists the publisher without the 'ca-' prefix that the page snippet uses.
    publisher = ADSENSE_PUBLISHER_ID.removeprefix('ca-')
    if publisher not in content:
        problems.append(f'ads.txt does not mention {publisher} used by the page ad slots')
    if 'DIRECT' not in content.upper():
        problems.append('ads.txt has no DIRECT line for the publisher')
    return problems


def main(argv) -> int:
    dist_dir = Path(argv[1]) if len(argv) > 1 else Path('dist')
    problems = find_problems(dist_dir)
    for problem in problems:
        print(f'❌ {problem}')
    if problems:
        print(f'\n{len(problems)} problem(s) found in {dist_dir.resolve()}')
        return 1
    print(f'✅ Site checks passed ({len(list(dist_dir.glob("*.html")))} pages)')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
