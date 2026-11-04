# Portable managed regression checks

This dependency-free .NET 8 console suite links the actual production sources for process execution, settings persistence/path exclusions, registry backup publication, duplicate scanning/verification, cleanup orchestration, startup approval/search/reporting, Windows care snapshot formatting/navigation, winget table parsing, update queue/result policy, maintenance history, and self-update validation policy. Every failure produces a nonzero exit code; the final line reports passed/failed counts.

From the repository root:

```sh
dotnet run --project tests/OpenSweep.RegressionTests -c Release
```

The cloud environment requires `source /workspace/opensweep-environment/env.sh` first. The first run restores the SDK project; no third-party test packages are used.

The suite creates unique fixtures under the operating system's temporary directory and removes them afterward. Process tests launch this same test executable in fixed child modes to exercise output draining, exit codes, callback exceptions, and cancellation. Startup and Windows care tests use in-memory data only. CSV checks verify quoting and spreadsheet-formula neutralization. Winget parser checks use fixed text fixtures, including localized headings and Unicode names; they never run winget or install updates. Registry backup tests inject an exporter that writes fixture `.reg` files; they never access the registry. Duplicate tests prepare deletion plans but never invoke deletion/recycling. Settings tests point the test-only `AppPaths` adapter at temporary settings files, never at a user's OpenSweep configuration.

Inherited-pipe tests use descendants with six-second bounded lifetimes, verify bounded completion/cancellation while their pipes remain open, and wait for each fixture descendant to finish. Update queue tests verify that stopping the queue lets an active installer finish, only confirmed failures can be retried, and unknown completion prevents further installation. They use delegates returning fixed outcomes, never actual installers.

History tests write real XML checkpoints and exports in temporary directories, exercise concurrent saves, rejected oversized checkpoints that preserve prior data, count/byte retention, corrupt/oversized/external-entity record rejection, Unicode-safe bounded text, bounded details with replacement totals, and unknown/deleted/recycled totals. Cleanup history tests run the real orchestration against simulated engine outcomes to verify recorded partial work and cancellation. Self-update tests parse fixture feeds, hash actual fixture files, and check stable versions, executable metadata, allowed assets/redirects, limits, and signer-policy decisions, including rejection of unsigned current builds. Trust booleans in those policy tests are inputs; they do not exercise native signature verification.

Embedded executable metadata tests generate minimal PE32/PE32+ resource fixtures in memory. They exercise the production parser against numeric versions, consistent/conflicting localized resources, malformed headers/offsets/values, and an adjacent `.mui` file that must not affect the supplied executable stream. Those fixtures contain no executable code, are never launched, and are not checked in as binaries.

`PortableStubs.cs` replaces only Windows-dependent application paths/logging and UI notification/display-format helpers. Cleanup orchestration tests use a simulated cleaner engine: they verify the production concurrency gate and cancellation/error handling, not actual cleanup behavior. No cleanup algorithms are mocked and then claimed as tested.

These checks do **not** validate WPF rendering, native Windows registry access, Windows Update/winget integration, shell recycling, Windows file-ID/share-lock behavior, Authenticode certificate verification, real download networking, or installer behavior. The Linux duplicate-path check uses the production metadata fallback; passing it does not prove Windows native file identity handling. Symbolic-link fixture checks run only on non-Windows hosts. Windows integration testing remains required before relying on those features.
