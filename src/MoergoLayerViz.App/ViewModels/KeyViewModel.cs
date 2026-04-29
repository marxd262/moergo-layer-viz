using CommunityToolkit.Mvvm.ComponentModel;
using MoergoLayerViz.Core.Keymap;
using MoergoLayerViz.Core.Layout;
using MoergoLayerViz.Core.Models;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Per-key bindable: position (from the profile) + display (label + highlight
/// fill derived from the active layer's binding).
/// </summary>
public partial class KeyViewModel : ObservableObject
{
    public KeyPosition Position { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelFontSize))]
    private string _label = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubscriptFontSize))]
    private string _subscript = "";
    [ObservableProperty] private string _topLeftLabel = "";
    [ObservableProperty] private string _iconName = "";
    [ObservableProperty] private string _behavior = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyForegroundColor))]
    private string _keyFillColor = DefaultKeyFill;

    private const string DefaultKeyFill = "#F2F2F2";
    private const string DarkForeground = "#1E1E2E";
    private const string LightForeground = "#F2F2F2";

    /// <summary>
    /// Auto-contrasting label/icon color for the current <see cref="KeyFillColor"/>.
    /// Uses Rec. 709 luminance (0.2126R + 0.7152G + 0.0722B) with a threshold
    /// around mid-grey so pastel fills still read dark while navy / black
    /// decoration backgrounds get a light foreground.
    /// </summary>
    public string KeyForegroundColor => IsLightBackground(KeyFillColor) ? DarkForeground : LightForeground;

    private static bool IsLightBackground(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return true;
        if (hex.StartsWith('#')) hex = hex.Substring(1);
        // Accept CSS-style #RRGGBBAA — drop trailing alpha, use just RGB.
        if (hex.Length == 8) hex = hex.Substring(0, 6);
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length != 6) return true;
        if (!int.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !int.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !int.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return true;
        var luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        return luminance > 140; // ~55% of max — tuned so pastel layer palette stays "light".
    }
    [ObservableProperty] private bool _isLayerSignalKey;
    [ObservableProperty] private bool _isPressed;
    [ObservableProperty] private bool _isUntrackableLayerSwitch;
    [ObservableProperty] private bool _isInCombo;
    [ObservableProperty] private string _tooltip = "";

    /// <summary>
    /// Auto-scales the label font to the label length so single glyphs read
    /// big while multi-word labels still fit a 60px cap.
    /// </summary>
    public double LabelFontSize => Label switch
    {
        // Slim arrow glyphs need extra weight to read at a glance.
        "↑" or "↓" or "←" or "→" => 28,
        _ => LongestWordLength(Label) switch
        {
            0 => 14,
            1 => 20,
            2 => 18,
            3 => 15,
            <= 5 => 13,
            <= 8 => 11,
            _ => 10,
        },
    };

    /// <summary>
    /// Same length-based scaling for the subscript — single-char glyphs (the
    /// modifier icons ⇧ ⌃ ⌥ ⌘) get a bump so they read as icons rather than
    /// vestigial tags. Long layer-name subscripts (now possibly multi-word
    /// after FormatLayerName) shrink further so they fit when wrapped.
    /// </summary>
    public double SubscriptFontSize => LongestWordLength(Subscript) switch
    {
        0 => 11,
        1 => 15,
        2 => 12,
        <= 4 => 11,
        <= 7 => 10,
        _ => 9,
    };

    private static int LongestWordLength(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int best = 0, run = 0;
        foreach (var c in s)
        {
            if (c == ' ' || c == '\n') { if (run > best) best = run; run = 0; }
            else run++;
        }
        return run > best ? run : best;
    }

    public KeyViewModel(KeyPosition position)
    {
        Position = position;
    }

    // Tooltip without combo lines, captured so SetCombos can rebuild Tooltip
    // by appending combo info without re-running the per-key binding logic.
    private string _baseTooltip = "";

    /// <summary>
    /// Pushes a new binding from the active layer into this view model.
    /// Driven by <see cref="MainWindowViewModel"/> on layer change. Combo info
    /// is layered on afterwards via <see cref="SetCombos"/>, once every key's
    /// label is settled (so the combo tooltip can name participants by label).
    /// </summary>
    public void ApplyBinding(KeyBinding binding, bool isSignalMacro, bool isUntrackable, int? targetLayer, string? targetLayerName, string profileId, HoldTap? holdTap = null, SignalMacro? signal = null)
    {
        Behavior = binding.Behavior;
        IsLayerSignalKey = isSignalMacro;
        IsUntrackableLayerSwitch = isUntrackable;
        IsInCombo = false;
        KeyFillColor = ResolveFillColor(binding, targetLayer, profileId);
        IconName = NormalizeIconName(binding.DecorationIcon);
        _baseTooltip = BuildTooltip(binding, targetLayerName, holdTap, signal);
        Tooltip = _baseTooltip;

        // decoration.icon acts as flair above the label — if a decoration.label
        // is also set, it still drives the main label. Derived label/subscript
        // logic is skipped so the user-authored pairing wins cleanly.
        if (!string.IsNullOrEmpty(IconName))
        {
            Label = binding.DecorationLabel ?? "";
            Subscript = "";
            TopLeftLabel = "";
            return;
        }

        var (label, sub, topLeft) = FormatBinding(binding, targetLayerName, holdTap, signal);
        Label = label;
        Subscript = sub;
        TopLeftLabel = topLeft;
    }

    /// <summary>
    /// Multi-line tooltip dump of every piece of data we have for this key,
    /// minus the decoration icon (already rendered visually). Format:
    /// <list type="bullet">
    /// <item>line 1: raw binding (<c>&amp;kp LS(LBKT)</c>, <c>&amp;ht_symbol RET</c>, …)</item>
    /// <item>category section: hold-tap / layer-switch / signal-macro detail with target layer</item>
    /// <item>decoration section: user-authored label and background hex if present</item>
    /// </list>
    /// </summary>
    private string BuildTooltip(KeyBinding b, string? targetLayerName, HoldTap? holdTap, SignalMacro? signal)
    {
        var posLine = Position.Description is { } desc
            ? $"{desc}  (idx {Position.Index})"
            : $"idx {Position.Index}";
        var lines = new List<string> { posLine, "", b.Display };

        string? signalKeycode = null;
        if (signal is not null && signal.TryResolveSignalKeycode(b, out var kc))
            signalKeycode = kc;

        var layerLabel = targetLayerName ?? "?";

        if (holdTap is not null)
        {
            var (holdParams, tapParams) = SplitHoldTapParams(b.Params, holdTap);
            lines.Add("");
            var heading = targetLayerName is null ? "Hold-Tap" : $"Hold-Tap → {targetLayerName}";
            lines.Add(heading);
            var sigSuffix = signalKeycode is null ? "" : $" (signals {signalKeycode})";
            var holdTail = holdParams.Count > 0 ? " " + string.Join(' ', holdParams) : "";
            var tapTail = tapParams.Count > 0 ? " " + string.Join(' ', tapParams) : "";
            lines.Add($"  Hold: {holdTap.HoldBinding}{holdTail}{sigSuffix}");
            lines.Add($"  Tap:  {holdTap.TapBinding}{tapTail}");
        }
        else if (signal is not null)
        {
            lines.Add("");
            var line = $"Signal macro → {layerLabel}";
            if (signalKeycode is not null) line += $" (signals {signalKeycode})";
            lines.Add(line);
        }
        else
        {
            string? category = b.Behavior switch
            {
                "&lt"  when b.Params.Count == 2 => "Layer Tap",
                "&mo"  when b.Params.Count >= 1 => "Momentary",
                "&to"  when b.Params.Count >= 1 => "To Layer",
                "&tog" when b.Params.Count >= 1 => "Toggle Layer",
                "&sl"  when b.Params.Count >= 1 => "Sticky Layer",
                _ => null,
            };
            if (category is not null)
            {
                lines.Add("");
                lines.Add($"{category} → {layerLabel}");
            }
        }

        var hasDecoration = !string.IsNullOrEmpty(b.DecorationLabel) || !string.IsNullOrEmpty(b.DecorationBackground);
        if (hasDecoration)
        {
            lines.Add("");
            if (!string.IsNullOrEmpty(b.DecorationLabel))
                lines.Add($"Label: {b.DecorationLabel}");
            if (!string.IsNullOrEmpty(b.DecorationBackground))
                lines.Add($"Background: {b.DecorationBackground}");
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Appends combo information to the tooltip and flips <see cref="IsInCombo"/>.
    /// Called by <see cref="MainWindowViewModel"/> in a second pass after every
    /// key has its label settled, so participating keys can be named by their
    /// rendered label (Q, W) rather than raw row/col coords.
    /// </summary>
    public void SetCombos(IReadOnlyList<MoergoCombo> combos, Func<int, string> labelLookup)
    {
        if (combos is null || combos.Count == 0)
        {
            IsInCombo = false;
            Tooltip = _baseTooltip;
            return;
        }

        IsInCombo = true;
        var lines = new List<string> { _baseTooltip };
        foreach (var combo in combos)
        {
            lines.Add("");
            var comboBinding = combo.Binding.Display;
            var heading = string.IsNullOrEmpty(combo.Name)
                ? $"Combo → {comboBinding}"
                : $"Combo \"{combo.Name}\" → {comboBinding}";
            lines.Add(heading);
            lines.Add("  Keys: " + string.Join(" + ", combo.KeyPositions.Select(idx => DescribeComboKey(idx, labelLookup))));
        }
        Tooltip = string.Join('\n', lines);
    }

    private static string DescribeComboKey(int idx, Func<int, string> labelLookup)
    {
        var label = labelLookup(idx);
        return string.IsNullOrWhiteSpace(label) ? idx.ToString() : label;
    }

    /// <summary>
    /// Splits the keymap-binding params between the hold and tap sides of a
    /// hold-tap, using each side's declared arity. The hold side consumes the
    /// leading params, the tap side the trailing ones — matching ZMK's order.
    /// If the keymap supplied fewer params than expected (e.g. a malformed
    /// entry), missing params are silently truncated.
    /// </summary>
    private static (IReadOnlyList<string> Hold, IReadOnlyList<string> Tap) SplitHoldTapParams(IReadOnlyList<string> all, HoldTap ht)
    {
        var holdCount = Math.Min(ht.HoldArity, all.Count);
        var tapCount = Math.Min(ht.TapArity, Math.Max(0, all.Count - holdCount));
        var hold = new string[holdCount];
        var tap = new string[tapCount];
        for (int i = 0; i < holdCount; i++) hold[i] = all[i];
        for (int i = 0; i < tapCount; i++) tap[i] = all[holdCount + i];
        return (hold, tap);
    }

    /// <summary>
    /// Fill-color precedence:
    /// <list type="number">
    /// <item><c>decoration.background</c> from the Moergo editor (user-authored).</item>
    /// <item>Target layer's palette color (for <c>&amp;lt</c> and signal-macro keys).</item>
    /// <item>Empty string — the view renders the default <c>KeyFill</c>.</item>
    /// </list>
    /// </summary>
    private static string ResolveFillColor(KeyBinding b, int? targetLayer, string profileId)
    {
        if (!string.IsNullOrWhiteSpace(b.DecorationBackground))
            return b.DecorationBackground!;
        if (targetLayer is int layer)
            return LayerColorPalette.GetColor(profileId, layer);
        return DefaultKeyFill;
    }

    private static (string Label, string Subscript, string TopLeft) FormatBinding(KeyBinding b, string? targetLayerName, HoldTap? holdTap = null, SignalMacro? signal = null)
    {
        // User-authored decoration.label from the Moergo editor wins outright.
        if (!string.IsNullOrEmpty(b.DecorationLabel))
            return (b.DecorationLabel, "", "");

        // Display-only: insert wrap opportunities into long layer names like
        // "HRM_WinLinx" → "HRM Win Linx" so TextBlock.TextWrapping can break
        // them onto multiple lines. Tooltip keeps the raw form.
        var layerName = targetLayerName is null ? null : FormatLayerName(targetLayerName);

        // Recognised layer-switch macro (wraps &mo / &to / &tog / &lt / … with a
        // host-visible signal keycode): mirror the bare &to layout — main label
        // is the target layer name, with a "Macro" badge so the user can tell
        // it's a macro-wrapped switch. The signal keycode stays in the tooltip.
        // Skip when the binding is also a hold-tap: the Hold-Tap path below
        // surfaces the layer name as the subscript and keeps the tap-side
        // keycode visible, which is more informative for &ht_*-style wrappers.
        if (signal is not null && holdTap is null)
        {
            var layerLabel = layerName
                ?? (signal.TryResolveTargetLayer(b, out var idx) ? "L" + idx : b.Behavior.TrimStart('&'));
            return (layerLabel, "", "Macro");
        }

        // Hold-tap: split the keymap params between hold and tap sides
        // according to each side's declared arity, render the tap-side as the
        // main label, and surface the hold-side as the subscript (preferring
        // the target layer name when the hold side activates a layer, falling
        // back to the hold-side's own rendered label — e.g. "⌥" for an
        // &kp LALT hold on a homerow-mod).
        if (holdTap is not null)
        {
            var (holdParams, tapParams) = SplitHoldTapParams(b.Params, holdTap);
            var tap = new KeyBinding(holdTap.TapBinding, tapParams);
            var hold = new KeyBinding(holdTap.HoldBinding, holdParams);
            var (tapLabel, _, _) = FormatBinding(tap, null, null);
            var (holdLabel, _, _) = FormatBinding(hold, targetLayerName, null);
            var sub = !string.IsNullOrEmpty(layerName) ? layerName : holdLabel;
            return (tapLabel, sub, "Hold-Tap");
        }

        switch (b.Behavior)
        {
            case "&trans": return ("▽", "", "");
            case "&none": return ("", "", "");
            case "&kp" when b.Params.Count >= 1:
                var (kpLabel, kpSub) = ZmkKeycodeLabel.FormatKpParams(b.Params);
                return (kpLabel, kpSub, "");
            case "&to" when b.Params.Count >= 1:
                return (layerName ?? ("L" + b.Params[0]), "", "To Layer");
            case "&mo" when b.Params.Count >= 1:
                return (layerName ?? ("L" + b.Params[0]), "", "Momentary");
            case "&tog" when b.Params.Count >= 1:
                return (layerName ?? ("L" + b.Params[0]), "", "Toggle Layer");
            case "&sl" when b.Params.Count >= 1:
                return (layerName ?? ("L" + b.Params[0]), "", "Sticky Layer");
            case "&lt" when b.Params.Count == 2 && int.TryParse(b.Params[0], out _):
                return (ZmkKeycodeLabel.Display(b.Params[1]),
                        layerName ?? ("L" + b.Params[0]),
                        "Layer Tap");

            // Standard ZMK system behaviors (magic / adjust layer).
            case "&bt" when b.Params.Count >= 1:
                return (FormatBtParams(b.Params), "", "Bluetooth");
            case "&out" when b.Params.Count >= 1:
                return (FormatOutParam(b.Params[0]), "", "Output");
            case "&sys_reset": return ("Reset", "", "System");
            case "&bootloader": return ("Boot", "", "System");
            case "&ext_power" when b.Params.Count >= 1:
                return (FormatExtPowerParam(b.Params[0]), "", "Ext Power");
            case "&rgb_ug" when b.Params.Count >= 1:
                return (FormatRgbParam(b.Params[0]), "", "Underglow");
        }

        // Home-row-mod macro convention: &HRM_<name> <modifier-keycode> <base-keycode>.
        if (b.Behavior.StartsWith("&HRM_", StringComparison.Ordinal) && b.Params.Count == 2)
        {
            var mod = ZmkKeycodeLabel.ModifierSubscript(b.Params[0]) ?? b.Params[0];
            return (ZmkKeycodeLabel.Display(b.Params[1]), mod, "");
        }

        // Moergo magic-layer macro wrappers: `&bt_0`..`&bt_4`, `&bt_clr`, etc.
        if (b.Behavior.StartsWith("&bt_", StringComparison.Ordinal))
        {
            var tail = b.Behavior.Substring(4).Replace('_', ' ').ToUpperInvariant();
            return (tail, "", "Bluetooth");
        }

        if (b.Params.Count == 0) return (b.Behavior.TrimStart('&'), "", "");
        return (string.Join(' ', b.Params), "", "");
    }

    // Internal so tests can exercise the splitting heuristic directly.
    internal static string FormatLayerName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '_')
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                continue;
            }
            // camelCase break: insert space when an uppercase letter follows a
            // lowercase letter or digit. Runs of uppercase ("HRM", "TKZ") are
            // preserved so acronyms read as a unit.
            if (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
            }
            sb.Append(c);
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatBtParams(IReadOnlyList<string> p) => p[0] switch
    {
        "BT_SEL" when p.Count >= 2 => p[1],
        "BT_CLR" => "Clear",
        "BT_CLR_ALL" => "Clr All",
        "BT_NXT" => "Next",
        "BT_PRV" => "Prev",
        "BT_DISC" when p.Count >= 2 => "Disc " + p[1],
        _ => p[0],
    };

    private static string FormatOutParam(string p) => p switch
    {
        "OUT_TOG" => "Toggle",
        "OUT_USB" => "USB",
        "OUT_BLE" => "BT",
        _ => p,
    };

    private static string FormatExtPowerParam(string p) => p switch
    {
        "EP_ON" => "On",
        "EP_OFF" => "Off",
        "EP_TOG" => "Toggle",
        _ => p,
    };

    private static string FormatRgbParam(string p) => p switch
    {
        "RGB_ON"  => "On",
        "RGB_OFF" => "Off",
        "RGB_TOG" => "Toggle",
        "RGB_EFF" => "Effect +",
        "RGB_EFR" => "Effect −",
        "RGB_HUI" => "Hue +",
        "RGB_HUD" => "Hue −",
        "RGB_SAI" => "Sat +",
        "RGB_SAD" => "Sat −",
        "RGB_BRI" => "Bri +",
        "RGB_BRD" => "Bri −",
        "RGB_SPI" => "Spd +",
        "RGB_SPD" => "Spd −",
        _ => p,
    };

    /// <summary>
    /// Translates Moergo editor's icon identifiers to Font Awesome 6 names.
    /// Handles (a) Ionicons prefixed with <c>io-</c> that have FA equivalents,
    /// and (b) FA4 / FA5 names that were renamed in FA6 Free.
    /// </summary>
    private static string NormalizeIconName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return raw switch
        {
            // Ionicons → FA
            "io-finger-print" => "fa-fingerprint",

            // FA4/FA5 → FA6 renames (only the ones actually used in Moergo's JSON).
            "fa-search" => "fa-magnifying-glass",
            "fa-search-plus" => "fa-magnifying-glass-plus",
            "fa-search-minus" => "fa-magnifying-glass-minus",
            "fa-redo" => "fa-rotate-right",
            "fa-undo" => "fa-rotate-left",
            "fa-cut" => "fa-scissors",

            _ => raw,
        };
    }
}
