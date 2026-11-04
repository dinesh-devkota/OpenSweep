# OpenSweep – Free PC Cleaner & Updater for Windows 10 and 11

**OpenSweep is a free, open-source Windows maintenance app**, with a **dark mode** and a choice of **installer or portable app**. Review temporary files and browser caches, manage startup apps, investigate storage, and update apps and Windows. Windows care guides you to relevant Windows controls when you need help with performance, interruptions, or repairs. No ads, no trial, no "Pro" upsell, no telemetry.

[![Download OpenSweep Setup for Windows](https://img.shields.io/badge/Download-OpenSweep%20Setup%20(Free)-10B981?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep-Setup.exe)
[![Download portable OpenSweep.exe](https://img.shields.io/badge/Portable-OpenSweep.exe-374151?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-0070BA?style=for-the-badge&logo=paypal&logoColor=white)](https://www.paypal.biz/alinafunddevkota)

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dinesh-devkota/OpenSweep)](https://github.com/dinesh-devkota/OpenSweep/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/dinesh-devkota/OpenSweep/total)](https://github.com/dinesh-devkota/OpenSweep/releases)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)

---

## ⬇️ Download (free)

| | |
|---|---|
| **Installer (recommended)** | **[OpenSweep-Setup.exe](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep-Setup.exe)** (~2 MB). Adds Start menu and desktop shortcuts and an uninstaller. No administrator rights needed |
| **Portable** | **[OpenSweep.exe](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)** (~400 KB). Run it from anywhere, even a USB stick. Nothing is installed |
| All versions and release notes | [Releases page](https://github.com/dinesh-devkota/OpenSweep/releases) |
| Requirements | Windows 10 or 11 (64-bit or 32-bit). Nothing else to install |

**How to start it:**

1. Download **OpenSweep-Setup.exe** and run it, or download the portable **OpenSweep.exe** and just double-click it.
2. If Windows shows **"Windows protected your PC"** (SmartScreen), click **More info → Run anyway**. This appears because OpenSweep is new and not code-signed. The full source code is in this repository.
3. Optional: right-click → **Run as administrator** for system-wide cleaning, restore points and Windows Update installs.
4. Optional: choose **Light**, **Dark** or **System** theme in **Options → Appearance**.

To uninstall, use **Settings → Apps → Installed apps → OpenSweep**, or **Options → Uninstall** in the app.

## ❤️ Support OpenSweep

OpenSweep is free and will stay free. If it saved you time or disk space, please consider a small donation. It helps keep the project maintained and improving.

**[👉 Donate with PayPal](https://www.paypal.biz/alinafunddevkota)**

You can also help by ⭐ **starring this repository** and sharing it. That's how other people find it.

## ✨ Features

| Tool | What it does |
|---|---|
| **Health overview** | Checks selected cleanup categories, startup apps, updates, and disk space; opens a fresh cleanup preview for review |
| **Windows care** | Guidance and Windows settings shortcuts for startup, storage, notifications, updates, permissions, and system repair; copy or save a local diagnostic report |
| **Custom Clean (junk file cleaner)** | Windows temp files, Recycle Bin, thumbnail cache, error reports, memory dumps, logs and the Windows Update download cache, plus cache, cookies and history for **Edge, Chrome, Firefox, Brave and Opera**. Analyze first, then clean |
| **Registry (Advanced)** | Reviews possible leftovers from removed programs. Selected changes require a complete `.reg` backup first; registry cleanup is separate from routine health checks |
| **Software Updater** | Finds outdated apps and updates them in one click using **winget** (Windows Package Manager) |
| **Windows Update** | Checks for, downloads and installs Windows updates |
| **Startup Manager** | Search Run-key and Startup-folder entries, filter their state, reversibly disable them, and export the visible list |
| **Uninstaller** | Lists installed programs and removes them |
| **Disk Analyzer** | Shows folders, file types, and the 200 largest files; export the largest-file list with paths, recorded sizes, and scan time |
| **Duplicate File Finder** | Finds identical files by content hash and moves extras to the Recycle Bin |
| **System Restore** | Creates, lists and deletes restore points |
| **Scheduled cleaning** | Cleans automatically every day or week using Task Scheduler |
| **Dark mode** | Light, dark or follow-Windows theme, including a dark title bar |

### Safe by design

- Interactive cleaning asks for confirmation. Optional scheduled cleaning and `/auto` use your saved selection without prompting.
- Recycle Bin contents, diagnostic reports, crash dumps, logs, and shader caches are opt-in for new selections. Existing saved selections are retained.
- Registry fixes are backed up (`%LOCALAPPDATA%\OpenSweep\Backups`), and a restore point can be created first.
- Duplicate removal requests the **Recycle Bin**; Windows asks before permanent deletion when recycling is unavailable.
- It runs as a normal user by default. Administrator rights are only used when you ask for them.
- Cleaning and local reports work offline. Update tools contact package sources and Windows Update; help links open your browser. Exports stay at the location you choose and can include file paths or startup commands.

## ❓ FAQ

**Is OpenSweep really free?**
Yes. It's free for personal and commercial use under the MIT License, with no ads, trials or paid tiers. Donations are optional.

**Is it safe?**
Review the preview and selected categories before cleaning. Custom Clean permanently removes selected files, while registry changes require backups and duplicate removal requests recycling. Automatic cleaning uses the selection you save. The source is public, and exclusions let you protect folders you want to keep.

**Is this CCleaner?**
No. OpenSweep is an independent, open-source project written from scratch. It is not affiliated with, endorsed by, or based on CCleaner, Piriform or Gen Digital.

**Why does my antivirus or SmartScreen warn about it?**
New, unsigned apps that clean files and the registry are sometimes flagged as a precaution. You can build it yourself from source (below) to be sure.

## 🛠️ Build from source

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later. The app targets .NET Framework 4.8, which is built into Windows, so the result runs anywhere without a runtime.

```powershell
git clone https://github.com/dinesh-devkota/OpenSweep.git
cd OpenSweep
.\build.ps1          # output: .\dist\OpenSweep.exe and .\dist\OpenSweep-Setup.exe
```

`OpenSweep-Setup.exe` is built with the free [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) from [`installer/OpenSweep.iss`](installer/OpenSweep.iss). Without Inno Setup, only the portable exe is built.

### Command-line switches

| Switch | Description |
|---|---|
| `/auto` | Silently cleans the saved Custom Clean selection, then exits (used by scheduled cleaning) |
| `/install` | Installs for the current user (`%LOCALAPPDATA%\Programs\OpenSweep`) |
| `/uninstall` | Removes the installed copy, shortcuts and scheduled task |
| `/page <name>` | Opens a specific page: `health`, `care`, `cleaner`, `registry`, `updater`, `winupdate`, `startup`, `uninstall`, `disk`, `dupes`, `restore`, `options` |

Settings and logs are stored in `%LOCALAPPDATA%\OpenSweep`.

### Project layout

```
src/OpenSweep/
  Core/      cleaning, registry, winget, Windows Update, startup, scheduler, installer
  Pages/     one WPF page per sidebar section
  Themes/    styles plus Light.xaml / Dark.xaml palettes
installer/OpenSweep.iss  Inno Setup script for OpenSweep-Setup.exe
tools/make-icon.ps1   generates the app icon
build.ps1             release build -> dist/
```

## 🔏 Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [dinesh-devkota](https://github.com/dinesh-devkota)
- Approvers: [dinesh-devkota](https://github.com/dinesh-devkota)

Every release is built from this repository's source code by [GitHub Actions](.github/workflows/release.yml) and signed only after manual approval. Maintainer setup is described in [SIGNING.md](SIGNING.md).

### Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Update checks use Windows' own services: the **Windows Package Manager (winget)** for app updates, with the [Microsoft privacy statement](https://privacy.microsoft.com/privacystatement) and each package source's terms, and **Windows Update**. Donate, Website, and help links open your browser. Diagnostic reports are generated locally; startup and storage exports may contain commands and file paths, so review them before sharing.

## 📄 License

[MIT](LICENSE): free to use, modify and share.

---

<sub>Keywords: free PC cleaner, free Windows cleaner, CCleaner alternative, open source PC cleaner, junk file cleaner, registry cleaner, temp file cleaner, browser cache cleaner, software updater, winget GUI, Windows 11 cleaner, Windows 10 cleaner, disk cleanup, startup manager, duplicate file finder, speed up PC.</sub>
