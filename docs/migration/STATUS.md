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
