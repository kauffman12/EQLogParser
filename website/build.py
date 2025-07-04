from pathlib import Path
from bs4 import Tag
from bs4 import BeautifulSoup
import markdown
import re

header = f"""<nav class="topbar">
<div class="nav-container">
  <ul class="nav-links">
    <li><a href="index.html">EQLogParser v2.3.0</a></li>
  </ul>
  <ul class="nav-links">
    <li><a href="documentation.html">Docs</a></li>
    <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/discussions">Discussion</a></li>
    <li><a target="_blank" href="https://github.com/kauffman12/EQLogParser/issues">Issues</a></li>
  </ul>
</div>
</nav>"""

def convert_markdown_to_html(md_text: str) -> str:
  """Converts markdown text to HTML body content."""
  return markdown.markdown(md_text, extensions=["extra"])
    
def slugify(text):
  # Create a URL-safe ID from the header text
  return re.sub(r'\W+', '-', text.strip().lower()).strip('-')    

def wrap_docs_html(toc_items: str, content: str) -> str:
  """Wraps the HTML content with the required full HTML structure and CSS."""
  return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EQLogParser</title>
  <meta name="description" content="EQLogParser is is a real-time combat analyzer and damage parsing application built specifically for the EverQuest MMO. It monitors and processes in-game log files to provide detailed statistics as well as various utility functions">
  <meta name="robots" content="index, follow">
  <link rel="stylesheet" href="css/style.css" />
</head>
<body>
  {header}
  <nav class="toc">
    <h1>Contents</h1>
    <ul>
      {toc_items}
    </ul>
  </nav>
  <main class="docs-container">
    <section>
      {content}
    </section>
  </main>
</body>
</html>"""

def main():
  ### GENERATE DOCS
  input_md = Path("documentation.md")
  docs_html = Path("documentation.html")

  md_text = input_md.read_text(encoding="utf-8")
  html_content = convert_markdown_to_html(md_text)
  soup = BeautifulSoup(html_content, "html.parser")
  toc_items = ''
    
  # add anchors to headings
  for h1 in soup.find_all("h1"):
    header_text = h1.get_text()
    anchor_id = slugify(header_text)
    h1['id'] = anchor_id
    toc_items += f'<li><a href="#{anchor_id}">{header_text}</a></li>'

  for h2 in soup.find_all("h2"):
    var_span = soup.new_tag("span", attrs={"class": "var"})
    var_span.string = h2.text
    h2.clear()
    h2.append(var_span)
  final_html = wrap_docs_html(toc_items, str(soup))

  docs_html.write_text(final_html, encoding="utf-8")
  print(f"✅ HTML generated: {docs_html.resolve()}")
  
  ### GENERATE INDEX
  index_tmpl = Path("index.tmpl")
  index_html = Path("index.html")
  
  index_text = index_tmpl.read_text(encoding="utf-8")
  soup = BeautifulSoup(index_text, "html.parser")
  nav_bar = soup.find("nav", id="nav-bar")
  if nav_bar:
    nav_bar.clear()
    nav_bar.append(BeautifulSoup(header, "html.parser"))
      
  index_html.write_text(str(soup), encoding="utf-8")
  print(f"✅ HTML generated: {index_html.resolve()}")
        
if __name__ == "__main__":
  main()
