# Hearth 1.0.11

Hosting now creates secure connections directly inside Hearth. Enable hosting, approve the Windows firewall permission, and Hearth generates home-network links and attempts automatic Internet setup through the router. Owners no longer need to install or configure a tunnel, reverse proxy, domain, or certificate.

- Copy a host connection link and separate owner key to manage packs from another PC. Friends still join with a private pack invite and individual approval.
- Share separate Internet and home-network invites. Router configuration and actual outside reachability are described separately.
- Connections verify the exact host certificate before sending credentials or downloading files. A changed identity cannot silently replace a trusted connection.
- Reconnecting through a fresh address for the same verified host retains the existing Minecraft instance, access approval, and terrain preference.
- Canceling Windows permission leaves hosting disabled; retry reuses the saved identity and key. Startup never opens an unexpected administrator prompt.
- Hosting and router renewal stop when Hearth exits completely. Close to tray keeps them running.

Automatic Internet setup depends on the router and Internet provider accepting incoming connections. Unsupported networks remain usable on the home network and show a clear explanation. This release does not include a relay service or automatic DNS; when the public IP changes, copy a fresh link. See the installed first-run guide for the outside-network test.
