# AutoShazam

A WPF desktop app that listens to a selected microphone and identifies the song currently
playing, using Shazam's recognition service.

## Features

- **Auto Shazam** toggle in the title bar (always starts **off**). When enabled it runs an
  immediate recognition, then listens for silence gaps (the kind of pause between tracks on an
  LP, CD, or streamed album) and automatically re-recognizes after each one.
- **Shazam button** in the panel for an on-demand, one-shot recognition.
- Selectable microphone (enumerated via WASAPI).
- A microphone icon in the title bar glows green while the mic is actually capturing.
- Guards against spamming Shazam: only one recognition runs at a time, and auto-triggered
  recognitions respect a cooldown between requests.
- Match results (album art, title, artist) are shown in the panel.
- Window position/size is persisted and restored on the next launch.
- Self-updating via [Velopack](https://velopack.io), with a startup check that prompts before
  downloading/installing an update.
- A locally-run/dev build and an installed release build use **separate** `%LocalAppData%`
  folders (`AutoShazam-Dev` vs `AutoShazam`), so testing never touches your real settings.

## How recognition works

`Services/Shazam` contains a C# port of the reverse-engineered signature algorithm and HTTP
protocol that Shazam's own mobile apps use (there is no public official API for this). It has no
API key and no official support contract — if Shazam changes their protocol, this will need
updating.

## Building

Open `AutoShazam.sln` in Visual Studio 2022+ (or `dotnet build`) with the .NET 8 SDK installed.

## Publishing a release

1. Edit `AutoShazam/Services/Update/UpdateConfig.cs` and set `GithubRepoUrl` to your actual
   GitHub repository URL.
2. Push a tag like `v1.0.1` — `.github/workflows/release.yml` builds, packages with
   [vpk](https://docs.velopack.io), and publishes a GitHub Release with the installer.
3. To test packaging locally first, run `build/Pack-Release.ps1 -Version 1.0.1`.

Installed copies check GitHub Releases for updates on startup and prompt before installing.
