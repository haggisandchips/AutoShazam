using System.IO;

namespace AutoShazam.Services.Settings;

/// <summary>
/// Resolves the app's local-appdata storage root. A genuine Velopack-installed release build
/// and a locally-run/dev build are kept in separate folders so dev runs never clobber the real
/// user's settings or trip update-related state meant for the shipped app.
/// </summary>
internal static class AppPaths
{
    public static string GetAppDataRoot(bool isInstalled)
    {
        string folderName = isInstalled ? "AutoShazam" : "AutoShazam-Dev";
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folderName);
        Directory.CreateDirectory(root);
        return root;
    }
}
