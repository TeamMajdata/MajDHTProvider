using System;
using System.IO;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>
    /// Persists the routing table snapshot of the node, so a restarted provider rejoins the network through the nodes it
    /// already knew even when no bootstrap endpoint answers. The snapshot holds node identifiers and endpoints only, and
    /// losing it costs no more than one bootstrap, so a file that cannot be read or written is logged and never stops the
    /// provider.
    /// </summary>
    internal static class RoutingTableFile
    {
        /// <summary>The file name of the routing table snapshot inside the data directory.</summary>
        public const string FileName = "routing.bin";

        /// <summary>Reads the saved snapshot.</summary>
        /// <param name="path">The snapshot file path.</param>
        /// <param name="logger">Receives a warning when the file exists but cannot be read.</param>
        /// <returns>The snapshot, or null when there is no file or it cannot be read.</returns>
        public static byte[]? TryLoad(string path, ILogger logger)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not read the routing table {Path}; it is ignored.", path);
                return null;
            }
        }

        /// <summary>Writes a snapshot atomically, replacing any earlier snapshot.</summary>
        /// <param name="path">The snapshot file path.</param>
        /// <param name="snapshot">The exported routing table.</param>
        /// <param name="logger">Receives a warning when the file cannot be written.</param>
        /// <returns>Whether the snapshot was written.</returns>
        public static bool Save(string path, byte[] snapshot, ILogger logger)
        {
            var temporary = path + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(temporary, snapshot);
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not save the routing table to {Path}.", path);
                return false;
            }
            finally
            {
                File.Delete(temporary);
            }
        }
    }
}
