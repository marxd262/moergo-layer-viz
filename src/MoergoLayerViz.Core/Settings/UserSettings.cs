namespace MoergoLayerViz.Core.Settings;

/// <summary>
/// User-configurable settings, persisted as JSON.
/// </summary>
public record UserSettings
{
    /// <summary>
    /// Bumped on any breaking schema change (rename, type change, removed
    /// required field). <see cref="Settings.SettingsService.Load"/> uses this
    /// to dispatch migrations and to refuse files written by a newer version
    /// rather than silently nuking them.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the persisted settings file. Defaults to current for new files.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>
    /// Which Moergo keyboard profile to use. "GO60" or "Glove80". Chosen once by the user
    /// at first launch (or via settings) — remembered across sessions.
    /// </summary>
    public string Keyboard { get; init; } = "GO60";

    /// <summary>
    /// Last-used Moergo layout-editor JSON path per keyboard profile.
    /// Key = profile id ("GO60", "Glove80"), Value = absolute path. Empty until
    /// the user has picked a file — switching keyboards reloads whichever JSON
    /// was last associated with the new profile (or leaves the board blank if
    /// none yet).
    /// </summary>
    public Dictionary<string, string> LayoutJsonPaths { get; init; } = new();

    /// <summary>
    /// Per-layer color overrides, scoped per keyboard profile.
    /// Outer key = profile id, inner key = layer index, value = hex "#RRGGBB".
    /// </summary>
    public Dictionary<string, Dictionary<int, string>> LayerColors { get; init; } = new();

    /// <summary>
    /// User-assigned signal keycodes for layers that have no auto-detected
    /// signal macro. Outer key = profile id, inner key = layer index, value =
    /// ZMK keycode (e.g. "F17"). Auto-detected mappings always take precedence;
    /// entries here for layers with an auto mapping are silently ignored.
    /// </summary>
    public Dictionary<string, Dictionary<int, string>> ManualLayerSignals { get; init; } = new();

    /// <summary>Whether the window stays on top of other windows.</summary>
    public bool AlwaysOnTop { get; init; } = true;

    /// <summary>Global hotkey key name (SharpHook KeyCode enum without "Vc" prefix, e.g. "F12").</summary>
    public string HotkeyKey { get; init; } = "F12";

    /// <summary>Global hotkey modifier (SharpHook EventMask name, e.g. "None", "Ctrl").</summary>
    public string HotkeyModifiers { get; init; } = "None";

    /// <summary>
    /// Whether live key highlighting (pressed-key visualization) is enabled.
    /// Requires Accessibility permission on macOS.
    /// </summary>
    public bool LiveKeyHighlighting { get; init; } = true;

    /// <summary>Whether to auto-switch the displayed layer when signal-macro keys are detected.</summary>
    public bool AutoLayerSwitch { get; init; } = true;

    /// <summary>Background solidity behind the board (0.0 = transparent, 1.0 = solid dark). Default: 0.5.</summary>
    public double BackgroundOpacity { get; init; } = 0.5;

    /// <summary>
    /// Color used for the press-highlight dot pulsed on each pressed key.
    /// Stored as "#RRGGBB". Default preserves the original saturated yellow.
    /// </summary>
    public string PressHighlightColor { get; init; } = "#FFD60A";

    /// <summary>Whether the user has seen the help/welcome window. Controls first-launch auto-open.</summary>
    public bool HasSeenHelp { get; init; } = false;

    /// <summary>UI language code (e.g. "en", "nl"). Default follows system locale, falls back to English.</summary>
    public string Language { get; init; } = "en";

    /// <summary>
    /// Rendering mode override. "auto" (default) uses the platform default GPU pipeline.
    /// "software" forces software rendering (workaround for GPU driver issues).
    /// The MOERGO_RENDER_MODE environment variable takes precedence over this setting.
    /// </summary>
    public string RenderingMode { get; init; } = "auto";

    /// <summary>Last window X position (pixels). Null = first launch, center on screen.</summary>
    public double? WindowX { get; init; }

    /// <summary>Last window Y position (pixels). Null = first launch, center on screen.</summary>
    public double? WindowY { get; init; }

    /// <summary>Last window width (pixels). Null = use default (1200).</summary>
    public double? WindowWidth { get; init; }

    /// <summary>Last window height (pixels). Null = use default (600).</summary>
    public double? WindowHeight { get; init; }

    /// <summary>Minimum log level for diagnostic logging. Default "Info". Values: Trace, Debug, Info, Warn, Error.</summary>
    public string LogLevel { get; init; } = "Info";

    /// <summary>
    /// When true, the two halves of the keyboard render stacked vertically
    /// instead of side-by-side. Useful for narrow / portrait window placements.
    /// </summary>
    public bool StackedLayout { get; init; } = false;

    /// <summary>Which half goes on top in stacked mode. "Left" or "Right".</summary>
    public string StackedTopHand { get; init; } = "Left";
}
