# Your first Hearth pack

## Join and play

1. Install `Hearth-Setup-1.1.1-win-x64.exe` and open Hearth.
2. Select **Add modpack**, paste your friend's `hearth://join?...` invite, check the name shown under it, and select **Request access**. **Change name in Settings** opens the one shared profile-name setting and keeps your pasted invite. You can also open an invite directly after installing Hearth.
3. Select the pack in the sidebar. Its pending screen shows your fingerprint with **Copy fingerprint**. Send your name and fingerprint to the owner so they approve the correct request. Hearth rechecks automatically while this pack is open, about every 30 seconds and when the window is activated; **Check approval** also checks immediately.
4. The main button becomes **Set up Prism** if Minecraft setup is needed. Hearth downloads and verifies the official portable Windows launcher. Finish Prism's first-run wizard, then use **Settings → Accounts → Add Microsoft** inside Prism. Sign in with your own account that owns Minecraft Java and let Prism configure Java. You can reopen Prism from Hearth's **Settings → Open Prism**.
5. Close the Hearth Prism window. Choose **Off**, **Voxy**, or **Distant Horizons** if the owner offers that option, then press **Play**. Hearth installs or updates the pack, verifies its files, and opens its persistent Prism instance. The first launch also downloads Minecraft and its loader.
6. In Minecraft, use **Multiplayer → Add Server** for your existing world server's address. Hearth's file-host address and the Minecraft server address have different jobs.

Close this pack's Minecraft and Hearth Prism window before updating or changing terrain. If a transfer fails, retry Play. Play installed verifies the saved release before launching; if its managed files are damaged, reconnect to the host and use Play to repair them. When a host is unavailable and a complete copy is installed, the main button becomes **Play installed**; this uses the last complete copy without applying updates or a terrain change. Prism account requirements still apply. When a new release is available, **What's new** shows the owner's release notes.

Your local worlds, controls, graphics, screenshots, and other personal files remain in the same instance. A conflicting personal JAR is left alone; move or rename it and retry. **⋯ → Remove from library** removes the saved pack entry while keeping its downloaded instance. A current invite lets you request access again and reuses the existing instance for the verified host and pack, even if its address changed. A removed member sees **You were removed from this pack**; an archived pack reports that its owner archived it.

## Startup and closing

In **Settings → Startup & closing**, choose either option independently. Both are off by default.

- **Close to system tray**: closing the window keeps Hearth running. Find its icon beside the Windows clock or under the hidden-icons arrow. Double-click to reopen it, or right-click and select **Open Hearth** or **Quit Hearth**. Quit exits the launcher completely.
- **Start Hearth with Windows**: open Hearth when your Windows account signs in after starting the PC. This starts the launcher after sign-in, not before login. If **Close to system tray** is also enabled, it starts quietly in the tray.

You can turn either option off here. If this PC hosts packs, hosting continues while Hearth is open, minimized, or in the tray. **Quit Hearth** stops hosting. If hosting is running and **Close to system tray** is off, closing asks whether to **Keep hosting in the tray** or **Quit and stop hosting**. Hearth remembers enabled hosting and resumes it when the app next opens. Enable **Start Hearth with Windows** to resume it after you sign in following a reboot. Updates keep your preferences, and uninstall removes automatic startup for the normal launcher profile.

## Choose an instance folder

Open **Settings → Instance location → Change folder…** and choose an empty folder, such as `D:\Minecraft`. Hearth copies all existing instances, verifies the copies, then saves the new location. Worlds, keybinds, graphics settings, screenshots, and personal files move with each instance. The previous folder stays as a backup, shown in Settings; it uses disk space until you remove it yourself after checking the new location.

Close Minecraft and the Hearth Prism window first. Canceling or a failed transfer keeps the old location active. If Hearth closes just before saving the new location, choose the same destination again to finish. Do not move the folder manually or change Prism's instance-directory setting independently; Hearth configures Prism to use the selected folder. Microsoft account settings and Prism itself remain in Hearth's app data folder.

## Host from your always-on Windows PC

1. Install the same Hearth installer on the server PC. Open **Share packs → This PC → Enable hosting**. Approve the Windows permission prompt so Hearth can allow its secure hosting port through the firewall. Canceling initial setup leaves hosting disabled and does not add a broken connection. You can retry without recovering or entering an owner key.
2. Hearth creates its HTTPS identity, finds this PC's network address, and attempts automatic router setup. No tunnel, domain, reverse proxy, or certificate installation is required. Keep Hearth open or enable **Settings → Startup & closing → Close to system tray**. **Quit Hearth** stops both local and secure hosting.
3. The **This PC** card shows a **Home-network address** and, when router setup succeeds, an **Internet address**. These addresses are filled in by Hearth. **Copy home-network link** is for another PC on the same network; **Copy host connection link** under the Internet address is for your remote management PC. Use **Copy owner key** separately and keep the key private. Players get pack invites instead of owner credentials.
4. On your main PC, open or paste that connection link under **Share packs → Connect an existing host**, paste the owner key, and select **Connect**. The link includes the host's certificate identity; a plain address does not contain that information. A host connection link is for managing packs and is different from a player's pack invite. Connected hosts can be renamed under **Connection options → Rename**.
5. To resume after a reboot, enable **Start Hearth with Windows** and sign in to Windows. Hosting resumes with the app. Startup never opens an administrator prompt. If an older install has not granted the firewall permission yet, open **Share packs → This PC → Retry connection setup** and approve Windows. A network that starts later is detected automatically.

Hearth uses local management port **17842** and its own encrypted sharing port **17843** by default. It does not take port 443 from MC Server UI or change the Minecraft world server. Two hosting PCs can use different external router ports; Hearth tries a small range when its first choice is occupied and includes the mapped port in the link. Changing the local management port in a development profile also changes sharing to the next port.

If existing host data needs its original key, expand **Recover or replace owner key** on the This PC card and enter it before enabling hosting. Hearth verifies it before saving. A wrong key leaves recovery available; a previously saved wrong key can be replaced here. This does not reset or remove the host's packs.

### If automatic Internet setup cannot finish

Hearth keeps home-network sharing available and explains what it could not configure. **Retry connection setup** rechecks Windows and the router; canceling its Windows permission prompt preserves an already running host. Network changes and failed router lease renewals are retried automatically. Some routers disable automatic port mapping; another router or the Internet provider may also prevent incoming connections. Hearth cannot create an inbound route through an uncooperative network. Ask your Internet provider whether your connection has a public address and permits incoming connections. This version does not include an operated relay service.

If you already have a routed DNS, dynamic DNS, or VPN address, expand **This PC → Use my own address**, enter the address and port, then **Use this address**. For example: `https://packs.example.com:17843`. Hearth uses it in Internet invites and host connection links. **Use automatic address** returns to router detection. This setting does not register a domain, configure a VPN, or forward a router port. The certificate identity in a Hearth link remains valid when its DNS name changes; a friend's successful connection is still needed to verify reachability.

Automatic setup tries NAT-PMP and UPnP, changes only its own mapping, and renews it while Hearth runs. Normal shutdown removes the owned mapping. A finite mapping expires after a crash. Some routers only support permanent mappings; if Hearth crashes those may remain until a subsequent normal stop or router cleanup, but no Hearth host process continues serving files after the app exits. The narrowly scoped firewall rule remains available for later launches.

### Confirm a friend can connect

A successful router setup is not proof of Internet reachability. Have a friend on a different network install Hearth, open an **Internet invite** for a pack, request access, get approved, and press **Play**. **Share packs → This PC → Check connection** reports the last approved player's request and separately records a confirmed outside connection when one arrives directly over HTTPS from a public IPv4 address; confirm with that friend that the download finished. A request from a PC on your home network does not prove outside access. Browser visits to a built-in host address are not the test: Hearth trusts the exact certificate inside the connection link rather than installing public browser trust.

Quit Hearth and confirm that your friend can no longer refresh or download from the host; reopening it resumes hosting. Test again after rebooting the PC and signing in. If the Internet provider changes your public IP, let Hearth recheck the network or use **Retry connection setup**, then copy a fresh invite or host connection link and send it to existing users. After verifying the HostId and certificate pin, a fresh link reconnects all saved packs and connections for that host while preserving their Minecraft instances, approvals, and terrain preferences. There is no background address directory or automatic DNS service in this version.

## Create, publish, and share

1. Open **Share packs** and select **New pack** on the chosen host's card. Enter the pack name, Minecraft version, mod loader, and loader version, then **Create pack**. Compatible version lists load automatically; choose versions that match your mods and world server. The pack appears in your own library automatically.
2. On the **Mods** tab, use **Add files…**, drag JARs onto the Mods card, or copy JAR files in Explorer and use **Paste mods** / **Ctrl+V**. **Search imported mods** filters the mods already in this pack by filename or group; it does not search an online catalog. Select existing files to **Copy**, **Replace…**, or **Remove** them. The published release stays unchanged while you edit. Imported JARs are copied into Hearth draft storage, so rebuilding or deleting the original file does not change the draft.
3. Always-required mods belong in **Required mods**. Under **Optional terrain mods**, **Add Voxy** and **Add Distant Horizons** fetch matching Modrinth versions and required dependencies. **Choose .jar files…** lets you supply tested JARs manually. Imports skip dependencies whose mod IDs are already provided by Required mods, so switching Off keeps them. Copying mods between Hearth packs preserves their groups. Test exact versions together before publishing; catalog matching and valid hashes do not prove game compatibility.
4. Use **Pack settings** to change the name or Minecraft/loader versions. Add optional release notes for players. Select **Review & publish** at the top of the editor, inspect the changes, then **Publish**. With no changes, publication is disabled. Duplicate mod-ID checks run before uploads, and the host makes a release visible only after every referenced upload is verified.
5. Open **Members** after the first release. Select **Copy internet invite** for outside friends or **Copy same-network invite** for someone on the same network. A manager connected over the home network can still obtain the host's advertised Internet invite. If sharing is not ready, use **Set up hosting**. Compare the pending request's fingerprint with the one your friend sent, then **Approve**. Counts beside your packs refresh about every minute; opening Members loads current requests.
6. **Decline** rejects a pending request; **Decline all** clears pending spam without removing approved members. **Remove member** blocks that person's future authorized downloads. Either removal dialog can also reset the shared invite link. **Make owner** allows an approved person to publish and manage this pack. Owners can use **⋯ → Manage as owner** from their library; delegated packs are grouped under a single **Shared with me** host entry.

To manage from your main PC, install Hearth there and use **Share packs → Connect an existing host**, pasting the host connection link and owner key copied from the server PC. You can host multiple packs, connect other hosts, and use the visible **Open in my library** button to play your own packs. If another owner publishes while your draft is untouched, Hearth loads that release automatically. For a draft with real edits, **Discard my changes** loads the latest release, or **Keep editing** asks to apply your edits to it. Conflicts are listed before your draft's values are kept; unrelated changes from the latest release remain. Review the merged draft before publishing. A changed certificate identity is blocked; check with the owner before removing a saved connection.

### Personal invites and People

On Members, **Create personal invite…** lets you label an invite for a person, choose whether it permits one request, and select an expiry. **Create and copy invite** puts the link on your clipboard. The resulting request says which named invite was used; every request still needs approval. Personal invites can be listed, copied again while available, or revoked. A forwarded link is not proof of who used it: compare fingerprints with the intended friend.

**Reset invite link** invalidates the old shared link while approved members keep access. Uncheck **Allow the shared invite link** to accept only personal invites. Resetting the shared link does not revoke personal invites; revoke those separately if needed.

An approved member's **Invite to another pack…** creates a personal invite to another published pack on the same host, labelled with their name. Send them that copied link; it does not silently enroll them. **People** on a host card summarizes the packs associated with each display name and invite label. Names and labels are for organization and do not replace fingerprint verification.

### Archive a pack

A host administrator can open **Mods → Pack options → Archive pack** and confirm. The pack disappears from host lists and downloads are blocked. Its complete folder moves to the host's archive folder rather than being deleted. Members see an archived status, and their local Minecraft files remain. There is no restore button in this version; keep backups and leave archived data in place.

## Update Hearth itself

Open **Settings → Updates → Check for updates**. Hearth uses [official releases](https://github.com/BigUziHert/Hearth/releases) automatically. When a newer version exists, read **What's new**, select **Download update**, then **Restart to update**. A verified download remains available when you navigate away and return. The installer preserves player and host data. Hosting stops while Hearth restarts and resumes with the app if enabled.

Upgrading from the old independent Windows service requires one administrator approval to stop and unregister it. The upgrade keeps its existing host data, releases, members, and owner key. Canceling that prompt cancels the upgrade before the app is replaced; the old service may still be running. Retry the installer and approve the conversion to complete the switch. Fresh hosting and later updates do not need this conversion.

Hearth uses its official release source, including for profiles that previously saved a custom repository. Pack updates still come from each pack's host when you press Play. In **Settings → Minecraft**, **Choose existing Prism** connects an installation you already have. The **Help** section contains **Open data folder** and **Open guide**.

## Stop or move hosting

**Share packs → This PC → Disable hosting** stops serving packs and turns off automatic hosting when Hearth opens. Enabling again reuses the existing host data and your saved owner key. Quitting Hearth stops hosting without forgetting that it should resume next time. Uninstalling stops hosting and preserves data. Back up `%LOCALAPPDATA%\Hearth\Host` while hosting is stopped and retain your owner key before moving to another PC. A host migrated from the Windows service keeps its existing folder, normally `%PROGRAMDATA%\Hearth\Host`; back up that folder instead.

Hearth keeps a backup of its encrypted profile. If the primary profile cannot be read, it tries the backup and reports recovery. If neither can be read, the originals are retained under recovery filenames and Hearth opens an empty profile with their location. Do not delete those files; they may help recover credentials. Windows encryption is tied to the Windows account, so an owner key saved separately remains necessary when moving between accounts.
