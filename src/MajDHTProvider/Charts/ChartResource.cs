using System;
using System.Collections.Generic;

#nullable enable

namespace MajDHTProvider.Charts
{
    /// <summary>
    /// Names one resource file of one chart and maps it between the storage layout <c>&lt;GUID&gt;/&lt;file&gt;</c> and the
    /// DHT key name <c>majnet://chart-resources/by-uuid/&lt;GUID&gt;/&lt;suffix&gt;</c>.
    /// </summary>
    /// <param name="ChartId">The chart identifier, formatted in keys as a lowercase hyphenated GUID.</param>
    /// <param name="Kind">The resource file of the chart.</param>
    internal readonly record struct ChartResource(Guid ChartId, ChartResourceKind Kind)
    {
        /// <summary>The prefix shared by every chart resource key name.</summary>
        public const string KeyPrefix = "majnet://chart-resources/by-uuid/";

        /// <summary>The GUID format used for chart directories and key names: 32 hexadecimal digits separated by hyphens.</summary>
        private const string ChartIdFormat = "D";

        /// <summary>Gets every resource kind in a stable order.</summary>
        public static IReadOnlyList<ChartResourceKind> AllKinds { get; } =
        [
            ChartResourceKind.Maidata,
            ChartResourceKind.Track,
            ChartResourceKind.Image,
            ChartResourceKind.ImageFullSize,
            ChartResourceKind.Video
        ];

        /// <summary>Gets the case-sensitive file name of this resource inside its chart directory.</summary>
        public string FileName => GetFileName(Kind);

        /// <summary>Gets the DHT key name of this resource in the publisher namespace.</summary>
        public string KeyName => KeyPrefix + FormatChartId(ChartId) + "/" + GetKeySuffix(Kind);

        /// <summary>Gets the case-sensitive file name that stores a resource kind.</summary>
        /// <param name="kind">The resource kind.</param>
        /// <returns>The file name, for example <c>bg_fullSize.jpg</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The kind is not defined.</exception>
        public static string GetFileName(ChartResourceKind kind)
        {
            return kind switch
            {
                ChartResourceKind.Maidata => "maidata.txt",
                ChartResourceKind.Track => "track.mp3",
                ChartResourceKind.Image => "bg.jpg",
                ChartResourceKind.ImageFullSize => "bg_fullSize.jpg",
                ChartResourceKind.Video => "bg.mp4",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown chart resource kind.")
            };
        }

        /// <summary>Gets the key name suffix that identifies a resource kind.</summary>
        /// <param name="kind">The resource kind.</param>
        /// <returns>The suffix, for example <c>image-fullsize</c>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The kind is not defined.</exception>
        public static string GetKeySuffix(ChartResourceKind kind)
        {
            return kind switch
            {
                ChartResourceKind.Maidata => "maidata",
                ChartResourceKind.Track => "track",
                ChartResourceKind.Image => "image",
                ChartResourceKind.ImageFullSize => "image-fullsize",
                ChartResourceKind.Video => "video",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown chart resource kind.")
            };
        }

        /// <summary>Formats a chart identifier as it appears in directory and key names.</summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <returns>The lowercase hyphenated GUID.</returns>
        public static string FormatChartId(Guid chartId)
        {
            return chartId.ToString(ChartIdFormat);
        }

        /// <summary>Parses a chart directory name; only the hyphenated GUID format is accepted, in either letter case.</summary>
        /// <param name="directoryName">The directory name without any parent path.</param>
        /// <param name="chartId">Receives the chart identifier when the name is valid.</param>
        /// <returns>Whether the name is a chart identifier.</returns>
        public static bool TryParseChartId(string? directoryName, out Guid chartId)
        {
            return Guid.TryParseExact(directoryName, ChartIdFormat, out chartId);
        }

        /// <summary>Maps an exact, case-sensitive file name to its resource kind.</summary>
        /// <param name="fileName">The file name without any directory.</param>
        /// <param name="kind">Receives the resource kind when the name is recognized.</param>
        /// <returns>Whether the file name belongs to the chart layout.</returns>
        public static bool TryParseFileName(string? fileName, out ChartResourceKind kind)
        {
            foreach (var candidate in AllKinds)
            {
                if (string.Equals(fileName, GetFileName(candidate), StringComparison.Ordinal))
                {
                    kind = candidate;
                    return true;
                }
            }
            kind = default;
            return false;
        }

        /// <summary>Parses a key name in the canonical form produced by <see cref="KeyName"/>.</summary>
        /// <param name="keyName">The key name, for example <c>majnet://chart-resources/by-uuid/&lt;GUID&gt;/maidata</c>.</param>
        /// <param name="resource">Receives the named resource when the key name is canonical.</param>
        /// <returns>Whether the key name names a chart resource.</returns>
        public static bool TryParseKeyName(string? keyName, out ChartResource resource)
        {
            resource = default;
            if (keyName == null || !keyName.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            var remainder = keyName.AsSpan(KeyPrefix.Length);
            var separator = remainder.IndexOf('/');
            if (separator < 0)
            {
                return false;
            }
            var chartIdText = remainder[..separator].ToString();
            var suffix = remainder[(separator + 1)..].ToString();
            if (!TryParseChartId(chartIdText, out var chartId) || chartIdText != FormatChartId(chartId))
            {
                return false;
            }
            foreach (var kind in AllKinds)
            {
                if (string.Equals(suffix, GetKeySuffix(kind), StringComparison.Ordinal))
                {
                    resource = new ChartResource(chartId, kind);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Returns the key name, which identifies the resource in logs.</summary>
        /// <returns>The DHT key name.</returns>
        public override string ToString()
        {
            return KeyName;
        }
    }
}
