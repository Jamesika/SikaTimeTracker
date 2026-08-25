using System.Windows.Automation;
using SikaTimeTracker.Core.Contracts;

namespace SikaTimeTracker.Services;

public sealed class BrowserWebsiteDomainResolver : IWebsiteDomainResolver
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "arc",
        "brave",
        "chrome",
        "firefox",
        "msedge",
        "opera",
        "opera_gx",
        "vivaldi"
    };

    private static readonly PropertyCondition EditControlCondition = new(
        AutomationElement.ControlTypeProperty,
        ControlType.Edit);

    public string Resolve(nint windowHandle, string processName)
    {
        if (!BrowserProcesses.Contains(Path.GetFileNameWithoutExtension(processName)))
        {
            return string.Empty;
        }

        try
        {
            var root = AutomationElement.FromHandle(windowHandle);
            var edits = root.FindAll(TreeScope.Descendants, EditControlCondition);
            string? addressBarDomain = null;
            foreach (AutomationElement edit in edits)
            {
                if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern valuePattern
                    && TryGetDomain(valuePattern.Current.Value, out var domain))
                {
                    if (HasExplicitWebScheme(valuePattern.Current.Value))
                    {
                        return domain;
                    }

                    if (LooksLikeAddressBar(edit))
                    {
                        addressBarDomain = domain;
                    }
                }
            }

            return addressBarDomain ?? string.Empty;
        }
        catch (ElementNotAvailableException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }

        return string.Empty;
    }

    internal static bool TryGetDomain(string? address, out string domain)
    {
        domain = string.Empty;
        var candidate = address?.Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        const string viewSourcePrefix = "view-source:";
        if (candidate.StartsWith(viewSourcePrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[viewSourcePrefix.Length..];
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        domain = uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.IdnHost[4..]
            : uri.IdnHost;
        return domain.Length > 0;
    }

    private static bool HasExplicitWebScheme(string value)
    {
        return value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("view-source:http", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAddressBar(AutomationElement element)
    {
        var identity = $"{element.Current.AutomationId} {element.Current.Name}";
        return identity.Contains("address", StringComparison.OrdinalIgnoreCase)
               || identity.Contains("location", StringComparison.OrdinalIgnoreCase)
               || identity.Contains("url", StringComparison.OrdinalIgnoreCase)
               || identity.Contains("地址", StringComparison.OrdinalIgnoreCase);
    }
}
