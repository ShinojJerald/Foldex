// Caches engines keep between conversions (typefaces looked up for HWP and CAD text). The app releases them while no
// conversion runs, so a Filee waiting in the tray gives the memory back. Skia's own caches stay: they are shared with
// the UI's render thread, and purging them while it draws crashed in native code (SKGraphics.PurgeAllCaches).

using Filee.Engines.Cad;
using Filee.Engines.Hwp;

namespace Filee.Engines.Infrastructure;

public static class EngineCaches
{
    /// <summary>
    /// Drops what the engines cache between conversions. Call only while no conversion runs: the next one looks
    /// everything up again.
    /// </summary>
    public static void Release()
    {
        RhwpFonts.ReleaseCache();
        CadFonts.ReleaseCache();
    }
}
