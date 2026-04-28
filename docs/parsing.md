# Macro & hold-tap parsing — MoergoLayerViz

How the viewer extracts layer-switch information from a Moergo JSON export.
Detection is **purely structural** — no naming convention is required from
the user. `&mo_<layer>_f<n>signal` / `&ht_<layer>` style names are cosmetic.

## Why parsing exists at all

ZMK layer-switch behaviors (`&mo`, `&to`, `&tog`, `&sl`, `&lt`) execute
entirely on the keyboard's microcontroller — the host OS sees nothing. To
mirror layer state in a desktop overlay, the keymap has to *also* emit an
OS-visible keycode whenever a layer changes. The standard idiom is a
"signal macro": a ZMK macro that runs `&mo` (or `&to`, `&tog`) alongside a
`&kp <signal-keycode>` (typically F13–F24, since they're reserved and
type nothing in apps).

The viewer's job is to find these macros in the JSON and build a
`keycode → layer` table the runtime tracker can consume.

## Signal-macro shapes

A macro qualifies as a signal macro when one **phase** of its body
contains both a layer source and a keycode source.

A *phase* begins at `&macro_press` or `&macro_tap` and ends at the next
phase marker: `&macro_press`, `&macro_tap`, `&macro_pause_for_release`,
or `&macro_release`. Multi-phase macros are scanned phase-by-phase; the
first phase that yields a complete (layer, keycode) pair classifies the
macro.

| Source                | Routed style                                                                       | Literal style                |
| --------------------- | ---------------------------------------------------------------------------------- | ---------------------------- |
| Target layer          | `&macro_param_NtoX` followed by `&mo` consuming the macro's param `N`              | `&mo <int>` directly         |
| Signal keycode        | `&macro_param_NtoX` followed by `&kp` consuming the macro's param `N`              | `&kp <KEYCODE>` directly     |
| Hold-to-release       | Body contains `&macro_pause_for_release` → `IsMomentary = true`; absent → `false`  | (same)                       |

Routed and literal sources can be mixed within one macro (rare but valid).
The `SignalMacro` record carries both `LayerParamIndex` / `KeyParamIndex`
(routed) and `LiteralLayerIndex` / `LiteralKeycode` (literal); resolution
helpers `TryResolveTargetLayer` / `TryResolveSignalKeycode` prefer the
routed source when set, fall back to literal otherwise.

### `&macro_press` vs `&macro_tap`

| Phase marker        | Semantics                                  | Typical use                                     |
| ------------------- | ------------------------------------------ | ----------------------------------------------- |
| `&macro_press` + `&macro_pause_for_release` | Hold-to-activate (momentary)         | `&mo` recipes — layer is held while key is held |
| `&macro_tap`                          | Fire-once on press                         | `&to` / `&tog` recipes — set-and-stay       |

The runtime tracker uses `IsMomentary` to decide whether to push/pop a
held-layer stack (true) or just flip the active layer (false).

## Hold-tap parsing

Top-level `holdTaps[]` array. Each entry has:
- `name` — behavior name including `&` (e.g. `&ht_symbol`, `&HRM_left_hand_v1_TKZ`).
- `bindings: [hold, tap]` — two strings naming the behaviors fired on
  hold and tap respectively. **Note:** these are plain strings, not the
  nested `{value, params}` objects used in macros.
- Other fields (`tappingTermMs`, `flavor`, etc.) are ignored — the viewer
  only needs the wiring.

### Arity resolution

To know how to split keymap-binding params between the hold and tap sides,
the loader resolves each side's **arity** (param count) using:

| Behavior name                                       | Arity |
| --------------------------------------------------- | ----- |
| `&kp`, `&mo`, `&to`, `&tog`, `&sl`                  | 1     |
| `&lt`                                               | 2     |
| `&trans`, `&none`, `&bootloader`, `&sys_reset`      | 0     |
| User macro (looked up in `macros[]`)                | declared param count |
| Anything else                                       | 0 (safe default — params route to the other side rather than dropped) |

Examples:

| Hold-tap                         | Bindings              | Hold arity | Tap arity | `&ht_x A B` splits to |
| -------------------------------- | --------------------- | ---------- | --------- | --------------------- |
| `&ht_symbol`                     | `[&mo_symbol_f16signal, &kp]` | 0          | 1         | hold=[], tap=[A]      |
| `&HRM_left_hand_v1_TKZ`          | `[&kp, &kp]`          | 1          | 1         | hold=[A], tap=[B]     |
| `&mt` (standard ZMK)             | `[&kp, &kp]`          | 1          | 1         | hold=[A], tap=[B]     |

The leading `holdArity` params go to the hold side; the next `tapArity`
go to the tap side. Missing params (malformed entries) are silently
truncated.

## Layer-signal table

Built by `LayerSignalTable.Build(config, signalMacros)`. The table maps
`OS keycode → (target layer, IsMomentary, source-macro name)`.

Construction:
1. `BuildSignalLookup` produces a `name → SignalMacro` map.
   - Direct entry per detected signal macro.
   - **Hold-tap alias**: for any hold-tap whose `HoldBinding` resolves to
     a signal macro, register the hold-tap's own name as an alias for that
     signal macro. Layer bindings that reference `&ht_symbol` then
     transparently resolve as if they referenced `&mo_symbol_f16signal`.
2. Walk every layer binding. For each binding whose behavior is in the
   lookup, resolve `(targetLayer, signalKeycode)` via the helper methods
   and record the mapping. Last writer wins on conflicts.

The resulting table is consumed by `HotkeyLayerTracker`: when the OS
fires a keypress matching a known signal keycode, the tracker activates
the mapped layer.

## Untrackable layer switches

`SignalMacroScanner.FindUntrackableLayerSwitches` flags layer bindings
the viewer cannot follow:

- Bare `&mo`, `&to`, `&tog`, `&sl`, `&lt` (no signal keycode emitted).
- Hold-taps whose hold side is one of the above (no signal keycode).

Hold-taps whose hold side **is** a signal macro are explicitly excluded
from the warning list.

## Rendering — what the user sees

Hold-tap keys (when a `HoldTap` record is in scope):
- **Label** = tap-side rendered glyph. Synthesized by recursing
  `FormatBinding` on `KeyBinding(holdTap.TapBinding, tapParams)`. So
  `&kp RET` → `⏎`, `&kp LS(LBKT)` → `{`, `&kp Z` → `Z`.
- **Subscript** = target layer name when the hold side activates a layer
  (signal macro, `&mo`, `&to`, etc.); otherwise the hold side's own
  rendered label (e.g. `⌥` for an `&kp LALT` hold).
- **Top-left tag** = `Hold-Tap`.
- **Fill color** = target layer's palette color (same precedence as
  `&lt`).

Tooltip dumps the full data set:

```
Right thumb 2  (idx 58)

&ht_symbol RET

Hold-Tap → Symbol
  Hold: &mo_symbol_f16signal (signals F16)
  Tap:  &kp RET
```

```
Row 4, L col 5 (index)  (idx 39)

&HRM_left_hand_v1_TKZ LALT Z

Hold-Tap
  Hold: &kp LALT
  Tap:  &kp Z
```

## Choosing a signal keycode

The keycode emitted by a signal macro must be **safe** — no app should
react to it as input.

- ✅ **F13–F24** — the standard pick. No physical keyboard generates
  them, no app binds them by default.
- ✅ Region keycodes some Western OSes ignore: **`INT1`–`INT9`**,
  **`LANG1`–`LANG5`**.
- ❌ Letters, digits, punctuation, navigation keys — these type or
  trigger shortcuts in the focused app every time a layer changes.

ZMK can emit modifier-wrapped F-keys (`&kp LC(LS(F16))`), which expands
the address space if 12 plain F-keys ever runs out. The current viewer
does **not** distinguish modifier-tagged signals from bare ones — the
signal table is keyed by base keycode only. Adding `(mods, code)` keying
would require changes in `SignalMacroScanner` (skip leading short-mod
tokens, store mod set), `LayerSignalTable` (key by tuple), and
`HotkeyLayerTracker` (read mod state from the OS event).
