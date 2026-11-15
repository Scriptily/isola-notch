# Microsoft Store package

Isola ships in two forms built from the same tag with the same version:

| | GitHub release | Microsoft Store |
|---|---|---|
| File | `IsolaSetup.exe` (per-user installer) | MSIX package |
| Signing | not yet (see [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md)) | signed by Microsoft when it passes certification |
| Updates | Isola updates itself from GitHub releases | the Store updates it; Isola never checks GitHub |
| Start with Windows | sign-in task and Run entry | the package's startup task |
| Settings, notes, shelf | `%LOCALAPPDATA%\Isola` | the same folder, so moving between them keeps everything |

The package is unsigned when it's uploaded: Partner Center signs it with
Microsoft's certificate after certification, so it doesn't wait on code
signing for the GitHub download.

## Files

| | |
|---|---|
| `AppxManifest.xml` | The package manifest, with `{{tokens}}` for the identity and version |
| `identity.json` | The package identity from Partner Center (public values, not secrets). Placeholders until the name is reserved |
| `build-msix.ps1` | Builds `dist\Isola-<version>.msix` (version from `VERSION`, as x.y.z.0); `-Register` installs the unpacked build on this PC |
| `make_tiles.py` | Redraws the tile and logo images in `assets/` from `assets/island.png` |

## Releasing

Every `vX.Y.Z` tag runs `.github/workflows/release.yml`, which builds the GitHub
setup and this package from the same source. Once the Store listing is live,
the same run submits the package to the Store; certification takes up to three
business days, and the Store updates installed copies itself.

## One-time setup

1. **Partner Center account.** Register as an individual developer at
   [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com/) (free for
   individuals; it asks for ID verification).
2. **Reserve the name.** Apps and games › New product › MSIX or PWA app. Done:
   "Isola" was taken, so the Store name is **Isola Notch** (the app still calls
   itself Isola).
3. **Copy the identity.** Done: `identity.json` holds the values from the
   product's Product identity page.
4. **First submission, by hand.** Upload `dist\Isola-<version>.msix` and fill in
   the listing (text below). Microsoft's update automation only works for an
   app that's already live.
5. **Automation.** In Partner Center, associate a Microsoft Entra tenant and
   add an Entra app with the Manager role (Account settings › User management ›
   Microsoft Entra applications). Then in the GitHub repository add the secrets
   `AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`,
   `AZURE_AD_APPLICATION_SECRET` and `SELLER_ID`, and the variable
   `STORE_PRODUCT_ID` (the Store ID, like 9NXXXXXXXXXX). From then on, every
   release goes to both.

## Trying the package on this PC

Turn on Developer Mode (Settings › System › For developers), close the GitHub
install's island, then:

```powershell
.\store\build-msix.ps1 -Register      # installs the unpacked build
Get-AppxPackage *Isola* | Remove-AppxPackage   # removes it again
```

Microsoft's certification checks can be run locally with the App
Certification Kit (as administrator):

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe" test -appxpackagepath dist\Isola-<version>.msix -reportoutputpath wack.xml
```

## Text for the first submission

**Short description**

> A dynamic island for Windows: notifications, media and a launcher at the top of your screen.

**Description**

> Isola puts a small pill at the top of your screen. It shows what's happening as it happens: notifications with the app's icon, calls from your phone through Phone Link, the song that's playing, your Bluetooth devices and AirPods battery, and a timer. Click it for a dashboard with media controls, per-app volume, Home Assistant devices, weather, notes, a file shelf and your clipboard history. Press Alt+Space and it becomes a launcher with math, unit conversion and commands.
>
> It's light on your PC (about 30 MB of memory, under 0.1% CPU at rest), has no account and no telemetry, and is free and open source under GPL-3.0.

**Screenshots:** `store/screenshots/1-home.png` to `5-hello.png` (1920x1080),
made from sample data by `make_screenshots.py`.

**Privacy policy URL:** `https://github.com/Scriptily/isola-notch/blob/main/PRIVACY.md`

**Website / support:** `https://github.com/Scriptily/isola-notch`

**Restricted capabilities: why Isola needs them**

> runFullTrust: Isola is a desktop app. It draws its window at the top of the screen with Direct2D, reads media sessions, Bluetooth devices and the clipboard history, and controls Phone Link calls through UI Automation, none of which run in an app container.
>
> unvirtualizedResources: to show each notification once, Isola turns off Windows' own banner and chime for each app while it shows notifications itself (a setting the user controls, explained in the app and the privacy policy). Those are the user's real per-app notification settings under HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings; with registry virtualization the change would go to a private copy and Windows would keep showing its banners. Isola saves the original values first and restores them when it stops, on quit, on sign-out, after a crash, and on uninstall. It also keeps its settings in %LOCALAPPDATA%\Isola, shared with the version from GitHub, so people moving between the two keep their settings.
