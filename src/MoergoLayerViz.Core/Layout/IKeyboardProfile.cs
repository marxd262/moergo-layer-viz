namespace MoergoLayerViz.Core.Layout;

/// <summary>
/// A keyboard-specific physical layout: canvas dimensions and the absolute
/// positions of every key, indexed so they can be joined against
/// <see cref="Models.Layer.Bindings"/>.
/// </summary>
public interface IKeyboardProfile
{
    /// <summary>Stable id — "GO60", "Glove80", etc. — matches <see cref="Settings.UserSettings.Keyboard"/>.</summary>
    string Id { get; }

    /// <summary>Display name for the UI.</summary>
    string DisplayName { get; }

    /// <summary>Total number of physical keys (60 for GO60, 80 for Glove80).</summary>
    int KeyCount { get; }

    /// <summary>Canvas width for the full board render.</summary>
    double CanvasWidth { get; }

    /// <summary>Canvas height for the full board render.</summary>
    double CanvasHeight { get; }

    /// <summary>
    /// Every key on the board. Order is irrelevant — <see cref="KeyPosition.Index"/>
    /// is authoritative and must match the JSON binding order.
    /// </summary>
    IReadOnlyList<KeyPosition> Keys { get; }
}
