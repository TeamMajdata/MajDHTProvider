using System;

#nullable enable

namespace MajDHTProvider.Hosting
{
    /// <summary>Reports invalid command-line arguments; the message is shown to the user with the usage text.</summary>
    internal sealed class CommandLineException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Explains which argument is invalid.</param>
        public CommandLineException(string message) : base(message)
        {
        }
    }
}
