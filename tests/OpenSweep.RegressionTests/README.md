# Portable managed regression checks

This dependency-free .NET 8 console suite links the actual production sources for process execution, settings persistence/path exclusions, registry backup publication, duplicate scanning/verification, and cleanup orchestration. Every failure produces a nonzero exit code; the final line reports passed/failed counts.

From the repository root:

```sh
dotnet run --project tests/OpenSweep.RegressionTests -c Release
```

The cloud environment requires `source /workspace/opensweep-environment/env.sh` first. The first run restores the SDK project; no third-party test packages are used.

The suite creates unique fixtures under the operating system's temporary directory and removes them afterward. Process tests launch this same test executable in fixed child modes to exercise output draining, exit codes, callback exceptions, and cancellation. Registry backup tests inject an exporter that writes fixture `.reg` files; they never access the registry. Duplicate tests prepare deletion plans but never invoke deletion/recycling. Settings tests point the test-only `AppPaths` adapter at temporary settings files, never at a user's OpenSweep configuration.

`PortableStubs.cs` replaces only Windows-dependent application paths/logging and UI notification/display-format helpers. Cleanup orchestration tests use a simulated cleaner engine: they verify the production concurrency gate and cancellation/error handling, not actual cleanup behavior. No cleanup algorithms are mocked and then claimed as tested.

These checks do **not** validate WPF rendering, native Windows registry access, Windows Update/winget integration, shell recycling, Windows file-ID/share-lock behavior, or installer behavior. The Linux duplicate-path check uses the production metadata fallback; passing it does not prove Windows native file identity handling. Symbolic-link fixture checks run only on non-Windows hosts. Windows integration testing remains required before relying on those features.
