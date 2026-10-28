# OpenSweep – Free PC Cleaner & Updater for Windows 10 and 11

**OpenSweep is a 100% free, open-source Windows PC cleaner and software updater.** It cleans junk files, temp files and browser caches, fixes registry leftovers, updates your apps and Windows, and speeds up startup. No ads, no trial, no "Pro" upsell, no data collection. It's a free alternative to paid PC cleaner tools.

[![Download OpenSweep for Windows](https://img.shields.io/badge/Download-OpenSweep.exe%20(Free)-10B981?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-0070BA?style=for-the-badge&logo=paypal&logoColor=white)](https://www.paypal.biz/alinafunddevkota)

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dinesh-devkota/OpenSweep)](https://github.com/dinesh-devkota/OpenSweep/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/dinesh-devkota/OpenSweep/total)](https://github.com/dinesh-devkota/OpenSweep/releases)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)

---

## ⬇️ Download (free)

| | |
|---|---|
| **Direct download** | **[OpenSweep.exe](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)**, the latest version (~400 KB) |
| All versions and release notes | [Releases page](https://github.com/dinesh-devkota/OpenSweep/releases) |
| Requirements | Windows 10 or 11 (64-bit or 32-bit). Nothing else to install |

**How to start it:**

1. Download **OpenSweep.exe** and double-click it. It's portable, so no installation is needed.
2. If Windows shows **"Windows protected your PC"** (SmartScreen), click **More info → Run anyway**. This appears because OpenSweep is new and not code-signed. The full source code is in this repository.
3. Optional: right-click → **Run as administrator** for system-wide cleaning, restore points and Windows Update installs.
4. Optional: go to **Options → Install** to add Start menu and desktop shortcuts.

## ❤️ Support OpenSweep

OpenSweep is free and will stay free. If it saved you time or disk space, please consider a small donation. It helps keep the project maintained and improving.

**[👉 Donate with PayPal](https://www.paypal.biz/alinafunddevkota)**

You can also help by ⭐ **starring this repository** and sharing it. That's how other people find it.

## ✨ Features

| Tool | What it does |
|---|---|
| **Health Check** | One-click scan of junk files, registry, startup programs, app updates, Windows updates and disk space, then **Fix all** |
| **Custom Clean (junk file cleaner)** | Windows temp files, Recycle Bin, thumbnail cache, error reports, memory dumps, logs and the Windows Update download cache, plus cache, cookies and history for **Edge, Chrome, Firefox, Brave and Opera**. Analyze first, then clean |
| **Registry Cleaner** | Finds leftovers from removed programs: missing files, broken uninstall entries, file associations, app paths and more. Every fix is **backed up** to a `.reg` file first |
| **Software Updater** | Finds outdated apps and updates them in one click using **winget** (Windows Package Manager) |
| **Windows Update** | Checks for, downloads and installs Windows updates |
| **Startup Manager** | Enables, disables or removes programs that slow down boot |
| **Uninstaller** | Lists installed programs and removes them |
| **Disk Analyzer** | Shows which folders and file types fill up your drive |
| **Duplicate File Finder** | Finds identical files by content hash and moves extras to the Recycle Bin |
| **System Restore** | Creates, lists and deletes restore points |
| **Scheduled cleaning** | Cleans automatically every day or week using Task Scheduler |

### Safe by design

- Nothing is deleted until you confirm.
- Registry fixes are backed up (`%LOCALAPPDATA%\OpenSweep\Backups`), and a restore point can be created first.
- Duplicate files go to the **Recycle Bin**, not permanently deleted.
- It runs as a normal user by default. Administrator rights are only used when you ask for them.
- It works offline and never sends your data anywhere. Only update checks go online.

## ❓ FAQ

**Is OpenSweep really free?**
Yes. It's free for personal and commercial use under the MIT License, with no ads, trials or paid tiers. Donations are optional.

**Is it safe?**
Yes. The full source code is public, risky actions are backed up or reversible, and nothing is cleaned without your confirmation.

**Is this CCleaner?**
No. OpenSweep is an independent, open-source project written from scratch. It is not affiliated with, endorsed by, or based on CCleaner, Piriform or Gen Digital.

**Why does my antivirus or SmartScreen warn about it?**
New, unsigned apps that clean files and the registry are sometimes flagged as a precaution. You can build it yourself from source (below) to be sure.

## 🛠️ Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later. The app targets .NET Framework 4.8, which is built into Windows, so the result runs anywhere without a runtime.

```powershell
git clone https://github.com/dinesh-devkota/OpenSweep.git
cd OpenSweep
.\build.ps1          # output: .\dist\OpenSweep.exe
```

### Command-line switches

| Switch | Description |
|---|---|
| `/auto` | Silently cleans the saved Custom Clean selection, then exits (used by scheduled cleaning) |
| `/install` | Installs for the current user (`%LOCALAPPDATA%\Programs\OpenSweep`) |
| `/uninstall` | Removes the installed copy, shortcuts and scheduled task |
| `/page <name>` | Opens a specific page: `cleaner`, `registry`, `updater`, `winupdate`, `startup`, `uninstall`, `disk`, `dupes`, `restore`, `options` |

Settings and logs are stored in `%LOCALAPPDATA%\OpenSweep`.

### Project layout

```
src/OpenSweep/
  Core/      cleaning, registry, winget, Windows Update, startup, scheduler, installer
  Pages/     one WPF page per sidebar section
  Themes/    styles
tools/make-icon.ps1   generates the app icon
build.ps1             release build -> dist/
```

## 📄 License

[MIT](LICENSE): free to use, modify and share.

---

<sub>Keywords: free PC cleaner, free Windows cleaner, CCleaner alternative, open source PC cleaner, junk file cleaner, registry cleaner, temp file cleaner, browser cache cleaner, software updater, winget GUI, Windows 11 cleaner, Windows 10 cleaner, disk cleanup, startup manager, duplicate file finder, speed up PC.</sub>
