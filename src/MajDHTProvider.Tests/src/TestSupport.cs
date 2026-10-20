using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Charts;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Shared helpers for chart files and loopback DHT nodes.</summary>
    internal static class TestSupport
    {
        /// <summary>Creates loopback TCP options without identity work or NAT traversal.</summary>
        /// <param name="capabilities">The advertised services.</param>
        /// <param name="bootstrap">Started nodes used to join the network.</param>
        /// <returns>Independent node options.</returns>
        public static DHTNodeOptions NodeOptions(DHTCapabilities capabilities, params MajDHTNode[] bootstrap)
        {
            var options = new DHTNodeOptions
            {
                ListenAddresses = [IPAddress.Loopback],
                TcpPort = 0,
                UdpPort = null,
                PreferredTransport = DHTTransport.Tcp,
                Capabilities = capabilities,
                IdentityWorkBits = 0,
                EnableNatTraversal = false,
                RequestTimeout = TimeSpan.FromSeconds(10)
            };
            foreach (var node in bootstrap)
            {
                options.BootstrapEndpoints.Add(EndpointOf(node));
            }
            return options;
        }

        /// <summary>Gets the loopback TCP endpoint of a started node.</summary>
        /// <param name="node">The node.</param>
        /// <returns>The endpoint.</returns>
        public static IPEndPoint EndpointOf(MajDHTNode node)
        {
            return new IPEndPoint(IPAddress.Loopback, node.LocalPeer.Endpoints[0].TcpPort);
        }

        /// <summary>Starts a node and joins every configured bootstrap node.</summary>
        /// <param name="node">The node.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <returns>The started node.</returns>
        public static async Task<MajDHTNode> StartAsync(MajDHTNode node, CancellationToken cancellationToken)
        {
            await node.StartAsync(cancellationToken);
            await node.BootstrapAsync(cancellationToken);
            return node;
        }

        /// <summary>Writes a chart resource file with random content.</summary>
        /// <param name="root">The chart root.</param>
        /// <param name="resource">The resource.</param>
        /// <param name="length">The content length.</param>
        /// <returns>The written content.</returns>
        public static byte[] WriteResource(string root, ChartResource resource, int length)
        {
            var content = RandomNumberGenerator.GetBytes(length);
            WriteResource(root, resource, content);
            return content;
        }

        /// <summary>Writes a chart resource file.</summary>
        /// <param name="root">The chart root.</param>
        /// <param name="resource">The resource.</param>
        /// <param name="content">The file content.</param>
        public static void WriteResource(string root, ChartResource resource, byte[] content)
        {
            var directory = Path.Combine(root, ChartResource.FormatChartId(resource.ChartId));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, resource.FileName), content);
        }

        /// <summary>Gets the canonical path of a chart resource file.</summary>
        /// <param name="root">The chart root.</param>
        /// <param name="resource">The resource.</param>
        /// <returns>The file path.</returns>
        public static string ResourcePath(string root, ChartResource resource)
        {
            return Path.Combine(root, ChartResource.FormatChartId(resource.ChartId), resource.FileName);
        }

        /// <summary>Downloads a resource of a publisher through the DHT.</summary>
        /// <param name="node">The downloading node.</param>
        /// <param name="publisherId">The publisher fingerprint.</param>
        /// <param name="resource">The resource.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <returns>The downloaded content.</returns>
        public static async Task<byte[]> DownloadAsync(MajDHTNode node, string publisherId, ChartResource resource,
            CancellationToken cancellationToken)
        {
            using var destination = new MemoryStream();
            await node.DownloadAsync(new DHTKey(publisherId, resource.KeyName), destination, null, cancellationToken);
            return destination.ToArray();
        }

        /// <summary>Polls a condition until it holds.</summary>
        /// <param name="condition">The condition.</param>
        /// <param name="cancellationToken">The test deadline.</param>
        /// <returns>Completion once the condition holds.</returns>
        /// <exception cref="OperationCanceledException">The deadline passed first.</exception>
        public static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
        {
            while (!await condition())
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        /// <summary>Asserts that two byte sequences are equal without producing a huge failure message.</summary>
        /// <param name="expected">The expected bytes.</param>
        /// <param name="actual">The actual bytes.</param>
        public static void AssertBytes(ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> actual)
        {
            Assert.True(expected.Span.SequenceEqual(actual.Span), $"Expected {expected.Length} bytes but got {actual.Length} different bytes.");
        }
    }
}
