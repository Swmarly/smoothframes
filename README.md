# SmoothFrames

**An open-source, Konata Izumi-themed frame limiter control panel for Windows.**

SmoothFrames gives you a small, friendly way to set per-game FPS caps through RivaTuner Statistics Server (RTSS). Choose a running game or browse to its `.exe`, select a cap, and apply it while the game is open.

> SmoothFrames controls RTSS. RTSS is the component that applies the limit to games, so RTSS must be installed and running. SmoothFrames does not claim to replace RTSS's limiter engine.

## What it does

- Finds games with visible windows and lets you choose an executable manually.
- Applies or clears an RTSS per-application `FramerateLimit` profile value.
- Asks RTSS to reload active profiles after a change.
- Shows the target frame interval (`1000 ÷ FPS`) as a pacing guide. This is not live game telemetry.
- Uses an original blue-haired, starry interface theme inspired by Konata Izumi and *Lucky Star*.

## Requirements

- Windows 10 or 11, 64-bit
- RivaTuner Statistics Server (RTSS), installed and running
- SmoothFrames build for Windows x64

SmoothFrames uses the RTSS profile interface exposed by `RTSSHooks64.dll`; it does not redistribute RTSS files. If RTSS runs with elevated permissions and SmoothFrames cannot save a profile, run SmoothFrames with the same permissions.

## Build

Install the .NET 8 SDK, then run:

```powershell
dotnet build .\src\SmoothFrames\SmoothFrames.csproj -c Release
```

To make a self-contained x64 publish:

```powershell
dotnet publish .\src\SmoothFrames\SmoothFrames.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\publish
```

Tagged releases are built and attached automatically by GitHub Actions.

## Limits and safety

A frame cap can help reduce unnecessary rendering and make pacing more consistent when the game can sustain the chosen target. It cannot guarantee perfectly flat frametimes, lower latency, or better performance in every game. Results depend on the game, graphics API, GPU load, sync settings, and RTSS compatibility.

SmoothFrames itself does not inject code into a game. RTSS uses its own integration to apply limits; check the game's anti-cheat rules before using RTSS with protected online games. SmoothFrames does not bypass anti-cheat protections.

## Roadmap

- Live frametime capture and graphing
- Saved named profiles and per-game quick presets
- Optional tray controls and global hotkeys
- More themes and localization

The first version keeps the scope focused on reliable per-game RTSS profile control.

## License and naming

The SmoothFrames source is available under the [MIT License](LICENSE).

SmoothFrames is an independent fan project and is not affiliated with RTSS, Valve, or the creators of *Lucky Star*. Konata Izumi and *Lucky Star* are the property of their respective rights holders. The interface uses an original theme and contains no official character artwork.
