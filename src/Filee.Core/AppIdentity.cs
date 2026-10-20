// The name this app runs, stores its data and registers itself under. Foldex (built on Filee, with the PDF workspace)
// keeps its own folders, single-instance name, Explorer entries and installer id, so it installs next to Filee.

namespace Filee.Core;

public static class AppIdentity
{
    /// <summary>Shown to people: window titles, the tray, shortcuts.</summary>
    public const string DisplayName = "Foldex";

    /// <summary>Folder name: %APPDATA%\&lt;Folder&gt; (settings) and %LOCALAPPDATA%\&lt;Folder&gt;\engines.</summary>
    public const string Folder = "Foldex";

    /// <summary>Key for names without spaces: the single-instance mutex and pipe, registry values, the Explorer verb.</summary>
    public const string Key = "Foldex";

    /// <summary>The author's GitHub profile (sidebar).</summary>
    public const string ProfileUrl = "https://github.com/ShinojJerald";

    /// <summary>Support the author (sidebar, About).</summary>
    public const string SupportUrl = "https://buymeacoffee.com/tunekadal";

    /// <summary>
    /// Whether Filee's GitHub releases are this edition's updates. They aren't for Foldex: its installer is a
    /// different app, so the automatic update check stays off (About still shows Filee's latest release on request).
    /// </summary>
    public static bool UpdatesFromFileeReleases => false;
}
