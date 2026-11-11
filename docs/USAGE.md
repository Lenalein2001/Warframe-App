# Warframe Companion

Launch `WarframeCompanion.Desktop.exe` for normal per-user storage, or `Start Warframe Companion.exe` in an extracted Windows package. The launcher reuses existing `data/state.sqlite` portable storage when present; otherwise it shares the installed profile. Pass `--portable` to explicitly create separate portable storage. Windows requires WebView2. The framework-dependent edition also requires the .NET 10 and ASP.NET Core 10 runtimes; the self-contained edition includes them.

## Inventory and crafting uses

In Inventory, combine **Weapons**, **Mastered** and **Crafting use** filters, or choose **Review mastered weapons** to show mastered weapons with no known crafting uses. **Clear filters** returns to all items. Open an item to see the recipes that consume it, quantities required, and the target items' mastery and ownership. Open a target to follow its next crafting use.

No known crafting uses means no consuming recipes were found in the loaded catalog. Missing recipe data is shown separately. Mastery is account-wide: a second copy may still be unranked. Check upgrades and loadouts before selling in-game. The companion never sells items for you.

## Recognition

Public exports, profiles, logs, imports and optional read-only memory capture supply inventory and game data. Reward choices and on-screen riven cards use the bundled OCR fallback. Enable memory capture in Settings if wanted; it is disabled by default.

## Notifications

Settings includes **Send a test notification**. Windows uses native toasts; Linux uses the desktop notification service through libnotify. No PowerShell or notification command-line helper is launched. Windows registers a Start menu shortcut on the first notification so the toast identifies the companion. Notification visibility follows your desktop's settings, including Do Not Disturb.

## Linux and Proton

Run the native Linux companion alongside Warframe through Steam/Proton. The companion itself does not need Proton. See [development and Linux dependencies](https://github.com/Lenalein2001/Warframe-App/blob/senpai/docs/DEVELOPMENT.md) for the Nix package and build instructions.

Log discovery covers standard Steam, Flatpak Steam, additional libraries in `libraryfolders.vdf`, and `STEAM_COMPAT_DATA_PATH`. If discovery fails, set **EE.log path** explicitly. Automatic screenshots select a tool for the active desktop; install scrot or maim for X11, Spectacle for KDE, GNOME Screenshot for GNOME, or grim for Hyprland/Sway. Availability and permissions still depend on the desktop session. Global shortcuts on other desktops can use the commands shown in Settings.

Wayland overlays require layer-shell support or an available XWayland fallback. GNOME and other compositor restrictions may prevent overlays; use the main window when that happens. Live Warframe/Proton gameplay and the full Wayland compositor matrix have not yet been verified.

## Storage

Normal startup uses `%LOCALAPPDATA%\WarframeCompanion` on Windows and copies legacy data on the first launch. Original files are retained. Back up the data directory before moving it between machines. Do not run multiple instances against the same port.

Portable archives keep program files in `app/`. Use the top-level launcher and keep `data/` when updating. To bring an installed profile into a portable copy, close the app and copy the contents of its normal data directory into the portable `data/` directory first.

`--data-dir PATH` selects another location; `--offline` uses cached data; `--headless` runs without a desktop window. Full build and command-line documentation is in [the repository](https://github.com/Lenalein2001/Warframe-App/tree/senpai/docs).

## Authenticated local commands

`WarframeCompanion.Desktop trigger`, `riven` and `quit` read the current API credential automatically. Pass `--data-dir PATH` when using a separate profile and `--port NUMBER` for a port override. Start the companion first. Linux users with previously registered curl shortcuts should register them again in Settings after updating; existing custom scripts must supply authentication.

The running app writes `api-<port>.auth` in its data directory with user-only access. For custom curl scripts, use `curl --noproxy '*' -H @/path/to/data/api-8642.auth http://127.0.0.1:8642/api/state`. Never publish this file or put its contents into a command line. It rotates on startup and is removed on clean shutdown. A browser should be opened using the app's `--browser` option to establish its session.
