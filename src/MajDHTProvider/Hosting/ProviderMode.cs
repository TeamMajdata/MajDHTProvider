#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Selects how the provider treats its chart directory.</summary>
    internal enum ProviderMode
    {
        /// <summary>Publishes every chart resource found on disk and withdraws resources that disappear.</summary>
        Publisher,
        /// <summary>Serves a trusted publisher's resources from disk, pulling missing ones from the origin on demand.</summary>
        Replica
    }
}
