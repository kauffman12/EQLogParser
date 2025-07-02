from pathlib import Path
from bs4 import Tag
from bs4 import BeautifulSoup
import markdown

def convert_markdown_to_html(md_text: str) -> str:
    """Converts markdown text to HTML body content."""
    return markdown.markdown(md_text, extensions=["extra"])

def wrap_html(content: str) -> str:
    """Wraps the HTML content with the required full HTML structure and CSS."""
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>Trigger Variables</title>
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <style>
    body {{
      font-family: 'Inter', 'Segoe UI', sans-serif;
      background: #fafafa;
      color: #222;
      padding: 2rem;
      max-width: 800px;
      margin: auto;
      line-height: 1.6;
    }}
    h1, h2 {{
      font-weight: 600;
      border-bottom: 1px solid #ddd;
      padding-bottom: 0.3em;
      margin-top: 2rem;
    }}
    code {{
      background: #f0f0f0;
      padding: 0.1em 0.3em;
      border-radius: 4px;
      font-family: monospace;
    }}
    .var {{
      font-weight: bold;
      color: #0055aa;
    }}
    ul {{
      margin-left: 1.5rem;
    }}
  </style>
</head>
<body>
{content}
</body>
</html>"""

def main():
    input_md = Path("trigger_vars.md")
    output_html = Path("trigger_vars.html")

    md_text = input_md.read_text(encoding="utf-8")
    html_body = convert_markdown_to_html(md_text)

    # Optional: wrap special vars in span tags (like `{C}` -> `<span class="var">{C}</span>`)
    # Only in <h2> elements:
    soup = BeautifulSoup(html_body, "html.parser")
    for h2 in soup.find_all("h2"):
        var_span = soup.new_tag("span", attrs={"class": "var"})
        var_span.string = h2.text
        h2.clear()
        h2.append(var_span)
    final_html = wrap_html(str(soup))

    output_html.write_text(final_html, encoding="utf-8")
    print(f"✅ HTML generated: {output_html.resolve()}")

if __name__ == "__main__":
    main()
