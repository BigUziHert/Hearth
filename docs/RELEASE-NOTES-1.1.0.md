# Hearth 1.1.0

This release improves recovery, sharing, and everyday pack management. Existing packs, installed worlds, settings, and ordinary invite links remain supported.

- **One owner page:** Share packs combines this PC's hosting controls, connected hosts, packs, and a People view. Navigation stays visible while the pack list scrolls. Owners' packs appear in their library automatically.
- **Safer recovery:** Wrong recovery keys are never saved before verification. Damaged encrypted profiles fall back to a durable backup, and unreadable files are preserved. Clipboard contention reports a retry message instead of closing the app.
- **Responsive installs:** Hashing, imports, verification, file migration, and process checks run outside the UI thread. Downloads allow browsing other pages and report verification and installation phases.
- **Reliable hosting:** Network changes and failed router leases retry automatically. Canceling a Windows permission request preserves an already running host. Multiple hosting PCs can use different mapped ports. Owners can supply their own DNS or VPN address, and remote administrators receive the host's advertised addresses.
- **Better publishing:** Edit pack settings, search imported mods, preserve optional groups when copying, and resolve stale drafts visibly. Clean drafts follow the latest release automatically. Required mod dependencies take precedence over optional duplicates; duplicate mod IDs are checked before uploads. Unchanged drafts cannot publish a redundant release.
- **Clearer access management:** Reset shared invites, turn them off, or create named personal invites with expiry and single-use options. Requests show the invite label, time, and fingerprint. Owners can decline spam requests, invite a member to another pack, or archive a pack while retaining its files.
- **Player feedback:** Approval refreshes automatically; new requests show a copyable fingerprint. Updates show release notes. Removed members, archived packs, and locally disabled hosting have distinct messages.
- **Stable installations:** Verified host address changes update all matching connections while preserving instance locations. Rejoining a pack can rediscover its existing installation and personal files.
- **Closing:** When hosting is running and close-to-tray is off, closing asks whether to keep hosting in the tray or quit and stop hosting.

Hosting still depends on the owner's network accepting incoming connections. Automatic router configuration cannot bypass carrier-grade NAT or a router that disallows mappings. Microsoft account sign-in and real Minecraft/mod compatibility remain the player's and pack owner's setup steps. See the first-run guide for the external connectivity check.
