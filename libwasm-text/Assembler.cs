using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Pixie;
using Pixie.Code;
using Pixie.Markup;
using Wasm.Instructions;
using Wasm.Optimize;

namespace Wasm.Text
{
    /// <summary>
    /// An assembler for the WebAssembly text format. Converts parsed WebAssembly text format
    /// modules to in-memory WebAssembly binary format modules.
    /// </summary>
    public sealed class Assembler
    {
        /// <summary>
        /// Creates a WebAssembly assembler.
        /// </summary>
        /// <param name="log">A log to send diagnostics to.</param>
        public Assembler(ILog log)
            : this(log, DefaultModuleFieldAssemblers, DefaultPlainInstructionAssemblers)
        { }

        /// <summary>
        /// Creates a WebAssembly assembler.
        /// </summary>
        /// <param name="log">A log to send diagnostics to.</param>
        /// <param name="moduleFieldAssemblers">
        /// A mapping of module field keywords to module field assemblers.
        /// </param>
        /// <param name="plainInstructionAssemblers">
        /// A mapping of instruction keywords to instruction assemblers.
        /// </param>
        public Assembler(
            ILog log,
            IReadOnlyDictionary<string, ModuleFieldAssembler> moduleFieldAssemblers,
            IReadOnlyDictionary<string, PlainInstructionAssembler> plainInstructionAssemblers)
        {
            this.Log = log;
            this.ModuleFieldAssemblers = moduleFieldAssemblers;
            this.PlainInstructionAssemblers = plainInstructionAssemblers;
        }

        /// <summary>
        /// Gets the log that is used for reporting diagnostics.
        /// </summary>
        /// <value>A log.</value>
        public ILog Log { get; private set; }

        /// <summary>
        /// Gets the module field assemblers this assembler uses to process
        /// module fields.
        /// </summary>
        /// <value>A mapping of module field keywords to module field assemblers.</value>
        public IReadOnlyDictionary<string, ModuleFieldAssembler> ModuleFieldAssemblers { get; private set; }

        /// <summary>
        /// Gets the module field assemblers this assembler uses to process
        /// module fields.
        /// </summary>
        /// <value>A mapping of module field keywords to module field assemblers.</value>
        public IReadOnlyDictionary<string, PlainInstructionAssembler> PlainInstructionAssemblers { get; private set; }

        /// <summary>
        /// Assembles an S-expression representing a module into a WebAssembly module.
        /// </summary>
        /// <param name="expression">The expression to assemble.</param>
        /// <returns>An assembled module.</returns>
        public WasmFile AssembleModule(SExpression expression)
        {
            var file = new WasmFile();
            if (!expression.IsCallTo("module"))
            {
                Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "top-level modules must be encoded as S-expressions that call ",
                            "module",
                            "."),
                        Highlight(expression)));
            }

            IReadOnlyList<SExpression> fields;
            if (expression.Tail.Count > 0 && expression.Tail[0].IsIdentifier)
            {
                // We encountered a module name. Turn it into a name entry and then skip it
                // for the purpose of module field analysis.
                file.ModuleName = (string)expression.Tail[0].Head.Value;
                fields = expression.Tail.Skip(1).ToArray();
            }
            else
            {
                fields = expression.Tail;
            }

            // Now assemble the module's fields.
            var context = new ModuleContext(this);
            foreach (var field in fields)
            {
                ModuleFieldAssembler fieldAssembler;
                if (!field.IsCall)
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            "unexpected token; expected a module field.",
                            Highlight(expression)));
                }
                else if (DefaultModuleFieldAssemblers.TryGetValue((string)field.Head.Value, out fieldAssembler))
                {
                    fieldAssembler(field, file, context);
                }
                else
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            Quotation.QuoteEvenInBold(
                                "unexpected module field type ",
                                (string)field.Head.Value,
                                "."),
                            Highlight(expression)));
                }
            }
            context.ResolveIdentifiers(file);
            return file;
        }

        /// <summary>
        /// Assembles an S-expression representing a module into a WebAssembly module.
        /// </summary>
        /// <param name="tokens">A stream of tokens to parse and assemble.</param>
        /// <returns>An assembled module.</returns>
        public WasmFile AssembleModule(IEnumerable<Lexer.Token> tokens)
        {
            var exprs = Parser.ParseAsSExpressions(tokens, Log);
            if (exprs.Count == 0)
            {
                Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "nothing to assemble",
                        "input stream contains no S-expression that can be assembled into a module."));
                return new WasmFile();
            }
            else if (exprs.Count != 1)
            {
                Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "multiple modules",
                        "input stream contains more than one S-expression to assemble into a module; expected just one.",
                        Highlight(exprs[1])));
            }
            return AssembleModule(exprs[0]);
        }

        /// <summary>
        /// Assembles an S-expression representing a module into a WebAssembly module.
        /// </summary>
        /// <param name="document">A document to parse and assemble.</param>
        /// <param name="fileName">The name of the file in which <paramref name="document"/> is saved.</param>
        /// <returns>An assembled module.</returns>
        public WasmFile AssembleModule(string document, string fileName = "<string>")
        {
            return AssembleModule(Lexer.Tokenize(document, fileName));
        }

        private static HighlightedSource Highlight(Lexer.Token expression)
        {
            return new HighlightedSource(new SourceRegion(expression.Span));
        }

        private static HighlightedSource Highlight(SExpression expression)
        {
            return Highlight(expression.Head);
        }

        /// <summary>
        /// A type for module field assemblers.
        /// </summary>
        /// <param name="moduleField">A module field to assemble.</param>
        /// <param name="module">The module that is being assembled.</param>
        /// <param name="context">The module's assembly context.</param>
        public delegate void ModuleFieldAssembler(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context);

        /// <summary>
        /// A type for plain instruction assemblers.
        /// </summary>
        /// <param name="keyword">The keyword expression that names the instruction.</param>
        /// <param name="operands">
        /// A nonempty list of S-expressions that represent instruction operands to assemble.
        /// </param>
        /// <param name="context">The module's assembly context.</param>
        public delegate Instruction PlainInstructionAssembler(
            SExpression keyword,
            ref IReadOnlyList<SExpression> operands,
            InstructionContext context);

        /// <summary>
        /// Context that is used when assembling a module.
        /// </summary>
        public sealed class ModuleContext
        {
            /// <summary>
            /// Creates a module context.
            /// </summary>
            /// <param name="assembler">The assembler that gives rise to this conetxt.</param>
            public ModuleContext(Assembler assembler)
            {
                this.Assembler = assembler;
                this.MemoryContext = IdentifierContext<MemoryType>.Create();
                this.FunctionContext = IdentifierContext<LocalOrImportRef>.Create();
                this.GlobalContext = IdentifierContext<LocalOrImportRef>.Create();
                this.TableContext = IdentifierContext<LocalOrImportRef>.Create();
                this.TypeContext = IdentifierContext<uint>.Create();
            }

            /// <summary>
            /// Gets the identifier context for the module's memories.
            /// </summary>
            /// <value>An identifier context.</value>
            public IdentifierContext<MemoryType> MemoryContext { get; private set; }

            /// <summary>
            /// Gets the identifier context for the module's functions.
            /// </summary>
            /// <value>An identifier context.</value>
            public IdentifierContext<LocalOrImportRef> FunctionContext { get; private set; }

            /// <summary>
            /// Gets the identifier context for the module's globals.
            /// </summary>
            /// <value>An identifier context.</value>
            public IdentifierContext<LocalOrImportRef> GlobalContext { get; private set; }

            /// <summary>
            /// Gets the identifier context for the module's tables.
            /// </summary>
            /// <value>An identifier context.</value>
            public IdentifierContext<LocalOrImportRef> TableContext { get; private set; }

            /// <summary>
            /// Gets the identifier context for the module's types.
            /// </summary>
            /// <value>An identifier context.</value>
            public IdentifierContext<uint> TypeContext { get; private set;}

            /// <summary>
            /// Gets the assembler that gives rise to this context.
            /// </summary>
            /// <value>An assembler.</value>
            public Assembler Assembler { get; private set; }

            /// <summary>
            /// Gets the log used by the assembler and, by extension, this context.
            /// </summary>
            public ILog Log => Assembler.Log;

            /// <summary>
            /// Resolves any pending references in the module.
            /// </summary>
            /// <param name="module">The module for which this context was created.</param>
            public void ResolveIdentifiers(WasmFile module)
            {
                var importSection = module.GetFirstSectionOrNull<ImportSection>() ?? new ImportSection();
                var memorySection = module.GetFirstSectionOrNull<MemorySection>() ?? new MemorySection();
                var functionSection = module.GetFirstSectionOrNull<FunctionSection>() ?? new FunctionSection();
                var globalSection = module.GetFirstSectionOrNull<GlobalSection>() ?? new GlobalSection();
                var tableSection = module.GetFirstSectionOrNull<TableSection>() ?? new TableSection();

                var memoryIndices = new Dictionary<MemoryType, uint>();
                var functionIndices = new Dictionary<LocalOrImportRef, uint>();
                var globalIndices = new Dictionary<LocalOrImportRef, uint>();
                var tableIndices = new Dictionary<LocalOrImportRef, uint>();
                for (int i = 0; i < importSection.Imports.Count; i++)
                {
                    var import = importSection.Imports[i];
                    if (import is ImportedMemory importedMemory)
                    {
                        memoryIndices[importedMemory.Memory] = (uint)memoryIndices.Count;
                    }
                    else if (import is ImportedFunction importedFunction)
                    {
                        functionIndices[new LocalOrImportRef(true, (uint)i)] = (uint)functionIndices.Count;
                    }
                    else if (import is ImportedGlobal importedGlobal)
                    {
                        globalIndices[new LocalOrImportRef(true, (uint)i)] = (uint)globalIndices.Count;
                    }
                    else if (import is ImportedTable importedTable)
                    {
                        tableIndices[new LocalOrImportRef(true, (uint)i)] = (uint)tableIndices.Count;
                    }
                }
                foreach (var memory in memorySection.Memories)
                {
                    memoryIndices[memory] = (uint)memoryIndices.Count;
                }
                for (int i = 0; i < functionSection.FunctionTypes.Count; i++)
                {
                    functionIndices[new LocalOrImportRef(false, (uint)i)] = (uint)functionIndices.Count;
                }
                for (int i = 0; i < globalSection.GlobalVariables.Count; i++)
                {
                    globalIndices[new LocalOrImportRef(false, (uint)i)] = (uint)globalIndices.Count;
                }
                for (int i = 0; i < tableSection.Tables.Count; i++)
                {
                    tableIndices[new LocalOrImportRef(false, (uint)i)] = (uint)tableIndices.Count;
                }

                // Resolve memory identifiers.
                MemoryContext.ResolveAll(
                    Assembler.Log,
                    mem => memoryIndices[mem]);

                // Resolve function identifiers.
                FunctionContext.ResolveAll(
                    Assembler.Log,
                    func => functionIndices[func]);

                // Resolve global identifiers.
                GlobalContext.ResolveAll(
                    Assembler.Log,
                    global => globalIndices[global]);

                // Resolve table identifiers.
                TableContext.ResolveAll(
                    Assembler.Log,
                    table => tableIndices[table]);

                // Resolve type identifiers.
                TypeContext.ResolveAll(
                    Assembler.Log,
                    index => index);
            }
        }

        /// <summary>
        /// Context that is used when assembling an instruction.
        /// </summary>
        public sealed class InstructionContext
        {
            private InstructionContext(
                IReadOnlyDictionary<string, uint> namedLocalIndices,
                string labelOrNull,
                ModuleContext moduleContext,
                InstructionContext parent)
            {
                this.NamedLocalIndices = namedLocalIndices;
                this.LabelOrNull = labelOrNull;
                this.ModuleContext = moduleContext;
                this.ParentOrNull = parent;
            }

            /// <summary>
            /// Creates a top-level instruction context.
            /// </summary>
            /// <param name="namedLocalIndices">The instruction context's named local indices.</param>
            /// <param name="moduleContext">A context for the module that analyzes the instruction.</param>
            public InstructionContext(
                IReadOnlyDictionary<string, uint> namedLocalIndices,
                ModuleContext moduleContext)
                : this(namedLocalIndices, null, moduleContext, null)
            { }

            /// <summary>
            /// Creates a child instruction context with a particular label.
            /// </summary>
            /// <param name="label">A label that a break table can branch to.</param>
            /// <param name="parent">A parent instruction context.</param>
            public InstructionContext(
                string label,
                InstructionContext parent)
                : this(parent.NamedLocalIndices, label, parent.ModuleContext, parent)
            { }

            /// <summary>
            /// Gets a mapping of local variable names to their indices.
            /// </summary>
            /// <value>A mapping of names to indices.</value>
            public IReadOnlyDictionary<string, uint> NamedLocalIndices { get; private set; }

            /// <summary>
            /// Gets the enclosing module context.
            /// </summary>
            /// <value>A module context.</value>
            public ModuleContext ModuleContext { get; private set; }

            /// <summary>
            /// Gets this instruction context's label if it has one
            /// and <c>null</c> otherwise.
            /// </summary>
            /// <value>A label or <c>null</c>.</value>
            public string LabelOrNull { get; private set; }

            /// <summary>
            /// Tells if this instruction context has a label.
            /// </summary>
            public bool HasLabel => LabelOrNull != null;

            /// <summary>
            /// Gets this instruction context's parent context if it has one
            /// and <c>null</c> otherwise.
            /// </summary>
            /// <value>An instruction context or <c>null</c>.</value>
            public InstructionContext ParentOrNull { get; private set; }

            /// <summary>
            /// Tells if this instruction context has a parent context.
            /// </summary>
            public bool HasParent => ParentOrNull != null;

            /// <summary>
            /// Gets the log used by the assembler and, by extension, this context.
            /// </summary>
            public ILog Log => ModuleContext.Log;
        }

        /// <summary>
        /// A reference to a function, global, table or memory that is either defined
        /// locally or imported.
        /// </summary>
        public struct LocalOrImportRef
        {
            /// <summary>
            /// Creates a reference to a function, global, table or memory that is either defined
            /// locally or imported.
            /// </summary>
            /// <param name="isImport">
            /// Tells if the value referred to by this reference is an import.
            /// </param>
            /// <param name="indexInSection">
            /// The intra-section index of the value being referred to.
            /// </param>
            public LocalOrImportRef(bool isImport, uint indexInSection)
            {
                this.IsImport = isImport;
                this.IndexInSection = indexInSection;
            }

            /// <summary>
            /// Tells if the value referred to by this reference is an import.
            /// </summary>
            /// <value><c>true</c> if the value is an import; otherwise, <c>false</c>.</value>
            public bool IsImport { get; private set; }

            /// <summary>
            /// Gets the intra-section index of the value being referred to.
            /// </summary>
            /// <value>An intra-section index.</value>
            public uint IndexInSection { get; private set; }
        }

        /// <summary>
        /// An identifier context, which maps identifiers to indices.
        /// </summary>
        public struct IdentifierContext<T>
        {
            /// <summary>
            /// Creates an empty identifier context.
            /// </summary>
            /// <returns>An identifier context.</returns>
            public static IdentifierContext<T> Create()
            {
                return new IdentifierContext<T>()
                {
                    identifierDefinitions = new Dictionary<string, T>(),
                    pendingIdentifierReferences = new List<KeyValuePair<Lexer.Token, Action<uint>>>()
                };
            }

            private Dictionary<string, T> identifierDefinitions;
            private List<KeyValuePair<Lexer.Token, Action<uint>>> pendingIdentifierReferences;

            /// <summary>
            /// Defines a new identifier.
            /// </summary>
            /// <param name="identifier">The identifier to define.</param>
            /// <param name="value">The value identified by the identifier.</param>
            /// <returns>
            /// <c>true</c> if <paramref name="identifier"/> is non-null and there is no
            /// previous definition of the identifier; otherwise, <c>false</c>.
            /// </returns>
            public bool Define(string identifier, T value)
            {
                if (identifier == null || identifierDefinitions.ContainsKey(identifier))
                {
                    return false;
                }
                else
                {
                    identifierDefinitions[identifier] = value;
                    return true;
                }
            }

            /// <summary>
            /// Introduces a new identifier use.
            /// </summary>
            /// <param name="token">A token that refers to an identifier or an index.</param>
            /// <param name="patch">
            /// An action that patches a user based on the index assigned to the token.
            /// Will be executed once the module is fully assembled.
            /// </param>
            public void Use(Lexer.Token token, Action<uint> patch)
            {
                pendingIdentifierReferences.Add(
                    new KeyValuePair<Lexer.Token, Action<uint>>(token, patch));
            }

            /// <summary>
            /// Introduces a new identifier use.
            /// </summary>
            /// <param name="value">A value that will eventually be assigned an index.</param>
            /// <param name="patch">
            /// An action that patches a user based on the index assigned to the value.
            /// Will be executed once the module is fully assembled.
            /// </param>
            public void Use(T value, Action<uint> patch)
            {
                Use(Lexer.Token.Synthesize(value), patch);
            }

            /// <summary>
            /// Tries to map an identifier back to its definition.
            /// </summary>
            /// <param name="identifier">An identifier to inspect.</param>
            /// <param name="definition">A definition for <paramref name="identifier"/>, if one exists already.</param>
            /// <returns>
            /// <c>true</c> <paramref name="identifier"/> is defined; otherwise, <c>false</c>.
            /// </returns>
            public bool TryGetDefinition(string identifier, out T definition)
            {
                return identifierDefinitions.TryGetValue(identifier, out definition);
            }

            /// <summary>
            /// Resolves all pending references.
            /// </summary>
            /// <param name="log">A log to send diagnostics to.</param>
            /// <param name="getIndex">A function that maps defined values to indices.</param>
            public void ResolveAll(ILog log, Func<T, uint> getIndex)
            {
                foreach (var pair in pendingIdentifierReferences)
                {
                    uint index;
                    if (TryResolve(pair.Key, getIndex, out index))
                    {
                        pair.Value(index);
                    }
                    else
                    {
                        var id = (string)pair.Key.Value;
                        var suggested = NameSuggestion.SuggestName(id, identifierDefinitions.Keys);
                        log.Log(
                            new LogEntry(
                                Severity.Error,
                                "syntax error",
                                Quotation.QuoteEvenInBold("identifier ", id, " does is undefined"),
                                suggested == null
                                    ? (MarkupNode)"."
                                    : Quotation.QuoteEvenInBold("; did you mean ", suggested, "?"),
                                Highlight(pair.Key)));
                    }
                }
                pendingIdentifierReferences.Clear();
            }

            /// <summary>
            /// Tries to map an identifier to its associated index.
            /// </summary>
            /// <param name="identifier">An identifier to inspect.</param>
            /// <param name="getIndex">A function that maps defined values to indices.</param>
            /// <param name="index">The associated index.</param>
            /// <returns>
            /// <c>true</c> if an index was found for <paramref name="identifier"/>; otherwise, <c>false</c>.
            /// </returns>
            private bool TryResolve(string identifier, Func<T, uint> getIndex, out uint index)
            {
                T val;
                if (identifierDefinitions.TryGetValue(identifier, out val))
                {
                    index = getIndex(val);
                    return true;
                }
                else
                {
                    index = 0;
                    return false;
                }
            }

            /// <summary>
            /// Tries to map an identifier or index to its associated index.
            /// </summary>
            /// <param name="identifierOrIndex">An identifier or index to inspect.</param>
            /// <param name="getIndex">A function that maps defined values to indices.</param>
            /// <param name="index">The associated index.</param>
            /// <returns>
            /// <c>true</c> if an index was found for <paramref name="identifierOrIndex"/>; otherwise, <c>false</c>.
            /// </returns>
            private bool TryResolve(Lexer.Token identifierOrIndex, Func<T, uint> getIndex, out uint index)
            {
                if (identifierOrIndex.Kind == Lexer.TokenKind.UnsignedInteger)
                {
                    index = (uint)(BigInteger)identifierOrIndex.Value;
                    return true;
                }
                else if (identifierOrIndex.Kind == Lexer.TokenKind.Identifier)
                {
                    return TryResolve((string)identifierOrIndex.Value, getIndex, out index);
                }
                else if (identifierOrIndex.Kind == Lexer.TokenKind.Synthetic
                    && identifierOrIndex.Value is T)
                {
                    index = getIndex((T)identifierOrIndex.Value);
                    return true;
                }
                else
                {
                    index = 0;
                    return false;
                }
            }
        }

        /// <summary>
        /// The default set of module field assemblers.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, ModuleFieldAssembler> DefaultModuleFieldAssemblers =
            new Dictionary<string, ModuleFieldAssembler>()
        {
            ["export"] = AssembleExport,
            ["function"] = AssembleFunction,
            ["import"] = AssembleImport,
            ["memory"] = AssembleMemory,
            ["table"] = AssembleTable,
            ["type"] = AssembleType
        };

        /// <summary>
        /// The default set of instruction assemblers.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, PlainInstructionAssembler> DefaultPlainInstructionAssemblers;

        static Assembler()
        {
            var insnAssemblers = new Dictionary<string, PlainInstructionAssembler>()
            {
                ["i32.const"] = AssembleConstInt32Instruction,
                ["i64.const"] = AssembleConstInt64Instruction
            };
            foreach (var op in Operators.AllOperators)
            {
                if (op is NullaryOperator nullary)
                {
                    // Nullary operators have a fairly regular structure that is almost identical
                    // to their mnemonics as specified for the binary encoding.
                    // The only way in which they are different is that they do not include slashes.
                    // To accommodate this, we map binary encoding mnemonics to text format mnemonics like
                    // so:
                    //
                    //   i32.add -> i32.add
                    //   𝚏3𝟸.𝚌𝚘𝚗𝚟𝚎𝚛𝚝_𝚞/𝚒𝟼𝟺 -> 𝚏𝟹𝟸.𝚌𝚘𝚗𝚟𝚎𝚛𝚝_𝚒𝟼𝟺_𝚞
                    //   𝚏𝟹𝟸.𝚍𝚎𝚖𝚘𝚝𝚎/𝚏𝟼𝟺 -> 𝚏𝟹𝟸.𝚍𝚎𝚖𝚘𝚝𝚎_𝚏𝟼𝟺
                    //
                    var mnemonic = nullary.Mnemonic;
                    var mnemonicAndType = mnemonic.Split(new[] { '/' }, 2);
                    if (mnemonicAndType.Length == 2)
                    {
                        var mnemonicAndSuffix = mnemonicAndType[0].Split(new[] { '_' }, 2);
                        if (mnemonicAndSuffix.Length == 2)
                        {
                            mnemonic = $"{mnemonicAndSuffix[0]}_{mnemonicAndType[1]}_{mnemonicAndSuffix[1]}";
                        }
                        else
                        {
                            mnemonic = $"{mnemonicAndType[0]}_{mnemonicAndType[1]}";
                        }
                    }
                    if (nullary.DeclaringType != WasmType.Empty)
                    {
                        mnemonic = $"{DumpHelpers.WasmTypeToString(nullary.DeclaringType)}.{mnemonic}";
                    }
                    insnAssemblers[mnemonic] = (SExpression keyword, ref IReadOnlyList<SExpression> operands, InstructionContext context) =>
                        nullary.Create();
                }
            }
        }

        private static Instruction AssembleConstInt32Instruction(
            SExpression keyword,
            ref IReadOnlyList<SExpression> operands,
            InstructionContext context)
        {
            if (TryPopImmediate(keyword, ref operands, context, out SExpression immediate))
            {
                return Operators.Int32Const.Create(AssembleSignlessInt32(immediate, context.ModuleContext));
            }
            else
            {
                return Operators.Int32Const.Create(0);
            }
        }

        private static Instruction AssembleConstInt64Instruction(
            SExpression keyword,
            ref IReadOnlyList<SExpression> operands,
            InstructionContext context)
        {
            if (TryPopImmediate(keyword, ref operands, context, out SExpression immediate))
            {
                return Operators.Int64Const.Create(AssembleSignlessInt64(immediate, context.ModuleContext));
            }
            else
            {
                return Operators.Int64Const.Create(0);
            }
        }

        private static bool TryPopImmediate(
            SExpression keyword,
            ref IReadOnlyList<SExpression> operands,
            InstructionContext context,
            out SExpression immediate)
        {
            if (operands.Count == 0)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        "expected another immediate.",
                        Highlight(keyword)));
                immediate = default(SExpression);
                return false;
            }
            else
            {
                immediate = operands[0];
                operands = operands.Skip(1).ToArray();
                return true;
            }
        }

        private static void AssembleMemory(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context)
        {
            const string kind = "memory definition";

            // Process the optional memory identifier.
            var memory = new MemoryType(new ResizableLimits(0));
            var tail = moduleField.Tail;
            if (tail.Count > 0 && tail[0].IsIdentifier)
            {
                context.MemoryContext.Define((string)tail[0].Head.Value, memory);
                tail = tail.Skip(1).ToArray();
            }

            if (!AssertNonEmpty(moduleField, tail, kind, context))
            {
                return;
            }

            // Parse inline exports.
            while (tail[0].IsCallTo("export"))
            {
                var exportExpr = tail[0];
                tail = tail.Skip(1).ToArray();
                if (!AssertNonEmpty(moduleField, tail, kind, context))
                {
                    return;
                }
                if (!AssertElementCount(exportExpr, exportExpr.Tail, 1, context))
                {
                    continue;
                }

                var exportName = AssembleString(exportExpr.Tail[0], context);
                AddExport(module, context.MemoryContext, memory, ExternalKind.Memory, exportName);
            }

            if (tail[0].IsCallTo("data"))
            {
                var data = AssembleDataString(tail[0].Tail, context);
                var pageCount = (uint)Math.Ceiling((double)data.Length / MemoryType.PageSize);
                memory.Limits = new ResizableLimits(pageCount, pageCount);
                module.AddMemory(memory);
                var dataSegment = new DataSegment(0, new InitializerExpression(Operators.Int32Const.Create(0)), data);
                context.MemoryContext.Use(memory, index => { dataSegment.MemoryIndex = index; });
                module.AddDataSegment(dataSegment);
                AssertEmpty(context, kind, tail.Skip(1));
            }
            else if (tail[0].IsCallTo("import"))
            {
                var (moduleName, memoryName) = AssembleInlineImport(tail[0], context);
                tail = tail.Skip(1).ToArray();
                memory.Limits = AssembleLimits(moduleField, tail, context);
                var import = new ImportedMemory(moduleName, memoryName, memory);
                module.AddImport(import);
            }
            else
            {
                memory.Limits = AssembleLimits(moduleField, tail, context);
                module.AddMemory(memory);
            }
        }

        private static void AssembleExport(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context)
        {
            var tail = moduleField.Tail;

            if (!AssertElementCount(moduleField, tail, 2, context)
                || !AssertElementCount(tail[1], tail[1].Tail, 1, context))
            {
                return;
            }

            var exportName = AssembleString(tail[0], context);
            var index = AssembleIdentifierOrIndex(tail[1].Tail[0], context);

            if (tail[1].IsCallTo("memory"))
            {
                AddExport(module, context.MemoryContext, index, ExternalKind.Memory, exportName);
            }
            else if (tail[1].IsCallTo("func"))
            {
                AddExport(module, context.FunctionContext, index, ExternalKind.Function, exportName);
            }
            else if (tail[1].IsCallTo("table"))
            {
                AddExport(module, context.TableContext, index, ExternalKind.Table, exportName);
            }
            else if (tail[1].IsCallTo("global"))
            {
                AddExport(module, context.GlobalContext, index, ExternalKind.Global, exportName);
            }
            else
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "unexpected expression in export definition; expected ",
                            "func", ",", "table", ",", "memory", " or ", "global", "."),
                        Highlight(tail[1])));
            }
        }

        private static void AssembleImport(SExpression moduleField, WasmFile module, ModuleContext context)
        {
            if (!AssertElementCount(moduleField, moduleField.Tail, 3, context))
            {
                return;
            }

            var moduleName = AssembleString(moduleField.Tail[0], context);
            var importName = AssembleString(moduleField.Tail[1], context);
            var importDesc = moduleField.Tail[2];

            string importId = null;
            var importTail = importDesc.Tail;
            if (importDesc.Tail.Count > 0 && importDesc.Tail[0].IsIdentifier)
            {
                importId = (string)importDesc.Tail[0].Head.Value;
                importTail = importTail.Skip(1).ToArray();
            }

            if (importDesc.IsCallTo("memory"))
            {
                if (!AssertNonEmpty(importDesc, importTail, "import", context))
                {
                    return;
                }
                var memory = new MemoryType(AssembleLimits(importDesc, importTail, context));
                module.AddImport(new ImportedMemory(moduleName, importName, memory));
                context.MemoryContext.Define(importId, memory);
            }
            else if (importDesc.IsCallTo("func"))
            {
                var type = AssembleTypeUse(importDesc, ref importTail, context, module, true);
                var typeIndex = AddOrReuseFunctionType(type, module);
                var importIndex = module.AddImport(new ImportedFunction(moduleName, importName, typeIndex));
                context.FunctionContext.Define(importId, new LocalOrImportRef(true, importIndex));
                AssertEmpty(context, "import", importTail);
            }
            else if (importDesc.IsCallTo("global"))
            {
                var type = AssembleGlobalType(importTail[0], context);
                var importIndex = module.AddImport(new ImportedGlobal(moduleName, importName, type));
                context.GlobalContext.Define(importId, new LocalOrImportRef(true, importIndex));
                AssertEmpty(context, "global", importTail.Skip(1).ToArray());
            }
            else if (importDesc.IsCallTo("table"))
            {
                var type = AssembleTableType(importDesc, importTail, context);
                var importIndex = module.AddImport(new ImportedTable(moduleName, importName, type));
                context.TableContext.Define(importId, new LocalOrImportRef(true, importIndex));
            }
            else
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "unexpected expression in import; expected ",
                            "func", ",", "table", ",", "memory", " or ", "global", "."),
                        Highlight(importDesc)));
            }
        }

        private static void AssembleTable(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context)
        {
            string tableId = null;
            var tail = moduleField.Tail;
            if (moduleField.Tail.Count > 0 && moduleField.Tail[0].IsIdentifier)
            {
                tableId = (string)moduleField.Tail[0].Head.Value;
                tail = tail.Skip(1).ToArray();
            }

            var exportNames = new List<string>();
            while (tail.Count > 0 && tail[0].IsCallTo("export"))
            {
                var exportExpr = tail[0];
                if (!AssertElementCount(exportExpr, exportExpr.Tail, 1, context))
                {
                    continue;
                }

                exportNames.Add(AssembleString(exportExpr.Tail[0], context));
                tail = tail.Skip(1).ToArray();
            }

            if (!AssertNonEmpty(moduleField, tail, "table definition", context))
            {
                return;
            }

            LocalOrImportRef tableRef;
            if (tail[0].IsCallTo("import"))
            {
                var (moduleName, importName) = AssembleInlineImport(tail[0], context);
                var table = AssembleTableType(moduleField, tail.Skip(1).ToArray(), context);
                var tableIndex = module.AddImport(new ImportedTable(moduleName, importName, table));
                tableRef = new LocalOrImportRef(true, tableIndex);
                context.TableContext.Define(tableId, tableRef);
            }
            else if (tail[0].Head.Kind == Lexer.TokenKind.Keyword)
            {
                var elemType = AssembleElemType(tail[0], context);
                var table = new TableType(elemType, new ResizableLimits(0));
                var tableIndex = module.AddTable(table);
                tableRef = new LocalOrImportRef(false, tableIndex);
                context.TableContext.Define(tableId, tableRef);

                if (!AssertElementCount(moduleField, tail, 2, context))
                {
                    return;
                }

                var elems = tail[1];
                if (!elems.IsCallTo("elem"))
                {
                    context.Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            Quotation.QuoteEvenInBold(
                                "unexpected expression in initialized table; expected an ",
                                "elem", " expression."),
                            Highlight(elems)));
                }

                var elemSegment = new ElementSegment(
                    0,
                    new InitializerExpression(Operators.Int32Const.Create(0)),
                    Enumerable.Empty<uint>());
                module.AddElementSegment(elemSegment);

                context.TableContext.Use(tableRef, index => { elemSegment.TableIndex = index; });

                for (int i = 0; i < elems.Tail.Count; i++)
                {
                    elemSegment.Elements.Add(0);
                    var functionId = AssembleIdentifierOrIndex(elems.Tail[i], context);
                    var j = i;
                    context.FunctionContext.Use(functionId, index => { elemSegment.Elements[j] = index; });
                }
            }
            else
            {
                var table = AssembleTableType(moduleField, tail, context);
                var tableIndex = module.AddTable(table);
                tableRef = new LocalOrImportRef(false, tableIndex);
                context.TableContext.Define(tableId, tableRef);
            }

            foreach (var exportName in exportNames)
            {
                AddExport(module, context.TableContext, tableRef, ExternalKind.Table, exportName);
            }
        }

        private static void AssembleType(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context)
        {
            string typeId = null;
            var tail = moduleField.Tail;
            if (moduleField.Tail.Count > 0 && moduleField.Tail[0].IsIdentifier)
            {
                typeId = (string)moduleField.Tail[0].Head.Value;
                tail = tail.Skip(1).ToArray();
            }

            var type = AssembleTypeUse(moduleField, ref tail, context, module, false);
            AssertEmpty(context, "type definition", tail);

            var index = module.AddFunctionType(type);
            context.TypeContext.Define(typeId, index);
        }

        private static void AssembleFunction(
            SExpression moduleField,
            WasmFile module,
            ModuleContext context)
        {
            string functionId = null;
            var tail = moduleField.Tail;
            if (moduleField.Tail.Count > 0 && moduleField.Tail[0].IsIdentifier)
            {
                functionId = (string)moduleField.Tail[0].Head.Value;
                tail = tail.Skip(1).ToArray();
            }

            var localIdentifiers = new Dictionary<string, uint>();
            var funType = AssembleTypeUse(moduleField, ref tail, context, module, true, localIdentifiers);
            var locals = AssembleLocals(ref tail, localIdentifiers, context, "local", funType.ParameterTypes.Count);
            var insnContext = new InstructionContext(localIdentifiers, context);
            var insns = new List<Instruction>();

            while (tail.Count > 0)
            {
                insns.AddRange(AssembleInstruction(ref tail, insnContext));
            }

            var index = module.AddFunction(
                AddOrReuseFunctionType(funType, module),
                new FunctionBody(locals.Select(x => new LocalEntry(x, 1)), insns));
            context.FunctionContext.Define(functionId, new LocalOrImportRef(false, index));
        }

        private static IReadOnlyList<Instruction> AssembleInstruction(
            ref IReadOnlyList<SExpression> instruction,
            InstructionContext context)
        {
            var first = instruction[0];
            if (first.IsKeyword)
            {
                PlainInstructionAssembler assembler;
                if (context.ModuleContext.Assembler.PlainInstructionAssemblers.TryGetValue(
                    (string)first.Head.Value,
                    out assembler))
                {
                    instruction = instruction.Skip(1).ToArray();
                    return new[] { assembler(first, ref instruction, context) };
                }
                else
                {
                    context.ModuleContext.Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            Quotation.QuoteEvenInBold(
                                "unknown instruction keyword ",
                                first.Head.Span.Text,
                                "."),
                            Highlight(first)));
                    instruction = Array.Empty<SExpression>();
                    return Array.Empty<Instruction>();
                }
            }
            else
            {
                context.ModuleContext.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "expected an instruction; got ",
                            first.Head.Span.Text,
                            " instead."),
                        Highlight(first)));
                instruction = Array.Empty<SExpression>();
                return Array.Empty<Instruction>();
            }
        }

        private static List<WasmValueType> AssembleLocals(
            ref IReadOnlyList<SExpression> tail,
            Dictionary<string, uint> localIdentifiers,
            ModuleContext context,
            string localKeyword,
            int parameterCount)
        {
            var locals = new List<WasmValueType>();

            // Parse locals.
            while (tail.Count > 0 && tail[0].IsCallTo(localKeyword))
            {
                var paramSpec = tail[0];
                var paramTail = paramSpec.Tail;
                if (paramTail.Count > 0 && paramTail[0].IsIdentifier)
                {
                    var id = (string)paramTail[0].Head.Value;
                    paramTail = paramTail.Skip(1).ToArray();
                    if (!AssertNonEmpty(paramSpec, paramTail, localKeyword, context))
                    {
                        continue;
                    }

                    var valType = AssembleValueType(paramTail[0], context);
                    locals.Add(valType);

                    paramTail = paramTail.Skip(1).ToArray();
                    AssertEmpty(context, localKeyword, paramTail);

                    if (localIdentifiers != null)
                    {
                        localIdentifiers[id] = (uint)(parameterCount + locals.Count);
                    }
                }
                else
                {
                    locals.AddRange(paramTail.Select(x => AssembleValueType(x, context)));
                }
                tail = tail.Skip(1).ToArray();
            }

            return locals;
        }

        private static TableType AssembleTableType(SExpression parent, IReadOnlyList<SExpression> tail, ModuleContext context)
        {
            if (tail.Count < 2 || tail.Count > 3)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "expected a table type, that is, resizable limits followed by ",
                            "funcref",
                            ", the table element type."),
                        Highlight(parent)));
                return new TableType(WasmType.AnyFunc, new ResizableLimits(0));
            }

            var limits = AssembleLimits(parent, tail.Take(tail.Count - 1).ToArray(), context);
            var elemType = AssembleElemType(tail[tail.Count - 1], context);
            return new TableType(WasmType.AnyFunc, limits);
        }

        private static WasmType AssembleElemType(SExpression expression, ModuleContext context)
        {
            if (!expression.IsSpecificKeyword("funcref"))
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "unexpected table type expression; expected ",
                            "funcref", "."),
                        Highlight(expression)));
            }
            return WasmType.AnyFunc;
        }

        private static GlobalType AssembleGlobalType(SExpression expression, ModuleContext context)
        {
            if (expression.IsCallTo("mut"))
            {
                if (!AssertElementCount(expression, expression.Tail, 1, context))
                {
                    return new GlobalType(WasmValueType.Int32, true);
                }

                return new GlobalType(AssembleValueType(expression.Tail[0], context), true);
            }
            else
            {
                return new GlobalType(AssembleValueType(expression, context), false);
            }
        }

        private static FunctionType AssembleTypeUse(
            SExpression parent,
            ref IReadOnlyList<SExpression> tail,
            ModuleContext context,
            WasmFile module,
            bool allowTypeRef = false,
            Dictionary<string, uint> parameterIdentifiers = null)
        {
            var result = new FunctionType();

            FunctionType referenceType = null;
            if (allowTypeRef && tail.Count > 0 && tail[0].IsCallTo("type"))
            {
                var typeRef = tail[0];
                tail = tail.Skip(1).ToArray();
                if (AssertElementCount(typeRef, typeRef.Tail, 1, context))
                {
                    uint referenceTypeIndex;
                    if ((typeRef.Tail[0].IsIdentifier
                        && context.TypeContext.TryGetDefinition((string)typeRef.Tail[0].Head.Value, out referenceTypeIndex)))
                    {
                    }
                    else if (!typeRef.Tail[0].IsCall
                        && typeRef.Tail[0].Head.Kind == Lexer.TokenKind.UnsignedInteger)
                    {
                        referenceTypeIndex = AssembleUInt32(typeRef.Tail[0], context);
                    }
                    else
                    {
                        context.Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "syntax error",
                                "expected an identifier or an unsigned integer.",
                                Highlight(typeRef.Tail[0])));
                        return result;
                    }

                    var funTypes = module.GetFirstSectionOrNull<TypeSection>();
                    if (referenceTypeIndex >= funTypes.FunctionTypes.Count)
                    {
                        context.Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "syntax error",
                                Quotation.QuoteEvenInBold(
                                    "index ", referenceTypeIndex.ToString(), " does not correspond to a type."),
                                Highlight(typeRef.Tail[0])));
                        return result;
                    }

                    referenceType = funTypes.FunctionTypes[(int)referenceTypeIndex];

                    if (tail.Count == 0)
                    {
                        return referenceType;
                    }
                }
            }

            // Parse parameters.
            result.ParameterTypes.AddRange(AssembleLocals(ref tail, parameterIdentifiers, context, "param", 0));

            // Parse results.
            while (tail.Count > 0 && tail[0].IsCallTo("result"))
            {
                var resultSpec = tail[0];
                var resultTail = resultSpec.Tail;
                result.ReturnTypes.AddRange(resultTail.Select(x => AssembleValueType(x, context)));
                tail = tail.Skip(1).ToArray();
            }

            if (referenceType != null)
            {
                if (ConstFunctionTypeComparer.Instance.Equals(referenceType, result))
                {
                    return referenceType;
                }
                else
                {
                    context.Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            Quotation.QuoteEvenInBold(
                                "expected locally-defined type ",
                                result.ToString(),
                                " to equal previously-defined type ",
                                referenceType.ToString(),
                                "."),
                            Highlight(parent)));
                }
            }

            return result;
        }

        private static uint AddOrReuseFunctionType(
            FunctionType type,
            WasmFile module)
        {
            var sec = module.GetFirstSectionOrNull<TypeSection>();
            if (sec == null)
            {
                return module.AddFunctionType(type);
            }
            else
            {
                var index = sec.FunctionTypes.FindIndex(x => ConstFunctionTypeComparer.Instance.Equals(type, x));
                if (index < 0)
                {
                    return module.AddFunctionType(type);
                }
                else
                {
                    return (uint)index;
                }
            }
        }

        private static WasmValueType AssembleValueType(SExpression expression, ModuleContext context)
        {
            if (expression.IsSpecificKeyword("i32"))
            {
                return WasmValueType.Int32;
            }
            else if (expression.IsSpecificKeyword("i64"))
            {
                return WasmValueType.Int64;
            }
            else if (expression.IsSpecificKeyword("f32"))
            {
                return WasmValueType.Float32;
            }
            else if (expression.IsSpecificKeyword("f64"))
            {
                return WasmValueType.Float64;
            }
            else
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "unexpected token",
                        Quotation.QuoteEvenInBold(
                            "unexpected a value type, that is, ",
                            "i32", ",", "i64", ",", "f32", " or ", "f64", "."),
                        Highlight(expression)));
                return WasmValueType.Int32;
            }
        }

        private static void AddExport<T>(
            WasmFile module,
            IdentifierContext<T> context,
            T value,
            ExternalKind kind,
            string exportName)
        {
            var export = new ExportedValue(exportName, kind, 0);
            module.AddExport(export);
            var exportSection = module.GetFirstSectionOrNull<ExportSection>();
            int index = exportSection.Exports.Count - 1;
            context.Use(
                value,
                i => { exportSection.Exports[index] = new ExportedValue(exportName, kind, i); });
        }

        private static void AddExport<T>(
            WasmFile module,
            IdentifierContext<T> context,
            Lexer.Token identifier,
            ExternalKind kind,
            string exportName)
        {
            var export = new ExportedValue(exportName, kind, 0);
            module.AddExport(export);
            var exportSection = module.GetFirstSectionOrNull<ExportSection>();
            int index = exportSection.Exports.Count - 1;
            context.Use(
                identifier,
                i => { exportSection.Exports[index] = new ExportedValue(exportName, kind, i); });
        }

        private static Lexer.Token AssembleIdentifierOrIndex(SExpression expression, ModuleContext context)
        {
            if (expression.Head.Kind != Lexer.TokenKind.UnsignedInteger
                && expression.Head.Kind != Lexer.TokenKind.Identifier)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        "expected an identifier or unsigned integer.",
                        Highlight(expression)));
            }
            return expression.Head;
        }

        private static (string, string) AssembleInlineImport(SExpression import, ModuleContext context)
        {
            if (import.Tail.Count != 2)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "encountered ",
                            import.Tail.Count.ToString(),
                            " elements; expected exactly two names."),
                        Highlight(import)));
                return ("", "");
            }
            else
            {
                return (AssembleString(import.Tail[0], context), AssembleString(import.Tail[1], context));
            }
        }

        private static string AssembleString(SExpression expression, ModuleContext context)
        {
            if (expression.IsCall || expression.Head.Kind != Lexer.TokenKind.String)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "expected a string literal."),
                        Highlight(expression)));
                return "";
            }
            else
            {
                return (string)expression.Head.Value;
            }
        }

        private static bool AssertElementCount(
            SExpression expression,
            IReadOnlyList<SExpression> tail,
            int count,
            ModuleContext context)
        {
            if (tail.Count == count)
            {
                return true;
            }
            else
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(
                            "encountered ",
                            tail.Count.ToString(),
                            " elements; expected exactly ", count.ToString(), "."),
                        Highlight(expression)));
                return false;
            }
        }

        private static bool AssertNonEmpty(
            SExpression expression,
            IReadOnlyList<SExpression> tail,
            string kind,
            ModuleContext context)
        {
            if (tail.Count == 0)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold(kind + " is unexpectedly empty."),
                        Highlight(expression)));
                return false;
            }
            else
            {
                return true;
            }
        }

        private static byte[] AssembleDataString(
            IReadOnlyList<SExpression> tail,
            ModuleContext context)
        {
            var results = new List<byte>();
            foreach (var item in tail)
            {
                results.AddRange(Encoding.UTF8.GetBytes(AssembleString(item, context)));
            }
            return results.ToArray();
        }

        private static void AssertEmpty(
            ModuleContext context,
            string kind,
            IEnumerable<SExpression> tail)
        {
            var tailArray = tail.ToArray();
            if (tailArray.Length > 0)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold("", kind, " has an unexpected trailing expression."),
                        Highlight(tailArray[0])));
            }
        }

        private static ResizableLimits AssembleLimits(SExpression parent, IReadOnlyList<SExpression> tail, ModuleContext context)
        {
            if (tail.Count == 0)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        "limits expression is empty.",
                        Highlight(parent)));
                return new ResizableLimits(0);
            }

            var init = AssembleUInt32(tail[0], context);

            if (tail.Count == 1)
            {
                return new ResizableLimits(init);
            }
            if (tail.Count == 2)
            {
                var max = AssembleUInt32(tail[1], context);
                return new ResizableLimits(init, max);
            }
            else
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        "limits expression contains more than two elements.",
                        Highlight(tail[2])));
                return new ResizableLimits(0);
            }
        }

        private static uint AssembleUInt32(
            SExpression expression,
            ModuleContext context)
        {
            return AssembleInt<uint>(
                expression,
                context,
                "32-bit unsigned integer",
                new[] { Lexer.TokenKind.UnsignedInteger },
                (kind, data) => data <= uint.MaxValue ? (uint)data : (uint?)null);
        }

        private static int AssembleSignlessInt32(
            SExpression expression,
            ModuleContext context)
        {
            return AssembleInt<int>(
                expression,
                context,
                "32-bit integer",
                new[] { Lexer.TokenKind.UnsignedInteger, Lexer.TokenKind.SignedInteger },
                (kind, data) => {
                    if (expression.Head.Kind == Lexer.TokenKind.UnsignedInteger && data <= uint.MaxValue)
                    {
                        return (int)(uint)data;
                    }
                    else if (data >= int.MinValue && data <= int.MaxValue)
                    {
                        return (int)data;
                    }
                    else
                    {
                        return null;
                    }
                });
        }

        private static long AssembleSignlessInt64(
            SExpression expression,
            ModuleContext context)
        {
            return AssembleInt<long>(
                expression,
                context,
                "64-bit integer",
                new[] { Lexer.TokenKind.UnsignedInteger, Lexer.TokenKind.SignedInteger },
                (kind, data) => {
                    if (expression.Head.Kind == Lexer.TokenKind.UnsignedInteger && data <= ulong.MaxValue)
                    {
                        return (long)(ulong)data;
                    }
                    else if (data >= long.MinValue && data <= long.MaxValue)
                    {
                        return (long)data;
                    }
                    else
                    {
                        return null;
                    }
                });
        }

        private static T AssembleInt<T>(
            SExpression expression,
            ModuleContext context,
            string description,
            Lexer.TokenKind[] acceptableKinds,
            Func<Lexer.TokenKind, BigInteger, T?> tryCoerceInt)
            where T : struct
        {
            if (expression.IsCall)
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        $"expected a {description}; got a call.",
                        Highlight(expression)));
                return default(T);
            }
            else if (!acceptableKinds.Contains(expression.Head.Kind))
            {
                context.Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "syntax error",
                        Quotation.QuoteEvenInBold($"expected a {description}; token ", expression.Head.Span.Text, "."),
                        Highlight(expression)));
                return default(T);
            }
            else
            {
                var data = (BigInteger)expression.Head.Value;
                var coerced = tryCoerceInt(expression.Head.Kind, data);
                if (coerced.HasValue)
                {
                    return coerced.Value;
                }
                else
                {
                    context.Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "syntax error",
                            $"expected a {description}; got an integer that is out of range.",
                            Highlight(expression)));
                    return default(T);
                }
            }
        }
    }
}
