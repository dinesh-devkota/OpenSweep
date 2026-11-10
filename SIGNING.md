# Signing OpenSweep releases

Windows releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) and signed through [SignPath.io](https://about.signpath.io). Open-source projects can get this for free from the [SignPath Foundation](https://signpath.org). Until Windows signing is set up, the workflow still publishes unsigned Windows builds.

## Windows signing setup

1. **Apply for free OSS signing:** go to https://signpath.org/apply and fill in:
   - Project: OpenSweep, repository `https://github.com/dinesh-devkota/OpenSweep`, license MIT
   - Download page: `https://github.com/dinesh-devkota/OpenSweep/releases`
   - Turn on **2-factor authentication** on GitHub and SignPath; it's required.
2. **Once approved, in SignPath:**
   - Add the predefined **Trusted Build System "GitHub.com"** to your organization and link it to the project.
   - Create a **Project** with the slug `OpenSweep`.
   - Set its **Artifact configuration** to the contents of [`.signpath/artifact-configuration.xml`](.signpath/artifact-configuration.xml).
   - Create a **Signing policy** with the slug `release-signing` (Release certificate, manual approval).
   - Install the [SignPath GitHub App](https://github.com/apps/signpath) on this repository.
   - Create a **CI user**, add it as a *Submitter* on the signing policy, and copy its **API token**.
3. **In GitHub**, go to *Settings → Secrets and variables → Actions*:
   - Secret `SIGNPATH_API_TOKEN`: the API token from step 2.
   - Variable `SIGNPATH_ORGANIZATION_ID`: your SignPath organization ID (shown in SignPath under *Settings*).
   - Optional variables `SIGNPATH_PROJECT_SLUG` (default `OpenSweep`) and `SIGNPATH_SIGNING_POLICY_SLUG` (default `release-signing`).

## Releasing a new version

```powershell
git tag v1.0.1
git push origin v1.0.1
```

You can also run **Actions → Build, sign and release → Run workflow** from the default branch (`main`) and enter a version.

For Windows releases with SignPath enabled, the workflow performs these steps.

1. Builds `OpenSweep.exe` with that version and sends it to SignPath. Approve the request in SignPath; the workflow waits up to 1 hour.
2. Builds `OpenSweep-Setup.exe` around the signed exe (Inno Setup) and sends that to SignPath too, so there are **two requests to approve** per release.
3. Checks the signatures and publishes both files to the GitHub release. The direct links `releases/latest/download/OpenSweep-Setup.exe` and `releases/latest/download/OpenSweep.exe` then serve the signed files.

## Android APK signing

The official Android release certificate has SHA-256 fingerprint `FC792F282BE754E0935E89C065C13B32B802667B8AB375CDFC07508AFD5D9F78`. To check a download, run `apksigner verify --print-certs OpenSweep.apk` and compare the signer certificate digest. Use the original keystore for each OpenSweep update.

For a new distribution, create a dedicated Android release keystore once, outside the repository. Keep an encrypted backup of the keystore and its passwords. Android requires the same application ID and signing certificate to install an update over an existing release.

```text
keytool -genkeypair -keystore opensweep-release.jks -storetype JKS -alias opensweep -keyalg RSA -keysize 3072 -validity 10000
```

From the directory containing the keystore, upload it directly to the repository’s Actions secrets with the GitHub CLI. This PowerShell command sends the encoded keystore to GitHub through standard input.

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path opensweep-release.jks))) | gh secret set ANDROID_KEYSTORE_BASE64 --repo dinesh-devkota/OpenSweep
```

In **Settings → Secrets and variables → Actions**, also set `ANDROID_KEY_ALIAS` to the alias you created, `ANDROID_KEYSTORE_PASSWORD` to the keystore password, and `ANDROID_KEY_PASSWORD` to the key password. On a version release the workflow signs and verifies `OpenSweep.apk` and adds it to the same GitHub release as the Windows downloads. With Android signing unconfigured, Windows releases and Android test builds are still available. Debug APKs from different workflow runs may have different test keys. Uninstall the old debug copy before installing a new one if Android reports a signature conflict.
