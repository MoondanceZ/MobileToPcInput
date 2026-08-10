# High-DPI icon design

## Goal

Keep the existing blue-purple circular microphone identity while removing the
cyan edge fringe and small-size stair-stepping visible in the Windows tray,
taskbar, and main window on high-DPI displays.

## Design

- Rebuild the mark as deterministic vector geometry with a transparent canvas.
- Keep the diagonal blue-to-indigo gradient and white microphone silhouette.
- Render 16, 20, 24, and 32 px frames with heavier, simplified strokes.
- Render 40, 48, 64, 96, 128, and 256 px frames from the full geometry.
- Store ICO frames as uncompressed 32-bit DIB images so Windows 10 can select
  native sizes without treating PNG-compressed ICO data as a document icon.
- Keep a 1024 px alpha PNG for Avalonia's in-window logo and an SVG master for
  future edits.

## Verification

- Confirm transparent corners and the absence of cyan/green edge pixels.
- Confirm every required ICO frame exists at its native dimensions.
- Build the Debug and Release PC projects.
- Inspect the actual main-window and tray icons at Windows display scaling.
