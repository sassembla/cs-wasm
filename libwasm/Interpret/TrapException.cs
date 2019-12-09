using System;
using System.Runtime.Serialization;

namespace Wasm.Interpret
{
    /// <summary>
    /// A WebAssembly exception that is thrown when WebAssembly execution traps.
    /// </summary>
    [Serializable]
    public class TrapException : WasmException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TrapException"/> class.
        /// </summary>
        /// <param name="message">A user-friendly error message.</param>
        /// <param name="specMessage">A spec-mandated generic error message.</param>
        public TrapException(string message, string specMessage) : base(message)
        {
            this.SpecMessage = specMessage;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="TrapException"/> class.
        /// </summary>
        /// <param name="info">Serialization info.</param>
        /// <param name="context">A streaming context.</param>
        protected TrapException(
            SerializationInfo info,
            StreamingContext context) : base(info, context) { }

        /// <summary>
        /// Gets the generic error message mandated by the spec, as opposed to the possibly
        /// more helpful message encapsulated in the exception itself.
        /// </summary>
        /// <value>A spec error message.</value>
        public string SpecMessage { get; private set; }

        /// <summary>
        /// A collection of generic spec error messages for traps.
        /// </summary>
        public static class SpecMessages
        {
            /// <summary>
            /// The error message for out of bounds memory accesses.
            /// </summary>
            public const string OutOfBoundsMemoryAccess = "out of bounds memory access";

            /// <summary>
            /// The error message for when an unreachable instruction is reached.
            /// </summary>
            public const string Unreachable = "unreachable";

            /// <summary>
            /// The error message for when the max execution stack depth is exceeded.
            /// </summary>
            public const string StackOverflow = "stack overflow";
        }
    }
}
