# Warframe Companion — portable

Extract the entire archive before starting.

- **Windows:** double-click `Start Warframe Companion.cmd`.
- **Linux:** run `./Portable.sh`. Native GTK/WebKit and OCR dependencies must be installed; see `app/README.md`.
- **app/** contains the program, libraries and licenses. Keep these files together.
- **data/** is created beside the launcher on first start. It contains your portable settings, accounts and inventory.

To update, close the companion and replace `app/` and the launcher with the new version. Keep `data/` untouched.

Portable data is separate from an installed copy. To bring your existing profile with you, close all companion windows and copy the **contents** of `%LOCALAPPDATA%\WarframeCompanion` (Windows) or `~/.local/share/WarframeCompanion` (Linux) into this package's `data/` folder. Back up both folders first. For an older portable copy, copy its existing `data/` folder here instead.

Do not launch the executable inside `app/` directly: it uses the normal per-user profile. Always use the top-level portable launcher.
