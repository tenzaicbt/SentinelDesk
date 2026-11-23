---
name: publish-public
description: Sync SentinelDesk improvements from the private source repo to the public release repo. Commits and pushes the private repo (github.com/tenzaicbt/SentinelDesk), rebuilds, writes user-facing release notes, and updates and pushes the public repo Git/ (github.com/tenzaicbt/SentinelDesk-EDM). Use when the user asks to publish, release, sync or update the public repo, or to "push improvements to public".
---

# Publish improvements to the public repo

Two repositories (see `doc/repo-details.md`):

| | Folder | Remote | Contains |
|---|---|---|---|
| Private | `C:\Users\yohan\Documents\GitHub\SentinelDesk` | github.com/tenzaicbt/SentinelDesk | full source code |
| Public | `C:\Users\yohan\Documents\GitHub\SentinelDesk\Git` (git-ignored in private) | github.com/tenzaicbt/SentinelDesk-EDM | end-user files only |

The public repo must never get source code, build/dev scripts, dev settings, keys, databases, source maps or internal
implementation detail. `publish-public.ps1` enforces the file rules; you keep the *text* (README, CHANGELOG, commit
messages) free of developer detail.

## Steps

### 1. Private repo: commit and push any improvements

1. `git status` and `git diff` in the private repo. If there are uncommitted changes, review them and commit in
   logical commits with clear messages (match the existing log style: short imperative summary). Never commit secrets
   (`.env`, `data/`, keys, `*.db`). Never add `Co-Authored-By: Claude` or any other Claude attribution to commit
   messages, tags or release notes in either repo.
2. `git push origin main`. If the push is rejected, stop and tell the user. Do not force-push.

### 2. Find what changed since the last public release

```bash
last=$(cat Git/.git/sentineldesk-source-commit 2>/dev/null)
git log --oneline ${last:+$last..}HEAD
```

If the file is missing, use the public repo's last commit date: `git log --since="<date>"`.
If no relevant commits are new and nothing changed, tell the user the public repo is already current and stop.

### 3. Write the user-facing text (in `Git/`)

- **`Git/CHANGELOG.md`**: add a section at the top, `## <version> — <YYYY-MM-DD>`, with bullets for what users
  notice: new features, fixes, security improvements. Plain language for IT administrators. Leave out refactors,
  internal names, file paths, class names and dev tooling. Create the file if it doesn't exist (title `# Changelog`).
  The version is the agent's ProductVersion; if unsure, read it after the build in step 4 and fill it in then.
- **`Git/README.md`**: only if a change adds or changes a user-visible feature, update the *Features*, install or
  *Privacy* sections to match. Keep the existing tone and structure. Version numbers and file names are updated by
  the script; don't edit those by hand.
- If the private `README.md` gained new screenshots under `doc/screenshots/`, the script copies them; reference them
  from `Git/README.md` as `docs/images/<name>.png`.

### 4. Build, package, commit and push the public repo

Run from the private repo root (PowerShell, can take several minutes):

```powershell
./publish-public.ps1 -Message "SentinelDesk <version>: <one-line summary of the main improvement>"
```

- `-SkipBuild` reuses the existing `publish/` output (only if it was built from the current commit).
- `-NoPush` commits without pushing (use if the user asked not to push).

If the script stops on a safety check ("Refusing to publish..." or the 100 MB limit), do not work around it. Report
it to the user and fix the cause in the private repo.

### 5. Report

Tell the user: private commits pushed, version published, the release-notes bullets, and the public repo link
(https://github.com/tenzaicbt/SentinelDesk-EDM). Mention any GitHub large-file warnings only briefly.
