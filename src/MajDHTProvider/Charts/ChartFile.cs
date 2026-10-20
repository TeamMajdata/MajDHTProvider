using MajDHTProvider.Storage;

#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>A chart resource found on disk.</summary>
    /// <param name="Resource">The resource the file stores.</param>
    /// <param name="Path">The full path of the file.</param>
    /// <param name="Stamp">The length and last write time the file had when it was found, taken from the directory listing.</param>
    internal readonly record struct ChartFile(ChartResource Resource, string Path, FileStamp Stamp);
}
