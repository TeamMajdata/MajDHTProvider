using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Parses <c>MajDHTProvider &lt;publisher|replica&gt; [options]</c> into validated provider options.</summary>
    internal static class CommandLineParser
    {
        /// <summary>The help text printed for <c>--help</c> and after argument errors.</summary>
        public const string Usage = """
            Usage:
              MajDHTProvider publisher --root <dir> [options]
              MajDHTProvider replica --root <dir> --publisher <id> [options]
              MajDHTProvider bootstrap --data <dir> [options]

            Modes:
              publisher  Publishes every <GUID>/<file> chart resource under the root and withdraws
                         resources whose files or chart directories are removed.
              replica    Serves the trusted publisher's resources from the root. A resource that a
                         peer requests but is missing locally is pulled from the origin, stored and
                         forwarded; local files are deleted only when the publisher withdraws them.
              bootstrap  Joins the network only to introduce peers to each other: keeps a routing
                         table, answers discovery requests and indexes records, and with --traversal
                         or --forwarder assists peers behind NAT. It publishes and serves no content
                         and needs no root.

            Common options:
              --root <dir>                 Chart root directory (required by publisher and replica).
              --data <dir>                 Identity, ledger, cache and staging directory (default: <root>/.majdht;
                                           required by bootstrap).
              --listen <ip>                Listen address; give one IPv4 and one IPv6 address for dual stack
                                           (default: 0.0.0.0).
              --public                     The listeners accept packets from other networks, for example on a
                                           fixed public address or behind a router port mapping; skips the
                                           reachability checks.
              --bootstrap <ip:port>        Known node used to join the network; repeatable, at most 16.
              --tcp-port <port|none>       TCP port, 0 for any free port (default: 42000).
              --udp-port <port|none>       UDP port, 0 for any free port (default: 42000).
              --traversal                  Also coordinate direct connections and dial-backs for peers behind NAT.
              --forwarder                  Also relay encrypted traffic between peers that cannot connect directly.
              --work-bits <0-20>           Identity work required by the network (default: 12).
              --quiet-period <duration>    Wait after the last file event of a chart (default: 2s).
              --rescan-interval <duration> Interval of full directory rescans (default: 10m).
              --parallelism <n>            Chart resources synchronized at once, 1-64 (default: 8).
              --max-records <n>            DHT records retained by the node, 1-65536 (default: 65536).
              --max-local-leaves <n>       Merkle leaves of served content, 1-16777216 (default: 4194304,
                                           64 GiB at 16 KiB blocks).
              --cache-mb <n>               Memory cache for blocks not yet stored in a file (default: 64).
              --log-level <level>          debug, info, warning or error (default: info).
              -h, --help                   Show this help.

            Publisher options:
              --record-lifetime <duration> Publication lifetime, renewed at half-life, 1s-7d (default: 24h).

            Replica options:
              --publisher <id>             Trusted publisher fingerprint, 64 hexadecimal digits (required).
              --refresh-interval <duration> Interval of withdrawal and update checks (default: 10m).
              --pull-wait <duration>       How long a peer request waits for an origin pull (default: 3s).

            Durations: 500ms, 30s, 10m, 24h, 7d or hh:mm:ss.
            """;

        /// <summary>Parses the process arguments.</summary>
        /// <param name="args">The arguments after the executable name.</param>
        /// <returns>The validated options, or null when help was requested.</returns>
        /// <exception cref="CommandLineException">An argument is missing, unknown or invalid.</exception>
        public static ProviderOptions? Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0)
            {
                throw new CommandLineException("A mode is required: publisher or replica.");
            }
            if (IsHelp(args[0]))
            {
                return null;
            }
            var options = new ProviderOptions
            {
                Mode = args[0].ToLowerInvariant() switch
                {
                    "publisher" => ProviderMode.Publisher,
                    "replica" => ProviderMode.Replica,
                    "bootstrap" => ProviderMode.Bootstrap,
                    _ => throw new CommandLineException($"Unknown mode '{args[0]}'; expected publisher, replica or bootstrap.")
                }
            };
            var chartOnly = new List<string>();
            var replicaOnly = new List<string>();
            var publisherOnly = new List<string>();
            for (var index = 1; index < args.Count; index++)
            {
                var argument = args[index];
                if (IsHelp(argument))
                {
                    return null;
                }
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new CommandLineException($"Unexpected argument '{argument}'.");
                }
                var separator = argument.IndexOf('=');
                var name = separator < 0 ? argument : argument[..separator];
                var inlineValue = separator < 0 ? null : argument[(separator + 1)..];
                if (TryApplyFlag(options, name, inlineValue))
                {
                    continue;
                }
                var value = inlineValue ?? TakeNextValue(args, ref index, name);
                switch (name)
                {
                    case "--root":
                        options.RootDirectory = value;
                        chartOnly.Add(name);
                        break;
                    case "--data":
                        options.DataDirectory = value;
                        break;
                    case "--publisher":
                        options.PublisherId = ParsePublisherId(value);
                        replicaOnly.Add(name);
                        break;
                    case "--listen":
                        options.ListenAddresses.Add(ParseAddress(name, value));
                        break;
                    case "--bootstrap":
                        options.BootstrapEndpoints.Add(ParseEndpoint(value));
                        break;
                    case "--tcp-port":
                        options.TcpPort = ParsePort(name, value);
                        break;
                    case "--udp-port":
                        options.UdpPort = ParsePort(name, value);
                        break;
                    case "--work-bits":
                        options.IdentityWorkBits = ParseInteger(name, value, 0, 20);
                        break;
                    case "--quiet-period":
                        options.QuietPeriod = ParseDuration(name, value);
                        chartOnly.Add(name);
                        break;
                    case "--rescan-interval":
                        options.RescanInterval = ParseDuration(name, value);
                        chartOnly.Add(name);
                        break;
                    case "--parallelism":
                        options.Parallelism = ParseInteger(name, value, 1, 64);
                        chartOnly.Add(name);
                        break;
                    case "--max-records":
                        options.MaxRecords = ParseInteger(name, value, 1, 65536);
                        break;
                    case "--max-local-leaves":
                        options.MaxLocalLeaves = ParseInteger(name, value, 1, 16777216);
                        chartOnly.Add(name);
                        break;
                    case "--cache-mb":
                        options.BlockCacheCapacity = ParseInteger(name, value, 1, 65536) * 1024L * 1024;
                        chartOnly.Add(name);
                        break;
                    case "--log-level":
                        options.LogLevel = ParseLogLevel(value);
                        break;
                    case "--record-lifetime":
                        options.RecordLifetime = ParseDuration(name, value);
                        publisherOnly.Add(name);
                        break;
                    case "--refresh-interval":
                        options.RefreshInterval = ParseDuration(name, value);
                        replicaOnly.Add(name);
                        break;
                    case "--pull-wait":
                        options.PullWait = ParseDuration(name, value);
                        replicaOnly.Add(name);
                        break;
                    default:
                        throw new CommandLineException($"Unknown option '{name}'.");
                }
            }
            var foreignOptions = options.Mode switch
            {
                ProviderMode.Publisher => replicaOnly,
                ProviderMode.Replica => publisherOnly,
                _ => [.. chartOnly, .. publisherOnly, .. replicaOnly]
            };
            Validate(options, foreignOptions);
            return options;
        }

        /// <summary>Parses a duration such as <c>500ms</c>, <c>30s</c>, <c>10m</c>, <c>24h</c>, <c>7d</c> or <c>hh:mm:ss</c>.</summary>
        /// <param name="name">The option name used in error messages.</param>
        /// <param name="value">The duration text.</param>
        /// <returns>The positive duration.</returns>
        /// <exception cref="CommandLineException">The duration is malformed or not positive.</exception>
        public static TimeSpan ParseDuration(string name, string value)
        {
            var units = new (string Suffix, double Ticks)[]
            {
                ("ms", TimeSpan.TicksPerMillisecond),
                ("s", TimeSpan.TicksPerSecond),
                ("m", TimeSpan.TicksPerMinute),
                ("h", TimeSpan.TicksPerHour),
                ("d", TimeSpan.TicksPerDay)
            };
            TimeSpan? duration = null;
            foreach (var (suffix, ticks) in units)
            {
                if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(value[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                    && double.IsFinite(amount) && Math.Abs(amount * ticks) < TimeSpan.MaxValue.Ticks)
                {
                    duration = TimeSpan.FromTicks((long)(amount * ticks));
                    break;
                }
            }
            if (duration == null && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }
            if (duration == null || duration.Value <= TimeSpan.Zero)
            {
                throw new CommandLineException($"{name} requires a positive duration such as 30s, 10m or 24h.");
            }
            return duration.Value;
        }

        /// <summary>Checks the combination of options.</summary>
        /// <param name="options">The parsed options.</param>
        /// <param name="foreignOptions">Options given that belong to the other mode.</param>
        /// <exception cref="CommandLineException">The combination is invalid.</exception>
        private static void Validate(ProviderOptions options, List<string> foreignOptions)
        {
            if (foreignOptions.Count != 0)
            {
                throw new CommandLineException($"{foreignOptions[0]} is not valid in {options.Mode.ToString().ToLowerInvariant()} mode.");
            }
            if (options.Mode == ProviderMode.Bootstrap && string.IsNullOrWhiteSpace(options.DataDirectory))
            {
                throw new CommandLineException("--data is required in bootstrap mode.");
            }
            if (options.Mode != ProviderMode.Bootstrap && string.IsNullOrWhiteSpace(options.RootDirectory))
            {
                throw new CommandLineException("--root is required.");
            }
            if (options.Mode == ProviderMode.Replica && options.PublisherId == null)
            {
                throw new CommandLineException("--publisher is required in replica mode.");
            }
            if (options.TcpPort == null && options.UdpPort == null)
            {
                throw new CommandLineException("At least one of TCP and UDP must stay enabled.");
            }
            if (options.ListenAddresses.Count > 2)
            {
                throw new CommandLineException("--listen accepts at most one IPv4 and one IPv6 address.");
            }
            if (options.BootstrapEndpoints.Count > 16)
            {
                throw new CommandLineException("--bootstrap accepts at most 16 endpoints.");
            }
            if (options.RecordLifetime < TimeSpan.FromSeconds(1) || options.RecordLifetime > TimeSpan.FromDays(7))
            {
                throw new CommandLineException("--record-lifetime must be between 1s and 7d.");
            }
        }

        /// <summary>Applies an option that takes no value, such as <c>--public</c>.</summary>
        /// <param name="options">The options being filled.</param>
        /// <param name="name">The option name.</param>
        /// <param name="inlineValue">The value given after <c>=</c>, or null.</param>
        /// <returns>Whether the name is a flag; false for options that take a value.</returns>
        /// <exception cref="CommandLineException">A flag was given a value.</exception>
        private static bool TryApplyFlag(ProviderOptions options, string name, string? inlineValue)
        {
            Action<ProviderOptions>? apply = name switch
            {
                "--public" => static target => target.PubliclyReachable = true,
                "--traversal" => static target => target.EnableTraversal = true,
                "--forwarder" => static target => target.EnableForwarder = true,
                _ => null
            };
            if (apply == null)
            {
                return false;
            }
            if (inlineValue != null)
            {
                throw new CommandLineException($"{name} does not take a value.");
            }
            apply(options);
            return true;
        }

        /// <summary>Checks whether an argument requests help.</summary>
        /// <param name="argument">The argument.</param>
        /// <returns>Whether the argument is a help switch.</returns>
        private static bool IsHelp(string argument)
        {
            return argument is "-h" or "--help" or "help";
        }

        /// <summary>Takes the argument after an option as its value.</summary>
        /// <param name="args">All arguments.</param>
        /// <param name="index">The option index, advanced past the value.</param>
        /// <param name="name">The option name used in error messages.</param>
        /// <returns>The value.</returns>
        /// <exception cref="CommandLineException">The value is missing.</exception>
        private static string TakeNextValue(IReadOnlyList<string> args, ref int index, string name)
        {
            if (index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CommandLineException($"{name} requires a value.");
            }
            index++;
            return args[index];
        }

        /// <summary>Parses and normalizes a publisher fingerprint.</summary>
        /// <param name="value">The fingerprint text.</param>
        /// <returns>The lowercase fingerprint.</returns>
        /// <exception cref="CommandLineException">The text is not 64 hexadecimal digits.</exception>
        private static string ParsePublisherId(string value)
        {
            if (value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            {
                throw new CommandLineException("--publisher requires a 64-digit hexadecimal publisher fingerprint.");
            }
            return value.ToLowerInvariant();
        }

        /// <summary>Parses an IP address.</summary>
        /// <param name="name">The option name used in error messages.</param>
        /// <param name="value">The address text.</param>
        /// <returns>The address.</returns>
        /// <exception cref="CommandLineException">The text is not an IP address.</exception>
        private static IPAddress ParseAddress(string name, string value)
        {
            if (!IPAddress.TryParse(value, out var address))
            {
                throw new CommandLineException($"{name} requires an IP address, not '{value}'.");
            }
            return address;
        }

        /// <summary>Parses an <c>ip:port</c> or <c>[ipv6]:port</c> endpoint.</summary>
        /// <param name="value">The endpoint text.</param>
        /// <returns>The endpoint.</returns>
        /// <exception cref="CommandLineException">The text is not an endpoint with a non-zero port.</exception>
        private static IPEndPoint ParseEndpoint(string value)
        {
            if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0)
            {
                throw new CommandLineException($"--bootstrap requires an ip:port endpoint, not '{value}'.");
            }
            return endpoint;
        }

        /// <summary>Parses a port, or <c>none</c> to disable a transport.</summary>
        /// <param name="name">The option name used in error messages.</param>
        /// <param name="value">The port text.</param>
        /// <returns>The port, or null when disabled.</returns>
        /// <exception cref="CommandLineException">The text is not a port.</exception>
        private static int? ParsePort(string name, string value)
        {
            if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return ParseInteger(name, value, 0, 65535);
        }

        /// <summary>Parses a bounded integer.</summary>
        /// <param name="name">The option name used in error messages.</param>
        /// <param name="value">The integer text.</param>
        /// <param name="minimum">The smallest accepted value.</param>
        /// <param name="maximum">The largest accepted value.</param>
        /// <returns>The integer.</returns>
        /// <exception cref="CommandLineException">The text is not an integer in range.</exception>
        private static int ParseInteger(string name, string value, int minimum, int maximum)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
            {
                throw new CommandLineException($"{name} requires an integer from {minimum} to {maximum}.");
            }
            return result;
        }

        /// <summary>Parses a log level name.</summary>
        /// <param name="value">The level name.</param>
        /// <returns>The log level.</returns>
        /// <exception cref="CommandLineException">The name is unknown.</exception>
        private static LogLevel ParseLogLevel(string value)
        {
            return value.ToLowerInvariant() switch
            {
                "debug" => LogLevel.Debug,
                "info" or "information" => LogLevel.Information,
                "warn" or "warning" => LogLevel.Warning,
                "error" => LogLevel.Error,
                _ => throw new CommandLineException("--log-level must be debug, info, warning or error.")
            };
        }
    }
}
