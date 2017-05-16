using System;
using System.Collections.Generic;

namespace Wasm.Interpret
{
    /// <summary>
    /// Represents an instance of a WebAssembly module.
    /// </summary>
    public sealed class ModuleInstance
    {

        private ModuleInstance(InstructionInterpreter Interpreter)
        {
            this.interpreter = Interpreter;
            this.definedMemories = new List<LinearMemory>();
            this.definedGlobals = new List<Variable>();
            this.definedFuncs = new List<Func<IReadOnlyList<object>, object>>();
        }

        /// <summary>
        /// The interpreter for this module instance.
        /// </summary>
        private InstructionInterpreter interpreter; 

        private List<LinearMemory> definedMemories;
        private List<Variable> definedGlobals;
        private List<Func<IReadOnlyList<object>, object>> definedFuncs;

        /// <summary>
        /// Gets a read-only list of the memories in this module.
        /// </summary>
        public IReadOnlyList<LinearMemory> Memories => definedMemories;

        /// <summary>
        /// Gets a read-only list of global variables in this module.
        /// </summary>
        public IReadOnlyList<Variable> Globals => definedGlobals;

        /// <summary>
        /// Evaluates the given initializer expression.
        /// </summary>
        /// <param name="Expression">The expression to evaluate.</param>
        /// <returns>The value obtained by evaluating the initializer expression.</returns>
        public T Evaluate<T>(InitializerExpression Expression)
        {
            var context = new InterpreterContext(this);
            foreach (var instruction in Expression.BodyInstructions)
            {
                interpreter.Interpret(instruction, context);
            }
            var result = context.Pop<T>();
            if (context.StackDepth > 0)
            {
                throw new WasmException(
                    "The stack must contain exactly one value after " +
                    "evaluating an initializer expression. Actual stack depth: " +
                    context.StackDepth + ".");
            }
            return result;
        }

        /// <summary>
        /// Instantiates the given WebAssembly file. An importer is used to
        /// resolve module imports.
        /// </summary>
        /// <param name="File">The file to instantiate.</param>
        /// <param name="Importer">Resolves module imports.</param>
        /// <returns>A module instance.</returns>
        public static ModuleInstance Instantiate(WasmFile File, IImporter Importer)
        {
            return Instantiate(File, Importer, new DefaultInstructionInterpreter());
        }

        /// <summary>
        /// Instantiates the given WebAssembly file. An importer is used to
        /// resolve module imports and an interpreter is used to interpret
        /// instructions.
        /// </summary>
        /// <param name="File">The file to instantiate.</param>
        /// <param name="Importer">Resolves module imports.</param>
        /// <param name="Interpreter">Interprets instructions.</param>
        /// <returns>A module instance.</returns>
        public static ModuleInstance Instantiate(
            WasmFile File,
            IImporter Importer,
            InstructionInterpreter Interpreter)
        {
            var instance = new ModuleInstance(Interpreter);

            // Resolve all imports.
            instance.ResolveImports(File, Importer);

            // Instantiate global variables.
            instance.InstantiateGlobals(File);

            // Instantiate memories.
            instance.InstantiateMemories(File);

            return instance;
        }

        /// <summary>
        /// Uses the given importer to resolve all imported values.
        /// </summary>
        /// <param name="Importer">The importer.</param>
        private void ResolveImports(WasmFile File, IImporter Importer)
        {
            var allImportSections = File.GetSections<ImportSection>();
            for (int i = 0; i < allImportSections.Count; i++)
            {
                var importSection = allImportSections[i];
                foreach (var import in importSection.Imports)
                {
                    if (import is ImportedMemory)
                    {
                        definedMemories.Add(Importer.ImportMemory((ImportedMemory)import));
                    }
                    else if (import is ImportedGlobal)
                    {
                        definedGlobals.Add(Importer.ImportGlobal((ImportedGlobal)import));
                    }
                    else if (import is ImportedFunction)
                    {
                        definedFuncs.Add(Importer.ImportFunction((ImportedFunction)import));
                    }
                    else
                    {
                        throw new WasmException("Unknown import type: " + import.ToString());
                    }
                }
            }
        }

        private void InstantiateMemories(WasmFile File)
        {
            // Create module-defined memories.
            var allMemorySections = File.GetSections<MemorySection>();
            for (int i = 0; i < allMemorySections.Count; i++)
            {
                var memorySection = allMemorySections[i];
                foreach (var memorySpec in memorySection.Memories)
                {
                    definedMemories.Add(new LinearMemory(memorySpec.Limits));
                }
            }

            // Initialize memories by applying the segments defined by data sections.
            var allDataSections = File.GetSections<DataSection>();
            for (int i = 0; i < allDataSections.Count; i++)
            {
                var dataSection = allDataSections[i];
                foreach (var segment in dataSection.Segments)
                {
                    var memoryView = Memories[(int)segment.MemoryIndex].Int8;
                    var evalOffset = Evaluate<int>(segment.Offset);
                    for (int j = 0; j < segment.Data.Length; j++)
                    {
                        memoryView[(uint)(evalOffset + j)] = (sbyte)segment.Data[j];
                    }
                }
            }
        }

        private void InstantiateGlobals(WasmFile File)
        {
            // Create module-defined globals.
            var allGlobalSections = File.GetSections<GlobalSection>();
            for (int i = 0; i < allGlobalSections.Count; i++)
            {
                var globalSection = allGlobalSections[i];
                foreach (var globalSpec in globalSection.GlobalVariables)
                {
                    definedGlobals.Add(
                        Variable.Create<object>(
                            globalSpec.Type.ContentType,
                            globalSpec.Type.IsMutable,
                            Evaluate<object>(globalSpec.InitialValue)));
                }
            }
        }
    }
}