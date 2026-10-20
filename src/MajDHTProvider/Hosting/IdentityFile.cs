using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using MajDHTNetwork;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>
    /// Persists the node identity seed so the node ID and, for a publisher, the key namespace survive restarts. The seed
    /// is a secret: it is never logged and, on Unix, the file is readable by its owner only.
    /// </summary>
    internal static class IdentityFile
    {
        /// <summary>The file name of the identity seed inside the data directory.</summary>
        public const string FileName = "identity.key";

        /// <summary>The length of an identity seed in bytes.</summary>
        private const int SeedLength = 32;

        /// <summary>Loads the identity from a seed file, or creates and saves a new identity when the file does not exist.</summary>
        /// <param name="path">The seed file path.</param>
        /// <param name="workBits">The identity work the network requires.</param>
        /// <param name="logger">Receives progress messages; the seed itself is never logged.</param>
        /// <param name="cancellationToken">Cancels the search for a new identity.</param>
        /// <returns>The persistent identity.</returns>
        /// <exception cref="InvalidDataException">The file is not a seed, or its identity does not reach the required work.</exception>
        /// <exception cref="IOException">The file cannot be read or written.</exception>
        /// <exception cref="OperationCanceledException">Creating an identity was cancelled.</exception>
        public static DHTIdentity LoadOrCreate(string path, int workBits, ILogger logger, CancellationToken cancellationToken)
        {
            if (File.Exists(path))
            {
                var identity = Load(path);
                if (identity.IdentityWork < workBits)
                {
                    throw new InvalidDataException(
                        $"The identity in {path} has {identity.IdentityWork} work bits but the network requires {workBits}.");
                }
                logger.LogInformation("Loaded node identity from {Path}.", path);
                return identity;
            }
            logger.LogInformation("Creating a node identity with {WorkBits} work bits; this can take a while.", workBits);
            var created = DHTIdentity.Create(workBits, cancellationToken);
            Save(path, created);
            logger.LogInformation("Saved the new node identity to {Path}; keep this file secret and back it up.", path);
            return created;
        }

        /// <summary>Reads an identity from a seed file.</summary>
        /// <param name="path">The seed file path.</param>
        /// <returns>The identity.</returns>
        /// <exception cref="InvalidDataException">The file does not contain exactly one seed.</exception>
        /// <exception cref="IOException">The file cannot be read.</exception>
        private static DHTIdentity Load(string path)
        {
            var seed = File.ReadAllBytes(path);
            try
            {
                if (seed.Length != SeedLength)
                {
                    throw new InvalidDataException($"The identity file {path} must contain exactly {SeedLength} bytes.");
                }
                return new DHTIdentity(seed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }

        /// <summary>Writes the identity seed atomically, restricting access on Unix.</summary>
        /// <param name="path">The seed file path.</param>
        /// <param name="identity">The identity to persist.</param>
        /// <exception cref="IOException">The file cannot be written.</exception>
        private static void Save(string path, DHTIdentity identity)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory != null)
            {
                Directory.CreateDirectory(directory);
            }
            var temporary = path + ".tmp";
            var seed = identity.ExportSeed();
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                using (var stream = new FileStream(temporary, options))
                {
                    stream.Write(seed);
                    stream.Flush(true);
                }
                File.Move(temporary, path, false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
                File.Delete(temporary);
            }
        }
    }
}
