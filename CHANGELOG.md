# Changelog

## v1.8.0

- **Fewer missed matches (hopefully)**: Shazam's recognition endpoint is known to quietly degrade
  (silently returning no match instead of an outright error) traffic that looks automated - a
  fixed User-Agent hit at a metronomic, unvarying interval, which is exactly what Auto Shazam's
  polling looked like. The User-Agent is now rotated per request and the poll interval jittered by
  +/-20%.

## v1.7.0

- **Always on Top**: a toggle next to Auto Shazam in the title bar keeps the window above every
  other window; the setting is remembered between launches.

## v1.6.0

- **Faster re-checks**: Auto Shazam now re-checks every 5 seconds instead of 12, using a rolling
  audio buffer so a check no longer has to wait for a whole fresh clip to record first.
- **Fix**: capturing from a speaker (loopback) no longer gets stuck re-matching the same stale
  clip, or hangs the manual Shazam button forever, once whatever was playing stops - Windows
  suspends a render device's audio engine when it's idle, which silently stalled the capture
  entirely; a silent keep-alive stream now keeps it running.
- **Fix**: lyrics could drift out of sync, since a check served from the rolling buffer had
  actually started a little earlier than the moment it was returned - timing is now based on when
  the audio was really captured.
- **Auto-clear**: the current track (and its lyrics) now clears automatically after enough
  consecutive "no match" results in a row, instead of staying on screen indefinitely - the
  threshold is configurable in Settings (default 2).
- **Settings**: the microphone/speaker checklists moved out of Settings into their own popups
  (handy if you have a lot of devices), and the window now sizes itself to fit its content instead
  of leaving blank space.
- The mini lyrics preview panel now grows open and tints from the surrounding panel's colour to
  its own as the first line arrives, instead of just sitting there blank; its entrance also scrolls
  in as one smooth 2-second motion instead of two separate, back-to-back scrolls.

## v1.5.1

- **Resizable panels**: an invisible grab bar between the artwork and artist/title halves of the
  main panel lets you drag to resize them; the split is persisted and restored on restart.

## v1.5.0

- **Mini lyrics preview**: when synced lyrics are available, a compact 3-line panel (previous/
  current/next) now sits in the main window itself instead of just a "Lyrics" button - the
  upcoming line scrolls into the middle slot shortly before it's due, and stays fully blank until
  the first line is actually about to start rather than sitting there through the whole intro. A
  pop-out icon opens the full Lyrics window as before. The plain-text Lyrics button is unchanged
  for tracks without line timing.
- **Fix**: lyrics timing now resyncs on every Shazam match, not just when the track changes - if a
  song is rewound or seeked, both the mini panel and the Lyrics window pick up the new position
  instead of continuing to play through from wherever it was first matched.

## v1.4.0

- **Speaker capture**: alongside the microphone, AutoShazam can now capture speaker output
  (WASAPI loopback) - a new speaker icon sits next to the microphone one; click either to select
  it, right-click for a device picker (with a checkmark on the current selection). Selection is
  independent of whether Auto Shazam or a manual check happens to be running at the time.
- **Removed silence detection**: Auto Shazam no longer waits for silence gaps between tracks;
  instead it polls on a self-paced interval (never more than once every 5 seconds), backing off
  automatically if Shazam's response signals it's being queried too fast.
- **Settings**: repurposed into a device checklist - mark which microphones/speakers are "offered"
  in the picker - alongside the existing automatic-update-check toggle.
- **Status line**: now reflects what's actually happening (Listening.../Shazaming/Idle/blank)
  instead of a single static message.
- **Result panel**: added a dismiss button to manually clear the current match.
- Rounded the corners of the Lyrics button.
- **Fix**: capturing from a speaker configured for surround sound (5.1/7.1) no longer crashes with
  "Source must be stereo".
- **Fix**: unchecking an offered device in Settings, and the microphone/speaker auto-picked on
  first run, are now actually persisted - previously both could silently revert on restart.

## v1.3.0

- **Synced lyrics**: lyrics lookup now tries LRCLIB first, which can return line-by-line timed
  (LRC) lyrics; when available, the Lyrics panel highlights and auto-scrolls to the current line
  in step with the track, estimated from Shazam's match position rather than needing playback
  control. Falls back to the existing plain-text sources (Musixmatch, then lyrics.ovh) when LRCLIB
  has nothing.
- **Fix**: Auto Shazam re-confirming the same track no longer resets or re-flashes the lyrics
  panel - only an actual track change reloads lyrics and resyncs the highlighted line.

## v1.2.0

- **Lyrics**: after a match, looks up lyrics (Musixmatch, falling back to lyrics.ovh) and caches
  them for the session; a "Lyrics" button appears below the artist/title when found, opening a
  panel that sizes itself to fit the lyrics (capped to the screen) instead of always scrolling.
- **Settings storage**: switched from a flat `settings.json` to a local SQLite database, mainly to
  discourage casually hand-editing the file; an existing `settings.json` is migrated in once, then
  removed.
- **Fix**: window position/size/state and every settings change now save immediately instead of
  only on a graceful close, so a crash, force-kill, or ungraceful shutdown no longer silently
  discards them.

## v1.1.0

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
