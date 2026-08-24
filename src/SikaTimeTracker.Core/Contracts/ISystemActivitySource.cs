using SikaTimeTracker.Core.Models;

namespace SikaTimeTracker.Core.Contracts;

public interface ISystemActivitySource : IDisposable
{
    event EventHandler<SystemActivityChangedEventArgs>? SystemActivityChanged;

    bool IsSessionInteractive { get; }

    TimeSpan GetIdleDuration();

    void Start();

    void Stop();
}
