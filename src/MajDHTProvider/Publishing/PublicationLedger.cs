using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

#nullable enable

namespace MajDHTProvider.Publishing
{
    /// <summary>
    /// Persists the latest revision of every key the publisher signed, so revisions keep increasing across restarts and
    /// resources removed while the publisher was offline are withdrawn at the next start. Every member is thread safe, so
    /// the resources of one synchronization can record their publications at once; <see cref="Save"/> writes the file
    /// only when something changed, so it is cheap to call after every synchronization regardless of how many resources
    /// it touched.
    /// </summary>
    internal sealed class PublicationLedger
    {
        /// <summary>The file name of the ledger inside the data directory.</summary>
        public const string FileName = "publications.json";

        /// <summary>The ledger file format version.</summary>
        private const int FormatVersion = 1;

        /// <summary>The JSON format of the ledger file.</summary>
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        /// <summary>The ledger file path.</summary>
        private readonly string _path;
        /// <summary>Protects the entries and the change flag.</summary>
        private readonly Lock _gate = new();
        /// <summary>The latest signed state of every key, keyed by key name.</summary>
        private readonly Dictionary<string, PublicationLedgerEntry> _entries;
        /// <summary>Whether an entry changed since the ledger was loaded or last saved.</summary>
        private bool _changed;

        /// <summary>Creates a ledger backed by a file.</summary>
        /// <param name="path">The ledger file path.</param>
        /// <param name="entries">The loaded entries.</param>
        private PublicationLedger(string path, Dictionary<string, PublicationLedgerEntry> entries)
        {
            _path = path;
            _entries = entries;
        }

        /// <summary>Gets the key names whose latest signed record is a publication rather than a withdrawal.</summary>
        public IReadOnlyList<string> PublishedKeyNames
        {
            get
            {
                var names = new List<string>();
                lock (_gate)
                {
                    foreach (var (name, entry) in _entries)
                    {
                        if (!entry.Withdrawn)
                        {
                            names.Add(name);
                        }
                    }
                }
                return names;
            }
        }

        /// <summary>Loads a ledger file, or starts an empty ledger when the file does not exist.</summary>
        /// <param name="path">The ledger file path.</param>
        /// <returns>The ledger.</returns>
        /// <exception cref="InvalidDataException">The file is not a ledger of a supported version.</exception>
        /// <exception cref="IOException">The file cannot be read.</exception>
        public static PublicationLedger Load(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return new PublicationLedger(fullPath, new Dictionary<string, PublicationLedgerEntry>(StringComparer.Ordinal));
            }
            PublicationLedgerDocument? document;
            try
            {
                document = JsonSerializer.Deserialize<PublicationLedgerDocument>(File.ReadAllText(fullPath), s_jsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"The publication ledger {fullPath} is not valid JSON.", exception);
            }
            if (document == null || document.Version != FormatVersion || document.Publications == null)
            {
                throw new InvalidDataException($"The publication ledger {fullPath} has an unsupported format.");
            }
            return new PublicationLedger(fullPath, new Dictionary<string, PublicationLedgerEntry>(document.Publications, StringComparer.Ordinal));
        }

        /// <summary>Gets the latest signed state of a key.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="entry">Receives the entry when the key was ever signed.</param>
        /// <returns>Whether the key was ever signed.</returns>
        public bool TryGet(string keyName, out PublicationLedgerEntry entry)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(keyName, out entry!);
            }
        }

        /// <summary>
        /// Chooses the revision of the next record of a key: above every revision signed before and at least the current Unix
        /// time in milliseconds, so revisions still increase if the ledger is lost.
        /// </summary>
        /// <param name="keyName">The key name.</param>
        /// <returns>The next revision.</returns>
        public long NextRevision(string keyName)
        {
            long previous;
            lock (_gate)
            {
                previous = _entries.TryGetValue(keyName, out var entry) ? entry.Revision : 0;
            }
            return Math.Max(previous + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        /// <summary>Records a signed publication.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="revision">The signed revision.</param>
        /// <param name="fileHash">The published complete-file hash.</param>
        public void RecordPublished(string keyName, long revision, string fileHash)
        {
            lock (_gate)
            {
                _entries[keyName] = new PublicationLedgerEntry(revision, fileHash, false);
                _changed = true;
            }
        }

        /// <summary>Records a signed withdrawal.</summary>
        /// <param name="keyName">The key name.</param>
        /// <param name="revision">The signed withdrawal revision.</param>
        public void RecordWithdrawn(string keyName, long revision)
        {
            lock (_gate)
            {
                _entries[keyName] = new PublicationLedgerEntry(revision, null, true);
                _changed = true;
            }
        }

        /// <summary>Writes the ledger atomically when an entry changed since it was loaded or last saved.</summary>
        /// <returns>Whether the file was written.</returns>
        /// <exception cref="IOException">The file cannot be written; the changes stay pending for the next save.</exception>
        public bool Save()
        {
            string json;
            lock (_gate)
            {
                if (!_changed)
                {
                    return false;
                }
                var document = new PublicationLedgerDocument
                {
                    Version = FormatVersion,
                    Publications = new SortedDictionary<string, PublicationLedgerEntry>(_entries, StringComparer.Ordinal)
                };
                json = JsonSerializer.Serialize(document, s_jsonOptions);
                // Entries recorded while the file is written mark the ledger changed again and are saved next time.
                _changed = false;
            }
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (directory != null)
                {
                    Directory.CreateDirectory(directory);
                }
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, json);
                File.Move(temporary, _path, true);
                return true;
            }
            catch
            {
                lock (_gate)
                {
                    _changed = true;
                }
                throw;
            }
        }

        /// <summary>The serialized ledger file.</summary>
        private sealed class PublicationLedgerDocument
        {
            /// <summary>Gets or sets the format version.</summary>
            public int Version { get; set; }

            /// <summary>Gets or sets the entries keyed by key name.</summary>
            public IDictionary<string, PublicationLedgerEntry>? Publications { get; set; }
        }
    }
}
