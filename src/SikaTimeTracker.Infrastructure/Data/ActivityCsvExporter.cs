using System.Globalization;
using System.Text;
using SikaTimeTracker.Core.Contracts;

namespace SikaTimeTracker.Infrastructure.Data;

public sealed class ActivityCsvExporter
{
    private readonly IActivityStore _store;

    public ActivityCsvExporter(IActivityStore store)
    {
        _store = store;
    }

    public async Task<string> ExportAsync(
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(
            destinationDirectory,
            $"SikaTimeTracker-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var activities = await _store.GetAllActivitiesAsync(cancellationToken);
        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteLineAsync("Id,StartTimeUtc,EndTimeUtc,DurationSeconds,ProcessName,WindowTitle,WebsiteDomain,CategoryId,IsManuallyClassified");
        foreach (var activity in activities)
        {
            var values = new[]
            {
                activity.Id.ToString(CultureInfo.InvariantCulture),
                activity.StartTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                activity.EndTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                activity.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                activity.ProcessName,
                activity.WindowTitle,
                activity.WebsiteDomain,
                activity.CategoryId.ToString(CultureInfo.InvariantCulture),
                activity.IsManuallyClassified.ToString(CultureInfo.InvariantCulture)
            };
            await writer.WriteLineAsync(string.Join(',', values.Select(Escape)));
        }

        return path;
    }

    private static string Escape(string value)
    {
        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
