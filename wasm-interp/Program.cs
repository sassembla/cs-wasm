﻿using System;
using System.Collections.Generic;

namespace Wasm.Interpret
{
    /// <summary>
    /// Represents interpreter command-line options.
    /// </summary>
    public struct InterpreterArguments
    {
        /// <summary>
        /// Gets a path to the WebAssembly file to load.
        /// </summary>
        public string WasmFilePath { get; private set; }

        /// <summary>
        /// Gets the name of the function to run, if any.
        /// </summary>
        public string FunctionToRun { get; private set; }

        /// <summary>
        /// Tries to read command-line options.
        /// </summary>
        public static bool TryRead(string[] Args, out InterpreterArguments ParsedArgs)
        {
            ParsedArgs = default(InterpreterArguments);
            bool expectingRunFuncName = false;
            for (int i = 0; i < Args.Length; i++)
            {
                if (expectingRunFuncName)
                {
                    if (ParsedArgs.FunctionToRun != null)
                    {
                        return false;
                    }

                    ParsedArgs.FunctionToRun = Args[i];
                    expectingRunFuncName = false;
                }
                else if (Args[i] == "--run")
                {
                    expectingRunFuncName = true;
                }
                else
                {
                    if (ParsedArgs.WasmFilePath != null)
                    {
                        return false;
                    }

                    ParsedArgs.WasmFilePath = Args[i];
                }
            }

            return ParsedArgs.WasmFilePath != null
                && !expectingRunFuncName;
        }
    }

    public static class Program
    {
        private static int PrintUsage()
        {
            Console.Error.WriteLine("usage: wasm-interp file.wasm [--run exported_func_name]");
            return 1;
        }

        public static int Main(string[] args)
        {
            // Read command-line arguments.
            InterpreterArguments parsedArgs;
            if (!InterpreterArguments.TryRead(args, out parsedArgs))
            {
                return PrintUsage();
            }

            // Read and instantiate the module.
            var wasmFile = WasmFile.ReadBinary(parsedArgs.WasmFilePath);
            var module = ModuleInstance.Instantiate(wasmFile, new SpecTestImporter());

            // Figure out which function to run.
            FunctionDefinition funcToRun = null;
            if (parsedArgs.FunctionToRun != null)
            {
                if (!module.ExportedFunctions.TryGetValue(parsedArgs.FunctionToRun, out funcToRun))
                {
                    Console.Error.WriteLine(
                        "error: module does not export a function named '" +
                        funcToRun + "'");
                    return 1;
                }
            }
            else
            {
                var startSec = wasmFile.GetFirstSectionOrNull<StartSection>();
                if (startSec == null)
                {
                    Console.Error.WriteLine(
                        "error: module does not define a 'start' section " +
                        " and '--run exported_func_name' was not specified.");
                    return 1;
                }
                else
                {
                    IReadOnlyList<FunctionDefinition> funcs = module.Functions;
                    funcToRun = funcs[(int)startSec.StartFunctionIndex];
                }
            }

            // Run that function.
            int exitCode = 0;
            try
            {
                IReadOnlyList<object> output = funcToRun.Invoke(new object[0]);
                if (output.Count > 0)
                {
                    for (int i = 0; i < output.Count; i++)
                    {
                        if (i > 0)
                        {
                            Console.Write(" ");
                        }
                        Console.Write(output[i]);
                    }
                    Console.WriteLine();
                }
            }
            catch (WasmException ex)
            {
                Console.Error.WriteLine("error: " + ex.Message);
                exitCode = 1;
            }
            return exitCode;
        }
    }
}