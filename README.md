# SmoothFrames

A small, open-source Windows frame limiter with a restrained blue-and-lavender theme inspired by Konata Izumi. Native WPF interface, built-in C++ engine, no RTSS, browser runtime, service, or separate .NET installation.

## Use

1. Extract the release ZIP and open **SmoothFrames.exe**.
2. Start your game and choose it from the list. Use **Refresh** after starting a game.
3. Choose an FPS cap (15–1000), then **Apply cap**. The status changes from **Attached · waiting for frames** to **FPS active** only when the engine sees presentation calls.
4. **Pause** removes the cap and keeps your saved value. Minimize to the notification area to keep limiting; close/Exit clears the cap.

**Browse** lets you save a cap for a game that is not running yet. SmoothFrames remembers caps by full executable path. It does not launch or automatically attach to games. One game can be limited at a time; attaching to another clears the previous cap. Multiple instances of the same game are distinguished by process ID.

Everything required at runtime is embedded in the app, including both x86 and x64 engines. They are extracted to `%LocalAppData%\SmoothFrames\engine` when needed, with a content check before reuse. Settings live in `%LocalAppData%\SmoothFrames\profiles.json`. No downloads or installation prompts occur at runtime.

## Compatibility and measurements

- Windows 10 1809+ or Windows 11 **x64**, with x86/x64 games.
- Built-in hooks for **DXGI Present/Present1** (Direct3D 10/11, and compatible Direct3D 12 paths), **Direct3D 9/9Ex device Present**, and **OpenGL GDI SwapBuffers**.
- **Vulkan, DirectDraw, ARM64, D3D9 additional swapchains, and alternative OpenGL swap entry points are not supported.** DX12 and individual game/overlay combinations still need real-game validation. This is an initial native engine, not a claim of compatibility with every game supported by mature limiters.
- The engine chooses the first active presentation stream and ignores secondary swapchains until that stream has been idle for a second. Multi-window renderers may need further compatibility work.
- Nonblocking/test presents are left untouched. V-sync and game presentation flags are preserved.
- FPS, average and P99 are measured **CPU-side intervals between intercepted presentation calls**, including limiter wait. They are not GPU render duration, display scanout timing, input latency, or proof of perfectly smooth displayed frames.
- The graph shows 250 ms sampled means over up to 45 seconds; P99 uses up to 2048 recent individual intervals. A mean graph can hide single-frame spikes; use P99 alongside it.

Use the engine with games that permit graphics hooks. Anti-cheat or protected processes may reject attachment; SmoothFrames does not bypass protections or automatically elevate. If a game runs as administrator, matching permissions may be necessary. **Attached · waiting for frames** means support has not yet been confirmed for that renderer; it must not be read as an active cap.

The cap resets on clean exit. If the UI hangs or crashes, the engine stops pacing after its heartbeat is stale for 2.5 seconds. Hook code stays resident but inactive until the game exits, avoiding unsafe DLL unloading. A cap cannot make an overloaded game reach its target or fix all sources of stutter. Avoid stacking multiple limiters on the same game.

## Build

Install the .NET 8 SDK, Visual Studio 2022 or newer C++ desktop build tools (Windows SDK and CMake), Git and PowerShell 7. On Windows:

```powershell
pwsh ./scripts/build.ps1
```

This builds and tests both native architectures, embeds their binaries, publishes a self-contained executable, verifies extraction from that executable, and produces `artifacts/SmoothFrames-win-x64.zip`. Native code statically links its C runtime and MinHook, so no separate VC++ runtime installer is needed. MinHook is fetched **at build time only**, pinned to commit `c3fcafdc10146beb5919319d0683e44e3c30d537` (v1.3.4).

The app project deliberately fails a direct build when native artifacts are absent. `-SkipTests` is available for local builds without a graphics-capable desktop; CI and releases always run the tests.

## Validation

Windows CI builds x64 and x86 and runs an actual Direct3D 11 fixture for each: process identity checking, helper injection, shared-memory handshake, 30/60 FPS pacing, pause, stale-heartbeat handling and recovery. It also verifies the engines embedded in the published executable and opens/closes the actual UI, capturing its minimum-size layout. This does not replace testing D3D9, OpenGL, DX12, overlays, fullscreen transitions, or anti-cheat policies in actual games.

## License

SmoothFrames is MIT-licensed. The bundled MinHook component uses the BSD 2-Clause license, included in each package. This independent fan project is not affiliated with RTSS, Valve, or the creators of *Lucky Star*. It contains no official character artwork.
