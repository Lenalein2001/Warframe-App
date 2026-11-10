# Validation record — 2026-11-10

Scope: desktop C#/C/C++ replacement candidate. Full parity is not yet certified.

## Confirmed locally on Windows

- Release builds succeed with warnings treated as errors.
- 37 regression checks pass when run with `--ocr`: parser/SQLite safety, service semantics, account isolation, legacy migration, history, settings, update selection, memory candidate preference and HTTP contracts.
- HTTP tests serve every embedded frontend asset byte-for-byte, exercise main page routes offline, reject foreign Host/Origin values, and verify import/clear/history behavior.
- Native C++ regression executable passes bounded search, pixel conversion, sequence matching and synthetic reward-layout checks. It reports a 100-frame 1080p layout timing; this is a microbenchmark, not gameplay performance proof.
- The installed Tesseract executable reads synthetic reward and riven images through native preprocessing. The source image remains unchanged.
- All 31 frontend hashes and 46 original route names match the baseline.
- An isolated online headless instance copied the existing account data without modifying Rust files and loaded 810 mastery entries, 162 prime entries, 799 relics, 1,346 inventory stacks and reward history. Main page endpoints returned HTTP 200. Current DE recipe loading and arbitration schedule loading reported no data-source errors.
- The original frontend was displayed in the Photino Windows host. The main window and overview were visually inspected. Overlay bridge rendering executed, but full overlay visual/focus/click-through verification remains outstanding.
- Read-only capture was attempted against the running game using only the isolated candidate database. No usable inventory payload was found; the confirmed migrated snapshot was retained. This is evidence of safe failure, not successful live capture.

## Not yet established

Linux runtime/compositor behavior, clean-machine installation, complete visual navigation, actual reward/riven recognition, full game-memory lifecycle, installer/uninstaller behavior, and end-to-end performance remain unverified. Existing inventory-only artifact measurements are obsolete for desktop package comparisons.

The repository CI matrix builds native components and runs .NET checks on Windows and Ubuntu. A workflow file alone does not establish that remote CI passed; record actual run results separately.
