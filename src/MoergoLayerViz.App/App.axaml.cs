using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MoergoLayerViz.App.Localization;
using MoergoLayerViz.App.Services;
using MoergoLayerViz.App.ViewModels;
using MoergoLayerViz.App.Views;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App;

public partial class App : Application
{
    private GlobalHotkeyService? _hotkeyService;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                DiagnosticLog.Error("Unhandled", $"AppDomain unhandled: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                DiagnosticLog.Error("Unhandled", $"Unobserved task: {e.Exception}");
                e.SetObserved();
            };
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                DiagnosticLog.Error("Unhandled", $"UI dispatcher: {e.Exception}");
                e.Handled = true;
            };

            var settingsService = new SettingsService();
            var initialSettings = settingsService.Load();
            Loc.Instance.SetCulture(initialSettings.Language);

            var envLevel = Environment.GetEnvironmentVariable("MOERGO_LOG_LEVEL");
            if (!string.IsNullOrEmpty(envLevel) && Enum.TryParse<LogLevel>(envLevel, true, out var envLogLevel))
                DiagnosticLog.SetMinimumLevel(envLogLevel);
            else if (Enum.TryParse<LogLevel>(initialSettings.LogLevel, true, out var logLevel))
                DiagnosticLog.SetMinimumLevel(logLevel);

            DiagnosticLog.Info("Startup", "Creating MainWindowViewModel...");
            var viewModel = new MainWindowViewModel(settingsService);
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            // Restore saved window position/size (or center on first launch)
            if (initialSettings.WindowX.HasValue && initialSettings.WindowY.HasValue)
            {
                mainWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                mainWindow.Position = new PixelPoint((int)initialSettings.WindowX.Value, (int)initialSettings.WindowY.Value);
            }
            else
            {
                mainWindow.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            if (initialSettings.WindowWidth.HasValue)
                mainWindow.Width = initialSettings.WindowWidth.Value;
            if (initialSettings.WindowHeight.HasValue)
                mainWindow.Height = initialSettings.WindowHeight.Value;

            DiagnosticLog.Info("Startup", $"Window position: {mainWindow.Position.X},{mainWindow.Position.Y} size: {mainWindow.Width}x{mainWindow.Height}");

            // Validate restored position is on a visible screen
            mainWindow.Opened += (_, _) =>
            {
                if (mainWindow.Screens.ScreenFromWindow(mainWindow) is null)
                    mainWindow.Position = new PixelPoint(0, 0);
            };

            void SaveWindowState()
            {
                if (mainWindow.WindowState != WindowState.Minimized)
                {
                    var s = settingsService.Load();
                    settingsService.Save(s with
                    {
                        WindowX = mainWindow.Position.X,
                        WindowY = mainWindow.Position.Y,
                        WindowWidth = mainWindow.Width,
                        WindowHeight = mainWindow.Height,
                    });
                }
            }

            var closingHandled = false;
            mainWindow.Closing += (sender, e) =>
            {
                if (closingHandled) return;
                closingHandled = true;
                e.Cancel = true;
                SaveWindowState();
                viewModel.Shutdown();
                ((Window)sender!).Close();
            };

            viewModel.QuitRequested = () =>
            {
                SaveWindowState();
                viewModel.Shutdown();
                Environment.Exit(0);
            };

            // Tray icon: localize menu headers + pipe clicks to the VM
            var trayIcons = TrayIcon.GetIcons(this);
            if (trayIcons?.Count > 0)
            {
                trayIcons[0].Icon = new WindowIcon(
                    AssetLoader.Open(new Uri("avares://MoergoLayerViz.App/Assets/icon.png")));
                LocalizeTrayMenu(trayIcons[0]);
                Loc.CultureChanged += () =>
                    Dispatcher.UIThread.Post(() => LocalizeTrayMenu(trayIcons[0]));
            }

            viewModel.ShowWindowRequested = () =>
            {
                mainWindow.Show();
                mainWindow.Activate();
                if (mainWindow.WindowState == WindowState.Minimized)
                    mainWindow.WindowState = WindowState.Normal;
            };

            viewModel.ToggleWindowRequested = () =>
            {
                if (mainWindow.IsVisible)
                {
                    mainWindow.Hide();
                }
                else
                {
                    mainWindow.Show();
                    mainWindow.Activate();
                    if (mainWindow.WindowState == WindowState.Minimized)
                        mainWindow.WindowState = WindowState.Normal;
                }
            };

            viewModel.LoadLayoutRequested = async () =>
            {
                var file = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Loc.Instance["LoadDialog_Title"],
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(Loc.Instance["LoadDialog_FileType"])
                        {
                            Patterns = ["*.json"]
                        }
                    ],
                });
                if (file.Count > 0)
                    viewModel.LoadLayoutFromPath(file[0].Path.LocalPath);
            };

            viewModel.CopyDiagnosticsRequested = async () =>
            {
                try
                {
                    var report = DiagnosticLog.CollectDiagnosticReport();
                    var clipboard = mainWindow.Clipboard;
                    if (clipboard is not null)
                    {
                        await clipboard.SetTextAsync(report);
                        viewModel.StatusMessage = Loc.Instance["Status_DiagnosticsCopied"];
                    }
                }
                catch (Exception ex)
                {
                    viewModel.StatusMessage = $"Could not copy diagnostics: {ex.Message}";
                }
            };

            // Global show/hide hotkey — Linux/Wayland blocks global hooks from unfocused windows.
            if (!OperatingSystem.IsLinux())
            {
                _hotkeyService = new GlobalHotkeyService();
                try
                {
                    _hotkeyService.Key = GlobalHotkeyService.ParseKey(initialSettings.HotkeyKey);
                    _hotkeyService.Modifiers = GlobalHotkeyService.ParseModifiers(initialSettings.HotkeyModifiers);
                }
                catch
                {
                    // Invalid saved hotkey — use defaults
                }
                _hotkeyService.HotkeyPressed = () =>
                    Dispatcher.UIThread.Post(() => viewModel.ToggleWindowRequested?.Invoke());
                _hotkeyService.Start();
                desktop.Exit += (_, _) => _hotkeyService.Dispose();
            }

            // Restore the last-loaded layout (or show a "pick a file" prompt).
            Dispatcher.UIThread.Post(() => viewModel.InitializeAsync(), DispatcherPriority.Background);

            DataContext = viewModel;
            DiagnosticLog.Info("Startup", "OnFrameworkInitializationCompleted done");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static readonly (string Key, string ResKey)[] TrayMenuKeys =
    [
        ("Show", "Tray_ShowLayers"),
        ("Quit", "Tray_Quit"),
    ];

    private static void LocalizeTrayMenu(TrayIcon trayIcon)
    {
        trayIcon.ToolTipText = Loc.Instance["Tray_Tooltip"];
        if (trayIcon.Menu is not { } menu) return;

        var menuItems = menu.Items.OfType<NativeMenuItem>().ToList();
        for (var i = 0; i < menuItems.Count && i < TrayMenuKeys.Length; i++)
            menuItems[i].Header = Loc.Instance[TrayMenuKeys[i].ResKey];
    }
}
