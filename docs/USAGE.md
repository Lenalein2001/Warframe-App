# Warframe Companion

Launch `WarframeCompanion.Desktop.exe` for normal per-user storage, or `Portable.cmd` to keep settings and data beside the executable. Windows requires WebView2. The framework-dependent edition also requires the .NET 10 and ASP.NET Core 10 runtimes; the self-contained edition includes them.

## Inventory and crafting uses

In Inventory, combine **Weapons**, **Mastered** and **Crafting use** filters, or choose **Review mastered weapons without crafting uses**. Open an item to see the recipes that consume it, quantities required, and the target items' mastery and ownership. Open a target to follow its next crafting use.

No known crafting uses means no consuming recipes were found in the loaded catalog. Missing recipe data is shown separately. Mastery is account-wide: a second copy may still be unranked. Check upgrades and loadouts before selling in-game. The companion never sells items for you.

## Recognition

Public exports, profiles, logs, imports and optional read-only memory capture supply inventory and game data. Reward choices and on-screen riven cards use the bundled OCR fallback. Enable memory capture in Settings if wanted; it is disabled by default.

## Storage

Normal startup uses `%LOCALAPPDATA%\WarframeCompanion` on Windows and copies legacy data on the first launch. Original files are retained. Back up the data directory before moving it between machines. Do not run multiple instances against the same port.

`--data-dir PATH` selects another location; `--offline` uses cached data; `--headless` runs without a desktop window. Full build and command-line documentation is in [the repository](https://github.com/Lenalein2001/Warframe-App/tree/senpai/docs).
