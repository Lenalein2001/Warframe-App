# Warframe Companion

A Windows and Linux Warframe companion with the existing interface preserved. C# hosts the desktop window and application services; C++ handles process-memory reading and image analysis, and C handles compressed public-export decoding. No Rust compiler or backend is required.

Mastery, prime sets, relics, inventory, foundry, farming plans, standing, world events, market prices, reward overlays and riven grading are implemented. Migration and validation details, including outstanding live-game checks, are in [STATUS](docs/migration/STATUS.md). This remains a replacement candidate until those checks are complete.

## Build

Windows prerequisites: .NET SDK 10, Visual Studio C++ Build Tools with CMake, and WebView2.

```powershell
./tools/build-native.ps1
dotnet build WarframeCompanion.slnx -c Release -p:RestoreLockedMode=true
dotnet run --project companion/Tests -c Release
python tools/migration-contracts.py --check
dotnet run --project companion/Desktop -c Release
```

On Linux, install .NET SDK 10, CMake, a C++ compiler, GTK3, WebKitGTK 4.1 and gtk-layer-shell, then run `./run.sh`. `shell.nix` supplies the development tools on NixOS; `default.nix` packages the C# desktop application. Nix validation runs in CI.

## Data and recognition

The application copies legacy settings and accounts on first normal launch, preserving the original files. Inventory snapshots and settings use SQLite, with account isolation and protection against stale or partial replacement. `--portable` stores data beside the executable; `--data-dir PATH` uses an explicit location. See [build, storage and packaging details](companion/README.md).

Public exports, profiles, game logs, imported inventory and optional read-only native memory capture provide data without OCR. Explicit riven fingerprints can be decoded directly. Automatic recognition of reward choices and hovered riven cards still uses OCR where no reliable direct source is available. The application does not modify or inject into the game process.

## Packages and tests

```powershell
./tools/publish-companion.ps1 -TesseractRoot C:/path/to/tesseract-bundle
python tools/smoke-desktop-package.py C:/path/to/package-output
```

Framework-dependent packages require .NET 10 and ASP.NET Core 10. Self-contained packages include those runtimes. Both include the preserved UI and OCR fallback; Windows still requires WebView2. Debug symbols and user data are excluded.

Native tests, managed and HTTP integration tests, package restart tests and the preserved-interface contract run separately. Live-game capture and Linux compositor checks are reported separately from fixture success. See [validation notes](docs/migration/VALIDATION.md).

This fan project is not affiliated with Digital Extremes. It is provided under the [MIT license](LICENSE), without warranty. Historical releases are documented in [CHANGELOG](CHANGELOG.md).
