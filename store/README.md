# Microsoft Store package

Isola ships in two forms built from the same tag with the same version:

| | GitHub release | Microsoft Store |
|---|---|---|
| File | `IsolaSetup.exe` (per-user installer) | MSIX package |
| Signing | not yet (see [CODE_SIGNING_POLICY.md](../CODE_SIGNING_POLICY.md)) | signed by Microsoft when it passes certification |
| Updates | Isola updates itself from GitHub releases | the Store updates it; Isola never checks GitHub |
| Start with Windows | sign-in task and Run entry | the package's startup task |
| Hiding Windows' own banners | Isola turns them off per app and restores them | not available: Settings opens Windows' notification settings instead |
| Settings, notes, shelf | `%LOCALAPPDATA%\Isola` | that same folder if it already exists (a GitHub install made it); otherwise the package's private copy, removed on uninstall |
| The other one installed | the setup asks first whether to replace the Store version | asks at first start whether to replace the GitHub install |

Replacing either way keeps settings, notes and shelf unless the user switches
that off (`src/App/Switchover.cs`). The Store version removes the GitHub
install with that install's own uninstaller, which runs outside the package,
so the sign-in task, uninstall entry and shortcut really go and Windows'
banners are restored. The GitHub setup copies the Store version's private data
(if any) into `%LOCALAPPDATA%\Isola` before removing the package with
`Remove-AppxPackage`, and leaves the package alone if that copy fails.

The package is unsigned when it's uploaded: Partner Center signs it with
Microsoft's certificate after certification, so it doesn't wait on code
signing for the GitHub download.

## Files

| | |
|---|---|
| `AppxManifest.xml` | The package manifest, with `{{tokens}}` for the identity and version |
| `identity.json` | The package identity from Partner Center (public values, not secrets). Placeholders until the name is reserved |
| `build-msix.ps1` | Builds `dist\Isola-<version>.msix` (version from `VERSION`, as x.y.z.0); `-Register` installs the unpacked build on this computer |
| `make_tiles.py` | Redraws the tile and logo images in `assets/` from `assets/island.png` |

## Releasing

Every `vX.Y.Z` tag runs `.github/workflows/release.yml`, which builds the GitHub
setup and this package from the same source, and submits the package to
Partner Center. Certification takes up to three business days, and the Store
updates installed copies itself.

## One-time setup

1. **Partner Center account.** Register as an individual developer at
   [storedeveloper.microsoft.com](https://storedeveloper.microsoft.com/) (free for
   individuals; it asks for ID verification).
2. **Reserve the name.** Apps and games -> New product -> MSIX or PWA app. Done:
   "Isola" was taken, so the Store name is **Isola Notch** (the app still calls
   itself Isola).
3. **Copy the identity.** Done: `identity.json` holds the values from the
   product's Product identity page. The Store ID is **9NW22R7PVDQT**
   (listing: https://apps.microsoft.com/detail/9NW22R7PVDQT).
4. **First submission, by hand.** Done: 1.2.1 passed certification and went
   live on 2026-11-16 (https://apps.microsoft.com/detail/9NW22R7PVDQT). The
   first attempt was refused only for `unvirtualizedResources` (see below).
5. **Automatic submission.** Done on 2026-11-16. Partner Center's "Create"
   button under Tenants leads into a Microsoft business account that needs a
   tax number, so the directory came from a free Azure account instead:
   - azure.microsoft.com/free (individual) creates a "Default Directory". The
     trial subscription can lapse; the directory and the app below stay free.
   - In that directory: a user `admin@<domain>.onmicrosoft.com` with the
     Global Administrator role (Partner Center's **Associate** only accepts a
     work account), and an app registration "Isola releases" with a client
     secret.
   - Partner Center -> Account settings -> Tenants -> Associate, signed in as that
     admin; then User management -> Microsoft Entra applications -> add "Isola
     releases" as **Manager**. Seller ID is under Legal info -> Developer.
   - Repository secrets `AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`,
     `AZURE_AD_APPLICATION_SECRET`, `SELLER_ID`; variables
     `STORE_PRODUCT_ID=9NW22R7PVDQT` and `STORE_SECRET_EXPIRES` (the secret's
     expiry date).

## Keeping access working

The client secret expires (24 months at most) and can't renew itself.
`.github/workflows/store-check.yml` signs in and reads the listing on the 1st of
every month without submitting anything, and `release.yml` does the same before
each submission. Within 60 days of `STORE_SECRET_EXPIRES`, or if sign-in fails,
it opens an issue titled "Renew the Microsoft Store client secret" with the
steps, and closes it once the check passes. Test it with
`gh workflow run store-check.yml -f pretend_days_left=30`.

GitHub pauses scheduled workflows in a public repository after 60 days without
a commit, so in a quiet stretch the monthly check stops; the check in
`release.yml` still catches an expired secret at the next release.

If certification fails or Partner Center is down, the package is still an
artifact of the release run (`Isola-<version>-msix`) and can be uploaded by
hand: Isola Notch -> Update -> Packages.

## Trying the package on this computer

Turn on Developer Mode (Settings -> System -> For developers), close the GitHub
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
> It's light on your computer (about 30 MB of memory, under 0.1% CPU at rest), has no account and no telemetry, and is free and open source under GPL-3.0.

**Screenshots:** `store/screenshots/1-home.png` to `5-hello.png` (1920x1080),
made from sample data by `make_screenshots.py`.

**Privacy policy URL:** `https://github.com/Scriptily/isola-notch/blob/main/PRIVACY.md`

**Website / support:** `https://github.com/Scriptily/isola-notch`

**Restricted capability: why Isola needs it**

> runFullTrust: Isola is a desktop app. It draws its window at the top of the screen with Direct2D, reads media sessions, Bluetooth devices and the clipboard history, and controls Phone Link calls through UI Automation, none of which run in an app container.

The first submission also asked for `unvirtualizedResources` (so the Store
version could hide Windows' own banners and share the GitHub version's data
folder). Microsoft denied it on 2026-11-15, so the package no longer declares
it: the Store version links to Windows' notification settings instead, and its
data stays in the package's own folder.
