using System;
using System.Buffers;
using System.Buffers.Binary;

#nullable enable

namespace MajDHTProvider.Storage
{
    /// <summary>
    /// A MajDHT v1 leaf identifier, the 32-byte <c>SHA256(0x00 || block)</c>, held as four integers so an index of millions
    /// of blocks compares and stores identifiers without a string per block.
    /// </summary>
    /// <param name="A">The first eight digest bytes, big endian.</param>
    /// <param name="B">The second eight digest bytes, big endian.</param>
    /// <param name="C">The third eight digest bytes, big endian.</param>
    /// <param name="D">The last eight digest bytes, big endian.</param>
    internal readonly record struct LeafId(ulong A, ulong B, ulong C, ulong D)
    {
        /// <summary>The length of an identifier in bytes.</summary>
        public const int Length = 32;

        /// <summary>Reads an identifier from its raw digest.</summary>
        /// <param name="digest">Exactly <see cref="Length"/> bytes.</param>
        /// <returns>The identifier.</returns>
        /// <exception cref="ArgumentException">The digest is not <see cref="Length"/> bytes long.</exception>
        public static LeafId FromDigest(ReadOnlySpan<byte> digest)
        {
            if (digest.Length != Length)
            {
                throw new ArgumentException($"A leaf identifier is {Length} bytes long.", nameof(digest));
            }
            return new LeafId(BinaryPrimitives.ReadUInt64BigEndian(digest), BinaryPrimitives.ReadUInt64BigEndian(digest[8..]),
                BinaryPrimitives.ReadUInt64BigEndian(digest[16..]), BinaryPrimitives.ReadUInt64BigEndian(digest[24..]));
        }

        /// <summary>Parses the hexadecimal form the node uses for block hashes.</summary>
        /// <param name="hex">64 hexadecimal digits in either letter case.</param>
        /// <param name="leafId">Receives the identifier when the text is valid.</param>
        /// <returns>Whether the text is a leaf identifier.</returns>
        public static bool TryParse(string? hex, out LeafId leafId)
        {
            leafId = default;
            if (hex == null || hex.Length != Length * 2)
            {
                return false;
            }
            Span<byte> digest = stackalloc byte[Length];
            if (Convert.FromHexString(hex, digest, out _, out var written) != OperationStatus.Done || written != Length)
            {
                return false;
            }
            leafId = FromDigest(digest);
            return true;
        }

        /// <summary>Writes the raw digest of the identifier.</summary>
        /// <param name="destination">At least <see cref="Length"/> writable bytes.</param>
        /// <exception cref="ArgumentException">The destination is too short.</exception>
        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < Length)
            {
                throw new ArgumentException($"A leaf identifier needs {Length} bytes.", nameof(destination));
            }
            BinaryPrimitives.WriteUInt64BigEndian(destination, A);
            BinaryPrimitives.WriteUInt64BigEndian(destination[8..], B);
            BinaryPrimitives.WriteUInt64BigEndian(destination[16..], C);
            BinaryPrimitives.WriteUInt64BigEndian(destination[24..], D);
        }

        /// <summary>Formats the identifier as the node's lowercase hexadecimal block hash.</summary>
        /// <returns>64 lowercase hexadecimal digits.</returns>
        public override string ToString()
        {
            Span<byte> digest = stackalloc byte[Length];
            WriteTo(digest);
            return Convert.ToHexStringLower(digest);
        }
    }
}
