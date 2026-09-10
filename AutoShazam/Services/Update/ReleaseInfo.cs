using System.Reflection;

namespace AutoShazam.Services.Update;

/// <summary>Reads version/release metadata embedded in the assembly at build time.</summary>
internal static class ReleaseInfo
{
    public static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "Unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// The date this build was published on GitHub (baked in by the release workflow via
    /// `dotnet publish -p:ReleaseDate=...`), formatted for display. Null for local/dev builds,
    /// which never have this set.
    /// </summary>
    public static string? GetReleaseDate()
    {
        var raw = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ReleaseDate")
            ?.Value;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return DateTime.TryParse(raw, out var date) ? date.ToString("d MMMM yyyy") : raw;
    }
}
