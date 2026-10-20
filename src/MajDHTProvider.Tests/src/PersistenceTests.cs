using System;
using System.IO;
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
