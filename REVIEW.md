# BetterShot review

## Thumbnail and editor update — September 6, 2026

Three GPT-5.6 Sol agents implemented the editor, thumbnail workflow, and circular loupe. The primary agent reviewed their code, requested corrections, built the installed Windows application, ran its diagnostics, and visually inspected renders from the actual WPF controls.

Delivered behavior:

- Successful capture leaves a persistent, non-activating lower-left thumbnail instead of showing the main window.
- Hover/focus reveals edit, copy, PNG save, pin, and dismiss actions. Thumbnails start above other windows. Visible stacks fit monitor work areas; dismissed and overflow captures remain accessible in the session's Recent list.
- Clicking a thumbnail opens a dark editor with arrows, rectangles, freehand pen, editable text, numbered steps, opaque redaction, selection/movement/deletion, undo/redo, and fit/zoom. Copy and save export the rendered annotations at native image dimensions.
- Closing/reopening an editor retains its shared editable document for the running session.
- A circular 8× magnifier replaces the square loupe. Edge-replicated samples keep the cursor pixel centered at display edges; selection outline, corner marks, and dimension labels are polished.

Validation of the installed update:

- `build.ps1`: zero warnings, zero errors.
- `self-test.ps1`: passed shortcut classification, geometry, annotation retention/events, opaque redaction, high-DPI pixel fidelity, undo/redo, and document rendering.
- `diagnostic-test.ps1`: passed selector startup/cancellation/cleanup and rendered the main window.
- Integrated workflow diagnostic: passed thumbnail-first/no-auto-editor assertions, native thumbnail placement and action-tray fit, multi-capture retention, dismissal, and shared-document reopening.
- Visually reviewed normal and hovered thumbnail, editor toolbar/canvas, and loupe at center and screen edges. Images are in the ignored `artifacts` folder.

Review corrections included high-DPI export scaling, live provisional drawing, text modal capture release, stale-preview cleanup, dark color/width controls, accessible contrast, DPI-aware thumbnail placement, hidden action hit-testing, recent capture access, and application-window hiding during capture.

Limits: documents/history are in memory until exit; save PNGs to keep flattened results. Crop, OCR, AI, and video are not part of this update. Direct human mouse/keyboard interaction and external clipboard receiving applications still need hands-on checks; diagnostics exercise application logic and native window creation without injecting input. The original capture-only source is backed up at `artifacts/BetterShot-v1-source.zip`.

## First prototype baseline

Reviewed September 6, 2026. Implementation was delegated to two GPT-5.6 Sol agents; the primary agent reviewed their code, requested fixes, and performed Windows build and runtime checks.

## Verified on this machine

- Windows 11, OS build 26200, x64; project-local Microsoft .NET SDK 10.0.400. SDK ZIP verified against Microsoft's published SHA-512.
- `build.ps1`: Release build succeeded with zero warnings and zero errors.
- `self-test.ps1`: shortcut classification/pairing checks and physical-pixel geometry checks passed. These do not prove native hotkey interception.
- `smoke-test.ps1`: native desktop capture and PNG encoding succeeded; output was 6200 × 1920 pixels.
- `diagnostic-test.ps1`: all three selector modes opened and canceled cleanly. Each overlay's actual Windows bounds matched its monitor; WPF dimensions scaled to physical pixels within one pixel. Closing one overlay canceled the complete session.
- The actual WPF main window rendered successfully and was visually reviewed. Controls fit without overlap, including the separate shortcut option and export actions.
- Tests launch the Windows app host with its DPI manifest and project-local runtime.

## Review fixes included

- Mixed WPF/WinForms namespace conflicts; multi-monitor focus handling; pixel selection and loupe edge targeting.
- Cleanup on Escape, window close, loss of mouse capture, and startup failure.
- Native graphics handle validation and cleanup.
- Preview/error visibility after capture initiated from the tray.
- PNG encoding to a temporary sibling file before replacing the destination.
- Paired shortcut suppression, a rooted hook delegate, fail-open callback error handling, and hook removal on exit.
- Reliable PowerShell process invocation, explicit build/test exit checks, and a console-free app launcher.

## Hands-on checks still needed

1. Start BetterShot and try an area drag, window selection, and display selection; compare captured content with the selected region.
2. Copy an image and paste it into a receiving application; save a PNG through the file dialog.
3. Enable **Use Win+Shift+S for BetterShot** and press that shortcut. Verify only BetterShot opens. Disable it and verify Windows Snipping Tool works again. Also test the fallback Ctrl+Shift+F9.
4. Drag across monitor boundaries and inspect the loupe and output at different scaling settings.

Windows UI automation could not initialize in this WSL task due to a host path metadata error, so mouse/keyboard flows above are not claimed as tested. The Computer Use skill also prohibits automated Windows-key combinations.

## Prototype boundaries

GDI captures the visible composed desktop in SDR. Window mode crops visible window bounds, including any occluding content; it does not recover hidden/minimized window content. HDR fidelity and protected surfaces are not guaranteed. The shortcut checkbox resets when the app exits; it makes no persistent OS configuration changes. Video and editing are deferred.
