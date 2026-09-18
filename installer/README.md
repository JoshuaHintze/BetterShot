# BetterShot installer

`BetterShot.iss` defines the first unsigned, per-user BetterShot installer for 64-bit Windows 10 version 1809 or newer. It has no auto-updater and never removes capture history or other user data during uninstall.

## Inputs and output

The default application version is `0.1.0`. Override it when compiling with Inno Setup:

```powershell
ISCC.exe /DAppVersion=0.1.1 installer\BetterShot.iss
```

The installer reads a self-contained x64 publish from `artifacts\installer-publish` and writes `BetterShot-<version>-Setup-x64.exe` to `artifacts\installer`. Override those directories with `/DPublishDir=...` and `/DOutputDir=...` if needed. The publish must include `BetterShot.exe` and `ThirdPartyNotices\Lucide-LICENSE.txt`; all publish files except `.pdb` files are packaged.

BetterShot must be fully closed before installing or uninstalling because the installer checks its single-instance mutex. Closing the main window only sends BetterShot to the notification area, so use **Exit** from the BetterShot tray menu first.

The installer creates a Start menu shortcut. A desktop shortcut is available as an optional, unchecked task. Start-with-Windows remains controlled by BetterShot Settings. On uninstall, setup removes only the app-owned `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\BetterShot.lnk`; it leaves `%LOCALAPPDATA%\BetterShot\History` and other user data intact.
## Rebuild

Run `.\build-installer.ps1` from the project root. It publishes an untrimmed self-contained win-x64 app, includes .NET and Lucide notices, compiles setup, and writes a SHA-256 checksum beside the installer. Runtime packs are restored from the official NuGet feed. Published payloads use fresh timestamped folders. The installer is unsigned; no certificate or automatic updater is configured.

The local compiler is Inno Setup 6.7.3 at `.tools\InnoSetup\ISCC.exe`. Official compiler download: https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe. Downloaded compiler SHA-256: 9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732; its Authenticode signature was validated against Pyrsys B.V. Compiler licensing information: https://jrsoftware.org/isdl.php (a commercial compiler license is required for commercial use).

## First-release verification

`artifacts\verify-first-installer.ps1` performs an actual silent install into a scoped test directory, standalone self/startup/history checks without DOTNET_ROOT variables, reinstall, uninstall, and checks hashes of preexisting history/settings. It then installs to the normal per-user location and launches that copy. This changes local installation state and stops the development copy. Do not treat it as a read-only unit test.

Final output: `artifacts\installer\BetterShot-0.1.0-Setup-x64.exe` (57,809,569 bytes), SHA-256 `3D1FF6E944CA9435FC1B8645C5807C68F5ABA5B0DC4BEA0F6A23167414A89F36`. Fresh payload self/history/startup/workflow/shell diagnostics passed. Actual install/reinstall/uninstall passed. Existing user data remained byte-for-byte unchanged during the cycle. Verification artifacts: `artifacts\installer-final-payload-checks` and `artifacts\installer-cycle-checks`. Verified on this machine; a clean separate PC has not yet been tested.
Latest update0.1.1: automatic capture clipboard copy and saved image/file drag mode. Built and upgraded installed0.1.0 in place; history/settings preserved. Installer artifacts\installer\BetterShot-0.1.1-Setup-x64.exe; verification artifacts\sharing-release-checks. Still unsigned.
