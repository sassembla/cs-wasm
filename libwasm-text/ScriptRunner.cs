using System;
using System.Collections.Generic;
using System.IO;
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
        /// <param name="compiler">A compiler to use for compiling modules.</param>
        public ScriptRunner(ILog log, Func<ModuleCompiler> compiler = null)
        {
            this.Log = log;
            this.Assembler = new Assembler(log);
            this.Compiler = compiler;
            this.moduleInstances = new List<ModuleInstance>();
            this.moduleInstancesByName = new Dictionary<string, ModuleInstance>();
            this.importer = new NamespacedImporter();
            this.importer.RegisterImporter("spectest", new SpecTestImporter(new StringWriter()));
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

        /// <summary>
        /// Gets the type of compiler to use.
        /// </summary>
        /// <value>A function that produces a module compiler.</value>
        public Func<ModuleCompiler> Compiler { get; private set; }

        private NamespacedImporter importer;

        private List<ModuleInstance> moduleInstances;
        private Dictionary<string, ModuleInstance> moduleInstancesByName;

        /// <summary>
        /// A data structure that tallies the number of tests that were run.
        /// </summary>
        public struct TestStatistics
        {
            /// <summary>
            /// Initializes an instance of the <see cref="TestStatistics"/> type.
            /// </summary>
            /// <param name="successfulCommandCount">
            /// The number of commands that were executed successfully.
            /// </param>
            /// <param name="failedCommandCount">
            /// The number of commands that were executed unsuccessfully.
            /// </param>
            /// <param name="unknownCommandCount">
            /// The number of command expressions that were skipped because they were not recognized as a known command.
            /// </param>
            public TestStatistics(int successfulCommandCount, int failedCommandCount, int unknownCommandCount)
            {
                this.SuccessfulCommandCount = successfulCommandCount;
                this.FailedCommandCount = failedCommandCount;
                this.UnknownCommandCount = unknownCommandCount;
            }

            /// <summary>
            /// Gets the number of commands that were recognized and successfully executed.
            /// </summary>
            /// <value>The number of successfully executed commands.</value>
            public int SuccessfulCommandCount { get; private set; }

            /// <summary>
            /// Gets the number of commands that were recognized and executed, but then errored in some way.
            /// </summary>
            /// <value>The number of commands that failed.</value>
            public int FailedCommandCount { get; private set; }

            /// <summary>
            /// Gets the number of command expressions that could not be recognized as a
            /// known command and were hence skipped.
            /// </summary>
            /// <value>The number of unrecognized commands.</value>
            public int UnknownCommandCount { get; private set; }

            /// <summary>
            /// Gets the total number of commands expressions that were encountered.
            /// </summary>
            public int TotalCommandCount => SuccessfulCommandCount + FailedCommandCount + UnknownCommandCount;

            /// <summary>
            /// Computes the elementwise sum of two test statistics.
            /// </summary>
            /// <param name="left">A first set of test statistics.</param>
            /// <param name="right">A second set of test statistics.</param>
            /// <returns>The elementwise sum of <paramref name="left"/> and <paramref name="right"/>.</returns>
            public static TestStatistics operator+(TestStatistics left, TestStatistics right)
            {
                return new TestStatistics(
                    left.SuccessfulCommandCount + right.SuccessfulCommandCount,
                    left.FailedCommandCount + right.FailedCommandCount,
                    left.UnknownCommandCount + right.UnknownCommandCount);
            }

            /// <summary>
            /// An empty set of test statistics: the test statistics for a file that
            /// does not execute any commands.
            /// </summary>
            public static readonly TestStatistics Empty = new TestStatistics(0, 0, 0);

            /// <summary>
            /// A set of test statistics that represent the successful execution of a single command.
            /// </summary>
            public static readonly TestStatistics SingleSuccess = new TestStatistics(1, 0, 0);

            /// <summary>
            /// A set of test statistics that represent a single failed command.
            /// </summary>
            public static readonly TestStatistics SingleFailure = new TestStatistics(0, 1, 0);

            /// <summary>
            /// A set of test statistics that represent a single unknown command.
            /// </summary>
            public static readonly TestStatistics SingleUnknown = new TestStatistics(0, 0, 1);

            /// <inheritdoc/>
            public override string ToString()
            {
                return $"total: {TotalCommandCount}, successes: {SuccessfulCommandCount}, " +
                    $"failures: {FailedCommandCount}, unknown: {UnknownCommandCount}";
            }
        }

        /// <summary>
        /// Runs a script, encoded as a sequence of expressions.
        /// </summary>
        /// <param name="expressions">The script, parsed as a sequence of expressions.</param>
        public TestStatistics Run(IEnumerable<SExpression> expressions)
        {
            var results = TestStatistics.Empty;
            foreach (var item in expressions)
            {
                results += Run(item);
            }
            return results;
        }

        /// <summary>
        /// Runs a script, encoded as a sequence of tokens.
        /// </summary>
        /// <param name="tokens">The script, parsed as a sequence of tokens.</param>
        public TestStatistics Run(IEnumerable<Lexer.Token> tokens)
        {
            return Run(Parser.ParseAsSExpressions(tokens, Log));
        }

        /// <summary>
        /// Runs a script, encoded as a string.
        /// </summary>
        /// <param name="script">The text of the script to run.</param>
        /// <param name="scriptName">The file name of the script to run.</param>
        public TestStatistics Run(string script, string scriptName = "<string>")
        {
            return Run(Lexer.Tokenize(script, scriptName));
        }

        /// <summary>
        /// Runs a single expression in the script.
        /// </summary>
        /// <param name="expression">The expression to run.</param>
        public TestStatistics Run(SExpression expression)
        {
            if (expression.IsCallTo("module"))
            {
                var module = Assembler.AssembleModule(expression, out string moduleId);
                var instance = Wasm.Interpret.ModuleInstance.Instantiate(
                    module,
                    importer,
                    policy: ExecutionPolicy.Create(maxMemorySize: 0x1000),
                    compiler: Compiler);

                moduleInstances.Add(instance);
                if (moduleId != null)
                {
                    moduleInstancesByName[moduleId] = instance;
                }
                if (module.StartFunctionIndex.HasValue)
                {
                    instance.Functions[(int)module.StartFunctionIndex.Value].Invoke(Array.Empty<object>());
                }
                return TestStatistics.Empty;
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
                return TestStatistics.Empty;
            }
            else if (expression.IsCallTo("invoke") || expression.IsCallTo("get"))
            {
                RunAction(expression);
                return TestStatistics.SingleSuccess;
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
                    return TestStatistics.SingleFailure;
                }

                bool failures = false;
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
                            failures = true;
                        }
                    }
                }
                return failures ? TestStatistics.SingleFailure : TestStatistics.SingleSuccess;
            }
            else if (expression.IsCallTo("assert_trap") || expression.IsCallTo("assert_exhaustion"))
            {
                var expected = Assembler.AssembleString(expression.Tail[1], Log);
                bool caught = false;
                Exception exception = null;
                try
                {
                    if (expression.Tail[0].IsCallTo("module"))
                    {
                        Run(expression.Tail[0]);
                    }
                    else
                    {
                        RunAction(expression.Tail[0], false);
                    }
                }
                catch (TrapException ex)
                {
                    caught = ex.SpecMessage == expected;
                    exception = ex;
                }
                catch (PixieException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    caught = false;
                    exception = ex;
                }

                if (caught)
                {
                    return TestStatistics.SingleSuccess;
                }
                else
                {
                    if (exception == null)
                    {
                        Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "assertion failed",
                                "action should have trapped, but didn't.",
                                Assembler.Highlight(expression)));
                    }
                    else
                    {
                        Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "assertion failed",
                                "action trapped as expected, but with an unexpected exception. ",
                                exception.ToString(),
                                Assembler.Highlight(expression)));
                    }
                    return TestStatistics.SingleFailure;
                }
            }
            else if (expression.IsCallTo("assert_return_canonical_nan"))
            {
                var results = RunAction(expression.Tail[0]);
                bool isCanonicalNaN;
                if (results.Count != 1)
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "assertion failed",
                            "action produced ",
                            results.Count.ToString(),
                            " results (",
                            string.Join(", ", results),
                            "); expected a single canonical NaN.",
                            Assembler.Highlight(expression)));
                    return TestStatistics.SingleFailure;
                }
                else if (results[0] is double)
                {
                    var val = Interpret.ValueHelpers.ReinterpretAsInt64((double)results[0]);
                    isCanonicalNaN = val == Interpret.ValueHelpers.ReinterpretAsInt64((double)FloatLiteral.NaN(false))
                        || val == Interpret.ValueHelpers.ReinterpretAsInt64((double)FloatLiteral.NaN(true));
                }
                else if (results[0] is float)
                {
                    var val = Interpret.ValueHelpers.ReinterpretAsInt32((float)results[0]);
                    isCanonicalNaN = val == Interpret.ValueHelpers.ReinterpretAsInt32((float)FloatLiteral.NaN(false))
                        || val == Interpret.ValueHelpers.ReinterpretAsInt32((float)FloatLiteral.NaN(true));
                }
                else
                {
                    isCanonicalNaN = false;
                }
                if (isCanonicalNaN)
                {
                    return TestStatistics.SingleSuccess;
                }
                else
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "assertion failed",
                            "action produced ",
                            results[0].ToString(),
                            "; expected a single canonical NaN.",
                            Assembler.Highlight(expression)));
                    return TestStatistics.SingleFailure;
                }
            }
            else if (expression.IsCallTo("assert_return_arithmetic_nan"))
            {
                var results = RunAction(expression.Tail[0]);
                bool isNaN;
                if (results.Count != 1)
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "assertion failed",
                            "action produced ",
                            results.Count.ToString(),
                            " results (",
                            string.Join(", ", results),
                            "); expected a single NaN.",
                            Assembler.Highlight(expression)));
                    return TestStatistics.SingleFailure;
                }
                else if (results[0] is double)
                {
                    isNaN = double.IsNaN((double)results[0]);
                }
                else if (results[0] is float)
                {
                    isNaN = float.IsNaN((float)results[0]);
                }
                else
                {
                    isNaN = false;
                }
                if (isNaN)
                {
                    return TestStatistics.SingleSuccess;
                }
                else
                {
                    Log.Log(
                        new LogEntry(
                            Severity.Error,
                            "assertion failed",
                            "action produced ",
                            results[0].ToString(),
                            "; expected a single NaN.",
                            Assembler.Highlight(expression)));
                    return TestStatistics.SingleFailure;
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
                return TestStatistics.SingleUnknown;
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

        private IReadOnlyList<object> RunAction(SExpression expression, bool reportExceptions = true)
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
                        if (TryInvokeNamedFunction(inst, name, args, expression, out IReadOnlyList<object> results, reportExceptions))
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
                        if (TryInvokeNamedFunction(inst, name, args, expression, out IReadOnlyList<object> results, reportExceptions))
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
            out IReadOnlyList<object> results,
            bool reportExceptions = true)
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
                    if (reportExceptions)
                    {
                        Log.Log(
                            new LogEntry(
                                Severity.Error,
                                "unhandled exception",
                                $"function invocation threw {ex.GetType().Name}",
                                new Paragraph(ex.ToString()),
                                Assembler.Highlight(expression)));
                    }
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
