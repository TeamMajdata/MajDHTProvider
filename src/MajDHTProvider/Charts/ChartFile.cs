#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>A chart resource found on disk.</summary>
    /// <param name="Resource">The resource the file stores.</param>
    /// <param name="Path">The full path of the file.</param>
    internal readonly record struct ChartFile(ChartResource Resource, string Path);
}
