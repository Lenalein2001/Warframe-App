# Warframe Companion

Launch `WarframeCompanion.Desktop.exe` for normal per-user storage, or `Start Warframe Companion.exe` in an extracted Windows package. The launcher reuses existing `data/state.sqlite` portable storage when present; otherwise it shares the installed profile. Pass `--portable` to explicitly create separate portable storage. Windows requires WebView2. All standard releases require the x64 .NET 10 and ASP.NET Core 10 shared runtimes.

The Windows single-file portable is `WarframeCompanion-win-x64.exe`. Launch it directly; no ZIP extraction or `app/` folder is needed. Native libraries are extracted automatically into the per-user .NET cache on first launch and reused. Shared runtimes are still required. Replace the executable while the app is closed to update; settings and optional OCR stay in the selected profile.

## Inventory and crafting uses

In Inventory, combine **Weapons**, **Mastered** and **Crafting use** filters, or choose **Review mastered weapons** to show mastered weapons with no known crafting uses. **Clear filters** returns to all items. Open an item to see the recipes that consume it, quantities required, and the target items' mastery and ownership. Open a target to follow its next crafting use.

No known crafting uses means no consuming recipes were found in the loaded catalog. Missing recipe data is shown separately. Mastery is account-wide: a second copy may still be unranked. Check upgrades and loadouts before selling in-game. The companion never sells items for you.

**Changes since last sync** compares the two most recent confirmed inventories for the selected account. It shows item and currency changes and links to affected farming goals. One interval can include several missions, trades or crafts. After upgrading, the existing confirmed inventory becomes the baseline for the next sync.

**Copies to keep** shows which equipment goals need each ingredient. Set a minimum to keep after those crafts. For example, if Afuris needs two Furis and you own three, a minimum of one leaves zero unreserved. The mastered weapons filter narrows the table to equipment you might want to review.

## Reward picks, finished builds and Duviri

In **Overview â†’ Relic rewards**, use **Pick** beside the reward you took. Select a different reward to correct it, or click **Picked** to clear the choice. In **Foundry**, use **Mark collected** after collecting a finished job in Warframe. Equipment details also offer **Mark as built** for crafts missing from the last read. Inventory includes these marks until a confirmed sync covers their time. **Changes since last sync** continues to compare confirmed reads.

In **Farming**, matching goals show whether equipment was offered in Teshin's Cave. **World â†’ Duviri** lists the saved offers and the weekly Circuit reset in your local time. An older or undated roll is labelled separately.

## Recognition

Public exports, profiles, logs, imports and optional read-only memory capture supply inventory and game data. Reward choices and on-screen riven cards use optional OCR. On Windows, select **Download and install OCR** under Settings > Reward screen and riven overlays. This downloads approximately 5 MB once; no administrator access or restart is needed. Inventory and collection features work without it. Linux uses Tesseract and English data from the distribution.

Memory capture is off by default. With **Read inventory from game memory** enabled and the logged-in account selected, new logins and mission syncs trigger a refresh. The companion groups related mission messages and prefers the inventory update when it arrives. Select **Read inventory once** for an immediate refresh. For regular updates without reading game memory, import an inventory JSON file or set its path in Settings.

## Required runtimes

The portable and installer releases use shared x64 .NET 10 and ASP.NET Core 10 runtimes. They are not bundled with the app. Download the latest serviced 10.0.x Windows runtime installers from https://dotnet.microsoft.com/en-us/download/dotnet/10.0; no SDK is required. The Windows executable prompts for a missing runtime. Linux users should install the ASP.NET Core 10 package from their distribution, which also brings in the base .NET runtime.

## Notifications

Settings includes **Send a test notification**. Windows uses native toasts; Linux uses the desktop notification service through libnotify. No PowerShell or notification command-line helper is launched. Windows registers a Start menu shortcut on the first notification so the toast identifies the companion. Notification visibility follows your desktop's settings, including Do Not Disturb.

## Linux and Proton

Run the native Linux companion alongside Warframe through Steam/Proton. The companion itself does not need Proton. See [development and Linux dependencies](https://github.com/Lenalein2001/Warframe-App/blob/senpai/docs/DEVELOPMENT.md) for the Nix package and build instructions.

Log discovery covers standard Steam, Flatpak Steam, additional libraries in `libraryfolders.vdf`, and `STEAM_COMPAT_DATA_PATH`. If discovery fails, set **EE.log path** explicitly. Automatic screenshots select a tool for the active desktop; install scrot or maim for X11, Spectacle for KDE, GNOME Screenshot for GNOME, or grim for Hyprland/Sway. Availability and permissions still depend on the desktop session. Global shortcuts on other desktops can use the commands shown in Settings.

Wayland overlays require layer-shell support or an available XWayland fallback. GNOME and other compositor restrictions may prevent overlays; use the main window when that happens. Live Warframe/Proton gameplay and the full Wayland compositor matrix have not yet been verified.

## Storage

Normal startup uses `%LOCALAPPDATA%\WarframeCompanion` on Windows and copies legacy data on the first launch. Original files are retained. Back up the data directory before moving it between machines. Do not run multiple instances against the same port.

Linux portables and offline OCR bundles keep program files in `app/`. Use the top-level launcher and keep `data/` when updating. To bring an installed profile into a portable copy, close the app and copy the contents of its normal data directory into the portable `data/` directory first.

`--data-dir PATH` selects another location; `--offline` uses cached data; `--headless` runs without a desktop window. Full build and command-line documentation is in [the repository](https://github.com/Lenalein2001/Warframe-App/tree/senpai/docs).

## Authenticated local commands

`WarframeCompanion.Desktop trigger`, `riven` and `quit` read the current API credential automatically. Pass `--data-dir PATH` when using a separate profile and `--port NUMBER` for a port override. Start the companion first. Linux users with previously registered curl shortcuts should register them again in Settings after updating; existing custom scripts must supply authentication.

The running app writes `api-<port>.auth` in its data directory with user-only access. For custom curl scripts, use `curl --noproxy '*' -H @/path/to/data/api-8642.auth http://127.0.0.1:8642/api/state`. Never publish this file or put its contents into a command line. It rotates on startup and is removed on clean shutdown. A browser should be opened using the app's `--browser` option to establish its session.

## Startup troubleshooting

Extract the entire portable archive before opening its top-level launcher. For archives that include an `app/` directory, keep it beside the launcher. Opening the same profile twice activates its existing desktop window; headless instances must be stopped before opening that profile in the desktop app. Separate profiles can run with different `--data-dir` and `--port` values.

Windows startup errors use a native message box. Headless commands report errors to stderr. If a data directory has been selected, startup failures can also write `startup-error.log` there; it contains the error type and general recovery instructions, not account or inventory data. The empty `.instance.lock` file is normal and may remain after exit. Do not delete it to bypass a running instance.

`startup-timing.log` records elapsed startup phases, including native window creation. It contains fixed stage names and timings only. This separates backend readiness from actual window startup; headless startup checks do not validate WebView rendering.

If most of the delay occurs before managed entry, Windows may be checking the executable before it can run. Application-control policies can perform external reputation checks even in audit mode. An administrator can inspect Code Integrity events and a Windows performance recording to distinguish this from application initialization. Any machine-specific trust rule should cover reviewed binary hashes or an approved signer; the companion does not change security policies or add antivirus exclusions.
