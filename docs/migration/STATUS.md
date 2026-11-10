# C# replacement status

Baseline: Rust fork `decc9b5` (0.9.0). The original Rust source and all 31 frontend files are retained unchanged. The compatibility manifest records 46 HTTP routes.

## Implemented replacement

- Photino desktop window and embedded original frontend; loopback-only ASP.NET Core HTTP compatibility host with Host and Origin checks.
- Account switching and linking, public profiles, game-log account detection, copy migration of legacy accounts/settings/inventory/manual data/reward history.
- SQLite inventory snapshots, ordered promotion, partial-observation retention, account isolation, history, backups and clear-current semantics.
- Catalog and public export loading, current DE recipes through native C LZMA decoding, drop sources, mastery, prime sets, relic probabilities, inventory, foundry and farming plans.
- Market metadata and bounded price queue, platform selection, riven tables/grading/listings, image caching and resizing.
- World state, fissures and watches, arbitration schedule, syndicate standing and offerings, Duviri offers, timers and notifications.
- Windows and Linux process readers; read-only inventory scan, ownership checks, section coverage and stale-data protection. Automatic scans after log events and periodically during long sessions.
- Native C++ reward-card detection, OCR pixel variants, Unicode sequence matching; Tesseract recognition and riven text interpretation; reward history and delayed price updates.
- Windows hotkeys, desktop notifications and native transparent overlays; Linux GTK layer-shell/X11 overlay implementation and Hyprland/Sway binding installation.
- Fork release discovery restricted to C# package assets, feeding the original update popup. It does not install updates automatically, matching the Rust behavior.

## Remaining completion gates

This status is deliberately not a declaration of full parity. The following require additional work or evidence before release:

- Finish differential page/response checks against the Rust engine, including less common recipe, standing, world-state and account transitions.
- Validate capture ownership/completeness across fresh login, mission completion, account switch and game restart. The first local live scan found no usable payload and preserved the migrated snapshot.
- Validate OCR on actual reward/riven screens across UI scales, themes, hover states and multi-monitor setups; synthetic OCR success is not live-game evidence.
- Inspect overlays for placement, click-through, focus preservation, price updates and close behavior. Validate Windows DPI and Linux compositor paths.
- Complete Linux game-window capture geometry and compositor fallback behavior; compile and run CI and a Linux GUI environment.
- Validate packaged builds on clean machines, dependency notices, installed-mode setup and uninstall behavior. No final installer or release has been published.
- Run performance measurements for startup, memory, capture latency and OCR under realistic workload. Native hot-path implementations alone do not demonstrate a performance improvement.

The preserved interface remains the acceptance surface. An implemented route or passing fixture is not sufficient evidence that every original interaction works.

## Requested follow-up after migration completion

- Make `senpai` the default branch. First move the active `senpai/csharp-replacement` branch out of that Git ref namespace, then establish `senpai` and update the default branch and workflow references.
- Remove obsolete PR branches after a final ancestry check. On 2026-11-10, the six remote `pr-*` branches each had zero commits absent from `origin/main`; none needs to be retained to preserve its changes.
- Reduce desktop package size and test the reduced artifacts. The first measured package included an unnecessary 91.54 MiB `libSkiaSharp.pdb`; exclude dependency debug symbols from shipping packages. Measure OCR assets, native dependencies and bundled .NET separately, and evaluate optional OCR installation while retaining the fallback. Do not promise a final size before publishing and testing.
- Keep package directories separate from runtime/test data. GUI launch testing showed that a relative `--data-dir` can resolve inside the executable directory; use absolute test data paths and verify archive contents before release.

## Latest validation checkpoint

- Process-memory enumeration, read-only access, bounded buffers and scanning now live in C++ on Windows and Linux. C# retains JSON validation, account ownership and snapshot promotion. Native tests read a heap fixture from their own process and verify cancellation; this does not establish successful Warframe inventory capture.
- Background memory scans no longer block game-log polling. UTF-8 log tails preserve split characters and suppress historical reward events.
- An explicit-data riven fingerprint API decodes rolls without OCR using current export definitions. Automatic selection of a hovered riven from game memory is not implemented.
- Windows native tests and 41 managed/integration/image tests passed, including all four exact reward names in the supplied external screenshot. The screenshot is not included in the repository.
- Release publishing excludes dependency PDB files. A framework-dependent runtime-only publish measured 17,789,461 bytes in 19 files, with no PDBs. This excludes OCR, licenses and the shared .NET runtime; it is not the final distribution size.
- Linux capture geometry and overlay coordinate handling have additional changes awaiting CI and compositor runtime verification.
