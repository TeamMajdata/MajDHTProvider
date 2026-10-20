using System;
using System.IO;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>Cheaply identifies a file version by its length and last write time, to skip rehashing unchanged files.</summary>
    /// <param name="Length">The file length in bytes.</param>
    /// <param name="LastWriteTimeUtc">The last write time in UTC.</param>
    internal readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc)
    {
        /// <summary>Reads the current stamp of a file.</summary>
        /// <param name="path">The full file path.</param>
        /// <returns>The stamp of the file as it is now.</returns>
        /// <exception cref="FileNotFoundException">The file does not exist.</exception>
        /// <exception cref="IOException">The file metadata cannot be read.</exception>
        public static FileStamp Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The chart resource file does not exist.", path);
            }
            return new FileStamp(info.Length, info.LastWriteTimeUtc);
        }
    }
}
