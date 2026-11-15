using Silk.NET.Input;
using Valencia.App;
using Valencia.Core;
using Xunit;

namespace Valencia.Tests;

public sealed class KeyboardTests
{
    [Fact]
    public void ACompleteKeyPressBetweenRenderFramesIsDeliveredOnce()
    {
        var keymap = new PlayerKeymap();
        keymap.Record(Key.Space, pressed: true);
        keymap.Record(Key.Space, pressed: false);
        Assert.Equal([TransportAction.TogglePlayback], keymap.Read(0.016));
        Assert.Empty(keymap.Read(0.016));
    }

    [Fact]
    public void RepeatedKeyDownAndFocusResetDoNotReplayAnOldAction()
    {
        var keymap = new PlayerKeymap();
        keymap.Record(Key.O, pressed: true);
        keymap.Record(Key.O, pressed: true);
        Assert.Equal([TransportAction.Open], keymap.Read(0.016));
        keymap.Record(Key.Right, pressed: true);
        keymap.Reset();
        Assert.Empty(keymap.Read(0.016));
    }
}
