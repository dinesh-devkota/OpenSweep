# OpenSweep for Windows and Android

OpenSweep is a free, open-source cleaner for Windows and Android. On Windows, review temporary files and browser caches, manage startup apps, investigate storage, and update your software. On Android, choose a folder, review temporary files and desktop metadata left in it, and select what to delete. Both apps are ad-free and telemetry-free.

[![Download OpenSweep Setup for Windows](https://img.shields.io/badge/Download-OpenSweep%20Setup%20(Free)-10B981?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep-Setup.exe)
[![Download portable OpenSweep.exe](https://img.shields.io/badge/Portable-OpenSweep.exe-374151?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)
[![Install OpenSweep on Android](https://img.shields.io/badge/Android-Install%20APK-276246?style=for-the-badge&logo=android&logoColor=white)](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.apk)
[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-0070BA?style=for-the-badge&logo=paypal&logoColor=white)](https://www.paypal.biz/alinafunddevkota)

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/dinesh-devkota/OpenSweep)](https://github.com/dinesh-devkota/OpenSweep/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/dinesh-devkota/OpenSweep/total)](https://github.com/dinesh-devkota/OpenSweep/releases)
![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)

---

## ⬇️ Download (free)

| | |
|---|---|
| **Windows installer** | **[OpenSweep-Setup.exe](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep-Setup.exe)** (~2 MB). Adds Start menu and desktop shortcuts and an uninstaller. No administrator rights needed |
| **Windows portable** | **[OpenSweep.exe](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.exe)** (~400 KB). Run it from anywhere, even a USB stick. Nothing is installed |
| **Android app** | **[OpenSweep.apk](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.apk)** for Android 8.0 or newer. [Installation instructions](#install-on-android) |
| All versions and release notes | [Releases page](https://github.com/dinesh-devkota/OpenSweep/releases) |
| Windows requirements | Windows 10 or 11 (64-bit or 32-bit). Nothing else to install |

### Install on Android

1. Download **[OpenSweep.apk](https://github.com/dinesh-devkota/OpenSweep/releases/latest/download/OpenSweep.apk)** on your phone.
2. Tap the APK and choose **Install**. If Android asks for permission, enable **Allow from this source** for the browser or file manager you used. You can turn it off again after installation.
3. Open **OpenSweep** and choose a folder in the system picker. For example, choose a project folder under **Documents**. On Android 11 and later, the picker restricts locations such as the top-level Downloads folder.
4. Review the matches in that folder and its subfolders. OpenSweep looks for temporary files ending in `.tmp` and desktop metadata named `Thumbs.db` or `.DS_Store`. Select the files you want to remove, review the count, and confirm permanent deletion.

The app works locally with the folders you grant it. Other apps’ private caches and Android system files are outside its scope. Release APKs use the same signing key for updates. Test builds use a separate app ID and can be installed alongside the release.

### What's new in Windows 1.2.0

- **Windows care:** guided access to Windows storage, startup, notification, privacy, update, and repair controls, with a local diagnostic report.
- **Review-first cleanup:** a fresh preview replaces Health's “Fix all”; recovery and diagnostic data are opt-in for new selections.
- **Better startup management:** search, status filters, clear unknown states, and CSV export.
- **Storage reports:** export the largest-file list with recorded sizes and scan details.
- **Reliability fixes:** safer winget parsing, accurate failed/cancelled update checks, and scheduled-cleaner migration when installing.

See the [1.2.0 release notes](docs/releases/1.2.0.md) and [feature comparison and review notes](docs/windows-care-upgrades.md).

### Start OpenSweep on Windows

1. Download **OpenSweep-Setup.exe** and run it, or download the portable **OpenSweep.exe** and just double-click it.
2. Check the release notes for the build's signing status and SHA-256 hashes. Windows may show an unknown-publisher or SmartScreen prompt for unsigned builds.
3. Optional: right-click → **Run as administrator** for system-wide cleaning, restore points and Windows Update installs.
4. Optional: choose **Light**, **Dark** or **System** theme in **Options → Appearance**.

To uninstall, use **Settings → Apps → Installed apps → OpenSweep**, or **Options → Uninstall** in the app.

## ❤️ Support OpenSweep

OpenSweep is free and will stay free. If it saved you time or disk space, please consider a small donation. It helps keep the project maintained and improving.

**[👉 Donate with PayPal](https://www.paypal.biz/alinafunddevkota)**

You can also help by ⭐ **starring this repository** and sharing it. That's how other people find it.

## ✨ Windows features

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

### Android

Open the `android` folder in Android Studio, or use JDK 17 or 21 and the Android SDK with Platform 36 and Build Tools 35.0.0. Set `ANDROID_HOME` to your SDK directory or configure `sdk.dir` in `android/local.properties`. Run these commands from the repository root on Windows.

```powershell
cd android
.\gradlew.bat :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

On macOS or Linux use `bash gradlew` from the same directory. The installable debug APK is at `android/app/build/outputs/apk/debug/app-debug.apk`. With a USB-debugging device connected, run `:app:installDebug` with the wrapper to install it. For public releases, configure [Android APK signing](SIGNING.md#android-apk-signing).

To try a test build from GitHub, sign in and download the **OpenSweep-Android-debug** artifact from a successful [Android build](https://github.com/dinesh-devkota/OpenSweep/actions/workflows/android.yml). Extract **OpenSweep-debug.apk** from the ZIP.

### Windows

You need the [.NET SDK](https://dotnet.microsoft.com/download) 8 or later. The app targets .NET Framework 4.8, which is built into Windows, so the result runs anywhere without a runtime.

```powershell
git clone https://github.com/dinesh-devkota/OpenSweep.git
cd OpenSweep
.\build.ps1          # output: .\dist\OpenSweep.exe and .\dist\OpenSweep-Setup.exe
```

`OpenSweep-Setup.exe` is built with the free [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`) from [`installer/OpenSweep.iss`](installer/OpenSweep.iss). Without Inno Setup, only the portable exe is built.

### Windows command-line switches

| Switch | Description |
|---|---|
| `/auto` | Silently cleans the saved Custom Clean selection, then exits (used by scheduled cleaning) |
| `/install` | Installs for the current user (`%LOCALAPPDATA%\Programs\OpenSweep`) |
| `/uninstall` | Removes the installed copy, shortcuts and scheduled task |
| `/page <name>` | Opens a specific page: `health`, `care`, `cleaner`, `registry`, `updater`, `winupdate`, `startup`, `uninstall`, `disk`, `dupes`, `restore`, `options` |

Settings and logs are stored in `%LOCALAPPDATA%\OpenSweep`.

### Project layout

```
android/              native Android app and Gradle wrapper
src/OpenSweep/
  Core/      cleaning, registry, winget, Windows Update, startup, scheduler, installer
  Pages/     one WPF page per sidebar section
  Themes/    styles plus Light.xaml / Dark.xaml palettes
installer/OpenSweep.iss  Inno Setup script for OpenSweep-Setup.exe
tools/make-icon.ps1   generates the app icon
build.ps1             release build -> dist/
```

## 🔏 Code signing policy

Windows code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [dinesh-devkota](https://github.com/dinesh-devkota)
- Approvers: [dinesh-devkota](https://github.com/dinesh-devkota)

Releases are built from this repository's source code by [GitHub Actions](.github/workflows/release.yml). Windows builds use SignPath when configured. Android release APKs use a dedicated Android signing key. Release notes identify the available installers and their hashes. Maintainer setup is described in [SIGNING.md](SIGNING.md).

### Privacy policy

The Android app requests no internet permission and processes the folder you choose on your device.

The Windows app will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Update checks use Windows' own services: the **Windows Package Manager (winget)** for app updates, with the [Microsoft privacy statement](https://privacy.microsoft.com/privacystatement) and each package source's terms, and **Windows Update**. Donate, Website, and help links open your browser. Diagnostic reports are generated locally; startup and storage exports may contain commands and file paths, so review them before sharing.

## 📄 License

[MIT](LICENSE): free to use, modify and share.

---

<sub>Android folder cleanup · Android APK · Windows maintenance · cleanup previews · startup manager · disk analysis · diagnostic reports · winget updates · Windows Update · open source</sub>
