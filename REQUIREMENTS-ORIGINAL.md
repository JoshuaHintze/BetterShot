> Historical planning document preserved September 7, 2026. Early statements below about implementation not starting, an undecided name/stack, and the initial prototype scope are superseded. Read SESSION-HANDOFF.md for current status.

# KhingShot for Windows — feature requirements

Status: initial draft for review; implementation has not started.  
Date: September 6, 2026.  
Working name: KhingShot for Windows; final name to be decided.  
Technology stack: intentionally undecided.

## Current prototype scope — September 6, 2026

The user has selected a screen-capture-only prototype first. Video capture and editing are deferred; the full parity requirements below remain the longer-term backlog.

Prototype acceptance: run in the Windows tray; test replacement of Win + Shift + S; capture an area, window, or display; provide the 8× selection loupe; handle mixed-DPI monitors; copy the result to the clipboard and save a PNG; allow cancellation and reversible shortcut configuration. Exclude annotation editing, video recording/trimming, OCR, Claude, HEIC, and automatic updates from this first experiment.

Recommended stack, pending prototype validation: C# with .NET 10 LTS, WPF for the desktop UI and selection overlays, Win32 interop for Windows integration, and Windows.Graphics.Capture with Direct3D interop as the initial capture backend to evaluate. Keep capture and shortcut services separate from the UI so later recording and editing can be added without replacing the application shell.

Supporting documentation: [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/), [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), and [Windows screen capture APIs](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/screen-capture).

## 1. Product goal

Build a fast, dependable Windows desktop application with all the capabilities of the Mac KhingShot application. It should become the user's everyday capture tool: invoke a shortcut, capture, annotate, and copy or drag the result into another application with very little friction.

Replacing **Win + Shift + S** is a high-priority preference and an early feasibility gate. A different shortcut is a fallback to review if Windows prevents reliable replacement, not an assumed equivalent outcome.

## 2. Evidence and scope

The [KhingShot website](https://khingshot.khingsuite.com/) was retrieved on September 6, 2026 and advertised version **0.13.0**. Its advertised features establish the minimum parity baseline below. The installed Mac application and embedded promotional video have not been examined; the website alone cannot establish exhaustive behavioral parity.

Requirement labels:

- **Parity:** capability explicitly advertised on the website; required for the complete product.
- **Windows:** adaptation needed for a good Windows experience.
- **Proposed:** additional detail or measurable target proposed in this draft, not a claim about the Mac application.
- **Discovery:** behavior needing inspection or a technical prototype before final commitment.

All parity capabilities are in scope, including recording and Claude integration. Build stages describe delivery order, not permission to omit features.

## 3. Feature requirements

| ID | Origin | Requirement and acceptance condition |
| --- | --- | --- |
| CAP-01 | Parity | Capture a selected rectangular area, a selected window, or a full screen. Each mode is available through a global shortcut and the tray menu. Captures preserve the selected content at native pixel resolution. |
| CAP-02 | Parity | Display an **8× magnifying loupe** during area selection. The loupe must accurately show the pixels under the selection point. |
| CAP-03 | Parity | Highlight a candidate window before the user clicks to capture it. The highlight must not appear in the resulting image. |
| CAP-04 | Windows / Proposed | Support multiple displays, portrait orientation, displays positioned left or above the primary display, and mixed display scaling. Define one-display versus entire-desktop capture explicitly; propose offering both. |
| CAP-05 | Proposed | Show selection dimensions, allow Escape to cancel, and restore normal input immediately after capture or cancellation. Never include our selection overlay or toolbar in a screenshot. |
| EDT-01 | Parity | Open each screenshot ready to annotate without requiring a separate import or editor-launch step. |
| EDT-02 | Parity | Provide arrows, text, blur, cropping, and numbered steps. Each tool must work on any supported screenshot size. |
| EDT-03 | Parity | Reopen saved captures with their annotations still editable. Retain the original image and editable document state separately from flattened exports. Restarting the application must not discard markup. |
| EDT-04 | Proposed | Support selecting, moving, editing, and deleting annotations; undo/redo; zoom; and appropriate color, text-size, and stroke controls. Save editable state reliably after changes. |
| EDT-05 | Proposed | Add an opaque redaction tool for sensitive content. Shared exports contain only the flattened visible result, never original pixels or editable project data. Blur remains available as a visual effect. |
| OCR-01 | Parity | Extract text from screenshots on the device and copy the recognized text in one action. This must work without an account or internet connection once required local components are installed. |
| OCR-02 | Proposed / Discovery | Preserve useful line breaks and reading order. Provide a clear result when no text is found. Establish supported languages and benchmark accuracy using actual screenshots before selecting the OCR engine. |
| FLT-01 | Parity | Show captures as floating previews and allow screenshots to remain pinned on screen as references. |
| FLT-02 | Parity / Discovery | Match the advertised behavior in which floating captures follow the cursor across displays. Inspect the Mac application to define when previews relocate versus when an explicitly pinned reference stays put. |
| EXP-01 | Parity | Drag a capture into another application as a real image file in **PNG, JPEG, or HEIC**, at the configured output size. Changing the export format or size must not destroy the original. |
| EXP-02 | Proposed | Provide Copy Image, Save As, and Open Containing Folder. Copy and drag operations must include current annotations and cropping. Preserve aspect ratio by default when resizing. |
| EXP-03 | Windows / Discovery | Validate HEIC encoding, required components, and drag-and-drop interoperability early. Never silently substitute another format; clearly identify unavailable encoding support. HEIC remains a parity requirement until a scope change is agreed. |
| REC-01 | Parity | Record screen content using H.264. The exact recording source choices are unverified; propose area, window, and display recording to align with screenshot capture. |
| REC-02 | Parity | Capture system audio and microphone audio, with the microphone retained on its own track. Verify the saved recording contains independently recoverable audio tracks. |
| REC-03 | Windows / Proposed | Provide independent audio toggles, microphone selection, an obvious recording indicator, elapsed time, and an accessible stop action. Explain permission or device failures before silently omitting requested audio. |
| REC-04 | Parity | Provide a filmstrip with start/end trimming and lossless video output. Preserve the source recording and audio synchronization. |
| REC-05 | Discovery | Prototype lossless trimming around H.264 keyframes. If exact arbitrary-frame cuts require re-encoding, distinguish exact cuts from lossless cuts and show the effective boundaries; never label re-encoded output lossless. |
| REC-06 | Parity | Export recordings as GIFs suitable for sharing in Slack. |
| REC-07 | Proposed | Let users choose GIF dimensions and frame rate, preview the result, and see estimated or final file size. Propose MP4 as the default video container; verify preservation of separate audio tracks. |
| AI-01 | Parity | Integrate Claude to describe a capture, explain an error, or draft a bug report from an image. |
| AI-02 | Parity / Windows | Use the user's API key and store it in protected Windows credential storage. Never store the key in plaintext settings, exported images, editable capture documents, or logs. |
| AI-03 | Parity | Send image content to Claude only in response to an explicit user action. Core capture, recording, editing, OCR, and export must remain usable without Claude or a key. |
| AI-04 | Proposed | Support a custom prompt, copying the answer, and clear handling of invalid keys, connectivity problems, and provider errors. Send the current flattened image so applied crops and redactions are respected. |
| DAT-01 | Parity | Store captures in a user-selected local folder. Require no product account and collect no analytics. Do not upload captures automatically. |
| DAT-02 | Proposed | Offer a simple recent-captures view with reopen, copy, reveal in Explorer, and delete. Clearly distinguish deleting an editable capture from deleting a separately exported file. |
| DAT-03 | Windows / Proposed | Handle unavailable folders, permission errors, full disks, and filename collisions without losing an existing capture. Keep API keys outside the capture folder. |
| APP-01 | Parity / Windows | Replace the Mac menu-bar presence with a Windows system-tray application. Expose capture modes, recording, recent captures, settings, and Quit. |
| APP-02 | Proposed | Offer launch at sign-in and a single running instance. Closing the editor must not unexpectedly terminate the tray application or an active recording. |
| UPD-01 | Parity / Windows | Check for updates daily in the background and offer one-action installation. Provide an equivalent Windows release-signing and verification process. |
| UPD-02 | Proposed | Verify update authenticity, defer installation during recordings or pending saves, and preserve settings and editable captures. Allow manual update checks and disabling automatic checks. |

## 4. Win + Shift + S behavior

### Desired experience

After the user enables shortcut replacement, pressing **Win + Shift + S** opens our area selector immediately, even when another ordinary application is active. Windows Snipping Tool must not open alongside it. Window capture, full-screen capture, and recording also need configurable shortcuts; their defaults remain undecided.

### Requirements

- **KEY-01:** Make replacement an explicit onboarding/settings choice with a test action and visible status: active, conflicting, or unavailable.
- **KEY-02:** Detect shortcut conflicts and provide a usable configurable fallback and tray access.
- **KEY-03:** Avoid double activation, stuck modifier keys, or interfering with unrelated Windows shortcuts. Test both left and right Windows/Shift keys and different key-release orders.
- **KEY-04:** Make every system configuration change reversible. Disabling replacement or uninstalling must restore any settings changed by the application without overwriting later user changes.
- **KEY-05:** Test startup, sleep/resume, application restart/crash, Explorer restart, and Windows updates. State behavior while the app is not running.
- **KEY-06:** Test ordinary apps, full-screen apps, elevated apps, and remote sessions separately. Document boundaries around the lock screen, secure desktop, and managed-device restrictions.
- **KEY-07:** Everyday capture should run without administrator privileges. Establish whether any proposed shortcut mechanism changes that expectation before selecting it.

### Feasibility status

Microsoft documents Windows-key combinations as reserved and notes that an already registered shortcut normally cannot be registered again. Therefore ordinary shortcut registration does **not** establish that Win + Shift + S can be reliably replaced. See [RegisterHotKey documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey).

Microsoft's [PowerToys Keyboard Manager documentation](https://learn.microsoft.com/en-us/windows/powertoys/keyboard-manager) demonstrates global shortcut remapping and application launching, while also documenting restrictions. This is a useful prototype candidate, not proof that this specific shortcut will behave correctly on every target Windows configuration.

The first implementation experiment should test integrated interception/remapping and, if useful, PowerToys-assisted remapping on the user's Windows installation. Record whether each option suppresses Snipping Tool, survives restart, needs elevated privileges or another background application, and restores normal behavior. Prefer an integrated solution. Do not require the user to uninstall Snipping Tool.

## 5. Windows quality requirements

These are proposed release criteria, not measured results or verified Mac specifications.

| Area | Draft acceptance target |
| --- | --- |
| Platform | Windows 11 x64 first. Confirm the user's exact Windows version and whether ARM64 or Windows 10 is needed before fixing the supported-platform matrix. |
| Responsiveness | On an agreed reference PC, 95% of warm shortcut activations show the selector within 200 ms; a typical screenshot opens for editing within 500 ms of selection completion. Measure cold startup separately. |
| Idle behavior | No continuous screen capture while idle; average idle CPU below 1% on the reference PC. Establish a measured memory budget during prototyping. |
| Image fidelity | Correct crop coordinates at 100%, 125%, 150%, and 200% display scaling; no unexpected resampling. Test SDR and HDR output for clipping or washed-out colors. |
| Recording | Provisional baseline: 1080p at 30 fps for 30 minutes with two audio sources, no crash, and end-of-recording audio/video drift below 100 ms on the reference PC. Define higher-resolution targets after testing. |
| Reliability | Complete 100 consecutive capture/edit/export cycles without crashes or lost captures. Recover gracefully from disconnected monitors/audio devices and failed saves. |
| Accessibility | Keyboard-operable settings/editor, visible focus, readable scaling, screen-reader labels, and support for high contrast and reduced motion. |
| Interoperability | Verify pasted images and dropped files in Explorer, a browser file-upload target, Slack, Teams, and Outlook. Unsupported receiving-app formats must have clear fallbacks. |
| Installation | Straightforward install, update, and uninstall. Sign distributed Windows executables/installers; do not promise that signing alone eliminates every Windows reputation warning. |
| Privacy | No telemetry or automatic image transmission. Network use limited to explicit Claude requests, update checks/downloads, and clearly disclosed component downloads. Account for user-selected folders that may already sync through another service. |

## 6. Essential end-to-end acceptance scenarios

1. **Everyday screenshot:** invoke the preferred shortcut, select an area using the loupe, add an arrow and text, and paste the annotated result into a message.
2. **Editable history:** capture, add multiple annotations, crop, close the app, restart, reopen, and modify the original annotations without flattening damage.
3. **Multiple displays:** select pixels on a secondary display with different scaling, pin the capture, move between screens, and drag out a file at the requested dimensions.
4. **Local OCR:** disconnect the network, capture a readable error dialog, and copy its text with line breaks intact.
5. **Video:** record screen, system sound, and microphone; verify separate audio tracks; trim in the filmstrip; export a playable video and a shareable GIF.
6. **Explicit AI request:** apply a crop/redaction, ask Claude to draft a bug report, and verify only the flattened edited image is sent. Without a key, all local features remain functional.
7. **Shortcut restoration:** enable replacement, confirm Snipping Tool does not also appear, disable replacement, and verify the prior configuration is restored. Repeat through app restart and uninstall.
8. **Failure recovery:** attempt capture/export with an unavailable save folder and record through an audio-device disconnect; present actionable errors while preserving completed work wherever possible.

## 7. Suggested build sequence

1. **Parity discovery and feasibility:** inspect the actual Mac app or source; inventory settings and shortcuts; prototype Win + Shift + S, mixed-DPI capture, separate audio tracks, lossless trimming, and HEIC export. Resolve these before committing to a stack.
2. **Daily screenshot workflow:** tray application, capture modes, loupe, window highlight, editor, persistent markup, clipboard, storage, floating previews, and drag/export.
3. **Complete advertised capability set:** local OCR, recording/audio, filmstrip trimming, GIF export, and Claude integration.
4. **Windows release readiness:** performance, accessibility, compatibility tests, signing, installer, updates, and restoration/uninstall behavior.

A screenshot-only build can be an early usable milestone, but it does not satisfy the requested full parity outcome.

## 8. Open discovery items

- Inspect the son's Mac app, source, or a walkthrough to identify features and settings the marketing page omits. Record the exact reference version.
- Confirm the user's Windows version, CPU architecture, monitor layout, scaling, and typical recording workload.
- Determine precise floating-preview behavior, recording source options, cursor inclusion, window shadows/borders, export controls, OCR languages, and annotation controls.
- Check whether scrolling capture, delayed capture, pause/resume recording, additional drawing tools, or other unlisted features exist. They are **not verified** by the website and are neither promised nor ruled out by this draft.
- Decide the final product name, visual relationship to KhingShot, and whether this is a personal Windows build or a distributable product.
- Preserve the Mac app's low-friction feel; choose the stack after the Windows integration experiments, based on measured results.

