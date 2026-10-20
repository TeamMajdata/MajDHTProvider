using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MajDHTProvider.Hosting;
using Microsoft.Extensions.Logging;

#nullable enable

namespace MajDHTProvider
{
    /// <summary>The command-line entry point.</summary>
    internal static class Program
    {
        /// <summary>Exit code for a clean stop.</summary>
        private const int ExitSuccess = 0;
        /// <summary>Exit code for an unrecoverable runtime failure.</summary>
        private const int ExitFailure = 1;
        /// <summary>Exit code for invalid arguments.</summary>
        private const int ExitUsage = 2;

        /// <summary>Parses the arguments and runs the provider until Ctrl+C or SIGTERM.</summary>
        /// <param name="args">The command-line arguments.</param>
        /// <returns>0 after a clean stop, 1 after a failure, 2 for invalid arguments.</returns>
        private static async Task<int> Main(string[] args)
        {
            ProviderOptions? options;
            try
            {
                options = CommandLineParser.Parse(args);
            }
            catch (CommandLineException exception)
            {
                await Console.Error.WriteLineAsync($"error: {exception.Message}{Environment.NewLine}");
                await Console.Error.WriteLineAsync(CommandLineParser.Usage);
                return ExitUsage;
            }
            if (options == null)
            {
                Console.WriteLine(CommandLineParser.Usage);
                return ExitSuccess;
            }
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(options.LogLevel);
                builder.AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                });
            });
            var logger = loggerFactory.CreateLogger(typeof(Program).FullName!);
            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });
            try
            {
                await new ProviderHost(options, loggerFactory).RunAsync(shutdown.Token);
                return ExitSuccess;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                logger.LogInformation("Provider stopped.");
                return ExitSuccess;
            }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Provider stopped after an unrecoverable failure.");
                return ExitFailure;
            }
        }
    }
}
