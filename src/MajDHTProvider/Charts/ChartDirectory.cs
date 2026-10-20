using System;
using System.Collections.Generic;
using System.IO;

#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>
    /// Reads the chart storage layout <c>&lt;root&gt;/&lt;GUID&gt;/&lt;file&gt;</c>. Only directories named by a hyphenated GUID
    /// and the five exact, case-sensitive resource file names belong to the layout; everything else is ignored.
    /// </summary>
    internal sealed class ChartDirectory
    {
        /// <summary>Creates a view of a chart root directory.</summary>
        /// <param name="rootPath">The chart root directory; it is created when missing.</param>
        /// <exception cref="ArgumentException">The path is empty or invalid.</exception>
        /// <exception cref="IOException">The directory cannot be created.</exception>
        public ChartDirectory(string rootPath)
        {
            RootPath = Path.GetFullPath(rootPath);
            Directory.CreateDirectory(RootPath);
        }

        /// <summary>Gets the full path of the chart root directory.</summary>
        public string RootPath { get; }

        /// <summary>Lists every resource file of every chart directory.</summary>
        /// <returns>The resource files found on disk.</returns>
        /// <exception cref="IOException">The root directory cannot be enumerated.</exception>
        public IReadOnlyList<ChartFile> Scan()
        {
            var files = new List<ChartFile>();
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                if (ChartResource.TryParseChartId(Path.GetFileName(directory), out var chartId))
                {
                    AddChartFiles(chartId, directory, files);
                }
            }
            return files;
        }

        /// <summary>Lists the resource files of one chart.</summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <returns>The resource files found on disk; empty when the chart directory does not exist.</returns>
        /// <exception cref="IOException">The chart directory cannot be enumerated.</exception>
        public IReadOnlyList<ChartFile> ScanChart(Guid chartId)
        {
            var files = new List<ChartFile>();
            var directory = FindChartDirectory(chartId);
            if (directory != null)
            {
                AddChartFiles(chartId, directory, files);
            }
            return files;
        }

        /// <summary>Finds the file that currently stores a resource.</summary>
        /// <param name="resource">The resource.</param>
        /// <returns>The full path, or null when the resource file does not exist.</returns>
        /// <exception cref="IOException">The chart directory cannot be enumerated.</exception>
        public string? FindFile(ChartResource resource)
        {
            foreach (var file in ScanChart(resource.ChartId))
            {
                if (file.Resource == resource)
                {
                    return file.Path;
                }
            }
            return null;
        }

        /// <summary>Gets the path where a resource is stored, reusing an existing chart directory when one exists.</summary>
        /// <param name="resource">The resource.</param>
        /// <returns>The full target path; its directory may not exist yet.</returns>
        /// <exception cref="IOException">The root directory cannot be enumerated.</exception>
        public string GetTargetPath(ChartResource resource)
        {
            var directory = FindChartDirectory(resource.ChartId)
                ?? Path.Combine(RootPath, ChartResource.FormatChartId(resource.ChartId));
            return Path.Combine(directory, resource.FileName);
        }

        /// <summary>
        /// Finds the chart directory, preferring the canonical lowercase name and otherwise any hyphenated GUID spelling.
        /// </summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <returns>The full directory path, or null when the chart has no directory.</returns>
        /// <exception cref="IOException">The root directory cannot be enumerated.</exception>
        public string? FindChartDirectory(Guid chartId)
        {
            var canonical = Path.Combine(RootPath, ChartResource.FormatChartId(chartId));
            if (Directory.Exists(canonical))
            {
                return canonical;
            }
            foreach (var directory in Directory.EnumerateDirectories(RootPath))
            {
                if (ChartResource.TryParseChartId(Path.GetFileName(directory), out var found) && found == chartId)
                {
                    return directory;
                }
            }
            return null;
        }

        /// <summary>Maps any path below the root to the chart whose directory contains it.</summary>
        /// <param name="fullPath">A full path below the chart root.</param>
        /// <param name="chartId">Receives the chart identifier when the first path segment is a chart directory.</param>
        /// <returns>Whether the path belongs to a chart directory.</returns>
        public bool TryGetChartId(string fullPath, out Guid chartId)
        {
            chartId = Guid.Empty;
            var relative = Path.GetRelativePath(RootPath, fullPath);
            if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return false;
            }
            var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
            var firstSegment = separator < 0 ? relative : relative[..separator];
            return ChartResource.TryParseChartId(firstSegment, out chartId);
        }

        /// <summary>Adds the recognized resource files of one chart directory.</summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <param name="directory">The chart directory.</param>
        /// <param name="files">Receives the resource files.</param>
        /// <exception cref="IOException">The directory cannot be enumerated.</exception>
        private static void AddChartFiles(Guid chartId, string directory, List<ChartFile> files)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (ChartResource.TryParseFileName(Path.GetFileName(path), out var kind))
                    {
                        files.Add(new ChartFile(new ChartResource(chartId, kind), path));
                    }
                }
            }
            catch (DirectoryNotFoundException)
            {
                // The directory was removed while it was being scanned; it has no files.
            }
        }
    }
}
