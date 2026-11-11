# Warframe Companion — portable

Extract the entire archive before starting.

- **Windows:** double-click `Start Warframe Companion.exe`.
- **Linux:** run `./Portable.sh`. Native GTK/WebKit and OCR dependencies must be installed; see `app/README.md`.
- **app/** contains the program, libraries and licenses. Keep these files together.
- **Windows profile:** a fresh extracted package uses the same settings, accounts and inventory as the installed app. If `data/state.sqlite` already exists beside the launcher, that existing portable profile is used instead. To create a separate portable profile, run the launcher with `--portable`. No profiles are merged or overwritten.
- **Linux profile:** `Portable.sh` uses the adjacent `data/` folder.

To update, close the companion and replace `app/` and the launcher with the new version. Keep `data/` untouched.

Portable data is separate from an installed copy. To bring your existing profile with you, close all companion windows and copy the **contents** of `%LOCALAPPDATA%\WarframeCompanion` (Windows) or `~/.local/share/WarframeCompanion` (Linux) into this package's `data/` folder. Back up both folders first. For an older portable copy, copy its existing `data/` folder here instead.

Do not launch the executable inside `app/` directly: it uses the normal per-user profile. Always use the top-level portable launcher.
