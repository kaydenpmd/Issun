"""Writes ParityData.cs beside this file: the expected values for Issun's
artwork tests, computed by relay.py itself.

    cd <some scratch folder>
    python path/to/generate_parity.py [path/to/relay.py]

Run it from a scratch folder, not from here: importing relay.py creates
./art_cache in the current directory. The relay path defaults to the Ammy
repo's bridge/relay.py. The test project does not depend on Python; this only
exists so the committed numbers can be regenerated and checked.

The iTunes responses in fixtures/ were captured from itunes.apple.com in
September 2026 with the same urlencode'd URLs relay.py builds.
"""
import base64, binascii, contextlib, difflib, hashlib, importlib.util, io, json, os, platform, random, sys, unicodedata
import urllib.parse, urllib.request

sys.dont_write_bytecode = True
sys.stdout.reconfigure(errors="backslashreplace")
RELAY = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\links\Repos\Ammy\bridge\relay.py"
spec = importlib.util.spec_from_file_location("relay", RELAY)
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)

rng = random.Random(20260921)
HERE = os.path.dirname(os.path.abspath(__file__))


def has_lone_surrogate(s):
    return any(0xD800 <= ord(c) <= 0xDFFF for c in s)


def enc(s):
    """A string for the JSON: plain when System.Text.Json can read it back,
    otherwise a list of code points (lone surrogates can't survive JSON)."""
    if has_lone_surrogate(s):
        return {"cp": [ord(c) for c in s]}
    return s


def safe_for_csharp(s):
    # A lone high surrogate followed by a lone low one would pair up in UTF-16.
    for x, y in zip(s, s[1:]):
        if 0xD800 <= ord(x) <= 0xDBFF and 0xDC00 <= ord(y) <= 0xDFFF:
            return False
    return True


# ── _album_url ────────────────────────────────────────────────────────────
album_inputs = [
    "https://music.apple.com/us/album/i/1444846337?i=1444846349&uo=4",
    "https://music.apple.com/us/album/i/1444846337?i=1444846349",
    "https://music.apple.com/us/album/i/1444846337?uo=4&i=1444846349",
    "https://music.apple.com/us/album/i/1444846337",
    "https://music.apple.com/us/album/i/1444846337?",
    "https://music.apple.com/us/album/i/1444846337?i=1&i=2&uo=4",
    "https://music.apple.com/jp/album/%E3%83%86%E3%82%B9%E3%83%88/123?i=456&uo=4",
    "https://music.apple.com/us/album/rolling-papers-deluxe-version/426754194?i=426754283&uo=4",
    "https://music.apple.com/us/album/x/1?uo=4&ls=1&app=music",
    "https://music.apple.com/us/album/x/1?i=5&at=1000l%20x+y&ct=a%2Fb",
    "https://music.apple.com/us/album/x/1?i=5&flag",
    "https://music.apple.com/us/album/x/1?i=5&&uo=4&",
    "https://music.apple.com/us/album/x/1?i=5#frag",
    "https://music.apple.com/us/album/x/1?uo=4#frag?i=1",
    "https://music.apple.com/us/album/x/1?#",
    "https://music.apple.com/us/album/x/1?I=5&uo=4",
    "https://music.apple.com/us/album/x/1?%69=5&uo=4",
    "https://music.apple.com/us/album/x/1?i%3D5=1",
    "https://music.apple.com/us/album/x/1?q=caf%C3%A9&r=caf\u00e9&i=3",
    "https://music.apple.com/us/album/x/1?q=%E2%82&i=1",
    "https://music.apple.com/us/album/x/1?q=%zz&r=%&s=%4",
    "HTTPS://Music.Apple.com/us/album/x/1?i=2&uo=4",
    "  https://music.apple.com/us/album/x/1?i=2&uo=4",
    "https://music.apple.com/us/al\tbum/x/1?i=2&u\no=4\r",
    "music.apple.com/us/album/x/1?i=2&uo=4",
    "//music.apple.com/x?i=1&a=b",
    "https:///path?i=1&a=b",
    "https:path?i=1&a=b",
    "https:?i=1&a=b",
    "https:",
    "mailto:someone?i=1&x=y",
    "https://[::1]:8080/x?i=1&a=b",
    "https://[fe80::1%25eth0]/x?i=1&a=b",
    "https://[::1/x?i=1",
    "https://::1]/x?i=1",
    "https://[1.2.3.4]/x?i=1",
    "https://[v1.fe]/x?i=1&a=b",
    "https://[vz.fe]/x?i=1",
    "https://a[::1]/x?i=1",
    "https://[::1]x/x?i=1",
    "https://exa\u2100mple.com/x?i=1",
    "https://ex\u00e4mple.com/x?i=1&a=b",
    "https://\uff45xample.com/x?i=1&a=b",
    "https://user:pw@music.apple.com/x?i=1&a=b",
    "https://music.apple.com/x?a=1+2&b=%2B&c=~._-&d=!*'()",
    "https://music.apple.com/x?a=%20%20",
    "?i=1&a=b",
    "https://music.apple.com/x?=v&i=1",
    "https://music.apple.com/x?a==b",
    "https://music.apple.com/x?i",
    "https://music.apple.com/x?i=",
    "https://music.apple.com/x?a=b;c=d",
    "https://music.apple.com/x?emoji=%F0%9F%8E%B5&raw=\U0001F3B5",
    "https://music.apple.com/x?a=%E2%82%ACb%C3",
    "https://music.apple.com/x?a=%ED%A0%80",
    "https://music.apple.com/x?a=%C0%80",
    "https://music.apple.com/x?a=%F4%90%80%80",
    "https://music.apple.com/x?a=%E2%82%E2%82%AC",
    "https://music.apple.com/x?a=%e2%82%ac",
    "https://music.apple.com/x?a=%F0%9F%8E",
    "https://music.apple.com/x?a=%FF%FE%80",
    "https://music.apple.com/x?a=%E2\u00e9%82%AC",
    "\x00\x1f https://music.apple.com/x?i=1&a=b",
    "https://music.apple.com/x?a=1 ",
    "https://music.apple.com/x?a=1\x7f\x01",
    "",
    "1abc:foo?i=1&a=b",
    "a+b.c-d:foo?i=1&a=b",
    "\u00e9:foo?i=1&a=b",
    "HTTP://music.apple.com?i=1&a=b",
    "https://music.apple.com?i=1",
    "https://music.apple.com/us/album/x/1?i=1\u3000&a=\u00a0",
    "https://music.apple.com/us/album/x/1?%E3%83%86=%E3%82%B9",
    "https://music.apple.com/us/album/x/1?a=%2526&b=%3D%26",
    "file:///C:/x?i=1&a=b",
    "https://music.apple.com//double//slash?i=1&a=b",
    "https:////x?i=1&a=b",
]

pieces = ["https://", "http://", "HTTPS:", "//", "music.apple.com", "[", "]", "::1", "@", ":", "/", "/us/album/",
          "?", "#", "&", "=", "i", "i=", "uo=4", "%", "%2", "%41", "%zz", "%E2%82%AC", "%C3", "+", " ", "\t",
          "\u00e9", "\u3000", "\U0001F3B5", "\u2100", "\uff0f", "x", "a", "b", "0", ";", "~", "!", "'", "*", "(",
          "\u00a0", "\x00", "\n", "v1.x", "1.2.3.4", "%25"]
seen = set(album_inputs)
while len(album_inputs) < 360:
    s = "".join(rng.choice(pieces) for _ in range(rng.randint(1, 14)))
    if rng.random() < 0.6:
        s = "https://music.apple.com/us/album/x/1?" + s
    if s not in seen:
        seen.add(s)
        album_inputs.append(s)

album_cases = []
for s in album_inputs:
    try:
        out = relay._album_url(s)
        album_cases.append({"in": enc(s), "out": enc(out)})
    except ValueError as exc:
        album_cases.append({"in": enc(s), "error": type(exc).__name__, "message": str(exc)})

# ── urlencode of search and lookup queries ────────────────────────────────
terms = [
    "Kendrick Lamar i i - Single", "Wiz Khalifa Black and Yellow", "Beyonc\u00e9 D\u00e9j\u00e0 Vu",
    "AC/DC Back in Black", "Guns N' Roses Sweet Child O' Mine", "Simon & Garfunkel", "a+b=c?d#e%f",
    "~tilde_under.score-dash", "\u5b87\u591a\u7530\u30d2\u30ab\u30eb First Love", "\U0001F3B5 emoji \U0001F525",
    "", " ", "  double  spaces  ", "tab\there", "new\nline", "percent %20 literal", "\u00a0nbsp", "\u3000ideo",
    "Sigur R\u00f3s ( )", "\"quoted\" <angle> {brace} [bracket] |pipe| \\back^caret`",
    "\x00\x7f\x80\u07ff\u0800\uffff\U00010000\U0010ffff",
]
query_cases = []
for t in terms:
    query_cases.append({
        "term": enc(t),
        "search": urllib.parse.urlencode({"term": t, "entity": "song", "limit": 12}),
        "lookup": urllib.parse.urlencode({"id": t, "entity": "song"}),
    })

# ── _normalize ────────────────────────────────────────────────────────────
titles = [
    "Song (Remastered 2011)", "Song [Remix]", "Album - Single", "Album \u2013 EP", "Album\u2014Deluxe Edition",
    "Album - Deluxe", "Album - Remastered", "Album - Remaster", "Album - Special Edition",
    "Album - Expanded Edition", "Movie (Original Motion Picture Soundtrack)",
    "Movie - Original Motion Picture Soundtrack", "Album - Bonus Track Version", "Album - Singles",
    "Album -Single", "Album-Single", "Album  -  single  extra stuff", "Album - EPs", "Album - Episode",
    "Song feat. Someone", "Song ft. Someone", "Song featuring Someone", "Song with Someone", "Song Feat. X",
    "Without Me", "Withered", "Loft", "Feature Film", "Heartbreak Hotel", "Dance with Me",
    "Black and Yellow (feat. Juicy J, Snoop Dogg & T-Pain) [G-Mix]", "Bad Blood (Taylor's Version) [feat. Kendrick Lamar]",
    "Don't Wanna Know (feat. Kendrick Lamar) [BRAVVO Remix] - Single", "DAMN. COLLECTORS EDITION.",
    "HUMBLE.", "i", "I", "i - Single", "To Pimp a Butterfly", "Rolling Papers 2", "Rolling Papers (Deluxe Version)",
    "Beyonc\u00e9", "D\u00e9j\u00e0 Vu", "Sigur R\u00f3s", "M\u00f6tley Cr\u00fce", "\u00c6nima", "Stra\u00dfe",
    "\u00d8resund", "\u0141\u00f3d\u017a", "\u0130stanbul", "\u212aelvin", "\ufb01ne \ufb02ow", "\u2460\u2461\u2462",
    "\u00bd time", "\uff33\uff4f\uff4e\uff47", "\u216b", "x\u00b2", "Brand\u2122", "\u01c5ungla",
    "\u5b87\u591a\u7530\u30d2\u30ab\u30eb", "First Love (\u521d\u604b)", "\u30e8\u30eb\u30b7\u30ab - \u3060\u304b\u3089\u50d5\u306f\u97f3\u697d\u3092\u8f9e\u3081\u305f",
    "\ubc29\ud0c4\uc18c\ub144\ub2e8 Dynamite", "\U0001F3B5 Emoji \U0001F525 Song", "\U0001F1FA\U0001F1F8 flag",
    "tab\tseparated", "multi\nline (feat.\nX)", "paren (open", "bracket [open", "(\n)", "a\u001cb", "a\u0085b",
    "  lots   of   space  ", "", "   ", "!!!", "___", "a_b", "caf\u00e9 with\u0301 x", "with\u0301", "feat\u00e9",
    "x \u2014single", "x \u2013 single\u00e9", "x - single_", "x - single2", "(feat. A) Song", "[Live] Song",
    "Song (Live) [2019 Remaster] - Single", "Song - 2011 Remaster", "Song \u2012 Single", "Song \u2010 Single",
    "Song \uff0d Single", "Song \ufe63 Single", "Song\u3000-\u3000Single", "Song\u00a0-\u00a0Single",
    "Song\u2028- Single", "Song -\nSingle", "Song\n- Single", "WITH YOU", "FT. ME", "Ft.me", "Song ft", "Song ft.",
    "NFKD \u1e9b\u0323", "\u0391\u03b8\u03ae\u03bd\u03b1", "\u041c\u043e\u0441\u043a\u0432\u0430", "\u05e9\u05dc\u05d5\u05dd",
    "\u0645\u0631\u062d\u0628\u0627", "\u0e2a\u0e27\u0e31\u0e2a\u0e14\u0e35", "\u0928\u092e\u0938\u094d\u0924\u0947",
    "123 456", "\u0661\u0662\u0663", "\u00b9\u00b2\u00b3", "Song (Remix) (feat. B)", "Song [feat. B] (Remix)",
    "Song) (x", "Song ](x[ y]", "a(b)c[d]e", "Songwith", "withSong", "\u00e9with x", "1with x", "_with x",
    "Song \u00d7 Other", "Song & Other", "Song + Other", "Song / Other", "Song x Other",
]
frags = ["Song", " ", "(feat. X)", "[Remix]", " - Single", " \u2013 EP", "\u2014Deluxe Edition", "feat.", "ft", " with ",
         "Featuring", "\u00e9", "e\u0301", "\u5b87", "\U0001F3B5", "\n", "\u3000", "\u001c", "\u0301", "\u01c5",
         "\ufb01", "\u2460", "\u00bd", "\uff33", "\u0130", "\u212a", "\u216b", "\u00b2", "\u2122", "(", ")", "[", "]",
         "-", "_", "2011", "Remastered", "remaster", "single", "ep", "bonus track version", "WITH", "\u00a0", "\t",
         "\u0085", "\u2028", "\u200b", "\ufeff", "x", "\u00df", "\u0133", "\u0149", "\u1e9e", "\U0001d400"]
while len(titles) < 420:
    s = "".join(rng.choice(frags) for _ in range(rng.randint(1, 9)))
    if s not in titles:
        titles.append(s)
normalize_cases = [{"in": enc(t), "out": relay._normalize(t)} for t in titles]
normalize_cases.append({"in": enc("with\ud800 x"), "out": relay._normalize("with\ud800 x")})
normalize_cases.append({"in": enc("Song \ud83c - Single"), "out": relay._normalize("Song \ud83c - Single")})
normalize_cases.append({"in": enc("\udfff(feat. y)"), "out": relay._normalize("\udfff(feat. y)")})

# ── SequenceMatcher ratio ─────────────────────────────────────────────────
def rand_str(alphabet, lo, hi):
    return "".join(rng.choice(alphabet) for _ in range(rng.randint(lo, hi)))

uni = ["a", "b", "\u00e9", "e\u0301", "\u5b87", "\u591a", "\U0001F3B5", "\U0001F525", " ", "\u3000", "\u0301", "z", "\U00010348"]
pairs = [("", ""), ("", "a"), ("a", ""), ("a", "a"), ("abc", "abc"), ("abcd", "bcde"), ("private", "privately"),
         ("black and yellow", "black and yellow"), ("rolling papers 2", "rolling papers"), ("i", "i single"),
         ("kendrick lamar", "kendrick lamar sza"), ("abxcd", "abcd"), ("qabxcd", "abycdf"), ("ab", "ba"),
         ("aaaa", "aa"), ("\U0001F3B5\U0001F3B5", "\U0001F3B5"), ("x" * 250, "x" * 250), ("ab" * 150, "ba" * 150),
         ("a" * 5 + "b" * 300, "b" * 300 + "a" * 5), ("\ud800a", "a\ud800"), ("\udc00\ud800", "\ud800\udc00"[::-1])]
for _ in range(160):
    pairs.append((rand_str("ab ", 0, 12), rand_str("ab ", 0, 12)))
for _ in range(80):
    pairs.append((rand_str("abcdefghij klmnop", 0, 30), rand_str("abcdefghij klmnop", 0, 30)))
for _ in range(60):
    pairs.append((rand_str(uni, 0, 20), rand_str(uni, 0, 20)))
for _ in range(30):
    pairs.append((rand_str("abc ", 0, 60), rand_str("abc ", 200, 420)))
for _ in range(20):
    pairs.append((rand_str("abcdefghijklmnopqrstuvwxyz ", 150, 300), rand_str("abcdefghijklmnopqrstuvwxyz ", 200, 500)))
for _ in range(10):
    pairs.append((rand_str(uni, 180, 260), rand_str(uni, 200, 260)))
for _ in range(10):
    pairs.append((rand_str(["\ud800", "a", "\udfff", "b"], 0, 12), rand_str(["\ud800", "a", "\udfff", "b"], 0, 12)))
pairs = [(a, b) for a, b in pairs if safe_for_csharp(a) and safe_for_csharp(b)]

ratio_cases = []
for a, b in pairs:
    sm = difflib.SequenceMatcher(None, a, b)
    ratio_cases.append({"a": enc(a), "b": enc(b), "ratio": sm.ratio(),
                        "blocks": [list(m) for m in sm.get_matching_blocks()]})

# ── _score ────────────────────────────────────────────────────────────────
score_cases = []
names = ["i", "I", "i - Single", "Kendrick Lamar", "kendrick lamar", "Kendrick Lamar, SZA", "To Pimp a Butterfly",
         "Black and Yellow", "Wiz Khalifa", "Rolling Papers 2", "Rolling Papers (Deluxe Version)", "",
         "Beyonc\u00e9", "Beyonce", "D\u00e9j\u00e0 Vu (feat. Jay-Z)", "Deja Vu", "B'Day (Deluxe Edition)", "B'Day",
         "\u5b87\u591a\u7530\u30d2\u30ab\u30eb", "First Love", "Unknown Track", "Unknown Artist"]
for _ in range(240):
    score_cases.append([rng.choice(names) for _ in range(6)])
score_cases += [["i", "Kendrick Lamar", "i - Single", "i", "Kendrick Lamar", "i"],
                ["Black and Yellow", "Wiz Khalifa", "Rolling Papers", "Black and Yellow", "Wiz Khalifa", "Rolling Papers 2"]]
score_out = []
for tn, an, cn, t, ar, al in score_cases:
    result = {"trackName": tn, "artistName": an, "collectionName": cn}
    score_out.append({"result": [tn, an, cn], "track": [t, ar, al], "score": relay._score(result, t, ar, al)})

# ── base64 ────────────────────────────────────────────────────────────────
jpeg = bytes([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10]) + b"JFIF\x00" + bytes(range(40))
b64_inputs = [base64.b64encode(jpeg).decode(), base64.b64encode(jpeg[:-1]).decode(), base64.b64encode(jpeg[:-2]).decode(),
              "", "=", "==", "a", "ab", "abc", "abcd", "ab==", "abc=", "ab=", "a===", "abcd=", "abcd==", "ab==cd",
              "ab=c", "abc==", "a=", "a==", "=abc", "ab\ncd", "ab cd", "abcd\n", " abcd", "ab-_", "ab+/", "ab!d",
              "abc\u00e9", "\u00e9", "YWJj", "YWI=", "YQ==", "YQ", "YWI", "YQ=", "YQ===", "YWJjZA==", "YWJjZA",
              "YWJjZA=", "YWJjZA===", "Y=Q=", "YQ==YQ==", "/+/+", "////", "++++", "AAAA", "AAA=", "AA==",
              base64.b64encode(b"\xff\xd8\xff" + b"\x00" * 3000).decode(), "\x00", "ab\x00d", "abcd====",
              "ab=\n=", "abc=\n", "abcde===", "abcdef=", "abcdef==", "abcdefg=", "abcdefgh", "ab\tcd"]
alphabet = ["A", "B", "a", "z", "0", "9", "+", "/", "=", "==", "\n", " ", "-", "_", "\u00e9", "YQ", "AAAA"]
while len(b64_inputs) < 380:
    s = "".join(rng.choice(alphabet) for _ in range(rng.randint(0, 12)))
    b64_inputs.append(s)
b64_cases = []
for s in b64_inputs:
    try:
        out = base64.b64decode(s, validate=True)
        b64_cases.append({"in": s, "hex": out.hex()})
    except (binascii.Error, ValueError) as exc:
        b64_cases.append({"in": s, "error": type(exc).__name__, "message": str(exc)})

# ── uploaded-art filenames ────────────────────────────────────────────────
tracks = [
    {"title": "i", "artist": "Kendrick Lamar", "album": "i - Single"},
    {},
    {"title": "Demo", "artist": "Me"},
    {"title": "Beyonc\u00e9", "artist": "D\u00e9j\u00e0", "album": "\u00c6"},
    {"title": "\u5b87\u591a\u7530", "artist": "\U0001F3B5", "album": "\u3000"},
    {"title": "x" * 200, "artist": "y" * 129, "album": "z" * 128},
    {"title": "\U0001F3B5" * 130, "artist": "a|b", "album": "|"},
    {"title": "", "artist": "", "album": ""},
    {"title": "e\u0301", "artist": "\u00e9", "album": "\ufb01"},
    {"title": "Local File (Demo 2009)", "artist": "Bedroom Band", "album": "Tapes"},
]
filename_cases = []
for t in tracks:
    title = str(t.get("title", "Unknown Track"))[:128]
    artist = str(t.get("artist", "Unknown Artist"))[:128]
    album = str(t.get("album", ""))[:128]
    key = f"{artist}|{title}|{album}"
    filename_cases.append({"track": {k: v for k, v in t.items()}, "key": key,
                           "name": hashlib.sha256(key.encode()).hexdigest()[:20] + ".jpg"})

# ── {:.2f} ────────────────────────────────────────────────────────────────
values = [0.0, 1.0, 0.125, 0.375, 0.625, 0.875, 0.48, 0.005, 0.015, 0.025, 0.035, 0.045, 0.345, 0.355, 0.5949999999999999,
          0.595, 0.6, 0.35, 0.55, 0.3499999999999999, 0.1 + 0.2, 2.675, 1.005, 0.9999999, 0.994999999999, 0.995,
          5e-324, 1e-300, 0.5, 0.25, 0.75, 0.0625, 0.9375, 12.345, 123456.785, 0.285, 0.565, 0.415]
values += [k / 8 for k in range(17)] + [k / 200 for k in range(201)] + [rng.random() for _ in range(200)]
fixed2_cases = [{"value": v, "text": f"{v:.2f}"} for v in values]

# ── end to end: artwork_url and artwork_by_store_id against canned JSON ───
def fixture(name):
    with open(os.path.join(HERE, "fixtures", name), "rb") as f:
        return f.read()

synthetic_search = json.dumps({"resultCount": 5, "results": [
    {"trackName": "Tie Song", "artistName": "Tie Artist", "collectionName": "First",
     "artworkUrl100": "https://example.invalid/a/100x100bb.jpg"},
    {"trackName": "Tie Song", "artistName": "Tie Artist", "collectionName": "First",
     "artworkUrl100": "https://example.invalid/b/100x100bb.jpg"},
    {"trackName": "Tie Song", "artistName": "Tie Artist", "collectionName": "First Better Match"},
    {"trackName": "Tie Song", "artistName": "Tie Artist", "collectionName": "First", "artworkUrl100": ""},
    {"trackName": "Other", "artistName": "Else", "artworkUrl100": "https://example.invalid/c/100x100bb.jpg/100x100bb.jpg"},
]}).encode()
bom_search = b"\xef\xbb\xbf" + json.dumps({"results": [
    {"trackName": "Bom", "artistName": "Bom", "collectionName": "Bom", "artworkUrl100": "https://example.invalid/bom/100x100bb.jpg"}]}).encode()
null_results = b'{"resultCount": 0, "results": null}'
no_results_key = b'{"resultCount": 0}'

search_runs = [
    ("search-kendrick-i.json", "i", "Kendrick Lamar", "i - Single", 0.35),
    ("search-kendrick-i.json", "i", "Kendrick Lamar", "i", 0.35),
    ("search-kendrick-i.json", "i", "Kendrick Lamar", "", 0.35),
    ("search-kendrick-i.json", "I", "kendrick lamar", "To Pimp A Butterfly", 0.35),
    ("search-kendrick-i.json", "i", "Kendrick Lamar", "Some Other Album Entirely", 0.35),
    ("search-kendrick-i.json", "luther", "Kendrick Lamar & SZA", "GNX", 0.35),
    ("search-kendrick-i.json", "Nothing Alike", "Zzyzx Qqq", "Xxyyzz", 0.35),
    ("search-kendrick-i.json", "Nothing Alike", "Zzyzx Qqq", "Xxyyzz", 0.0),
    ("search-kendrick-i.json", "i", "Kendrick Lamar", "i - Single", 0.99),
    ("search-wiz.json", "Black and Yellow", "Wiz Khalifa", "Rolling Papers 2", 0.35),
    ("search-wiz.json", "Black and Yellow", "Wiz Khalifa", "Black and Yellow - Single", 0.35),
    ("search-wiz.json", "Black and Yellow", "Wiz Khalifa", "Rolling Papers (Deluxe Version)", 0.35),
    ("search-wiz.json", "Black & Yellow", "Wiz", "", 0.35),
    ("search-wiz.json", "Black and Yellow (Remastered)", "Wiz Khalifa feat. Nobody", "Rolling Papers - Deluxe", 0.35),
    ("search-wiz.json", "Completely Unrelated", "Nobody", "Nothing", 0.35),
    ("search-wiz.json", "Completely Unrelated", "Nobody", "Nothing", 0.5),
    ("search-wiz.json", "Black", "Khalifa", "Yellow", 0.35),
    ("search-wiz.json", "Yellow Black", "Wiz", "Unknown", 0.35),
    ("search-wiz.json", "Blacker", "Wizard", "Papers", 0.35),
    ("search-kendrick-i.json", "Stars", "SZA", "Panther", 0.35),
    ("search-kendrick-i.json", "Know", "Maroon", "Red", 0.35),
    ("search-kendrick-i.json", "I Who", "Luther", "", 0.35),
    ("search-kendrick-i.json", "I Who", "Luther", "", 0.3455),
    ("search-none.json", "Some Local Demo", "Bedroom Band", "Tapes", 0.35),
    ("search-none.json", "Some Local Demo", "Bedroom Band", "", 0.35),
    ("search-none.json", "", "Bedroom Band", "", 0.35),
    ("synthetic", "Tie Song", "Tie Artist", "First", 0.35),
    ("synthetic", "Tie Song", "Tie Artist", "", 0.35),
    ("synthetic", "Other", "Else", "", 0.35),
    ("bom", "Bom", "Bom", "Bom", 0.35),
    ("null", "Null Results", "Someone", "", 0.35),
    ("nokey", "No Key", "Someone", "", 0.35),
]
bodies = {"synthetic": synthetic_search, "bom": bom_search, "null": null_results, "nokey": no_results_key}

requested = []


def fake_urlopen_for(body):
    def fake(url, timeout=None):
        requested.append(url)
        assert timeout == 6
        return io.BytesIO(body)
    return fake


search_cases = []
for fx, title, artist, album, floor in search_runs:
    body = bodies[fx] if fx in bodies else fixture(fx)
    relay._artwork_cache.clear()
    relay.ART_MIN_SCORE = floor
    requested.clear()
    relay.urllib.request.urlopen = fake_urlopen_for(body)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        url, matched = relay.artwork_url(title, artist, album)
    search_cases.append({"fixture": fx, "title": title, "artist": artist, "album": album, "min_score": floor,
                         "url_requested": requested[0], "art": url, "matched": matched,
                         "logs": buf.getvalue().splitlines()})
relay.ART_MIN_SCORE = 0.35

synthetic_lookups = {
    "noart": json.dumps({"resultCount": 1, "results": [{
        "trackName": "Song", "collectionName": "Album",
        "trackViewUrl": "https://music.apple.com/us/album/song/2?i=3&uo=4",
        "artistViewUrl": "https://music.apple.com/us/artist/someone/9?uo=4",
        "collectionViewUrl": "https://music.apple.com/us/album/song/2?i=3&uo=4"}]}).encode(),
    "partial": json.dumps({"resultCount": 1, "results": [{
        "trackName": "Song", "collectionName": "",
        "trackViewUrl": "", "artistViewUrl": None,
        "artworkUrl100": "https://example.invalid/x/100x100bb.jpg"}]}).encode(),
    "onlyi": json.dumps({"results": [{
        "collectionViewUrl": "https://music.apple.com/us/album/song/2?i=3",
        "artworkUrl100": "https://example.invalid/y/100x100bb.jpg", "collectionName": "Y"}]}).encode(),
    "badurl": json.dumps({"results": [{
        "trackViewUrl": "https://music.apple.com/us/album/song/2?i=3&uo=4",
        "collectionViewUrl": "https://[::1/x?i=3",
        "artworkUrl100": "https://example.invalid/z/100x100bb.jpg", "collectionName": "Z"}]}).encode(),
    "notjson": b"<html>Service Unavailable</html>",
    "twoentries": json.dumps({"results": [
        {"trackViewUrl": "https://music.apple.com/us/album/first/1?i=1", "artworkUrl100": "https://example.invalid/first/100x100bb.jpg", "collectionName": "First"},
        {"trackViewUrl": "https://music.apple.com/us/album/second/1?i=2", "artworkUrl100": "https://example.invalid/second/100x100bb.jpg", "collectionName": "Second"}]}).encode(),
}
lookup_runs = [("lookup-1444846349.json", "1444846349"), ("lookup-missing.json", "1"), ("noart", "42"),
               ("partial", "43"), ("onlyi", "44"), ("badurl", "45"), ("notjson", "46"), ("twoentries", "47"),
               ("lookup-1444846349.json", "id with spaces&=+")]
lookup_cases = []
for fx, sid in lookup_runs:
    body = synthetic_lookups[fx] if fx in synthetic_lookups else fixture(fx)
    relay._artwork_cache.clear()
    relay._links_cache.clear()
    requested.clear()
    relay.urllib.request.urlopen = fake_urlopen_for(body)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        url, matched = relay.artwork_by_store_id(sid)
    lookup_cases.append({"fixture": fx, "store_id": sid, "url_requested": requested[0], "art": url,
                         "matched": matched, "links": relay.catalog_links(sid),
                         "logs": buf.getvalue().splitlines()})

# Every response body the cases above were run against, so the C# tests serve
# exactly the same bytes. "bom" marks a body that starts with a UTF-8 BOM.
all_bodies = {**bodies, **synthetic_lookups}
for name in sorted(os.listdir(os.path.join(HERE, "fixtures"))):
    if name.endswith(".json"):
        all_bodies[name] = fixture(name)
body_texts = {name: {"text": body.decode("utf-8-sig"), "bom": body.startswith(b"\xef\xbb\xbf")}
              for name, body in all_bodies.items()}

out = {
    "AlbumUrl": album_cases, "Query": query_cases, "Normalize": normalize_cases, "Ratio": ratio_cases,
    "Score": score_out, "Base64": b64_cases, "FileName": filename_cases, "Fixed2": fixed2_cases,
    "Search": search_cases, "Lookup": lookup_cases, "Bodies": body_texts,
}

lines = [
    "// <auto-generated>",
    f"// Written by generate_parity.py from relay.py {relay.RELAY_VERSION} under Python "
    f"{platform.python_version()}. Regenerate rather than edit.",
    "// </auto-generated>",
    "",
    "namespace Issun.Core.Tests.Artwork;",
    "",
    "/// <summary>Expected values computed by relay.py itself, one JSON document per function.</summary>",
    "internal static class ParityData",
    "{",
]
for name, data in out.items():
    if isinstance(data, list):
        items = [json.dumps(c, ensure_ascii=True) for c in data]
        text = "[\n" + ",\n".join(items) + "\n]"
    else:
        items = [json.dumps(k) + ": " + json.dumps(v, ensure_ascii=True) for k, v in data.items()]
        text = "{\n" + ",\n".join(items) + "\n}"
    assert '"""' not in text, name
    lines.append(f'    public const string {name} = """')
    lines.extend("        " + line for line in text.split("\n"))
    lines.append('        """;')
    lines.append("")
lines[-1] = "}"
with open(os.path.join(HERE, "ParityData.cs"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
for name, data in out.items():
    print(f"{name}: {len(data)} cases")

errs = [c for c in album_cases if "error" in c]
print("album_url errors:", len(errs), sorted({c["message"][:40] for c in errs})[:12])
for c in search_cases:
    print("search", c["title"], "|", c["album"], c["min_score"], "->", c["art"], "|", c["matched"], c["logs"])
for c in lookup_cases:
    print("lookup", c["store_id"], c["art"], c["matched"], c["links"], c["logs"])
