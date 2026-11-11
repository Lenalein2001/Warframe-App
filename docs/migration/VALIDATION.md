# Validation record

## 2026-11-11

- The local suite passed 52/52 checks, including the supplied private relic screenshot, synthetic reward/riven OCR, Proton library discovery, desktop-specific screenshot selection, notification callback isolation and catalog compaction.
- Windows package CI run `43050113767` measured 27,780,060 bytes installed / 12,898,265 bytes zipped for the framework-dependent edition, and 138,121,932 / 62,998,602 for the self-contained edition. Both editions passed isolated first-start, restart, settings persistence and clean shutdown. The smaller ZIP is about 48% smaller than the preceding 24,843,555-byte package.
- The OCR executable is built from pinned Tesseract 5.5.4 and Leptonica 1.87.0 sources, with a static runtime and no unused network/archive/external-codec dependencies. The original English LSTM model is unchanged. Internal PPM transport preserves pixels without PNG compression/decompression.
- Final Windows package CI run `43051414326` also passed at `204abbf`, including both editions and installer checks. Windows package CI run `43050113767` passed package and installer install/start/upgrade/uninstall checks at `d81eedc`. The downloaded framework-dependent package independently recognized all four supplied screenshot rewards with its bundled minimal OCR engine. The native Windows notification test was accepted by the OS API through Settings; this does not establish that banners are visible under every notification policy.
- Visual checks covered the inventory review and reset controls, Settings notification preview, and the corrected closed-game status. Filtering showed 23 mastered weapons with no known crafting uses; clearing restored 1,315 inventory rows.
- Thirty warm local API samples per route measured median latency of 3.36 ms for state, 8.50 ms for inventory, 23.76 ms for mastery, 18.36 ms for primes and 33.29 ms for relics. These are observations on this machine, not a controlled comparison with the original app. Full raw drop tables are no longer retained after indexing; catalog metadata keeps only definitions and labels used at runtime.
- GitHub's language API now reports C# as the largest language and no Rust entry.
- An isolated offline catalog probe kept the loader alive, allowed loading continuations to unwind, then forced collection before measuring managed memory. With identical cache inputs, retained memory fell from 164,320,392 to 130,007,760 bytes (about 21%). Normalized items, parts, relics, crafts, inventory definitions, recipes, crafting uses and drop indexes had the same SHA-256 before and after. This measures the catalog probe, not total desktop/WebView process memory or frame rate.

Ubuntu 24.04 passed all 51 maintained checks including OCR in CI run `43051414573` at `204abbf`. Both the Ubuntu source build and packaged Nix desktop passed GTK window startup, native libnotify delivery to dunst, X11 screenshot/overlay creation and clean shutdown in the same run. This uses Xvfb and an isolated D-Bus session. The test exposed and fixed missing direct GLib runtime dependencies. Synthetic OCR now renders the bundled font rather than relying on host fonts. No Linux machine with Warframe is available locally, so live Proton gameplay and Wayland compositor validation remain outstanding.

## Earlier validation — 2026-11-10

The Windows C#/C/C++ replacement has been exercised with the original interface, migrated user data and a running game. Platform-specific limits are listed separately from passing tests.

## Verified

- The combined local suite passed 48/48 checks, including OCR and both live-memory tests. Release builds treat warnings as errors. Managed tests cover parsing, SQLite rollback/concurrency, account isolation, migration, service calculations, HTTP validation, embedded assets, platform changes, log rotation and native integration.
- All 46 original HTTP routes are implemented. All 31 UI assets are retained; intentional changes center the settings gear and add inventory crafting-use controls and detail sections.
- Native C++ tests cover real read-only process access, cancellation, bounded search, image preprocessing, reward layout and Unicode matching. Windows and Ubuntu jobs and the complete Nix package build passed at commit `e46d19a` in Actions run `42889410404`.
- Live Windows memory capture found 1,349 stacks in 23 sections in approximately three seconds and matched the known account. A separate live watcher test promoted the capture to an isolated database. The rebuilt desktop then updated the normal user inventory successfully. No game writes or injection are performed.
- The supplied private relic screenshot produced all four expected names in order: Kestrel Prime Blade, Citrine Prime Blueprint, Caliban Prime Blueprint, Bronco Prime Barrel. Synthetic reward and riven OCR tests also passed. The private screenshot is not stored in this repository.
- The C# screenshot replay command independently recognized the same four rewards without recording reward history.
- A rebuilt original baseline and the C# app matched core collection counts and identifiers: 810 mastery entries, 162 prime sets, 799 relics, 1,313 visible inventory rows, nine pending builds, 955 craft entries and two farming tasks in the same saved-data comparison. Live inventory has subsequently changed.
- Visual navigation covered Overview, Mastery, Prime sets, Relics, Fissures, World, Standing, Foundry, Market, Farming, Inventory, Settings and an item detail drawer. Missing market thumbnails were traced to a missing User-Agent and repaired. Settings gear alignment was corrected.
- The final Windows package showed four separate reward panels at the expected game-relative positions, with transparent gaps and restored foreground focus, and disappeared on its timer. Cross-process click-through is configured but was not exercised by injecting input into the live game.
- Framework-dependent and self-contained Windows packages passed isolated first-start, restart, settings persistence and clean shutdown checks. Shipping archives exclude PDBs, source files and user databases.
- Windows installer CI run `42885633509` built both packages and the installer, then passed install, launch, upgrade and uninstall checks, preserving user-data hashes and sentinels.

## Package measurements

The 21:33 build measured 54,842,532 bytes installed / 24,843,555 bytes zipped for the framework-dependent edition, and 165,148,868 / 74,923,508 bytes for the self-contained edition. Its English data is 5,199,090 bytes instead of 23,466,654: the original LSTM network, dictionaries, character map, recoder and version are retained byte-for-byte, while unused legacy-engine data is omitted. CI uses Tesseract's own extraction/packing utility and checks the resulting SHA-256. OCR explicitly selects the LSTM engine. The suite passed 47/47 checks with this data, including synthetic reward/riven recognition and the supplied screenshot. Later documentation changes may alter package size slightly.

Crafting-use regression checks cover consumed weapon quantities, multi-step chains, duplicate recipe sources, mastered targets and missing recipe data. Against the loaded catalog, Lato → Bolto → Akbolto → Akjagara returned quantities 1, 2 and 1. JavaScript syntax and interface contracts passed. Desktop input was stopped with Escape before the new controls could be visually verified.

Thirty-sample warm API probes during live gameplay returned median latencies of approximately 17–50 ms for state, inventory, mastery, primes and relics, with substantial background-load variation. These observations do not establish a throughput improvement over the preceding build or the original backend. Concrete changes avoid repeated snapshot reads, let local thumbnails bypass network downloads, and pre-index consuming recipes. Use `tools/benchmark-api.py` with identical data and stable load for controlled comparisons.

The 21:02 Windows package, including OCR and its licenses, measured 73,099,373 bytes installed / 32,744,484 bytes zipped for the framework-dependent edition, and 183,405,709 / 82,824,437 bytes for the self-contained edition. Both editions passed isolated startup, settings persistence, restart and shutdown checks. The main executable is approximately 159 KiB. Removing an unnecessary dependency PDB saved approximately 91.5 MiB installed. Comparing only launcher executable sizes omits runtimes, libraries and OCR data.

## Limits of the evidence

Linux GUI/compositor behavior has not been exercised on a live Linux desktop. Multi-monitor DPI combinations, fresh login/account-switch/game-restart capture transitions, and live riven OCR across themes and scales remain manual checks. Windows installer validation used CI, not a clean physical PC. Passing tests do not establish perfection or universal game-version compatibility.

Non-OCR inventory capture is working. Riven fingerprint decoding accepts explicit game data; automatic selection of the currently hovered riven is not implemented. Automatic reward-choice recognition still uses the OCR fallback when the game does not expose a reliable direct source.

