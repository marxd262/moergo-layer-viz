using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using MoergoLayerViz.App.ViewModels;

namespace MoergoLayerViz.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    // TextBlock has no built-in click command, so the update-link's
    // PointerPressed handler lives here in the code-behind.
    private void OnUpdateLinkClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && vm.UpdateUrl is { } url)
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
