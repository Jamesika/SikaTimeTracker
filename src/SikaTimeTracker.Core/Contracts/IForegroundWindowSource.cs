using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Contracts;

public interface IForegroundWindowSource : IDisposable
{
    event EventHandler<WindowChangedEventArgs>? ForegroundWindowChanged;

    bool CaptureWindowTitles { get; set; }

    WindowSnapshot? GetCurrentWindow();

    void Start();

    void Stop();
}
