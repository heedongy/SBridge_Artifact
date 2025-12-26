namespace SBridge

module BinaryParser =
    open System
    open System.IO
    open System.Diagnostics
    open System.Text
    open System.Text.RegularExpressions
    open SBridge.FunctionLists
    open SBridge.CBMaker
    open SBridge.ParserQuery
    open Config


    // Generate CPG files from binary decompiled code
    let generateCPGFiles (testPath: string) (outputPath: string) (basePath: string) =
        printfn "Generating CPG files in: %s" testPath
        printfn "Output path: %s" outputPath

        // Create cpg directory in processed_target level
        let processedPath = Path.GetDirectoryName(Path.GetDirectoryName(outputPath))
        let cpgPath = Path.Combine(processedPath, "cpg", Path.GetFileName(outputPath))
        if not (Directory.Exists(cpgPath)) then
            Directory.CreateDirectory(cpgPath) |> ignore

        let binaryName = Path.GetFileName(Path.GetDirectoryName(outputPath))
        let cpgLogFilePath = Path.Combine(outputPath, sprintf "%s_cpg_generated.log" binaryName)

        let generatedCPGs =
            if File.Exists(cpgLogFilePath) then
                File.ReadAllLines(cpgLogFilePath) |> Set.ofArray
            else
                Set.empty

        Directory.EnumerateFiles(testPath, "*.c", SearchOption.AllDirectories)
        |> Seq.iter (fun file ->
            let funcName = Path.GetFileNameWithoutExtension(file)

            if Set.contains funcName generatedCPGs then
                printfn "\u001b[32m[*] %s : CPG already generated, skipping\u001b[0m" funcName
            elif List.contains funcName linkFunctionList then
                printfn "\u001b[32m[*] %s : link function, skipping CPG generation\u001b[0m" funcName
            else
                let cpgOutputPath = Path.Combine(cpgPath, funcName + ".cpg")

                if not (File.Exists(cpgOutputPath)) then
                    let _, dotcpgOutput = joernParserQuery file funcName basePath
                    printfn "[*] CPG output saved to: %s" cpgOutputPath
                    File.WriteAllText(cpgOutputPath, dotcpgOutput)

                    // Log CPG generation
                    File.AppendAllText(cpgLogFilePath, sprintf "%s\n" funcName)
                    printfn "\u001b[36m[*] %s : CPG generation completed\u001b[0m" funcName
                else
                    printfn "\u001b[32m[*] %s : CPG file already exists\u001b[0m" funcName
        )

    // Generate control blocks from CPG files
    let generateControlBlocks (testPath: string) (outputPath: string) (basePath: string) =
        printfn "Generating control blocks in: %s" testPath

        let binaryName = Path.GetFileName(Path.GetDirectoryName(outputPath))
        let cbLogFilePath = Path.Combine(outputPath, sprintf "%s_control_blocks.log" binaryName)

        let generatedCBs =
            if File.Exists(cbLogFilePath) then
                File.ReadAllLines(cbLogFilePath) |> Set.ofArray
            else
                Set.empty

        Directory.EnumerateFiles(testPath, "*.c", SearchOption.AllDirectories)
        |> Seq.iter (fun file ->
            let funcName = Path.GetFileNameWithoutExtension(file)

            if Set.contains funcName generatedCBs then
                printfn "\u001b[32m[*] %s : control block already generated, skipping\u001b[0m" funcName
            elif List.contains funcName linkFunctionList then
                printfn "\u001b[32m[*] %s : link function, skipping control block generation\u001b[0m" funcName
            else
                let codePath = file
                let processedPath = Path.GetDirectoryName(Path.GetDirectoryName(outputPath))
                let cpgPath = Path.Combine(processedPath, "cpg", Path.GetFileName(outputPath))
                let cpgOutputPath = Path.Combine(cpgPath, funcName + ".cpg")
                let cbOutputPath = Path.Combine(outputPath, funcName + ".bfunc")

                if File.Exists(cpgOutputPath) then
                    let parameters, dtypes, vars = Utils.extractFunctionInfo file
                    let dotcpgOutput = File.ReadAllText(cpgOutputPath)

                    let orgCodeList = File.ReadAllLines(codePath) |> Array.mapi (fun idx line -> (idx + 1, line)) |> Array.toList

                    // Control Block Generation
                    let (funcName, funcBody) = generateControlBlock funcName dotcpgOutput orgCodeList cbOutputPath parameters dtypes vars
                    printfn "[*] %s : %A" funcName funcBody

                    // Log control block generation
                    File.AppendAllText(cbLogFilePath, sprintf "%s\n" funcName)
                    printfn "\u001b[33m[*] %s : Control block generation completed\u001b[0m" funcName
                else
                    printfn "\u001b[31m[!] %s : CPG file not found, skipping control block generation\u001b[0m" funcName
        )

    // Combined analysis function
    let analyzeFunctions (testPath: string) (outputPath: string) (basePath: string) =
        generateCPGFiles testPath outputPath basePath
        generateControlBlocks testPath outputPath basePath

    let analyzeWithGhidra (binaryPath: string) =
        if Directory.Exists(PROJECT_DIR) then
            Directory.Delete(PROJECT_DIR, true)

        let ghidraScriptPath = Path.Combine(SCRIPT_PATH, "decomplie_ghidra.py")

        Directory.CreateDirectory(PROJECT_DIR) |> ignore

        let ghidraCmd =
            [|
                sprintf "%s/support/analyzeHeadless" GHIDRA_PATH
                PROJECT_DIR
                "project_name"
                "-import"
                binaryPath
                "-postScript"
                ghidraScriptPath
            |]
            |> String.concat " "

        ProcessStartInfo(
            FileName = "bash",
            Arguments = sprintf "-c \"%s\"" ghidraCmd,
            UseShellExecute = false
        )
        |> Process.Start
        |> fun proc -> proc.WaitForExit()

    let analyzeWithIda (binaryPath: string) (targetPath: string) =
        let idaScriptPath = Path.Combine(SCRIPT_PATH, "decompile_ida.py")
        let idaCmd = sprintf "%s/idat64 -A -L/dev/tty -S\"%s\" \"%s\"" IDA_PATH idaScriptPath binaryPath

        ProcessStartInfo(
            FileName = "bash",
            Arguments = sprintf "-c \"%s\"" idaCmd,
            UseShellExecute = false
        )
        |> Process.Start
        |> fun proc -> proc.WaitForExit()

    let binParsing (binaryPath: string) (ida: bool) (clean: bool) =
        let targetPath = Path.Combine(binaryPath, "target")
        let processedTargetPath = Path.Combine(binaryPath, "processed_target")
        let infoPath = Path.Combine(processedTargetPath, "info")

        let configPath = Path.Combine(SCRIPT_PATH, "config.json")
        let configJson = sprintf """{"input_path": "%s"}""" binaryPath
        File.WriteAllText(configPath, configJson)

        let createDirectoryIfNotExists path =
            if not (Directory.Exists(path)) then
                Directory.CreateDirectory(path) |> ignore

        let recreateDirectory path =
            if Directory.Exists(path) then
                Directory.Delete(path, true)
            Directory.CreateDirectory(path) |> ignore

        let currentPaths =
            match ida with
            | true -> // IDA
                {|
                    featurePath = Path.Combine(processedTargetPath, "features_ida")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode_ida")
                |}
            | false -> // Ghidra
                {|
                    featurePath = Path.Combine(processedTargetPath, "features")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode")
                |}

        if clean then
            recreateDirectory processedTargetPath
            recreateDirectory infoPath
            recreateDirectory currentPaths.featurePath
            recreateDirectory currentPaths.decompilePath
        else
            createDirectoryIfNotExists processedTargetPath
            createDirectoryIfNotExists infoPath
            createDirectoryIfNotExists currentPaths.featurePath
            createDirectoryIfNotExists currentPaths.decompilePath

        Directory.GetFiles(targetPath)
        |> Array.iter (fun binPath ->
            let binaryName = Path.GetFileName(binPath)

            try
                let infoTarget = Path.Combine(infoPath, binaryName)
                let currentFeaturePath = Path.Combine(currentPaths.featurePath, binaryName)
                let currentDecompilePath = Path.Combine(currentPaths.decompilePath, binaryName)

                if clean then
                    [infoTarget; currentFeaturePath; currentDecompilePath]
                    |> List.iter (fun path ->
                        if Directory.Exists(path) then
                            Directory.Delete(path, true)
                        Directory.CreateDirectory(path) |> ignore
                        printfn "(Clean Mode) Created directory: %s" path)
                else
                    [infoTarget; currentFeaturePath; currentDecompilePath]
                    |> List.iter (fun path ->
                        if not (Directory.Exists(path)) then
                            Directory.CreateDirectory(path) |> ignore
                            printfn "Created directory: %s" path)

                let shouldAnalyze =
                    clean ||
                    not (Directory.Exists(currentDecompilePath)) ||
                    Directory.GetFiles(currentDecompilePath).Length = 0

                if shouldAnalyze then
                    if ida then
                        analyzeWithIda binPath targetPath
                        analyzeFunctions currentDecompilePath currentFeaturePath binaryPath
                    else
                        analyzeWithGhidra binPath
                        analyzeFunctions currentDecompilePath currentFeaturePath binaryPath
                else
                    printfn "[*] Decompiled file already exists. Skipping analysis: %s" binaryName
                    analyzeFunctions currentDecompilePath currentFeaturePath binaryPath

            with
            | ex -> printfn "[!] Error processing %s: %s" binaryName ex.Message
        )

    // Generate only CPG files for binary decompiled code
    let binCPGGeneration (binaryPath: string) (ida: bool) (clean: bool) =
        let targetPath = Path.Combine(binaryPath, "target")
        let processedTargetPath = Path.Combine(binaryPath, "processed_target")
        let infoPath = Path.Combine(processedTargetPath, "info")

        let configPath = Path.Combine(SCRIPT_PATH, "config.json")
        let configJson = sprintf """{"input_path": "%s"}""" binaryPath
        File.WriteAllText(configPath, configJson)

        let createDirectoryIfNotExists path =
            if not (Directory.Exists(path)) then
                Directory.CreateDirectory(path) |> ignore

        let recreateDirectory path =
            if Directory.Exists(path) then
                Directory.Delete(path, true)
            Directory.CreateDirectory(path) |> ignore

        let currentPaths =
            match ida with
            | true -> // IDA
                {|
                    featurePath = Path.Combine(processedTargetPath, "features_ida")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode_ida")
                |}
            | false -> // Ghidra
                {|
                    featurePath = Path.Combine(processedTargetPath, "features")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode")
                |}

        if clean then
            recreateDirectory processedTargetPath
            recreateDirectory infoPath
            recreateDirectory currentPaths.featurePath
            recreateDirectory currentPaths.decompilePath
        else
            createDirectoryIfNotExists processedTargetPath
            createDirectoryIfNotExists infoPath
            createDirectoryIfNotExists currentPaths.featurePath
            createDirectoryIfNotExists currentPaths.decompilePath


        Directory.GetFiles(targetPath)
        |> Array.iter (fun binPath ->
            let binaryName = Path.GetFileName(binPath)

            try
                let infoTarget = Path.Combine(infoPath, binaryName)
                let currentFeaturePath = Path.Combine(currentPaths.featurePath, binaryName)
                let currentDecompilePath = Path.Combine(currentPaths.decompilePath, binaryName)

                if clean then
                    [infoTarget; currentFeaturePath; currentDecompilePath]
                    |> List.iter (fun path ->
                        if Directory.Exists(path) then
                            Directory.Delete(path, true)
                        Directory.CreateDirectory(path) |> ignore
                        printfn "(Clean Mode) Created directory: %s" path)
                else
                    [infoTarget; currentFeaturePath; currentDecompilePath]
                    |> List.iter (fun path ->
                        if not (Directory.Exists(path)) then
                            Directory.CreateDirectory(path) |> ignore
                            printfn "Created directory: %s" path)

                let shouldAnalyze =
                    clean ||
                    not (Directory.Exists(currentDecompilePath)) ||
                    Directory.GetFiles(currentDecompilePath).Length = 0

                if shouldAnalyze then
                    if ida then
                        analyzeWithIda binPath targetPath
                        generateCPGFiles currentDecompilePath currentFeaturePath binaryPath
                    else
                        analyzeWithGhidra binPath
                        generateCPGFiles currentDecompilePath currentFeaturePath binaryPath
                else
                    printfn "[*] Decompiled file already exists. Generating CPG files: %s" binaryName
                    generateCPGFiles currentDecompilePath currentFeaturePath binaryPath

            with
            | ex -> printfn "[!] Error processing %s: %s" binaryName ex.Message
        )


    // Generate only control blocks from existing CPG files
    let binControlBlockGeneration (binaryPath: string) (ida: bool) =
        let targetPath = Path.Combine(binaryPath, "target")
        let processedTargetPath = Path.Combine(binaryPath, "processed_target")

        let currentPaths =
            match ida with
            | true -> // IDA
                {|
                    featurePath = Path.Combine(processedTargetPath, "features_ida")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode_ida")
                |}
            | false -> // Ghidra
                {|
                    featurePath = Path.Combine(processedTargetPath, "features")
                    decompilePath = Path.Combine(processedTargetPath, "decompileCode")
                |}

        Directory.GetFiles(targetPath)
        |> Array.iter (fun binPath ->
            let binaryName = Path.GetFileName(binPath)
            let currentFeaturePath = Path.Combine(currentPaths.featurePath, binaryName)
            let currentDecompilePath = Path.Combine(currentPaths.decompilePath, binaryName)

            generateControlBlocks currentDecompilePath currentFeaturePath binaryPath
        )



