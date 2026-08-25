namespace SikaTimeTracker.Core.Contracts;

public interface IWebsiteDomainResolver
{
    string Resolve(nint windowHandle, string processName);
}
