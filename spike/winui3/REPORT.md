# WinUI 3 spike: can Issun's window move from WPF to WinUI 3?

Branch `winui3-spike`, never merged. 12 Nov 2026. A throwaway app
(`spike/winui3`, `IssunSpike.exe`) built to answer five questions before any
migration. Issun's own UI is untouched.

Built with Windows App SDK **2.6.5** (the newest stable on nuget.org that day;
2.6.6 is experimental), .NET SDK 9.0.318, target
`net9.0-windows10.0.22621.0`, unpackaged (`WindowsPackageType=None`),
`WindowsAppSDKSelfContained=true`, `SelfContained=true`, x64. It references
the `Microsoft.WindowsAppSDK.WinUI` and `.Runtime` packages, not the
`Microsoft.WindowsAppSDK` metapackage (see 1), and it references
`Issun.Core`, so its size is a WinUI Issun's, not a bare page's.

Clean-PC runs are Windows Sandbox on this PC (`sandbox/run.ps1` drives
`sandbox/test.ps1` through `wsb exec`, as Issun's `tools/sandbox` does), two
runs: `results/sandbox-run1` and `results/sandbox-run2` (a run in between was stopped for a script bug and isn't kept). The sandbox image is
Windows 11 Enterprise 10.0.26100 with no .NET installed. Pictures are dark mode.

## The answers

| | Question | Answer |
|---|---|---|
| 1 | One self-contained `.exe` that runs on a clean PC | **Yes**, with a cost: it unpacks 180 MB into `%TEMP%` on first run |
| 2 | Tray icon with a menu | **Yes**, hand-written (WinUI has none), ~170 lines, no WinForms |
| 3 | `--screenshot` offscreen, and UI Automation | **Yes** to both, with caveats: needs a cloaked window, and a rasterization-scale setting |
| 4 | Size and cold start against WPF | **Smaller** (77.95 MB against 82.18 MB) and **faster** to a usable window once unpacked (0.6 s against 1.8 s for a one-page WPF app); slower on the very first run (3.7 s against 2.8 s) |
| 5 | The real ProgressRing and InfoBar mark, dark | Rendered; the success mark matches the WPF copy, the ring is the real control |
| 6 | MSIX, the Microsoft Store's route | **Yes**: packages as a full-trust MSIX, installs from a signed `.msix`, and everything Issun needs works inside it; AppData and HKCU writes are redirected; the app turns its StartupTask on and off, but launching at sign-in wasn't seen (the sandbox can't sign back in) |

### 1. One `.exe` on a clean PC: yes

`dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true
-p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true`
gives **one file**, `IssunSpike.exe` (plus a .pdb). In the sandbox it opened its
window first time, with no .NET and nothing installed (run 1 and run 2,
`report.txt`). `SelfInstall` could go on copying one `.exe` into
`%LOCALAPPDATA%\Programs\Issun` unchanged.

The cost is what happens on first run. Windows App SDK's own build targets
*require* `IncludeAllContentForSelfExtract=true` for a single file
(`Microsoft.WindowsAppSDK.SingleFile.targets` in the Base package, line 29:
"PublishSingleFile requires IncludeAllContentForSelfExtract for Dll SxS
redirection"), so the .NET host unpacks **everything**, managed and native, to
`%TEMP%\.net\IssunSpike\<hash>` before starting:

| | First run unpacks | Into |
|---|---|---|
| WinUI spike (with Issun.Core) | 578 files, 179.7 MB | `%TEMP%\.net\IssunSpike\<hash>` |
| WPF Issun (today) | 6 files, 8.2 MB | `%TEMP%\.net\Issun\<hash>` |

The hash changes with every build, and the host doesn't delete old folders, so
each Issun update would leave another ~180 MB behind in `%TEMP%` (today ~8 MB)
until Disk Cleanup or Storage Sense empties it. Issun could delete the stale
ones itself on start.

Two more findings:
- **Use the WinUI package, not the metapackage.** `Microsoft.WindowsAppSDK`
  2.6.5 also pulls in the AI, ML, Search and Widgets packages (onnxruntime
  22 MB, DirectML 17.8 MB, the semantic index). The bare page came to 90.75 MB
  with it and 66.12 MB without.
- **Windows 11 24H2 ships Windows App Runtime itself** (the sandbox listed
  `Microsoft.WindowsAppRuntime.CBS.1.6` and `.CBS.2` 2.2.0.100, inbox packages,
  not installed by anyone). The spike doesn't use them: every Windows App SDK
  DLL it loaded came from its own unpacked folder (run 2 `report.txt`: "the
  spike loaded its own Windows App SDK DLLs (from %TEMP%\.net), none of
  Windows' inbox ones"; `Microsoft.UI.Xaml.dll` 3.3.0.0, Windows App Runtime
  2.6). Windows 10 has no inbox copy at all, so being self-contained is what
  lets it run there; not tested on Windows 10.

### 2. Tray icon and menu: yes

WinUI 3 has no notification-area control, and Issun's WinForms `NotifyIcon`
would mean keeping WinForms in a WinUI app. `TrayIcon.cs` does it directly:
`Shell_NotifyIcon` (NOTIFYICON_VERSION_4) on a hidden top-level window of its
own, the `.exe`'s icon resource at the small-icon size (no file needed beside a
single `.exe`), and a Win32 popup menu with Issun's three items, Open Issun in
bold as the default, a check on Start with Windows, and the
`SetForegroundWindow` dance so it closes on a click elsewhere. In the sandbox:
- the menu opened at the icon and was captured (`tray-menu.png`, dark);
- Explorer was killed and restarted under it, the spike got `TaskbarCreated` and
  added its icon again (`spike.log`: "TaskbarCreated: Explorer restarted, adding
  the icon again", then "NIM_ADD ok").

The menu follows dark mode through uxtheme ordinals 135/136
(`SetPreferredAppMode`, `FlushMenuThemes`), which are **undocumented**, though
Windows' own Win32 tray menus behave this way. Today's WinForms menu doesn't
follow dark mode at all. A WinUI-styled flyout menu (as Windows 11's own tray
flyouts look) would need a small popup window hosting a `MenuFlyout`, which is
what the third-party H.NotifyIcon.WinUI package does; not tried. Issun's one
tray notification (`ShowBalloonTip`) is `NIM_MODIFY` with `NIF_INFO` in the
same API; not tried.

### 3. Offscreen `--screenshot` and UI Automation: yes, with caveats

**Screenshot.** WPF renders a page with no window at all. WinUI can't:
`RenderTargetBitmap` needs the element in a live XAML tree, and a page in no
window never loads (`--mode nowindow`: "the page never loaded (5 s)", then
`RenderAsync` never returned). What works (`App.ScreenshotAsync`): a real
window made a tool window (no taskbar button), `WS_EX_NOACTIVATE`, **cloaked**
with `DWMWA_CLOAK` so DWM never puts it on screen, shown with
`AppWindow.Show(false)` so it takes no focus. On this PC and in the sandbox it
wrote the PNG and nothing appeared. Moving it offscreen also works.

Three things to know:
- **Set the page's `RasterizationScale` to the output scale** (1.25 here).
  `RenderAsync(element, width, height)` alone renders at 1x and stretches:
  compare `results/render-upscaled-zoom.png` with
  `results/render-rasterization-scale-zoom.png`.
- **Keep a reference to the window.** A cloaked window nothing refers to is
  collected, closes, and the app exits with code 0 mid-render, silently.
- **The ProgressRing comes out aliased in a render** (`ring-frames-compare-dark.png`)
  while live on screen it is anti-aliased (`live-ring-strip-dark.png`), so a
  screenshot of a ring is a little rougher than the real thing.

The `.text.json` beside each PNG (every piece of text and where it sits, for
wording pages) works as before (`App.WriteTextBoxes`). Not tested: a locked
session, which blanks WPF's renders.

**UI Automation.** In the sandbox, from Windows PowerShell's
`System.Windows.Automation`, as Issun's `tools/sandbox/test.ps1` does:
- found the window (class `WinUIDesktopWin32WindowClass`, name Issun);
- found Copy key by `AutomationProperties.Name` and the `[text]` button by
  `AutomationProperties.AutomationId`; **invoking Copy key ran its handler**
  (the spike logged the click);
- read the Endpoint box's text through `ValuePattern`;
- the real ProgressRing is exposed as a ProgressBar named "Busy"; the WPF copy
  is a bare `FrameworkElement` with no automation peer.

All eight buttons were listed by name. `tools/sandbox/test.ps1` would need its
WPF-specific bits updated (window class, message-box keystrokes), not
rewritten.

### 4. Size and cold start

Sizes, single file, same publish flags:

| Build | Size |
|---|---|
| WPF Issun, today (local publish of `main` at 2977e7d) | 82.18 MB (86,170,329 bytes) |
| WPF Issun, release b51 asset | 86,197,201 bytes |
| WinUI spike + Issun.Core | 77.95 MB (81,732,173 bytes; run 1), 78.22 MB (82,022,903 bytes; run 2, with module logging) |
| WPF baseline (one Fluent page) + Issun.Core | 70.93 MB (74,380,546 bytes) |
| WinUI spike, no Issun.Core | 66.12 MB |
| WinUI spike, no Issun.Core, with the metapackage | 90.75 MB |

Cold start in the sandbox (run 2; run 1 agrees within 60 ms on the warm
medians). The yardstick is the same for every build: from `Start-Process`
until UI Automation finds a button in a window of that process, polled every
15 ms. The first run includes unpacking into `%TEMP%\.net`; the warm runs
are the next five starts. All three carry Issun.Core.

| Build | First run | Warm, median of 5 (range) |
|---|---|---|
| WinUI spike (one page, tray) | 3,687 ms | **603 ms** (558–643) |
| WPF baseline (one Fluent page) | 2,842 ms | 1,849 ms (1,775–1,961) |
| WPF Issun `--demo` (its real window) | 4,401 ms | 2,280 ms (2,225–2,365) |

The WPF baseline is the fair comparison: Issun's own demo does real work
before its window is up, and the spike shows a single page. The spike's own
log agrees: first frame 2,423 ms after process start on the first run, and
501–559 ms on the warm ones. Why WPF is slower here wasn't looked into
(single-file WPF loads and JITs a lot of PresentationFramework).

### 5. Connect your source, dark: WPF against WinUI 3

`results/wpf-wait-dark.png` and `results/wpf-connected-dark.png` are Issun's own
`--demo --screenshot --theme dark --setup-page 2`;
`results/winui-wait-dark.png` and `results/winui-connected-dark.png` are the
spike's same page. `results/ring-and-mark-compare-dark.png` zooms the status
row of each; `results/ring-frames-compare-dark.png` puts the two rings side by
side at eight points of their 2 s loop.

- **Success mark:** the same. Both are InfoBar's glyph pair, F136 in
  `SystemFillColorSuccessBrush` under F13E in `TextFillColorInverseBrush`; the
  connected render also shows WinUI's real InfoBar at Success below it for
  reference.
- **Ring:** both draw the same thing, an arc growing to half the circle and
  shrinking again while the whole turns, in the accent colour. WPF's static
  render catches its ring at the very start of its loop (a dot), because WPF's
  animation clock doesn't run offscreen; the strip sets its progress by hand.
  The renders' phases aren't locked together, so this shows shape and
  thickness, not timing.
- **Everything else** is close but not identical, as a different text stack
  would be: WinUI's text is set by DirectWrite and comes out a touch wider, and
  its TextBox is taller, so the page is 7 px (at 1.25x) longer.
- The QR code is a grey placeholder in the spike: **WinUI 3 has no
  `BlurEffect`**, which Issun uses to blur the QR code and key until Show.
  A WinUI Issun would need a Composition blur (Win2D's GaussianBlurEffect
  through an effect brush) for those two.

### 6. MSIX packaging (the Microsoft Store's route): yes

Added at the owner's yes on 12 Nov 2026, after the decision "for now, only
store, and winget piggybacking off of store". Built with `-p:SpikePackaged=true`
(`Package.appxmanifest`, single-project MSIX, no packaging project), signed
with a throwaway self-signed certificate (`CN=IssunSpikeTest`, made in memory
and saved as files; trusted only inside the sandbox, in
`LocalMachine\TrustedPeople`; nothing added to this PC's certificate stores),
installed with `Add-AppxPackage` in a fresh sandbox (`sandbox/msix-run.ps1`,
`results/sandbox-msix-run1` and `-run2`). Nothing was submitted anywhere.

- **Packaging: yes.** One project; `WindowsPackageType=MSIX`, `runFullTrust`,
  a `windows.startupTask` extension and an execution alias (for the tests).
  The build would only sign from a certificate in the user's store (APPX0105
  with a password-protected `.pfx`), so it builds unsigned and the `.msix` is
  signed afterwards with `signtool` from the SDK build-tools package. The
  Store signs the copy it distributes, so this only matters for test builds.
- **Size:** the `.msix` is **55.7 MB** (58,405,148 bytes), 368 files and 140 MB
  installed. It carries .NET (there is no .NET framework package) but not the
  Windows App SDK.
- **The Windows App SDK runtime is a dependency, not inside.** The package
  declares `Microsoft.WindowsAppRuntime.2` at least 2.6.5.0 (from Microsoft).
  Installed alone, Windows refused it: `0x80073CF3`, "Package failed updates,
  dependency or conflict validation"; the 2.2 copy Windows 24H2 ships (CBS)
  doesn't count. With the runtime package beside it (`Dependencies\x64\
  Microsoft.WindowsAppRuntime.2.msix`, 47.9 MB, built alongside) it installed in
  5 s. From the Store this is automatic: the Store installs framework
  dependencies with the app. Sideloaded, someone has to supply it.
- **Runs, full trust:** from `C:\Program Files\WindowsApps\IssunSpike.WinUI3_1.0.0.0_x64__zfx5h6nf580cc\IssunSpike.exe`,
  with package identity.
- **StartupTask: the app turns it on and off itself.** `StartupTask.GetAsync`
  read Disabled, `RequestEnableAsync` returned **Enabled** with no prompt,
  `Disable()` gave **Disabled**, and enabled again it was recorded in the
  user's registry (`State 2`).
  **Whether Windows then starts it at sign-in wasn't seen.** Signing the
  sandbox out ended its session for good: it never signed back in, so nothing
  could be checked (`sandbox-msix-run1`). Making Explorer run the startup apps
  again instead (clearing `SessionInfo\1\StartupHasBeenRun` and restarting
  Explorer, `sandbox-msix-run2`) didn't start it either, after 40 s, so
  packaged startup tasks aren't launched that way. Microsoft documents that an
  enabled StartupTask starts the app at sign-in; to see it, install on a
  machine that can sign out and back in (a Hyper-V VM, not this PC).
- **Tray icon and menu:** work from inside the package (`msix-tray/tray-menu.png`).
- **127.0.0.1 listener:** Kestrel on a free port (59828 and 56551, never 8787) answered
  `ok` to itself and to `Invoke-WebRequest` from outside the package.
- **Named pipe:** the packaged app connected as a client to a pipe served
  outside it (as Issun connects to Discord's), sent a line and got the
  server's reply (`msix-spike.log`: "pipeReply: hello back from the pipe
  server"). Run 1 marked it failed because closing the writer after the
  server hung up threw and overwrote the result, the test's bug; run 2 keeps
  both and passes.
- **Running other programs:** a stand-in `tailscale.exe` (there is no Tailscale
  in the sandbox) ran from `C:\Program Files\Tailscale` both by full path and
  through `PATH`, and its output was read back. **It ran outside the package**
  (it reported no package identity) and its own AppData writes went to the real
  folder, so the real `tailscale` CLI would behave as it does today. `winget`
  isn't in Windows Sandbox, so it couldn't be tried; `cmd /c ver` ran.
- **AppData and HKCU writes are redirected.** Files the app wrote to
  `%LOCALAPPDATA%` and `%APPDATA%` landed in
  `%LOCALAPPDATA%\Packages\<family>\LocalCache\Local` and `\Roaming`, not the
  real folders, though the app saw the usual paths; its HKCU write never
  reached the real registry. It **could read** a settings file already in the
  real `%APPDATA%` before it was installed. So a Store Issun would find the
  owner's existing settings, but whatever it writes from then on stays with the
  package and is deleted with it, and other programs (and the owner, browsing
  `%LOCALAPPDATA%\Issun`) won't see new logs there. The manifest can opt out
  (`desktop6:FileSystemWriteVirtualization` and `desktop6:RegistryWriteVirtualization`
  set to disabled, with the restricted capability `unvirtualizedResources`,
  which the Store reviews); not tried.
- **UI Automation and `--screenshot`** work from inside the package, as outside.
- **Uninstall:** `Remove-AppxPackage` removed it, and with it the package's
  `LocalCache` (so everything it had written to AppData) and its startup
  record (`sandbox-msix-run2`). The settings file in the real `%APPDATA%`
  that it had only read stayed.

For Issun this means, measured here:
- `SelfInstall`, the Run key and the Apps entry go; the StartupTask replaces
  the Run key (a Run-key write from inside the package would be redirected
  and never seen by Windows).
- Question 1's 180 MB unpacked into `%TEMP%` doesn't happen: an installed
  package's files are already on disk, so there is no single-file extraction.
- Logs and settings written by a Store Issun live under
  `%LOCALAPPDATA%\Packages\<family>\LocalCache\`, and go when it's
  uninstalled. Anything that reads `%LOCALAPPDATA%\Issun\issun.log` directly
  (Issun's CLAUDE.md tells sessions to) would need the new path, unless the
  manifest opts out of the redirection.
- WPF can be packaged the same way (Issun's CLAUDE.md, Distribution), so the
  Store doesn't require WinUI 3; this spike only shows that WinUI 3 doesn't
  stand in the Store's way.

## What a migration would cost

Measured or counted, not guessed:
- **Both views rewritten.** Issun's UI project is ~4,800 lines of XAML and C#.
  WinUI 3 has no `DockPanel` (61 uses), no `DataTrigger`/`Style.Triggers`/
  `ControlTemplate.Triggers` (14), no `DynamicResource` (55; `ThemeResource`
  instead), no `BlurEffect` (2), no `IsDefault`/`IsCancel` (2), and no
  `MessageBox` (24 uses, to `ContentDialog`). The view models could mostly
  stay: `INotifyPropertyChanged` and `ICommand` are the same, the WPF
  `Dispatcher` becomes `DispatcherQueue`, and `ImageSource`s change type.
- **Tray**: `TrayIcon.cs` from this spike, plus the notification.
- **Screenshot tooling**: the cloaked-window path above, in `Startup/Screenshot.cs`.
- **First run unpacks ~180 MB** into `%TEMP%`, and old copies pile up per build.
- Builds take longer and fail more opaquely: a C# error also shows as XAML
  compiler `WMC9999 Object reference not set`.

What it gains:
- The real controls instead of hand-drawn copies: ProgressRing, InfoBar,
  NavigationView, the real Fluent styles, titles and type ramp (Issun's
  `MainInstruction` is `TitleTextBlockStyle`), with their accessibility.
- A window up about 1.2 s sooner on every start after the first (0.6 s
  against 1.8 s), though about 0.8 s later on the very first.
- A tray menu that follows dark mode.

## Recommendation

**Migrating is possible without losing anything Issun depends on.** One
`.exe` that runs on a clean PC, the tray icon and its menu, offscreen
screenshots and UI Automation all work, and the app comes out a little
smaller and starts faster. What it costs: a rewrite of both views (no
`DockPanel`, triggers, `BlurEffect` or `MessageBox` in WinUI), the tray
written by hand, the screenshot path redone around a cloaked window, and about
180 MB unpacked into `%TEMP%` on each new build's first run.

I'd recommend migrating if Issun's window will keep growing and keep wanting
Windows 11 controls that WPF doesn't have. The ProgressRing was the first;
InfoBar, TeachingTip, ContentDialog and NavigationView are the likely next
ones, and each hand-drawn copy is a small migration paid anyway without
getting the real control. If the window is close to done, staying on WPF and
drawing the odd control by hand is cheaper.

With the Microsoft Store as the way Issun ships (decided 12 Nov 2026), the
two biggest WinUI costs above shrink: there is no `%TEMP%` unpacking, because
the package's files are installed, and the Windows App SDK runtime is a
framework the Store installs once, alongside. The rewrite of both views
remains. The Store route itself works with either framework.

If you go ahead: do it on its own branch, port the setup window first (the
smallest and newest view), keep the view models, take `TrayIcon.cs` and the
cloaked-window screenshot from this spike, reference
`Microsoft.WindowsAppSDK.WinUI` rather than the metapackage, and have Issun
delete stale `%TEMP%\.net\Issun\*` folders when it starts.

## Files

- `IssunSpike.csproj`, `App.xaml(.cs)`, `ConnectPage.xaml(.cs)`, `TrayIcon.cs`,
  `Native.cs`, `SpikeLog.cs`: the spike. Modes are listed at the top of `App.xaml.cs`.
- `../baseline-wpf/`: the WPF baseline for cold start.
- `sandbox/run.ps1`, `sandbox/test.ps1`: the clean-PC runs.
- `Package.appxmanifest`, `Assets/`, `PackageTest.cs`: the MSIX build (`-p:SpikePackaged=true`) and its in-package checks (`--package-test`).
- `sandbox/msix-run.ps1`, `sandbox/msix-test.ps1`: installing and checking the MSIX in Windows Sandbox.
- `../fake-cli/`: the stand-in `tailscale.exe` for the sandbox.
- `tools/wpf-ring-frames.ps1`: renders Issun's WPF ProgressRing at set points.
- `Gallery.cs` (`--gallery <dir>`), `tools/button-styles.py`, `tools/style-usage.py`: every WinUI 3 button control and style key at rest, pointer over, pressed and disabled; `results/button-gallery/` (asked for by the owner, 12 Nov 2026).
- `results/`: pictures, the sandbox runs' reports, logs and captures (`sandbox-run1`, `sandbox-run2`, `sandbox-msix-run1`, `sandbox-msix-run2`).
