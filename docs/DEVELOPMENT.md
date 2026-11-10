# Warframe Companion C# replacement

The desktop application hosts the `web/` interface through Photino, with C# services and native C/C++ process reading, image processing, fuzzy matching and export decoding. Interface changes include the settings icon correction and inventory crafting-use filters. Platform-specific validation limits are recorded separately.

## Repository layout

`src/Core` defines validated inventory data, `src/Persistence` owns SQLite transactions, and `src/Services` implements catalog and game features. `src/Host` exposes the local HTTP API; `src/Desktop` hosts the window, hotkeys and overlays. `src/App` is the inventory administration CLI. `src/Native` contains C/C++ libraries and their public ABI.

Managed tests live in `tests/WarframeCompanion.Tests`; native tests live in `tests/native`. Windows and Linux packaging files live in `packaging`. Build output, private screenshots, account data, local configuration and agent files must not be committed. Maintained tests and repeatable build tools belong in the repository.

## Performance checks

Run `python tools/benchmark-api.py --url http://127.0.0.1:8642 --output artifacts/latency.json` against an isolated, populated instance. It warms each route and reports median/p95 latency and payload size, without exporting account content. Compare identical data, edition and background load. A live game can materially change timing; API latency is not a game frame-time measurement.

Crafting uses are indexed once per catalog load, then looked up by item ID. Cached thumbnails bypass the network download queue. Account summaries reuse their already-read snapshot. Avoid process-wide caches of mutable account data unless invalidation and account isolation are covered by tests.

## Build and run

Windows requires .NET SDK 10, Visual Studio C++ Build Tools with CMake, and WebView2. OCR requires English Tesseract, either installed or bundled under `tools/tesseract` beside the application.

```powershell
./tools/build-native.ps1
dotnet restore src/Desktop --locked-mode
dotnet build src/Desktop -c Release --no-restore
dotnet run --project tests/WarframeCompanion.Tests -c Release -- --ocr
python tools/migration-contracts.py --check
dotnet run --project src/Desktop -c Release
```

Linux additionally requires GTK3, WebKitGTK 4.1, gtk-layer-shell, Tesseract English data, a supported screenshot utility, and libnotify for notifications. Build the native libraries with CMake before building .NET. Linux runtime and compositor verification remain separate release gates.

## Storage and migration

Normal desktop startup uses `WarframeCompanion` in the OS local application-data directory. On first startup it copies existing Rust accounts, inventory, profile, manual counts, mastery, pins, farming tasks, settings and reward history. It never changes the Rust files. Account-specific data and settings use `state.sqlite`; normalized inventory observations and confirmed pointers use `companion.sqlite`.

`--portable` uses `data` beside the executable. `--data-dir PATH` selects an isolated location. Neither copies legacy data unless `--migrate-legacy` is also supplied. `--offline` disables network fetches and uses cached data. `--headless` runs only the HTTP services; `--no-game` disables game-log watching and automatic game integrations, useful for isolated tests. `--port NUMBER` overrides the saved port.

Partial inventory observations cannot replace confirmed inventory. Older or unordered source timestamps cannot roll it back. Clearing inventory clears its current pointer while retaining audit history. Unknown payload fields are excluded from normalized storage. Memory scanning is read-only and disabled by default. Actual capture depends on game state and OS access.

## Command-line use

`run` is the default command. `--browser` opens the interface in the system browser. `trigger`, `riven` and `quit` contact the running app using its saved port, or `--port NUMBER`. They also accept `--data-dir PATH` to select the settings directory.

`replay IMAGE... [--riven] [--scale PERCENT] [--offline] [--data-dir PATH] [--output JSONL]` reads saved screenshots through the same recognition pipeline without recording reward history or opening overlays. It needs a populated catalog cache or network access. `--output` is useful when invoking the Windows GUI executable from a terminal.

## Packaging

```powershell
./tools/publish-companion.ps1 -Runtime win-x64 -TesseractRoot C:/path/to/complete/tesseract-bundle
```

The OCR bundle must contain `tesseract.exe`, its DLLs, `tessdata/eng.traineddata`, and dependency licenses. `.github/bundle-tesseract.sh` creates this structure from MSYS2. Tesseract's own tool removes the unused legacy classifier while retaining the original English LSTM model and dictionaries; both the source and resulting file are pinned by SHA-256. Packages include the desktop host, services, native libraries, embedded UI, and OCR bundle. `Portable.cmd` selects portable storage; launching the executable normally selects per-user storage. Framework-dependent packages require both .NET 10 and ASP.NET Core 10 runtimes. Self-contained packages include these runtimes; WebView2 is still an OS prerequisite.

Package output goes to a unique directory under `artifacts`. No account data is packaged. Do not infer desktop sizes from the older inventory-only CLI artifacts.

The separate `src/App` CLI remains available for inventory validation, explicit complete imports, current/history inspection, normalized export, SQLite backup and integrity checking. Its data-directory defaults differ from the desktop; supply `--data-dir` when using it against a desktop snapshot database.

See `docs/migration/STATUS.md` and `docs/migration/VALIDATION.md` in the repository for implemented scope and outstanding evidence.
