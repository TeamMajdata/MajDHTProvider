using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajDHTNetwork;
using MajDHTProvider.Hosting;
using MajDHTProvider.Publishing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

#nullable enable

namespace MajDHTProvider.Tests
{
    /// <summary>Checks the files that survive provider restarts.</summary>
    public sealed class PersistenceTests
    {
        /// <summary>Keeps revisions increasing and remembers withdrawals across a reload.</summary>
        [Fact]
        public void LedgerRevisionsIncreaseAcrossReloads()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("data", PublicationLedger.FileName);
            var ledger = PublicationLedger.Load(path);
            Assert.Empty(ledger.PublishedKeyNames);
            var first = ledger.NextRevision("a");
            Assert.True(first >= DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
            var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            ledger.RecordPublished("a", future, "hash-a");
            ledger.RecordPublished("b", first, "hash-b");
            ledger.RecordWithdrawn("b", first + 1);
            ledger.Save();

            var reloaded = PublicationLedger.Load(path);

            Assert.Equal(["a"], reloaded.PublishedKeyNames);
            Assert.True(reloaded.TryGet("b", out var withdrawn));
            Assert.True(withdrawn.Withdrawn);
            Assert.Null(withdrawn.FileHash);
            Assert.Equal(future + 1, reloaded.NextRevision("a"));
            Assert.True(reloaded.NextRevision("b") > first + 1);
            Assert.False(File.Exists(path + ".tmp"));
        }

        /// <summary>Writes the ledger file only after an entry changed, so saving after every synchronization is cheap.</summary>
        [Fact]
        public void LedgerIsSavedOnlyWhenChanged()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("data", PublicationLedger.FileName);
            var ledger = PublicationLedger.Load(path);
            Assert.False(ledger.Save());
            Assert.False(File.Exists(path));

            ledger.RecordPublished("a", 1, "hash-a");
            Assert.True(ledger.Save());
            var written = File.GetLastWriteTimeUtc(path);
            Assert.False(ledger.Save());
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));

            ledger.RecordWithdrawn("a", 2);
            Assert.True(ledger.Save());
            Assert.True(PublicationLedger.Load(path).TryGet("a", out var entry));
            Assert.True(entry.Withdrawn);
        }

        /// <summary>Rejoins the network from a saved routing table without any bootstrap endpoint.</summary>
        /// <returns>Completion after the restored node found the saved contact.</returns>
        [Fact]
        public async Task RoutingTableFilesRejoinKnownNodes()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var token = deadline.Token;
            using var directory = new TestDirectory();
            var path = directory.Combine("data", RoutingTableFile.FileName);
            Assert.Null(RoutingTableFile.TryLoad(path, NullLogger.Instance));
            await using var known = new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download | DHTCapabilities.Provide));
            await known.StartAsync(token);
            await using var first = await TestSupport.StartAsync(
                new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download, known)), token);

            Assert.True(RoutingTableFile.Save(path, first.ExportRoutingTable(), NullLogger.Instance));
            Assert.False(File.Exists(path + ".tmp"));

            await using var second = new MajDHTNode(TestSupport.NodeOptions(DHTCapabilities.Download));
            await second.StartAsync(token);
            Assert.Empty(second.KnownPeers);
            var snapshot = RoutingTableFile.TryLoad(path, NullLogger.Instance);
            Assert.NotNull(snapshot);
            Assert.Equal(1, await second.RestoreRoutingTableAsync(snapshot, token));
            Assert.Contains(second.KnownPeers, peer => peer.NodeId == known.NodeId);
        }

        /// <summary>Rejects ledger files that are not ledgers.</summary>
        [Fact]
        public void CorruptLedgersAreRejected()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine(PublicationLedger.FileName);
            File.WriteAllText(path, "{ not json");
            Assert.Throws<InvalidDataException>(() => PublicationLedger.Load(path));
            File.WriteAllText(path, "{\"version\": 99, \"publications\": {}}");
            Assert.Throws<InvalidDataException>(() => PublicationLedger.Load(path));
        }

        /// <summary>Creates an identity once and loads the same identity afterwards.</summary>
        [Fact]
        public void IdentityFilesKeepTheNodeId()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine("data", IdentityFile.FileName);
            var created = IdentityFile.LoadOrCreate(path, 0, NullLogger.Instance, TestContext.Current.CancellationToken);
            Assert.Equal(32, new FileInfo(path).Length);
            var loaded = IdentityFile.LoadOrCreate(path, 0, NullLogger.Instance, TestContext.Current.CancellationToken);
            Assert.Equal(created.NodeId, loaded.NodeId);
            Assert.False(File.Exists(path + ".tmp"));
        }

        /// <summary>Rejects identity files of the wrong size or with too little work.</summary>
        [Fact]
        public void InvalidIdentityFilesAreRejected()
        {
            using var directory = new TestDirectory();
            var path = directory.Combine(IdentityFile.FileName);
            File.WriteAllBytes(path, new byte[31]);
            Assert.Throws<InvalidDataException>(() => IdentityFile.LoadOrCreate(path, 0, NullLogger.Instance, TestContext.Current.CancellationToken));
            var identity = new DHTIdentity();
            File.WriteAllBytes(path, identity.ExportSeed());
            var required = identity.IdentityWork + 1;
            Assert.Throws<InvalidDataException>(() => IdentityFile.LoadOrCreate(path, required, NullLogger.Instance, TestContext.Current.CancellationToken));
        }
    }
}
