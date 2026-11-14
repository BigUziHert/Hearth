# Hearth 1.0.8

- Hosting now runs only while Hearth is open or in the system tray. **Quit Hearth**, closing without close to tray, and Windows sign-out stop local hosting.
- Enabled hosting resumes when Hearth opens. **Start Hearth with Windows** starts it after Windows sign-in; combine it with **Close to system tray** for a quiet startup.
- Fresh hosting no longer installs a Windows service or asks for administrator permission.
- Upgrades retire the old independent service before replacing the app. One-time administrator approval preserves its existing packs, releases, member approvals, port, and owner credentials. Canceling the prompt cancels the upgrade, and the old host may still be running until conversion completes.
- Hosting status and instructions describe the app's lifetime. The rest of the interface is unchanged.
