# AGENTS.md

Conventions for any AI agent working in this repository.

## Git

- **Never `git push` unless the human explicitly asks for it in that message.** Committing locally is fine and expected; publishing is always their decision.
- **Never `git add -A` / `git add .` blindly.** Stage named paths. If a bulk add seems unavoidable, run `git status` first and report what would be staged.
- **Do not commit build output.** `*.exe`, `*.pdb`, `bin/`, `obj/` are gitignored. Binaries belong in a GitHub Release, not in the tree.
- **Do not commit exported results** (`loopback_*.csv`) — they are per-machine test artifacts.
- Commit author identity comes from global git config (`NW <olay097056@gmail.com>`). Do not override it per-repo or per-commit.
- Do not rewrite history that already exists on `origin/main`.

## The C# version has hard constraints — do not "modernise" it

`SerialLoopbackPro.cs` targets **.NET Framework 3.5, x86, Windows XP SP2+**. This is the entire reason it exists alongside the Python version. Modern C# will compile on your machine and then fail on the target hardware.

Forbidden in that file:

- default parameter values
- string interpolation (`$"..."`)
- `volatile double`
- `var` patterns, LINQ, lambdas beyond what 3.5 supports, `async`/`await`
- any NuGet package, any dependency beyond the BCL and `System.Management`

Also preserve:

- **Font fallbacks** — Segoe UI → Tahoma, Consolas → Courier New. XP SP2 has neither of the first choices. Do not hardcode a font.
- **Owner-drawn table and gauge.** They are hand-painted with GDI+ deliberately, not for lack of a control. Do not replace them with a DataGridView or a third-party control.
- **Single-file, no-installer output.** The deployment story is "copy one 40 KB exe onto the machine, run it, delete it". Anything that adds a dependency breaks it.

## Behaviour that must not regress

- **Loopback probe runs before the baud sweep.** Without a plug, a dead port and a missing jumper are indistinguishable, and a full sweep of FAILs looks authoritative while meaning nothing. Report `NO LOOP` and wait — never produce a results table in that state.
- **The port must be fully released on Stop.** The worker thread is joined before the Start button re-enables, and the log confirms it. A tool that holds the handle after Stop breaks whatever the user opens next.

## Docs

- `README.md` and `README.th.md` are kept in sync. Updating one means updating the other.
