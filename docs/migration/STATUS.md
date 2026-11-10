# C# replacement status

The active checkout contains C#, C and C++ application code, with no Rust source, Cargo project or Rust build dependency. The original implementation remains available in Git history at baseline `decc9b5`.

`senpai` is the default and only branch in the fork. The old main and PR branch tips were verified as ancestors before removing those redundant branch references; their history remains reachable.

## Implemented

- Preserved desktop interface hosted by C# Photino and a loopback ASP.NET Core service; 46 compatible API routes.
- Accounts, profiles, manual counts, mastery, pins, farming plans, history and settings migration. Original data is preserved. Learned memory-section requirements migrate with each account.
- SQLite inventory snapshots with ownership, completeness, timestamp ordering, transaction rollback, history and backup protections.
- Catalogs, current public exports, recipes, drop sources, mastery, prime sets, relic values, inventory, foundry, farming, standing, world events, Duviri offers, market prices and riven grading.
- C++ process-memory scanning, reward-card layout, preprocessing and fuzzy matching; C LZMA export decoding. Live Windows inventory capture and watcher persistence pass.
- Non-OCR public data/log/import/memory paths and explicit riven fingerprints. OCR remains the fallback for reward choices and on-screen riven cards.
- Windows hotkeys, notifications and reward/riven overlays; Linux native overlay and compositor binding implementations.
- Desktop, browser and headless modes; trigger/riven/quit commands and saved-image replay.
- Reduced portable packages and a per-user Windows installer with upgrade/uninstall data preservation tests.

## Acceptance evidence and remaining checks

See [VALIDATION](VALIDATION.md) for actual runs, visual observations, measurements and outstanding platform-specific checks. Full Linux GUI and every live-game lifecycle combination are not certified. No final public release has been published.

The preserved interface remains the acceptance surface. The settings gear is an intentional small correction; its reviewed asset hash is recorded in the interface contract.
