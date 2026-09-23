"""Regenerate ParityData.cs from relay.py itself.

    python generate_parity.py [path-to-relay.py]

Calls relay.py's own build_payload, Playhead, pad_for_discord and
_materially_different, with every artwork function stubbed on the imported
module so nothing touches the network, and writes what they returned and
printed into ParityData.cs beside this file. The tests need no Python; this is
only how their expected values were made. Needs pypresence installed, because
relay.py imports it.

relay.py creates its art cache and uptime log relative to the working
directory when imported, so this runs from a throwaway temp folder.
"""
import importlib.util
import json
import os
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
TARGET = os.path.join(HERE, "ParityData.cs")
RELAY = sys.argv[1] if len(sys.argv) > 1 else r"C:\Users\links\Repos\Ammy\bridge\relay.py"

WORK = tempfile.mkdtemp(prefix="issun-parity-")
os.chdir(WORK)
os.environ["ART_DIR"] = os.path.join(WORK, "art_cache")
os.environ["UPTIME_LOG"] = os.path.join(WORK, "uptime.log")
for name in ("STATUS_LINE", "SHOW_ALBUM", "ART_MIN_SCORE", "PUBLIC_BASE", "PUBLIC_READ"):
    os.environ.pop(name, None)

spec = importlib.util.spec_from_file_location("relay", RELAY)
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)
print("relay.py", relay.RELAY_VERSION, "from", RELAY)

KEYMAP = {
    "details": "Details", "state": "State", "activity_type": "Type",
    "status_display_type": "StatusDisplayType", "start": "Start", "end": "End",
    "large_image": "LargeImage", "large_text": "LargeText", "large_url": "LargeUrl",
    "details_url": "DetailsUrl", "state_url": "StateUrl",
}


def to_cs(payload):
    if payload is None:
        return None
    out = {}
    for k, v in payload.items():
        out[KEYMAP[k]] = int(v) if k in ("activity_type", "status_display_type", "start", "end") else v
    return out


def captured(fn, *args):
    # One entry per print() call. splitlines() would cut a title containing a
    # newline, \x1c or \x85 into several lines, which is not what relay.log saw.
    lines = []
    relay.print = lambda *a, **k: lines.append(" ".join(str(x) for x in a))
    try:
        result = fn(*args)
    finally:
        del relay.print
    return result, lines


# ───────────────────────────── build_payload scenarios ─────────────────────────────

def run_scenario(name, steps, status_line="state", show_album=False):
    relay.playhead = relay.Playhead()
    relay._padded_seen.clear()
    relay._unresolved_seen.clear()
    # relay.py normalised STATUS_LINE once, at import: .strip().lower().
    relay.STATUS_LINE = status_line.strip().lower()
    relay.SHOW_ALBUM = show_album

    out = []
    for step in steps:
        if step == "reset":
            relay.playhead.reset()
            out.append({"op": "reset"})
            continue

        track = step["track"]
        observed = step["observed_at"]
        art = step.get("art")
        links = step.get("links") or {}
        calls = []

        def by_store(sid):
            calls.append("store")
            if art and art["via"] == "store":
                return art["url"], art.get("matched", "")
            return None, ""

        def uploaded(key, b64):
            calls.append("uploaded")
            return art["url"] if art and art["via"] == "uploaded" else None

        def existing(key):
            calls.append("existing")
            return art["url"] if art and art["via"] == "existing" else None

        def search(t, a, al=""):
            calls.append("search")
            if art and art["via"] == "search":
                return art["url"], art.get("matched", "")
            return None, ""

        def cat_links(sid):
            calls.append("links")
            return dict(links)

        relay.artwork_by_store_id = by_store
        relay.store_uploaded_artwork = uploaded
        relay.existing_uploaded_artwork = existing
        relay.artwork_url = search
        relay.catalog_links = cat_links

        payload, logs = captured(relay.build_payload, track, observed)

        # The ArtworkResult Issun's resolver would hand the builder: the chain's
        # final (url, matched_album). Uploaded paths report the track's album.
        album = str(track.get("album", ""))[:128]
        if art:
            assert art["via"] in calls, (name, art["via"], calls)
            matched = album if art["via"] in ("uploaded", "existing") else art.get("matched", "")
            art_in = {"url": art["url"], "matched": matched, "via": art["via"]}
        else:
            art_in = None
        links_in = dict(links) if "links" in calls else None
        if links and "links" not in calls:
            raise AssertionError(f"{name}: links given but relay never consulted them")

        out.append({
            "op": "build",
            "body": dict(track, playing=True),
            "observed_at": observed,
            "art": art_in,
            "links": links_in,
            "expected": to_cs(payload),
            "logs": logs,
        })

    return {"name": name, "status_line": status_line, "show_album": show_album, "steps": out}


T0 = 1758400000.123

KENDRICK = {"title": "i", "artist": "Kendrick Lamar", "album": "i - Single",
            "duration": 231.5, "elapsed": 12, "store_id": "1440894925"}
KENDRICK_ART = {"via": "store", "url": "https://is1-ssl.mzstatic.com/image/thumb/Music/i/512x512bb.jpg",
                "matched": "i - Single"}
KENDRICK_LINKS = {"song": "https://music.apple.com/us/album/i/1440894924?i=1440894925&uo=4",
                  "artist": "https://music.apple.com/us/artist/kendrick-lamar/368183298?uo=4",
                  "album": "https://music.apple.com/us/album/i/1440894924?uo=4"}

scenarios = []

# 1. The regression: thirty builds against one frozen reading, one padded line.
steps = [{"track": KENDRICK, "observed_at": T0, "art": KENDRICK_ART, "links": KENDRICK_LINKS}] * 30
# Next heartbeat: 30.2s of wall time, 30.0s of playback -> drift -0.2, ignored.
steps.append({"track": dict(KENDRICK, elapsed=42.0), "observed_at": T0 + 30.2,
              "art": KENDRICK_ART, "links": KENDRICK_LINKS})
scenarios.append(run_scenario("kendrick_i_frozen_reading", steps))

# 2. Title and artist both one character: once per *value*, so "i" logs once.
same = {"title": "i", "artist": "i", "album": "", "duration": 100, "elapsed": 0}
scenarios.append(run_scenario("same_short_value_logs_once", [
    {"track": same, "observed_at": T0},
    {"track": same, "observed_at": T0},
    {"track": dict(same, artist="Ø"), "observed_at": T0 + 1},
]))

# 3. Blank, whitespace and one-code-point edge cases for pad_for_discord.
pad_titles = ["", "   ", "\t\n", "\x1c", "\x85", "\xa0", "\u3000", "\u2060", "\u200b",
              "\U0001F3B5", "e\u0301", "\xe9", "'", '"', "\\", "\x07", "\x7f", "\xad",
              "\ue000", "\U000E0001", "4", "T", "\u00d8", "ab"]
scenarios.append(run_scenario("pad_edge_cases", [
    {"track": {"title": t, "artist": "Artist Name", "album": "A", "duration": 0, "elapsed": 0},
     "observed_at": T0 + i}
    for i, t in enumerate(pad_titles)
]))

# 4. The playhead through a track change, correction push, drift, seeks and reset.
def pt(title, elapsed, duration=200.0):
    return {"title": title, "artist": "Artist", "album": "Album", "duration": duration, "elapsed": elapsed}

B = 1758400100.0
scenarios.append(run_scenario("playhead_sequence", [
    {"track": pt("Song A", 0.0), "observed_at": B},
    # Correction push 2.5s in: accepted although it moves the anchor later.
    {"track": pt("Song A", 1.1), "observed_at": B + 2.5},
    # Still inside the settle window (opened at B, runs to B+6).
    {"track": pt("Song A", 5.9), "observed_at": B + 5.99},
    # Steady state: a stale read (song looks earlier) is ignored.
    {"track": pt("Song A", 30.0), "observed_at": B + 32.0},
    # A fresher read, song 0.7s further along -> corrected forward.
    {"track": pt("Song A", 63.11), "observed_at": B + 62.5},
    # About 0.5s forward: the threshold is strict.
    {"track": pt("Song A", 93.11 + 0.5), "observed_at": B + 92.5},
    # Forward seek of 40s.
    {"track": pt("Song A", 140.0), "observed_at": B + 100.0},
    # Backward seek.
    {"track": pt("Song A", 20.0), "observed_at": B + 130.0},
    # Drift of exactly +10: not a seek, and positive drift is never "corrected".
    {"track": pt("Song A", 40.0), "observed_at": B + 160.0},
    "reset",
    # Same key after a pause: a fresh anchor, no log.
    {"track": pt("Song A", 40.0), "observed_at": B + 400.0},
    # Different track: fresh anchor and a new settle window.
    {"track": pt("Song B", 3.0), "observed_at": B + 500.0},
    # At settled_at exactly the steady-state rules apply: +8.5 is ignored.
    {"track": pt("Song B", 0.5), "observed_at": B + 506.0},
]))

# 5. Formatting ties: drifts that are exactly x.x5 in binary.
C = 1758400200.0
scenarios.append(run_scenario("playhead_rounding_ties", [
    {"track": pt("Tie", 0.0), "observed_at": C},
    {"track": pt("Tie", 20.25), "observed_at": C + 20.0},    # drift -0.25: ignored
    {"track": pt("Tie", 30.75), "observed_at": C + 30.0},    # corrected 0.75 -> "0.8"
    {"track": pt("Tie", 42.0), "observed_at": C + 40.0},     # corrected 1.25 -> "1.2", half to even
    {"track": pt("Tie", 74.25), "observed_at": C + 60.0},    # seek -12.25 -> "-12.2"
    {"track": pt("Tie", 82.0), "observed_at": C + 80.0},     # seek +12.25 -> "+12.2"
    {"track": pt("Tie", 0.0), "observed_at": C + 500.0 + 0.125},           # +513.375 -> "+513.4"
    {"track": pt("Tie", 0.0), "observed_at": C + 500.0 + 0.125 - 10.05},  # -10.05, not a binary tie
]))

# 6. Status line choices.
for sl in ("name", "state", "details", " Details ", "STATE", "bogus", ""):
    scenarios.append(run_scenario(f"status_line[{sl}]", [
        {"track": pt("Status", 10.0), "observed_at": T0},
    ], status_line=sl))

# 7. Artwork sources, links and SHOW_ALBUM.
base = {"title": "Song", "artist": "Artist", "album": "Library Album", "duration": 180, "elapsed": 5}
long_url = "https://music.apple.com/us/album/x/1?" + "a=b&" * 80
scenarios.append(run_scenario("art_and_links_show_album", [
    {"track": dict(base, store_id="111"), "observed_at": T0,
     "art": {"via": "store", "url": "https://img/1.jpg", "matched": "Catalog Album"}, "links": KENDRICK_LINKS},
    {"track": dict(base, store_id="111", title="No matched album"), "observed_at": T0,
     "art": {"via": "store", "url": "https://img/2.jpg", "matched": ""}, "links": {"song": long_url}},
    {"track": dict(base, title="Uploaded", artwork_b64="/9j/AAAA"), "observed_at": T0,
     "art": {"via": "uploaded", "url": "https://example-host.example-tailnet.ts.net/art/abc.jpg"}},
    {"track": dict(base, title="Existing"), "observed_at": T0,
     "art": {"via": "existing", "url": "https://example-host.example-tailnet.ts.net/art/def.jpg"}},
    {"track": dict(base, title="Fuzzy"), "observed_at": T0,
     "art": {"via": "search", "url": "https://img/3.jpg", "matched": "Fuzzy Album"}},
    {"track": dict(base, title="No album", album=""), "observed_at": T0,
     "art": {"via": "search", "url": "https://img/4.jpg", "matched": ""}},
    # Links without art: text links stay, the cover link has nothing to sit on.
    {"track": dict(base, store_id="222", title="Links no art"), "observed_at": T0, "links": KENDRICK_LINKS},
], show_album=True))

scenarios.append(run_scenario("art_show_album_off", [
    {"track": dict(base, store_id="111"), "observed_at": T0,
     "art": {"via": "store", "url": "https://img/1.jpg", "matched": "Catalog Album"}, "links": KENDRICK_LINKS},
]))

# 8. Unresolved artwork: once per track key, with what the phone sent.
scenarios.append(run_scenario("unresolved_once_per_track", [
    {"track": dict(base, store_id="999"), "observed_at": T0},
    {"track": dict(base, store_id="999"), "observed_at": T0 + 1},
    {"track": dict(base, title="Local file", store_id="0"), "observed_at": T0},
    {"track": dict(base, title="With jpeg", artwork_b64="/9j/AAAA"), "observed_at": T0},
    {"track": dict(base, title="Minus one", store_id="-1"), "observed_at": T0},
    {"track": dict(base, title="Padded id", store_id="  555  "), "observed_at": T0},
    {"track": {"duration": 60, "elapsed": 1}, "observed_at": T0},
    {"track": {"duration": 60, "elapsed": 1}, "observed_at": T0 + 30},
]))

# 9. Clipping and odd numbers.
emoji_title = "\U0001F3B5" * 130
scenarios.append(run_scenario("clipping_and_numbers", [
    {"track": {"title": emoji_title, "artist": "x" * 200, "album": "y" * 129, "duration": 100.9, "elapsed": 0.3},
     "observed_at": T0 + 0.9},
    {"track": {"title": "Negative elapsed", "artist": "A", "duration": 100, "elapsed": -5.5}, "observed_at": T0},
    {"track": {"title": "Past the end", "artist": "A", "duration": 100, "elapsed": 250.25}, "observed_at": T0},
    {"track": {"title": "Before 1970", "artist": "A", "duration": 100, "elapsed": 2000000000.7}, "observed_at": T0},
    {"track": {"title": "Zero duration", "artist": "A", "duration": 0, "elapsed": 10}, "observed_at": T0},
    {"track": {"title": "Negative duration", "artist": "A", "duration": -3, "elapsed": 10}, "observed_at": T0},
    {"track": {"title": "String numbers", "artist": "A", "duration": "215.25", "elapsed": "7.5"}, "observed_at": T0},
    {"track": {"title": "Numeric title", "artist": 4, "duration": 90, "elapsed": 0}, "observed_at": T0},
    # Absent and null numbers are 0 to relay.py (float(x or 0)) and to Issun.
    {"track": {"title": "No elapsed", "artist": "A", "duration": 100}, "observed_at": T0 + 2},
    {"track": {"title": "Null elapsed", "artist": "A", "duration": 100, "elapsed": None}, "observed_at": T0 + 3},
    {"track": {"title": "Null duration", "artist": "A", "duration": None, "elapsed": 5}, "observed_at": T0},
    {"track": {"title": "No numbers", "artist": "A"}, "observed_at": T0},
]))

# ───────────────────────────── direct unit parity ─────────────────────────────

pad_seq = []
relay._padded_seen.clear()
for text, label in [("i", "title"), ("i", "artist"), ("i", "title"), ("Ø", "artist"), ("", "title"),
                    ("  ", "artist"), ("ok", "title"), ("\U0001F3B5", "title"), ("\u2060", "title")]:
    result, logs = captured(relay.pad_for_discord, text, label)
    pad_seq.append({"text": text, "label": label, "result": result, "logs": logs})

playhead_seq = []
ph = relay.Playhead()
D = 1758400300.5
for op in [("a", 0.0, D), ("a", 3.0, D + 2.5), ("a", 9.0, D + 9.0), ("a", 10.0, D + 10.0 + 0.6),
           ("a", 50.0, D + 25.0), ("a", 0.0, D + 60.0), "reset", ("a", 0.0, D + 61.0),
           ("b", 1.0, D + 70.0), ("b", 1.0, D + 76.0), ("b", 30.0, D + 76.0 + 17.0)]:
    if op == "reset":
        ph.reset()
        playhead_seq.append({"op": "reset"})
        continue
    key, elapsed, now = op
    result, logs = captured(ph.anchor, key, elapsed, now)
    playhead_seq.append({"op": "anchor", "key": key, "elapsed": elapsed, "now": now, "start": result, "logs": logs})

materially = []
base_act = {"details": "Song", "state": "Artist", "activity_type": 2, "status_display_type": 1,
            "start": 1758400000, "end": 1758400200, "large_image": "https://img/1.jpg"}
pairs = [
    (None, None), (base_act, None), (None, base_act), (base_act, dict(base_act)),
    (dict(base_act, start=1758400002), base_act), (dict(base_act, start=1758400003), base_act),
    (dict(base_act, start=1757400000 - 2), dict(base_act, start=1757400000)),
    (dict(base_act, end=1758400197), base_act), (dict(base_act, end=1758400202, start=1758399998), base_act),
    ({k: v for k, v in base_act.items() if k != "start"}, base_act),
    ({k: v for k, v in base_act.items() if k not in ("start", "end")},
     {k: v for k, v in base_act.items() if k not in ("start", "end")}),
    (dict(base_act, details="Song2"), base_act), (dict(base_act, state="Artist2"), base_act),
    (dict(base_act, activity_type=0), base_act), (dict(base_act, status_display_type=2), base_act),
    ({k: v for k, v in base_act.items() if k != "status_display_type"}, base_act),
    (dict(base_act, large_text="Album"), base_act), (dict(base_act, large_url="https://a"), base_act),
    (dict(base_act, details_url="https://s"), base_act), (dict(base_act, state_url="https://t"), base_act),
    (dict(base_act, large_image="https://img/2.jpg"), base_act),
    ({k: v for k, v in base_act.items() if k != "large_image"}, base_act),
]
for new, old in pairs:
    materially.append({"new": to_cs(new), "old": to_cs(old), "different": relay._materially_different(new, old)})

repr_cases = ["i", "'", '"', "it's", 'say "hi"', "both ' and \"", "\\", "a\\b", "\t", "\n", "\r", "\x00", "\x07",
              "\x1b", "\x1f", " ", "\x7f", "\x80", "\x85", "\xa0", "\xad", "\xe9", "\xff", "\u0100", "\u0301",
              "e\u0301", "\u200b", "\u200d", "\u2028", "\u2029", "\u2060", "\u3000", "\ufeff", "\ue000",
              "\uffff", "\ufffd", "\U0001F3B5", "\U000E0001", "\U000F0000", "\U0010FFFF", "\U0001FFFF",
              "\u0378", "Ø", "日本", "\u1680", "\u180e", "\u061c", "\u0600"]
reprs = [{"text": s, "repr": repr(s)} for s in repr_cases]

fixed_values = [0.0, -0.0, 0.04, -0.04, 0.05, 0.15, 0.25, 0.35, 0.45, 0.75, 1.25, 2.5, 12.25, -12.25, 12.35,
                -12.35, 10.05, -10.05, 0.7000000476837158, 99.95, 999999.95, 1e-7, 123456789.25, -0.5,
                1758400000.123 - 1758399987.873, 0.9500000000000001, 0.9499999999999999,
                0.5, 1.5, 3.5, -2.5, 1e16 + 2, 5e-324, 1758400000.125 - 1758399987.875]
fixed = [{"value": v, "plus": format(v, "+.1f"), "plain": format(v, ".1f"), "zero": format(v, ".0f")}
         for v in fixed_values]

spaces = [c for c in range(0x110000) if chr(c).isspace()]

doc = {
    "generated_by": f"relay.py {relay.RELAY_VERSION} under Python {sys.version.split()[0]}",
    "scenarios": scenarios,
    "pad_sequence": pad_seq,
    "playhead_sequence": playhead_seq,
    "materially_different": materially,
    "repr": reprs,
    "fixed": fixed,
    "python_spaces": spaces,
}


# ─────────────────────────────── ParityData.cs ───────────────────────────────

HEADER = '''namespace Issun.Core.Tests.Presence;

// Expected values generated by calling relay.py's own build_payload, Playhead,
// pad_for_discord and _materially_different under Python, with every artwork
// function stubbed on the imported module so nothing touched the network.
// {generated_by}, pypresence 4.6.2.
//
// Committed as data so the tests need no Python. To regenerate, run
// generate_parity.py, beside this file, against the relay.py to match.
//
// Each build step carries the phone's JSON body (parsed here with
// NowPlayingPush.Parse, exactly as a real push would be), the observed_at the
// worker passed, the ArtworkResult and CatalogLinks Issun's resolver would hand
// the builder for that relay.py artwork chain, the activity relay.py built, and
// every line it printed, one entry per print() call.
internal static class ParityData
{{
    public const string Json = """"
'''

FOOTER = '''
"""";
}
'''


def d(value):
    return json.dumps(value, ensure_ascii=True, separators=(",", ":"))


def rows(items, indent):
    return ",\n".join(indent + d(item) for item in items)


out = ["{", ' "generated_by":' + d(doc["generated_by"]) + ",", ' "scenarios":[']
scenario_blocks = []
for sc in doc["scenarios"]:
    head = {k: v for k, v in sc.items() if k != "steps"}
    opener = "  " + d(head)[:-1] + ',"steps":['
    scenario_blocks.append(opener + "\n" + rows(sc["steps"], "   ") + "\n  ]}")
out.append(",\n".join(scenario_blocks))
out.append(" ],")
for key in ("pad_sequence", "playhead_sequence", "materially_different", "repr", "fixed"):
    out.append(f' "{key}":[')
    out.append(rows(doc[key], "  "))
    out.append(" ],")
out.append(' "python_spaces":' + d(doc["python_spaces"]))
out.append("}")
body = "\n".join(out)
assert json.loads(body) == doc

# *.cs is eol=crlf in .gitattributes.
with open(TARGET, "w", encoding="ascii", newline="\r\n") as fh:
    fh.write(HEADER.format(generated_by=doc["generated_by"]) + body + FOOTER)
print("wrote", TARGET, "-", len(scenarios), "scenarios,", sum(len(s["steps"]) for s in scenarios), "steps")

os.chdir(HERE)
shutil.rmtree(WORK, ignore_errors=True)
