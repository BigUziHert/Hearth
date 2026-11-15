# Reliability and sharing changes in 1.1.0

This maps the requested fixes to the implemented behavior. Detailed test evidence and release verification are recorded in [VERIFICATION.md](VERIFICATION.md).

| Request | Implemented behavior |
|---|---|
| 1 | Recovery credentials are committed only after the host accepts them. A wrong saved key can be replaced from This PC; rejection retains the previous profile and explains the mismatch. |
| 2 | Install, verification, import, migration, and native Windows process queries run outside the WPF dispatcher. Progress identifies verification and application phases; cancellation remains supported. |
| 3 | Application exception handlers log failures and retain the running app for recoverable errors. Clipboard writes share a bounded retry helper. Draft text saves report failures safely. |
| 4 | Profile writes use WriteThrough, disk flush, and atomic replacement with an encrypted backup. Loading tries the backup, preserves unreadable originals, and reports recovery paths. Locked originals remain in place. |
| 5 | Host readiness and window activation refresh library status. Locally stopped hosting has a specific message. |
| 6 | Network notifications, retry backoff, and failed lease renewals trigger recovery. Permission cancellation does not stop a running host. |
| 7 | An omitted member role retains the existing role. |
| 8 | Required mod IDs satisfy optional dependencies. Core shares duplicate-ID validation between owner preflight and host; conflicts explain which Required copy to retain. Missing compatible catalog versions have explicit errors. |
| 9 | Clean drafts follow the latest release. Dirty stale drafts show Discard my changes and Keep editing. Keeping changes confirms a merge onto the latest baseline and identifies conflicts. |
| 10 | Pack settings edits publish the name, Minecraft version, loader, and loader version. The editor explains the matching world-server requirement. |
| 11 | Only the host administrator can archive. Pack data moves to an archive folder; members receive an archived status. |
| 12 | Available releases show What's new in the library. |
| 13 | An unchanged published draft cannot create another release. Changes include files, notes, and pack settings. |
| 14 | Hearth clipboard data preserves groups; editor search resets between packs; optional expander/group state survives rendering; draft storage uses host identity and pack ID with legacy fallback. |
| 15 | Owners can reset the shared invite. Remove/decline dialogs also offer a reset while retaining other approved members. |
| 16 | Personal invites have labels, optional expiry, optional single use, listing, and revocation. Shared invites can be disabled; older join-code clients remain supported. |
| 17 | Invite to another pack creates a labelled personal invite. People groups requests by display name and invite label for organization, not authentication. |
| 18 | Per-pack pending counts refresh about every minute in owner lists and the sidebar. Rows show request time/label; Decline and Decline all have distinct wording. |
| 19 | Pending library access refreshes about every 30 seconds and on activation. The fingerprint is prominent and copyable. Removed membership has a specific message. |
| 20 | Unpublished packs explain Publish first and do not offer player invite actions. |
| 21 | The local host writes advertised addresses and certificate identity to its data folder. An administrator-only endpoint lets remote managers obtain Internet and home-network invites. |
| 22 | Use my own address supports an existing DNS, DDNS, or VPN address and port. Certificate pin validation is independent of the DNS name. It does not provision the network or domain. |
| 23 | A verified fresh invite migrates every connection and joined pack with the same host identity and pin, preserving installation URLs. |
| 24 | Installed records store host identity and pin. Rejoining can rediscover the matching instance; library removal remembers its location. Existing URL-based folders continue working. |
| 25 | Router mapping tries a bounded external-port range and advertises the assigned port. Cleanup affects only its own mapping. |
| 26 | Connected hosts use the host name/address by default and support Rename. |
| 27 | Delegated packs share one host group while retaining separate pack credentials. Legacy fake entries migrate; member credentials are not offered as owner keys. |
| 28 | API connections and metadata operations have bounded timeouts and friendly host errors. Large transfers retain longer limits and cancellation. |
| 29 | A stopped local host receives no library owner-credential request. Editor and owner queries also guard this state. |
| 30 | Share packs combines hosting and management. Add modpack, Share packs, and Settings stay outside the scrolling pack list. Current UI and guide terminology is aligned. |
| 31 | New owner packs appear in the library automatically. Open in my library is visible in the editor. |
| 32 | Duplicate primary actions and dead strips/fields are removed. The profile name has one editor in Settings. Owner lists refresh automatically, and primary labels match their actions. |
| 33 | Startup hosting and pack downloads leave navigation available. Conflicting page actions are disabled until completion. |
| 34 | Host management links explain their purpose; pasting a link into the wrong form names the correct destination. |
| 35 | Closing while hosting, without the close-to-tray preference, offers Keep hosting in the tray or Quit and stop hosting. |

## Limits and manual checks

- Test mod JARs verify distribution and state preservation, not real mod compatibility. Sign in to Prism with a Microsoft account and run the intended Minecraft version and mods before inviting a group.
- Test HTTPS connections verify certificate pins, DNS names, and access boundaries. They do not prove that a friend outside the owner's actual network can connect. Follow the first-run guide's external connection check.
- Old installed records without host identity cannot establish a previously forgotten host's identity from a different address alone. Existing entries retain their original instance URL; future verified installs and removals record the stable identity for rediscovery.
- Archive retains data but does not yet have an in-app restore action. Profile backups use the current Windows account's encryption; keep the owner key separately when moving accounts.
