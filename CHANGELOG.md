# Changelog

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
