using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoergoLayerViz.App.Localization;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Persistence;
using MoergoLayerViz.Core.Tooling;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// View model for the "Generate signal macros &amp; hold-taps" window.
/// Reads the currently-loaded layout JSON from disk, runs
/// <see cref="SignalMacroGenerator"/>, asks the user where to save the
/// result via a callback, and surfaces the list of additions and any
/// warnings back to the user along with a short post-import instruction.
/// </summary>
public partial class GenerateSignalsViewModel : ObservableObject
{
    private readonly string? _loadedPath;

    public GenerateSignalsViewModel(string? loadedLayoutPath)
    {
        _loadedPath = loadedLayoutPath;
        _loadedLayoutDisplay = string.IsNullOrEmpty(loadedLayoutPath)
            ? Loc.Instance["Generate_NoLayoutLoaded"]
            : Path.GetFileName(loadedLayoutPath);

        // Commands must exist before the first RecomputeProjections call —
        // RecomputeProjections sets SelectedCount, which fires the partial
        // OnSelectedCountChanged handler that pokes GenerateCommand.
        GenerateCommand = new AsyncRelayCommand(GenerateAsync,
            () => !IsBusy && !string.IsNullOrEmpty(_loadedPath) && SelectedCount > 0);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        ClearSelectionCommand = new RelayCommand(() => SetAllSelected(false));

        foreach (var (name, idx) in ReadLayerNames(loadedLayoutPath).Select((n, i) => (n, i)))
        {
            var item = new LayerSelectionItem(idx, name);
            item.PropertyChanged += OnLayerItemChanged;
            Layers.Add(item);
        }
        RecomputeProjections();
    }

    /// <summary>Set by App.axaml.cs — opens a save-file picker, returns the chosen path or null.</summary>
    public Func<string, Task<string?>>? SaveFileRequested { get; set; }

    [ObservableProperty]
    private string _loadedLayoutDisplay;

    [ObservableProperty]
    private int _startFkey = SignalMacroGenerator.MinFkey;

    public int MinFkey => SignalMacroGenerator.MinFkey;
    public int MaxFkey => SignalMacroGenerator.MaxFkey;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Status / error / success message shown in the result panel.</summary>
    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>True after a successful generation — toggles the result panel visibility.</summary>
    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private int _selectedCount;

    public ObservableCollection<LayerSelectionItem> Layers { get; } = new();
    public ObservableCollection<string> AddedItems { get; } = new();
    public ObservableCollection<string> SkippedItems { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    public IAsyncRelayCommand GenerateCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }

    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();
    partial void OnSelectedCountChanged(int value) => GenerateCommand.NotifyCanExecuteChanged();
    partial void OnStartFkeyChanged(int value) => RecomputeProjections();

    private void OnLayerItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LayerSelectionItem.IsSelected))
            RecomputeProjections();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var item in Layers) item.IsSelected = selected;
    }

    /// <summary>
    /// Walks <see cref="Layers"/> in original order and assigns each selected
    /// layer the next Fkey starting at <see cref="StartFkey"/>. Unselected and
    /// over-F24 layers get the placeholder. Also keeps <see cref="SelectedCount"/>
    /// in sync so the Generate button enable-state reflects the selection.
    /// </summary>
    private void RecomputeProjections()
    {
        var placeholder = Loc.Instance["Generate_LayerOutOfRange"];
        int position = 0;
        int selected = 0;
        foreach (var item in Layers)
        {
            if (!item.IsSelected)
            {
                item.ProjectedFkey = placeholder;
                continue;
            }
            selected++;
            int fkey = StartFkey + position;
            position++;
            item.ProjectedFkey = fkey > SignalMacroGenerator.MaxFkey
                ? placeholder
                : "F" + fkey.ToString(CultureInfo.InvariantCulture);
        }
        SelectedCount = selected;
    }

    private static List<string> ReadLayerNames(string? path)
    {
        var names = new List<string>();
        if (string.IsNullOrEmpty(path)) return names;
        try
        {
            var json = File.ReadAllText(path);
            if (JsonNode.Parse(json) is JsonObject obj && obj["layer_names"] is JsonArray arr)
            {
                foreach (var node in arr) names.Add(node?.GetValue<string>() ?? "");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Warn("GenerateSignals", $"could not pre-read layer names: {ex.Message}");
        }
        return names;
    }

    private async Task GenerateAsync()
    {
        if (string.IsNullOrEmpty(_loadedPath)) return;
        if (StartFkey < SignalMacroGenerator.MinFkey || StartFkey > SignalMacroGenerator.MaxFkey)
        {
            StatusMessage = Loc.Instance.Format("Generate_StatusFkeyRange",
                SignalMacroGenerator.MinFkey, SignalMacroGenerator.MaxFkey);
            return;
        }

        IsBusy = true;
        try
        {
            string inputJson;
            try
            {
                inputJson = await File.ReadAllTextAsync(_loadedPath);
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.Instance.Format("Generate_StatusReadFailed", ex.Message);
                return;
            }

            var picked = new HashSet<int>(Layers.Where(l => l.IsSelected).Select(l => l.Index));

            SignalMacroGenerator.GenerateResult result;
            try
            {
                result = SignalMacroGenerator.Generate(inputJson,
                    new SignalMacroGenerator.GenerateOptions(StartFkey: StartFkey, LayerIndices: picked));
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.Instance.Format("Generate_StatusGenerateFailed", ex.Message);
                return;
            }

            var defaultName = SuggestOutputName(_loadedPath);
            var savePath = SaveFileRequested is null ? null : await SaveFileRequested(defaultName);
            if (string.IsNullOrEmpty(savePath))
            {
                // User cancelled the save dialog — surface no message, leave VM ready for retry.
                return;
            }

            try
            {
                await AtomicFile.WriteAllTextAsync(savePath, result.OutputJson);
            }
            catch (Exception ex)
            {
                StatusMessage = Loc.Instance.Format("Generate_StatusWriteFailed", ex.Message);
                return;
            }

            AddedItems.Clear();
            foreach (var item in result.Added) AddedItems.Add(item.Name);
            SkippedItems.Clear();
            foreach (var name in result.SkippedExisting) SkippedItems.Add(name);
            Warnings.Clear();
            foreach (var w in result.Warnings) Warnings.Add(w);

            StatusMessage = Loc.Instance.Format("Generate_StatusSuccess",
                result.Added.Count, Path.GetFileName(savePath));
            HasResult = true;
            DiagnosticLog.Info("GenerateSignals",
                $"wrote='{savePath}' added={result.Added.Count} skipped={result.SkippedExisting.Count} warnings={result.Warnings.Count}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string SuggestOutputName(string inputPath)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        return $"{stem}_with_signals{ext}";
    }
}

/// <summary>
/// Per-layer row for the layer-selection list in the Generate Signals window.
/// </summary>
public partial class LayerSelectionItem : ObservableObject
{
    public LayerSelectionItem(int index, string name)
    {
        Index = index;
        Name = name;
    }

    public int Index { get; }
    public string Name { get; }
    public string Label => $"{Index}. {Name}";

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _projectedFkey = "";
}
