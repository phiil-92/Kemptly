# Security

## What Kemptly does and doesn't do

- **No network service.** Kemptly never opens a listening port and never accepts connections.
- **No telemetry, no auto-update, no downloads.** The only network traffic it causes is reverse-DNS lookups
  for the Network page and Microsoft Defender's own definition updates when you click *Update definitions*.
- **Nothing runs in the background.** The installer adds no service, scheduled task or startup entry. Kemptly
  only runs while you have it open.
- **No elevation by default.** It runs with normal user rights. Admin rights are only used when you click
  *Restart as administrator* or a Tools action marked with a shield, and Windows asks you first (UAC).
- **Nothing changes without a click.** Every delete, disable or uninstall needs a button press and confirmation.
- **No command-line backdoor.** The only arguments are `--page <name>` and `--scan`, which open a page and start
  a read-only scan.

## Hardening

- Windows tools (`cmd`, `powershell`, `explorer`, `powercfg`, ...) are always started by their full path in the
  Windows folder, with `System32` as the working directory, so a fake copy dropped next to Kemptly is never used.
- The Junk Cleaner deletes through a file handle and checks the file's real location first. If a folder is swapped
  for a junction or symlink during cleaning, the delete is refused instead of following it elsewhere.
- Directory junctions and symlinks are never followed while scanning.
- Uninstallers registered only for the current user show their exact command, with a warning when Kemptly is
  running as administrator.
- *Restart Explorer* never starts Explorer with administrator rights.

## Known limitations

- The per-user install folder (`%LOCALAPPDATA%\Programs\Kemptly`) is writable by your account, like most per-user
  apps (VS Code, Discord, ...). Malware already running as you could replace the exe. Kemptly adds no new way in.
- Releases are not code-signed yet, so Windows SmartScreen will warn on first run.
- The security scan is heuristic and doesn't replace an antivirus.

## Reporting a vulnerability

Please open a [private security advisory](../../security/advisories/new) instead of a public issue.
