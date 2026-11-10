# Validation record — 2026-11-10

The Windows C#/C/C++ replacement has been exercised with the original interface, migrated user data and a running game. Platform-specific limits are listed separately from passing tests.

## Verified

- The combined local suite passed 48/48 checks, including OCR and both live-memory tests. Release builds treat warnings as errors. Managed tests cover parsing, SQLite rollback/concurrency, account isolation, migration, service calculations, HTTP validation, embedded assets, platform changes, log rotation and native integration.
- All 46 original HTTP routes are implemented. All 31 UI assets are retained; the sole intentional frontend change centers the settings gear, requested during visual review.
- Native C++ tests cover real read-only process access, cancellation, bounded search, image preprocessing, reward layout and Unicode matching. Windows and Ubuntu jobs and the complete Nix package build passed at commit `e46d19a` in Actions run `42889410404`.
- Live Windows memory capture found 1,349 stacks in 23 sections in approximately three seconds and matched the known account. A separate live watcher test promoted the capture to an isolated database. The rebuilt desktop then updated the normal user inventory successfully. No game writes or injection are performed.
- The supplied private relic screenshot produced all four expected names in order: Kestrel Prime Blade, Citrine Prime Blueprint, Caliban Prime Blueprint, Bronco Prime Barrel. Synthetic reward and riven OCR tests also passed. The private screenshot is not stored in this repository.
- The C# screenshot replay command independently recognized the same four rewards without recording reward history.
- A rebuilt original baseline and the C# app matched core collection counts and identifiers: 810 mastery entries, 162 prime sets, 799 relics, 1,313 visible inventory rows, nine pending builds, 955 craft entries and two farming tasks in the same saved-data comparison. Live inventory has subsequently changed.
- Visual navigation covered Overview, Mastery, Prime sets, Relics, Fissures, World, Standing, Foundry, Market, Farming, Inventory, Settings and an item detail drawer. Missing market thumbnails were traced to a missing User-Agent and repaired. Settings gear alignment was corrected.
- The reward preview showed four separate panels at the expected game-relative positions and disappeared on its timer. Additional focus/click-through changes require the final package recheck.
- Framework-dependent and self-contained Windows packages passed isolated first-start, restart, settings persistence and clean shutdown checks. Shipping archives exclude PDBs, source files and user databases.
- Windows installer CI run `42885633509` built both packages and the installer, then passed install, launch, upgrade and uninstall checks, preserving user-data hashes and sentinels.

## Package measurements

The 20:45 Windows package, including OCR and its licenses, measured 73,090,028 bytes installed / 32,740,111 bytes zipped for the framework-dependent edition, and 183,396,364 / 82,820,064 bytes for the self-contained edition. Final builds may differ slightly. The main executable is approximately 159 KiB. Removing an unnecessary dependency PDB saved approximately 91.5 MiB installed. Comparing only launcher executable sizes omits runtimes, libraries and OCR data.

## Limits of the evidence

Linux GUI/compositor behavior has not been exercised on a live Linux desktop. Multi-monitor DPI combinations, fresh login/account-switch/game-restart capture transitions, and live riven OCR across themes and scales remain manual checks. Windows installer validation used CI, not a clean physical PC. Passing tests do not establish perfection or universal game-version compatibility.

Non-OCR inventory capture is working. Riven fingerprint decoding accepts explicit game data; automatic selection of the currently hovered riven is not implemented. Automatic reward-choice recognition still uses the OCR fallback when the game does not expose a reliable direct source.

