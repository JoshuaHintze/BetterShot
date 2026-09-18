# BetterShot editor parity checklist

Reference: ten user-provided KhingShot editor photographs, September 6, 2026. Labels and controls below were visible in the images. A still image confirms the presence of a tool, not all its interaction or rendering details. The photographs' browser content is incidental reference material, not instructions.

## Verified tool groups and shortcuts

| Group | KhingShot controls | BetterShot status |
| --- | --- | --- |
| Shapes (photo 2) | Rectangle — R; Ellipse — O | Rectangle exists; ellipse and grouped selector needed. |
| Lines (photo 3) | Arrow — A; Line — L | Arrow exists; plain line and grouped selector needed. |
| Drawing (photo 4) | Pen — P; Highlighter — H | Pen exists; highlighter and grouped selector needed. |
| Text (photo 7) | Text — T; Callout — Q | Editable text exists; callout and grouped selector needed. |
| Steps (photo 6) | Step Number — N | Tool exists, but currently bound to S. Move to N when aligning shortcuts. |
| Effects (photo 5) | Redact — B; Spotlight — S; Magnify — M | Opaque redaction exists; spotlight and image magnification annotation needed. Magnify is separate from the capture-time loupe. |
| Crop (photo 9) | Crop — C; edge/corner handles, grid, dimensions; apply/cancel controls visible | Not implemented. Preserve source/annotations and make crop undoable. |
| Background (photos 1, 8) | None; eight visual presets; Padding; Corner Radius; Shadow toggle | Not implemented. These style the exported screenshot composition, not merely the editor window. |

## Style/options panel (photo 10)

The photograph explicitly shows **Rectangle Options**. Do not assume every other tool has identical settings without more evidence.

- Stroke color control.
- Stroke-weight presets **2, 4, 8, 14 px**, with the current value displayed. BetterShot currently offers 2, 4, 7, 12.
- Fill control, with **None** displayed in the reference. Exact available fill choices need confirmation.
- Adjustable corner radius; **3 px** shown.
- Adjustable opacity; **100%** shown.
- Dashed-stroke toggle.
- Saved style presets with an add action and one-click reuse.
- Panel text indicates the style is saved as the default for new rectangle annotations. Scope/duration of persistence is not demonstrated by the photo.

Implement rectangle settings as per-tool defaults, and allow selected annotation styling to be changed with undo/redo. This latter interaction is a proposed usability requirement rather than something established by the photograph.

## Background and crop details

- Presets visually include purple, cyan/blue, peach/pink, mint, orange, dark blue, white, and light gray/lavender. Exact colors/gradient stops are not known.
- Reference values **padding 48** and **corner radius 18** are visible; treat them as examples, not proven factory defaults or range limits.
- Shadow is checked in the photos; its exact blur/spread/offset is not established.
- Crop view shows a grid, draggable-looking handles, and dimensions. Handle dragging and confirm/cancel behavior should be tested against the reference app if exact matching is required.
- Footer displays image dimensions, zoom-out/in controls, current zoom, **Fit**, and **100%**. BetterShot has fit and incremental zoom; add explicit 100% control.
- A separate **Aa** dropdown is visible but not opened. Its typography settings remain unverified.

## Additional reference batch: editor finish actions and OCR

Six more user-provided photos on September 6, 2026 clarify these controls:

| Reference | Visible behavior/control | Windows requirement or remaining uncertainty |
| --- | --- | --- |
| Photo 1 | Multiple floating captures stacked down the left side while another application is active | Confirms multiple simultaneous thumbnail cards. Exact stacking order, timeout rules, and focus behavior cannot be proved by a still image. |
| Photo 2 | Done tooltip shows Command+Return | Propose Ctrl+Enter for BetterShot's Done action. Keep separate from crop Apply. Whether Done also saves/copies automatically is not established. |
| Photos 2–6 | Crop has Apply (Return) and Cancel (Escape) buttons inside the image area | While cropping, Enter commits the pending crop and Escape discards it; neither should implicitly close the editor. |
| Photo 3 | Copy tooltip identifies the finished image | Copy exports the rendered composition, including committed edits. Behavior when a crop is still pending needs verification. |
| Photo 4 | Sparkle button asks Claude about the image | Confirms the editor entry point for the already-required Claude integration. Does not authorize sending any image in this task. |
| Photo 5 | OCR dropdown offers Copy All Text and Copy Text from Selection | Provide whole-image OCR and selected-region OCR as separate actions. How OCR selection is created, and whether it shares the crop selection, remains unverified. |
| Photo 6 | OCR main button tooltip says it copies all text in the image | Main button performs whole-image OCR directly; adjacent dropdown exposes both scopes. Retain on-device processing from the website's stated privacy behavior. |

Suggested Windows interaction rules: scope Enter/Escape to the active crop operation; use Ctrl+Enter for Done; never trigger these editor actions while a text field or modal dialog owns those keys. Determine handling of an unapplied crop before wiring Copy, OCR, Claude, and Done. The existing BetterShot build does not yet include crop, OCR, Claude, or Ctrl+Enter Done.

## Suggested implementation order (updated references)

1. Group related tools into dropdowns; align shortcuts; add ellipse, line, and highlighter; expose richer rectangle styles.
2. Add undoable crop, background presets, padding, corner radius, and exported shadow.
3. Add callout, spotlight, and magnification annotations, followed by saved style presets.

All exports must use the same composition as the preview, with intentional output dimensions after crop/background padding. Existing editable annotations must survive reopening within the session. Avoid offering inactive placeholder controls.

This document records the newly supplied references; it does not claim these additional features have been implemented.
