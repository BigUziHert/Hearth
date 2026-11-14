# Your first Hearth pack

## Join and play

1. Install `Hearth-Setup-1.0.9-win-x64.exe` and open Hearth.
2. Select **Add modpack**, paste your friend's `hearth://join?...` invite, enter the name they know you by, and select **Request access**. You can also open an invite directly after installing Hearth.
3. Select the pack in the sidebar. Open **⋯ → Member details & fingerprint** and tell the owner your name and fingerprint so they approve the correct request. Select **Check approval** after they approve you.
4. The main button becomes **Set up Prism** if Minecraft setup is needed. Hearth downloads and verifies the official portable Windows launcher. Finish Prism's first-run wizard, then use **Settings → Accounts → Add Microsoft** inside Prism. Sign in with your own account that owns Minecraft Java and let Prism configure Java. You can reopen Prism from Hearth's **Settings → Open Prism**.
5. Close the Hearth Prism window. Choose **Off**, **Voxy**, or **Distant Horizons** if the owner offers that option, then press **Play**. Hearth installs or updates the pack, verifies its files, and opens its persistent Prism instance. The first launch also downloads Minecraft and its loader.
6. In Minecraft, use **Multiplayer → Add Server** for your existing world server's address. Hearth's file-host address and the Minecraft server address have different jobs.

Close this pack's Minecraft and Hearth Prism window before updating or changing terrain. If a transfer fails, retry Play. Play installed verifies the saved release before launching; if its managed files are damaged, reconnect to the host and use Play to repair them. When a host is unavailable and a complete copy is installed, the main button becomes **Play installed**; this uses the last complete copy without applying updates or a terrain change. **⋯ → Play installed release** also opens that copy. Prism account requirements still apply.

Your local worlds, controls, graphics, screenshots, and other personal files remain in the same instance. A conflicting personal JAR is left alone; move or rename it and retry. **⋯ → Remove from library** removes the saved pack entry while keeping its downloaded instance. A current invite lets you request access again.

## Startup and closing

In **Settings → Startup & closing**, choose either option independently. Both are off by default.

- **Close to system tray**: closing the window keeps Hearth running. Find its icon beside the Windows clock or under the hidden-icons arrow. Double-click to reopen it, or right-click and select **Open Hearth** or **Quit Hearth**. Quit exits the launcher completely.
- **Start Hearth with Windows**: open Hearth when your Windows account signs in after starting the PC. This starts the launcher after sign-in, not before login. If **Close to system tray** is also enabled, it starts quietly in the tray.

You can turn either option off here. If this PC hosts packs, hosting continues while Hearth is open, minimized, or in the tray. **Quit Hearth** stops hosting. Closing the window stops hosting too when **Close to system tray** is off. Hearth remembers enabled hosting and resumes it when the app next opens. Enable **Start Hearth with Windows** to resume it after you sign in following a reboot. Updates keep your preferences, and uninstall removes automatic startup for the normal launcher profile.

## Host from your always-on Windows PC

1. Install the same Hearth installer on the server PC. Open **Hosting → Host on this PC → Enable hosting**. Fresh hosting does not require administrator permission. Keep Hearth open or enable **Settings → Startup & closing → Close to system tray**. Use **Quit Hearth** whenever you want to stop hosting completely.
2. Return to **Hosting** and confirm that hosting is running. The local host listens on `http://127.0.0.1:17842`; keep the Minecraft world server on its existing port. To resume hosting after a reboot, enable **Start Hearth with Windows** and sign in to Windows.
3. Expand the local connection's **Connection options**, select **Copy owner key**, and save it securely. Use that key on your main PC to administer the host. Ordinary players get invites, never the owner key.
4. Give the host a stable HTTPS address using one of the choices below. In **Hosting → Connect an existing host**, enter that address and the owner key you copied, then select **Connect**. Use the public connection when sharing invites. A local test invite works only on the host PC.

### A stable tunnel, without router changes

One option is [Tailscale Funnel](https://tailscale.com/docs/features/tailscale-funnel). Install/sign into Tailscale on the server PC and enable its Windows **Run unattended** setting so it remains connected before login. Open an Administrator PowerShell, then run:

```powershell
tailscale funnel --bg 17842
tailscale funnel status
```

Follow Tailscale's account approval steps for Funnel/HTTPS and copy the resulting `https://...ts.net` address into Hearth. Friends use the public address; they do not need the Tailscale client for Funnel. `--bg` keeps the forwarding configuration in the background. The [Funnel command reference](https://tailscale.com/docs/reference/tailscale-cli/funnel) describes status and turning it off.

### Your own domain and router

1. Point a DNS hostname you own, such as `packs.example.net`, at your public IP. Reserve the host PC's LAN address in your router.
2. Install [Caddy](https://caddyserver.com/docs/quick-starts/https) on the server and forward TCP ports **80 and 443** from the router to that PC. Allow Caddy through Windows Firewall on those ports. Do not expose Hearth's raw HTTP port to the internet.
3. Run this initial check in a terminal (replace the example hostname):

```powershell
caddy reverse-proxy --from packs.example.net --to 127.0.0.1:17842
```

4. Configure Caddy to start automatically as a Windows service or startup task using its Windows installation guidance. Closing a foreground Caddy terminal stops that forwarding process. Hearth itself must remain open or in the tray to serve packs.
5. Use `https://packs.example.net` in Hearth. Caddy handles certificates and forwards requests to the local host. If your ISP uses carrier-grade NAT, inbound port forwarding may not work; use a tunnel or obtain a reachable public address.

The public endpoint must preserve Authorization headers, allow PUT/POST and up to 512 MB JAR uploads, and avoid caching private API responses. A proxy's upload limit or timeout can block larger mods.

## Check actual outside access

**Running locally** is not proof that a friend can connect. On a phone with Wi-Fi disabled, open `https://YOUR-HOST/health`; it should return JSON identifying Hearth. Then have a friend on a different network install Hearth, request a pack, get approved, and press Play. Use **Hosting → Check connection** for the last successful player connection. That timestamp proves a player authenticated; confirm with the friend that their download completed. Finally reboot the host PC, sign in, and repeat this check after Hearth has started to verify your startup and tunnel/proxy settings. Quit Hearth and confirm the endpoint stops responding; reopen Hearth to resume hosting.

## Create, publish, and share

1. Open **Manage modpacks**, choose the host, and select **New pack**. Enter the pack name, Minecraft version, mod loader, and loader version, then **Create pack**. Compatible version lists load automatically; choose versions that match your mods.
2. On the **Mods** tab, add or drag in JARs, including custom builds. Select an existing file to **Replace** or **Remove** it. The published release stays unchanged while you edit.
3. Always-required mods belong in **Base**. Under **Optional terrain mods**, **Add Voxy** and **Add Distant Horizons** fetch matching Modrinth versions and required dependencies. **Choose .jar files** lets you supply tested JARs manually. Put dependencies also needed by the pack in Base so switching Off keeps them. Test exact versions together before publishing; catalog matching and valid hashes do not prove game compatibility.
4. Select **Review & publish** at the top of the pack editor, inspect added/replaced/removed files, then **Publish**. The host makes a release visible only after every referenced upload is verified.
5. Open **Members → Copy invite** and share it. If the connection is localhost, Hearth instead offers **Copy local test invite** and **Open Hosting**; connect your public HTTPS address before inviting outside friends. Use **Refresh requests**, compare fingerprints, and **Approve** each friend. Every pack has separate approvals; a forwarded link grants no downloads.
6. **Remove** blocks a person's future authorized downloads. **Make owner** allows that person to publish and manage this pack. Owners can open **⋯ → Manage as owner** from their joined pack.

To manage from your main PC, install Hearth there and use **Hosting → Connect an existing host**, entering the server's public HTTPS address and owner key. You can host multiple packs, connect other hosts, and use **Open in my library** to play your own packs. Publish from one PC at a time. If someone publishes a newer release while you are editing, Hearth keeps your draft and asks you to discard it before starting from the current release. Use the same host address consistently when playing a pack; localhost and public-address connections have separate local instances.

## Update Hearth itself

Open **Settings → Updates → Check for updates**. Hearth uses [official releases](https://github.com/BigUziHert/Hearth/releases) automatically. When a newer version exists, read **What's new**, select **Download update**, then **Restart to update**. A verified download remains available when you navigate away and return. The installer preserves player and host data. Hosting stops while Hearth restarts and resumes with the app if enabled.

Upgrading from the old independent Windows service requires one administrator approval to stop and unregister it. The upgrade keeps its existing host data, releases, members, and owner key. Canceling that prompt cancels the upgrade before the app is replaced; the old service may still be running. Retry the installer and approve the conversion to complete the switch. Fresh hosting and later updates do not need this conversion.

Hearth uses its official release source, including for profiles that previously saved a custom repository. Pack updates still come from each pack's host when you press Play. In **Settings → Minecraft**, **Choose existing Prism** connects an installation you already have. The **Help** section contains **Open data folder** and **Open guide**.

## Stop or move hosting

**Hosting → Host on this PC → Disable hosting** stops serving packs and turns off automatic hosting when Hearth opens. Enabling again reuses the existing host data and your saved owner credential. Quitting Hearth stops hosting without forgetting that it should resume next time. Uninstalling stops hosting and preserves data. Back up `%LOCALAPPDATA%\Hearth\Host` while hosting is stopped and retain your owner key before moving to another PC. A host migrated from the Windows service keeps its existing folder, normally `%PROGRAMDATA%\Hearth\Host`; back up that folder instead.
