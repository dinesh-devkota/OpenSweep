# Maintenance history and updates

These changes are in development after version 1.2.0.

## Maintenance history

The History page records cleanup, startup changes, registry fixes, recycling, restore actions, uninstall launches, and update attempts. Records include time, source, per-item outcomes, skipped and failed counts, and available recovery guidance. Scheduled cleanup uses the same store.

Records live under `%LOCALAPPDATA%\OpenSweep\History`. Atomic checkpoints preserve completed work if a later action fails. A started record without a completion checkpoint means completion was not recorded; it does not prove the operation is still running. A history-write failure is shown on the History page and logged without interrupting maintenance.

Retention is bounded to 200 records and 32 MiB. Large operations retain summary totals while limiting stored item details; an operation exceeding the live tracking limit is explicitly marked incomplete. Records can be filtered and exported as XML. Exports can contain local paths, app names, and error details; review them before sharing.

Deleted sizes represent recorded logical file data, not a measured increase in free disk space. Files moved to the Recycle Bin are shown separately because recycling does not immediately free space. Missing and locked files are skipped, and an unavailable native size query remains unknown.

Recovery links lead to the Recycle Bin, registry backups, startup controls, or System Restore when relevant. They do not promise that Windows can undo an operation. Custom Clean permanently deletes files. External uninstall and registry-import launches are recorded without claiming their eventual success. History data never supplies arbitrary executable recovery commands.

## App and Windows updates

Each selected item has a phase and final result. Windows Update reports its download and install stages; external app installers manage their own progress, so OpenSweep does not invent percentages. Retry failed selects only failed items that do not require a restart.

Cancel remaining stops new queue items. It lets the active external installer or Windows Update stage finish and records its actual result. OpenSweep does not forcibly kill an installer halfway through a change. Cancellation between Windows Update download and installation prevents that installation from starting.

Restart requirements reported by installers and Windows Update remain visible after a batch. If an external command exits without closing inherited output pipes, its completion is marked unconfirmed and the queue stops. This state survives restarting OpenSweep and blocks new installations until the user confirms that the installer has finished. Closing OpenSweep cannot guarantee an external installer stops.

## OpenSweep updates

The OpenSweep updates page checks stable releases in `dinesh-devkota/OpenSweep`, shows release notes, and offers eligible installer or portable downloads. Startup notifications are opt-in and check at most daily. Checking never downloads or installs automatically.

Before a downloaded file can be installed, OpenSweep checks:

1. Official repository asset URLs, allowed HTTPS redirects, expected size, and GitHub's SHA-256 asset digest.
2. Windows Authenticode trust and revocation status, including the code-signing certificate usage.
3. The same verified signing key as the running trusted OpenSweep executable.
4. Signed product metadata and a newer file version matching the selected release.

File and directory handles prevent replacement while verification and launch use the artifact. Installer launch requires explicit confirmation; the handle remains protected while OpenSweep observes the installer. Portable downloads are verified for manual replacement.

Unsigned builds can check versions and open the official release page, but cannot establish the signing identity needed for in-app installation. Existing users of unsigned 1.2.0 must install the first trusted signed release manually. Signing setup and key changes are described in [SIGNING.md](../SIGNING.md).

## Validation

Managed regressions cover history persistence, concurrent saves, retention, corrupt records, update outcomes and queue cancellation, bounded process-output draining, and self-update feed, URL, hash, version, and signer policy. Windows CI additionally builds WPF, renders the four history/update pages in both themes, verifies real signed and unsigned binaries through Windows trust APIs, and exercises staged-file locks and deletion results using temporary fixtures.

CI does not install Windows updates or third-party applications, run real cleanup, or exercise a production signed OpenSweep upgrade. Those integration paths still require controlled testing on Windows with signed release artifacts.
