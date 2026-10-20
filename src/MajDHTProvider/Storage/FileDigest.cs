#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>The content identity of an indexed file.</summary>
    /// <param name="FileHash">The lowercase hexadecimal SHA-256 of the complete file, as signed in DHT records.</param>
    /// <param name="Length">The file length in bytes.</param>
    internal readonly record struct FileDigest(string FileHash, long Length);
}
