# Windows care upgrade rationale

The upgrade focuses on practical maintenance tasks: understanding storage use, reviewing startup apps, reducing unwanted interruptions, and finding Windows' own update and repair controls. These are supported problem categories, not a measured ranking of user complaints. No user telemetry or complaint-frequency study was used.

## References and resulting changes

| Official source | Documented behavior | OpenSweep change |
| --- | --- | --- |
| [Microsoft PC Manager](https://pcmanager.microsoft.com/en-us) | Storage Sense, startup management, health checks, and a Windows toolbox | Windows care guidance and direct settings links |
| [BleachBit](https://www.bleachbit.org/features) | Descriptions, category selection, preview, then deletion | Review-first health workflow and conservative defaults for cleanup categories with recovery or troubleshooting value |
| [Autoruns](https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns) | Detailed startup inspection, reversible disabling, filtering, and CSV output | Search and state filters, visible unknown states, and export for the startup locations OpenSweep supports |
| [Bulk Crap Uninstaller](https://www.bcuninstaller.com/) | Filtering and export, original uninstallers, and certificate information | Retain native uninstall behavior; avoid replacing it with speculative file removal |
| [WinDirStat](https://windirstat.net/) | Largest-file investigation, visualization, and report export | Export the existing largest-file view with scan metadata |

[Microsoft's performance guidance](https://support.microsoft.com/en-us/windows/tips-to-improve-pc-performance-in-windows-b3b3ef5b-5953-fb6a-2528-4bbed82fba96) identifies low storage, startup applications, outdated software, and hardware limits as possible contributors to poor performance. A startup entry count alone does not establish a performance problem. The health overview therefore reports what it found without assigning a synthetic performance score or calling registry leftovers evidence of an unhealthy PC.

Windows care uses [documented Settings URIs](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings-app) and points system-file repair to [Microsoft's DISM and SFC instructions](https://support.microsoft.com/en-us/windows/using-system-file-checker-in-windows-365e0031-36b1-6031-f804-8fd86e0ef4ca). Settings availability and labels vary by Windows version and organizational policy. Opening a settings page does not apply a change.

## Behavior and boundaries

- Health checks do not delete files or modify the registry. Review cleanup opens Custom Clean and obtains a fresh preview for the current selection.
- New default selections preserve Recycle Bin contents, diagnostic data, and caches that need rebuilding. Explicit saved selections retain their meaning; review them when upgrading.
- Startup search covers Run keys and Startup folders, not every autorun mechanism that Autoruns supports. Unreadable sources and unknown approval flags are reported instead of being counted as enabled or healthy.
- Startup and largest-file CSV exports are user initiated. Cells are escaped and formula-like values neutralized for spreadsheet use. Reports can include commands or full file paths; they are saved only to the chosen destination.
- The Windows care snapshot is read-only. It records basic OS, memory, drive-space, and current access information; it does not claim to measure startup impact, hardware health, malware, or system-file integrity.
- No automatic debloating, security-service disabling, registry performance tweaks, or memory-purging features were added. These actions would require evidence of a specific problem and separate validation.

## Regression failure reported during this work

The earlier Windows regression failure in [run 41766308534](https://github.com/dinesh-devkota/OpenSweep/actions/runs/41766308534) exposed Windows environment-variable expansion truncating a malformed path at an embedded NUL. Fix `009d376` validates path characters before expansion and was merged in `630429a`.

[The subsequent main run](https://github.com/dinesh-devkota/OpenSweep/actions/runs/41767291133) passed: Windows 35 checks plus one skipped symbolic-link fixture, Linux 36 checks, and a Windows application build. This historical fix remains covered; the upgrade branch adds checks for its new managed helpers.

## Additional review fixes

- Winget table parsing previously sliced UTF-16 character positions as if they were terminal columns. Long CJK names could move the wrong field into the package ID. The parser now accounts for wide characters and combining marks, preserves spaced source names, and fails visibly on ambiguous or truncated targets.
- Failed or cancelled update refreshes invalidate previous results instead of leaving a successful check flag behind. Pending checks are shared with the health overview, and installs require a successful check.
- Command-line installation and installation from Settings now share schedule migration. If migration fails after the files are installed, the app reports that partial result and preserves saved schedule settings.
- Initial Windows care navigation waits for the shared system-information load before capturing OS and memory details. Storage result actions are disabled while a new scan is running.

## Validation

- The portable managed suite passes 65 checks on Linux, including the existing malformed-path regression and new startup, CSV, snapshot, navigation, and winget fixtures.
- `pwsh -NoLogo -NoProfile -NonInteractive -File ./build.ps1 -NoInstaller` builds and packages the .NET Framework 4.8 WPF app with zero warnings or errors.
- All 17 XAML files parse, their simple named-resource references resolve, and `git diff --check` passes.

Native WPF appearance, Windows settings launches, actual startup mutations, and update/install integrations require an interactive Windows smoke test. CI compilation and managed tests do not exercise these operating-system actions.

One existing cancellation limitation remains: stopping a launched process does not reliably stop all installer descendants, and inherited output streams may delay completion. Process-tree termination needs Windows installer integration testing; this upgrade does not claim to solve it. Windows Update's synchronous COM stages also finish before cancellation can stop the next stage.
