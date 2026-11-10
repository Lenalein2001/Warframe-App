# Warframe Companion C# replacement

The desktop application hosts the original `web/` interface unchanged through Photino, with a C# service layer and native C/C++ image processing, fuzzy matching and export decoding. It does not launch or depend on the Rust backend. This is a replacement candidate under validation, not a completed parity release.

## Build and run

Windows requires .NET SDK 10, Visual Studio C++ Build Tools with CMake, and WebView2. OCR requires English Tesseract, either installed or bundled under `tools/tesseract` beside the application.

```powershell
./tools/build-native.ps1
dotnet restore companion/Desktop --locked-mode
dotnet build companion/Desktop -c Release --no-restore
dotnet run --project companion/Tests -c Release -- --ocr
python tools/migration-contracts.py --check
dotnet run --project companion/Desktop -c Release
```

Linux additionally requires GTK3, WebKitGTK 4.1, gtk-layer-shell, Tesseract English data, a supported screenshot utility, and libnotify for notifications. Build the native libraries with CMake before building .NET. Linux runtime and compositor verification remain separate release gates.

## Storage and migration

Normal desktop startup uses `WarframeCompanion` in the OS local application-data directory. On first startup it copies existing Rust accounts, inventory, profile, manual counts, mastery, pins, farming tasks, settings and reward history. It never changes the Rust files. Account-specific data and settings use `state.sqlite`; normalized inventory observations and confirmed pointers use `companion.sqlite`.

`--portable` uses `data` beside the executable. `--data-dir PATH` selects an isolated location. Neither copies legacy data unless `--migrate-legacy` is also supplied. `--offline` disables network fetches and uses cached data. `--headless` runs only the HTTP services; `--no-game` disables game-log watching and automatic game integrations, useful for isolated tests. `--port NUMBER` overrides the saved port.

Partial inventory observations cannot replace confirmed inventory. Older or unordered source timestamps cannot roll it back. Clearing inventory clears its current pointer while retaining audit history. Unknown payload fields are excluded from normalized storage. Memory scanning is read-only and disabled by default. Actual capture depends on game state and OS access.

## Packaging

```powershell
./tools/publish-companion.ps1 -Runtime win-x64 -TesseractRoot C:/path/to/complete/tesseract-bundle
```

The OCR bundle must contain `tesseract.exe`, its DLLs, `tessdata/eng.traineddata`, and dependency licenses. The existing `.github/bundle-tesseract.sh` creates this structure from MSYS2. Packages include the desktop host, services, native libraries, unchanged embedded UI, and OCR bundle. `Portable.cmd` selects portable storage; launching the executable normally selects per-user storage. Framework-dependent packages require both .NET 10 and ASP.NET Core 10 runtimes. Self-contained packages include these runtimes; WebView2 is still an OS prerequisite.

Package output goes to a unique directory under `artifacts`. No account data is packaged. Do not infer desktop sizes from the older inventory-only CLI artifacts.

The separate `companion/App` CLI remains available for inventory validation, explicit complete imports, current/history inspection, normalized export, SQLite backup and integrity checking. Its data-directory defaults differ from the desktop; supply `--data-dir` when using it against a desktop snapshot database.

See `docs/migration/STATUS.md` and `docs/migration/VALIDATION.md` in the repository for implemented scope and outstanding evidence.
