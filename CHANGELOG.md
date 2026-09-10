# Changelog

## Unreleased

- **Title bar menu**: the icon and "Auto Shazam" title now open a File / About menu, replacing the
  gear-icon Settings button and standalone About access; File contains Settings, Check for Update,
  and Exit.
- **About dialog**: new dialog showing app icon, version, and release date (the actual GitHub
  release date, not local install date) — or "Local development build" for a dev run.
- **Settings**: added an "Automatically check for updates on startup" toggle; fixed the Done
  button's label not being centered.
- **Update checks**: dialogs (Check for Update, update-available prompt) now use a custom
  themed dialog instead of the OS message box, fixing a bug where it could open invisibly; About,
  Settings, and these dialogs no longer show their own separate taskbar entry.

## v1.0.0

Initial release.

- **Recognition**: manual "Shazam" button for one-shot recognition, and an "Auto Shazam" mode
  (title bar toggle, off by default) that runs an initial check, then listens for silence gaps
  between tracks and re-recognizes automatically. Talks to Shazam's own recognition endpoint via
  a from-scratch C# port of its audio fingerprinting algorithm.
- **Auto Shazam safeguards**: only one recognition runs at a time; auto-triggered attempts respect
  a cooldown (shorter after a miss, longer after a match); a periodic fallback timer catches
  sources with no clean silence between tracks (streaming, DJ mixes); Auto Shazam switches itself
  off after a configurable stretch of continuous silence.
- **Live feedback**: title bar icons show microphone activity (green), input level relative to the
  silence threshold (purple), and whether a request to Shazam is currently in flight (blue).
- **Match display**: album art, artist, and title shown in the main panel; the last match stays on
  screen until a new one replaces it.
- **Settings**: microphone selection, extended-silence timeout, silence threshold (dBFS), and
  sound-icon debounce, all persisted and explained in plain language in the Settings dialog.
- **Window state**: position and size persisted between launches.
- **Self-updating**: checks GitHub Releases on startup and prompts before downloading, via
  Velopack. A locally-run build and an installed release use separate `%LocalAppData%` folders so
  development never touches real user data.
- **Diagnostics**: a rolling `recognition.log` in the app's data folder records what each attempt
  captured and what Shazam returned, to help diagnose recognition misses after the fact.
