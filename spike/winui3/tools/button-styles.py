"""Lists every keyed style in WinUI's generic.xaml whose target is a button-family
control, with its BasedOn and the visual states its template defines.
    python -I button-styles.py <generic.xaml> [--states KEY ...]
"""
import re
import sys

FAMILY = re.compile(r"Button|Toggle|CheckBox|Radio|Switch|Hyperlink|SplitButton")


def styles(text):
    for m in re.finditer(r"<Style\b([^>]*)>(.*?)</Style>", text, re.S):
        attrs = m.group(1)
        key = re.search(r'x:Key="([^"]+)"', attrs)
        target = re.search(r'TargetType="([^"]+)"', attrs)
        based = re.search(r'BasedOn="\{StaticResource ([^}]+)\}"', attrs)
        states = list(dict.fromkeys(re.findall(r'<VisualState x:Name="([^"]+)"', m.group(2))))
        yield (key.group(1) if key else None, target.group(1) if target else "?", based.group(1) if based else "", states)


def main():
    text = open(sys.argv[1], encoding="utf-8").read()
    want = set(sys.argv[3:]) if len(sys.argv) > 2 and sys.argv[2] == "--states" else None
    for key, target, based, states in styles(text):
        if key is None or not FAMILY.search(target):
            continue
        if want is not None:
            if key in want:
                print(f"{target} {key}: {' '.join(states)}")
        else:
            print(f"{target}\t{key}\t{based}\t{len(states)} states")


main()
