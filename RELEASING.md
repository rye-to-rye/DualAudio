# Releasing DualAudio

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a `v*` tag is pushed. The version comes
from the tag (`v0.1.1` → `0.1.1`); `Directory.Build.props` `<Version>` is only the default for local builds.

## Cut a release

1. Make sure `main` is green on CI (Actions → CI).
2. Hardware-check the build you are about to tag (docs/test-log.md), at least connect + audio + tray Exit.
3. Optional: set `<Version>` in `Directory.Build.props` to the new version so local builds match (not required).
4. Tag the commit and push the tag:

   ```powershell
   git checkout main
   git pull
   git tag v0.1.1
   git push origin v0.1.1
   ```

5. Watch Actions → Release. It: restores, builds Release with `-p:Version=0.1.1`, runs the tests (without the
   Hardware category), downloads the previous release (for a delta package), runs `build/pack.ps1`, then
   `vpk upload github --publish` creates the release `DualAudio 0.1.1` with the assets, and GitHub's generated notes
   are written into it.
6. Check the release page. Assets (exact names):

   | File | What |
   |------|------|
   | `DualAudioApp-win-Setup.exe` | Installer (website download link) |
   | `DualAudioApp-win-Portable.zip` | Portable copy |
   | `DualAudioApp-0.1.1-full.nupkg` | Update package |
   | `DualAudioApp-0.1.1-delta.nupkg` | Small update from the previous version (not on the first release) |
   | `releases.win.json`, `assets.win.json`, `RELEASES` | Update feed read by installed apps |

   Stable download link for the website (always the newest release):
   `https://github.com/rye-to-rye/DualAudio/releases/latest/download/DualAudioApp-win-Setup.exe`

Pre-releases: a tag with a suffix (`v0.2.0-beta.1`) is published as a GitHub pre-release. Installed apps ignore
pre-releases (they ask GitHub for stable releases only).

A tag that failed: delete the release (if one was created) and the tag, fix, tag again:
`gh release delete v0.1.1 --yes; git push --delete origin v0.1.1; git tag -d v0.1.1`.

## Code signing (optional)

Unsigned builds work but show SmartScreen ("More info → Run anyway"). To sign, add two repository secrets:
`SIGN_CERT_BASE64` (the .pfx file, base64) and `SIGN_CERT_PASSWORD`. The release workflow then passes
`--signParams` to vpk (signtool, SHA-256, DigiCert timestamp). Without the secrets the step is skipped.

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('codesign.pfx')) | Set-Clipboard
```

## Build the installer locally

```powershell
./build/pack.ps1                 # version from Directory.Build.props
./build/pack.ps1 -Version 0.1.1  # what CI does for tag v0.1.1
```

Output goes to `releases/` (ignored by git); the self-contained publish goes to `artifacts/publish/`.

## Test an update locally (0.1.0 → 0.1.1)

Velopack can update from a local folder, but the app is hard-wired to GitHub Releases, so the honest end-to-end test
uses two real releases:

1. Tag and push `v0.1.0`; wait for the release. Download and run `DualAudioApp-win-Setup.exe` (SmartScreen: More
   info → Run anyway). Finish onboarding, connect, play audio. Settings → About shows version 0.1.0.
2. Make any small change (or none), tag and push `v0.1.1`; wait for the release.
3. In the installed 0.1.0: either wait (first check runs 60 s after start; restart DualAudio from the tray to trigger
   one) or press **Settings → About → Check for updates**. About shows "Version 0.1.1 is ready: restart to install",
   the dashboard shows the "Update ready" banner and the tray tooltip has "Update ready: restart to install".
4. Press **Later**: the banner and tray line disappear and nothing restarts. Exit DualAudio from the tray, start it
   again: About shows 0.1.1 (the update was installed on exit, no restart).
   Or press **Restart now**: DualAudio exits cleanly (phone audio returns to the phone), restarts within a few seconds
   as 0.1.1 and reconnects.
5. Check that settings (saved phone, volume, shortcuts) are unchanged and start-with-Windows still points at
   `%LOCALAPPDATA%\DualAudioApp\current\DualAudio.exe`.

Logs to look for (`%LOCALAPPDATA%\DualAudio\logs`): `Checking for updates`, `Update 0.1.1 found; downloading`,
`Update 0.1.1 downloaded and ready`, `will be installed after exit` or `User chose to restart to install`.
Velopack writes its own log (`velopack*.log`) for the installer/updater; look in `%LOCALAPPDATA%\DualAudioApp` and
`%TEMP%` if an update doesn't apply.

Dry run without publishing: tag in a fork, or build two local packages with `./build/pack.ps1 -Version 0.1.0` and
`-Version 0.1.1 -OutputDir releases` (same folder, so the second gets a delta) and inspect `releases.win.json`.
