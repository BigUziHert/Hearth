# Your first Hearth pack

## Join and play

1. Install `Hearth-Setup-1.0.13-win-x64.exe` and open Hearth.
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

1. Install the same Hearth installer on the server PC. Open **Hosting → Host on this PC → Enable hosting**. Approve the Windows permission prompt so Hearth can allow its secure hosting port through the firewall. Canceling leaves hosting disabled and does not add a broken connection. You can retry without recovering or entering an owner key.
2. Hearth creates its HTTPS identity, finds this PC's network address, and attempts automatic router setup. No tunnel, domain, reverse proxy, or certificate installation is required. Keep Hearth open or enable **Settings → Startup & closing → Close to system tray**. **Quit Hearth** stops both local and secure hosting.
3. The **This PC** card shows **On your home network** and, when router setup succeeds, an **Internet address**. These addresses are filled in by Hearth. **Copy home network link** is for another PC on the same network; **Copy host connection link** under the Internet address is for your remote management PC. Copy the **owner key** separately from **Connection options**. Players get pack invites instead of either owner credential.
4. On your main PC, open or paste that connection link under **Hosting → Connect an existing host**, paste the owner key, and select **Connect**. The link includes the host's certificate identity; a plain address does not contain that information. Each connection checks the host before sending credentials.
5. To resume after a reboot, enable **Start Hearth with Windows** and sign in to Windows. Hosting resumes with the app. Startup never opens an administrator prompt. If an older install has not granted the firewall permission yet, open **Hosting → Retry connection setup** once and approve Windows.

Hearth uses local management port **17842** and its own encrypted sharing port **17843** by default. It does not take port 443 from MC Server UI or change the Minecraft world server. Changing the local management port in a development profile also changes sharing to the next port.

### If automatic Internet setup cannot finish

Hearth keeps home-network sharing available and explains what it could not configure. **Retry connection setup** rechecks Windows and the router and briefly restarts hosting. Some routers disable automatic port mapping; another router or the Internet provider may also prevent incoming connections. Hearth cannot create an inbound route through an uncooperative network. Ask your Internet provider whether your connection has a public address and permits incoming connections. This version does not include an operated relay service. It does not send users through manual tunnel or proxy setup.

Automatic setup tries NAT-PMP and UPnP, changes only its own mapping, and renews it while Hearth runs. Normal shutdown removes the owned mapping. A finite mapping expires after a crash. Some routers only support permanent mappings; if Hearth crashes those may remain until a subsequent normal stop or router cleanup, but no Hearth host process continues serving files after the app exits. The narrowly scoped firewall rule remains available for later launches.

### Confirm a friend can connect

A successful router setup is not proof of Internet reachability. Have a friend on a different network install Hearth, open an **Internet invite** for a pack, request access, get approved, and press **Play**. **Hosting → Check connection** reports the last approved player's request and separately records a confirmed outside connection when one arrives directly over HTTPS from a public IPv4 address; confirm with that friend that the download finished. A request from a PC on your home network does not prove outside access. Browser visits to a built-in host address are not the test: Hearth trusts the exact certificate inside the connection link rather than installing public browser trust.

Quit Hearth and confirm that your friend can no longer refresh or download from the host; reopening it resumes hosting. Test again after rebooting the PC and signing in. If the Internet provider changes your public IP, use **Retry connection setup**, copy a fresh invite or host connection link, and send it to existing users. A fresh link for the same verified host reconnects an existing pack while preserving its Minecraft instance, approval, and terrain preference. There is no background address directory or automatic DNS service in this version.

## Create, publish, and share

1. Open **Manage modpacks**, choose the host, and select **New pack**. Enter the pack name, Minecraft version, mod loader, and loader version, then **Create pack**. Compatible version lists load automatically; choose versions that match your mods.
2. On the **Mods** tab, add or drag in JARs, including custom builds. Select an existing file to **Replace** or **Remove** it. The published release stays unchanged while you edit.
3. Always-required mods belong in **Base**. Under **Optional terrain mods**, **Add Voxy** and **Add Distant Horizons** fetch matching Modrinth versions and required dependencies. **Choose .jar files** lets you supply tested JARs manually. Put dependencies also needed by the pack in Base so switching Off keeps them. Test exact versions together before publishing; catalog matching and valid hashes do not prove game compatibility.
4. Select **Review & publish** at the top of the pack editor, inspect added/replaced/removed files, then **Publish**. The host makes a release visible only after every referenced upload is verified.
5. Open **Members** and select **Copy internet invite** for outside friends or **Copy same-network invite** for someone on the same network. If sharing is not ready, use **Set up hosting** to finish setup. Use **Refresh requests**, compare fingerprints, and **Approve** each friend. Every pack has separate approvals; a forwarded link grants no downloads.
6. **Remove** blocks a person's future authorized downloads. **Make owner** allows that person to publish and manage this pack. Owners can open **⋯ → Manage as owner** from their joined pack.

To manage from your main PC, install Hearth there and use **Hosting → Connect an existing host**, pasting the host connection link and owner key copied from the server PC. You can host multiple packs, connect other hosts, and use **Open in my library** to play your own packs. Publish from one PC at a time. If someone publishes a newer release while you are editing, Hearth keeps your draft and asks you to discard it before starting from the current release. Refreshed links for the same verified host preserve the existing local instance. A changed certificate identity is blocked; check with the owner before removing a saved connection.

## Update Hearth itself

Open **Settings → Updates → Check for updates**. Hearth uses [official releases](https://github.com/BigUziHert/Hearth/releases) automatically. When a newer version exists, read **What's new**, select **Download update**, then **Restart to update**. A verified download remains available when you navigate away and return. The installer preserves player and host data. Hosting stops while Hearth restarts and resumes with the app if enabled.

Upgrading from the old independent Windows service requires one administrator approval to stop and unregister it. The upgrade keeps its existing host data, releases, members, and owner key. Canceling that prompt cancels the upgrade before the app is replaced; the old service may still be running. Retry the installer and approve the conversion to complete the switch. Fresh hosting and later updates do not need this conversion.

Hearth uses its official release source, including for profiles that previously saved a custom repository. Pack updates still come from each pack's host when you press Play. In **Settings → Minecraft**, **Choose existing Prism** connects an installation you already have. The **Help** section contains **Open data folder** and **Open guide**.

## Stop or move hosting

**Hosting → Host on this PC → Disable hosting** stops serving packs and turns off automatic hosting when Hearth opens. Enabling again reuses the existing host data and your saved owner credential. Quitting Hearth stops hosting without forgetting that it should resume next time. Uninstalling stops hosting and preserves data. Back up `%LOCALAPPDATA%\Hearth\Host` while hosting is stopped and retain your owner key before moving to another PC. A host migrated from the Windows service keeps its existing folder, normally `%PROGRAMDATA%\Hearth\Host`; back up that folder instead.
