# OpenSweep for Windows and Android

OpenSweep is a free, open-source cleaner for Windows and Android. On Windows, review temporary files and browser caches, manage startup apps, investigate storage, and update your software. On Android, scan shared phone storage and accessible SD cards, review junk and large files, find and remove duplicate copies, ask Android to clear shared app caches, optionally trim app caches with Shizuku, and manage installed apps. Both apps are ad-free and telemetry-free.

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
3. Open **OpenSweep** and accept the full shared-storage setup prompt. On Android 11 and later, enable **Allow access to manage all files** in Android Settings, then return to the app. On Android 8–10, grant the storage permission. The home screen shows whether access is enabled and provides a button to review it.
4. Choose **Duplicate files**, **Temporary files**, or **Large files**. Duplicate results show groups, removable extra copies, and recoverable space. Use **Select extra copies**, optionally choose **Keep this copy instead**, review the selection, and confirm permanent deletion. One verified copy stays in every duplicate group. **Scan details** shows coverage and unreadable locations.
5. Open **Apps** to inspect installed apps, review their storage usage, open cache controls, check their store pages, or request uninstall. Optional **Usage access** supplies app data/cache sizes and last-used information.
6. In **Device**, enable daily scan notifications if you want a scan while charging. Tap the result notification to review a fresh scan.

Android's all-files permission covers shared storage, including Downloads and accessible SD cards. Android manages system files and other apps' private data separately. On Android 11 and later, **Clear shared app caches** opens Android's confirmation to clear external app caches. For private app caches, open an app's Android Settings screen and choose **Storage & cache → Clear cache**. This permission differs from the folder picker used by OpenSweep 1.3.0, which Android blocks from selecting the storage root and top-level Downloads folder. [Android storage access](https://developer.android.com/training/data-storage/manage-all-files)

### Android features

| Tool | What it does |
|---|---|
| **Storage overview** | Shows available and total storage on the phone |
| **Junk cleanup** | Reviews temporary files ending in `.tmp` and metadata named `Thumbs.db` or `.DS_Store` across accessible shared storage |
| **Shared app caches** | On Android 11 and later, asks Android to clear external app caches through its system confirmation; older versions open individual app controls |
| **Large files** | Lists files of at least 100 MiB for individual review and deletion |
| **Duplicates** | Compares same-sized files by SHA-256 and bytes, groups identical files, shows removable space, and lets you select extras and choose which copy to keep |
| **Scan coverage** | Shows scan progress, readable storage volumes, checked files/folders, and unavailable locations |
| **Optional Shizuku cleanup** | With your authorization, asks Android to trim caches across apps and reports the observed storage change |
| **Apps** | Shows installed applications, APK sizes, and optional usage-access storage/cache statistics; opens system app controls and uninstall confirmation |
| **App updates** | Opens an app's store page to check for updates |
| **Device maintenance** | Opens Android's storage, battery, security, and system update controls |
| **Scheduled scans** | Optional daily junk scan while charging, with a notification to review results |
| **Appearance** | Choose Light, Dark, or follow the System appearance |

Android controls app installation, app caches, battery restrictions, and system updates through its own confirmation screens. The Windows edition also provides Windows-specific registry, startup, winget, and restore-point tools.


### Optional deeper cache cleanup with Shizuku

Install [Shizuku from its official project](https://github.com/RikkaApps/Shizuku/releases) and follow its [setup guide](https://shizuku.rikka.app/guide/setup/). On supported devices you can start it through wireless debugging; it must be started again after a reboot. Return to OpenSweep, choose **Authorize Shizuku**, and approve the request in Shizuku.

Choose **Trim app caches with Shizuku** and confirm the device-wide scope. Android chooses eligible caches; individual apps cannot be excluded. The result reports available storage before and after the request, which other device activity can also affect. This operation uses Android’s cache-trimming command, never its app-data clearing command. Shizuku does not grant unrestricted private-file access in ADB mode, and duplicate scanning remains limited to shared user storage.

### Why Android works differently from Windows

Android separates shared files from each app's private data. The folder picker in OpenSweep 1.3.0 could not select the storage root or top-level Downloads on newer Android versions. OpenSweep now requests all-files access for shared-storage scans, and uses Android's own confirmation for clearing external app caches. Private caches remain available through each app's Settings screen.

This follows the same platform boundaries other cleaners work within: [Files by Google](https://support.google.com/files/answer/9848742) offers junk, duplicate, and large-file cleanup, while [SD Maid's AppCleaner](https://github.com/d4rken-org/sdmaid-se/wiki/AppCleaner) can optionally automate Settings using accessibility access. OpenSweep opens those private-cache controls for you to confirm. Windows registry, restore points, and winget have no direct Android equivalent; the Android app provides app management and device-maintenance shortcuts instead.

### Upgrading an existing installation

OpenSweep is the app’s name again, following the SqueakySift 1.4.0 and 1.5.0 releases. On Android, install **OpenSweep.apk** over your existing OpenSweep or SqueakySift app and choose **Update**. The application ID and signing certificate stay the same. On Windows, run the new setup over your existing installation; your settings, registry backups, and scheduled cleanup remain available. The previous SqueakySift download filenames are retained as aliases. [1.5.1 release notes](docs/releases/1.5.1.md)

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

The release pipeline supports Windows code signing through [SignPath.io](https://about.signpath.io) and [SignPath Foundation](https://signpath.org) when configured. Check each release for its signing status. Android release APKs are signed with the project's existing Android certificate.

- Committers and reviewers: [dinesh-devkota](https://github.com/dinesh-devkota)
- Approvers: [dinesh-devkota](https://github.com/dinesh-devkota)

Releases are built from this repository's source code by [GitHub Actions](.github/workflows/release.yml). Windows builds use SignPath when configured. Android release APKs use a dedicated Android signing key. Release notes identify the available installers and their hashes. Maintainer setup is described in [SIGNING.md](SIGNING.md). Included third-party components are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

### Privacy policy

The Android app processes file paths, installed-app information, and any permitted usage statistics on your device. Optional Shizuku access runs a fixed Android cache-trimming operation only after your authorization and confirmation. Its shared-storage scan uses all-files access on Android 11 and newer, or storage permission on older devices. Optional usage access supplies app storage and last-used statistics. Optional notifications report scheduled scan results. Store and maintenance buttons open the relevant external app or Android Settings.

The Windows app will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Update checks use Windows' own services: the **Windows Package Manager (winget)** for app updates, with the [Microsoft privacy statement](https://privacy.microsoft.com/privacystatement) and each package source's terms, and **Windows Update**. Donate, Website, and help links open your browser. Diagnostic reports are generated locally; startup and storage exports may contain commands and file paths, so review them before sharing.

## 📄 License

[MIT](LICENSE): free to use, modify and share.

---

<sub>Android shared-storage cleanup · app cache controls · Android APK · Windows maintenance · cleanup previews · startup manager · disk analysis · diagnostic reports · winget updates · Windows Update · open source</sub>
