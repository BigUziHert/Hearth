# Hearth 1.0.12

Fixes a Windows firewall permission check that could prevent secure hosting setup or stop an existing host from resuming after the 1.0.11 update. A missing firewall rule now correctly offers setup; automatic startup keeps local hosting running without opening a Windows prompt. Both Windows representations of an all-network firewall rule are recognized.

Hosting includes secure connection links, automatic router setup attempts, separate Internet and same-network invites, and preserved Minecraft instances when a trusted host address changes. Existing host data, certificates, credentials, and player files are retained.

If hosting was already enabled, open **Hosting → Retry connection setup** and approve the Windows permission request to enable secure sharing. Internet reachability still needs an approved friend connecting from another network; unsupported routers or ISP restrictions are reported clearly.
