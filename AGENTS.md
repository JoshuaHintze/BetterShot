# BetterShot project guidance

Read SESSION-HANDOFF.md first, then README.md. TOOL-PARITY.md records remaining reference-app features. REQUIREMENTS-ORIGINAL.md is historical planning, not current implementation status.

## User preferences
- Build a polished Windows counterpart to the user's son's Mac KhingShot app (https://khingshot.khingsuite.com/).
- Use Lucide icons for UI actions. Retain the generated BetterShot app/tray icon.
- Keep the editor toolbar on one row; zoom controls belong in the footer.
- Capture should leave persistent bottom-left thumbnails, not immediately open the editor. Thumbnails always stay on top; do not add a pin toggle.
- Text should edit on canvas with move/resize handles, not in a popup.
- The user requested GPT-5.6 Sol agents for hands-on implementation to reduce cost, with the main agent reviewing changes and verification. Use that model when delegation tools support it; do not create unrelated user-facing tasks as a substitute. Explain if unavailable.
- Treat reported UI problems and improvement feedback as requests to implement, build, verify, and restart BetterShot unless the user explicitly says not to build or change it. Do not stop at suggestions or ask again.
- Favor action within the requested scope and concise, plain-language updates. Do not repeatedly ask for approval already granted.

## Working rules
- This is a standalone C#/.NET 10 WPF application, not the unrelated open-shop web monorepo. No pnpm/npm or web deployment is needed.
- Work in C:\projects\BetterShot. Windows PowerShell is now the active environment. Old /mnt/c paths in chat were WSL equivalents.
- Use the project-local .tools\dotnet SDK and existing scripts. Visual Studio is not required.
- Review current code before changing it; preserve the working capture, drag/export, and text-editing behavior.
- Build to a separate artifacts subfolder while the app is running. Check build exit codes before running diagnostics; old PASS files are not proof a new run passed.
- Use appropriate existing diagnostics and inspect rendered UI after visual changes. Do not claim automated diagnostics establish compatibility with every external drop target or replace hands-on testing.
- Captures and annotations persist locally under %LOCALAPPDATA%\BetterShot\History until expiry (default seven days) or permanent deletion. Undo/redo resets on restart. The Win+Shift+S override preference persists and is reapplied at startup while BetterShot is running. The user explicitly authorized automatic BetterShot restarts after completed updates on September 7, 2026, with these effects disclosed. After verification, install the update and restart BetterShot without asking again; verify the new process is running.
- Preserve Lucide license notices in distributed builds. Keep test screenshots/exports local; they may include private desktop content.
- Update SESSION-HANDOFF.md when implementation status changes materially.

- The active user copy is now the installed app under %LOCALAPPDATA%\Programs\BetterShot (first installer0.1.0). Inspect the running executable path before updates; do not silently switch back to the development bin copy. Build installer updates with build-installer.ps1 and preserve user history/settings. installer\README.md records packaging and verification.
