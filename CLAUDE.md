# Context for Claude Code

**Issun** is the Windows receiver for **Ammy**. Ammy (iOS, `C:\Users\links\Repos\Ammy`)
reads Apple Music's now-playing state and POSTs it as JSON to an HTTPS endpoint;
Issun receives it on `127.0.0.1:8787` behind Tailscale Funnel and sets Discord
Rich Presence through the Discord desktop app's local IPC pipe. It replaced
relay.py, the Python relay that lived in the Ammy repo's `bridge/` folder, and
keeps every behaviour relay.py had, including the fixes that cost days to find.

The name is from Ōkami: Issun is the tiny Poncle who travels with Amaterasu,
"Ammy", and does the talking she can't. That is this app's whole job.

```
iPhone (Ammy) ──https──▶ <machine>.<tailnet>.ts.net (Tailscale Funnel) ──▶ Issun, 127.0.0.1:8787 ──named pipe──▶ Discord desktop
```

**History:** the modules were built in parallel against `Contracts.cs` on
21–22 Sept 2026. **Issun took over from relay.py on 22 Sept 2026** (build 23).
The installed copy is build 46, updated from build 43 on 3 Nov 2026 at the
owner's yes. Build 43 brought the new icon, and a tray hint and new-key notice
that no longer name Ammy; build 46 took the old description off the Start menu
entry. Check `/version` against `gh release list -R kaydenpmd/Issun`
before assuming the installed copy runs this code. The live test is still partway
through — see "The owner's live test" at the end of this file for what has and
hasn't been checked, and on which build. The switch was made permanent the same
evening: Issun lives at `%LOCALAPPDATA%\Programs\Issun\Issun.exe` and starts in
the tray from the HKCU Run key.

**There is no rollback, since 25 Sept 2026.** The owner: "kill relay.py it's
obsolete". The Ammy repo's `bridge/` folder was deleted (committed 9 Oct 2026),
and the last relay.py stays in its history: `git -C C:\Users\links\Repos\Ammy
show 3b7875c:bridge/relay.py`. The same day the owner decided to move off the
old Cloudflare tunnel address "in favor of tailscale. need to live among the
people", so Tailscale Funnel is to be the only way in. On 9 Oct 2026, and
still on 27 Oct, that was not yet true: the `cloudflared` service still ran, and Issun's Public address
setting still held the old address, imported from relay.py's `.env`, which
overrides the Tailscale name for the window's endpoint and for uploaded-cover
URLs. Ammy's CLAUDE.md ("Funnel is the only endpoint") has the steps left,
which are the owner's to take.

Ammy's CLAUDE.md learned this the hard way, twice: documented state decays in
whichever direction you are not looking. **Check `git log`, not this file.**

## What Issun is, in anything a user reads

**A Discord receiver for any source, never "Ammy's receiver".** The owner, 23 Sept
2026: "remove all references to ammy STOP. but don't remove references to
discord. this is meant for discord." The window, the tray and log notices say
**source**, not Ammy or phone. The Source row was the Phone row until then. It
is the mirror of Ammy's own rule, where user-facing text never names Discord.

Since 3 Nov 2026 Issun gives the Start menu entry no description (public text
is the owner's to write). Earlier builds gave it one, and an entry was then
only rewritten when the .exe moved, so the owner's own still had the first,
"Shows what Ammy is playing on Discord". A run now takes the old ones off
(`StartMenuShortcut.OldDescriptions`), changing nothing else in the entry, and
leaves any other description alone. The owner's entry lost its old one on
3 Nov 2026, the first time build 46 started.

Three things still carry the name on purpose, and they stay:
- the `X-Ammy-Relay` response header, which Ammy checks for by exact name;
- log lines kept word for word from relay.py, such as `[init] phone reports Ammy …`,
  which hundreds of parity tests pin;
- this file and code comments, which explain how the two fit together.

The same day the owner removed three settings from the window and **locked** them:
the member list always shows the artist, the card never gets an album line, and
cover matching uses relay.py's 0.35 floor. `IssunHost.Locked()` applies that
wherever settings enter the app, including load, Apply and import, and the importer
notes `STATUS_LINE`, `SHOW_ALBUM` and `ART_MIN_SCORE` as not applying. The
`Settings` record keeps the fields so the relay.py parity tests can still
exercise the builder's other modes. Don't put them back in the window without
asking. The check-in history file was renamed `uptime.log` at the same time.

On 24 Sept 2026 the owner had the window's **progress bar removed**, along with
the plumbing that existed only to feed it (`HostSnapshot.Intended` and
`TrackObservedAt`, `IPresenceWorker.Intended`, the window's `Playhead`).
Discord's card keeps its bar; that comes from `ActivityBuilder`'s timestamps
and is untouched. The same day the card's "Nothing playing" became "Not
playing". Don't bring the window's bar back without asking.

The card's source line is the source's name and number, e.g. `Source: Ammy 1.0
(97)`, from the push's `app_name` (`IPhoneDiagnostics.SourceName`). No word
"version": the owner, 23 Sept 2026, "NO 'VERSION' JUST NAME AND NUMBER".

## Why it exists

Ammy's CLAUDE.md, open work item 1, counted what a stranger needs to run the
relay end to end (Python, `relay.py`, a hand-made key in a `.env`, a Scheduled
Task, Tailscale, a Discord application). The single biggest lever it named was
"packaging the relay as one self-provisioning executable that registers its own
autostart", and it said outright that this is Issun, not a second thing to
build. Fixing relay.py's own first-run experience was discarded on 18 Sept 2026
as throwaway work for exactly that reason.

So Issun generates its own key on first run, registers its own autostart, reads
Tailscale's state instead of asking for a URL, offers to turn Funnel on, and
imports a relay-era `.env` so the owner's phone keeps working without
re-pairing.

**The shape is unchanged, deliberately.** Discord has no Rich Presence SDK on
iOS. Setting presence from a phone directly would mean a Gateway WebSocket with
a user token, which is self-botting, against ToS, and gets accounts terminated.
The phone never talks to Discord; the desktop app's local IPC pipe is the
sanctioned path. **Any change that moves Discord communication onto the phone is
wrong.** The accepted cost: presence needs the PC awake with Discord desktop
open. That is not a bug to fix.

## Issun names Discord. Ammy doesn't.

Ammy's user-facing copy is receiver-agnostic on purpose. It is a sender that
POSTs to whatever endpoint the user chooses, built to pair with Issun and usable
with anything, and naming Discord there has had to be undone once already (see
Ammy's CLAUDE.md, "What Ammy is"). Issun is the opposite case: it *is* the
Discord receiver, so its window and release notes name Discord plainly. The
README is only the name since 3 Nov 2026: public text is the owner's to write.

The rule runs one way. **Don't let Issun's framing leak back into Ammy's**
listing, landing page, permission strings or UI. And the Discord application
stays named `Apple Music`, not Issun: its name is the text after "Listening to",
and that should describe the source, not the bridge.

## Layout

```
Issun.sln
Directory.Build.props           <VersionPrefix>, the only version a human sets
.github/workflows/build.yml     test, publish the single-file .exe, release, on every push to main
src/Issun.Core/                 everything that isn't UI, testable without a window
  Contracts.cs                  the seams between modules; read this first
  Push.cs                       NowPlayingPush, TrackInfo, TrackText (defaults, 128-char clip, track key)
  Settings.cs                   Settings, IConfig / MutableConfig, ISettingsStore
  Timing.cs                     relay.py's timing constants
  Log.cs                        "[tag] message" lines: file, in-memory ring, per-test capture
  Clock.cs                      IClock, SystemClock, ManualClock, unix-seconds helpers
  Paths.cs                      where state lives; ISSUN_HOME
  IssunInfo.cs                  Version, Build, Commit, Display, Wire
  <Module>/                     one folder per module, namespace Issun.Core.<Folder>
src/Issun/                      WPF window and tray; talks only to IIssunHost
tests/Issun.Core.Tests/         xUnit, one folder per module, namespace Issun.Core.Tests.<Folder>
tools/                          make_icon.py, which draws the icon
```

The modules, by contract. Each replaces a named piece of relay.py, and the doc
comments in `Contracts.cs` say which function each member replaces.

| Module | Types | Was, in relay.py |
|---|---|---|
| state (`State/`) | `PhoneState`, `PhoneDiagnostics`, `UptimeLog`, `CheckinProcessor` | `State`, `record_phone_version/diag`, `_diag_summary`, `_log_line`, `note_checkin`, `note_silence`, `_gap_verdict`, `print_summary`, the middle of `do_POST` |
| activity | `ActivityBuilder`, `Playhead`, `DiscordText` | `build_payload` minus artwork, `Playhead`, `pad_for_discord`, `_materially_different` |
| artwork | `ArtworkResolver` | the store-ID lookup, `_album_url`, uploaded JPEGs, the cache pruning, `_normalize` / `_similarity` / `_score` (an exact port of `difflib.SequenceMatcher`), fuzzy search |
| discord | `DiscordIpcClient`, `PresenceWorker` | pypresence itself, `rpc_worker`, `push_with_fallback` |
| server | `RelayServer` (Kestrel) | `Handler`, `QuietHTTPServer`, `public_state` |
| platform | `SettingsStore`, `KeyGenerator`, `EnvImporter`, `Autostart`, `TailscaleCli`, `SelfInstall`, `StartMenuShortcut` | `.env` and `_load_env_file`, `install-task.ps1`, the hand-typed `tailscale funnel` command |
| host | `IssunHost` | `main()`: builds every module, wires them, gives the window one object |
| ui | `App`, `MainWindow`, `SetupWindow`, the tray, `DemoHost`, `HostFactory` | a PowerShell window and `relay.log` |

**The icon, since 28 Oct 2026, is two waves arriving at a speech bubble**, the
owner's pick. `tools/make_icon.py` draws it in the style of the owner's iPhone
apps (Albums' squircle, black, greys and gradient), with the frames from 32 px
down laid out by hand. It is the second half of Ammy's icon, where the same two
waves leave an album cover. The ink-circle placeholder before it was drawn by
`tools/make-icon.ps1`, still in git history.

## Rules every module follows

- **Thread-safe.** HTTP requests arrive on Kestrel's threads, the presence
  worker runs its own loop, and the window reads snapshots on the UI thread.
- **Invariant culture** for anything parsed, logged or put on the wire. The
  machine's culture is not guaranteed; a comma decimal separator in `0.35`, a
  log line or a JSON number breaks parity silently.
- **Log lines keep relay.py's exact text.** Every `[tag] message` that relay.py
  printed is written the same way through `Log.Write`, em dashes included. The
  owner reads these logs, and `relay.log` history has to stay greppable across
  the switch. Don't tidy the wording.
- **Every failure path logs.** See the gotchas below; this is the project's
  recurring bug.
- **Time is injected.** `IClock` returns unix seconds as a double, the same unit
  as Python's `time.time()`, so ported arithmetic keeps its shape. Tests use
  `ManualClock`.
- **Parity is checked against Python and frozen into C#.** Expected values come
  from running relay.py's own functions and are committed into the tests as
  literals, so the tests have no Python dependency. To generate more, work from a
  scratch folder, since importing relay.py creates `./art_cache` in the current
  directory. relay.py is only in the Ammy repo's history now: both
  `generate_parity.py` scripts extract it from commit `3b7875c` by default, and
  for anything else, `git -C C:\Users\links\Repos\Ammy show
  3b7875c:bridge/relay.py > relay.py` in the scratch folder. Load it with
  `importlib.util.spec_from_file_location`, and stub `artwork_by_store_id`, `store_uploaded_artwork`,
  `existing_uploaded_artwork`, `artwork_url` and `catalog_links` on the module
  so nothing touches the network. pypresence 4.6.2 is installed for Python 3.14.
- **`Contracts.cs` is shared by every module.** Change it deliberately and
  update every implementation in the same commit.

## Build, test, run

```
dotnet build Issun.sln
dotnet test
dotnet run --project src/Issun -- --demo
```

.NET 9 SDK, Windows only: WPF, named pipes and the registry are all Windows.

- `--background`: start in the tray with no window. The autostart entry passes
  it.
- `--demo`: `HostFactory` returns `DemoHost`, a fake whose track and
  statuses change over time. It must not write into the real data folder.
- `--screenshot <png>` (with `--demo`): renders the window offscreen to a PNG
  after layout, then exits. **This is how a session looks at the UI**: render,
  then Read the PNG. It never puts a window on the owner's desktop.
  `--setup-page 0..3` renders a page of the setup window instead, and
  `--expanded` there draws the QR code (of the demo's made-up key).
- `--uninstall`: what Settings › Apps runs. Asks first, quits a running Issun,
  then removes the Run entry, the Start menu entry, the Apps entry and the
  install folder. Settings, key, logs and history stay.
- `ISSUN_HOME`: puts both the config and data folders under one directory
  (see `Paths.cs`).

relay.py's `--version` and `--summary` have no command-line equivalent: a WinExe
has no console to print to. Use `GET /version`, the `.exe`'s file properties, or
the window's Diagnostics section, which shows `IUptimeLog.Summarize()`.

A single-file `.exe` unpacks WPF's native DLLs under `%TEMP%\.net` on first
launch. That is expected, not litter.

### Testing on a clean PC: Windows Sandbox

`pwsh tools/sandbox/run.ps1` (from PowerShell, not Claude Code's Bash) builds a
release-numbered `.exe`, opens Windows Sandbox, and runs `tools/sandbox/test.ps1`
inside it through `wsb exec`: first run, the setup window page by page, the
self-install's Apps, Start menu and Run entries, a second launch, an update over
the running copy, and uninstall as Settings › Apps runs it. Every check prints
`ok` or `FAIL`; the report, screenshots and Issun's logs land in
`%LOCALAPPDATA%\IssunSandbox\<time>\out`. `-Keep` leaves the sandbox open,
`-Steps first,update` runs some steps. About two minutes. The sandbox has its
own network and registry, so it is the one place a self-installing build can
run on this PC; never open its `.exe` outside it. Enabled on 12 Nov 2026
(`dism /online /enable-feature /featurename:Containers-DisposableClientVM
/all`, from an elevated shell; `Enable-WindowsOptionalFeature` failed in
PowerShell 7 with "Class not registered"). Computer use can't see the Sandbox
window (its owner, `WindowsSandboxRemoteSession`, can't be granted), which is
why the test drives itself: UI Automation for WPF, keystrokes for message boxes,
whose buttons UI Automation didn't find from there. It can't test Discord,
Tailscale, Funnel or the phone.

### Testing without touching the live setup

The owner's receiver is live on this machine. These are hard limits:

- **Never bind port 8787.** The live receiver is on it. Tests pick free ports.
- **Never connect to `\\.\pipe\discord-ipc-N`.** `DiscordIpcClient` takes a pipe
  prefix; tests serve their own fake Discord on a unique one.
- **Autostart tests** use a throwaway `HKCU\Software\IssunTests\<guid>` for both
  registry paths and delete it afterwards.
- **Tailscale:** only read-only commands (`tailscale status --json`,
  `tailscale funnel status --json`, `tailscale debug prefs`). `EnableFunnelAsync`
  and `EnableUnattendedAsync` run for real only when the owner clicks a button;
  tests go through an injected process runner.
- **Self-install tests** install into a temp folder and register under a
  throwaway `HKCU\Software\IssunTests\<guid>` key, never the real Uninstall key
  or `%LOCALAPPDATA%\Programs\Issun`.
- **No secrets anywhere.** Never read or print `C:\Users\links\Python Scripts\.env`.
  No key, Discord client ID or tailnet name (the part between the machine name
  and `.ts.net`) in code, tests, fixtures, logs or commit messages. Fixtures say
  `example-tailnet`. When a secret has to be checked, print its `.Length`, never
  the value.
- **Never enable or start the `Ammy Relay` task.** It was still registered,
  disabled, on 27 Oct 2026, beside relay.py's loose copy in
  `C:\Users\links\Python Scripts`, and there is no rollback: starting it would
  put a second receiver on 8787. Unregistering it or deleting that folder is the
  owner's call.
- **Don't change Tailscale or `cloudflared` state** unless the owner asks.

## Telling builds apart

`<VersionPrefix>` in `Directory.Build.props` is the only number a human sets.
CI supplies the rest as MSBuild properties, which `Directory.Build.props` stamps
into assembly metadata for `IssunInfo` to read back:

- `IssunBuild` is `git rev-list --count HEAD`: a commit count, so it climbs on
  its own.
- `IssunCommit` is the short SHA.

A local build has neither, and reports build 0, shown as `dev`, with commit
`local`.

| Where | Looks like |
|---|---|
| `IssunInfo.Display`, the window | `0.1.0 (12)`, the same shape as Ammy's Version row `1.0 (80)` |
| `IssunInfo.Wire`: `GET /version` body and the `X-Ammy-Relay` header | `issun 0.1.0 (12)` |
| Uptime-log stamp | `[issun 0.1.0 (12) / app 1.0 (80)]` |
| Artifact, release asset | `Issun-0.1.0-b12-a1b2c3d.exe`, tag `v0.1.0-b12` |
| Explorer → Properties → Details | File version `0.1.0.12` |

**Why a commit count, not `github.run_number`.** Ammy used the run number until
18 Sept 2026, and it broke the moment another workflow started calling the
build: in a reusable workflow the `github` context belongs to the caller, so the
number restarted at 1. A commit count doesn't care which workflow runs. It needs
the full history, so the checkout uses `fetch-depth: 0`, and the workflow
refuses a shallow clone outright rather than publishing "build 1" again.

relay.py answered `/version` and `X-Ammy-Relay` with a bare `1.9.0`; Issun
answers `issun 0.1.0 (12)`, so a reader can tell which receiver answered. Ammy
only checks that the header is **present**: `PushOutcome.refused(fromRelay:)` in
`PresenceRelay.swift` tests `value(forHTTPHeaderField:) != nil`, read on
21 Sept 2026. Anything else that compared `/version` against a relay version
string needs updating.

This file deliberately does **not** state the current version. Ammy's did, and
it went stale within the hour, twice. Ask `/version`.

## Where state lives

Never beside the `.exe`. A single-file download lives wherever the browser put
it, and moving it must not lose anything.

| What | Where | Notes |
|---|---|---|
| Settings | `%APPDATA%\Issun\settings.json` | Roams with the profile. Holds the key in plain text. |
| Log | `%LOCALAPPDATA%\Issun\issun.log` | relay.log's successor. Moves to `.old` past 5 MB; relay.log grew forever. |
| Uptime log | `%LOCALAPPDATA%\Issun\uptime.log` | Named `ammy-uptime.log` until 23 Sept 2026; `IssunHost` renames an old one once, never over an existing file. Import merges relay.py's `ammy-uptime.log` into it, placed before Issun's own lines. |
| Uploaded covers | `%LOCALAPPDATA%\Issun\art_cache\` | 60 newest kept. |
| Autostart | `HKCU\...\CurrentVersion\Run`, value `Issun` | `"<exe>" --background`. |
| The program | `%LOCALAPPDATA%\Programs\Issun\Issun.exe` | Self-installed (below); the one before an update is kept as `Issun.exe.previous`. |
| Settings › Apps entry | `HKCU\...\CurrentVersion\Uninstall\Issun` | No Publisher: public text is the owner's. |
| Window state | `%LOCALAPPDATA%\Issun\window.json` | Whether the tray hint and the setup window have shown once. |

`ISSUN_HOME` puts both folders under one directory instead.

**Settings.** `SettingsStore` writes atomically (a temp file in the same folder,
then replace). A missing file means first run: a fresh key, saved, and
`FirstRun` set so the window shows the Connect your source banner. An existing file with
an empty key gets one generated. A corrupt file is moved aside to
`settings.json.bad-<yyyyMMddHHmmss>`, not overwritten, and the log says loudly
that the source will need the new key ("Your source will need the new key
before it can connect again.").

**The key** is 32 characters of `[A-Za-z0-9]`: typeable on a phone keyboard,
and the same length as the relay-era key. Log lines give its length, never its
value. An empty key refuses every authenticated request: a receiver with no key
configured should be unreachable, not reachable by anyone.

**Autostart** is the HKCU Run key, not a Scheduled Task, and `IsEnabled` also
honours Task Manager's Startup switch (`StartupApproved\Run`). `RepairIfMoved`
repoints the entry when Issun runs from a new path, which is what happens on
every update, because each release has a new filename.

**Tailscale's state is Tailscale's.** Issun reads `tailscale status --json` (the
MagicDNS name, which becomes `PublicBase` unless Settings overrides it),
`tailscale funnel status --json`, and, while Tailscale is running, `tailscale
debug prefs` for Run unattended. It changes nothing unless the owner clicks
**Turn on Funnel** or the setup window's **Turn on** for Run unattended.

**Issun installs itself** (since 11 Nov 2026, `SelfInstall`). A release build
opened from anywhere but the install folder asks a running Issun to quit (a
named event, `Local\Issun-<SID>-quit`), renames the installed `.exe` to
`Issun.exe.previous`, copies itself in, registers the Apps entry, starts the
installed copy and exits. That is the by-hand update procedure at the end of
this file, rename first. Builds 48 and earlier don't listen for the quit
request, so the first update over one of them says to quit Issun from the tray
and open the file again. The Run key and Start menu entry repoint themselves as
before. Local builds never install, and under `ISSUN_HOME` the install folder
is `<ISSUN_HOME>\Programs\Issun`. The installed copy also writes the Apps entry
if it's missing, so the owner's copy, installed by hand, gets one on its first
run of a build that has this.

**The setup window** opens by itself on a first run (no settings.json), once
ever, and from **Open setup** in Settings after that. Four pages, each applying
as it goes: the Discord application ID (applied 0.6 s after it looks like a
17–20 digit ID, with the live Discord status under it), Tailscale (Get
Tailscale, Turn on Funnel, and **Run unattended**, `tailscale set
--unattended`, read from `tailscale debug prefs`' `ForceDaemon`), Start with
Windows (ticked by default on a first run), and Connect your source (endpoint,
key, a QR code behind **Show QR code**, and the source's live status). Its
structure is Microsoft's wizard guidance, saved in the design library at
`microsoft/windows-setup-wizards/`: no welcome page, a main instruction per page
(Windows 11's Title style, 28/36 semibold), Back, Next and Cancel, Cancel
becoming Close on the last page. The rest is the main window's cards. Every
step logs as `[setup]`.

**The QR code is plain JSON**, `{"endpoint": "...", "key": "..."}`
(`PairingCode`), black on white, error correction M. Not a link: a link would
name an app, and Issun names no source. Ammy reads it from its **Scan QR Code**
row (`CodeScanner.swift`), and so can any source.

**Import never writes to relay.py's folder.** It reads the `.env` it is pointed
at and copies the `ammy-uptime.log` beside it, and leaves both where they were.

## relay.py's gotchas, preserved, and where they live now

Each of these cost real time in the Ammy project. Until 9 Oct 2026 the full
stories were in Ammy's CLAUDE.md, "Gotchas already paid for" (`git -C
C:\Users\links\Repos\Ammy show 20ab199:CLAUDE.md`). Ammy's file now points
here, so this section is the record.

**The playhead anchor is paired with the push's arrival time, never the current
time.** The project's most expensive bug, invisible on inspection because every
calculation looks right. The worker builds an activity every second, but the
phone refreshes `elapsed` every thirty. Computing `start = now - elapsed`
against a frozen reading slides `start` forward a second per second, and
Discord's bar falls behind and snaps back. That was the rubberbanding (relay
1.2.1). `IActivityBuilder.Build(track, observedAt, ...)` takes `observedAt`
from `IPhoneState.Get()`'s `UpdatedAt`. **Never pass it the clock.** There is a
regression test: thirty builds with the same `observedAt` while the clock
advances must produce an identical `Start`. Diagnosis tip carried over: the
giveaway was `[playhead] re-anchored +10.0s` repeating with *identical* drift,
and a constant drift is a clock, not a measurement. `MateriallyDifferent` still
treats `Start`/`End` within 2 s as equal (`ActivityBuilder.TimestampTolerance`);
with a correct anchor that is belt-and-braces, since the anchor doesn't move
between pushes at all.

The `Playhead` forward-correction rule ("prefer the reading that implies the
song is further along") has never fired against real data. It is insurance.
Don't cite it as evidence that staleness exists.

**A rejected payload is not a dead socket.** Discord refuses the entire activity
when one field breaks a rule, and relay.py's catch-all read the refusal as a
dropped connection. It reconnected, re-sent the same payload, and looped. The track was
"i" by Kendrick Lamar. Here the two are different exception types:
`DiscordRejectedException` (Discord answered `SET_ACTIVITY` with `ERROR`; the
connection is fine) and `DiscordConnectionLostException` (the pipe is gone).
`PresenceWorker` catches them in two separate clauses. A rejection keeps the
connection, retries once with the optional extras stripped, then records the
*intended* payload as sent so the next tick doesn't re-send a refused payload
forever. A lost connection is logged, disposed, and waited on (`LostDelay`,
1 s; never spin) before reconnecting. **Don't collapse those two catch clauses
back together.** It took an hour to diagnose in relay.py because it printed
`[rpc] <title>` only after a push succeeded, so the one track Discord refused
was the only one with no line. Log the refusal itself, as `PresenceWorker` does.

**Two characters minimum.** Discord refuses `details` or `state` shorter than
two characters. `DiscordText` pads with U+2060 WORD JOINER (invisible, but it
counts), measures length in code points the way Python's `len()` does, and
substitutes `Unknown` for a blank field. The padding notice logs once per
distinct value, because relay 1.3.0 wrote 189 identical lines over one song.

**`push_with_fallback`, and what didn't survive the port.** pypresence raised
`TypeError` for keyword arguments it didn't recognise, and `AttributeError` when
handed an int where it wanted an enum; relay.py shed the offending field by
matching its *quoted* name, because `state` is a substring of `state_url`.
Issun writes the IPC wire format itself, modelled on pypresence 4.6.2's
`payloads.py`, so there are no keyword arguments to reject and no enum to call
`.value` on: `type` is `2` and `status_display_type` is `0`/`1`/`2` on the wire.
The quoted-name rule has no C# analogue.

One pypresence quirk to know before comparing payloads byte for byte. Its
`Payload.set_activity` always puts `status_display_type` on the wire, and uses 0
(name) when it's given none. `Presence.update()` turns `StatusDisplayType.NAME`
into none first, because the enum's value is 0 and 0 is falsy. So relay.py's
`STATUS_LINE=name` reached Discord only through that default, and a relay.py
payload with no status line still carried a 0. Read on 21 Sept 2026 in
`presence.py` and `payloads.py`. The principle survives: when Discord
refuses, shed the optional fields (`DetailsUrl`, `StateUrl`, `LargeUrl`,
`LargeText`, `StatusDisplayType`) and try again rather than tearing anything
down. `IPresenceWorker.Current` is what Discord actually accepted, which may be
the degraded payload.

**Silent failure is this project's recurring bug.** Three times in Ammy, a
function returned nothing on a failure path without logging, and the empty log
read as "working". Every artwork path logs. The uploaded-JPEG rejections, which
relay.py returned `None` from silently, now log once per track. **When you add a
failure path, log it.**

**`X-Ammy-Relay` is on every response.** relay.py set it in `_reply` and
`_reply_json` only, so `/art/` responses went without. Issun sends it on every
response, errors included. Status codes alone can't separate Issun's own 404
(wrong path) from Tailscale Funnel's (hostname resolves, nothing served on that
port), and Ammy's "Wrong Path" and "Nothing at That Address" messages depend on
the header.

**`/health` returns exactly `ok`.** Shortcuts test for that string, which is why
the version has its own route.

**A GET never performs a lookup.** `GET /now-playing` is a projection of state,
never the raw push (which carries ~80 KB of base64 and the whole diag block).
Its artwork, links and looked-up explicitness come only from `CachedArtwork` /
`CachedLinks` / `CachedExplicit`. A public endpoint must not be a way to make
this machine send traffic.

**`/art/` is unauthenticated.** Discord's CDN fetches covers itself and can't
send a header. Filenames are the first 20 hex characters of
`sha256(TrackText.Key)`, byte-identical to relay.py's, so they aren't
enumerable. `UploadedArtPath` rejects anything but a bare `.jpg` that exists
directly inside `art_cache`. Uploaded art needs a public base URL, because
Discord's CDN cannot reach 127.0.0.1: the Settings override, else
`https://<tailscale dns name>`.

**127.0.0.1 only.** Only a tunnel reaches it: Tailscale Funnel, and, until the
owner removes it, the old `cloudflared` tunnel (see the top of this file). Kestrel rather than
`HttpListener`, because http.sys matches on the Host header: a `localhost`
prefix refuses the requests Funnel forwards with the public hostname, and a
wildcard prefix needs admin (see the comment in `Issun.Core.csproj`).

**Links only ever come from the exact store-ID lookup.** A near-miss cover is a
cosmetic annoyance; a link that opens the wrong song is a broken promise.

**So does explicitness, when the source doesn't say** (added 23 Sept 2026, the
window's "E" after the title). The E is a typed 🅴 (U+1F174) after a space, in
the title's own colour (`NowPlayingText.CardTitle`). WPF's font fallback draws
it with Segoe UI Symbol, seen rendering on 24 Sept 2026. (Yu Gothic and a few
other Japanese fonts have the character too; fallback picks Segoe UI Symbol.) It replaced a hand-drawn badge the owner asked to
have rolled back as more complicated than typing the character. `TrackText.Explicit` takes the push's `explicit`
first: only a JSON `true` or `false` counts, and `false` beats the catalog.
Ammy sends only `true`, because iOS's `isExplicitItem` is a plain Bool whose
`false` also means "unrated". Absent, it falls back to `trackExplicitness` from
the store-ID lookup, and never to fuzzy search, because the clean and explicit
editions of a song are separate catalog entries with the same title. Discord's
activity object has no field for it (checked against Discord's gateway docs
that day), so the card is unchanged; the badge is the window's and
`GET /now-playing`'s. A song entry with no `trackExplicitness`, or a word other
than `explicit` / `cleaned` / `notExplicit`, logs once per lookup.

**The interactive session is load-bearing.** Discord's IPC pipe belongs to the
signed-in user's session. relay.py's Scheduled Task needed `-LogonType
Interactive`, because a session 0 task starts cleanly, listens on 8787, and
never reaches Discord, which looks like a Discord problem and isn't. The HKCU
Run key starts Issun in the interactive session for free. **Don't turn Issun
into a Windows service.**

**The gap-log wording is data.** `UptimeLog` keeps relay.py's phrases exactly:
"phone silent, relay up throughout", "relay was down for ... of it", "— the path
failed, not the app". That includes the word "relay", even though Issun is the
relay now, because months of history and the summary parser depend on them. Only
the tag changed, from `[relay 1.9.0 / app ...]` to `[issun ... / app ...]`.
`Summarize()` reads relay-era lines, untagged lines from before builds were
recorded, and Issun lines, and counts both "relay started" and "issun started".
**Don't split the log per version**, for Ammy's reason: the phone's build decides
survival, and a stamped line can be grouped any way later.
Since build 48 some verdicts and silence lines carry Issun's own words (see
"Where Issun deliberately differs from relay.py"). The summary reads only "gap
HH:MM:SS", "phone silent", "stopped checking in" and the two kinds of start, so
new words go after those, never in their place.

**Diag values follow Python's types.** relay.py compared with
`isinstance(x, int)`, so a JSON bool is not an int and neither is a JSON float.
Flag changes render with Python `repr` (`True`, `None`, `'nominal'`). Parity
tests pin both.

**`seq` is milliseconds since epoch, not a counter.** A counter resets on
relaunch, and every push from the new process would read as older (Ammy item 6).
An absent `seq` always applies, the same compatibility rule as
`RELAY_SECRET` / `RELAY_KEY` and `X-Relay-Secret` / `X-Relay-Key`, both of which
Issun still reads.

**Don't transcribe config from screenshots.** A Discord client ID copied with
one misread digit gets handshake close code 4000, "Client ID is Invalid", which
reads like a deleted application. The Discord status detail says to check for
a misread digit.

The timing constants are in `Timing.cs`: gap threshold 90 s, idle timeout 90 s,
minimum push gap 3 s, seek tolerance 10 s, settle window 6 s. `ArtMinScore`
defaults to 0.35, not the 0.55 an old Ammy note claimed.

## Where Issun deliberately differs from relay.py

Each was a known bug or latent hazard in relay.py 1.9.0. This list was written
from the module specs before integration. Each module's commit says what it
actually changed; **if this list and the code disagree, the code wins**, so fix
this file.

- **Check-ins are atomic.** `CheckinProcessor.Accept` holds one lock around the
  whole sequence `do_POST` ran: read the previous check-in, record version and
  diag (capturing the previous `app_uptime_s` at that moment), apply the `seq`
  rule, stamp the check-in, and note any gap. relay.py didn't. On 14 Sept 2026 a
  near-simultaneous second push logged the same 38-minute gap twice, the second
  time as `app restarted 3358s -> 3359s — it died`, which was wrong. Ammy's
  CLAUDE.md records it as "One wrong line in the history, from a relay.py bug
  Issun fixed". relay.py never got the fix, and the wrong line stays in the
  imported history.
- **A relaunch supersedes the old `seq`.** A push whose `app_uptime_s` fell
  further than its `seq` did, by more than `SameProcessSlack` (5 s), comes from
  a new app process and wins. Uptime merely going backwards isn't enough: a
  request overtaken in flight comes from the same process, sampled a moment
  earlier, and applying it would be exactly the late push `seq` exists to drop.
  Sessions are keyed and superseded, never stacked (Ammy item 7).
- **A phone clock that jumps back is not a race.** In-flight pushes are
  milliseconds apart. A `seq` more than 60 s older than the last one means the
  phone's clock moved, and relay.py would drop every push from then on until it
  restarted. Issun accepts it, resets, and logs why.
- **A check-in is any authorised push.** `LastCheckinAt` updates on every
  authorised push, applied or dropped, and gap and silence detection measure
  that. relay.py measured `updated_at`, which a dropped push never moved.
  `UpdatedAt` still changes only when a push is applied, `playing: false`
  included.
- **The key comparison is constant-time.** relay.py used `==` under a comment
  that said "constant-time-ish".
- **"Discord not reachable" is throttled.** relay.py logged it every 10 s for as
  long as Discord was closed. Issun logs when the reason changes, and at most
  every 10 minutes while it doesn't.
- **Artwork resolves while Discord is disconnected**, so the window can show the
  cover. Concurrent resolves for one track share a single request.
- **Nothing exits over missing config.** relay.py quit with "Set RELAY_KEY" or
  "Set DISCORD_CLIENT_ID". Issun generates the key and runs without a client
  ID, saying in the Discord row what to set.
- **Settings apply live.** The key is read per request, a new client ID
  reconnects Discord, and a new port restarts the receiver.
- **A taken port is a status, not a traceback.** `PortInUseException` asks
  whether relay.py is still running, the likely culprit for anyone moving over
  from it, and the window shows it.

Four more since build 48 (11 Nov 2026), all in the uptime log's verdicts and
the phone's diag, from a census of every log since 24 Aug 2026 (Ammy's
CLAUDE.md, "The census of 11 Nov 2026"). The owner's go-ahead was "Yes, do
Issun", to a proposal to log Ammy 167's journal and fix the two labelling
bugs the census found:

- **A process too old to have started during the gap stayed up.** relay.py
  called any gain under 90% of the gap a death. Issun does so only when the
  uptime is also under the gap plus 20 s (`UptimeLog.InFlightSlack`: Ammy gives
  up on a request after 20 s, so the last push that arrived left the phone no
  earlier than that), because an older process was already running before the
  gap began. Issun wrote two false deaths before this, on 16 Oct 2026 at
  21:56:52 (89081s -> 89165s over 93 s) and 8 Nov at 16:11:50 (6211s -> 6242s
  over 133 s): each push that ended the gap carried a snapshot built before iOS
  froze the app and sent when it woke. The 14 Sept race above now reads as
  stayed up too.
- **No failed pushes is a suspension, not the path.** A running Ammy sends at
  least every 30 s, so a process that stayed up through a gap with `push_fails`
  at 0 wasn't running: iOS had suspended it. The verdict is "— suspended, no
  pushes failed" where relay.py said "— the path failed, not the app", and the
  census found 24 of those in the history. A push without `push_fails` keeps
  relay.py's words, since it can't tell the two apart.
- **Ammy's Stop is the person's doing.** Stop sends `playing: false` with no
  diag, and no other push Ammy sends lacks one, so `CheckinProcessor` takes
  that as a goodbye: only from a source that has sent a diag before, and a push
  the seq dropped changes nothing. The silence after it reads "last seen
  HH:MM:SS, stopped on the phone", and the gap it ends "— stopped on the
  phone", whether or not the process lived through it. Both still say
  "stopped checking in" and "phone silent", so the summary counts them as
  relay.py did, and a Stop that's never followed by Start still counts as
  "never came back".
- **A death names when the dead process last ran.** From Ammy build 167, a new
  process sends its predecessor's last keepalive tick (`prev_alive`, written
  every 10 s while the keepalive is on, so its last value is when that process
  stopped running) and the last 25 lines of its keepalive journal (`prev_tail`)
  on every push until one lands. "— it died" gets ", last ran HH:MM:SS
  engine=yes|no", and issun.log gets the stamp and every journal line, in local
  time, once per launch (`[keepalive] previous run last ran ...`, then
  `[keepalive] journal: ...`). The other fields build 167 added go on the diag
  summary beside the ones they explain, only when sent: `failrun`, `failstage`,
  `failcode`, `sincefail` and `down` after `fails=`, and `intlate`,
  `intreason`, `sincebegan` and `sinceended` after `int=`. `int_on_resume`
  and `int_reason` are also logged as they change.

## CI and releases

`.github/workflows/build.yml`, one job on `windows-latest`, bash throughout like
Ammy's workflows. Every push to `main` that changes more than Markdown runs the
tests, publishes the single-file `.exe`, uploads it, and publishes it as a
GitHub Release. That is the rule Ammy has followed since 18 Sept 2026. **Pushing
to main is releasing**, so write the commit message for whoever reads the
release notes. The notes are the commit message minus trailers, plus the full
SHA, the `.exe`'s SHA-256 (it's unsigned; the hash is the only integrity check)
and the SmartScreen instructions.

- `archive: false` on the upload, so a browser download is the `.exe` itself.
  Ammy uploads a second, archived copy because a later job downloads it and
  `download-artifact` unpacks a bare upload. Here the Release is made in the
  same job from the file on disk, so there's no round trip and one upload.
- The tag `v<version>-b<build>` exists only so GitHub has somewhere to hang the
  asset. `target_commitish` pins it to the commit that was built.
- `workflow_dispatch` from any branch other than `main` builds and uploads but
  doesn't release, because a branch's commit count can collide with one main has
  already used.
- 0.x releases are marked pre-release. That was set because nobody had run one
  against a live phone; builds 23, 34 and 38 have since, but the live test is
  still unfinished (items 5, 6 and 8). Bumping `<VersionPrefix>` to 1.0.0 ends
  the pre-release marking.

**CI has run on GitHub since the first push.** `gh run list -R kaydenpmd/Issun`
shows the runs and `gh release list -R kaydenpmd/Issun` the releases; read the
run after a push rather than assuming it passed. The shallow-clone guard was
checked against a `--depth 1` clone on 21 Sept 2026 and failed as intended. On
the scaffold, the published `.exe` was 74 MB compressed (161 MB without
`EnableCompressionInSingleFile`), FileVersion `0.1.0.2`, and `Issun.Core`
carried the SHA, so the CI properties do reach `IssunInfo` through the project
reference. Trimming is not an option: WPF doesn't support it.

The repo is public, which keeps Windows runner minutes free; they bill at 2× on
a private repo.

## Queued work

Asked for by the owner and not yet done. Take the top item unless told otherwise.

1. **Setup wizard and installer: built 11 Nov 2026, not yet run for real.**
   Talked through first, as asked; the owner chose "Self-install", "QR, scanned
   in Ammy" and "Its own window". Seen only as `--screenshot` renders of the demo
   (the owner declined a click-through of the demo window). **Run for real in
   Windows Sandbox on 12 Nov 2026** (`tools/sandbox/run.ps1`, 20 checks, all
   passing): the first run installs and opens setup within seconds, every page
   and Close work, the update over a running copy swaps it and keeps
   `Issun.exe.previous`, and uninstall removes everything but the settings.
   It found the main window opening 820 tall in a 775 work area with its title
   bar above the screen, fixed by `WindowFit`. Not yet tried: the update over
   the owner's build 48, real Tailscale and Discord, and scanning the code with
   Ammy (merged into Ammy's main). The QR image wasn't decoded here; the phone
   is the test.
   **Its words are the owner's to write** (their rule from 12 Nov 2026, starting
   with this window): build 51 shipped Claude's drafts, to be replaced word for
   word with what the owner writes in the "Issun Setup Wording" artifact
   (claude.ai/artifact/BKtxXmKxc3ot9CSi7D2xRJ; its `texts` collection, one
   document per text, keyed like `discord.title`). That covers the setup window,
   the Setup row, the install and uninstall messages, and Ammy's scan screens and
   camera prompt. The Discord application ID stays a text box (the owner, the
   same day).
   **Still waiting on the owner (12 Nov 2026):** the Tailscale page's two
   cards. They asked to see the whole page first and to go over Tailscale's own
   setup again; the page now shows it in all four states (`--demo-tailscale
   0..3`, screens `setup-1a` to `setup-1d`), and the refresher was given in
   chat. Its new fields (`tailscale.check`, `tailscale.funnel`,
   `tailscale.unattended.*`) have no answers yet. `discord.id.saveFailed` is
   "[have me write error messages myself eventually]": the sample stays until
   they do. The Discord page's live status line ("Set the Discord application
   ID in Settings. ...", older text shared with the main window) stays as it
   is: asked whether to put it on the wording page, the owner said "Leave it".
   Cancel stays: the owner wanted it only if it's standard in this
   kind of window, and Microsoft's wizard guidance puts Cancel in every
   wizard's command area, title-bar X or not. The six commits from
   `8b556b8` to `7d902f8` aren't pushed, because a push releases, and the
   Tailscale cards are still Claude's drafts.
2. **Review findings still open**, from the 23 Sept review that stopped partway:
   - a type slip in `settings.json` discards the key;
   - `Autostart.RepairIfMoved` repoints the Run key at local builds;
   - a bad `PUBLIC_BASE` in a `.env` fails the whole import;
   - the uptime verdict problems in `UptimeLog`. What the review found was
     never written down. Build 48 fixed the three a census of the logs found
     (see "Where Issun deliberately differs from relay.py"), so check whether
     any of the review's are left;
   - CI builds with the .NET 10 SDK, which needs pinning.

## Working with the owner

Limited coding experience: comfortable running commands and reading output,
not writing code. Prefers concise, concrete instructions over conceptual
explanation. When the owner has to run something, give **one command at a time**
and wait for the output; stacked commands hide the failure when an early one
hangs.

The owner wants Claude to do the work rather than describe it: "the more you
can do the better (to an extent lol)". Run the commands, commit, verify with a
real check, and bundle the obvious adjacent work into the same turn rather than
proposing it item by item. The hedge is real. Anything that reaches outside the
repo or is hard to undo gets confirmed first: deleting, spending money, or
touching the live setup (Tailscale Funnel, port 8787, the installed Issun, the
`cloudflared` service, the disabled `Ammy Relay` task, `C:\Users\links\Python
Scripts`). The
GitHub repo is `kaydenpmd/Issun`, public, and
pushing to `main` is routine, as it is in Ammy. Remember that a push is also a
release: every push to `main` that changes more than Markdown publishes the
`.exe`.

**Git is Claude's to run.** A Claude Code session has a real shell on the owner's
Windows machine, so `git status`, `add`, `commit`, `log` and `push` all work
against the clone directly. Run them and report what they printed. The old
hand-it-over rule was Cowork-only (a Linux VM whose mount can't unlink, or no
shell at all) and doesn't apply here.

**Verify, don't assert.** The Ammy project burned several rounds on confident
wrong answers. Read the file, read the log, probe the live URL, read the CI run.
When the owner says something works, believe them and go look. When restating
something the owner said, use their words rather than a stronger paraphrase.

**The owner's live test is the instrument.** Anything that involves the phone,
Discord or the tunnel isn't verified by a unit test. The live test for the first
build should cover:

1. The relay task is stopped and disabled.
2. The `.env` import shows the key coming across.
3. The phone checks in without re-pairing.
4. Discord shows "Listening to Apple Music" with cover, links and a progress
   bar that doesn't rubberband.
5. A skip updates presence.
6. A pause clears it.
7. A reboot brings Issun back in the tray and it reaches Discord.
8. Then a few days of `uptime.log` continuing the relay-era history.

Record the result here, with the build number.

**Build 23 (`e9b578e`), 22 Sept 2026 — the GitHub release, run from Downloads:**

- 1: **passed.** The task was stopped at 20:42:37, and Issun took the port at
  20:42:47 through its 15 s retry, with no restart. The task was disabled at about
  21:25. `Disable-ScheduledTask` is refused as "Access is denied" from a normal
  shell, so it ran through a UAC prompt the owner approved.
- 2: **passed.** The key came across from `RELAY_SECRET` (32 characters), along with
  the Discord application ID and `PUBLIC_BASE`, plus 176 lines of uptime
  history placed ahead of Issun's own.
- 3: **passed.** Ammy 1.0 (83) checked in at 20:43:00 with its existing key, and
  `/version` on the old Cloudflare tunnel address answered `issun 0.1.0 (23)`.
- 4: **passed on Issun's side.** "EAT YOU UP" went to Discord at 21:15:05, once,
  with no re-push on the heartbeats that followed and no `[playhead]` lines. The
  cover came from the exact store-ID lookup, and all three links resolved — the
  album link with `?i=` stripped. That the card *looks* right in Discord is the
  owner's to confirm, not this log's.
- 5, 6 and 8: not yet.

**Build 34 (`ce2d501`), 23 Sept 2026 — the reboot:**

- 7: **passed.** Issun logged `Windows is shutting down; stopping` at 15:25:43
  and stopped cleanly. Windows booted at 15:26:53, and the owner signed in over
  Remote Desktop at 15:27. Issun started in the tray from the Run key at
  15:29:56, then took 8787 and connected to Discord at 15:29:57, with no retries.
- **Tailscale did not come back with it.** It reported `NoState` when Issun
  started, the tray app `tailscale-ipn` wasn't running at 15:31, and Funnel
  wasn't ready again until 18:20:20, almost three hours later. Anything using
  the Funnel address was cut off for that long. The old Cloudflare tunnel
  address, served by a Windows service, answered `issun 0.1.0 (34)` at 15:31.
- No push from the phone had arrived by 02:51 the next morning.

**What that turned out to be, 24 Sept 2026:**

- **Funnel was broken from outside, not just late.** Tailscale connected at
  18:20, but check-host.net probes from Los Angeles, Dallas, Atlanta, Miami and
  New York all had the connection dropped mid-handshake, and the phone saw
  "Secure Connection Failed". Meanwhile `tailscale funnel status` said "Funnel
  on" and the PC reached its own address fine. That matches Tailscale bug
  [#21114](https://github.com/tailscale/tailscale/issues/21114) on 1.102.3
  (Funnel stops serving after a control-plane reconnect). Restarting the
  Tailscale service didn't clear it. Updating to 1.102.4, which asked for a
  reboot, did: all three probed cities got through at 03:18:43, and the phone
  checked in at 03:19:09.
- **Don't test Funnel from this PC.** Lookups of its own ts.net name are
  answered by Tailscale with the tailnet address, even when another DNS server
  is named, and forcing a request through Funnel's public addresses from here
  failed even after Funnel worked. check-host.net is what agreed with the phone.
- **Why Tailscale waited:** it wasn't running unattended, so it connected only
  once its tray app started, and Windows starts that from the common Startup
  folder after the Run-key apps, one at a time. On the 03:08 boot: Discord
  03:09:40, Issun 03:13:43, the tray app and a connected Tailscale 03:18:26. The
  owner has since turned on **Run unattended** (`ForceDaemon: true`), so
  Tailscale connects at boot without waiting for sign-in or the tray.
- **Issun showed "not connected" after Tailscale had connected**, until the
  owner pressed Refresh, because it re-read Tailscale only every 10 minutes.
  `IssunHost.NextTailscaleProbe` now looks every 15 s while Tailscale is
  installed but not connected.

**Updating the installed copy** (build 34 to 38, 24 Sept 2026, about 5 s of
downtime): download the release asset with `gh release download`, check its
SHA-256 against the release notes, `Stop-Process -Force` the running Issun,
copy the new `.exe` over `%LOCALAPPDATA%\Programs\Issun\Issun.exe` (the Run key
points at that path, so autostart needs no change), then start it with
`--background`. **Closing the window doesn't quit it**: `CloseMainWindow()`
only hides Issun to the tray, so the file stays locked and the copy fails. A
second launch while it runs just shows the running copy's window and exits.
Keep the previous build beside it (`Issun.exe.b34.bak`) for rollback. Run all of
it from PowerShell outside the sandbox, as below.

**29 Oct 2026, build 38 to 43 (about 3 s down): the copy failed on a locked
file**, even after `Stop-Process -Force` and `Wait-Process`, and the restart
brought back build 38. What worked was renaming the running `Issun.exe` to
`Issun.exe.b38.bak` first (Windows lets a running `.exe` be renamed, though not
overwritten), copying the new one in, then stopping and restarting. The rename
is the backup too, so do it in that order from the start.

**3 Nov 2026, build 43 to 46 (a few seconds down): that order worked first
time**, from PowerShell outside the sandbox: download and check the SHA-256,
rename the running `Issun.exe` to `Issun.exe.b43.bak`, copy the new one in and
check its hash, `Stop-Process -Force`, start it with `--background`, then ask
`/version`, which answered `issun 0.1.0 (46)`.

**11 Nov 2026, build 46 to 48 (4.1 s down), at the owner's "Yes, install it":
the same order, as one PowerShell script**, with build 46 kept as
`Issun.exe.b46.bak`. If the copy or its hash
check failed, the script was to delete the copy and rename the backup back
before stopping anything. `/version` answered `issun 0.1.0 (48)`; Discord
reconnected and the phone's next push (Ammy 1.0 (167)) landed within 2 s of
the start.

**Two things the handover taught, for anyone repeating it:**

- **Do anything that touches live state from PowerShell, not from Claude Code's Bash
  tool.** That shell runs sandboxed. An `.env` import run from it printed success,
  but its writes to `%APPDATA%` and `%LOCALAPPDATA%` never landed. A log watcher run
  from it saw a stale copy of `issun.log` and stayed silent while Issun was working.
- **The order is: Issun open and imported first, then stop the relay.** The first
  attempt stopped the relay while Issun still had its own freshly generated key.
  The phone's pushes got 401 for about two minutes, Ammy stopped on "Key
  Rejected", and it had to be relaunched by hand.
