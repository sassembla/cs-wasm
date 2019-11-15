using System;
using System.Collections.Generic;
using System.Linq;
using Pixie;
using Pixie.Markup;
using Wasm.Interpret;

namespace Wasm.Text
{
    /// <summary>
    /// Maintains state for and runs a single WebAssembly test script.
    /// </summary>
    public sealed class ScriptRunner
    {
        /// <summary>
        /// Creates a new script runner.
        /// </summary>
        /// <param name="log">A log to send diagnostics to.</param>
        public ScriptRunner(ILog log)
        {
            this.Log = log;
            this.Assembler = new Assembler(log);
            this.moduleInstances = new List<ModuleInstance>();
            this.moduleInstancesByName = new Dictionary<string, ModuleInstance>();
            this.importer = new NamespacedImporter();
            this.importer.RegisterImporter("spectest", new SpecTestImporter());
        }

        /// <summary>
        /// Gets a log to which this script runner sends diagnostics.
        /// </summary>
        /// <value>A log.</value>
        public ILog Log { get; private set; }

        /// <summary>
        /// Gets the assembler that assembles modules for this script runner.
        /// </summary>
        /// <value>A WebAssembly text format assembler.</value>
        public Assembler Assembler { get; private set; }

        private NamespacedImporter importer;

        private List<ModuleInstance> moduleInstances;
        private Dictionary<string, ModuleInstance> moduleInstancesByName;

        /// <summary>
        /// Runs a script, encoded as a sequence of expressions.
        /// </summary>
        /// <param name="expressions">The script, parsed as a sequence of expressions.</param>
        public void Run(IEnumerable<SExpression> expressions)
        {
            foreach (var item in expressions)
            {
                Run(item);
            }
        }

        /// <summary>
        /// Runs a script, encoded as a sequence of tokens.
        /// </summary>
        /// <param name="tokens">The script, parsed as a sequence of tokens.</param>
        public void Run(IEnumerable<Lexer.Token> tokens)
        {
            Run(Parser.ParseAsSExpressions(tokens, Log));
        }

        /// <summary>
        /// Runs a script, encoded as a string.
        /// </summary>
        /// <param name="script">The text of the script to run.</param>
        /// <param name="scriptName">The file name of the script to run.</param>
        public void Run(string script, string scriptName = "<string>")
        {
            Run(Lexer.Tokenize(script, scriptName));
        }

        /// <summary>
        /// Runs a single expression in the script.
        /// </summary>
        /// <param name="expression">The expression to run.</param>
        public void Run(SExpression expression)
        {
            if (expression.IsCallTo("module"))
            {
                var module = Assembler.AssembleModule(expression, out string moduleId);
                var instance = Wasm.Interpret.ModuleInstance.Instantiate(module, importer, maxMemorySize: 0x1000);
                moduleInstances.Add(instance);
                if (moduleId != null)
                {
                    moduleInstancesByName[moduleId] = instance;
                }
                if (module.StartFunctionIndex.HasValue)
                {
                    instance.Functions[(int)module.StartFunctionIndex.Value].Invoke(Array.Empty<object>());
                }
            }
            else if (expression.IsCallTo("register"))
            {
                var tail = expression.Tail;
                var name = Assembler.AssembleString(tail[0], Log);
                tail = tail.Skip(1).ToArray();
                var moduleId = Assembler.AssembleLabelOrNull(ref tail);
                if (moduleId == null)
                {
                    importer.RegisterImporter(name, new ModuleExportsImporter(moduleInstances[moduleInstances.Count - 1]));
                }
                else
                {
                    importer.RegisterImporter(name, new ModuleExportsImporter(moduleInstancesByName[moduleId]));
                }
            }
            else if (expression.IsCallTo("invoke") || expression.IsCallTo("get"))
            {
                RunAction(expression);
            }
            else if (expression.IsCallTo("assert_return"))
            {
                var results = RunAction(expression.Tail[0]);
                var expected = expression.Tail
                    .Skip(1)
                    .Zip(results, (expr, val) => EvaluateConstExpr(expr, val.GetType()))
                    .ToArray();

                if (expected.Length != results.Count)
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "assertion failed",
                            "action produced result ",
                            string.Join(", ", results),
                            "; expected ",
                            string.Join(", ", expected),
                            ".",
                            Assembler.Highlight(expression)));
                    return;
                }

                for (int i = 0; i < expected.Length; i++)
                {
                    if (!object.Equals(results[i], expected[i]))
                    {
                        if (AlmostEquals(results[i], expected[i]))
                        {
                            Log.Log(
                                new LogEntry(
                                    Severity.Warning,
                                    "rounding error",
                                    "action produced result ",
                                    results[i].ToString(),
                                    "; expected ",
                                    expected[i].ToString(),
                                    ".",
                                    Assembler.Highlight(expression)));
                        }
                        else
                        {
                            Log.Log(
                                new LogEntry(
                                    Severity.Error,
                                    "assertion failed",
                                    "action produced result ",
                                    results[i].ToString(),
                                    "; expected ",
                                    expected[i].ToString(),
                                    ".",
                                    Assembler.Highlight(expression)));
                        }
                    }
                }
            }
            else
            {
                Log.Log(
                    new LogEntry(
                        Severity.Warning,
                        "unknown script command",
                        Quotation.QuoteEvenInBold(
                            "expression ",
                            expression.Head.Span.Text,
                            " was not recognized as a known script command."),
                        Assembler.Highlight(expression)));
            }
        }

        private static bool AlmostEquals(object value, object expected)
        {
            if (value is float && expected is float)
            {
                return AlmostEquals((float)value, (float)expected, 1);
            }
            else if (value is double && expected is double)
            {
                return AlmostEquals((double)value, (double)expected, 1);
            }
            else
            {
                return false;
            }
        }

        private static bool AlmostEquals(double left, double right, long representationTolerance)
        {
            // Approximate comparison code suggested by Torbjörn Kalin on StackOverflow
            // (https://stackoverflow.com/questions/10419771/comparing-doubles-with-adaptive-approximately-equal).
            long leftAsBits = ToBitsTwosComplement(left);
            long rightAsBits = ToBitsTwosComplement(right);
            long floatingPointRepresentationsDiff = Math.Abs(leftAsBits - rightAsBits);
            return (floatingPointRepresentationsDiff <= representationTolerance);
        }

        private static long ToBitsTwosComplement(double value)
        {
            // Approximate comparison code suggested by Torbjörn Kalin on StackOverflow
            // (https://stackoverflow.com/questions/10419771/comparing-doubles-with-adaptive-approximately-equal).
            long valueAsLong = Interpret.ValueHelpers.ReinterpretAsInt64(value);
            return valueAsLong < 0
                ? (long)(0x8000000000000000 - (ulong)valueAsLong)
                : valueAsLong;
        }

        private static bool AlmostEquals(float left, float right, int representationTolerance)
        {
            // Approximate comparison code suggested by Torbjörn Kalin on StackOverflow
            // (https://stackoverflow.com/questions/10419771/comparing-doubles-with-adaptive-approximately-equal).
            long leftAsBits = ToBitsTwosComplement(left);
            long rightAsBits = ToBitsTwosComplement(right);
            long floatingPointRepresentationsDiff = Math.Abs(leftAsBits - rightAsBits);
            return (floatingPointRepresentationsDiff <= representationTolerance);
        }

        private static int ToBitsTwosComplement(float value)
        {
            // Approximate comparison code suggested by Torbjörn Kalin on StackOverflow
            // (https://stackoverflow.com/questions/10419771/comparing-doubles-with-adaptive-approximately-equal).
            int valueAsInt = Interpret.ValueHelpers.ReinterpretAsInt32(value);
            return valueAsInt < 0
                ? (int)(0x80000000 - (int)valueAsInt)
                : valueAsInt;
        }

        private object EvaluateConstExpr(SExpression expression, WasmValueType resultType)
        {
            var anonModule = new WasmFile();
            var instructions = Assembler.AssembleInstructionExpression(expression, anonModule);
            var inst = ModuleInstance.Instantiate(anonModule, new SpecTestImporter());
            return inst.Evaluate(new InitializerExpression(instructions), resultType);
        }

        private object EvaluateConstExpr(SExpression expression, Type resultType)
        {
            return EvaluateConstExpr(expression, ValueHelpers.ToWasmValueType(resultType));
        }

        private IReadOnlyList<object> RunAction(SExpression expression)
        {
            if (expression.IsCallTo("invoke"))
            {
                var tail = expression.Tail;
                var moduleId = Assembler.AssembleLabelOrNull(ref tail);
                var name = Assembler.AssembleString(tail[0], Log);
                var args = tail.Skip(1);

                if (moduleId == null)
                {
                    foreach (var inst in Enumerable.Reverse(moduleInstances))
                    {
                        if (TryInvokeNamedFunction(inst, name, args, expression, out IReadOnlyList<object> results))
                        {
                            return results;
                        }
                    }

                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "undefined function",
                            Quotation.QuoteEvenInBold(
                                "no function named ",
                                name,
                                " is defined here."),
                            Assembler.Highlight(expression)));
                    return Array.Empty<object>();
                }
                else
                {
                    if (moduleInstancesByName.TryGetValue(moduleId, out ModuleInstance inst))
                    {
                        if (TryInvokeNamedFunction(inst, name, args, expression, out IReadOnlyList<object> results))
                        {
                            return results;
                        }
                        else
                        {
                            Log.Log(
                                new LogEntry(
                                    Severity.Error,
                                    "undefined function",
                                    Quotation.QuoteEvenInBold(
                                        "no function named ",
                                        name,
                                        " is defined in module ",
                                        moduleId,
                                        "."),
                                    Assembler.Highlight(expression)));
                            return Array.Empty<object>();
                        }
                    }
                    else
                    {
                        Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "undefined module",
                                Quotation.QuoteEvenInBold(
                                    "no module named ",
                                    moduleId,
                                    " is defined here."),
                                Assembler.Highlight(expression)));
                        return Array.Empty<object>();
                    }
                }
            }
            else if (expression.IsCallTo("get"))
            {
                var tail = expression.Tail;
                var moduleId = Assembler.AssembleLabelOrNull(ref tail);
                var name = Assembler.AssembleString(tail[0], Log);
                if (moduleId == null)
                {
                    foreach (var inst in moduleInstances)
                    {
                        if (inst.ExportedGlobals.TryGetValue(name, out Variable def))
                        {
                            return new[] { def.Get<object>() };
                        }
                    }

                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "undefined global",
                            Quotation.QuoteEvenInBold(
                                "no global named ",
                                name,
                                " is defined here."),
                            Assembler.Highlight(expression)));
                    return Array.Empty<object>();
                }
                else
                {
                    if (moduleInstancesByName.TryGetValue(moduleId, out ModuleInstance inst))
                    {
                        if (inst.ExportedGlobals.TryGetValue(name, out Variable def))
                        {
                            return new[] { def.Get<object>() };
                        }
                        else
                        {
                            Log.Log(
                                new LogEntry(
                                    Severity.Error,
                                    "undefined global",
                                    Quotation.QuoteEvenInBold(
                                        "no global named ",
                                        name,
                                        " is defined in module ",
                                        moduleId,
                                        "."),
                                    Assembler.Highlight(expression)));
                            return Array.Empty<object>();
                        }
                    }
                    else
                    {
                        Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "undefined module",
                                Quotation.QuoteEvenInBold(
                                    "no module named ",
                                    moduleId,
                                    " is defined here."),
                                Assembler.Highlight(expression)));
                        return Array.Empty<object>();
                    }
                }
            }
            else
            {
                Log.Log(
                    new LogEntry(
                        Severity.Error,
                        "unknown action",
                        Quotation.QuoteEvenInBold(
                            "expression ",
                            expression.Head.Span.Text,
                            " was not recognized as a known script action."),
                        Assembler.Highlight(expression)));
                return Array.Empty<object>();
            }
        }

        private bool TryInvokeNamedFunction(
            ModuleInstance instance,
            string name,
            IEnumerable<SExpression> argumentExpressions,
            SExpression expression,
            out IReadOnlyList<object> results)
        {
            if (instance.ExportedFunctions.TryGetValue(name, out FunctionDefinition def))
            {
                var args = argumentExpressions
                    .Zip(def.ParameterTypes, (expr, type) => EvaluateConstExpr(expr, type))
                    .ToArray();
                try
                {
                    results = def.Invoke(args);
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "unhandled exception",
                            $"function invocation threw {ex.GetType().Name}",
                            new Paragraph(ex.ToString()),
                            Assembler.Highlight(expression)));
                    throw;
                }
            }
            else
            {
                results = null;
                return false;
            }
        }
    }
}
