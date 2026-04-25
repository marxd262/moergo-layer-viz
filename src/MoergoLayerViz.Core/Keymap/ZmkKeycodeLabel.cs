namespace MoergoLayerViz.Core.Keymap;

/// <summary>
/// Translates ZMK keycode identifiers (as they appear in Moergo JSON exports)
/// into short display glyphs suitable for rendering on a 40–80px-wide key cap.
///
/// Covers the common punctuation abbreviations (FSLH, BSLH, SEMI, …), the
/// number-row aliases (N0..N9, NUMBER_0..NUMBER_9) and a handful of arrow /
/// whitespace glyphs. Unknown keycodes are returned unchanged so callers can
/// still show *something* rather than swallow a keycode we haven't mapped.
/// </summary>
public static class ZmkKeycodeLabel
{
    private static readonly Dictionary<string, string> DisplayMap = new(StringComparer.Ordinal)
    {
        // Punctuation — ZMK short abbreviations + long-form aliases the Moergo
        // editor emits (e.g. SINGLE_QUOTE alongside SQT). All aliases of the
        // same keycode map to the same glyph.
        ["FSLH"] = "/",      ["SLASH"] = "/",              ["FORWARD_SLASH"] = "/",
        ["BSLH"] = "\\",     ["BACKSLASH"] = "\\",
        ["SEMI"] = ";",      ["SEMICOLON"] = ";",
        ["SQT"] = "'",       ["SINGLE_QUOTE"] = "'",       ["APOS"] = "'",       ["APOSTROPHE"] = "'",
        ["DQT"] = "\"",      ["DOUBLE_QUOTES"] = "\"",
        ["GRAVE"] = "`",
        ["COMMA"] = ",",
        ["DOT"] = ".",       ["PERIOD"] = ".",
        ["MINUS"] = "-",
        ["EQUAL"] = "=",     ["EQUALS"] = "=",
        ["LBKT"] = "[",      ["LEFT_BRACKET"] = "[",
        ["RBKT"] = "]",      ["RIGHT_BRACKET"] = "]",
        ["LBRC"] = "{",      ["LEFT_BRACE"] = "{",
        ["RBRC"] = "}",      ["RIGHT_BRACE"] = "}",
        ["LPAR"] = "(",      ["LEFT_PARENTHESIS"] = "(",
        ["RPAR"] = ")",      ["RIGHT_PARENTHESIS"] = ")",
        ["PIPE"] = "|",
        ["TILDE"] = "~",
        ["COLON"] = ":",
        ["EXCL"] = "!",      ["EXCLAMATION"] = "!",
        ["AT"] = "@",        ["AT_SIGN"] = "@",
        ["HASH"] = "#",      ["POUND"] = "#",
        ["DLLR"] = "$",      ["DOLLAR"] = "$",
        ["PRCNT"] = "%",     ["PERCENT"] = "%",
        ["CARET"] = "^",
        ["AMPS"] = "&",      ["AMPERSAND"] = "&",
        ["STAR"] = "*",      ["ASTERISK"] = "*",
        ["UNDER"] = "_",     ["UNDERSCORE"] = "_",
        ["PLUS"] = "+",
        ["QMARK"] = "?",     ["QUESTION"] = "?",
        ["LT"] = "<",        ["LESS_THAN"] = "<",
        ["GT"] = ">",        ["GREATER_THAN"] = ">",

        // Whitespace / editing
        ["SPACE"] = "␣",
        ["BSPC"] = "⌫",      ["BACKSPACE"] = "⌫",
        ["DEL"] = "⌦",       ["DELETE"] = "⌦",
        ["RET"] = "⏎",       ["ENTER"] = "⏎",             ["RETURN"] = "⏎",
        ["TAB"] = "⇥",

        // Numpad-specific long forms (base names — a `KP_` prefix is stripped
        // in Display() before lookup, so `KP_DIVIDE` resolves to `/` via these).
        ["DIVIDE"] = "/",
        ["MULTIPLY"] = "*",
        ["SUBTRACT"] = "-",
        ["NUMLOCK"] = "Num", ["NUM_LOCK"] = "Num", ["NLCK"] = "Num",

        // Consumer / media keycodes (ZMK `C_*` usage codes, passed as params to &kp).
        ["C_BRI_UP"] = "☀+", ["C_BRIGHTNESS_INC"] = "☀+",
        ["C_BRI_DN"] = "☀−", ["C_BRIGHTNESS_DEC"] = "☀−",
        // Speaker glyphs (🔊 🔉 🔇) are in the emoji plane and render in color
        // in most fonts — use plain text to stay monochrome with the rest.
        ["C_VOL_UP"] = "Vol +", ["C_VOLUME_UP"] = "Vol +",
        ["C_VOL_DN"] = "Vol −", ["C_VOLUME_DOWN"] = "Vol −",
        ["C_MUTE"] = "Mute",
        ["C_PP"] = "⏯", ["C_PLAY_PAUSE"] = "⏯",
        ["C_PREV"] = "⏮", ["C_PREVIOUS"] = "⏮",
        ["C_NEXT"] = "⏭",
        ["C_STOP"] = "⏹",
        ["C_FF"] = "⏩", ["C_FAST_FORWARD"] = "⏩",
        ["C_REWIND"] = "⏪",
        ["C_EJECT"] = "⏏",
        ["PSCRN"] = "PrtSc", ["PRINTSCREEN"] = "PrtSc",
        ["SLCK"] = "ScrLk", ["SCROLLLOCK"] = "ScrLk",
        ["PAUSE_BREAK"] = "Pause", ["PAUSE"] = "Pause",

        // Arrows — thin monochrome variants (the heavy forms ⬆⬇⬅➡ render as
        // color emoji in many fonts). KeyViewModel.LabelFontSize gives these
        // a size bump so the slim strokes still read at a glance.
        ["UP"] = "↑",        ["UP_ARROW"] = "↑",
        ["DOWN"] = "↓",      ["DOWN_ARROW"] = "↓",
        ["LEFT"] = "←",      ["LEFT_ARROW"] = "←",
        ["RIGHT"] = "→",     ["RIGHT_ARROW"] = "→",

        // Modifier keys as main labels — L/R hand distinction is dropped
        // (same convention as ModifierSubscript: the modifier type matters,
        // the hand usually doesn't when you're scanning a keymap).
        ["LSHFT"] = "⇪", ["RSHFT"] = "⇪",
        ["LEFT_SHIFT"] = "⇪", ["RIGHT_SHIFT"] = "⇪",
        ["LCTRL"] = "⌃", ["RCTRL"] = "⌃",
        ["LEFT_CONTROL"] = "⌃", ["RIGHT_CONTROL"] = "⌃",
        ["LALT"] = "⌥", ["RALT"] = "⌥",
        ["LEFT_ALT"] = "⌥", ["RIGHT_ALT"] = "⌥",
        ["LGUI"] = "⌘", ["RGUI"] = "⌘",
        ["LEFT_GUI"] = "⌘", ["RIGHT_GUI"] = "⌘",
        ["LEFT_COMMAND"] = "⌘", ["RIGHT_COMMAND"] = "⌘",
        ["LWIN"] = "⌘", ["RWIN"] = "⌘",
    };

    /// <summary>
    /// Returns a short display glyph for a ZMK keycode. Falls through to the
    /// raw input for anything not in the map (e.g. TAB, ESC, F1..F24, letters).
    /// </summary>
    public static string Display(string zmkKeycode)
    {
        if (string.IsNullOrEmpty(zmkKeycode)) return "";

        if (DisplayMap.TryGetValue(zmkKeycode, out var mapped)) return mapped;

        // Numpad keycodes share the same glyphs as their main-row counterparts.
        // Strip the `KP_` prefix and retry so `KP_N1`, `KP_PLUS`, `KP_DIVIDE`,
        // etc. all route through the existing logic.
        if (zmkKeycode.StartsWith("KP_", StringComparison.Ordinal))
            return Display(zmkKeycode.Substring(3));

        // N0..N9 and NUMBER_0..NUMBER_9 both map to the underlying digit.
        if (zmkKeycode.Length == 2 && zmkKeycode[0] == 'N' && zmkKeycode[1] >= '0' && zmkKeycode[1] <= '9')
            return zmkKeycode[1].ToString();

        if (zmkKeycode.StartsWith("NUMBER_", StringComparison.Ordinal) && zmkKeycode.Length == 8
            && zmkKeycode[7] >= '0' && zmkKeycode[7] <= '9')
            return zmkKeycode[7].ToString();

        return zmkKeycode;
    }

    /// <summary>
    /// Maps a modifier keycode (LSHFT/RSHFT/LCTRL/RCTRL/LALT/RALT/LGUI/RGUI)
    /// to its single-character subscript glyph. L/R hand distinction is dropped
    /// — labels read cleaner without it, and knowing *which* shift fires is
    /// rarely what the user is checking when glancing at a key. Returns null
    /// for non-modifier keycodes so callers can fall back to the raw param.
    /// </summary>
    public static string? ModifierSubscript(string zmkKeycode) => zmkKeycode switch
    {
        "LSHFT" or "RSHFT" => "⇪",
        "LCTRL" or "RCTRL" => "⌃",
        "LALT" or "RALT" => "⌥",
        "LGUI" or "RGUI" => "⌘",
        _ => null,
    };

    /// <summary>
    /// ZMK modifier-function shorthands used when wrapping another keycode:
    /// e.g. <c>&amp;kp LS(LBKT)</c> flattens to params ["LS", "LBKT"].
    /// </summary>
    private static readonly HashSet<string> ShortModifiers = new(StringComparer.Ordinal)
    {
        "LS", "RS", "LC", "RC", "LA", "RA", "LG", "RG",
    };

    public static bool IsShortModifier(string s) => ShortModifiers.Contains(s);

    /// <summary>Short-modifier glyph (e.g. LS / RS → ⇧, LC / RC → ⌃).</summary>
    public static string ShortModifierGlyph(string s) => s switch
    {
        "LS" or "RS" => "⇪",
        "LC" or "RC" => "⌃",
        "LA" or "RA" => "⌥",
        "LG" or "RG" => "⌘",
        _ => s,
    };

    /// <summary>
    /// US-QWERTY shifted symbol for a keycode — e.g. LBKT → "{", N1 → "!".
    /// Returns null if the keycode has no distinct shifted glyph on US layout
    /// (letters, function keys, navigation keys, etc).
    /// </summary>
    private static readonly Dictionary<string, string> ShiftedMap = new(StringComparer.Ordinal)
    {
        ["N1"] = "!", ["N2"] = "@", ["N3"] = "#", ["N4"] = "$", ["N5"] = "%",
        ["N6"] = "^", ["N7"] = "&", ["N8"] = "*", ["N9"] = "(", ["N0"] = ")",
        ["NUMBER_1"] = "!", ["NUMBER_2"] = "@", ["NUMBER_3"] = "#",
        ["NUMBER_4"] = "$", ["NUMBER_5"] = "%", ["NUMBER_6"] = "^",
        ["NUMBER_7"] = "&", ["NUMBER_8"] = "*", ["NUMBER_9"] = "(",
        ["NUMBER_0"] = ")",
        ["MINUS"] = "_",
        ["EQUAL"] = "+", ["EQUALS"] = "+",
        ["LBKT"] = "{", ["LEFT_BRACKET"] = "{",
        ["RBKT"] = "}", ["RIGHT_BRACKET"] = "}",
        ["BSLH"] = "|", ["BACKSLASH"] = "|",
        ["SEMI"] = ":", ["SEMICOLON"] = ":",
        ["SQT"] = "\"", ["SINGLE_QUOTE"] = "\"", ["APOS"] = "\"", ["APOSTROPHE"] = "\"",
        ["COMMA"] = "<",
        ["DOT"] = ">", ["PERIOD"] = ">",
        ["FSLH"] = "?", ["SLASH"] = "?", ["FORWARD_SLASH"] = "?",
        ["GRAVE"] = "~",
    };

    public static string? Shifted(string zmkKeycode) =>
        ShiftedMap.TryGetValue(zmkKeycode, out var s) ? s : null;

    /// <summary>
    /// Formats the flattened params of a <c>&amp;kp</c> binding into a display
    /// (Label, Subscript) pair. Handles modifier-function wrappers (LS, RS, LC,
    /// …): a single shift over a shiftable key collapses to the shifted glyph
    /// (e.g. LS + LBKT → "{"), other modifier chains render as base-key label
    /// with the modifier glyphs as subscript.
    /// </summary>
    public static (string Label, string Subscript) FormatKpParams(IReadOnlyList<string> paramList)
    {
        if (paramList.Count == 0) return ("", "");
        if (paramList.Count == 1) return (Display(paramList[0]), "");

        // Strip leading short-modifier wrappers.
        int i = 0;
        while (i < paramList.Count - 1 && IsShortModifier(paramList[i])) i++;
        var baseKc = paramList[i];
        var modCount = i;

        if (modCount == 0)
            return (Display(baseKc), "");

        // Single shift over a shiftable key → use the shifted glyph.
        if (modCount == 1 && (paramList[0] == "LS" || paramList[0] == "RS"))
        {
            var shifted = Shifted(baseKc);
            if (shifted is not null) return (shifted, "");
        }

        // Otherwise: base keycode as main, modifier glyphs concatenated as subscript.
        var modGlyphs = new System.Text.StringBuilder();
        for (int m = 0; m < modCount; m++) modGlyphs.Append(ShortModifierGlyph(paramList[m]));
        return (Display(baseKc), modGlyphs.ToString());
    }
}
