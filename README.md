# Issun

Issun shows what you're listening to on your Discord profile, as Rich Presence:
"Listening to Apple Music", with the song, the artist, the cover and a progress
bar. It doesn't read any music player itself. A source app sends it the
now-playing details over HTTPS, and Issun passes them to Discord.

```
source app ──https──▶ Tailscale Funnel ──▶ Issun, 127.0.0.1:8787 ──▶ Discord desktop app
```

Issun sets presence through the Discord desktop app's local connection, which is
the supported way for another program to do it. Nothing ever needs your Discord
account token. The cost is that your PC has to be awake, with Discord open.

It's one `.exe` with a window, a tray icon and a Start with Windows switch.
Nothing to install alongside it.

## Requirements

- Windows 10 or 11, 64-bit.
- The **Discord desktop app**, open and signed in, on the same PC and the same
  Windows account. Discord in a browser can't show presence from other apps.
- A free **Tailscale** account, with Funnel. This gives your source an HTTPS
  address to reach your PC. No domain, no port forwarding.
- For now, a **Discord application** of your own. It's free and takes a couple
  of minutes (step 3). Its name is what appears after "Listening to".
- A **source**: an app that sends what's playing to Issun (see
  [What a source sends](#what-a-source-sends)).

## Setup

1. **Download** `Issun-<version>-b<build>-<commit>.exe` from this repo's
   Releases page. Move it to a folder you'll keep, not Downloads. You can
   rename it to `Issun.exe`.

2. **Run it.** Issun isn't code-signed, so Windows shows "Windows protected your
   PC" the first time. Click **More info**, then **Run anyway**. Each release's
   notes list the file's SHA-256 if you want to check your copy:
   `Get-FileHash .\Issun.exe` in PowerShell.

   On first run Issun generates its key and opens the window with a **Connect
   your source** banner.

3. **Create the Discord application.** Go to
   `discord.com/developers/applications`, click **New Application** and name it
   after where the music comes from, such as `Apple Music`. Whatever you name it
   is the text after "Listening to". Copy the **Application ID** and paste it
   into Issun under **Settings → Discord application ID**. Paste it rather than
   retyping it: one wrong digit gets "Client ID is Invalid", which looks like a
   deleted application but isn't one.

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
   the PC's hostname. If you rename the PC, the address moves and your source
   can no longer reach Issun. In the Tailscale admin console, open this machine
   and turn off **Auto-generate from OS hostname**.

5. **Connect your source.** Issun's **Connect a source** section shows an
   **Endpoint** (like `https://<machine>.<tailnet>.ts.net/now-playing`) and a
   **Key**. Put both into your source's settings. The Endpoint stays blank until
   Tailscale is set up.

6. **Turn on Start with Windows** in Settings. Issun then starts in the tray
   when you sign in. Closing the window hides it to the tray. To exit, choose
   **Quit** from the tray icon's menu. To open it again, click the tray icon,
   or search the Start menu for **Issun**: it adds its own entry the first time
   it runs.

Put the `.exe` somewhere it will stay before you do step 6. It works from
Downloads, but Start with Windows and the Start menu entry both point at wherever
Issun last ran from.

When it's working, the **Source** row says it's checking in, the **Discord** row
says "Connected as" your name, and your Discord profile shows "Listening to"
followed by your application's name.

## Where things live

| What | Where |
|---|---|
| Settings, including the key | `%APPDATA%\Issun\settings.json` |
| Log | `%LOCALAPPDATA%\Issun\issun.log`, moved to `issun.log.old` at 5 MB |
| Source check-in history | `%LOCALAPPDATA%\Issun\uptime.log` |
| Covers uploaded by a source | `%LOCALAPPDATA%\Issun\art_cache\` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Issun` |
| Start menu entry | `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Issun.lnk`. Added once; if you delete it, Issun leaves it deleted |

Nothing is written next to the `.exe`. **Diagnostics → Open log folder** opens
`%LOCALAPPDATA%\Issun`. `settings.json` contains your key in plain text, so don't
share it. Setting the `ISSUN_HOME` environment variable puts everything in that
one folder instead.

## What a source sends

A source POSTs JSON to `/now-playing` on the Endpoint, with the key in an
`X-Relay-Key` header, whenever the track changes and every 30 seconds while it's
running. Issun answers `204`. All fields are optional apart from `playing`:

| Field | Meaning |
|---|---|
| `playing` | `true` while something is playing. `false` clears the Discord card. |
| `title`, `artist`, `album` | The track. |
| `duration`, `elapsed` | Seconds. A `duration` of 0 means no progress bar, e.g. a live station. |
| `live` | `true` for a live station. |
| `explicit` | `true` puts Apple Music's E after the title in Issun's window. Send it only if you know: leave it out and Issun uses what the `store_id` lookup says, while `false` overrides that. Discord has no place for it, so the card doesn't change. |
| `store_id` | Apple Music catalog ID. Gives the exact cover and clickable links. |
| `artwork_b64` | Base64 JPEG cover, for tracks with no catalog ID. Needed once per track. |
| `seq` | Milliseconds since 1970 at sending, so a push that arrives late can't overwrite a newer one. |
| `app_name`, `app_version` | The source's name and version, e.g. `"1.0 (97)"`. The window shows them as "Source: <name> 1.0 (97)". |
| `diag` | A self-report from the source, recorded for diagnostics. |

Without a `store_id` or `artwork_b64`, Issun looks the cover up by title and
artist, which is usually right but not always.

## Coming from relay.py

If you ran the older Python `relay.py`:

1. **Start Issun** and choose **Settings → Import relay .env...**, then pick the
   `.env` next to `relay.py`. Issun takes `DISCORD_CLIENT_ID`, `RELAY_KEY` (or
   the old name `RELAY_SECRET`), `RELAY_PORT`, `PUBLIC_BASE` and `PUBLIC_READ`,
   shows one line per setting, and gives secrets as lengths only. The key comes
   across, so your source keeps working without being set up again. If an uptime
   log sits beside the `.env`, its history is merged into Issun's.

2. **Then stop the relay.** It holds port 8787, and only one program can. Issun
   keeps trying the port every 15 seconds and takes it over as soon as the relay
   has stopped, with no restart. Do it in this order: if the relay stops before
   the import, Issun is holding a different key and your source's pushes are
   refused. If your relay runs as a Scheduled Task, disable the task rather than
   deleting it, so you can switch back.

3. **Check the public address.** An imported `PUBLIC_BASE` becomes Issun's
   override under **Settings → Advanced**. If it's an address you no longer use,
   clear it and Issun will use your Tailscale address.

If the **Receiver** row says the port is in use, the relay is still running.

## HTTP routes

Send the key in an `X-Relay-Key` header; `X-Relay-Secret`, the old name, still
works. Every response carries an `X-Ammy-Relay` header, so a source can tell
Issun's own "not found" from one sent by something in front of it.

| Route | Key | Answer |
|---|---|---|
| `POST /now-playing` | yes | What a source sends. `204`; `401 unauthorized`; `400 bad json` |
| `GET /now-playing` | yes, unless Public now playing is on | The current track as JSON (below) |
| `GET /health` | no | `ok`, exactly |
| `GET /version` | no | e.g. `issun 0.1.0 (12)` |
| `GET /status` | yes | `alive` if the source checked in within 90 s, else `stale` |
| `GET /diag` | yes | The source's last self-report, or `no diagnostics yet` |
| `GET /art/<hash>.jpg` | no | A cover a source uploaded. Discord's servers fetch it and can't send a key |

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
  "links": { "song": "https://music.apple.com/…", "artist": "…", "album": "…" },
  "explicit": true
}
```

When nothing is playing, or the source has been quiet for more than 90 s, only
`playing`, `stale` and `updated_ago` appear. `explicit` appears only when it's
true.

`GET /now-playing` returns a summary, not the raw push. It never triggers a
lookup, so nobody can use it to make your PC send requests, and artwork, links
and a looked-up `explicit` appear only once they're cached. **Public now playing** serves it with no
key and with CORS so a web page can read it. That also means anyone with the
address can see what you're listening to.

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
Markdown publishes a release. See `.github/workflows/build.yml`.

Command-line switches:

- `--background`: start in the tray without opening the window. Start with Windows uses this.
- `--demo`: made-up data, for trying the window.
- `--screenshot <file.png>`: with `--demo`, draw the window to a PNG without showing it, then exit.

`CLAUDE.md` records the design decisions and the traps already paid for. Read
it before changing anything non-obvious.
