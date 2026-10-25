# Security

## What Kemptly does and doesn't do

- **No network service.** Kemptly never opens a listening port and never accepts connections.
- **No telemetry, no automatic network traffic.** Kemptly only goes online when you ask it to: "Check for updates" on the
  App Updates page (winget, which downloads updates from the app makers), "Check for updates now" in Settings (asks GitHub
  for the latest release number; optional at startup, off by default), reverse-DNS lookups on the Network page and
  Microsoft Defender's own definition updates.
- **Nothing runs in the background by default.** The installer adds no service, scheduled task or startup entry. The
  only exception is optional: "Clean automatically every week" creates a scheduled task that runs as you (no admin) once a
  week to clean the safe junk categories, and is removed again when you turn it off.
- **No elevation by default.** It runs with normal user rights. Admin rights are only used when you click
  *Restart as administrator* or a Tools action marked with a shield, and Windows asks you first (UAC).
- **Nothing changes without a click.** Every delete, disable or uninstall needs a button press and confirmation.
- **No command-line backdoor.** The only arguments are `--page <name>` and `--scan` (open a page and start a read-only
  scan) and `--clean-silent`, which runs the same safe junk clean as the weekly schedule.

## Hardening

- Windows tools (`cmd`, `powershell`, `explorer`, `powercfg`, ...) are always started by their full path in the
  Windows folder, with `System32` as the working directory, so a fake copy dropped next to Kemptly is never used.
- The Junk Cleaner deletes through a file handle and checks the file's real location first. If a folder is swapped
  for a junction or symlink during cleaning, the delete is refused instead of following it elsewhere.
- Directory junctions and symlinks are never followed while scanning.
- Uninstallers registered only for the current user show their exact command, with a warning when Kemptly is
  running as administrator.
- *Restart Explorer* never starts Explorer with administrator rights.
- Every change (startup, services, privacy settings, firewall rules, deletions) is logged locally in
  `%LOCALAPPDATA%\Kemptly\history.json` and can be undone from the History page where possible.
- Blocking a program's internet access only creates outbound Windows Firewall rules named "Kemptly block - ...", and
  refuses Windows' own programs.
- The leftover scan after an uninstall only matches folders and registry keys by the app's exact name. It never offers
  drive roots, Windows or personal folders (Documents, Desktop, ...), or anything another installed program still uses.
  Folders go to the Recycle Bin; registry keys and scheduled tasks are exported to `%LOCALAPPDATA%\Kemptly\backups`
  before they are removed. Items that need administrator rights are removed in one elevated step, after a UAC prompt.

## Known limitations

- The per-user install folder (`%LOCALAPPDATA%\Programs\Kemptly`) is writable by your account, like most per-user
  apps (VS Code, Discord, ...). Malware already running as you could replace the exe. Kemptly adds no new way in.
- Releases are not code-signed yet, so Windows SmartScreen will warn on first run.
- The security scan is heuristic and doesn't replace an antivirus.

## Reporting a vulnerability

Please open a [private security advisory](../../security/advisories/new) instead of a public issue.
