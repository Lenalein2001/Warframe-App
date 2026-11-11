# Warframe Companion portable

Extract the entire archive before starting.

.NET runtimes are not bundled. Install .NET 10 and ASP.NET Core 10 for your platform and architecture. On Windows, use the x64 runtime installers from https://dotnet.microsoft.com/en-us/download/dotnet/10.0 (the SDK is not needed). If a required runtime is missing, the Windows executable displays Microsoft's download prompt. On Linux, install your distribution's ASP.NET Core 10 package; on Ubuntu 24.04 this is `aspnetcore-runtime-10.0`.

- **Windows:** double-click `Start Warframe Companion.exe`.
- **Linux:** run `./Portable.sh`. Install the native GTK/WebKit and OCR dependencies listed in `app/README.md`.
- **app/** contains the program, libraries and licenses. Keep these files together.

A fresh package uses the same settings, accounts and inventory as the installed app. An existing `data/state.sqlite` or `data/companion.sqlite` beside the launcher selects that portable profile instead. To create a separate portable profile, run the launcher with `--portable`. Use `--data-dir PATH` for a specific profile. No profiles are merged or overwritten.

To update, close the companion, back up your profile, and extract the new package into a new folder. If you use portable storage, copy the existing `data/` folder into the new folder before starting its launcher. Keep the old copy until you have confirmed the update works. Do not run both copies together.

To bring an installed profile with you, close the companion and copy the contents of `%LOCALAPPDATA%\WarframeCompanion` (Windows) or `~/.local/share/WarframeCompanion` (Linux) into the package's `data/` folder. Back up both folders first.

Always use the top-level launcher: starting the executable inside `app/` directly selects the normal per-user profile.

If startup fails, confirm that you extracted the entire archive and kept the launcher beside `app/`. Windows displays a startup error; failures after opening the profile also write a short `startup-error.log` there, without account or inventory contents. A profile already running in the desktop app is brought forward instead of starting a second backend.
