# Hearth

A native Windows launcher for private Minecraft Java modpacks. Owners publish complete releases; friends join by invite and press **Play** to update their persistent Prism instance before Minecraft starts.

Hearth shows the next action for each pack: wait for approval, set up Prism, install and play, or play the installed copy when its host is offline. **My modpacks** is your library, **Add modpack** opens invites, **Share packs** combines hosting, publishing, and members, and **Settings** contains instance location, startup and closing preferences, Prism, and launcher updates. The navigation stays in place while the pack list scrolls.

The same installer contains the launcher and optional hosting. Hosting runs while Hearth is open or in the system tray and stops when Hearth exits completely. A host can serve multiple private packs; a player can join multiple hosts. Hearth distributes mod files. Your existing Minecraft server still runs the shared world.

## Start here

- [Download the latest Windows installer](https://github.com/BigUziHert/Hearth/releases/latest)
- Local installer: `artifacts/Hearth-Setup-1.1.1-win-x64.exe`
- Local source archive: `artifacts/Hearth-Source-1.1.1.zip`
- [First-time hosting, publishing, and joining](docs/FIRST-RUN.md)
- [Verification and remaining manual checks](docs/VERIFICATION.md)
- [HTTP protocol and host data](docs/HOST-PROTOCOL.md)
- [Changes in 1.1.1](docs/RELEASE-NOTES-1.1.1.md)
- [All 35 reliability and sharing changes](docs/RELIABILITY-1.1.0.md)

## Startup and the system tray

Open **Settings → Startup & closing**. Both options are off by default:

- **Close to system tray** keeps Hearth running when you close its window. Double-click its tray icon to reopen it, or right-click for **Open Hearth** and **Quit Hearth**.
- **Start Hearth with Windows** starts the launcher when your Windows account signs in. It does not launch before sign-in. If close to tray is also enabled, Hearth starts quietly in the tray.

Hosting continues while the window is minimized or Hearth is in the tray. If hosting is running and close to tray is off, closing asks whether to **Keep hosting in the tray** or **Quit and stop hosting**. **Quit Hearth** stops local hosting. Enabled hosting resumes when Hearth next opens. To resume after a reboot, enable **Start Hearth with Windows** and sign in to Windows. Installer updates preserve these preferences; uninstall removes the launcher's normal startup entry.

## Mod editing and instance location

Owners can search their imported mod list by filename or group. Add local or custom JARs with the file picker, drag-and-drop, or Explorer copy/paste; copy selected JARs, replace, and remove before reviewing and publishing. Copying between Hearth packs preserves Required/Voxy/Distant Horizons groups. Imports are saved as verified snapshots so changing the source files does not break a draft. **Pack settings** edits the name and Minecraft/loader versions. **Review & publish** becomes available only when the draft has changes or needs its first release.

Untouched drafts follow the latest release automatically. A draft with local changes shows **Discard my changes** and **Keep editing** when another owner publishes. Keeping edits applies them to the newer release after confirmation, identifies conflicts, and retains unrelated changes. Publication still checks for a newer release before uploading and committing.

Each published pack has an approval-only shared invite, plus optional personal invites with a name, expiry, and single-use setting. Owners can reset or disable the shared link, revoke personal invites, decline pending requests, and invite an approved person to another pack. **People** shows the packs associated with each display name and invite label; it is an overview, not a verified identity directory. Host administrators can **Archive pack**, which preserves its data in the host's archive folder and ends download access. New owner packs appear in the owner's library automatically.

**Settings → Instance location** chooses another folder or drive. Hearth copies and verifies existing instances before switching, keeps worlds/settings/personal files, and retains the original folder as a backup. Close Minecraft and Prism before moving. The installer preserves this preference.

## Build and run

Requires Windows 10/11 x64 and the .NET 10 SDK pinned in `global.json`. The installer bundles the .NET runtime. Prism is downloaded from its official GitHub release during onboarding and handles Minecraft, Java, and Microsoft account sign-in.

```powershell
dotnet build Hearth.sln -c Release
dotnet run --project src/Hearth.App -c Release
dotnet run --project tests/Hearth.Tests -c Release
dotnet run --project tests/Hearth.App.Tests -c Release
python tests/host_security_smoke.py
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bootstrap-inno.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1
```

`bootstrap-inno.ps1` obtains a pinned official Inno Setup compiler and verifies its SHA-256 and publisher signature. You can instead install Inno Setup 6 or 7 yourself and pass `-IsccPath <ISCC.exe>` to the build script.

`build.ps1` builds the solution, runs integration and WPF component checks, publishes self-contained binaries, compiles the installer, packages source, and verifies the release files. `artifacts/SHA256SUMS.txt` covers both installer and source archive. Local verification profiles and downloaded build tools are excluded from source control and the source archive.

Use a separate local profile for testing:

```powershell
& .\src\Hearth.App\bin\Release\net10.0-windows\Hearth.App.exe --profile C:\HearthTest\Alice
```

## Implementation

| Project | Responsibility |
| --- | --- |
| `src/Hearth.App` | WPF launcher, state-aware library, publishing, approval, hosting setup, Prism and launcher updates |
| `src/Hearth.Host` | Launcher-owned ASP.NET Core host process, hashed bearer credentials, per-pack authorization, immutable blobs and atomic releases |
| `src/Hearth.Core` | HTTP client, validation, journaled synchronization, Prism handoff, verified GitHub updates |
| `tests/Hearth.Tests` | Real local-host and separate-profile integration, interrupted transfers, recovery, update fixtures |
| `tests/Hearth.App.Tests` | Encrypted profile migration and actual WPF navigation/access-state checks |
| `scripts` | Installer, self-contained build, release verification, one-time legacy service retirement |

Player credentials use Windows DPAPI for the current Windows user. Pack membership is separate from Microsoft authentication: an owner approves the requested fingerprint, and Prism authenticates the player's Minecraft account. Hearth does not collect account passwords.

The app keeps local management on `127.0.0.1:17842` and serves authenticated HTTPS directly on port `17843`. Hosting setup creates a private certificate identity, configures a scoped Windows Firewall rule with permission, detects network addresses, and attempts NAT-PMP/UPnP router mapping. If a router port is already in use, Hearth tries other external ports and includes the selected port in its links. Network changes and failed lease renewals trigger retries. **Use my own address** accepts an existing DNS, dynamic DNS, or VPN endpoint; it does not create that route or a DNS service. Remote managers receive the host's advertised endpoints for the correct Internet and same-network invites.

Generated host connection links and pack invites carry the expected certificate identity. Unsupported routers or ISP restrictions are reported; there is no operated relay or automatic DNS service. Router setup is kept separate from proof of outside reachability. Every pack metadata and file request checks current membership. Forwarded invites create pending requests. Removal blocks future authorized downloads; files already downloaded remain on the player's PC. A verified address change reconnects all saved packs for that host without moving their persistent instances.

Only declared `mods/*.jar` files are managed. Downloads are checked by length and SHA-256 before live mod changes. A durable journal restores previous files after a partial commit. Worlds, settings, unknown personal mods, and other personal data are preserved. An unmanaged filename collision blocks the update. Terrain selection installs Required mods plus the chosen option, keeping dependencies still used by Required mods. Terrain imports compare mod IDs to avoid adding a second copy of a Required dependency; local and host validation reject conflicting mod IDs before a release is committed.

## Launcher releases

Hearth checks [official releases](https://github.com/BigUziHert/Hearth) from **Settings → Updates**. Legacy custom-source settings are ignored; pack credentials and player preferences are preserved.

The [Windows release workflow](.github/workflows/windows-release.yml) builds and tests pull requests and main-branch changes. A `v*` tag also publishes a complete stable GitHub release after all checks pass. To release a later version:

1. Set `Directory.Build.props` to the new version, update script defaults and add `docs/RELEASE-NOTES-VERSION.md`.
2. Commit and push the source, then push a matching tag such as `v1.1.1`. Tag and source version must match exactly.
3. The workflow builds `Hearth-Setup-1.1.1-win-x64.exe`, a source ZIP, and `SHA256SUMS.txt`; uploads them to a draft; verifies the expected installer; and publishes the complete release.
4. In an older Hearth, open **Settings → Updates**, then use **Check for updates → Download update → Restart to update**.

For a manual release, run `scripts/build.ps1 -Version 1.1.1` after changing the source version, then upload the resulting installer, source ZIP, and checksums to a stable `v1.1.1` release. The release must contain exactly one Windows installer whose version matches the tag. Do not replace files in a published release; publish a higher version.

The updater checks the GitHub asset digest or exact filename in SHA256SUMS, verifies bytes before saving and again before executing, and reuses a verified cached download. Installer updates preserve profiles, Prism instances, and hosted packs. The first secure-host setup asks Windows permission for a scoped firewall rule; canceling leaves hosting disabled. Later launches reuse that rule without elevation. When upgrading an old independent `HearthHost` service, Windows asks once to retire it; canceling stops the upgrade before replacing the app. The existing host may still be running if approval is canceled. Installers are unsigned until a code-signing certificate is configured.

## Storage and backup

| Data | Default location |
| --- | --- |
| Player profile and encrypted credentials | `%LOCALAPPDATA%\Hearth\profile.bin` |
| Personal Minecraft instances | `%LOCALAPPDATA%\Hearth\Prism\instances` by default; change under Settings → Instance location |
| Download cache and local drafts | `%LOCALAPPDATA%\Hearth\cache`, `Drafts` |
| New hosted packs and member state | `%LOCALAPPDATA%\Hearth\Host` (or `Host` inside a separate profile) |
| Migrated service data | Existing location, normally `%PROGRAMDATA%\Hearth\Host` |
| Per-user installed launcher | `%LOCALAPPDATA%\Programs\Hearth` |

Back up host data while Hearth is fully closed or hosting is stopped. Keep an owner key in a password manager: DPAPI data cannot be moved to another Windows account as a credential backup. **Share packs → This PC → Recover or replace owner key** verifies a replacement before saving it; an incorrect key does not lock out recovery. Profile writes keep a flushed `profile.bin.bak`; loading tries that backup before starting a fresh profile. Unreadable profile files are preserved and their location is reported for recovery.

Uninstall preserves data and stops hosting. Legacy service migration keeps existing data in place and grants the installing Windows account access; packs, releases, members, and owner credentials stay unchanged. Old protected service binaries are left inert for recovery, with no registered service to start them.

Prism Launcher and Minecraft are separate projects. Hearth is not affiliated with Mojang, Microsoft, or Prism Launcher.
