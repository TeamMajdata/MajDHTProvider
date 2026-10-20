using System;
using System.IO;
using System.Net;
using MajDHTNetwork;
using MajDHTProvider.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks command-line parsing and the derived node configuration.</summary>
    public sealed class CommandLineParserTests
    {
        /// <summary>A valid publisher fingerprint.</summary>
        private const string PublisherId = "ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

        /// <summary>Applies defaults to a minimal publisher command line.</summary>
        [Fact]
        public void PublisherDefaults()
        {
            var options = CommandLineParser.Parse(["publisher", "--root", "charts"])!;
            Assert.Equal(ProviderMode.Publisher, options.Mode);
            Assert.Equal("charts", options.RootDirectory);
            Assert.Equal(Path.GetFullPath(Path.Combine("charts", ".majdht")), options.GetDataDirectory());
            Assert.Equal(ProviderOptions.DefaultPort, options.TcpPort);
            Assert.Equal(ProviderOptions.DefaultPort, options.UdpPort);
            Assert.Equal(TimeSpan.FromHours(24), options.RecordLifetime);
            Assert.Equal(LogLevel.Information, options.LogLevel);
            var node = options.CreateNodeOptions(NullLogger.Instance);
            Assert.Equal([IPAddress.Any], node.ListenAddresses);
            Assert.Equal(DHTCapabilities.Download | DHTCapabilities.Provide, node.Capabilities);
            Assert.Equal(12, node.IdentityWorkBits);
            Assert.Equal(4 * 1024 * 1024, node.MaxLocalLeaves);
        }

        /// <summary>Parses every replica option, including repeated and inline values.</summary>
        [Fact]
        public void ReplicaOptions()
        {
            var options = CommandLineParser.Parse(
            [
                "replica", "--root=cache", "--publisher", PublisherId, "--data", "state",
                "--listen", "0.0.0.0", "--listen", "::", "--advertise", "203.0.113.7",
                "--bootstrap", "198.51.100.1:42000", "--bootstrap=[2001:db8::1]:42001",
                "--tcp-port", "0", "--udp-port", "none", "--relay", "--work-bits", "0",
                "--quiet-period", "500ms", "--rescan-interval", "1h", "--refresh-interval", "00:05:00", "--pull-wait", "2s",
                "--max-records", "100", "--max-local-leaves", "1000", "--cache-mb", "8", "--log-level", "debug"
            ])!;
            Assert.Equal(ProviderMode.Replica, options.Mode);
            Assert.Equal(PublisherId.ToLowerInvariant(), options.PublisherId);
            Assert.Equal(Path.GetFullPath("state"), options.GetDataDirectory());
            Assert.Equal(2, options.ListenAddresses.Count);
            Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(options.AdvertisedAddresses));
            Assert.Equal(IPEndPoint.Parse("[2001:db8::1]:42001"), options.BootstrapEndpoints[1]);
            Assert.Equal(0, options.TcpPort);
            Assert.Null(options.UdpPort);
            Assert.True(options.EnableRelay);
            Assert.Equal(TimeSpan.FromMilliseconds(500), options.QuietPeriod);
            Assert.Equal(TimeSpan.FromHours(1), options.RescanInterval);
            Assert.Equal(TimeSpan.FromMinutes(5), options.RefreshInterval);
            Assert.Equal(TimeSpan.FromSeconds(2), options.PullWait);
            Assert.Equal(8L * 1024 * 1024, options.BlockCacheCapacity);
            Assert.Equal(LogLevel.Debug, options.LogLevel);
            var node = options.CreateNodeOptions(NullLogger.Instance);
            Assert.Equal(DHTCapabilities.Download | DHTCapabilities.Provide | DHTCapabilities.Relay, node.Capabilities);
            Assert.Equal(DHTTransport.Tcp, node.PreferredTransport);
            Assert.Equal(100, node.MaxRecords);
            Assert.Equal(1000, node.MaxLocalLeaves);
        }

        /// <summary>Returns null for every help spelling.</summary>
        /// <param name="argument">The help argument.</param>
        [Theory]
        [InlineData("--help")]
        [InlineData("-h")]
        [InlineData("help")]
        public void HelpIsRecognized(string argument)
        {
            Assert.Null(CommandLineParser.Parse([argument]));
            Assert.Null(CommandLineParser.Parse(["publisher", "--root", "x", argument]));
        }

        /// <summary>Rejects invalid command lines with a message.</summary>
        /// <param name="commandLine">The space-separated arguments.</param>
        [Theory]
        [InlineData("")]
        [InlineData("mirror --root x")]
        [InlineData("publisher")]
        [InlineData("publisher --root")]
        [InlineData("publisher --root x --unknown 1")]
        [InlineData("publisher --root x stray")]
        [InlineData("publisher --root x --publisher " + PublisherId)]
        [InlineData("publisher --root x --pull-wait 1s")]
        [InlineData("replica --root x")]
        [InlineData("replica --root x --publisher abc")]
        [InlineData("replica --root x --publisher " + PublisherId + " --record-lifetime 1h")]
        [InlineData("publisher --root x --tcp-port none --udp-port none")]
        [InlineData("publisher --root x --tcp-port 70000")]
        [InlineData("publisher --root x --work-bits 21")]
        [InlineData("publisher --root x --listen localhost")]
        [InlineData("publisher --root x --bootstrap 127.0.0.1")]
        [InlineData("publisher --root x --quiet-period soon")]
        [InlineData("publisher --root x --quiet-period 0s")]
        [InlineData("publisher --root x --record-lifetime 8d")]
        [InlineData("publisher --root x --log-level verbose")]
        [InlineData("publisher --root x --relay=yes")]
        public void InvalidCommandLinesAreRejected(string commandLine)
        {
            var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var exception = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(args));
            Assert.False(string.IsNullOrWhiteSpace(exception.Message));
        }

        /// <summary>Parses every duration unit.</summary>
        /// <param name="text">The duration text.</param>
        /// <param name="milliseconds">The expected duration in milliseconds.</param>
        [Theory]
        [InlineData("250ms", 250)]
        [InlineData("1.5s", 1500)]
        [InlineData("2m", 120000)]
        [InlineData("1h", 3600000)]
        [InlineData("1d", 86400000)]
        [InlineData("00:00:03", 3000)]
        public void DurationsAreParsed(string text, double milliseconds)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), CommandLineParser.ParseDuration("--test", text));
        }
    }
}
