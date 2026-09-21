# Issun

Issun is the Windows receiver for [Ammy](https://github.com/kaydenpmd/Ammy).
Ammy reads what Apple Music is playing on your iPhone and sends it here; Issun
shows it on your Discord profile as Rich Presence: "Listening to Apple Music",
with the song, the artist, the cover and a progress bar. The name comes from
Ōkami, where Issun is the small companion who does the talking for Amaterasu
(Ammy), and that is his job here too.

```
iPhone (Ammy) ──https──▶ Tailscale Funnel ──▶ Issun, 127.0.0.1:8787 ──▶ Discord desktop app
```

The phone never talks to Discord. Discord has no Rich Presence for iOS apps,
and setting presence from a phone directly would mean using your account token,
which is against Discord's terms. Issun sets presence through the Discord
desktop app's local connection, which is the supported way. The cost is that
your PC has to be awake with Discord open.

Issun replaces `relay.py`, the Python relay from the Ammy repo. Same routes,
same port, same key, same uptime log, but it's one `.exe` with a window, a tray
icon and a Start with Windows switch. No Python, `.env` file or Scheduled Task.

## Requirements

- Windows 10 or 11, 64-bit.
- The **Discord desktop app**, open and signed in, on the same PC and the same
  Windows account. Discord in a browser can't show presence from other apps.
- A free **Tailscale** account, with Funnel. This gives the phone an HTTPS
  address to reach your PC. No domain, no port forwarding.
- For now, a **Discord application** of your own. It's free and takes a couple
  of minutes (step 3). Its name is what appears after "Listening to".
- **Ammy** on your iPhone.

## Setup

1. **Download** `Issun-<version>-b<build>-<commit>.exe` from this repo's
   Releases page. Move it to a folder you'll keep, not Downloads. You can
   rename it to `Issun.exe`.

2. **Run it.** Issun isn't code-signed, so Windows shows "Windows protected your
   PC" the first time. Click **More info**, then **Run anyway**. Each release's
   notes list the file's SHA-256 if you want to check your copy:
   `Get-FileHash .\Issun.exe` in PowerShell.

   On first run Issun generates its key and opens the window with a **Pair
   Ammy** banner.

3. **Create the Discord application.** Go to
   `discord.com/developers/applications`, click **New Application** and name it
   `Apple Music`. Whatever you name it is the text after "Listening to". Copy the
   **Application ID** and paste it into Issun under **Settings → Discord
   application ID**. Paste it rather than retyping it: one wrong digit gets
   "Client ID is Invalid", which looks like a deleted application but isn't one.

4. **Set up Tailscale.** Install it from `tailscale.com/download` and sign in.
   In Issun's **Tailscale** row, click **Turn on Funnel**. That runs:

   ```
   tailscale funnel --bg --https=443 localhost:8787
   ```

   You can also run that command yourself. If the output includes a
   `login.tailscale.com` link, open it to allow Funnel on your tailnet, then
   click the button again. Your tailnet needs MagicDNS, HTTPS certificates
   and the `funnel` attribute. Tailscale links you to whichever is missing.

   Then **pin the machine name.** Your address is
   `https://<machine>.<tailnet>.ts.net`, and by default the machine half follows
   the PC's hostname. If you rename the PC, the address moves and Ammy loses
   Issun. In the Tailscale admin console, open this machine and turn off
   **Auto-generate from OS hostname**.

5. **Pair Ammy.** Issun's **Pair Ammy** section shows an **Endpoint** (like
   `https://<machine>.<tailnet>.ts.net/now-playing`) and a **Key**. Copy each
   into the fields with the same names in Ammy and tap **Start**. The Endpoint
   stays blank until Tailscale is set up.

6. **Turn on Start with Windows** in Settings. Issun then starts in the tray
   when you sign in. Closing the window hides it to the tray. To exit, choose
   **Quit** from the tray icon's menu.

When it's working, the **Phone** row says the phone is checking in, the
**Discord** row says "Connected as" your name, and your Discord profile shows
"Listening to Apple Music".

## Where things live

| What | Where |
|---|---|
| Settings, including the key | `%APPDATA%\Issun\settings.json` |
| Log (what `relay.log` was) | `%LOCALAPPDATA%\Issun\issun.log`, moved to `issun.log.old` at 5 MB |
| Phone check-in history | `%LOCALAPPDATA%\Issun\ammy-uptime.log` |
| Covers uploaded by the phone | `%LOCALAPPDATA%\Issun\art_cache\` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Issun` |

Nothing is written next to the `.exe`. **Diagnostics → Open log folder** opens
`%LOCALAPPDATA%\Issun`. `settings.json` contains your key in plain text, so don't
share it. Setting the `ISSUN_HOME` environment variable puts everything in that
one folder instead.

## Migrating from relay.py

1. **Stop the relay.** It holds port 8787, and only one program can. In
   PowerShell, one command at a time:

   ```powershell
   Stop-ScheduledTask -TaskName "Ammy Relay"
   ```
   ```powershell
   Disable-ScheduledTask -TaskName "Ammy Relay"
   ```

   Disable, don't delete, so you can switch back.

2. **Start Issun** and choose **Settings → Import relay .env...**, then pick the
   `.env` next to `relay.py`. Issun reads the settings `relay.py` read:
   `DISCORD_CLIENT_ID`, `RELAY_KEY` (or the old name `RELAY_SECRET`),
   `RELAY_PORT`, `PUBLIC_BASE`, `STATUS_LINE`, `SHOW_ALBUM`, `PUBLIC_READ` and
   `ART_MIN_SCORE`. It shows one line per setting and gives secrets as lengths
   only. The key comes across, so **Ammy keeps working without re-pairing**.

   If an `ammy-uptime.log` sits beside the `.env`, it's merged into Issun's, with
   the old history first. The original file isn't changed, and importing twice
   doesn't duplicate anything. `ART_DIR` and `UPTIME_LOG` don't apply to Issun.

3. **Nothing changes on the phone.** Issun listens on the same port, so the
   existing Funnel and address still work.

4. **Check the public address.** An imported `PUBLIC_BASE` becomes Issun's
   override under **Settings → Advanced**. If it's an address you no longer use,
   such as an old Cloudflare tunnel, clear it and Issun will use your Tailscale
   address.

5. **Turn on Start with Windows.**

If the **Receiver** row says the port is in use, `relay.py` is still running.

To go back: turn off Start with Windows, quit Issun from the tray, then run
`Enable-ScheduledTask -TaskName "Ammy Relay"` and
`Start-ScheduledTask -TaskName "Ammy Relay"`.

A few things look different. `/version` answers `issun 0.1.0 (12)` rather than
`1.9.0`. Uptime-log lines are stamped `[issun 0.1.0 (12) / app 1.0 (80)]`
rather than `[relay 1.9.0 / app 1.0 (80)]`, and the summary reads both.
`relay.py --summary` is now the **Diagnostics** section of the window.

## HTTP routes

These are the same as `relay.py`. Send the key in an `X-Relay-Key` header;
`X-Relay-Secret`, the old name, still works. Every response carries
`X-Ammy-Relay`, which lets Ammy tell Issun's own "not found" from Tailscale's.

| Route | Key | Answer |
|---|---|---|
| `POST /now-playing` | yes | What Ammy sends. `204`; `401 unauthorized`; `400 bad json` |
| `GET /now-playing` | yes, unless Public read is on | The current track as JSON (below) |
| `GET /health` | no | `ok`, exactly |
| `GET /version` | no | e.g. `issun 0.1.0 (12)` |
| `GET /status` | yes | `alive` if the phone checked in within 90 s, else `stale` |
| `GET /diag` | yes | The phone's last self-report, e.g. `12s ago: engine=yes want=yes ...`, or `no diagnostics yet` |
| `GET /art/<hash>.jpg` | no | A cover the phone uploaded. Discord's servers fetch it and can't send a key |

Trailing slashes are ignored. Any other path gets `404 not found`, and any other
method gets `501`. Issun listens on `127.0.0.1` only, so Funnel is the only way
in.

```
curl https://<machine>.<tailnet>.ts.net/version
curl -H "X-Relay-Key: <key>" https://<machine>.<tailnet>.ts.net/now-playing
```

```json
{
  "playing": true, "stale": false, "updated_ago": 4.2,
  "title": "…", "artist": "…", "album": "…",
  "duration": 214.0, "elapsed": 61.3,
  "artwork": "https://…",
  "links": { "song": "https://music.apple.com/…", "artist": "…", "album": "…" }
}
```

`GET /now-playing` returns a summary, not the phone's raw push. It never
triggers a lookup, so nobody can use it to make your PC send requests, and
artwork and links appear only once they're cached. **Public read** serves it
with no key and with CORS so a web page can read it. That also means anyone
with the address can see what you're listening to.

## Building from source

You need Windows and the .NET 9 SDK.

```
dotnet build Issun.sln
dotnet test
dotnet run --project src/Issun -- --demo
```

`--demo` runs the window with made-up data and doesn't touch your real settings
or logs. To build the single `.exe` the way CI does:

```
dotnet publish src/Issun/Issun.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

The result is at `src/Issun/bin/Release/net9.0-windows/win-x64/publish/Issun.exe`.
A local build calls itself `0.1.0 (dev)`. CI stamps the build number (a commit
count) and the commit, and every push to `main` that changes more than
Markdown publishes a release. See
`.github/workflows/build.yml`.

Command-line switches:

- `--background`: start in the tray without opening the window. Start with Windows uses this.
- `--demo`: made-up data, for trying the window.
- `--screenshot <file.png>`: with `--demo`, draw the window to a PNG without showing it, then exit.

`CLAUDE.md` records the design decisions and the traps already paid for. Read
it before changing anything non-obvious.
