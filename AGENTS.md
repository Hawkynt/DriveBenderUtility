# Agent guide — DriveBenderUtility

Working agreement for **all** coding agents and human contributors working in
this repository. These rules are not optional. The full house spec lives in
the `Hawkynt/project-template` repo (`STANDARD.md`); this file is the
per-repo distillation.

## What this is

A C# suite for managing **Drive Bender storage pools** outside the original
product: `DriveBender.Core` (pool/drive/duplication/integrity logic),
`DriveBender.Console`, `DriveBender.UI` (WPF), `DriveBender.Tests`.
Solution `DriveBender.sln`.

## Commits

- **Group changes semantically/logically** — one pool-operation/concern per
  commit.
- **Every subject line starts with a prefix**: `+` added · `-` removed ·
  `*` changed · `#` bug fixed · `!` critical todo.
- Never start a subject with "fix"/"bugfix"/"changed"/"modified".
- **No AI traces anywhere**: no `Co-Authored-By` AI lines, no "Generated
  with" footers, no agent mentions in messages, comments, or authorship.

## The loop (always, in this order)

1. **Before committing**: `dotnet build DriveBender.sln -c Release` and the
   test suite until green (wall-clock Performance tests are the advisory
   tier). Pool-mutation logic gets dry-run coverage — this tool touches
   people's storage arrays.
2. **Commit** (rules above) and **push**.
3. **Wait for CI**; on `main` a green CI triggers the nightly (prerelease +
   GFS prune, same-day replace). Fix and loop until everything is green.

Stable releases are **manual** (`gh workflow run release.yml`) — never cut
one unless explicitly asked.

## Code conventions

- Latest C# features; Core stays UI-free — Console and WPF are thin shells.
- Anything that writes to a pool follows verify-then-act with explicit
  logging; integrity checking must never "repair" without being asked.

## README & repo conventions

- Standard frame: title → badges → one-line `>` blockquote (no Overview
  header); fixed emoji mapping for the standard sections (`## ✨ Features`,
  `## 📦 Getting Started`, `## ❤️ Support`, `## 📜 License`);
  `## 🆘 Getting Help` stays distinct from the funding section.
- License is LGPL-3.0-or-later; the `## ❤️ Support` section and
  `.github/FUNDING.yml` stay intact.
