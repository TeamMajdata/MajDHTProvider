using System;
using System.Collections.Generic;
using System.IO;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>A temporary directory deleted after the test.</summary>
    internal sealed class TestDirectory : IDisposable
    {
        /// <summary>Creates an empty temporary directory.</summary>
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MajDHTProvider.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Gets the full directory path.</summary>
        public string Path { get; }

        /// <summary>Creates a subdirectory path, without creating the directory.</summary>
        /// <param name="names">The path segments below the temporary directory.</param>
        /// <returns>The combined path.</returns>
        public string Combine(params IEnumerable<string> names)
        {
            var path = Path;
            foreach (var name in names)
            {
                path = System.IO.Path.Combine(path, name);
            }
            return path;
        }

        /// <summary>Deletes the directory, ignoring files still locked by a stopping component.</summary>
        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary directory does not affect other tests.
            }
        }
    }
}
