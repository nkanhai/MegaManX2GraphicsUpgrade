# X2 HD-2D engineering instructions

Preserve the original simulation; enhance its presentation. This directory is
our unofficial, noncommercial remaster experiment, separate from Matthew
Stanley's MegaManX2Recomp and its framework dependencies.

- Read README.md and docs/roadmap/SPRINT_0_REPORT.md before changing scope.
- Keep additions here unless a verified integration requires a small upstream
  edit. Do not change gameplay, dependency pins, or generated game code merely
  to organize this project. Preserve history, attribution, LICENSE,
  PERMISSIONS.md, CONTRIBUTING.md, and dependency notices.
- Verify source against docs/architecture/upstream-lock.json. Distinguish
  observed facts, design proposals, and untested hypotheses. X2 does not use
  every rendering facility present in the shared framework.
- Never fetch or commit game ROMs, firmware, extracted assets, saves, captures,
  generated recompilation output, or Unity Library/build data. Use hd2d/local/
  for local captures. Only original synthetic fixtures belong in fixtures/.
- OAM slots are graphics pieces, not stable gameplay entities. PPU scroll is
  not a verified world-space camera. Keep semantic inference separately tagged.
- Capture on the simulation owner thread; publish immutable completed records.
  Never serialize a native Ppu struct or read mutable PPU arrays from a network
  worker. Keep frame/session identity and capture phase explicit.
- Check ROM-independent changes with:
  `python3 -m unittest discover -s hd2d/tests -v`.
  Use `python3 hd2d/tools/check_upstream.py` for the focused native checks.
  Document tool versions, commands, failures and skipped ROM/hardware checks.
- Start with recorded data and fidelity validation. Do not add Unity scene
  boilerplate or select the final native integration before evidence warrants it.
- Finish each increment on a feature branch with a commit and a precise report.
  Open a PR in the owner's confirmed fork when access permits; do not send this
  experiment to upstream as an unsolicited contribution.
