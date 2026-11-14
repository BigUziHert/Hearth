# Hearth 1.0.9

- Hosting status now notices an unexpected host exit immediately on the Hosting page and offers Retry. Closing the app during an operation cancels safely and stops hosting when close to tray is off.
- An old or unreadable hosting configuration no longer prevents players from opening Hearth. Hosting errors stay separate from joining and playing.
- Opening your own pack in the library uses your owner access even if you previously requested access as a player; terrain choices and the existing instance are preserved.
- Publishing detects stale drafts before uploading and handles publication races with a clear reload path. Discarding a draft waits until the latest release is available.
- Loading version lists no longer clears a loader version entered while the Minecraft list is loading.
- Play installed verifies managed mod files and Prism version metadata before launch, and gives specific recovery guidance for damaged files or local records.
- Running Minecraft and Prism are recognized with either Windows path separator without blocking unrelated servers or similarly named profiles.
- Incomplete invitations give clear correction instructions. Prism onboarding explains signing in and closing Prism before returning to Play.

The app still uses the same pages and layout, official GitHub updates, and app/tray hosting lifetime.
