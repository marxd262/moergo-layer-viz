using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MoergoLayerViz.App.ViewModels;

/// <summary>
/// Transient toast-banner state: a message, a visibility flag, and a
/// self-cancelling auto-dismiss timer. Owned by <see cref="MainWindowViewModel"/>
/// and bound under its <c>Toast</c> property.
/// </summary>
public partial class ToastViewModel : ObservableObject
{
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _isVisible;

    // Cancels the auto-dismiss timer on a re-shown toast or manual dismiss.
    private CancellationTokenSource? _cts;
    private const int ToastDurationMs = 4000;

    /// <summary>
    /// Shows a transient toast banner that auto-dismisses after
    /// <see cref="ToastDurationMs"/>. Re-entry cancels the previous timer so
    /// the new message gets the full display window. Click on the toast
    /// dismisses early via <see cref="DismissCommand"/>.
    /// </summary>
    public void Show(string message)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Message = message;
        IsVisible = true;

        _ = Task.Delay(ToastDurationMs, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_cts == cts)
                {
                    IsVisible = false;
                    _cts = null;
                    cts.Dispose();
                }
            });
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void Dismiss()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        IsVisible = false;
    }
}
