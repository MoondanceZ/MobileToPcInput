# Hotkey capture fix design

## Problem

The capture UI saves on the first key-up event. Windows can route `Alt` chords
through the system-key path, so `Alt` is captured while the ordinary key is
missed or arrives after the shortcut has already been saved.

## Design

- Install a low-level Windows keyboard hook only while shortcut capture is
  active, so system-key combinations are received even if another application
  already owns them.
- Swallow keyboard messages during capture so recording `Alt+Q` does not
  actually activate WeType and steal focus.
- Accumulate supported virtual keys in press order and save after every pressed
  key has been released.
- Preserve key order, remove duplicates, keep single-key shortcuts, and retain
  Escape cancellation.

## Verification

- Register the hook and inject `Alt+Q`, then confirm the hook receives both keys
  while a second observer receives none.
- Verify reverse release order, Escape cancellation, and single-key capture.
- Build Debug and Release configurations and manually exercise the capture UI.
