using MoergoLayerViz.App.Services;
using Xunit;

namespace MoergoLayerViz.Tests;

public class UpdateServiceTests
{
    /// <summary>
    /// Without VelopackApp.Build() having run, UpdateManager's ctor throws
    /// inside the Lazy initializer. The service must catch that, hold a null
    /// manager, and surface every public method as a graceful no-op so unit
    /// tests and dev runs don't crash before the UI loads.
    /// </summary>
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenVelopackNotBootstrapped()
    {
        var svc = new UpdateService();

        Assert.False(svc.IsInstalled);

        var info = await svc.CheckForUpdatesAsync();
        Assert.Null(info);
    }
}
