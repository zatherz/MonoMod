﻿using Mono.Cecil;
using MonoMod.DebugIL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MonoMod {
    class Program {

#if !MONOMOD_NO_ENTRY
        public static void Main(string[] args) {
            Console.WriteLine("MonoMod " + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version);

            if (args.Length == 0) {
                Console.WriteLine("No valid arguments (assembly path) passed.");
                if (System.Diagnostics.Debugger.IsAttached) // Keep window open when running in IDE
                    Console.ReadKey();
                return;
            }

            string pathIn;
            string pathOut;

            if (args.Length > 1 &&
                args[0] == "--generate-debug-il" ||
                args[0] == "--gen-dbg-il") {
                Console.WriteLine("[DbgILGen] Generating debug hierarchy and debug data (pdb / mdb).");

                pathIn = args[1];
                pathOut = args.Length != 2 ? args[args.Length - 1] : Path.Combine(Path.GetDirectoryName(pathIn), "MMDBGIL_" + Path.GetFileName(pathIn));

                using (MonoModder mm = new MonoModder() {
                    InputPath = pathIn,
                    OutputPath = pathOut
                }) {
                    mm.Read(false);

                    mm.Log("[DbgILGen] DebugILGenerator.Generate(mm);");
                    DebugILGenerator.Generate(mm);

                    mm.Write();

                    mm.Log("[DbgILGen] Done.");
                }

                if (System.Diagnostics.Debugger.IsAttached) // Keep window open when running in IDE
                    Console.ReadKey();
                return;
            }

            int pathInI = 0;

            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--dependency-missing-throw=0" || args[i] == "--lean-dependencies") {
                    Environment.SetEnvironmentVariable("MONOMOD_DEPENDENCY_MISSING_THROW", "0");
                    pathInI = i + 1;
                } else if (args[i] == "--cleanup=0" || args[i] == "--skip-cleanup") {
                    Environment.SetEnvironmentVariable("MONOMOD_CLEANUP", "0");
                    pathInI = i + 1;
                } else if (args[i] == "--cleanup-all=1" || args[i] == "--cleanup-all") {
                    Environment.SetEnvironmentVariable("MONOMOD_CLEANUP_ALL", "1");
                    pathInI = i + 1;
                } else if (args[i] == "--verbose=1" || args[i] == "--verbose" || args[i] == "-v") {
                    Environment.SetEnvironmentVariable("MONOMOD_VERBOSE", "1");
                    pathInI = i + 1;
                }

            if (pathInI >= args.Length) {
                Console.WriteLine("No assembly path passed.");
                if (System.Diagnostics.Debugger.IsAttached) // Keep window open when running in IDE
                    Console.ReadKey();
                return;
            }

            pathIn = args[pathInI];
            pathOut = args.Length != 1 && pathInI != args.Length - 1 ? args[args.Length - 1] : Path.Combine(Path.GetDirectoryName(pathIn), "MONOMODDED_" + Path.GetFileName(pathIn));

            if (File.Exists(pathOut)) File.Delete(pathOut);

            try {
                using (MonoModder mm = new MonoModder() {
                    InputPath = pathIn,
                    OutputPath = pathOut,
                    Verbose = Environment.GetEnvironmentVariable("MONOMOD_VERBOSE") == "1"
                }) {
                    mm.Read(false);

                    if (args.Length <= 2) {
                        mm.Log("[Main] Scanning for mods in directory.");
                        mm.ReadMod(Directory.GetParent(pathIn).FullName);
                    } else {
                        mm.Log("[Main] Reading mods list from arguments.");
                        for (int i = pathInI + 1; i < args.Length - 2; i++)
                            mm.ReadMod(args[i]);
                    }

                    mm.Read(true);

                    mm.Log("[Main] mm.AutoPatch();");
                    mm.AutoPatch();

                    mm.Write();

                    mm.Log("[Main] Done.");
                }
            } catch (Exception e) {
                Console.WriteLine(e);
            }

            if (System.Diagnostics.Debugger.IsAttached) // Keep window open when running in IDE
                Console.ReadKey();
        }
#endif

    }
}
