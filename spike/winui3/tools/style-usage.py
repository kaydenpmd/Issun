"""For each button-family style key, decides what content a gallery should give
it, from WinUI's own generic.xaml and nothing else:
- where WinUI uses the style and gives it content (a Content attribute, or
  the first Glyph=/Symbol= inside that element), it gets that;
- otherwise a style whose own text is drawn in the icon font gets no content,
  and says so (its control's code sets it);
- any other style gets the plain sample text.
Prints C# tuples for Gallery.cs's Keyed table.
    python -I style-usage.py <generic.xaml> KEY ...
"""
import re
import sys

text = open(sys.argv[1], encoding="utf-8").read()


def style_block(key):
    m = re.search(r'<Style\b[^>]*x:Key="' + re.escape(key) + r'"[^>]*>', text)
    if not m:
        return ""
    end = text.find("</Style>", m.end())
    return text[m.start():end]


def usage(key):
    m = re.search(r"<(\w[\w:.]*)\b([^>]*?Style=\"\{(?:StaticResource|ThemeResource) " + re.escape(key) + r"\}\"[^>]*?)(/?)>", text, re.S)
    if not m:
        return None
    tag, attrs, selfclose = m.group(1), m.group(2), m.group(3)
    c = re.search(r'\bContent="([^"{]*)"', attrs)
    if c:
        return c.group(1), "Content attribute"
    if selfclose:
        return None
    close = text.find("</" + tag + ">", m.end())
    inner = text[m.end():close if close > 0 else m.end()]
    g = re.search(r'\bGlyph="([^"{]*)"', inner)
    if g:
        return g.group(1), "glyph inside"
    s = re.search(r'\bSymbol="([^"{]*)"', inner)
    if s:
        return "symbol:" + s.group(1), "symbol inside"
    return None


for key in sys.argv[2:]:
    block = style_block(key)
    # Icon button: its own text is drawn in the icon font (a FontFamily setter,
    # or the ContentPresenter's), not merely a chevron somewhere in it.
    icon = bool(re.search(r'<Setter Property="FontFamily" Value="\{ThemeResource SymbolThemeFontFamily\}"', block)
                or re.search(r'<ContentPresenter\b[^>]*FontFamily="\{ThemeResource SymbolThemeFontFamily\}"', block))
    found = usage(key)
    if found:
        content, how = found
        note = f"content from WinUI's own use ({how})"
    elif icon:
        content, note = "", "no content: its control's code sets it"
    else:
        content, note = "TEXT", ""
    print(f'        ("{key}", "{content}", "{note}"),')
