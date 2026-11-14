# Verification record

The automated test harness starts a real ASP.NET Hearth host on an unused loopback port and uses separate local Alice and Bob profiles. Test JARs are synthetic archives with known content; they do not assert compatibility of a real Minecraft mod combination.

## Version 1.0.2 navigation and updates

The current integration suite passes **76 checks**, including ten new updater regressions: exact release-tag/installer matching, normalized version comparison, verified cache reuse, preservation of a valid cached installer after a failed replacement, checksum-file fallback, trusted repository origin, and the no-release state. The controlled updater fixture does not require a public release. The final full test report is `artifacts/verification/20261114-001910-90dc50/report.json`; logs are `artifacts/build-1.0.2-final.log` and `artifacts/host-security-1.0.2.log` (46 security checks). The final packaging log is `artifacts/build-1.0.2-release.log`.

The separate WPF component suite passes **28 checks** against actual application controls and local HTTP fixtures. It covers encrypted profile migration to the official update source while preserving custom overrides, native window controls, empty-library joining, direct Hosting navigation, Settings defaults, pending/approved/removed/unpublished membership, Prism setup as the next action, per-pack terrain persistence, and the offline installed-copy action. It uses a test-only Application with the real theme resources and never invokes the production startup/profile handler. No Microsoft account or Minecraft process is launched by these tests.

Off-screen renders of the real WPF content are available under `artifacts/screenshots/1.0.2-*.png`, including normal and minimum-size empty layouts, joining, Hosting, Settings, pending/approved packs, and the owner's Mods and Members pages. These are component renders without the operating system's title bar. An independent real-host owner harness also passed ten control-flow checks for pack loading, the split editor, staged changes, navigation back to drafts, and approval.

The Windows release workflow builds and tests source, verifies installer/source/checksums, and publishes only after a matching version tag completes successfully. `scripts/verify-release.ps1` checks that the source archive excludes local profiles, build output, tools, credentials, and personal absolute paths. The official source is now `BigUziHert/Hearth`; actual Actions and public check/download/install verification are recorded separately after publishing.

### Public release and installed updater verification

The tagged GitHub Actions run [43533562242](https://github.com/BigUziHert/Hearth/actions/runs/43533562242) completed successfully, including the Windows build, tests, package verification, and stable release publication. The main-branch run also passed. The published release is [v1.0.2](https://github.com/BigUziHert/Hearth/releases/tag/v1.0.2).

The production GithubUpdater checked the actual public repository as version 1.0.1, downloaded the published installer, verified its size and SHA-256, reused the verified cache, and executed the normal installer handoff. The installer returned exit code 0; the installed application changed from **1.0.1.0 to 1.0.2.0** and restarted as exactly one process. The encrypted normal profile remained byte-for-byte identical. A subsequent actual check at 1.0.2 reported no update available. In the installed app, clicking the footer's Check for updates automatically used Official Hearth releases and displayed **You're up to date**, without entering a repository.

The distributed installer is the actual GitHub Actions artifact, copied back into the local artifacts directory: 83,603,325 bytes; SHA-256 `5540c35f66a352c65b0fcdd2da92a2d3ce943c647a243909fffcc4fa09ae0508`. The matching public source ZIP and SHA256SUMS.txt are beside it. The earlier local build is retained separately under `artifacts/local-package-1.0.2`.

Live check/download/install JSON reports and the profile-preservation report are under `artifacts/live-update-probe`. Native maximize/restore, minimize/restore, and Close were exercised successfully; the app was reopened afterward. The actual installed update screen is captured in `artifacts/screenshots/1.0.2-installed-updates.jpg`. Component screenshots are identified separately above; they do not substitute for the installed-window check.

## Version 1.0.1 interface update

The refreshed interface was built with .NET 10 and passed the existing 66 integration checks with zero build warnings or errors. The report is `artifacts/verification/20261113-224333-2a475e/report.json`; the complete build log is `artifacts/build-1.0.1.log`.

The actual production installer handoff upgraded the conventional installed application from **1.0.0.0 to 1.0.1.0**, returned exit code 0, and restarted one Hearth process. Installed App and Host DLL hashes match the final published payloads, and the `hearth://` protocol still points to `%LOCALAPPDATA%\Programs\Hearth\Hearth.App.exe`. The normal profile was empty before this update and remained empty; no synthetic test data was inserted into it. Evidence is `artifacts/install-smoke-1.0.1/report.json`.

Populated UI checks use separate cloned profiles under `artifacts/ui-refresh-smoke`, with two existing local packs. Optional terrain JARs added to that fixture are inert synthetic archives for UI state verification. They are not real Voxy or Distant Horizons distributions. The underlying player-data preservation and real terrain-resolver checks below remain covered by the integration suite and earlier installation tests.

Native inspection confirmed the two-pack sidebar, the selected-pack hero and Play layout, host-derived status, unavailable terrain controls, and the dark pack-options menu. The packaged first-run view and Prism onboarding prompt were also observed. No durable screenshots were saved for this UI revision because the capture surface sometimes showed an unrelated foreground window. Terrain-click persistence and editable version-dropdown interaction were not independently verified in this visual pass; the underlying terrain synchronization remains covered by the automated checks.

## Automated checks completed

`dotnet run --project tests/Hearth.Tests -c Release` originally passed 66 checks on the .NET 10 build (now 76 in 1.0.2):

- Two independent private packs, distinct approved players, pending forwarded invites, and denied anonymous access.
- Pack-scoped metadata and actual byte-download authorization, owner-only publication/member changes, removal and fresh-join credential separation, delegated ownership, restart persistence, and no plaintext bearer tokens in host JSON.
- Custom JAR add, same-name replacement, and removal; incomplete release rejection and stale draft protection; published snapshot unchanged during upload.
- Injected connection loss after partial bytes: old JAR and installed manifest preserved, partial staging cleaned, retry succeeds.
- Simulated process exit during commit: journal rollback restores the durable previous file.
- Voxy → Distant Horizons → Off with shared option dependencies and a dependency also required by Base.
- Local world bytes, keybind/graphics `options.txt`, server list, screenshots, custom configuration, unknown personal mod, and Prism settings preserved. Unmanaged filename conflict blocks installation.
- Two different JAR filenames with the same Fabric mod ID are rejected by the host before publication. The client also independently rejects a malformed host's duplicate-ID release before installation, preserving the prior complete release.
- Prism instance format contains Minecraft/loader components. An actual child-process probe receives the expected `--dir` and `--launch` arguments with the persistent instance ID.
- GitHub release fixture exercises check without download, version comparison, digest-verified download, corrupt download discard, and re-verification before starting an installer.

Evidence is written under `artifacts/verification/<run>/report.json` and `host.log`; `artifacts/verification/latest.txt` identifies the latest run. The final full integration report is `artifacts/verification/20261113-215839-e849f3/report.json`. `python tests/host_security_smoke.py` passed another 46 HTTP authorization and path/input checks on .NET 10.

## Environment-dependent checks

- **Microsoft/Minecraft:** actual Microsoft authentication and reaching a Minecraft world require the user's account. Follow FIRST-RUN steps to sign into Prism, close it, and press Play on a tested real pack. No Microsoft credentials were supplied or fabricated.
- **Public network:** no domain, router configuration, tunnel account, or friend on another network was supplied. Local API reachability is verified. Use the outside-network health, approved download, and post-reboot steps in FIRST-RUN to verify your deployment.
- **Windows service:** the build shell is not elevated. Service initialization/run logic is exercised as a separate background process; actual SCM registration, LocalService permissions, and reboot startup need the normal **Enable hosting** elevation and the reboot check on the intended server PC.
- **Real GitHub release:** the official repository is now configured. Controlled check/download/integrity tests and earlier actual local installer execution are verified. The public release workflow and an installed client's live check/download/restart are separate deployment checks; see the version-specific record above.
- **Optional terrain compatibility:** synthetic group switching is verified. Test Voxy/Distant Horizons and their dependencies with the exact chosen Minecraft and loader version and your GPU before publishing.

## Native UI and Prism check

The local host contained the two seeded packs **Mosswood** and **Afterlight**. Mosswood was opened in the owner UI, and Juniper's pending access was approved through **Manage members**. In a separate player profile, **Play** downloaded the pack and launched the actual official Prism Launcher 11.1.1 into its first-run wizard. Screenshots are saved at `artifacts/screenshots/player-installed.png` and `artifacts/screenshots/prism-handoff.png`. Microsoft sign-in and starting Minecraft remain user-dependent.

The official Modrinth resolver also fetched Voxy for Minecraft 1.21.10/Fabric with its Sodium and Fabric API dependencies, and a matching Distant Horizons release. This verifies real catalog selection and download integrity, not GPU rendering or full-pack gameplay. Evidence is under `artifacts/terrain-smoke`.

## Windows installer and restart check

The Windows x64 installer was compiled with the custom Hearth icon and self-contained .NET 10.0.15 runtime. An actual silent per-user installation into `artifacts/install-smoke/installed` returned exit code 0. Both launcher and optional host payloads were present, and the `hearth://` protocol was registered. The installed launcher created its native window without requiring the system's .NET 10 runtime.

After the final UI fixes were packaged, the production `GithubUpdater.StartInstaller` method verified the actual installer digest and launched it. The installer replaced the older local build, returned exit code 0, and restarted exactly one installed Hearth process. SHA-256 checks confirmed the installed App and Host DLLs matched the final published binaries. The encrypted profile, a world-file sentinel, and the player's keybind/graphics settings sentinel were byte-for-byte unchanged. Evidence is `artifacts/install-smoke/report.json` and `initial-install.log`.

This exercised a local rebuild-to-rebuild update at version 1.0.0. Higher-version detection was verified separately by the GitHub-compatible fixture. No real GitHub release was published. The installer is unsigned because no code-signing certificate was supplied.

The temporary default test profile was moved to `artifacts/install-smoke/profile-preserved-after-update` after verification. The installed app was reopened with a clean first-run profile; the synthetic background host was stopped. No permanent Windows host service was installed by these tests.

Finally, the same verified installer was installed into the conventional `%LOCALAPPDATA%\Programs\Hearth` directory. The protocol handler and uninstall entry were checked against this location, and the clean installed app was left open there. The isolated installation and its evidence remain under artifacts.
