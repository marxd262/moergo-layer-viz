using System.Collections.ObjectModel;
using System.IO;
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
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsBusy && !string.IsNullOrEmpty(_loadedPath));
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

    public ObservableCollection<string> AddedItems { get; } = new();
    public ObservableCollection<string> SkippedItems { get; } = new();
    public ObservableCollection<string> Warnings { get; } = new();

    public IAsyncRelayCommand GenerateCommand { get; }

    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

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

            SignalMacroGenerator.GenerateResult result;
            try
            {
                result = SignalMacroGenerator.Generate(inputJson,
                    new SignalMacroGenerator.GenerateOptions(StartFkey: StartFkey));
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
