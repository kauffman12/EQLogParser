from pathlib import Path
from bs4 import BeautifulSoup
import markdown
import pypandoc  # Requires Pandoc installed
import re

# Constants
INNO_FILE = Path('../EQLogParserInstall/EQLogParserInstall.iss')
DIST_DIR = Path('dist')
RTF_OUT = Path('../EQLogParser/data/releasenotes.rtf')

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

def wrap_docs_html(title: str, header: str, toc: str, content: str) -> str:
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EQLogParser - {title}</title>
  <meta name="description" content="EQLogParser is is a real-time combat analyzer and damage parsing application built specifically for the EverQuest MMO. It monitors and processes in-game log files to provide detailed statistics as well as various utility functions">
  <meta name="robots" content="index, follow">
  <meta name="google-adsense-account" content="ca-pub-4428145487599357">
  <link rel="shortcut icon" href="/favicon.ico">
  <link rel="stylesheet" href="css/style.css?v=2" />
</head>
<body>
  {header}
  {toc}
  <main class="docs-container">
    <section>{content}</section>
  </main>
</body>
</html>"""

def build_toc(toc_title: str, toc_items: str) -> str:
    return f"""<nav class="toc">
<h1>{toc_title}</h1>
<ul>{toc_items}</ul>
</nav>"""

def build_nav_header(version: str) -> str:
    return f"""<nav class="topbar">
<div class="nav-container">
<ul class="nav-links">
  <li><a href="index.html">EQLogParser v{version}</a></li>
</ul>
<ul class="nav-links">
  <li><a href="releasenotes.html">Release Notes</a></li>
  <li><a href="documentation.html">Docs</a></li>
  <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/discussions">Discussion</a></li>
  <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/issues">Issues</a></li>
  <li><a href="policy.html">Privacy</a></li>
</ul>
</div>
</nav>"""

def process_markdown_to_html(input_path: Path, output_path: Path, title: str, toc_title: str, header: str, decorate_h2=False):
    md_text = input_path.read_text(encoding='utf-8')
    html_body = convert_markdown_to_html(md_text)
    soup = BeautifulSoup(html_body, 'html.parser')

    toc_items = ''
    for h1 in soup.find_all('h1'):
        anchor_id = slugify(h1.get_text())
        h1['id'] = anchor_id
        toc_items += f'<li><a href="#{anchor_id}">{h1.get_text()}</a></li>'

    if decorate_h2:
        for h2 in soup.find_all('h2'):
            span = soup.new_tag('span', attrs={"class": 'var'})
            span.string = h2.text
            h2.clear()
            h2.append(span)
            
    toc = ''
    if toc_title != None and toc_items != '':
      toc = build_toc(toc_title, toc_items)

    final_html = wrap_docs_html(title, header, toc, str(soup))
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

def update_index_html(index_path: Path, output_path: Path, header_html: str, url: str):
    soup = BeautifulSoup(index_path.read_text(encoding='utf-8'), 'html.parser')
    nav_bar = soup.find('nav', id='nav-bar')
    if nav_bar:
        nav_bar.clear()
        nav_bar.append(BeautifulSoup(header_html, 'html.parser'))
    download_link = soup.find('a', id='download-link')
    if download_link:
        download_link['href'] = url
    output_path.write_text(str(soup), encoding='utf-8')
    print(f"✅ HTML updated: {output_path.resolve()}")

def main():
    version = get_version_from_inno(INNO_FILE)
    header_html = build_nav_header(version)
    url = f'https://github.com/kauffman12/EQLogParser/raw/master/Release/EQLogParser-install-{version}.exe'

    DIST_DIR.mkdir(exist_ok=True)

    process_markdown_to_html(Path('releasenotes.md'), DIST_DIR / 'releasenotes.html', 'Release Notes', 'Versions', header_html)
    process_markdown_to_html(Path('documentation.md'), DIST_DIR / 'documentation.html', 'Documentation', 'Contents', header_html, decorate_h2=True)
    process_markdown_to_html(Path('policy.md'), DIST_DIR / 'policy.html', 'Privacy Policy', 'Contents', header_html)

    update_index_html(Path('index.tmpl'), DIST_DIR / 'index.html', header_html, url)

    convert_md_to_rtf(Path('releasenotes.md'), RTF_OUT)
    patch_rtf_in_place(RTF_OUT)

if __name__ == "__main__":
    main()
