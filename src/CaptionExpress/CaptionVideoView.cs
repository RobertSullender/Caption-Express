using Avalonia.Platform;
using LibVLCSharp.Avalonia;

namespace CaptionExpress;

/// <summary>Binds LibVLC after Avalonia creates the native child window.</summary>
public sealed class CaptionVideoView : VideoView
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        if (OperatingSystem.IsLinux() && MediaPlayer is { } player)
            player.XWindow = unchecked((uint)handle.Handle.ToInt64());
        return handle;
    }
}
