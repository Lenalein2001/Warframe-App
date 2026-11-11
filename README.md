<div align="center">

# Warframe Companion

Inventory, mastery, crafting and mission rewards in one desktop app.

[![Validation](https://github.com/Lenalein2001/Warframe-App/actions/workflows/companion.yml/badge.svg?branch=senpai)](https://github.com/Lenalein2001/Warframe-App/actions/workflows/companion.yml)
[![Windows](https://github.com/Lenalein2001/Warframe-App/actions/workflows/windows.yml/badge.svg?branch=senpai)](https://github.com/Lenalein2001/Warframe-App/actions/workflows/windows.yml)
[![Linux](https://github.com/Lenalein2001/Warframe-App/actions/workflows/linux.yml/badge.svg?branch=senpai)](https://github.com/Lenalein2001/Warframe-App/actions/workflows/linux.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-7bb6b1.svg)](LICENSE)

[Download](https://github.com/Lenalein2001/Warframe-App/releases/latest) · [Usage](docs/USAGE.md) · [Development](docs/DEVELOPMENT.md) · [Report a bug](https://github.com/Lenalein2001/Warframe-App/issues)

</div>

Warframe Companion brings collection progress, crafting information and reward comparisons together without leaving your desktop. It runs on Windows and Linux, with separate accounts, persistent settings and optional in-game overlays.

## Features

| | |
| --- | --- |
| **Inventory** | Search equipment and resources, filter by mastery and trading status, and see which recipes consume an item. |
| **Collections** | Track mastery, prime sets, relics, missing parts and pinned items. |
| **Crafting and farming** | Review foundry jobs, required materials, drop locations and saved farming plans. |
| **Relic rewards** | Compare recognized rewards, ownership and market values in an overlay. |
| **Rivens and markets** | Grade rolls, decode explicit riven fingerprints and look up market prices. |
| **World and standing** | Browse fissures, cycles, events, syndicates and offers. |
| **Desktop integration** | Receive native notifications and use shortcuts for common actions. |

### Review mastered weapons

The inventory's **Review mastered weapons** filter shows mastered weapons with no known consuming recipes. Open an item to inspect its crafting uses, required quantities, target ownership and mastery, then follow the recipe chain.

Recipe information reflects the loaded catalog. Mastery is account-wide; individual copies, upgrades and loadouts still matter. The app never sells items for you.

## Installation

Download packages from [GitHub Releases](https://github.com/Lenalein2001/Warframe-App/releases/latest). All published builds target **x86-64**.

### Windows

- **Installer:** run `WarframeCompanion-win-x64-Setup.exe` for a per-user installation and Start menu shortcut.
- **Portable:** extract the self-contained ZIP and run `Start Warframe Companion.exe`. Program files stay in `app/`. A fresh copy shares your installed profile; an existing `data/` profile stays portable. Use `--portable` to create separate portable storage.
- **Smaller download:** the framework-dependent ZIP requires the .NET 10 and ASP.NET Core 10 runtimes. The self-contained edition includes them.

Windows requires WebView2. Both editions include the English OCR fallback. The framework-dependent ZIP is approximately 13 MB; the self-contained ZIP is approximately 63 MB.

### Linux

The native Linux app runs alongside Warframe through Steam/Proton; the companion itself does not need Proton.

**Ubuntu 24.04 / Linux Mint 22:** install the Debian package with dependency resolution:

```sh
sudo apt install ./WarframeCompanion-linux-x64.deb
warframe-companion
```

**Portable:** extract `WarframeCompanion-linux-x64-portable.tar.gz` into a writable folder and run `./Portable.sh`. The .NET runtime is included; GTK 3, WebKitGTK 4.1, GTK Layer Shell, libnotify, fontconfig, ICU, OpenSSL and English Tesseract must be supplied by your distribution. Screenshot tools depend on the session: scrot/maim for X11, Spectacle for KDE, GNOME Screenshot for GNOME, or grim for wlroots compositors.

**Nix:** use `nix-build` and launch `./result/bin/WarframeCompanion.Desktop`. See [development](docs/DEVELOPMENT.md) for details.

Ubuntu and Nix desktop startup, notifications and X11 overlays are tested in CI. Live Warframe under Proton and the Wayland compositor matrix remain unverified. Wayland screenshot permissions and overlay support vary by desktop; the main app remains available when an overlay is unsupported.

## Data and recognition

Settings and account data are stored outside the installation directory by default:

| Platform | Location |
| --- | --- |
| Windows | `%LOCALAPPDATA%\WarframeCompanion` |
| Linux | `$XDG_DATA_HOME/WarframeCompanion`, or `~/.local/share/WarframeCompanion` |
| Portable | `data/` beside the top-level launcher |

Updates and uninstallers preserve user data. Back up the data folder before moving profiles between machines. Existing legacy data is copied on first normal startup without changing the original files.

The app combines several sources; none should be mistaken for a complete official inventory API:

| Source | What it supplies |
| --- | --- |
| Public catalogs and community services | Item definitions, recipes, drop tables, world state and market estimates. |
| Public account profiles | Profile and mastery information; not a complete inventory. |
| Local `EE.log` / `EE.cfg` | Account/session signals and interface scale, read without changing game files. |
| Imports and manual tracking | User-supplied inventory snapshots, counts and collection progress. |
| Optional process-memory capture | Inventory found in the running game through read-only OS access. Disabled by default; existing choices are retained. |
| Screenshot/OCR fallback | Visible relic reward names and riven cards, processed locally. |

## What the app does�and does not do

The companion helps you inspect information and plan your next action. It **does not write to Warframe's process memory, inject code, modify game files, simulate gameplay input, sell items, craft items, automate missions, or log in to game services with your credentials**. Editing counts or clearing inventory in the companion changes its own records, not your in-game inventory. Relic-name copying writes to the clipboard; you choose when to paste it.

Overlays are separate desktop windows. OCR uses screen pixels and temporary local files; when game-window capture is unavailable, capture can include other desktop content. Memory scanning can temporarily encounter unrelated game bytes. Raw scan buffers are cleared when released; only recognized inventory fields are saved. This is data minimization, not a promise that sensitive data can never exist in process memory or an OS crash dump.

Disabling memory capture cancels active work and prevents cancelled results from being saved. Scans have time and byte limits, do not elevate privileges, and stop when the operating system denies access. Existing confirmed inventory remains available when capture fails.

### Local API and privacy

The local HTTP service binds to loopback and requires a random credential regenerated on every launch. The desktop interface authenticates automatically using an HttpOnly, SameSite cookie. CLI commands and registered Linux shortcuts read a user-restricted `api-<port>.auth` file in the data directory. Credentials are removed on clean shutdown; do not share these files. Old custom HTTP scripts need to supply the current Authorization header.

Host/origin checks and cross-site request rejection provide additional browser protection. These controls do **not** isolate the app from malware already running as your user, administrators, or someone with access to your data directory. Keep portable data in private storage. Account data stays local; requests for profiles, prices and artwork necessarily disclose the requested account/item identifiers to the corresponding service. No whole-inventory upload is implemented.

### Publisher policy

Read-only access is not a guarantee of publisher permission or protection from account enforcement. This project is not approved by Digital Extremes. Review the [Warframe EULA](https://www.warframe.com/en/eula) and [DE's third-party software guidance](https://forums.warframe.com/topic/1383123-third-party-software-usage/) before enabling game integrations. Functional tests cannot certify EULA compliance.

See [usage](docs/USAGE.md) for account setup, notifications and command-line options.

## Architecture and development

C# handles application services, persistence and the desktop host. C/C++ handles process reading, image processing and export decoding. The interface uses local HTML, CSS and JavaScript in the system webview.

```mermaid
flowchart LR
    G[Game data and public sources] --> S[C# services]
    N[C/C++ processing] --> S
    S <--> D[(SQLite)]
    S --> U[Desktop interface]
    S --> O[Overlays and notifications]
```

```text
src/          C# projects and C/C++ libraries
web/          Interface assets
tests/       Maintained regression and integration tests
packaging/    Windows and Linux installation files
tools/        Build, packaging and verification scripts
docs/         Usage and development documentation
licenses/     Third-party notices
```

Build instructions and test commands are in [DEVELOPMENT.md](docs/DEVELOPMENT.md). Bug reports should include the app version, operating system, reproduction steps and relevant errors. Remove account identifiers and private data from logs before sharing them.

## License and acknowledgements

[MIT](LICENSE). Third-party license notices are included in [licenses](licenses/) and the distributed packages. This project builds on the original [Warframe-App](https://github.com/Diskutabell/Warframe-App) interface and functionality.

Warframe is a trademark of Digital Extremes. This is an independent community project and is not affiliated with or endorsed by Digital Extremes.
