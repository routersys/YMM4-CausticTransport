using System.Reflection;
using System.Windows;

namespace CausticTransport.Tests;

public sealed class HostIntegrationTests
{
    static int UpdateChecksStarted()
        => (int)typeof(CausticTransportUpdateNotifier).GetField("_started", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    [Fact]
    public void OutsideAWpfApplicationNoUpdateCheckIsStarted()
    {
        Assert.Null(Application.Current);

        CausticTransportUpdateNotifier.EnsureCheckedOnce();

        Assert.Equal(0, UpdateChecksStarted());
    }

    [Fact]
    public void OutsideAWpfApplicationTheEffectCanStillBeCreated()
    {
        Assert.Null(Application.Current);

        var effect = new CausticTransportEffect();

        Assert.Equal(Texts.CausticTransport, effect.Label);
        Assert.Equal(0, UpdateChecksStarted());
    }
}
