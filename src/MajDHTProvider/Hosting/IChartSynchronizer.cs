using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Keeps the DHT state of one provider mode consistent with the chart directory.</summary>
    internal interface IChartSynchronizer
    {
        /// <summary>Synchronizes every chart, for the initial scan and for rescans after lost file notifications.</summary>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>Completion after every chart was synchronized.</returns>
        /// <exception cref="System.IO.IOException">The chart root cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was cancelled.</exception>
        Task SynchronizeAllAsync(CancellationToken cancellationToken);

        /// <summary>Synchronizes one chart after its directory changed.</summary>
        /// <param name="chartId">The chart identifier.</param>
        /// <param name="cancellationToken">Stops synchronization.</param>
        /// <returns>Completion after the chart was synchronized.</returns>
        /// <exception cref="System.IO.IOException">The chart directory cannot be enumerated.</exception>
        /// <exception cref="OperationCanceledException">Synchronization was cancelled.</exception>
        Task SynchronizeChartAsync(Guid chartId, CancellationToken cancellationToken);

        /// <summary>Runs periodic mode-specific maintenance, such as renewals or withdrawal checks, until cancellation.</summary>
        /// <param name="cancellationToken">Stops maintenance.</param>
        /// <returns>A task that completes only by cancellation.</returns>
        /// <exception cref="OperationCanceledException">Maintenance was cancelled.</exception>
        Task RunMaintenanceAsync(CancellationToken cancellationToken);
    }
}
