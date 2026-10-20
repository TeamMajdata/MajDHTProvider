using System;
using System.IO;
using System.Linq;
using MajDHTProvider.Charts;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks the mapping between the storage layout and DHT key names.</summary>
    public sealed class ChartLayoutTests
    {
        /// <summary>A fixed chart identifier.</summary>
        private static readonly Guid s_chartId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        /// <summary>Maps every resource kind to its file name and key name.</summary>
        /// <param name="kindName">The name of the resource kind.</param>
        /// <param name="fileName">The expected file name.</param>
        /// <param name="suffix">The expected key name suffix.</param>
        [Theory]
        [InlineData(nameof(ChartResourceKind.Maidata), "maidata.txt", "maidata")]
        [InlineData(nameof(ChartResourceKind.Track), "track.mp3", "track")]
        [InlineData(nameof(ChartResourceKind.Image), "bg.jpg", "image")]
        [InlineData(nameof(ChartResourceKind.ImageFullSize), "bg_fullSize.jpg", "image-fullsize")]
        [InlineData(nameof(ChartResourceKind.Video), "bg.mp4", "video")]
        public void ResourcesMapToFileNamesAndKeyNames(string kindName, string fileName, string suffix)
        {
            var kind = Enum.Parse<ChartResourceKind>(kindName);
            var resource = new ChartResource(s_chartId, kind);
            Assert.Equal(fileName, resource.FileName);
            Assert.Equal($"majnet://chart-resources/by-uuid/0f8fad5b-d9cb-469f-a165-70867728950e/{suffix}", resource.KeyName);
            Assert.True(ChartResource.TryParseKeyName(resource.KeyName, out var parsed));
            Assert.Equal(resource, parsed);
            Assert.True(ChartResource.TryParseFileName(fileName, out var parsedKind));
            Assert.Equal(kind, parsedKind);
        }

        /// <summary>Rejects key names that are not canonical chart resource keys.</summary>
        /// <param name="keyName">The rejected key name.</param>
        [Theory]
        [InlineData("songs/example")]
        [InlineData("majnet://chart-resources/by-uuid/0f8fad5b-d9cb-469f-a165-70867728950e")]
        [InlineData("majnet://chart-resources/by-uuid/0f8fad5b-d9cb-469f-a165-70867728950e/audio")]
        [InlineData("majnet://chart-resources/by-uuid/0F8FAD5B-D9CB-469F-A165-70867728950E/maidata")]
        [InlineData("majnet://chart-resources/by-uuid/0f8fad5bd9cb469fa16570867728950e/maidata")]
        [InlineData("majnet://chart-resources/by-uuid/0f8fad5b-d9cb-469f-a165-70867728950e/maidata/extra")]
        public void NonCanonicalKeyNamesAreRejected(string keyName)
        {
            Assert.False(ChartResource.TryParseKeyName(keyName, out _));
        }

        /// <summary>Accepts only the exact file names and hyphenated GUID directory names.</summary>
        [Fact]
        public void OnlyTheExactLayoutIsRecognized()
        {
            Assert.False(ChartResource.TryParseFileName("Maidata.txt", out _));
            Assert.False(ChartResource.TryParseFileName("bg_fullsize.jpg", out _));
            Assert.False(ChartResource.TryParseFileName("bg.png", out _));
            Assert.True(ChartResource.TryParseChartId("0F8FAD5B-D9CB-469F-A165-70867728950E", out var upper));
            Assert.Equal(s_chartId, upper);
            Assert.False(ChartResource.TryParseChartId("0f8fad5bd9cb469fa16570867728950e", out _));
            Assert.False(ChartResource.TryParseChartId("{0f8fad5b-d9cb-469f-a165-70867728950e}", out _));
            Assert.False(ChartResource.TryParseChartId(".majdht", out _));
        }

        /// <summary>Scans only resource files inside chart directories.</summary>
        [Fact]
        public void ScanIgnoresFilesOutsideTheLayout()
        {
            using var root = new TestDirectory();
            var charts = new ChartDirectory(root.Path);
            var second = Guid.NewGuid();
            TestSupport.WriteResource(root.Path, new ChartResource(s_chartId, ChartResourceKind.Maidata), 10);
            TestSupport.WriteResource(root.Path, new ChartResource(s_chartId, ChartResourceKind.Video), 10);
            File.WriteAllText(root.Combine(ChartResource.FormatChartId(s_chartId), "notes.txt"), "ignored");
            Directory.CreateDirectory(root.Combine(ChartResource.FormatChartId(s_chartId), "nested"));
            File.WriteAllText(root.Combine(ChartResource.FormatChartId(s_chartId), "nested", "maidata.txt"), "ignored");
            Directory.CreateDirectory(root.Combine("not-a-chart"));
            File.WriteAllText(root.Combine("not-a-chart", "maidata.txt"), "ignored");
            File.WriteAllText(root.Combine("maidata.txt"), "ignored");
            var upperDirectory = root.Combine(ChartResource.FormatChartId(second).ToUpperInvariant());
            Directory.CreateDirectory(upperDirectory);
            File.WriteAllText(Path.Combine(upperDirectory, "track.mp3"), "track");

            var files = charts.Scan().OrderBy(file => file.Resource.KeyName, StringComparer.Ordinal).ToList();

            Assert.Equal(3, files.Count);
            Assert.Contains(files, file => file.Resource == new ChartResource(s_chartId, ChartResourceKind.Maidata));
            Assert.Contains(files, file => file.Resource == new ChartResource(s_chartId, ChartResourceKind.Video));
            Assert.Contains(files, file => file.Resource == new ChartResource(second, ChartResourceKind.Track));
            var track = charts.FindFile(new ChartResource(second, ChartResourceKind.Track));
            Assert.NotNull(track);
            Assert.Equal(Path.Combine(upperDirectory, "track.mp3"), track.Value.Path, ignoreCase: OperatingSystem.IsWindows());
            // The stamp a scan reports must be the one a metadata query reports, so unchanged files are recognized without one.
            Assert.Equal(MajDHTProvider.Storage.FileStamp.Read(track.Value.Path), track.Value.Stamp);
            Assert.Null(charts.FindFile(new ChartResource(s_chartId, ChartResourceKind.Image)));
            Assert.Empty(charts.ScanChart(Guid.NewGuid()));
        }

        /// <summary>Maps changed paths to the chart directory containing them.</summary>
        [Fact]
        public void ChangedPathsMapToTheirChart()
        {
            using var root = new TestDirectory();
            var charts = new ChartDirectory(root.Path);
            var directory = root.Combine(ChartResource.FormatChartId(s_chartId));
            Assert.True(charts.TryGetChartId(directory, out var chartId));
            Assert.Equal(s_chartId, chartId);
            Assert.True(charts.TryGetChartId(Path.Combine(directory, "nested", "file.bin"), out chartId));
            Assert.Equal(s_chartId, chartId);
            Assert.False(charts.TryGetChartId(root.Combine(".majdht", "staging", "x.part"), out _));
            Assert.False(charts.TryGetChartId(root.Path, out _));
            Assert.False(charts.TryGetChartId(Path.GetTempPath(), out _));
        }

        /// <summary>Reuses an existing chart directory spelling and otherwise targets the canonical name.</summary>
        [Fact]
        public void TargetPathsReuseExistingChartDirectories()
        {
            using var root = new TestDirectory();
            var charts = new ChartDirectory(root.Path);
            var resource = new ChartResource(s_chartId, ChartResourceKind.Image);
            Assert.Equal(TestSupport.ResourcePath(root.Path, resource), charts.GetTargetPath(resource));
            var upper = root.Combine(ChartResource.FormatChartId(s_chartId).ToUpperInvariant());
            Directory.CreateDirectory(upper);
            Assert.Equal(Path.Combine(charts.FindChartDirectory(s_chartId)!, "bg.jpg"), charts.GetTargetPath(resource));
        }
    }
}
