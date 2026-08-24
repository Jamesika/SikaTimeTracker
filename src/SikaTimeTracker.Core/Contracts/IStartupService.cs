namespace SikaTimeTracker.Core.Contracts;

public interface IStartupService
{
    bool IsEnabled();

    void SetEnabled(bool enabled, bool startMinimized);
}
