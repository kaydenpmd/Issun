# Context for Claude Code

**This file holds the current state and the rules. The story of how they came
about is in `HISTORY.md`**: every measurement, test, install, correction and
rejected approach up to 15 Nov 2026, under the section names used here. Read
the matching section there before redoing anything this file calls settled,
and before reopening a decision.

**Keeping it that way.** When something changes, edit this file to say what is
true now, in as few lines as it takes, and write the story (what was tried,
measured, seen or rejected, with dates and evidence) at the end of
`HISTORY.md` under a dated heading. Rules and current facts here; narrative
there. Each install of a new build is a line in `HISTORY.md`, not here. Code
comments that cite "CLAUDE.md" and a section name mean `HISTORY.md` when the
section is only a pointer here.

**Check `git log`, `gh release list -R kaydenpmd/Issun` and `/version`, not
this file, for what has shipped and what is installed.**

## What it is

**Issun** receives now-playing JSON on `127.0.0.1:8787` behind Tailscale Funnel
and sets Discord Rich Presence through the Discord desktop app's local IPC
pipe. Its first source is **Ammy** (iOS, `C:\Users\links\Repos\Ammy`), which
reads Apple Music. It replaced relay.py on 22 Sept 2026 and keeps every
behaviour relay.py had, including the fixes that cost days to find. The name
is from Ōkami: Issun does the talking Amaterasu, "Ammy", can't.

```
iPhone (Ammy) ──https──▶ <machine>.<tailnet>.ts.net (Tailscale Funnel) ──▶ Issun, 127.0.0.1:8787 ──named pipe──▶ Discord desktop
```

**There is no rollback** (the owner, 25 Sept 2026: "kill relay.py it's
obsolete"). The last relay.py is
`git -C C:\Users\links\Repos\Ammy show 3b7875c:bridge/relay.py`. Tailscale
Funnel is the only way in; Issun's address is always Tailscale's (the Public
address override was removed on 13 Nov 2026, and a `publicBase` in
settings.json is ignored). The `cloudflared` service is still the owner's to
remove (Ammy's CLAUDE.md).

**Why the shape stays.** Discord has no Rich Presence SDK on iOS, and setting
presence from a phone means a user-token Gateway connection: self-botting,
against ToS. The phone never talks to Discord. **Any change that moves Discord
communication onto the phone is wrong.** The accepted cost: presence needs the
PC awake with Discord desktop open.

## Where things stand

As of 16 Nov 2026: `main` is pushed through build 178 (a238ae6, the
workflow's actions off Node 20, on top of 2e2b040, a mistyped setting no
longer costs the key; build 177's release failed, HISTORY.md), and build 178
is installed on the owner's PC (17 Nov 2026).
The window still has `[text]`
placeholders for the owner to write (the line under setup's progress bar
while Tailscale installs, the new-key errors, the Tailscale page's cards, and
`PresenceWorker.InvalidIdDetail`). The wording page
(claude.ai/artifact/BKtxXmKxc3ot9CSi7D2xRJ, its `texts` collection) has a
field for each of the 11, with build 175's pictures, and no Discord page,
since 16 Nov 2026; it waits on the owner. "New key" is the owner's word, not
a placeholder.

**Not yet seen on the owner's PC or in a real window:**
- setup opening on the Tailscale page on the owner's PC (the sandbox's first
  run has); the sandbox's `address`, `qrrules` and `tailscale` steps, which
  now skip one page fewer, and its `shots` step's new names, not run since
- the setup pages' slide; the QR blur animating on real hardware
- the centred column in a maximized window; a real non-square cover; the
  one-line title; covers at 150% (`--screenshot` above 1x can't judge them)
- the update over an old build 48, and scanning the white-on-black QR code
  with Ammy
- explicitness by lookup: Apple's public Lookup returned only clean editions
  on 13 Nov 2026, so `trackExplicitness` may never say explicit
- the live test's skip, pause and days of `uptime.log` (items 5, 6, 8 below)
- build 175's look (status rows with no dots; setup's not-installed card,
  "Tailscale isn't installed" with Install) outside `--screenshot` renders

## What Issun is, in anything a user reads

**A Discord receiver for any source, never "Ammy's receiver".** The owner,
23 Sept 2026: "remove all references to ammy STOP. but don't remove references
to discord. this is meant for discord." The window, tray and notices say
**source**, not Ammy or phone. The rule runs one way: Issun names Discord,
Ammy never does, and **Issun's framing must not leak into Ammy's** listing,
landing page, permission strings or UI.

- The README is only the name; the Start menu entry and the Apps entry have no
  description or Publisher. Public text and all interface text are the
  owner's to write (global CLAUDE.md, the wording page).
- Three things carry Ammy's name on purpose and stay: the `X-Ammy-Relay`
  header (Ammy checks for it), log lines kept word for word from relay.py
  (`[init] phone reports Ammy …`), and this file and code comments.
- **Locked by the owner (23 Sept 2026)**, enforced in `IssunHost.Locked()` on
  load and Apply: the member list shows the artist (status display type 1),
  the card never gets an album line, cover matching uses the 0.35 floor. The
  `Settings` fields stay for the parity tests. Don't put them back in the
  window without asking.
- No progress bar in the window (removed 24 Sept 2026); Discord's card keeps
  its own. Don't bring it back without asking.
- **The Now playing card is Windows' Quick Settings media card** without its
  app-name row and buttons: title (Body Strong) over artist (Caption), one
  line each with "…" and the full text on hover, cover on the right at 58 epx,
  text centred against it, 24 of padding (design library,
  `microsoft\windows11-media-controls`). "i just want title and artist": no
  source line, no Discord line. "Not playing" stands alone. No cover: an empty
  square in `ControlAltFillColorQuarternary`, no note, no hairline.
- Covers are decoded to exactly the pixels drawn, at the window's current
  scale, redone when it changes (`MainViewModel.CoverScale`), shrunk with
  WIC's Fant as Quick Settings does. **A non-square cover is fitted, never
  cropped** ("i will not publish something that crops album art"). Covers get
  a one-physical-pixel hairline in `ControlStrokeColorDefault`.
- **Never assume the display's scale**: anything sized in pixels reads the
  window's current scale (`XamlRoot.Changed`); a log line's dpi says which
  display it was.
- **The explicit badge** is drawn: a `Path` built from SF Pro's 🅴 proportions
  at the title's cap height (the owner chose "Draw Apple's design"; numbers in
  `MainView.xaml`), screen-reader name "Explicit". `TrackText.Explicit` takes
  the push's JSON `true`/`false` first (`false` beats the catalog), else the
  store-ID lookup's `trackExplicitness`, never fuzzy search. Discord has no
  field for it; it's the window's and `GET /now-playing`'s only.

## The Discord application and "Listening to"

- **One application ID, built in** (`DiscordApplication.Id` in
  `src/Issun.Core/Discord/DiscordApplication.cs`, the owner's own, named
  Issun). No field anywhere to change it ("just have it in wherever it's
  supposed to be in the code"); an old `discordClientId` in settings.json is
  ignored and dropped. The ID isn't secret.
- **The source decides what follows "Listening to".** A push's
  `activity_name` (trimmed, clipped to 128) goes out as the activity's `name`.
  Ammy sends "Apple Music". With none, Discord shows the application's name.
  No UI for it. If Discord refuses an activity, the fallback sheds `name` with
  the other optional fields.
- **Discord caches the application's name.** After a rename, check
  `discord.com/api/v10/applications/<id>/rpc`, then restart Discord, before
  suspecting Issun.
- The application's icon is the 1024 px blurple square from
  `tools/make_icon.py <out> --square`. The black-and-white one is the old icon.
- A handshake close 4000 now means the application really is gone; its status
  text (`PresenceWorker.InvalidIdDetail`) is a `[text]` placeholder.

## The icon

Two waves arriving at a speech bubble, the other half of Ammy's icon, drawn as
Discord draws its own on Windows since 14 Nov 2026 ("issun is more of a
discord-adjacent tool"). `tools/make_icon.py` makes `Assets\issun.ico` (a flat
blurple disc, #5662F6, the picture white, no gradient or shadow), `Assets\tray.ico`
(the white picture alone with Discord's soft halo, carried in the .exe as the
managed resource `Issun.tray.ico`) and, with `--square`, the Discord
application's icon. Measurements in the design library, `discord/windows-icons/`.
**After an icon change, the shell must be told**, or the taskbar keeps the
cached picture: `ShellIcons.Refresh` does it after an install or a new build
at the installed path.

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
src/Issun/                      WinUI 3 window and tray; talks only to IIssunHost
tests/Issun.Core.Tests/         xUnit, one folder per module, namespace Issun.Core.Tests.<Folder>
tools/                          make_icon.py, install-local.ps1, sandbox/
HISTORY.md                      How everything got this way
```

| Module | Types | Was, in relay.py |
|---|---|---|
| state (`State/`) | `PhoneState`, `PhoneDiagnostics`, `UptimeLog`, `CheckinProcessor` | `State`, `record_phone_version/diag`, `_diag_summary`, `_log_line`, `note_checkin`, `note_silence`, `_gap_verdict`, `print_summary`, the middle of `do_POST` |
| activity | `ActivityBuilder`, `Playhead`, `DiscordText` | `build_payload` minus artwork, `Playhead`, `pad_for_discord`, `_materially_different` |
| artwork | `ArtworkResolver` | the store-ID lookup, `_album_url`, uploaded JPEGs, cache pruning, `_normalize` / `_similarity` / `_score` (an exact port of `difflib.SequenceMatcher`), fuzzy search |
| discord | `DiscordIpcClient`, `PresenceWorker` | pypresence itself, `rpc_worker`, `push_with_fallback` |
| server | `RelayServer` (Kestrel) | `Handler`, `QuietHTTPServer`, `public_state` |
| platform | `SettingsStore`, `KeyGenerator`, `Autostart`, `TailscaleCli`, `SelfInstall`, `StartMenuShortcut` | `.env`, `install-task.ps1`, the hand-typed `tailscale funnel` command |
| host | `IssunHost` | `main()`: builds every module, wires them, gives the window one object |
| ui | `App`, `MainWindow`, `SetupWindow`, the tray, `DemoHost`, `HostFactory` | a PowerShell window and `relay.log` |

## Rules every module follows

- **Thread-safe.** Kestrel's threads, the presence worker's loop and the UI
  thread all touch state.
- **Invariant culture** for anything parsed, logged or put on the wire.
- **Log lines keep relay.py's exact text**, em dashes included. The owner reads
  them and history must stay greppable. Don't tidy the wording.
- **Every failure path logs.** Silent failure is this project's recurring bug.
- **Time is injected**: `IClock` returns unix seconds as a double. Tests use
  `ManualClock`.
- **Parity is checked against Python and frozen into C#** as literals. To
  generate more, work in a scratch folder (importing relay.py creates
  `./art_cache`); both `generate_parity.py` scripts extract relay.py from
  commit `3b7875c`; stub `artwork_by_store_id`, `store_uploaded_artwork`,
  `existing_uploaded_artwork`, `artwork_url` and `catalog_links` so nothing
  touches the network. pypresence 4.6.2 is installed for Python 3.14.
- **`Contracts.cs` is shared by every module.** Change it deliberately and
  update every implementation in the same commit.

## Build, test, run

```
dotnet build Issun.sln
dotnet test
dotnet run --project src/Issun -- --demo
```

.NET 9 SDK, Windows only, x64 only (`win-x64`). `dotnet build Issun.sln`
puts the app in `src/Issun/bin/Debug/net9.0-windows10.0.22621.0/win-x64/`
(the solution builds it as Any CPU); a build of the project itself, as
`dotnet run --project` does, goes to `bin/x64/Debug/...` instead. **Render
from the folder you just built**: the other one holds an older build.

- `--background`: start in the tray (the autostart entry passes it).
- `--demo`: `DemoHost`, a fake that never writes into the real data folder. On
  the owner's PC its songs come from `%LOCALAPPDATA%\Issun demo songs\`
  (`songs.json` and `covers\`), the owner's listening, which stays off this
  public repository: 227 songs since 16 Nov 2026, three picked at random per
  run; `scripts\expand.py` there adds more from the logs.
  `ISSUN_DEMO_SONGS` points it at another folder.
- `--screenshot <png>` (with `--demo`): renders the window offscreen and exits.
  **This is how a session looks at the UI**: render, then Read the PNG. Never
  put a window on the owner's desktop. `--setup-page 0..2`, `--expanded` (the
  QR code), `--demo-tailscale 0..3`, `--press-next`, `--theme dark`. Each PNG
  gets a `.text.json` of every visible text and its position, for wording
  pages. Refuses from a disconnected Remote Desktop session; use the
  sandbox's `shots` step then. States that need a click (installing, Funnel
  failing, Next refused) take `local/screenshot-press/` (private, never
  committed; its NOTES.md says how), applied in a scratch copy only.
- `--uninstall`: what Settings › Apps runs. Removes the Run entry, Start menu
  entry, Apps entry and install folder; settings, key, logs and history stay.
  Asks nothing (Windows asks first).
- `ISSUN_HOME`: puts the config and data folders under one directory.
- No `--version` or `--summary` (a WinExe has no console): use `GET /version`,
  the .exe's properties, or the window's Diagnostics section.
- A single-file .exe unpacks about 180 MB into `%TEMP%\.net\Issun\<hash>`;
  `StaleUnpacks` deletes earlier builds' folders on start.

**Testing on a clean PC: Windows Sandbox.** `pwsh tools/sandbox/run.ps1` (from
PowerShell, not the Bash tool) builds a release-numbered .exe and runs
`tools/sandbox/test.ps1` inside the sandbox: first run, setup page by page,
the self-install's entries, a second launch, the tray, an update over a
running copy, uninstall. Every check prints `ok` or `FAIL`; output in
`%LOCALAPPDATA%\IssunSandbox\<time>\out`. `-Keep`, `-Steps first,update`;
`address` and `qrrules` each need a fresh sandbox; `shots` renders every
screen. About two minutes. **The sandbox is the only place a self-installing
build may run on this PC.** Computer use can't see it, so the test drives
itself with UI Automation. It can't test Discord, Tailscale or the phone.

### Testing without touching the live setup

The owner's receiver is live on this machine. Hard limits:

- **Never bind port 8787.** Tests pick free ports.
- **Never connect to `\\.\pipe\discord-ipc-N`.** Tests serve a fake Discord
  on their own pipe prefix.
- **Autostart and self-install tests** use a throwaway
  `HKCU\Software\IssunTests\<guid>` and a temp folder, never the real Run,
  Uninstall key or `%LOCALAPPDATA%\Programs\Issun`.
- **Tailscale: read-only commands only** (`tailscale status --json`,
  `tailscale funnel status --json`, `tailscale debug prefs`). Funnel and Run
  unattended change only when the owner clicks; tests inject a process runner.
- **No secrets anywhere.** Never read `C:\Users\links\Python Scripts\.env`. No
  key or tailnet name in code, tests, fixtures, logs or commits (fixtures say
  `example-tailnet`). The built-in Discord ID is the only ID in the code.
  Check a secret by its `.Length`.
- **Never enable or start the `Ammy Relay` task**; it would put a second
  receiver on 8787.
- **Don't change Tailscale or `cloudflared` state** unless the owner asks.
- **Anything touching live state runs from PowerShell, not the Bash tool**,
  whose writes to `%APPDATA%`/`%LOCALAPPDATA%` don't land and whose view of
  `issun.log` is stale.

## Telling builds apart

`<VersionPrefix>` in `Directory.Build.props` is the only number a human sets.
CI passes `IssunBuild` (`git rev-list --count HEAD`, so the checkout uses
`fetch-depth: 0` and refuses a shallow clone) and `IssunCommit` (short SHA). A
local build reports `dev` / `local`.

| Where | Looks like |
|---|---|
| `IssunInfo.Display`, the window | `0.1.0 (12)` |
| `IssunInfo.Wire`: `GET /version` and the `X-Ammy-Relay` header | `issun 0.1.0 (12)` |
| Uptime-log stamp | `[issun 0.1.0 (12) / app 1.0 (80)]` |
| Release asset | `Issun-0.1.0-b12-a1b2c3d.exe`, tag `v0.1.0-b12` |
| Explorer → Properties | File version `0.1.0.12` |

Ammy checks only that `X-Ammy-Relay` is present. Never write the current
version in this file; ask `/version`.

## Where state lives

Never beside the .exe.

| What | Where | Notes |
|---|---|---|
| Settings | `%APPDATA%\Issun\settings.json` | Holds the key in plain text. Written atomically. |
| Log | `%LOCALAPPDATA%\Issun\issun.log` | Moves to `.old` past 5 MB. |
| Uptime log | `%LOCALAPPDATA%\Issun\uptime.log` | relay.py's history merged in ahead of Issun's. |
| Uploaded covers | `%LOCALAPPDATA%\Issun\art_cache\` | 60 newest kept. |
| Autostart | `HKCU\...\CurrentVersion\Run`, value `Issun` | `"<exe>" --background`; honours Task Manager's Startup switch. |
| The program | `%LOCALAPPDATA%\Programs\Issun\Issun.exe` | Self-installed. |
| Apps entry | `HKCU\...\CurrentVersion\Uninstall\Issun` | No Publisher. |
| Window state | `%LOCALAPPDATA%\Issun\window.json` | Whether the tray hint and setup have shown. |

- **Settings**: a missing file is a first run (fresh key, setup window opens).
  An empty key gets one generated. **A value of the wrong type costs only
  itself** (a port as `"87o7"`, `"yes"` for true): its default is used, a
  `[config] WARNING` names it, the key and the rest load, and the file is
  left as written. Nulls are defaults. A key written as a number is used as
  written; a key that's `true`, an object or a list gets a new key, keeping
  the rest, with the file moved aside first. A file that isn't a JSON object
  is moved aside to `settings.json.bad-<timestamp>`, never overwritten, and
  the log says loudly the source needs the new key.
- **The key**: 32 characters of `[A-Za-z0-9]`. Logs give its length, never its
  value. An empty key refuses every authenticated request. The comparison is
  constant-time.
- **Autostart** is the HKCU Run key, not a Scheduled Task and **never a
  Windows service**: Discord's pipe belongs to the interactive session.
  `RepairIfMoved` repoints the entry when Issun runs from a new path. **Local
  builds (build 0) never point it at themselves**, by `Enable` or
  `RepairIfMoved`, as they never install or touch the Start menu entry.
- **Tailscale's state is Tailscale's.** Issun reads the MagicDNS name (which is
  `PublicBase`), Funnel status and Run unattended; it changes nothing unless the
  owner clicks **Turn on Funnel** or **Turn on**. While Tailscale is installed
  but not connected it re-reads every 15 s.
- **Issun installs itself** (`SelfInstall`): a release build opened from
  anywhere else asks a running Issun to quit (`Local\Issun-<SID>-quit`),
  renames the installed .exe to `Issun.exe.previous`, copies itself in,
  registers the Apps entry, starts the installed copy and exits. Local builds
  never install.

## The setup window

Opens by itself on a first run, once, and from **Set up again** after.
Microsoft's wizard structure (design library,
`microsoft/windows-setup-wizards/`): no welcome page, a Title-style main
instruction per page, Back, Next and Cancel (Cancel on every page; Next is
Finish on the last), and **Skip on every page, labelled Skip**, which moves
on without the page's check. Pages slide with WinUI's horizontal slide in a
`Frame` (Next and Skip from the right, Back from the left). Every step logs
as `[setup]`. **No Discord page**: the owner had it removed on 16 Nov 2026
("remove the first page (discord) from setup"), with Open Discord and Next's
connection check; don't bring it back without asking.

1. **Tailscale**: install (`winget install --id Tailscale.Tailscale --exact`,
   by id, else the download page), Turn on Funnel, Run unattended
   (`tailscale set --unattended`). Not installed, the card is the owner's
   title, "Tailscale isn't installed", with an accent **Install** on the right
   and nothing else: no command, no Check again (16 Nov 2026). Opening setup asks Tailscale afresh, as
   arriving from another page does. Next refreshes and stays with an error
   unless Tailscale has this PC's address and Funnel is on for Issun's port;
   while it waits, Next shows a 16 px ProgressRing and is disabled. The other
   cards' words are still waiting on the owner.
2. **Connect your source**: the QR code, the endpoint as an editable field
   (filled from Tailscale; sets only what the QR code carries, nothing saves
   it; a reload button redraws the code), the key as ● dots with Show, Copy
   and **New key** (no confirmation), and the source's live status:
   ProgressRing and "Waiting..." until a source checks in, then a success
   mark and "Connected". **The main window's Connect your source is the same
   control** (`Views/PairingCards` on `PairingViewModel`): a change to one is
   a change to both (the owner: "favor the setup screen").
3. **Start with Windows**, ticked by default on a first run, Yes and No radio
   buttons, Next labelled "Finish".

**The QR code** is plain JSON `{"endpoint": "...", "key": "..."}`
(`PairingCode`), black on white in light theme and white on black in dark,
error correction M. Either value may be left out. Not a link, so it names no
app. **Blurred until Show, by MetaMask's numbers**: 8 while hidden; 4 at 30%
opacity while out of date. The blur is animated through Win2D's
`GaussianBlurEffect` (never a cross-fade) over `ControlNormalAnimationDuration`
on Point to Point (0.55, 0.55, 0, 1); the dim fades linearly at
`ControlFasterAnimationDuration`; nothing moves when Windows' animation
effects are off. A transparent Image of the sharp code sits beneath for UI
Automation. Research in the design library, `cross-platform/secret-blur-values/`.

Both windows' columns stop at 1000 epx (Windows Settings' width), centred
when wider.

## WinUI 3 specifics

Moved from WPF on 12 Nov 2026 (Windows App SDK 2.6.5, referenced whole for the
Community Toolkit's SettingsCard). What has no direct WinUI equivalent:

- **Tray**: `Tray/TrayIcon.cs`, Shell_NotifyIcon on a hidden untitled window,
  a Win32 popup menu that follows dark mode; also receives
  `WM_QUERYENDSESSION`. **Closing the main window hides Issun to the tray; it
  doesn't quit.**
- **Message boxes** are `MessageBoxW` (`DesktopShell.Message`, `AskYesNo`); the
  file dialog is `GetOpenFileNameW`.
- **Every binding is x:Bind**; visibility comes from `Views/Converters.cs`
  (class `Bind`). A `Command` whose CanExecute depends on state names its view
  model and is requeried when that changes (no CommandManager).
- **A `v:Bind.Present(...)` binding carries `FallbackValue=Collapsed`**: x:Bind
  skips a function whose argument turns null, so without it a cleared message
  stays on screen, empty.
- **Covers** are fetched as bytes and decoded on the UI thread (`Views/Images.cs`).
- **Theme**: `--theme` sets `Application.RequestedTheme` in App's
  constructor; `AppTheme.IsDark` asks Windows directly for the QR colours.
- **Settings rows** are the toolkit's `SettingsCard` and `SettingsExpander`;
  rows that can't be are Borders in the `Card` style using SettingsCard's
  resources. SettingsCard has no Invoke pattern (toolkit issue #391).
  Start with Windows in the main window waits for Apply, like the settings
  above it; the tray's item and setup's Yes and No apply at once.
- **No coloured status dots**, anywhere: the owner had them removed on
  16 Nov 2026 ("remove the colored dots gimmick from everywhere in the app").
  A status row is its name and its words (`StatusLine` is the words alone).
  The success mark beside Connect your source's "Connected" is an icon, not
  a dot, and stays ("keep the checkmark for now").
- **The .pri must be named `resources.pri`** (`ProjectPriFileName` in
  `Issun.csproj`), or a renamed copy loads no XAML.
- **Single file** needs `EnableMsixTooling` (no MSIX is made). Trimming isn't
  an option.

## relay.py's gotchas, preserved, and where they live now

Ammy's CLAUDE.md points here for these. Full stories in `HISTORY.md`.

- **The playhead anchor is paired with the push's arrival time, never the
  clock.** `IActivityBuilder.Build(track, observedAt, ...)` takes `observedAt`
  from `IPhoneState.Get()`'s `UpdatedAt`. **Never pass it the clock** (the
  rubberbanding bar, relay 1.2.1). A regression test pins it. A constant
  repeating drift in `[playhead] re-anchored` is a clock, not a measurement.
  The `Playhead` forward-correction rule has never fired; it is insurance.
- **A rejected payload is not a dead socket.** `DiscordRejectedException` (the
  connection is fine: retry once with the optional fields shed, then record
  the intended payload as sent) and `DiscordConnectionLostException` (log,
  dispose, wait `LostDelay`, reconnect) are **two catch clauses; don't merge
  them.** Log the refusal itself. Optional fields shed: `DetailsUrl`,
  `StateUrl`, `LargeUrl`, `LargeText`, `StatusDisplayType`, `name`.
  `IPresenceWorker.Current` is what Discord accepted.
- **Two characters minimum** for `details` and `state`: `DiscordText` pads with
  U+2060, counts code points, substitutes `Unknown` for blank, and logs the
  padding once per distinct value.
- Issun writes the IPC wire format itself (modelled on pypresence 4.6.2's
  `payloads.py`): `type` 2, `status_display_type` on the wire as 0/1/2.
- **`X-Ammy-Relay` is on every response**, errors included: Ammy tells Issun's
  404 from Funnel's by it.
- **`/health` returns exactly `ok`.**
- **A GET never performs a lookup.** `GET /now-playing` is a projection, never
  the raw push; artwork, links and explicitness come only from the caches.
- **`/art/` is unauthenticated** (Discord's CDN can't send a header).
  Filenames are the first 20 hex characters of `sha256(TrackText.Key)`;
  `UploadedArtPath` accepts only a bare `.jpg` directly in `art_cache`.
  Uploaded covers use `https://<tailscale dns name>`.
- **127.0.0.1 only**, through Kestrel, not `HttpListener` (http.sys matches the
  Host header, and a wildcard prefix needs admin).
- **Links only ever come from the exact store-ID lookup.**
- **The gap-log wording is data**: "phone silent, relay up throughout", "relay
  was down for ... of it", "— the path failed, not the app", "relay" included.
  `Summarize()` reads "gap HH:MM:SS", "phone silent", "stopped checking in"
  and both kinds of start, so new words go after those, never in their place.
  **Don't split the log per version.**
- **Diag values follow Python's types** (`isinstance(x, int)`; a bool isn't an
  int) and flag changes render with Python `repr`.
- **`seq` is milliseconds since epoch.** An absent `seq` always applies, as do
  `RELAY_SECRET`/`X-Relay-Secret`, still read.
- **Don't transcribe config from screenshots.**
- Timing (`Timing.cs`): gap threshold 90 s, idle timeout 90 s, minimum push gap
  3 s, seek tolerance 10 s, settle window 6 s. `ArtMinScore` 0.35.

## Where Issun deliberately differs from relay.py

If this list and the code disagree, the code wins; fix this file.

- **Check-ins are atomic**: `CheckinProcessor.Accept` holds one lock over the
  whole check-in, so a near-simultaneous second push can't log a false gap.
- **A relaunch supersedes the old `seq`**: uptime fell further than `seq` did,
  by more than `SameProcessSlack` (5 s). Sessions are superseded, never
  stacked.
- **A phone clock jumping back** (`seq` more than 60 s older) resets and logs,
  rather than dropping every push.
- **A check-in is any authorised push**, applied or dropped (`LastCheckinAt`);
  `UpdatedAt` moves only when a push is applied.
- **"Discord not reachable" is throttled**: logged on a change of reason and at
  most every 10 minutes.
- **Artwork resolves while Discord is disconnected**; concurrent resolves for
  one track share a request.
- **Nothing exits over missing config**; settings apply live; a taken port is
  a status (`PortInUseException`), not a traceback.
- Since build 48, in the uptime log:
  - **a process older than the gap plus 20 s stayed up** (`UptimeLog.InFlightSlack`);
  - **no failed pushes through a gap is a suspension**: "— suspended, no pushes
    failed";
  - **Ammy's Stop** (`playing: false` with no diag, from a source that has sent
    diag before) reads "stopped on the phone";
  - **a death names when the dead process last ran** ("— it died, last ran
    HH:MM:SS engine=yes|no"), and issun.log gets the previous run's journal
    once per launch (`[keepalive] previous run ...`, `[keepalive] journal:
    ...`). Build 167's other diag fields print beside the counts they explain.
- **A restart that brings a different app build reads "— updated, <old> ->
  <new>"**, not "it died" (15 Nov 2026): an install ends the process it
  replaces. Only between two builds the source named; a stop before the gap
  still wins. It claims no death either way: a long gap ending in a new build
  may hold one before the install, and a same-build reinstall (a SideStore
  refresh) still reads "it died".
- **A restart on a new boot reads "— the phone restarted, devup <old>s ->
  <new>s"** (16 Nov 2026): a phone restart ends every app on it, and Ammy's
  `device_uptime_s` (`systemUptime`) goes back only then. The new reading must
  be below the old and within the gap plus `InFlightSlack`. A stop or an
  update still wins; a snapshot without the field keeps "it died".

## CI and releases

`.github/workflows/build.yml`, one job on `windows-latest`, bash throughout.
**Every push to `main` that changes more than Markdown is a release**: tests,
the single-file .exe, a GitHub Release whose notes are the commit message
minus trailers, plus the full SHA, the .exe's SHA-256 and SmartScreen
instructions. So **write the commit message for whoever reads the release
notes**, and never push text of your own that the owner hasn't confirmed.
`workflow_dispatch` from another branch builds without releasing. 0.x releases
are pre-releases; bumping `<VersionPrefix>` to 1.0.0 ends that. Read the run
after a push (`gh run list -R kaydenpmd/Issun`). The repo is public, which
keeps Windows runner minutes free. **Don't push a change to
`.github/workflows/` while an earlier push's run is still releasing**: on
16 Nov 2026 build 177's release step got a 403 when such a change landed
mid-run, probably because the token may not tag a commit whose workflow
files differ from main's (HISTORY.md, same date).

## Updating the installed copy

**Get the owner's yes first**: it stops the live receiver for about 6–7 s.
Run `tools/install-local.ps1` (CI's own publish command, tests first) from
PowerShell, not the Bash tool. What it does, and what to do by hand if it
can't:

1. Publish (or `gh release download` and check the SHA-256 against the
   release notes).
2. **Rename** the running `%LOCALAPPDATA%\Programs\Issun\Issun.exe` to
   `Issun.exe.b<old build>.bak` (Windows allows renaming a running .exe, not
   overwriting it). That's the rollback too.
3. Copy the new .exe in and check its hash.
4. Stop the old process **by the process ID noted before the rename**
   (`Get-Process` still reports the old path afterwards), then start the new
   one with `--background`.
5. Ask `/version`, and check `[rpc] connected to Discord` and the phone's next
   check-in in the log.

If the script stops at "tests did not pass", run `dotnet test` at normal
verbosity: a flaky test has struck twice there, name unknown, and the script
reads only "Passed!". In CI on 15 Nov 2026,
`RelayServerTests.A_client_that_half_closes_mid_body_is_logged_as_normal`
timed out waiting for its log line and a re-run passed; whether it's the
same test isn't known. Record each install as a line at the end of
`HISTORY.md`. Deleting old `.bak` files is the owner's call.

## Distribution: the Microsoft Store (decided 12 Nov 2026)

"for now, only store, and winget piggybacking off of store": an MSIX package
the Store signs and updates, found by winget through `msstore`. Not yet
packaged (the `winui3-spike` report, question 6, shows how). The unsigned .exe
on GitHub goes on until then. The Store needs a privacy policy and Discord and
Tailscale named early in the description; the listing, description and policy
are the owner's to write. MSIX replaces `SelfInstall`, the Run key (a
StartupTask) and the Apps entry, and redirects AppData writes, which matters
for carrying settings across. Research details in `HISTORY.md`.

## Queued work

Take the top item unless told otherwise.

1. **The owner's words for the `[text]` placeholders and the Tailscale page's
   cards** (Where things stand, above). Then push, only once they say it can
   go out.
2. **Review findings still open**, from the 23 Sept review: check whether any
   `UptimeLog` verdict problems remain beyond the three build 48 fixed; CI
   builds with the .NET 10 SDK, which needs pinning.
3. **Later, only when asked: several of the owner's PCs backing each other
   up** (14 Nov 2026, "future feature"). The open questions are in
   `HISTORY.md`, Queued work item 3. Design-heavy; its own session.

**The live test**, still partway: 1–4 and 7 passed (builds 23 and 34); still
to check: 5, a skip updates presence; 6, a pause clears it; 8, a few days of
`uptime.log` continuing the relay-era history. Record results with the build
number in `HISTORY.md`.

## Working with the owner

Limited coding experience: comfortable running commands and reading output,
not writing code. Concise, concrete instructions; when the owner has to run
something, **one command at a time**.

The owner wants Claude to do the work: "the more you can do the better (to an
extent lol)". Run the commands, commit, verify with a real check, and bundle
the obvious adjacent work. **Confirm first** anything outside the repo or
hard to undo: deleting, spending money, or touching the live setup (Tailscale
Funnel, port 8787, the installed Issun, `cloudflared`, the `Ammy Relay` task,
`C:\Users\links\Python Scripts`). The repo is `kaydenpmd/Issun`, public;
**git is Claude's to run**, and pushing to `main` is routine, but a push is a
release.

**Verify, don't assert.** Read the file, the log, the CI run; probe the live
URL. When the owner says something works, believe them and go look. When
restating what the owner said, use their words. **The owner's live test is
the instrument** for anything involving the phone, Discord or the tunnel.
