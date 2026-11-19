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

Captures and editable annotations are stored locally in `%LOCALAPPDATA%\BetterShot\History` for seven days by default. In **Settings > Library**, choose 1, 7, 14, 30, 90, or 180 days, or Never (kept until you delete them). The limit starts at capture time; editing does not extend it. Expiry runs at startup, after captures or retention changes, and every minute while running. Shortening the limit removes older captures immediately. Hover a floating thumbnail or Recent card and click the trash icon to delete it permanently, including its stored history and open editor. Explicitly saved PNG copies are untouched. History returns in Recent after restart, without reopening floating thumbnails. Undo/redo history is session-only. Up to four thumbnails are shown per display, or fewer when the working area is short; older captures remain accessible from **Recent captures** in the main window. The top-right X dismisses the floating thumbnail while leaving its document in Recent. The trash action stays in the bottom hover toolbar and permanently deletes the capture. Floating thumbnails always stay on top; there is no pin toggle.

## Window capture from screenshot mode

Start an area capture using the main button or capture shortcut. Hover over an app window to see a blue outline and translucent blue fill, then click once to capture its visible window bounds. Drag instead to select a custom area. The highlight stays out of the captured image, which is copied to the clipboard and shown as a persistent thumbnail as usual. Press Esc to cancel.

## Shortcuts

`Ctrl+Shift+F9` captures an area whenever BetterShot is running. The main window also offers an explicit **Use Win+Shift+S for BetterShot** option. BetterShot remembers this preference and reapplies it when the app restarts. Turning it off is remembered too. The main capture shortcut hint and area-button tooltip show Win+Shift+S when the override is active and Ctrl+Shift+F9 otherwise. That option installs a process-local low-level keyboard hook and suppresses only the `S` press/release for the exact Win+Shift+S chord. It makes no registry or policy changes and is disabled when BetterShot exits. If the hook cannot be installed, the global hotkey and notification-area menu remain available.

**Settings > Capture** also has a recorder for each configurable global shortcut: Area, Window, Fullscreen (the whole display under the pointer, captured immediately), and Open library. Click a recorder, press the keys, Esc cancels, Backspace clears. Shortcuts use `RegisterHotKey`; a combination Windows or another app already owns is reported and not saved. Combinations without Ctrl, Alt, or Win (except Print Screen, Pause, F13-F24, or Shift+F-keys) are rejected so typing is not intercepted. Ctrl+Shift+F9 stays reserved as the built-in area fallback. New shortcuts start unassigned.

## Capture options and saving

- **Delay before capture**: Off, 3, 5, or 10 seconds. A small, click-through countdown appears at the bottom of the display under the pointer; it never takes focus, so menus opened in other apps stay open. Trigger any capture again to cancel the countdown.
- **After capture**: Copy to clipboard (on), Show floating thumbnail (on), Open editor (off), Play capture sound (on; a short shutter click synthesized in code, no audio asset), and Auto-dismiss thumbnails after Never/10/30/60 seconds. Hovering or focusing a thumbnail pauses its countdown; dismissed captures remain in the library.
- **Saving**: Save folder (default `%USERPROFILE%\Pictures\BetterShot`, following a redirected Pictures library), Choose and Open save folder buttons, and an optional "Save every capture". With the floating thumbnail turned off, every capture is saved to the folder. File names use a pattern with `%date` (2026-08-05), `%time` (10.22.31), `%year`, `%month`, `%day`, `%hour`, `%minute`, `%second`, and `%counter`, with a live preview. Characters Windows forbids become dashes, reserved device names are prefixed, and existing files are never overwritten (`name (2).png`, `name (3).png`, ...).
- **Format and size**: PNG or JPEG (quality 10-100) and Full size / Fit within 1920 px / Fit within 1280 px. These apply to saved files (auto-save and Save dialogs) and dragged images. Dragged image data keeps its in-memory PNG format and only the dropped file follows the JPEG choice. Clipboard copies stay full size unless "Also resize clipboard copies" is on. The library always keeps the full-resolution original.

Run .\capture-settings-test.ps1 to verify templating, sanitizing, collisions, resize math, PNG/JPEG export, settings migration and preservation, hotkey validation, the shutter sound format, thumbnail-off auto-save, and auto-dismiss, using isolated fixtures. It renders `capture-settings-panel.png`, `capture-settings-full.png`, and `capture-countdown.png`.

## Ask Claude

The sparkles button on a floating thumbnail's hover tray, or **Ask Claude** under the selected capture in the main window, opens "Ask about this capture". Pick a preset (Describe, Explain error, Draft bug report, Find UI issues, Extract text, Write alt text) or type a question (Enter sends, Shift+Enter adds a line). Answers stream in with basic Markdown formatting; ask follow-ups in the same conversation, start a new conversation, or copy the latest answer. Esc or the stop button cancels an answer in progress; closing the window cancels it too.

Nothing is sent until you click a preset or send a question. The rendered capture (with annotations), downscaled so its long edge is at most 1568 px, is sent as PNG with the first question only; follow-ups send the text history. Requests go only to `https://api.anthropic.com/v1/messages` with your own key (redirects are refused).

Settings > Ask Claude holds the API key and model (Claude Sonnet 5.5, the default; Claude Opus 5.5; Claude Haiku 5.5). The key is encrypted with Windows data protection (DPAPI, current user) in `%LOCALAPPDATA%\BetterShot\ask-claude-key.bin`, never in settings.json, and is never shown again after saving; Remove deletes it. The model is saved as `AiModel` in settings.json. Other code can open the window with `AskClaudeWindow.Show(owner, renderedBitmap, title)`.

Run .\ai-test.ps1 to verify the request format, streaming parser, error messages, timeouts, conversation history, image downscaling, model setting, and key storage using a fake HTTP handler (no network). It renders `ai-settings-panel.png`, `ai-window-empty.png`, and `ai-window-conversation.png`.
## Scrolling capture

Click **Scrolling** in the main window, choose **Scrolling capture** from the tray menu, or assign a shortcut in Settings > Capture. Drag over the scrollable part of a page or click a window. BetterShot scrolls the content under the center of the selection with mouse-wheel input (falling back to UI Automation), captures each step, and stitches the frames by matching their overlap. Title bars, sticky headers, and status bars that stay put appear once. It stops at the end of the content, at 20,000 px, or when you press Esc or click **Stop and keep what's captured** (that pill is excluded from the capture). The result is a normal capture with a thumbnail, library entry, and editor. If nothing moves, BetterShot reports "Nothing scrolled". Apps running as administrator ignore synthesized input from a normal app and cannot be scrolled.

## Background removal

In the editor, open **Background** and choose **Remove the background**. A small salient-object model (U²-Netp, Apache-2.0) runs on this PC through ONNX Runtime (MIT); screenshots are never uploaded. The first use asks before downloading the 4.4 MB model from the rembg GitHub release into `%LOCALAPPDATA%\BetterShot\Models`, and the file is checked against a pinned SHA-256. The removal is a mask stored with the capture, so Undo or **Restore the background** brings the original back. PNG copies, saves, and drags keep the transparency; JPEG exports fill it with white.

## First-run setup and updates

A new install opens **Welcome to BetterShot** once: shortcuts (Win+Shift+S and the capture recorders), start with Windows, and a first screenshot. Every step can be skipped. Settings > Updates checks GitHub Releases for JoshuaHintze/BetterShot once a day (never while capturing) and has a **Check for updates** button. A newer release shows its notes with Download, Skip this version, and Later; the installer and its `.sha256` file are downloaded to `%LOCALAPPDATA%\BetterShot\Updates`, verified, and run only after you click **Install now**.

Run `.\extras-test.ps1` (optionally `-Area scrolling|background-removal|update|onboarding`) to check stitching with synthetic frames, background-removal math, mask undo/persistence, update parsing and verified downloads with a fake HTTP handler, and first-run settings, and to render the new windows. It never downloads anything; model inference runs only if the model is already installed.

## Current limits

Capture uses Windows GDI and returns a frozen WPF bitmap. It captures SDR pixels from the visible desktop, so HDR color, protected content, hardware overlays, occluded/minimized windows, and some elevated or special Windows surfaces may be missing or differ from what is displayed. Area selection and window capture operate on what is visibly composited at capture time. History is local only; BetterShot does not upload captures, except the single capture you explicitly ask Claude about (see Ask Claude). Captures that cannot be stored remain available for the session, with an error in the status area.
## Screen recording

Click **Record** (or press the **Record screen** shortcut from Settings > Recording) and drag a region, or click a window to record its area. The arrow next to Record offers **Record a window** (follows that window) and **Record a display**. The tray menu has the same choices under **Record screen**. After an optional countdown (Off, 3 or 5 seconds), a small floating bar shows the elapsed time with pause/resume, microphone mute, camera toggle, and stop; for region recordings the rest of the screen is dimmed. The bar and the dimming never appear in the video (Windows 10 version 2004 or later). Stop with the bar, the Record button, the tray, or the shortcut.

Recordings are H.264/AAC MP4 files saved straight to the save folder using the recording file-name pattern (default `Recording %date at %time`). Settings > Recording also controls microphone (with device choice), system audio (mixed into the same track), webcam (saved beside the movie as `<name>-camera.mp4`), click highlights, shortcut keystrokes (only combinations with Ctrl, Alt or Win; ordinary typing is never shown or saved), 30/60 fps, and whether to open the recording editor afterwards. Clicks and shown shortcuts are also written to a hidden `.<movie file>.bettershot-events` JSON file and appear as markers on the editor timeline. Finished recordings appear in **Recent recordings** in the main window and as a floating card at the lower left; play, show in folder, drag the MP4 into another app, or move it to the Recycle Bin. If Windows privacy settings block the microphone or camera, BetterShot offers to open the right Settings page. If a recording cannot be finished, the unfinished file is kept and its location is shown.

## Recording editor

Turn on **Review and trim after recording** (Settings > Recording), or use the scissors **Edit** button on a recording card or floating recording thumbnail. The editor plays the movie (Space; Left/Right or `,`/`.` step one frame; Home/End; Shift+I/Shift+O jump to the in/out points; 0.5x-2x speed; loop the selection) above a timeline you can zoom (Ctrl+wheel, footer buttons, **Fit the whole recording**) and resize (drag the bar above it; double-click resets). Editing keys follow CapCut: **B** turns on split mode (every click on the clip track cuts it), **Ctrl+B** splits at the playhead, **I**/**O** set in/out points, **Q** deletes from the clip start to the playhead, **W** from the playhead to the clip end, **Delete** removes the selected clip or mark, **N** toggles snapping, Ctrl+Z/Ctrl+Y undo and redo. Cut stretches stay on the timeline, hatched, so **Put the selected cut back** restores one and **Put the recording back the way it was** removes every cut.

Markup (text, arrow, rectangle, ellipse, line, highlighter, callout, step numbers, spotlight, and solid/pixelate/blur redaction) is drawn on the frame at the playhead and gets a bar on the timeline that starts there and lasts 3 seconds; drag the bar or its ends to change that. Show every mark on its own row or fold them into one. **Follow** makes a redaction move with what it covers (template matching between frames; the diamonds on its bar are where it changed course). If it loses the field, the status says when; depending on **When it loses the field** it stops following or blurs the whole picture while lost (in the player and the export). Drag the box back over the content and choose **Follow again from the playhead**. The eye button hides markup in the player and the export; redactions always stay covered.

Edits are saved in a hidden `.<movie file>.bettershot-edit` JSON file beside the movie, so they reopen editable; the movie itself is untouched. **Export** writes an MP4 (the kept stretch, audio included), an MP4 without audio, a looping GIF (frame rate and size in Settings > Recording), or a timelapse (sped up to about 30 seconds or 2x/4x/8x/16x; audio is left out). Markup is drawn into exports. **Save trim** writes the kept stretch to a movie: beside the original as `<name> (trimmed).mp4`, or replacing the original when **Save trim replaces the original** is on; marks move with it. With **Keep a marked copy for sharing** on (default), dragging an edited recording shares a rendered copy (kept under `%LOCALAPPDATA%\BetterShot\History\MarkedCopies`); a recording with a redaction always shares its covered copy and is never dragged out unredacted. **Done** saves and refreshes that copy; closing with unsaved edits asks **Leave without saving?**

Run `.\recording-editor-test.ps1` to verify the cut/split/in-out math, keys, marks, tracking on synthetic frames, the project file, sharing rules, GIF/timelapse planning, and burned-in redactions; on Windows it also encodes a synthetic clip (no desktop content) and exports a trim, a redacted MP4, a GIF, and a timelapse from it, and renders `recording-editor.png`.

Run `.\recording-test.ps1` to verify recording settings, file names, crop/even-size/DPI math, the event sidecar, the recording state machine, audio mixing, the recordings list, and Settings rendering; on Windows with graphics capture available it also records the primary display for two seconds into `artifacts\recording-test` and checks that the MP4 is playable. Keep those files local; they show your desktop.

## Current limits

Screenshot capture uses Windows GDI and returns a frozen WPF bitmap (screen recording uses Windows.Graphics.Capture). It captures SDR pixels from the visible desktop, so HDR color, protected content, hardware overlays, occluded/minimized windows, and some elevated or special Windows surfaces may be missing or differ from what is displayed. Area selection and window capture operate on what is visibly composited at capture time. History is local only; BetterShot does not upload captures. Captures that cannot be stored remain available for the session, with an error in the status area.

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

The saved Win+Shift+S override lives in the Settings panel alongside the Ctrl+Shift+F9 fallback. Settings includes capture shortcuts, delay, after-capture options, saving and export format, library retention, reports the running assembly version and displays the bundled Lucide open-source license. Closing the main window still hides BetterShot to the notification area.

Run .\shell-test.ps1 to render and verify the library, Settings, and close-to-tray lifecycle using synthetic captures. Pass -AppPath to test a separate build without replacing the running app. Diagnostic images and logs stay under artifacts\shell-test by default.

Run .\history-test.ps1 to verify editable history, retention boundaries, settings persistence, locked-file handling, immediate deletion, and restart restoration using isolated synthetic history.

## Start with Windows

Settings > Start with Windows starts BetterShot quietly in the notification area when you sign in to your Windows account. The saved capture shortcut is initialized too. Turning the option off removes BetterShot from your Startup folder. When enabled, BetterShot checks its startup shortcut on normal launches and repairs a missing or stale entry. Windows Task Manager can independently disable startup apps; BetterShot does not override that Windows control.

The per-user Startup folder contains BetterShot.lnk. It launches a hidden Windows PowerShell command that initializes the bundled .NET runtime and starts BetterShot with --background. No administrator access is needed. This is Windows sign-in startup, not a Windows service. Run artifacts\verify-windows-startup.ps1 -RunName <fresh-name> -Regression for isolated startup, history, and shell checks.
## Windows installer

First installer: `artifacts\installer\BetterShot-0.1.0-Setup-x64.exe`. It includes .NET, installs for the current Windows account under `%LOCALAPPDATA%\Programs\BetterShot`, adds a Start menu shortcut and optional desktop shortcut, and appears in Windows Installed apps for removal. Capture history/settings are preserved on uninstall. Quit using the tray menu before installing or uninstalling. This first build is unsigned and has no automatic update mechanism. Rebuild with `.\build-installer.ps1`; see `installer\README.md` for verification and compiler setup.

The installed self-contained app registers Windows startup directly to its executable with --background. The hidden PowerShell runtime bootstrap described earlier is used only for framework-dependent development builds.
Version0.1.1 fixes automatic copy on capture and adds image/file drag selection. Clipboard contention is retried; failure preserves the capture and offers manual Copy retry. Drop target support varies: the receiving app chooses which offered format to use. Image mode includes a PNG attachment for file upload targets; PNG file mode restricts the payload to the attachment.
