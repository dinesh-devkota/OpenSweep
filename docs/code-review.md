# Code review and fixes

This review covers the application source, including the recent UI changes. The items below describe confirmed code paths that were corrected; they are not a claim that every Windows integration has been exercised.

The subsequent [Windows care upgrade review](windows-care-upgrades.md) documents the review-first health workflow, startup filtering/reporting, update parsing and stale-result fixes, and expanded validation.

## Highest-priority findings

- **Registry changes could proceed without a complete backup.** Failed exports were ignored, and a header-only or partial file could be accepted. Backups now require successful exports of every requested key, validate the expected sections, use unique filenames, and publish only after all exports complete. Startup removal also backs up the saved enabled/disabled state.
- **Duplicate deletion trusted stale scan results.** A kept copy or selected file could change without changing its size. Deletion now rehashes both sides, requires a verified kept copy, holds read handles during recycling, and rechecks file identities immediately before the native operation. Excluded paths, links, and groups with every copy selected are rejected.
- **Cleanup could follow a junction supplied as its root or through an ancestor.** Rules now reject those paths and file reparse points, honor exclusions, and check cancellation per file. The exact nonrecursive Windows MEMORY.DMP rule is allowed without permitting wildcard or recursive cleaning of protected roots.
- **Service cleanup could leave Windows Update services stopped.** Services stopped by OpenSweep are recorded immediately and restarted from a finally block, including stop-timeout and cancellation paths.
- **Cancellation and concurrent operations could cause unintended follow-on work or incorrect results.** Cleanup preparation is guarded, overlapping shared cleaner runs are rejected, cancellation is checked before registry fixes and between Windows update stages, and partially completed cleanup totals are preserved.

## Additional fixes

- Settings are flushed to a unique temporary file and atomically replaced; failed serialization preserves the last valid file. Exclusions are normalized with correct path boundaries and tolerate null configuration lists.
- Process execution handles pre-cancellation before launching, drains both output streams, serializes output callbacks, and returns callback errors through the task. Shared Windows argument quoting handles embedded quotes and trailing backslashes.
- Access failures are no longer treated as proof a referenced path is missing. Recycling counts only submitted paths confirmed missing afterward. The 32-bit shell-operation structure now uses the correct packing.
- Partial Windows Update searches and failed individual update results no longer appear successful. A cancelled download does not proceed into installation, while prior restart requirements are retained. Failed winget checks cannot be accepted based on partial output.
- Disabling a scheduled task checks removal failures and distinguishes missing tasks from access errors. Uninstall stops if automatic cleanup cannot be disabled; failed or timed-out Setup uninstallers retain settings and backups.
- Separate installed-program registry entries are no longer hidden by matching names and versions. MSI inference requires the correct executable and product-code argument.
- Restore-point frequency overrides restore the original value/type only when the override succeeded. Native handles and cancellation sources reviewed in these paths are disposed.
- Health summaries distinguish incomplete/failed/cancelled checks from healthy results. Disk analysis no longer counts an empty directory as inaccessible and handles extended UNC paths.

## Validation

- 36 portable managed regression checks passed on Linux with no failures or skips. See [the regression suite](../tests/OpenSweep.RegressionTests/README.md) for coverage and execution instructions.
- The .NET Framework 4.8 WPF Release build and portable packaging command completed with zero warnings or errors.
- GitHub CI now runs the managed checks on Linux and Windows, and builds the application on Windows. Adding the workflow does not establish that a remote run has passed.
- The existing UI control/event connections, dynamic theme resources, and modern Windows typography/corner changes were retained.

## Windows validation still required

WPF rendering, registry import/export permissions, service stop/restart, Windows Update/winget, Task Scheduler, native shell recycling, file locking, 32-bit native layout, and installer/uninstaller integration could not be exercised in this Linux environment. No real system cleanup, registry changes, updates, or uninstalls were run during this review.

Windows Update's synchronous COM calls cannot be interrupted immediately; cancellation stops the next stage. Shell recycling is path-based: holding verified files and checking their identities reduces stale-file risk but cannot make the subsequent native path operation atomic against every concurrent filesystem rename.
