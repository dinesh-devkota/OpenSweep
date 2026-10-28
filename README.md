# OpenSweep

**A free, open-source (MIT) Windows PC cleaner and updater.** It cleans junk files, removes leftover registry entries, keeps your apps and Windows up to date, and includes tools for startup programs, uninstalling, disk usage, duplicates and System Restore.

OpenSweep is a single small `.exe` (~400 KB). It runs on Windows 10 and 11 with nothing extra to install, because it uses the .NET Framework 4.8 built into Windows.

> OpenSweep is an independent project. It is not affiliated with, endorsed by, or based on the code of CCleaner, Piriform or Gen Digital.

## Features

| Area | What it does |
|---|---|
| **Health Check** | One-click scan of junk files, registry, startup programs, app updates, Windows updates and disk space, then **Fix all** |
| **Custom Clean** | Windows temp files, Recycle Bin, thumbnail cache, logs, crash dumps and Windows Update cache, plus browser caches/history for Edge, Chrome, Firefox, Brave and Opera, and common apps. Analyze first, then clean |
| **Registry** | Finds leftovers from removed programs: missing files, uninstall entries, file associations, app paths and more. Every fix is backed up to a `.reg` file first |
| **Software Updater** | Lists outdated apps and updates them through **winget** (Windows Package Manager) |
| **Windows Update** | Checks for, downloads and installs Windows updates through the Windows Update Agent API |
| **Startup** | Enables, disables or removes programs that start with Windows (registry Run keys and Startup folders) |
| **Uninstall** | Lists installed programs and runs their uninstallers |
| **Disk Analyzer** | Shows which folders and file types use the most space |
| **Duplicate Finder** | Finds identical files by size and hash, and moves the extras to the Recycle Bin |
| **System Restore** | Lists, creates and deletes restore points |
| **Options** | Folder exclusions, scheduled automatic cleaning (Task Scheduler), install/uninstall for the current user |

### Safety

- Nothing is deleted until you confirm.
- Registry fixes are backed up (`%LOCALAPPDATA%\OpenSweep\Backups`), and a restore point can be created first.
- Duplicate files are sent to the **Recycle Bin**, not permanently deleted.
- The app runs as a normal user by default. Click **Restart as administrator** for system-wide cleaning, restore points and Windows Update installs.

## Getting started

### Run it

1. Build it (see below), or download `OpenSweep.exe` from Releases.
2. Double-click **OpenSweep.exe**. For full cleaning, right-click it and choose **Run as administrator**.

To add Start menu and desktop shortcuts, go to **Options → Install**.

### Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later. It targets `net48`, so the output runs without any runtime installed.

```powershell
git clone https://github.com/dinesh-devkota/OpenSweep.git
cd OpenSweep
.\build.ps1          # output: .\dist\OpenSweep.exe
```

Or build manually:

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1   # only needed once
dotnet build src\OpenSweep -c Release
```

### Command-line switches

| Switch | Description |
|---|---|
| `/auto` | Silently cleans the saved Custom Clean selection, then exits (used by scheduled cleaning) |
| `/install` | Installs OpenSweep for the current user (`%LOCALAPPDATA%\Programs\OpenSweep`) |
| `/uninstall` | Removes the installed copy, shortcuts and scheduled task |
| `/page <name>` | Opens on a specific page, e.g. `cleaner`, `registry`, `updater`, `winupdate`, `startup`, `uninstall`, `disk`, `dupes`, `restore`, `options` |

Settings and logs are stored in `%LOCALAPPDATA%\OpenSweep`.

## Project layout

```
src/OpenSweep/
  Core/      cleaning, registry, winget, Windows Update, startup, scheduler, installer
  Pages/     one WPF page per sidebar section
  Themes/    styles
tools/make-icon.ps1   generates the app icon
build.ps1             release build -> dist/
```

## License

[MIT](LICENSE). You're free to use, modify and distribute it.
