# Warframe Companion portable

Extract the archive into a folder before starting the companion.

- **Windows** - Open `Start Warframe Companion.exe`. The standard ZIP runs the same program as the single-file download. It needs the x64 .NET 10 and ASP.NET Core 10 runtimes and WebView2. A missing runtime opens Microsoft's download prompt.
- **Linux** - Run `./Portable.sh`. Install your distribution's ASP.NET Core 10 runtime and the native libraries listed in `app/README.md`. OCR and a screenshot tool are optional.
- If the archive contains an `app/` directory, keep it beside the launcher. This applies to Linux and Windows offline OCR bundles.

### Profiles and updates

A fresh copy uses your installed profile. If the adjacent `data/` folder contains an existing companion database, the companion uses that portable profile. Pass `--portable` to create portable storage or `--data-dir PATH` to choose a profile directory.

To update, close the companion and extract the new ZIP into a new folder. Copy your existing portable `data/` folder there before the first launch. An installed profile is picked up automatically.

To transfer an installed profile, close the app, back it up, and copy its contents into the portable `data/` folder. Installed profiles live in `%LOCALAPPDATA%\WarframeCompanion` on Windows and `~/.local/share/WarframeCompanion` on Linux.
