# GrannyTAS

A tool-assisted speedrun (TAS) toolkit for **Granny Legacy**, built as a [MelonLoader](https://melonwiki.xyz/) mod.

Frame stepping, speed/tick/physics-rate controls, and deterministic macro record/playback
(a macro recorded at 0.1x speed replays bit-identical at 1x). See [CLAUDE.md](CLAUDE.md) for
the full design writeup, and [docs/](docs/) for subsystem-level docs (timing, code review notes,
IDA findings).

## Requirements

- [MelonLoader v0.7.3](https://melonwiki.xyz/) (Open-Beta, net6 runtime) installed into your
  Granny Legacy install
- .NET SDK 10.0+ (`dotnet build` targets `net6.0`, which MelonLoader 0.7.3 runs IL2CPP mods on)
- A copy of Granny Legacy with MelonLoader already run once, so `MelonLoader/Il2CppAssemblies/`
  exists (the build resolves game/interop assemblies from there)

## Building

```bash
cd GrannyTAS
dotnet build
```

By default the project resolves game assemblies from `%USERPROFILE%\Desktop\Granny_Legacy`.
Point it elsewhere with:

```bash
dotnet build -p:GameDir="D:\path\to\Granny_Legacy"
```

A successful build automatically copies `GrannyTAS.dll` into `<GameDir>\Mods\`. To skip that
(e.g. building on a machine without the game installed), pass `-p:NoDeploy=true`.

## Deploying by hand

If you built elsewhere or used `-p:NoDeploy=true`, copy the output manually:

1. Copy `GrannyTAS/bin/Debug/net6.0/GrannyTAS.dll` (or `Release`, depending on your build) into
   `<Granny Legacy install>\Mods\`.
2. This mod also uses Dear ImGui (ImGui.NET) for its control panel. The managed wrapper needs
   to land in `<Granny Legacy install>\UserLibs\` (not `Mods\` — MelonLoader would try to load it
   as a mod itself) and the native `cimgui.dll` needs to sit in the game's install root next to
   the game executable. `dotnet build` handles this deployment step automatically when
   `NoDeploy` isn't set; check `GrannyTAS.csproj` if you need to replicate it manually.
3. Launch the game. Press **Insert** to open the TAS panel (default keybind, rebindable in-panel).

## Repo layout

| Path | Contents |
|---|---|
| `GrannyTAS/` | The mod source and project file |
| `docs/` | Design docs: timing/determinism, code review, IDA reverse-engineering findings |
| `RE/` | Reverse-engineering notes (input handling, macro setup) |
| `tools/` | Standalone scripts (IDA helpers) |
| `tests/` | Regression tests |

## Status

See the [Status](CLAUDE.md#status) section of `CLAUDE.md` for what's implemented and what's
outstanding.
