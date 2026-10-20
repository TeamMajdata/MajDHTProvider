#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Selects the role of the provider process in the network.</summary>
    internal enum ProviderMode
    {
        /// <summary>Publishes every chart resource found on disk and withdraws resources that disappear.</summary>
        Publisher,
        /// <summary>Serves a trusted publisher's resources from disk, pulling missing ones from the origin on demand.</summary>
        Replica,
        /// <summary>
        /// Joins the network only to introduce peers to each other: keeps a routing table, answers discovery requests and
        /// indexes records, but publishes and serves no content and has no chart directory.
        /// </summary>
        Bootstrap
    }
}
