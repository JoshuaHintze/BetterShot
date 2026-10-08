# BetterShot

BetterShot is a compact Windows screenshot utility built with WPF on .NET 10. It captures an area, a window, or a display, automatically copies the capture to the clipboard for immediate paste, and leaves a persistent thumbnail at the lower left of that display. Click the thumbnail to annotate the same editable capture, drag its image into an app that accepts file attachments, or hover it for edit, copy, save, dismiss, and permanent delete actions. The main window stays out of the way after capture and remains available from the notification area.

## Build and run

Install the .NET 10 SDK on Windows, then run:

```powershell
.\build.ps1
.\run.ps1
```

After the Release build succeeds, `BetterShot.cmd` is a double-click launcher. It points the Windows app host at the project-local `.tools\dotnet` runtime when present, which also allows BetterShot to run without a machine-wide .NET installation. This checkout already includes that local SDK/runtime after setup.

Run `.\self-test.ps1` to verify shortcut handling, capture geometry, the editable document model, and rendered annotations. Run `.\smoke-test.ps1` to capture the display noninteractively and save `artifacts\smoke-test.png`. `.\workflow-test.ps1` creates a synthetic annotated document, verifies that capture opens a thumbnail without opening the editor or shell, closes and reopens the shared document, and renders the normal and hover thumbnail states, editor, and capture loupe under `artifacts\workflow-*.png`. `.\diagnostic-test.ps1` runs the overlay, shell-render, and workflow diagnostics together. These diagnostics inject no keyboard or mouse input.

Captures and editable annotations are stored locally in `%LOCALAPPDATA%\BetterShot\History` for seven days by default. In **Settings > Capture history**, choose 1, 7, 14, 30, or 90 days. The limit starts at capture time; editing does not extend it. Expiry runs at startup, after captures or retention changes, and every minute while running. Shortening the limit removes older captures immediately. Hover a floating thumbnail or Recent card and click the trash icon to delete it permanently, including its stored history and open editor. Explicitly saved PNG copies are untouched. History returns in Recent after restart, without reopening floating thumbnails. Undo/redo history is session-only. Up to four thumbnails are shown per display, or fewer when the working area is short; older captures remain accessible from **Recent captures** in the main window. The top-right X dismisses the floating thumbnail while leaving its document in Recent. The trash action stays in the bottom hover toolbar and permanently deletes the capture. Floating thumbnails always stay on top; there is no pin toggle.

## Window capture from screenshot mode

Start an area capture using the main button or capture shortcut. Hover over an app window to see a blue outline and translucent blue fill, then click once to capture its visible window bounds. Drag instead to select a custom area. The highlight stays out of the captured image, which is copied to the clipboard and shown as a persistent thumbnail as usual. Press Esc to cancel.

## Shortcuts

`Ctrl+Shift+F9` captures an area whenever BetterShot is running. The main window also offers an explicit **Use Win+Shift+S for BetterShot** option. BetterShot remembers this preference and reapplies it when the app restarts. Turning it off is remembered too. The main capture shortcut hint and area-button tooltip show Win+Shift+S when the override is active and Ctrl+Shift+F9 otherwise. That option installs a process-local low-level keyboard hook and suppresses only the `S` press/release for the exact Win+Shift+S chord. It makes no registry or policy changes and is disabled when BetterShot exits. If the hook cannot be installed, the global hotkey and notification-area menu remain available.

## Current limits

Capture uses Windows GDI and returns a frozen WPF bitmap. It captures SDR pixels from the visible desktop, so HDR color, protected content, hardware overlays, occluded/minimized windows, and some elevated or special Windows surfaces may be missing or differ from what is displayed. Area selection and window capture operate on what is visibly composited at capture time. History is local only; BetterShot does not upload captures. Captures that cannot be stored remain available for the session, with an error in the status area.

BetterShot is single-instance per signed-in Windows session. Closing the window leaves it running in the notification area; choose **Exit** from the tray menu to quit fully.

## Thumbnail drag and drop

Drag from the image area to share the current edited capture. **Settings > Drag screenshots** defaults to **Image (compatible)**, which offers bitmap/PNG image data plus a rendered PNG file attachment for Slack and other upload targets. It does not advertise text or URL paths. Choose **PNG file** to offer only the file attachment. The preference is saved across restarts and applies to existing thumbnails too. A translucent preview follows the pointer while preserving the normal Windows copy/unavailable cursor. The preview is click-through and closes on drop or cancellation. A normal click still opens the editor; the hover action buttons keep their existing behavior. Drop targets must support image/file drops (a plain text field may not). Windows supplies the copy or unavailable cursor based on the target. PNG attachments for both modes are stored in `%TEMP%\BetterShot\DragExports` so receiving apps can read them after the drop; exports older than seven days are cleaned up on a later drag.

## Editor layout and branding

The editor uses a single compact toolbar with vector tool icons and Copy, Save, and Done actions. Fit and zoom controls live in the footer. Workflow diagnostics verify the toolbar fits without overlapping at 780 and 1180 DIP window widths and save previews for each. The app and tray use the generated BetterShot icon in `BetterShot/Assets`.

Hold Ctrl and roll the mouse wheel to zoom around the point under the pointer. Hold Space to show the hand cursor, then drag with the left mouse button to pan the canvas; releasing Space returns to the selected annotation tool. Spaces typed in the inline text editor remain normal text input.

## Inline text editing

Choose Text (T), click the image, and type directly on the canvas. Enter inserts a new line; Ctrl+Enter finishes and Escape cancels. Clicking outside the text or leaving the editor commits the edit. Copy, Save, and Done also commit pending text. Double-click selected text to edit it again. In Select mode, drag text to move it; the four corner handles scale its font size. Each completed move, resize, or text edit is one undo step. Empty new text is discarded; clearing existing text removes it and can be undone. Selection handles and the editing box are excluded from exported images.

## Main window library

The main window is a compact charcoal capture library with native resize, drag, minimize, maximize, and close-to-tray behavior. Its custom title bar uses compact desktop proportions: 32 DIP height, 16 DIP app icon, and 12 DIP title text. Area capture is the primary action, with window and display capture beside it. The selected screenshot appears as a large preview with Edit, Copy, and atomic PNG Save actions. Recent visual cards adapt to the window width, with four fitting at normal widths and horizontal scrolling for additional captures. Previews refresh when annotations change. Capture controls use compact 40 DIP buttons with 14 DIP labels and 16 DIP icons; the area shortcut sits beside the controls. Edit, Copy, and Save use lighter 34 DIP buttons. The main window starts at 1100 x 900 and fits itself within the monitor work area.

The saved Win+Shift+S override lives in the Settings panel alongside the Ctrl+Shift+F9 fallback. Settings includes capture retention, reports the running assembly version and displays the bundled Lucide open-source license. Closing the main window still hides BetterShot to the notification area.

Run .\shell-test.ps1 to render and verify the library, Settings, and close-to-tray lifecycle using synthetic captures. Pass -AppPath to test a separate build without replacing the running app. Diagnostic images and logs stay under artifacts\shell-test by default.

Run .\history-test.ps1 to verify editable history, retention boundaries, settings persistence, locked-file handling, immediate deletion, and restart restoration using isolated synthetic history.

## Start with Windows

Settings > Start with Windows starts BetterShot quietly in the notification area when you sign in to your Windows account. The saved capture shortcut is initialized too. Turning the option off removes BetterShot from your Startup folder. When enabled, BetterShot checks its startup shortcut on normal launches and repairs a missing or stale entry. Windows Task Manager can independently disable startup apps; BetterShot does not override that Windows control.

The per-user Startup folder contains BetterShot.lnk. It launches a hidden Windows PowerShell command that initializes the bundled .NET runtime and starts BetterShot with --background. No administrator access is needed. This is Windows sign-in startup, not a Windows service. Run artifacts\verify-windows-startup.ps1 -RunName <fresh-name> -Regression for isolated startup, history, and shell checks.
## Windows installer

First installer: `artifacts\installer\BetterShot-0.1.0-Setup-x64.exe`. It includes .NET, installs for the current Windows account under `%LOCALAPPDATA%\Programs\BetterShot`, adds a Start menu shortcut and optional desktop shortcut, and appears in Windows Installed apps for removal. Capture history/settings are preserved on uninstall. Quit using the tray menu before installing or uninstalling. This first build is unsigned and has no automatic update mechanism. Rebuild with `.\build-installer.ps1`; see `installer\README.md` for verification and compiler setup.

The installed self-contained app registers Windows startup directly to its executable with --background. The hidden PowerShell runtime bootstrap described earlier is used only for framework-dependent development builds.
Version0.1.1 fixes automatic copy on capture and adds image/file drag selection. Clipboard contention is retried; failure preserves the capture and offers manual Copy retry. Drop target support varies: the receiving app chooses which offered format to use. Image mode includes a PNG attachment for file upload targets; PNG file mode restricts the payload to the attachment.
