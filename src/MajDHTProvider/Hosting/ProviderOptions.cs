using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using MajDHTNetwork;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>The validated configuration of one provider process.</summary>
    internal sealed class ProviderOptions
    {
        /// <summary>The default TCP and UDP listening port.</summary>
        public const int DefaultPort = 42000;
        /// <summary>The name of the default data directory inside the chart root; it is not a chart directory.</summary>
        public const string DefaultDataDirectoryName = ".majdht";
        /// <summary>The default number of chart resources synchronized at once.</summary>
        public const int DefaultParallelism = 8;

        /// <summary>Gets or sets the provider mode.</summary>
        public ProviderMode Mode { get; set; }

        /// <summary>Gets or sets the chart root directory containing <c>&lt;GUID&gt;/&lt;file&gt;</c> resources; empty in bootstrap mode.</summary>
        public string RootDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the directory for the identity seed, publication ledger, caches and staging files, or null for the
        /// default below the chart root; bootstrap mode has no root and requires it.
        /// </summary>
        public string? DataDirectory { get; set; }

        /// <summary>Gets or sets the trusted publisher fingerprint a replica serves; required in replica mode.</summary>
        public string? PublisherId { get; set; }

        /// <summary>Gets the listen addresses; empty means the IPv4 wildcard address.</summary>
        public List<IPAddress> ListenAddresses { get; } = [];

        /// <summary>
        /// Gets or sets whether the listeners are known to accept unsolicited packets from other networks, for example on a
        /// fixed public address or behind a router port mapping; the node then advertises public reachability at once.
        /// </summary>
        public bool PubliclyReachable { get; set; }

        /// <summary>Gets the endpoints of known nodes used to join the network.</summary>
        public List<IPEndPoint> BootstrapEndpoints { get; } = [];

        /// <summary>Gets or sets the TCP listening port, 0 for an OS-assigned port, or null to disable TCP.</summary>
        public int? TcpPort { get; set; } = DefaultPort;

        /// <summary>Gets or sets the UDP listening port, 0 for an OS-assigned port, or null to disable UDP.</summary>
        public int? UdpPort { get; set; } = DefaultPort;

        /// <summary>
        /// Gets or sets whether the node also acts as a Traversal node: it observes the endpoints of registered peers and
        /// coordinates their direct connections and dial-backs.
        /// </summary>
        public bool EnableTraversal { get; set; }

        /// <summary>
        /// Gets or sets whether the node also acts as a Forwarder: it hosts forwarding sessions and relays end-to-end
        /// encrypted envelopes between peers that cannot connect directly.
        /// </summary>
        public bool EnableForwarder { get; set; }

        /// <summary>Gets or sets the identity work required by the network, 0 to 20 bits; it must match every node.</summary>
        public int IdentityWorkBits { get; set; } = 12;

        /// <summary>Gets or sets how long a publication stays valid; the publisher renews it at half its lifetime.</summary>
        public TimeSpan RecordLifetime { get; set; } = TimeSpan.FromHours(24);

        /// <summary>
        /// Gets or sets how long after a served record last reached a replica the node announces it again, from one second to
        /// 45 minutes. Reannouncements renew the provider claims and wait while publications are replicating.
        /// </summary>
        public TimeSpan RepublishInterval { get; set; } = TimeSpan.FromMinutes(30);

        /// <summary>Gets or sets how often a replica rechecks its resources for withdrawals and updates.</summary>
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Gets or sets how long a replica holds a peer's block request while it pulls the resource from the origin; the
        /// peer fails over to other providers after it. Keep it below the five-second request timeout.
        /// </summary>
        public TimeSpan PullWait { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Gets or sets how long a chart directory must have no file events before it is synchronized.</summary>
        public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Gets or sets the interval between full rescans that catch lost file notifications.</summary>
        public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Gets or sets how many chart resources are read, published, withdrawn, renewed or checked at once, which is also
        /// how many publication replications the node admits at once.
        /// </summary>
        public int Parallelism { get; set; } = DefaultParallelism;

        /// <summary>Gets or sets the byte capacity of the memory cache for blocks not yet backed by a file.</summary>
        public long BlockCacheCapacity { get; set; } = 64L * 1024 * 1024;

        /// <summary>Gets or sets the maximum number of DHT records the node retains.</summary>
        public int MaxRecords { get; set; } = 65536;

        /// <summary>Gets or sets the total Merkle leaves of served content; at 16 KiB blocks the default covers 64 GiB.</summary>
        public int MaxLocalLeaves { get; set; } = 4 * 1024 * 1024;

        /// <summary>Gets or sets the minimum level of log output.</summary>
        public LogLevel LogLevel { get; set; } = LogLevel.Information;

        /// <summary>Gets the full path of the data directory.</summary>
        /// <returns>The configured data directory, or <c>&lt;root&gt;/.majdht</c>.</returns>
        public string GetDataDirectory()
        {
            return Path.GetFullPath(DataDirectory ?? Path.Combine(RootDirectory, DefaultDataDirectoryName));
        }

        /// <summary>Creates the DHT node configuration.</summary>
        /// <param name="logger">Receives DHT node diagnostics.</param>
        /// <returns>
        /// Node options with download and provide capabilities, or none in bootstrap mode, plus the Traversal and Forwarder
        /// capabilities when enabled, whose request budget admits <see cref="Parallelism"/> replications at once.
        /// </returns>
        public DHTNodeOptions CreateNodeOptions(ILogger logger)
        {
            // A bootstrap node advertises no content capability: peers never ask it for blocks and never see it as a provider.
            var capabilities = Mode == ProviderMode.Bootstrap ? default : DHTCapabilities.Download | DHTCapabilities.Provide;
            if (EnableTraversal)
            {
                capabilities |= DHTCapabilities.Traversal;
            }
            if (EnableForwarder)
            {
                capabilities |= DHTCapabilities.Forwarder;
            }
            var nodeOptions = new DHTNodeOptions
            {
                ListenAddresses = ListenAddresses.Count == 0 ? [IPAddress.Any] : [.. ListenAddresses],
                PubliclyReachable = PubliclyReachable,
                BootstrapEndpoints = [.. BootstrapEndpoints],
                TcpPort = TcpPort,
                UdpPort = UdpPort,
                PreferredTransport = TcpPort == null ? DHTTransport.Udp : DHTTransport.Tcp,
                Capabilities = capabilities,
                IdentityWorkBits = IdentityWorkBits,
                RecordLifetime = RecordLifetime,
                RepublishInterval = RepublishInterval,
                MaxRecords = MaxRecords,
                MaxLocalLeaves = MaxLocalLeaves,
                Logger = logger
            };
            ApplyParallelism(nodeOptions, Parallelism);
            return nodeOptions;
        }

        /// <summary>
        /// Raises the request budget of node options until the node admits a number of publication replications at once.
        /// A replication looks up the nodes near the key and near the file hash at the same time, each lookup runs every
        /// disjoint path at full parallelism, and the node admits as many replications as fit into
        /// <see cref="DHTNodeOptions.MaxConcurrentRequests"/>; the budget is never lowered.
        /// </summary>
        /// <param name="nodeOptions">The node options to adjust.</param>
        /// <param name="parallelism">The replications the node must admit at once, at least one.</param>
        /// <exception cref="ArgumentOutOfRangeException">The parallelism is not positive.</exception>
        public static void ApplyParallelism(DHTNodeOptions nodeOptions, int parallelism)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            var replicationFootprint = 2 * nodeOptions.DisjointLookupPaths * nodeOptions.LookupParallelism;
            nodeOptions.MaxConcurrentRequests = Math.Max(nodeOptions.MaxConcurrentRequests, parallelism * replicationFootprint);
        }
    }
}
