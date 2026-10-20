using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Helpers shared by the chart synchronizers.</summary>
    internal static class ChartSynchronization
    {
        /// <summary>
        /// Applies an asynchronous operation to every item with at most a given number of operations in flight, so a
        /// synchronization of many chart resources overlaps their file reads, hashing and DHT exchanges.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="items">The items.</param>
        /// <param name="parallelism">The maximum operations in flight, at least one.</param>
        /// <param name="operation">The operation applied to one item; it must handle the failures it wants to tolerate.</param>
        /// <param name="cancellationToken">Stops starting operations and cancels the running ones.</param>
        /// <returns>Completion after every operation ended.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The parallelism is not positive.</exception>
        /// <exception cref="OperationCanceledException">The operations were cancelled.</exception>
        public static Task ForEachAsync<T>(IReadOnlyList<T> items, int parallelism, Func<T, CancellationToken, Task> operation,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
            var options = new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };
            return Parallel.ForEachAsync(items, options, (item, token) => new ValueTask(operation(item, token)));
        }
    }
}
