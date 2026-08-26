# Startup minimized design

## Goal

Start MobileToPcInput silently in the notification area when Windows launches
it at sign-in, while preserving the normal visible window for manual launches.

## Design

- Register the startup command with an explicit `--startup` argument.
- Detect that argument through the Avalonia desktop lifetime arguments.
- Construct the main window normally so the tray icon, receiver services, and
  model warm-up continue to initialize.
- Suppress the initial taskbar entry and window opacity, then hide the window
  on its first `Opened` event to avoid a visible startup flash.
- Restore taskbar visibility and opacity when the user opens the window from
  the tray.
- Recognize and migrate the legacy startup command that contains only the EXE
  path, so existing enabled installations remain enabled after upgrading.

## Verification

- Build Debug and Release configurations.
- Launch with `--startup` and confirm the process remains alive without a main
  window handle.
- Launch without the argument and confirm the main window is visible.
- Confirm startup registration includes `--startup` and legacy registration is
  migrated.
