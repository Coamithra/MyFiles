# MyFiles — Harald's personal fork of Files

This is a personal fork of [files-community/Files](https://github.com/files-community/Files), the open-source WinUI 3 file manager, turned into Harald's own Windows Explorer replacement. The upstream `CLAUDE.md` / `AGENTS.md` at the repo root hold upstream's coding conventions, build commands and review rules — follow them. This file holds only what is specific to the fork. It lives in `.claude/` so the root files stay identical to upstream and merges never conflict on them.

## Why a fork, not Explorer tweaks

Explorer can only be trimmed through registry hacks and Windhawk hooks, and it cannot gain real features (a proper bookmarks tree, Everything search, reopen-closed-tab). Files already has parity with Explorer plus a real Settings page, so every change Harald wants becomes a preference there instead of a registry edit. Guiding principle: noise is hidden by default or moved into Settings, never just deleted without a way back.

## Repo and remotes

- `origin` = `Coamithra/MyFiles` (public fork). `upstream` = `files-community/Files`, fetch-only (push URL is `no_push`).
- Default branch: `main`. Pull upstream with `git fetch upstream` + merge `upstream/main` into `main`; keep fork changes small and localized so those merges stay cheap.
- Prefer adding new files/services over editing hot upstream files; when an upstream file must change, keep the diff minimal.

## Project tracking (Trello — local backend)

The board is a **local** (file-backed) Trello board in the `trello` CLI's local store, not on trello.com.

- **Board:** `MyFiles — Explorer replacement` · id `8bd03a82`. Always pass `--backend local --board 8bd03a82`.
- **Columns:** `To Do` `1a21de83` · `Doing` `f10844b0` · `Done` `9859000d` (the CLI's `grab` defaults match, so no `--from`/`--to` needed).
- The board is the backlog (Harald's wishlist: declutter-as-preferences plus new features). Check it for live status rather than trusting any summary.
- **When picking up a card, follow `~/.claude/CONTRIBUTING.md`** with these specifics:
  - Worktrees: `.claude/worktrees/<branch-name>` (ignored via `.claude/.gitignore`). No per-worktree bootstrap beyond `msbuild -restore`.
  - Verification gate: the focused `msbuild` build from the root `CLAUDE.md` succeeds with `-clp:ErrorsOnly`, and the change is checked in the running app. Upstream has no agent-usable test suite.
  - PRs target `main` of `Coamithra/MyFiles` (never upstream): `gh pr create --repo Coamithra/MyFiles --base main`. Ignore the root `CLAUDE.md`'s upstream PR-title prefixes and template.

## Toolchain

- Visual Studio 2026 Community (18.x) at `C:\Program Files\Microsoft Visual Studio\18\Community`, with .NET desktop, WinUI application development, Windows App SDK C# templates, Windows 11 SDK 26100, MSVC x64 + ATL. VS 2022 is also installed and is too old for this repo.
- .NET 10 SDK (pinned by `global.json`). The solution contains C++ projects (Launcher, OpenDialog, SaveDialog), hence MSVC + ATL.
- Upstream's DevShell example uses the `Professional` path; on this machine substitute `Community`.

## Out of scope

Windows resetting default apps is a Windows association issue, not a file-manager one.
