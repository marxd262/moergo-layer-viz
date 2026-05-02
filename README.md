# MoergoLayerViz

Desktop overlay that mirrors the active ZMK layer of a [Moergo](https://www.moergo.com/) GO60 or Glove80 keyboard, in real time, on Windows and macOS.

The app reads layout JSON exported from Moergo's online layout editor and renders the keyboard as a small overlay window. When you switch layers on the keyboard, the overlay follows — labels update, the active layer chip lights up, individual keypresses pulse on the board.

It's read-only. No firmware flashing, no keymap editing. The app watches the keyboard via Raw HID (when the firmware reports layer state directly) or via the OS keyboard hook on signal-macro F-keys, and reads JSON exports — that's it.

![MoergoLayerViz overlay following the active layer of a Glove80 keyboard](docs/screenshots/1.png)

## Install & run

Pre-built zips for each tagged release are on the [latest release](../../releases/latest) page. Each release ships:

- `MoergoLayerViz-<version>-windows-x64.zip` — Windows 10/11, x64.
- `MoergoLayerViz-<version>-macos-arm64.zip` — Apple Silicon Macs (M-series).
- `MoergoLayerViz-<version>-macos-x64.zip` — Intel Macs.

If you'd rather build it yourself, skip ahead to [Build from source](#build-from-source).

### Windows

1. Download `MoergoLayerViz-<version>-windows-x64.zip` from [Releases](../../releases/latest) and unzip.
2. Run `MoergoLayerViz.App.exe`.

The first launch may show a SmartScreen warning ("Windows protected your PC") because the binary isn't signed by an MS-trusted publisher. Click **More info → Run anyway**. No special permissions are needed beyond that — Windows allows the global keyboard hook without a user grant.

### macOS

1. Download `MoergoLayerViz-<version>-macos-arm64.zip` (or `-macos-x64.zip` on Intel) from [Releases](../../releases/latest).
2. Extract the zip and move `MoergoLayerViz.app` to `/Applications`.
3. The bundle is ad-hoc signed, so Gatekeeper will block the first launch. Clear the quarantine attribute once:

   ```bash
   xattr -cr /Applications/MoergoLayerViz.app
   ```

4. Open the app. On first launch it shows an **Accessibility required** dialog — click **Open Settings** (or go to **System Settings → Privacy & Security → Accessibility**) and enable **Moergo Layer Viz**. Quit and re-launch.

> macOS 26+ doesn't always persist the Accessibility grant across system upgrades for ad-hoc-signed apps. If the overlay stops following layers after a major OS update, remove the stale entry under Accessibility and grant it again. For a setup where the grant survives upgrades, build from source with a self-signed Keychain cert — see [macOS development build](#macos-development-build).

---

## Screenshots

| | |
| :---: | :---: |
| ![GO60 in default side-by-side layout](docs/screenshots/3.png) | ![Glove80 in stacked layout](docs/screenshots/2.png) |
| GO60 (60 keys) — side-by-side default | Glove80 in stacked layout (toggle in toolbar) |
| ![Keyboard picker dropdown](docs/screenshots/7.png) | ![Signal-macro generator dialog](docs/screenshots/4.png) |
| Keyboard picker (GO60 / Glove80, auto-switches on JSON load) | Signal-macro generator (`&Tolayer` / `&Molayer` / `&ht_*`) |
| ![Settings, General tab](docs/screenshots/5.png) | ![Settings, Layers tab](docs/screenshots/6.png) |
| Settings — General (opacity, press color, hotkey, layout) | Settings — Layers (per-layer color + signal-key picker) |

The toolbar and layer-tabs strip flip to whichever edge faces the screen edge as you drag the window: they sit **on top** when the window is in the top half of the screen, and **at the bottom** when it's in the bottom half. This keeps the controls between the overlay and the nearer screen edge so they don't get in the way of whatever you're looking at behind it.

---

## Why this exists — two ways to observe layer state

ZMK's layer-switch behaviors (`&mo`, `&to`, `&tog`, `&lt`, `&sl`) execute entirely on the keyboard's microcontroller. The host OS never sees them. So a plain desktop app can't observe layer changes via the normal input event stream — there's nothing to observe.

The app supports two ways around this. It picks one automatically; you can override in **Settings → General → Layer source**.

### 1. Raw HID (preferred)

When the firmware ships an extra Raw HID endpoint that *reports* layer state and key positions to the host, the app reads those reports directly. No signal macros, no host-side keycode lookup, no untrackable layer switches — `&magic`, bare `&mo`, combos, tap-dance all show up correctly. This is not how the **Go60 stock firmware** works today. If you want this you need to build the firmware yourself! See [Firmware support](#firmware-support) for what the protocol looks like and how to add it to other boards.

### 2. Signal macros (fallback)

The standard ZMK workaround when no HID feedback is available: a **signal macro** that wraps the layer switch alongside an OS-visible keycode tap, conventionally an F-key in the F13–F24 range (those are reserved by the OS and rarely consumed by apps). The app listens for the F-key via the OS keyboard hook and infers the layer.

This is the only path on **stock Glove80** firmware today, and the only path that works without a HID-capable build.

The canonical signal-macro shape:

```
&macro_press               → push layer N
                           → emit Fkey
&macro_pause_for_release
&macro_release             → pop layer
```

When the keyboard fires this macro, the host receives an F-key down/up. MoergoLayerViz listens for that F-key, looks up which layer it signals, and updates the overlay.

### Two macro shapes the app understands

The scanner is purely structural — it doesn't care about names. See [docs/parsing.md](docs/parsing.md) for the full reference.

- **Routed (parametric).** One macro definition serves every layer; the layer index and F-key are passed as parameters via `&macro_param_1to1` / `&macro_param_2to1`. Conventionally named `&Tolayer <layer> <Fkey>` (fire-once, for `&to` / `&tog`) and `&Molayer <layer> <Fkey>` (momentary, for `&mo`).
- **Literal.** One fixed macro per layer, layer index and F-key baked in (e.g. `&mo_symbol_f16signal`). Useful when you want each layer to have its own dedicated entry.

### Momentary vs fire-once

Determined structurally by the presence of `&macro_pause_for_release` in the press phase:

- **Momentary** (with `&macro_pause_for_release`) — the layer is active only while the physical key is held. Mirrors `&mo` semantics. The runtime tracker pushes/pops a held-layer stack.
- **Fire-once** — the layer flips and stays. Mirrors `&to` / `&tog` semantics.

### Untrackable layer switches

Bare `&mo` / `&magic` / unwrapped layer-switch bindings emit nothing the host can see in **signal-macro mode**. The app paints these keys with a pink border so you can spot them at a glance. On stock Glove80 firmware, `&magic` is the most common offender. The pink border is hidden automatically when the app is running over Raw HID — the firmware reports those switches directly.

---

## Firmware support

This section is for users (or board maintainers) who want the Raw HID path to work on a board that doesn't ship with it yet. The app itself stays read-only and neutral — it never flashes anything; the firmware change has to come from your own ZMK build pipeline.

### Where it works today

| Board | Stock firmware | Custom firmware |
| --- | --- | --- |
| **Moergo GO60** | Signal macros only | Raw HID possible, see below |
| **Moergo Glove80** | Signal macros only | Raw HID possible, see below |

The app auto-detects which path is available and falls back gracefully. HID is worth setting up if you want `&magic` (and other unwrapped switches) reflected in the overlay.

### What the app expects from the firmware

The app talks to a **Raw HID interface** with these properties — anything that satisfies the contract works, regardless of which ZMK module produced it:

- **Usage page** `0xFF60`, **usage** `0x61` — non-standard "vendor-defined" range so it doesn't collide with the keyboard interface and doesn't trigger Input Monitoring / TCC prompts on macOS. The same endpoint convention used by [QMK's Raw HID](https://docs.qmk.fm/#/feature_rawhid).
- **32-byte input reports**, with byte 0 as the message type:
  - `0xFF` — **layer state**. Bytes 6–7 carry a little-endian 16-bit bitmask of currently-active layers. The app collapses to "highest set bit" for the single-layer overlay.
  - `0xF1` — **key event**. Byte 2 is the matrix position (matches the binding index in the layout JSON), byte 3 is `0x01` for press / `0x00` for release.
- Reports must be sent on every layer change *and* on every key press/release so the overlay can highlight the physical key being pressed regardless of what binding it resolves to.

The pure parser lives in [src/MoergoLayerViz.Core/Input/RawHidProtocol.cs](src/MoergoLayerViz.Core/Input/RawHidProtocol.cs) — that file is the canonical reference if anything in this section is ambiguous.

### Adding it to a ZMK board

My Go60/Glove80 ZMK builds use two community modules to satisfy the contract:

- [zzeneg/zmk-raw-hid](https://github.com/zzeneg/zmk-raw-hid) — exposes the Raw HID endpoint over USB and BLE.
- [srwi/zmk-keypeek-layer-notifier](https://github.com/srwi/zmk-keypeek-layer-notifier) — emits the `0xFF` layer-state and `0xF1` key-event reports the app reads.

A complete, working `west` manifest pulling them in (this is the actual Go60 setup the app is developed against): [ovandongen/go60-zmk-config-west](https://github.com/ovandongen/go60-zmk-config-west). The relevant pieces are:

- `config/west.yml` — adds the two modules as projects on top of the `moergo-sc/zmk` base.
- `config/<shield>.conf` — `CONFIG_RAW_HID=y` to enable the endpoint at build time.

Build, flash, plug in. The app picks the board up automatically — matching is by HID usage page / usage, with the product-name string only used to scope to the currently-selected keyboard profile.

### Glove80

The Glove80 setup uses the same two modules: [ovandongen/glove80-zmk-config-west](https://github.com/ovandongen/glove80-zmk-config-west). Build and flash the same way as the Go60 — `config/west.yml` pulls in `zzeneg/zmk-raw-hid` + `srwi/zmk-keypeek-layer-notifier`, and `CONFIG_RAW_HID=y` in the shield config enables the endpoint. The app picks up either board automatically.

---

## Quick start

### 1. Add signal macros to your keymap

You can either let the app generate them for you, or hand-write them in Moergo's editor.

#### Option A — Built-in generator (recommended)

1. In Moergo's online layout editor, export your layout as JSON.
2. Open MoergoLayerViz and load that JSON.
3. Toolbar → **Generate signal macros & hold-taps**.
4. Save the modified JSON.
5. Re-import the modified JSON into Moergo's editor. Bind the generated `&Tolayer` / `&Molayer` / `&ht_<layer>` entries to physical keys, then flash.

What the generator produces (see [SignalMacroGenerator.cs](src/MoergoLayerViz.Core/Tooling/SignalMacroGenerator.cs)):

- One parametric `&Tolayer` macro (fire-once layer switch with F-key signal).
- One parametric `&Molayer` macro (momentary layer switch with F-key signal).
- One fixed `&mo_<layer>_f<NN>signal` macro per layer, base layer included.
- One `&ht_<layer>` hold-tap per layer (280 ms tappingTermMs, balanced flavor, 175 ms quickTapMs, 150 ms requirePriorIdleMs, holdTriggerOnRelease=true). Drop these on the keys you want to use for momentary layer switching on hold.

F-keys are assigned starting from F13, one per layer. Range is F13–F24, so up to 12 layers; layers beyond that produce a warning and are skipped. The generator is idempotent — re-running it on an already-generated layout produces no diff.

#### Option B — Hand-write the macros

Author the macros directly in Moergo's editor following one of the shapes above. [docs/parsing.md](docs/parsing.md) has the full reference for what the scanner accepts (phase markers, arity resolution, hold-tap aliases, etc.).

### 2. Load and run

- Load the JSON in MoergoLayerViz. The app auto-detects whether the layout is GO60 or Glove80 by binding count and switches profile automatically.
- In **Settings**, enable **Live highlighting** and **Auto layer switch** to start tracking.
- Default global show/hide hotkey: **F12** (configurable in Settings → General).

## Build from source

### Prerequisites

- **.NET 10 SDK** (target framework `net10.0`).
- macOS 11+ for the macOS bundle, Windows 10+ for the Windows build.

### Common commands

```bash
dotnet build                                       # full solution build
dotnet test                                        # run all xUnit tests (218)
dotnet run --project src/MoergoLayerViz.App        # launch (Windows only — see macOS dev notes below)
scripts/build-mac-app.sh                           # build signed .app bundle (macOS)
```

### macOS development build

For day-to-day development on macOS you'll want the bundle signed by a self-signed Keychain cert so the Accessibility grant persists across rebuilds and OS upgrades. Ad-hoc signing — what `scripts/build-mac-app.sh` falls back to without a cert, and what the published release zips use — does not give TCC a durable identity on macOS 26+.

#### 1. Create a self-signed code-signing certificate (one-time)

1. Open **Keychain Access** (Applications → Utilities).
2. Menu: **Keychain Access → Certificate Assistant → Create a Certificate**.
3. Set:
   - **Name:** `MoergoLayerViz Local Dev` (must match exactly — the build script looks it up by this name).
   - **Identity Type:** Self Signed Root.
   - **Certificate Type:** Code Signing.
4. Click **Create**. The cert appears under **My Certificates**. It does not need to be system-trusted.

#### 2. Build the bundled `.app`

```bash
scripts/build-mac-app.sh
```

What this does (see [scripts/build-mac-app.sh](scripts/build-mac-app.sh)):

- `dotnet publish` self-contained for the host RID (osx-arm64 or osx-x64).
- Assembles a proper `.app` at `src/MoergoLayerViz.App/bin/macos-bundle/MoergoLayerViz.app`, with `Contents/MacOS`, `Contents/Resources`, and [Info.plist](build/macos/Info.plist) (`CFBundleIdentifier dev.moergolayerviz.local`).
- Generates `AppIcon.icns` from a PNG via `sips`.
- Codesigns with the `MoergoLayerViz Local Dev` cert (or ad-hoc fallback). It does **not** pass `--options runtime` — hardened runtime's Library Validation blocks `libhostfxr.dylib` for ad-hoc / self-signed dylibs.

#### 3. Launch

```bash
open src/MoergoLayerViz.App/bin/macos-bundle/MoergoLayerViz.app
```

To pass environment variables (e.g. log level), use `open --env`:

```bash
open --env MOERGO_LOG_LEVEL=DEBUG src/MoergoLayerViz.App/bin/macos-bundle/MoergoLayerViz.app
```

Plain `open` does **not** propagate shell environment variables.

On first launch, grant Accessibility as described in the [macOS install section](#macos). If you've previously run the app under a different identity (for example via `dotnet run`, or from an earlier ad-hoc build), remove any stale entries in the Accessibility list before re-launching.

#### 4. Don't use `dotnet run` on macOS

`dotnet run` launches without a stable bundle identity, so SharpHook can't get a persistent Accessibility grant and the global hook fails silently. Always use the `.app` bundle produced by `scripts/build-mac-app.sh`.

---

## Project layout

```
MoergoLayerViz.sln
src/
  MoergoLayerViz.Core/    Pure .NET — JSON loader, signal-macro scanner,
                          layer-signal table, hotkey layer tracker, keyboard
                          profiles, settings, diagnostics. No Avalonia refs.
  MoergoLayerViz.App/     Avalonia 11 UI, MVVM (CommunityToolkit.Mvvm),
                          SharpHook adapter, custom chrome, EN/NL localization.
  MoergoLayerViz.Tests/   xUnit fixtures (218 tests). Includes Go60.json +
                          Glove80.json layouts copied to test output.
build/macos/Info.plist    Bundle plist (CFBundleIdentifier dev.moergolayerviz.local).
scripts/build-mac-app.sh  macOS build + codesign pipeline.
docs/parsing.md           Canonical signal-macro / hold-tap / layer-signal
                          parsing reference.
```

**Tech stack:** Avalonia 11.3 / .NET 10 / CommunityToolkit.Mvvm 8 / SharpHook 7 / Projektanker.Icons.Avalonia 9 (FontAwesome).

**User settings** are persisted at:

- macOS: `~/Library/Application Support/MoergoLayerViz/settings.json`
- Windows: `%APPDATA%\MoergoLayerViz\settings.json`

**Diagnostic log** at `<settings dir>/log.txt`, auto-rotates at 2 MB.

### Key source files

- [docs/parsing.md](docs/parsing.md) — full signal-macro / hold-tap reference.
- [src/MoergoLayerViz.Core/Keymap/SignalMacroScanner.cs](src/MoergoLayerViz.Core/Keymap/SignalMacroScanner.cs) — scanner.
- [src/MoergoLayerViz.Core/Tooling/SignalMacroGenerator.cs](src/MoergoLayerViz.Core/Tooling/SignalMacroGenerator.cs) — generator (F13–F24 range, naming convention).
- [src/MoergoLayerViz.Core/Input/HotkeyLayerTracker.cs](src/MoergoLayerViz.Core/Input/HotkeyLayerTracker.cs) — runtime tracker.
- [scripts/build-mac-app.sh](scripts/build-mac-app.sh) — macOS build pipeline.
- [build/macos/Info.plist](build/macos/Info.plist) — bundle plist.

---

## Limitations

- **Read-only.** No firmware flashing, no keymap editing. The app watches the keyboard and reads JSON; nothing more.
- **F-key ceiling.** F13–F24 = 12 trackable layers per board. Modifier-wrapped F-keys (e.g. `&kp LC(LS(F16))`) are a possible future expansion noted in [docs/parsing.md](docs/parsing.md), but not currently supported.
- **Untrackable behaviors.** Bare `&mo`, `&magic`, and any unwrapped layer-switch bindings remain invisible to the host. The app flags them with a pink border but cannot follow them.
- **No Linux build.** Wayland blocks process-global key capture by design, so the signal-macro fallback can't run there, and shipping `/dev/hidraw` access is more surface than this solo project wants to support. The code still compiles on Linux for development, but no binary is published.
- **No multi-layer view, no in-app EN/NL toggle UI** (resources exist; toggle UI deferred), **no screen-reader / keyboard-nav support**.

---

## Credits

- [Moergo](https://www.moergo.com/) for the GO60 and Glove80 keyboards.
- [SharpHook](https://github.com/TolikPylypchuk/SharpHook) and [libuiohook](https://github.com/kwhat/libuiohook) for the global keyboard hook.

---

## License

MIT — see [LICENSE](LICENSE). Free to use, modify, and redistribute, including commercially. Just keep the copyright notice.
