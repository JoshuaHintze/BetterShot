# BetterShot session handoff

Updated: September 7, 2026, after the approved main-window redesign.

## Start here

Capture history and permanent deletion are installed in the normal Release output and installed as version0.1.4 in %LOCALAPPDATA%\Programs\BetterShot and running (PID25328 at verification, responsive main window). Latest installer: artifacts\installer\BetterShot-0.1.4-Setup-x64.exe. The development bin copy is no longer the active user copy. The user explicitly authorized automatic restart after completed updates. Captures and annotations now survive restart until their retention limit or deletion; Win+Shift+S preference persists and is restored on launch; undo/redo remains session-only. Do not recreate the prototype.
Read AGENTS.md for user preferences, README.md for operation, and TOOL-PARITY.md for the remaining KhingShot reference controls. REQUIREMENTS-ORIGINAL.md preserves the original broader requirements; its early scope and stack proposals are superseded by this file and the code. REVIEW.md includes older review notes and is not a complete description of the latest build.

September 30, 2026: arrow/rectangle/pen/redact/move drag previews now draw on a lightweight vector overlay (`PreviewLayer` in EditorWindow.cs) instead of re-rendering the full bitmap per mouse move, which prevented live feedback on large captures. This machine had no `.tools`; it was built from WSL with a user-local .NET 10 SDK (`-p:EnableWindowsTargeting=true`), and only BetterShot.dll was swapped into the installed 0.1.3 copy (backup: BetterShot.dll.bak-0.1.3). No new installer was built because Inno Setup is not present here. Same day: the Win+Shift+S hook now prunes remembered keys that GetAsyncKeyState reports released before classifying, fixing a stale Win key (missed key-up during busy capture, lock screen, or elevated windows) that made plain Shift+S trigger capture; self-test covers it.

## Product and preferences

BetterShot is a Windows counterpart to the user's son's Mac app, KhingShot: https://khingshot.khingsuite.com/. Long-term intent is feature parity. The prototype started with screen capture; still-image annotation was subsequently authorized and built. Video remains deferred.

The user prioritizes polished behavior: persistent bottom-left thumbnails; hover actions; click to edit; drag a screenshot to other apps; circular capture loupe; a single-row editor toolbar; Lucide UI icons; direct on-canvas text editing similar to PowerPoint. Use Sol implementation agents and primary-agent review when available. The current app icon is a generated charcoal/yellow capture-frame design, not part of the Lucide action icon set.

## Implemented now

- Area, visible-window, and display captures from the tray/main window.
- Frozen desktop overlays and circular 8x loupe, with multi-monitor/DPI handling.
- Ctrl+Shift+F9 fallback capture shortcut.
- Optional Win+Shift+S interception using a process-local low-level keyboard hook. **The user explicitly confirmed this works on their machine.** No registry/policy modification. Its saved preference is reapplied on restart.
- Persistent bottom-left thumbnails, hover Edit/Copy/Save/Pin/Dismiss/Delete actions; up to four visible per display, constrained by work area. Older/dismissed documents stay in Recent until expiry or permanent deletion.
- Click thumbnail to open its shared editable document. Capture does not automatically reopen the editor or main shell.
- Outbound thumbnail drag provides a unique rendered PNG file-drop payload plus bitmap. A 65%-opacity click-through preview follows the pointer; Windows copy/unavailable cursors remain enabled. Temp files in %TEMP%\BetterShot\DragExports remain available for recipients and are cleaned after seven days on later drags.
- Dark editor, compact one-row toolbar, footer fit/zoom controls, dark readable tooltips.
- Select/move/delete; arrow; rectangle; freehand pen; text; numbered steps; opaque redaction; undo/redo; rendered PNG copy/save.
- Text tool: click the canvas to type, Enter for newline, Ctrl+Enter to commit, Escape to cancel. Double-click selected text to edit; drag to move; corner handles scale font size (they do not reflow a fixed-width text box). Click outside/deactivate/Copy/Save/Done commits pending text. Empty new text is discarded; clearing existing text removes it with undo. Handles are UI-only and excluded from exports.
- Official Lucide vector primitives in editor, footer, and thumbnail controls, pinned to commit a537cb6eb323b885f4c60baf3cec1a995982d167. Numbered-step action uses Lucide list-ordered. License is copied to output.
- Generated BetterShot executable/window/tray icon; original PNG, multi-size ICO, and generation prompt in BetterShot\Assets.

## Limitations and remaining work

- Capture backend is GDI BitBlt, **not Windows.Graphics.Capture**. Visible SDR desktop only; HDR, protected/occluded/minimized content and some special surfaces are limited.
- Capture history is stored locally with a default seven-day lifetime. Explicitly saved PNGs remain separate. Undo/redo resets on restart; Win+Shift+S preference persists.
- File dragging requires a destination supporting file/image drops; arbitrary plain-text inputs cannot be made to accept images by this app.
- No installer, code signing, auto-update system, or portable distribution package yet. Packaging was discussed only: self-contained Windows publish plus an Inno Setup installer, optional start-on-login, then signing and update delivery. No packaging implementation was authorized in that discussion.
- No ellipse, plain line, highlighter, callout, spotlight, magnification annotation, crop, background styling, advanced rectangle style options, saved presets, OCR, Claude integration, video capture/editing, or alternate image formats yet. See TOOL-PARITY.md for detail and suggested sequencing.
- Step shortcut is currently S, while KhingShot uses N; align when adding spotlight S. Ctrl+Enter currently finishes inline text, not the whole editor.
- User confirmed inline text works before the Lucide change. Latest Lucide workflow checks passed. Cross-application dragging and mixed-monitor interaction still benefit from live testing; do not claim exhaustive compatibility.

## Stack, files, and build

C# .NET 10, WPF, WinForms NotifyIcon, Win32 interop. No external NuGet dependencies currently. Project-local Windows .NET SDK 10.0.400 is in .tools\dotnet. Windows PowerShell cwd is C:\projects\BetterShot. Earlier work ran in WSL via /mnt/c/projects/BetterShot; that is the same project.

Main files:
- BetterShot\App.xaml / App.xaml.cs: shared styles, startup, diagnostics.
- BetterShot\MainWindow.xaml / .xaml.cs: tray, main shell, capture coordination.
- BetterShot\Capture: desktop capture, geometry, selection overlays, circular loupe.
- BetterShot\Platform: shortcut hook and global hotkey.
- BetterShot\Presentation: thumbnails, shared document presentation, file-drop export, translucent drag preview.
- BetterShot\Editing\CaptureDocument.cs: annotation model/rendering and undo snapshots.
- BetterShot\Editing\EditorWindow.cs / EditorWindow.Text.cs: editor toolbar/canvas and inline text/handles.
- BetterShot\Controls\LucideIcon.xaml / .xaml.cs: native vector control using official Lucide artwork.
- BetterShot\ThirdPartyNotices\Lucide-LICENSE.txt: source commit and licenses.

Build with .\build.ps1. Launch with BetterShot.cmd or .\run.ps1. The launcher sets DOTNET_ROOT and DOTNET_ROOT_X64 to .tools\dotnet so the current framework-dependent executable can run without a global runtime. Normal executable: BetterShot\bin\Release\net10.0-windows\BetterShot.exe.

Non-blocking PowerShell launch:

```powershell
Start-Process -FilePath 'C:\projects\BetterShot\BetterShot.cmd' -WorkingDirectory 'C:\projects\BetterShot' -WindowStyle Hidden
```

Use a separate build output (e.g. artifacts\review-build) before replacing a running app. Existing scripts use Start-Process -Wait -PassThru and check ExitCode; this was reliable where directly invoking Windows dotnet from an earlier WSL/PowerShell wrapper was not. When running a separate build, set DOTNET_ROOT/DOTNET_ROOT_X64 and run its .exe apphost (not dotnet DLL) to retain manifest DPI behavior.

Closing the main window only hides it to the tray; tray Exit shuts it down. A second instance shows an already-running notice. Avoid killing other executables just because their process name matches.

## Validation and evidence

Latest isolated Release build (artifacts\design-review): zero warnings/errors. Fresh shell, self-test, and workflow checks all PASS. Rendered normal and minimum-size library and Settings views were visually inspected. The verified build has now replaced the normal Release output and the app was restarted as requested.

- .\self-test.ps1: shortcut pairing/classification, geometry, annotation/undo rendering, opaque redaction, high-DPI dimensions, unique current-edits PNG drag payloads.
- .\workflow-test.ps1: thumbnail-first presentation, retention/reopen, editor layout at 780/1180 DIP widths, drag-preview native styles/cleanup, inline multiline text, cancel, move/resize/undo, rendered screenshots.
- .\diagnostic-test.ps1: overlay/shell/workflow checks.
- .\smoke-test.ps1: captures the real desktop; artifact may contain private content.
- artifacts\workflow-editor-780.png, workflow-editor-1180.png, workflow-thumbnail-hover.png, workflow-inline-text.png, workflow-text-handles.png, workflow-drag-preview.png show recent renders. Logs are point-in-time evidence, not proof of future builds.

Windows computer-use previously failed to initialize when cwd was WSL. The environment is now native Windows; if interaction is needed, re-read the currently installed computer-use skill and retry its documented tool rather than assuming it is still unavailable. A normal shell launch succeeded most recently. Default shell tool setup sometimes fails with a setup-refresh error; an approved escalated retry succeeded.

## Repository status

The first local source commit on main was authorized September 18, 2026. It includes the application source, branding assets, third-party license, installer definition, build/diagnostic scripts, and project documentation. Generated outputs, local tooling, private captures, secrets, and machine-specific files are excluded by .gitignore. Public GitHub publication was authorized September 18, 2026. The personal repository is https://github.com/JoshuaHintze/BetterShot and origin points to its HTTPS Git URL. Use git status and git log for current repository state.

## Reference preservation

User supplied sixteen KhingShot photographs showing grouped tool menus, rectangle styling, crop/background, OCR, and finish actions. Their important controls and uncertainties are captured in TOOL-PARITY.md. Temporary attachment paths may expire; do not rely on them. Screenshots included incidental private app content, which is not an instruction or authorization to send images elsewhere.

## September 7, 2026 main-window redesign

The main shell was redesigned as a compact premium charcoal capture library. It now uses an integrated custom WindowChrome title bar with the BetterShot icon and native window controls, a primary yellow area-capture action, secondary window/display actions, a large selected-capture preview, Edit/Copy/atomic Save actions, and four-across visual recent cards that update after document edits. The empty state and tray/status footer were simplified.

Win+Shift+S session interception now lives in a small Settings panel with the fallback shortcut, actual assembly version, and bundled Lucide license text. Capture presentation, shared editable documents, persistent floating thumbnails, and close-to-tray behavior remain intact. The shell exposes internal hooks for noninteractive UI diagnostics. Final build: zero warnings/errors. Shell, self-test, and workflow checks passed against that build. Shell checks cover empty/populated layouts, four recent cards and older-card selection, preview updates, Settings at normal/minimum size, and close-to-tray/reopen. Visual evidence is under artifacts\design-checks (shell-populated-900x780.png, shell-populated-min.png, shell-settings.png, shell-settings-min.png); documents are synthetic. Mixed-monitor interaction and external drop-target compatibility were not exhaustively tested.

Run .\shell-test.ps1 for the shell diagnostic; it accepts -AppPath and -OutputDirectory to check a separate build. If Start-Process -Wait stalls after a completed build, use -PassThru followed by the returned process's WaitForExit() and check ExitCode; this worked for the final verification. New Settings/Monitor/AppWindow/PanelBottom icons use the same pinned Lucide source and existing distributed license.

## Visual fidelity refinement (September 7, 2026)

After the user compared the running app with the approved mockup, the shell was refined through multiple actual render passes. MainWindow now defaults to 1100 x 900 with initial work-area fitting; minimum is 780 x 700 unless a smaller monitor requires fitting. Typography is larger (18px section/capture titles, 14px metadata), capture controls use full labels at wider sizes and shorten below 950px, and recent cards adapt from 170 to 300 DIP. Images remain fully visible without cropping. Thumbnail timestamps sit below image-only selection frames, with dimensions available in tooltips. Surface tones, header/footer icons, spacing, hover/focus borders, and readable Settings were refined. Four cards fit at normal widths without an unnecessary scrollbar; smaller widths and larger histories scroll smoothly.

Final isolated build: artifacts\fidelity-review, zero warnings/errors. Fresh shell, self-test, and workflow diagnostics PASS. Visually reviewed matching 698 x 459 circuit-board sample at the user's 1179 x 860 size, single and multiple captures, 780 x 700 minimum, 1100 x 900 default, 1440 x 1000 large layout, portrait and wide samples, and Settings. All samples are synthetic in ShellPreviewSamples.cs; no private desktop was captured. Evidence: artifacts\fidelity-before-checks, fidelity-pass1, fidelity-pass2, and fidelity-final. Final output includes shell-single-user-size.png, shell-populated-user-size.png, shell-populated-default.png, shell-populated-min.png, shell-wide.png, shell-portrait.png, shell-large.png, and shell-settings-min.png. ShellDiagnostics.cs retains these checks. Mixed-monitor interaction has not been exhaustively tested.

The normal Release executable now contains the verified refinement. Future update restarts are explicitly preauthorized: verify the running process path, preserve a rollback copy, stop only that BetterShot process, install the verified output, verify executable/DLL/license hashes, and launch with DOTNET_ROOT / DOTNET_ROOT_X64 pointing to .tools\dotnet. Confirm the new app is responsive.
## Capture retention and permanent trash (September 7, 2026)

Production MainWindow enables CaptureHistoryStore at %LOCALAPPDATA%\BetterShot\History. Each GUID.capture archive atomically stores original.png plus versioned editable annotation metadata; settings.json stores retention. Default seven days; Settings offers 1, 7, 14, 30, or 90 days. Expiry uses creation UTC, not last edit, and runs at startup, after capture/setting changes, and each minute. Restored history appears quietly in Recent without floating thumbnails. No-argument MainWindow remains transient for diagnostics; tests must never modify real user history.

Lucide trash buttons on floating thumbnails and Recent cards delete immediately without confirmation. X still only dismisses a floating thumbnail. Deletion closes the editor, detaches queued persistence, removes the archive, and updates Recent/selection; failures keep the capture accessible with a status error. Pending inline text is committed and saved on normal shutdown. Saved external PNG copies are untouched; drag-export temp files retain their separate seven-day cleanup. Unknown or missing annotation fields reject an archive rather than silently losing redactions. Corrupt owned packages age out; unrelated filenames are ignored.

Final isolated build had zero warnings/errors. Fresh history, shell, self, and workflow checks passed under artifacts\retention-final. History checks cover all editable annotation rendering, exact expiry boundary, setting persistence, locked overwrite/deletion/expiry recovery, corrupt/missing-redaction isolation, deleting an open editor without resurrection, pending text on shutdown, and quiet restart recovery. Visually inspected thumbnail trash, dark Settings selector and popup, and minimum-size Settings. Evidence: history-settings.png, history-retention-options.png, history-thumbnail-trash.png, shell-settings-min.png. Run history-test.ps1 or artifacts\verify-retention.ps1 -RunName <fresh-name> -Regression. No exhaustive external drop-target or mixed-monitor testing was performed.
## Compact title bar (September 7, 2026)

User identified oversized title bar beside a standard Windows app. MainWindow custom caption and header row reduced from 60 to 32 DIP; caption buttons 46x32 with 12 DIP symbols (Settings16), app icon16, title12 regular, smaller margins. PerMonitorV2 DPI unchanged. Main content sizes unchanged. Build zero warnings/errors; fresh shell and history checks passed under artifacts\compact-chrome-final. Visually reviewed shell-empty.png. Installed executable/DLL/Lucide license hashes matched build; app relaunched successfully.

User explicitly clarified that UI complaints/improvement feedback mean implement, build, verify, and restart by default, unless they specifically say not to build/change it. This is saved in AGENTS.md. Do not pause at suggestions when a concrete fix is clear.
## Lighter main-shell controls (September 7, 2026)

User found the 64 DIP capture buttons visually oversized after caption refinement. Capture controls now use 40 DIP height, 190/160/160 DIP widths, 14 DIP labels, 16 DIP icons, and 10 DIP gaps. Primary action is one line; shortcut hint moved alongside as muted 12 DIP text and remains in tooltip. Removed resize-handler width/font/label overrides; full labels fit at minimum780 width. Quiet actions use 34 DIP height/13 DIP type; Edit/Copy/Save are88 DIP wide with16 DIP icons. Main content top spacing reduced to18 DIP. Caption remains32 DIP. Other library typography and editor/thumbnail behavior unchanged.

Zero-warning build and fresh shell/history diagnostics passed under artifacts\compact-buttons-final. Visually reviewed shell-empty.png and shell-populated-min.png. Installed exe/DLL/Lucide license hashes matched artifacts\compact-buttons-review; restarted app responsive. Rollback under artifacts\before-compact-buttons-restart.
## Thumbnail corner close (September 7, 2026)

Moved the dismiss X from bottom ActionTray to an always-visible top-right CloseButton overlay (28x28,16 DIP Lucide X). Existing Dismiss_Click preserves capture in Recent; trash stays in bottom hover toolbar for permanent deletion. Card pointer handlers already exclude button sources. Updated existing workflow check to invoke CloseButton and assert card hidden while history retained. Fresh build zero warnings/errors; workflow and history checks PASS under artifacts\thumbnail-close-final. Visually inspected workflow-thumbnail-hover.png. Installed exe/DLL/license hashes matched isolated build; restarted app responsive.
## Remember Win+Shift+S (September 7, 2026)

History settings now include optional backward-compatible ReplaceScreenshotShortcut (false when missing). Atomic setters preserve retention and shortcut together and only change in-memory saved values after successful writes. MainWindow restores saved choice after constructing the native hook; programmatic checkbox changes are guarded to avoid rewriting saved intent. Startup hook failure shows an error and preserves saved preference for retry next launch. Toggle changes save both on and off; write failures report that the runtime choice was not saved. No-argument transient diagnostics do not touch real settings. Settings text now says the choice is remembered across restarts. Hook remains process-local, with no registry/system policy modification.

Fresh history/self/shell diagnostics passed with zero-warning build. Checks cover legacy settings migration, retention/shortcut preservation, failed atomic settings write, actual native hook enabled after reopening and disabled after saving off. Visual review: artifacts\remember-shortcut-final\shortcut-restored-settings.png. Installed hashes match isolated build. User intent applied: real ReplaceScreenshotShortcut=true, existing RetentionDays=1 preserved; app restarted responsive. Supersedes earlier historical session-only shortcut notes.
## Start with Windows (September 9, 2026)

Launched BetterShot immediately for the user, then implemented Settings > Start with Windows. Optional backward-compatible StartWithWindows bool is saved alongside retention and shortcut override; all setters preserve other fields. Production initialization repairs the per-user Startup/BetterShot.lnk when enabled and removes it when disabled. Tests use isolated startup directories; history-only diagnostics do not touch the real Startup folder. Startup link launches hidden PowerShell encoded command setting DOTNET_ROOT/DOTNET_ROOT_X64, then installed BetterShot.exe --background; paths are escaped as literal PowerShell strings. Registration checks target/args/workdir and avoids rewriting unchanged links. Windows Task Manager startup approval is not overridden.

App --background initializes the native window handle, tray, capture hotkey and saved override without showing the main window. Duplicate background launches quietly exit. Manual launches still show the window. Settings errors are reported and failed toggle persistence attempts restore prior registration where possible.

Fresh zero-warning build, startup/history/shell diagnostics PASS under artifacts\windows-startup-final. Startup checks cover link create/no-op/delete, missing-entry repair, disable persistence, preference preservation, runtime/bootstrap argument checks, and hidden tray/native-handle initialization. Visually reviewed shell-settings-min.png. Installed executable/DLL/license hashes match isolated build. Actual user Startup link was created and inspected, and launching it while already running quietly left one app process. A real Windows sign-out/sign-in was not performed. Saved user values at installation: RetentionDays7, ReplaceScreenshotShortcuttrue, StartWithWindowstrue. New app PID35800 responsive. Service implementation: Platform/WindowsStartupRegistration.cs; verification: StartupDiagnostics.cs and artifacts\verify-windows-startup.ps1.
## Active shortcut hint (September 9, 2026)

MainWindow.ApplyShortcut updates CaptureShortcutText and AreaButton.ToolTip from the actual enabled hook state: Win + Shift + S when active, Ctrl + Shift + F9 otherwise. Updates after saved preference restoration and live checkbox changes; enable failure shows fallback. Extended existing history diagnostics to assert restored enabled label/tooltip and immediate fallback on disable. Fresh zero-warning build, history/shell checks PASS; visual evidence artifacts\shortcut-label-final\shortcut-main-enabled.png. Installed hashes match; app restarted responsive PID32388. Startup and saved preferences unchanged.
## First installer (September 9, 2026)

Created Inno Setup6.7.3 per-user installer0.1.0, self-contained win-x64 .NET10.0.11, stableAppId2D73E074-7582-4AAA-B5C7-5EB91C343A38. Build script build-installer.ps1 publishes fresh timestamped payload, bundles .NET/Lucide license notices, compiles installer and SHA256 sidecar. Definition installer/BetterShot.iss creates Start menu and optional desktop links, registers uninstall, blocks install/uninstall while app mutex exists (quit via tray Exit), removes only app-owned Startup link during uninstall and preserves user history/settings. No signing or auto-updater. Source app version is overridden to0.1.0 by packaging.

WindowsStartupRegistration detects bundled coreclr.dll and uses direct exe --background startup for standalone installs; framework-dependent development builds retain hidden runtime bootstrap. Startup diagnostic updated for both cases. Exact final payload passed self/history/startup/workflow/shell checks with DOTNET_ROOT variables removed. Actual install/reinstall/uninstall, installed executable tests, and original history/settings hash preservation passed. Final app installed to C:\Users\gimp3\AppData\Local\Programs\BetterShot and launched PID34456 responsive. Start menu, uninstall entry and startup direct target verified. Startup preference retained. Development copy stopped.

Installer57,809,569bytes; SHA2563D1FF6E944CA9435FC1B8645C5807C68F5ABA5B0DC4BEA0F6A23167414A89F36. Evidence artifacts/installer-final-payload-checks and installer-cycle-checks; packaged source folder recorded in artifacts/installer/latest-publish-path.txt. Compiler downloaded from officialGitHub release, signatureValid PyrsysB.V.; pinned hash in installer/README.md. Inno compiler is noncommercial tooling unless separately licensed for commercial use. No clean second-PC or sign-in test yet. Future updates should target installed copy, not blindly restart bin/Release development output.
## Automatic clipboard and image drag default (September 12, 2026)

CaptureAsync now awaits CompleteCaptureAsync: writes fresh image to Clipboard.SetImage before thumbnail/history presentation with up to5 COM-busy attempts, then presents normal thumbnail. Copy failure preserves capture and reports manual Copy retry, without opening editor. The injectable clipboard writer exists only for diagnostics; cancellation exits before this helper, so no stale clipboard writes.

CaptureDragData defaults to Bitmap+PNG memory data, autoconvertfalse, noFileDrop/text/path or temp export. File mode remains explicit CreateData(document,asFile:true), rendering a unique PNG to existing seven-day temp store and FileDrop+bitmap. Manager.DragAsFile is read live when dragging, so existing thumbnails follow Settings changes. Optional settings DragAsFilefalse migrates old preferences to image mode; every settings setter preserves it. Settings > Drag screenshots offers Image(default)/PNGfile; restores on startup. Added dark vertical scrollbar for expanded Settings.

Version0.1.1 self-contained installer built (zero warnings/errors). Sharing diagnostic tests immediate injected clipboard writes, COM retry and preserving captures on failure; self checks validate default image formats/PNG pixels and explicit file export/redaction snapshots; history checks verify drag mode persistence/live update; startup/workflow/shell PASS. No external drop target or actual Ctrl+V destination was automated; image-only drop support depends on receiving app, use PNGfile for file-only targets. Visual review artifacts\sharing-release-checks\shell-settings-min.png. Exact installer SHA2564719CFA43298336EBD6891117F5D258D7C4048C3007F3E6D44E76D21E59F3638,57,812,009bytes. Upgrade from0.1.0 preserved preexisting history/settings hashes before launch; installedexe/DLL/license match payload; restartedPID36000 responsive, real drag defaultfalse. Update targets installed app, not developmentbin.
## Slack-compatible image drops (September 12, 2026)

User reported image-only drag failed in Slack. Slack official docs describe file-drop upload (https://slack.com/help/articles/201330736-Add-files-to-Slack). Default Image(compatible) now offers Bitmap + PNG memory + unique rendered PNG CF_HDROP attachment, all autoConvertfalse. No Text/UnicodeText/FileName/URL formats are explicitly offered. File mode now offers only CF_HDROP. Both use existing seven-day managed temp export cleanup. Thumbnail preview falls back to Document.Render in file-only mode. Receiving targets still choose how to handle offered data; no automatic paste/input injection or Slack messaging was introduced.

Native OLE tests request FORMATETC CF_HDROP/TYMED_HGLOBAL from COM IDataObject and verify DragQueryFile yields the correct existing PNG; checks cover both modes, no textformats, image PNG redaction/pixel dimensions, unique immutable snapshots. Fresh self/sharing/history/workflow/shell PASS; Settings minimum layout visually checked. No live Slack workspace drop/upload was performed, so do not claim end-to-end Slack verification. Installed0.1.2 PID27128 responsive, hashesmatch. Installer57,812,485bytes SHA256BE5920A707A5BA472BAB2343D03815F2A826E2FBB9ED39966E3A2DCD1DE0D7B8. Evidence artifacts/slack-drag-checks and slack-drag-upgrade.log. This supersedes0.1.1 image-only mode.
## Always-on-top thumbnails (September 12, 2026)

User requested removing pin toggle and always keeping thumbnails on top. Removed PinButton, constructor pin styling and Pin_Click; retained Topmost=true. Hover tray now Edit/Copy/Save/Delete; cornerX stilldismisses. Existing workflow diagnostic asserts Topmost and renders simplified tray. Fresh0.1.3 installerbuild, workflow/historyPASS; visually reviewed artifacts\always-top-checks\workflow-thumbnail-hover.png. Installed hashesmatch; app PID42948 responsive from per-user installedpath. InstallerSHA256547FA0E7655FFE9D2496C15F08C2D16A842F9F053B9724EEE3CF9593D6BF28EA. Preference saved inAGENTS; do not reintroduce pin toggle.
September13: rebuilt installer0.1.3 from current source at user request. Self/workflow checks PASS; same SHA256547FA0E7655FFE9D2496C15F08C2D16A842F9F053B9724EEE3CF9593D6BF28EA. Reinstalled and relaunched installed appPID22104 responsive. Logs artifacts/installer-rebuild-sept13*. Latest payload path remains in artifacts/installer/latest-publish-path.txt.

## Window hover and single-click capture (September 15, 2026)

Normal Area capture now highlights eligible hovered app windows with a blue border/translucent blue fill. Single click captures the visible window bounds from the original frozen desktop; dragging switches to the existing yellow area selection and circular loupe. Windows drag thresholds are checked during movement and again on release, and crossing the threshold stays a drag even when returning near the start. No-target clicks leave selection open. Initial highlight is updated on overlay startup. Explicit Window mode uses the same blue highlight and ignores empty clicks. Native targeting excludes minimized/cloaked windows, BetterShot itself, desktop hosts, and taskbars. Existing GDI visible-pixel limitations remain: occluded content is not reconstructed.

SelectionSession holds shared gesture classification self-checks (threshold, release-only movement, sticky drag, window click). Workflow renders the actual SelectionOverlay on synthetic desktop imagery for hover and area selection. Final self-contained0.1.4 payload passed fresh self/overlay/workflow/sharing/history checks under artifacts\window-hover-release-checks; both selector previews visually inspected. These checks do not constitute exhaustive hands-on mouse or mixed-monitor testing. The capture result still uses normal automatic clipboard copying, persistent thumbnail presentation, and history.

Built with build-installer.ps1 -Version0.1.4; installer57,813,724bytes SHA256B8CE28A08BB7CD6E8EFBFE6996945632F169F1269C45F453A0160789DDEA12DC. Installed in place at %LOCALAPPDATA%\Programs\BetterShot; history/settings hashes unchanged through installation, installedexe/DLL/runtime/Lucide license hashes match verified payload. Restarted processPID25328 responsive. Rollback and installation evidence are under artifacts\window-hover-upgrade-*. Final verification runner artifacts\verify-window-hover.ps1 and update script artifacts\install-window-hover.ps1. Sandbox helper initialization failed this session; approved escalated PowerShell worked. Image inspection used local PNG bytes through exec because view_image encountered the same sandbox issue.

## Latest branch rebuilt and installed (October 7, 2026)

Fetched all remote branches. main remains at 4ba57b7; newest application code is f48ded8 on fix/live-drag-preview-and-shortcut-stale-keys, now checked out locally. User requested rebuilding and installing this latest code to try it. Built version 0.1.5 using build-installer.ps1 into artifacts/installer-publish-0.1.5-20261008034332. Installer artifacts/installer/BetterShot-0.1.5-Setup-x64.exe, SHA256 B5A3C57450995E398C5FBF84E4897A13F7C1C9B6B544020C8E0E5C350546AE1D. Build succeeded with two nullable diagnostics (CS8600/CS8604 at EditorDiagnostics.cs:42, repeated for the WPF temporary project). Source code unchanged.

Fresh self, overlay, workflow, sharing, and history checks passed in artifacts/oct07-0.1.5-checks. Editor render visually inspected. Desktop automation initialization failed with sandbox setup-refresh error, so live mouse/keyboard testing was not performed. Used existing install-window-hover.ps1 -Version 0.1.5 to preserve rollback, replace the installed per-user copy, and restart. History/settings hashes unchanged through installation; installed executable/DLL/runtime/Lucide notice hashes match verified payload. Installed process PID30616 was responsive. No GitHub release or remote branch update was made for this local trial build.
## Editor mouse zoom and Space panning (October 7, 2026)

Installed version 0.1.6 adds Ctrl+wheel zoom anchored around the pointer (10%-400%) and Space-held hand-cursor/left-drag canvas panning. The existing annotation tool is retained; Space release, Escape and window deactivation end navigation. Mouse capture loss ends the active pan. Inline TextBox input keeps normal spaces. Wheel zoom cancels active annotation/text-handle gestures before changing scale. Footer controls and 0-to-fit remain available. Implementation is in Editing/EditorWindow.Navigation.cs with editor event wiring; README documents usage.

Built with build-installer.ps1 -Version 0.1.6 into artifacts/installer-publish-0.1.6-20261008035155. Build succeeded with existing nullable warnings in EditorDiagnostics.cs. Installer SHA256 06D709BD9AB5A4A716EB44A09A77031872AF2D8B57107EB392109B528FF52F54. Fresh self/overlay/workflow/sharing/history checks passed in artifacts/navigation-0.1.6-final, including pointer anchor across fit-to-overflow, two-axis pan offsets, text-input guard, and navigation cleanup. Inspected rendered editor with text handles. These programmatic diagnostics do not replace live mouse interaction testing; desktop automation was unavailable in this session.

Closed the running editor before installation to commit pending text. Existing installer update script preserved history/settings hashes, retained a rollback copy, and verified installed executable/DLL/runtime/Lucide license hashes. Installed 0.1.6 running from the per-user Programs/BetterShot directory, PID31516 responsive at verification. Source changes are on fix/live-drag-preview-and-shortcut-stale-keys; no GitHub release was published.
## Proper grab cursors (October 7, 2026)

Installed 0.1.7 replaces the Windows link-pointer hand during Space navigation with original embedded open-grab and closed-grabbing cursors. Open palm while Space held, closed fist while panning; mouse release, wheel interruption, and capture loss restore open grab, and Space release/Escape/deactivation restore normal cursor. Assets/Cursors contains 32/48/64-pixel CUR frames and their generator. Matching hotspots, transparent AND masks, and DPI-scaled loading are included. Revised artwork visually reviewed against light/dark backgrounds at artifacts/cursor-preview.png. Workflow diagnostic verifies cursor states and passed against exact final payload in artifacts/grab-cursor-0.1.7-final; build succeeded with existing nullable warnings only.

Installer artifacts/installer/BetterShot-0.1.7-Setup-x64.exe SHA256 3C25F54FFD00E8632AB8C04AABE2F0C3C6C03FC2C42426AAB843D21656822530. Saved pending editor text by closing the editor before update. Installed payload hashes match, history/settings preserved, restarted installed app PID52472 responsive. The user authorized committing and pushing the editor navigation and cursor changes on October 7, 2026. Installer 0.1.7 remains local; no new GitHub release was published.