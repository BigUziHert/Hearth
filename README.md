# Hearth

A native Windows launcher for private Minecraft Java modpacks. Owners publish complete releases; friends join by invite and press **Play** to update their persistent Prism instance before Minecraft starts.

Hearth shows the next action for each pack: request approval, set up Prism, install and play, or play the installed copy when its host is offline. **Add modpack** opens invites, **Manage modpacks** handles publishing and members, **Hosting** connects or runs hosts, and **Settings** contains startup and closing preferences, Prism, and launcher updates.

The same installer contains the launcher and an optional independent Windows host service. A host can serve multiple private packs; a player can join multiple hosts. Hearth distributes mod files. Your existing Minecraft server still runs the shared world.

## Start here

- [Download the latest Windows installer](https://github.com/BigUziHert/Hearth/releases/latest)
- Local installer: `artifacts/Hearth-Setup-1.0.5-win-x64.exe`
- Local source archive: `artifacts/Hearth-Source-1.0.5.zip`
- [First-time hosting, publishing, and joining](docs/FIRST-RUN.md)
- [Verification and remaining manual checks](docs/VERIFICATION.md)
- [HTTP protocol and host data](docs/HOST-PROTOCOL.md)
- [Changes in 1.0.5](docs/RELEASE-NOTES-1.0.5.md)

## Startup and the system tray

Open **Settings → Startup & closing**. Both options are off by default:

- **Close to system tray** keeps Hearth running when you close its window. Double-click its tray icon to reopen it, or right-click for **Open Hearth** and **Quit Hearth**.
- **Start Hearth with Windows** starts the launcher when your Windows account signs in. It does not launch before sign-in. If close to tray is also enabled, Hearth starts quietly in the tray.

Turning these options off restores normal window closing and disables automatic launcher startup. The optional hosting service remains independent: closing or quitting the launcher does not stop hosting. Installer updates preserve these preferences; uninstall removes the launcher's normal startup entry.

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
| `src/Hearth.Host` | ASP.NET Core Windows service, hashed bearer credentials, per-pack authorization, immutable blobs and atomic releases |
| `src/Hearth.Core` | HTTP client, validation, journaled synchronization, Prism handoff, verified GitHub updates |
| `tests/Hearth.Tests` | Real local-host and separate-profile integration, interrupted transfers, recovery, update fixtures |
| `tests/Hearth.App.Tests` | Encrypted profile migration and actual WPF navigation/access-state checks |
| `scripts` | Installer, self-contained build, release verification, elevated service management |

Player credentials use Windows DPAPI for the current Windows user. Pack membership is separate from Microsoft authentication: an owner approves the requested fingerprint, and Prism authenticates the player's Minecraft account. Hearth does not collect account passwords.

The host defaults to `127.0.0.1:17842`; remote clients require HTTPS through a stable tunnel or reverse proxy. Every pack metadata and file request checks current membership. Forwarded invites create pending requests. Removal blocks future authorized downloads; files already downloaded remain on the player's PC.

Only declared `mods/*.jar` files are managed. Downloads are checked by length and SHA-256 before live mod changes. A durable journal restores previous files after a partial commit. Worlds, settings, unknown personal mods, and other personal data are preserved. An unmanaged filename collision blocks the update. Terrain selection installs the union of Base and the chosen option, keeping dependencies still used by Base.

## Launcher releases

Hearth checks [official releases](https://github.com/BigUziHert/Hearth) from **Settings → Updates**. Legacy custom-source settings are ignored; pack credentials and player preferences are preserved.

The [Windows release workflow](.github/workflows/windows-release.yml) builds and tests pull requests and main-branch changes. A `v*` tag also publishes a complete stable GitHub release after all checks pass. To release a later version:

1. Set `Directory.Build.props` to the new version, update script defaults and add `docs/RELEASE-NOTES-VERSION.md`.
2. Commit and push the source, then push a matching tag such as `v1.0.6`. Tag and source version must match exactly.
3. The workflow builds `Hearth-Setup-1.0.6-win-x64.exe`, a source ZIP, and `SHA256SUMS.txt`; uploads them to a draft; verifies the expected installer; and publishes the complete release.
4. In an older Hearth, open **Settings → Updates**, then use **Check for updates → Download update → Restart to update**.

For a manual release, run `scripts/build.ps1 -Version 1.0.6` after changing the source version, then upload the resulting installer, source ZIP, and checksums to a stable `v1.0.6` release. The release must contain exactly one Windows installer whose version matches the tag. Do not replace files in a published release; publish a higher version.

The updater checks the GitHub asset digest or exact filename in SHA256SUMS, verifies bytes before saving and again before executing, and reuses a verified cached download. Installer updates preserve profiles, Prism instances, and hosted packs. Updating an enabled service requires elevation. Installers are unsigned until a code-signing certificate is configured.

## Storage and backup

| Data | Default location |
| --- | --- |
| Player profile and encrypted credentials | `%LOCALAPPDATA%\Hearth\profile.bin` |
| Personal Minecraft instances | `%LOCALAPPDATA%\Hearth\Prism\instances` |
| Download cache and local drafts | `%LOCALAPPDATA%\Hearth\cache`, `Drafts` |
| Hosted packs and member state | `%PROGRAMDATA%\Hearth\Host` |
| Independent service binaries | `%PROGRAMDATA%\Hearth\Service` |
| Per-user installed launcher | `%LOCALAPPDATA%\Programs\Hearth` |

Back up host data while the service is stopped. Keep an owner key in a password manager: DPAPI data cannot be moved to another Windows account as a credential backup. Uninstall preserves data and leaves the independent host running. Stop hosting first if it should go offline.

Prism Launcher and Minecraft are separate projects. Hearth is not affiliated with Mojang, Microsoft, or Prism Launcher.
