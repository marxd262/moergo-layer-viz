using Avalonia;
using MoergoLayerViz.Core.Diagnostics;
using MoergoLayerViz.Core.Settings;

namespace MoergoLayerViz.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "MoergoLayerViz-SingleInstance", out bool isNew);
        if (!isNew) return;

        DiagnosticLog.LogEnvironment();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

        // Allow users to force software rendering via env var or settings.json.
        if (OperatingSystem.IsWindows())
        {
            var renderMode = Environment.GetEnvironmentVariable("MOERGO_RENDER_MODE");
            var source = "MOERGO_RENDER_MODE env";

            if (string.IsNullOrEmpty(renderMode))
            {
                try
                {
                    renderMode = new SettingsService().Load().RenderingMode;
                    source = "settings.json";
                }
                catch
                {
                    renderMode = "auto";
                    source = "default (settings read failed)";
                }
            }

            if (renderMode?.Equals("software", StringComparison.OrdinalIgnoreCase) == true)
            {
                DiagnosticLog.Info("Startup", $"Rendering mode: software (via {source})");
                builder = builder.With(new Win32PlatformOptions
                {
                    RenderingMode = [Win32RenderingMode.Software]
                });
            }
            else
            {
                DiagnosticLog.Info("Startup", $"Rendering mode: {renderMode ?? "auto"} (via {source})");
            }
        }

        return builder;
    }
}
