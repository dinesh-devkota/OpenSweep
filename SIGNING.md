# Code signing with SignPath (maintainer guide)

OpenSweep releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) and signed through [SignPath.io](https://about.signpath.io). Open-source projects can get this for free from the [SignPath Foundation](https://signpath.org). Until signing is set up, the workflow still publishes unsigned releases.

## One-time setup

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

You can also run **Actions → Build, sign and release → Run workflow** and enter a version.

The workflow then:

1. Builds `OpenSweep.exe` with that version.
2. Sends it to SignPath. Approve the request in SignPath; the workflow waits up to 1 hour.
3. Checks the signature and publishes the GitHub release. The direct download link `releases/latest/download/OpenSweep.exe` then serves the signed file.
