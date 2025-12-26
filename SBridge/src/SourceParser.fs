namespace SBridge
module SourceParser =
    open System
    open System.Collections.Generic
    open System.Diagnostics
    open System.IO
    open System.Text
    open System.Text.Json
    open System.Text.RegularExpressions
    open System.Web
    open SBridge.Utils
    open Config
    open SBridge.CBMaker
    open SBridge.ParserQuery

    let POSSIBLE_EXTENSIONS = [|".c"; ".cc"; ".cpp"; ".hpp"; ".hxx"; ".h"|]

    let removeComment (input: string) =
        let pattern = @"//.*?$|/\*[\s\S]*?\*/|\'(?:\\.|[^\\\'])*\'|""(?:\\.|[^\\""])*"""
        let regex = Regex(pattern, RegexOptions.Multiline ||| RegexOptions.Singleline)
        regex.Replace(input, fun m ->
            let s = m.Value
            if s.StartsWith("/") || s.StartsWith("*") then ""
            else s
        )

    // Generate CPG files from source code
    let generateCPGFiles (testPath: string) (outputPath: string) (basePath: string) =
        let processedPath = Path.GetDirectoryName(Path.GetDirectoryName(outputPath))
        let cpgPath = Path.Combine(processedPath, "cpg", Path.GetFileName(outputPath))
        if not (Directory.Exists(cpgPath)) then
            Directory.CreateDirectory(cpgPath) |> ignore

        let binaryName = Path.GetFileName(Path.GetDirectoryName(outputPath))
        let cpgLogFilePath = Path.Combine(outputPath, sprintf "%s_cpg_generated.log" binaryName)

        // Load target functions
        let targetInfoPath = Path.Combine(basePath, "target.info")
        let targetFunctions =
            if File.Exists(targetInfoPath) then
                let functions = File.ReadAllLines(targetInfoPath)
                                |> Array.map (fun line -> line.Trim())
                                |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
                                |> Set.ofArray
                printfn "[*] target.info found: %d target functions loaded" (Set.count functions)
                Some functions
            else
                printfn "[*] target.info not found, processing all functions"
                None

        let generatedCPGs =
            if File.Exists(cpgLogFilePath) then
                File.ReadAllLines(cpgLogFilePath) |> Set.ofArray
            else
                Set.empty

        Directory.EnumerateFiles(testPath, "*.c", SearchOption.AllDirectories)
        |> Seq.iter (fun file ->
            let funcName = Path.GetFileNameWithoutExtension(file)

            // Skip if not in target functions list
            if Option.isSome targetFunctions && not (Set.contains funcName (Option.get targetFunctions)) then
                printfn "\u001b[90m[*] %s : not in target.info, skipping CPG generation\u001b[0m" funcName
            elif Set.contains funcName generatedCPGs then
                printfn "\u001b[32m[*] %s : CPG already generated, skipping\u001b[0m" funcName
            else
                let cpgOutputPath = Path.Combine(cpgPath, funcName + ".cpg")

                if not (File.Exists(cpgOutputPath)) then
                    let _, dotcpgOutput = joernParserQuery file funcName basePath
                    printfn "[*] CPG output saved to: %s" cpgOutputPath
                    File.WriteAllText(cpgOutputPath, dotcpgOutput)

                    File.AppendAllText(cpgLogFilePath, sprintf "%s\n" funcName)
                    printfn "\u001b[36m[Joern] %s : CPG generation completed\u001b[0m" funcName
                else
                    printfn "\u001b[32m[Joern] %s : CPG file already exists\u001b[0m" funcName
        )

    // Generate control blocks from CPG files
    let generateControlBlocks (testPath: string) (outputPath: string) (basePath: string) =
        let binaryName = Path.GetFileName(Path.GetDirectoryName(outputPath))
        let cbLogFilePath = Path.Combine(outputPath, sprintf "%s_control_blocks.log" binaryName)

        // Load target functions from target.info file
        let targetInfoPath = Path.Combine(basePath, "target.info")
        let targetFunctions =
            if File.Exists(targetInfoPath) then
                let functions = File.ReadAllLines(targetInfoPath)
                                |> Array.map (fun line -> line.Trim())
                                |> Array.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))
                                |> Set.ofArray
                Some functions
            else
                None

        let generatedCBs =
            if File.Exists(cbLogFilePath) then
                File.ReadAllLines(cbLogFilePath) |> Set.ofArray
            else
                Set.empty

        Directory.EnumerateFiles(testPath, "*.c", SearchOption.AllDirectories)
        |> Seq.iter (fun file ->
            let funcName = Path.GetFileNameWithoutExtension(file)

            // Skip if not in target functions list
            if Option.isSome targetFunctions && not (Set.contains funcName (Option.get targetFunctions)) then
                printfn "\u001b[90m[*] %s : not in target.info, skipping control block generation\u001b[0m" funcName
            elif Set.contains funcName generatedCBs then
                printfn "\u001b[32m[*] %s : control block already generated, skipping\u001b[0m" funcName
            else
                let codePath = file
                let processedPath = Path.GetDirectoryName(Path.GetDirectoryName(outputPath))
                let cpgPath = Path.Combine(processedPath, "cpg", Path.GetFileName(outputPath))
                let cpgOutputPath = Path.Combine(cpgPath, funcName + ".cpg")
                let cbOutputPath = Path.Combine(outputPath, funcName + ".sfunc")

                if File.Exists(cpgOutputPath) then
                    let parameters, dtypes, vars = Utils.extractFunctionInfo file
                    let dotcpgOutput = File.ReadAllText(cpgOutputPath)

                    let orgCodeList = File.ReadAllLines(codePath)
                                        |> Array.mapi (fun idx line -> (idx + 1, line))
                                        |> Array.toList

                    // Control Block Generation
                    let (funcName, callees) = generateControlBlock funcName dotcpgOutput orgCodeList cbOutputPath parameters dtypes vars
                    printfn "[*] %s : %A" funcName callees

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

    let extractFunctions (filePath: string) =
        try
            let fileExt = Path.GetExtension(filePath).ToLower()
            let mutable ctagsCommand = sprintf "%s -f - --kinds-C=* --fields=neKSt \"%s\"" CTAGS_PATH filePath

            if fileExt = ".i" then
                ctagsCommand <- sprintf "%s --language-force=C++ -f - --kinds-C=* --fields=neKSt \"%s\"" CTAGS_PATH filePath

            let startInfo = ProcessStartInfo(
                FileName = "bash",
                Arguments = sprintf "-c \"%s\"" ctagsCommand,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )
            use proc = Process.Start(startInfo)
            let functionList = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()

            let lines = File.ReadAllLines(filePath)
            let allFuncs = functionList.Split('\n')
            let funcRegex = Regex(@"(function)")
            let numberRegex = Regex(@"(\d+)")
            let funcSearchRegex = Regex(@"{([\S\s]*)}")
            let paramSearchRegex = Regex(@"\(([^)]*)\)")

            let functions = Dictionary<string, string * string * int * int * int>()

            for line in allFuncs do
                let elemList = Regex.Replace(line, @"[\t\s ]{2,}", "").Split('\t')
                if not (String.IsNullOrEmpty(line)) && elemList.Length >= 8 && funcRegex.IsMatch(elemList.[3]) then
                    let startLineOpt =
                        match Int32.TryParse(numberRegex.Match(elemList.[4]).Groups.[1].Value) with
                        | true, value -> Some value
                        | false, _ -> None

                    let endLineOpt =
                        match Int32.TryParse(numberRegex.Match(elemList.[7]).Groups.[1].Value) with
                        | true, value -> Some value
                        | false, _ -> None

                    match startLineOpt, endLineOpt with
                    | Some funcStartLine, Some funcEndLine ->
                        let funcName = elemList.[0]
                        let funcFullBody = String.Join(Environment.NewLine, lines.[funcStartLine - 1..funcEndLine - 1])
                        let mutable funcBody = String.Join(Environment.NewLine, lines.[funcStartLine - 1..funcEndLine - 1])
                        let funcType = elemList.[5].Split(':').[elemList.[5].Split(':').Length - 1]
                        let parsingFuncName = lines.[funcStartLine - 1].Split('(').[0].Trim()

                        let isType = funcType.Contains(parsingFuncName)

                        match funcSearchRegex.Match(funcBody) with
                        | m when m.Success -> funcBody <- m.Groups.[1].Value
                        | _ -> funcBody <- " "
                        funcBody <- removeComment funcBody

                        let paramCount =
                            match paramSearchRegex.Match(funcFullBody) with
                            | m when m.Success ->
                                m.Groups.[1].Value.Split(',')
                                |> Array.filter (fun p -> not (String.IsNullOrWhiteSpace(p)))
                                |> Array.length
                            | _ -> 0

                        let finalFuncFullBody =
                            if not isType then funcType + " " + funcFullBody
                            else funcFullBody

                        if not (functions.ContainsKey(funcName)) then
                            functions.Add(funcName, (finalFuncFullBody, funcBody, paramCount, funcStartLine, funcEndLine))
                    | _ ->
                        ()

            functions
        with
        | e ->
            printfn "Error: %A" e
            Dictionary<string, string * string * int * int * int>()


    let getSrcFunction (repoPath: string) (saveCodePath: string) =
        let mutable fileCnt = 0
        let mutable funcCnt = 0
        let mutable lineCnt = 0
        let resDict = Dictionary<string, ResizeArray<string * string * int * int * int>>()

        // Clang Preprocessing
        processFilesInDirectory repoPath

        for filePath in Directory.EnumerateFiles(repoPath, "*.*", SearchOption.AllDirectories) do
            if POSSIBLE_EXTENSIONS |> Array.exists (fun (ext:string) -> filePath.EndsWith(ext)) then
                let mutable iFunctions = Dictionary<string, string * string * int * int * int>()

                if filePath.EndsWith(".c") || filePath.EndsWith(".cpp") || filePath.EndsWith(".cc") then
                    let iFilePath = Path.ChangeExtension(filePath, ".i")
                    if File.Exists(iFilePath) then
                        let content = File.ReadAllLines(iFilePath)
                        let filteredContent =
                            content
                            |> Array.filter (fun line ->
                                not (Regex.IsMatch(line.Trim(), @"^#\s+\d+\s+"".*"".*$")))
                        File.WriteAllLines(iFilePath, filteredContent)
                        iFunctions <- extractFunctions iFilePath

                let functions = extractFunctions filePath
                if functions.Count > 0 then
                    fileCnt <- fileCnt + 1
                    let lines = File.ReadAllLines(filePath, Text.Encoding.UTF8)

                    for KeyValue(funcName, (funcFullBody, funcBody, paramCount, funcStartLine, funcEndLine)) in functions do
                        let mutable finalFuncFullBody = funcFullBody
                        let mutable finalFuncBody = funcBody
                        let mutable finalParamCount = paramCount

                        match iFunctions.TryGetValue(funcName) with
                        | true, (iFuncFullBody, iFuncBody, iParamCount, _, _) ->
                            finalFuncFullBody <- iFuncFullBody
                            finalFuncBody <- iFuncBody
                            finalParamCount <- iParamCount
                        | _ -> ()

                        finalFuncBody <- removeComment finalFuncBody

                        let storedPath = Path.GetFileName(filePath)

                        if not (resDict.ContainsKey(funcName)) then
                            resDict.Add(funcName, ResizeArray<_>())

                        resDict.[funcName].Add((storedPath, funcName, finalParamCount, funcStartLine, funcEndLine))

                        let funcFilePath = Path.Combine(saveCodePath, funcName + ".c")
                        if not (File.Exists(funcFilePath)) then
                            Directory.CreateDirectory(Path.GetDirectoryName(funcFilePath)) |> ignore
                            let processedCode = removeComment finalFuncFullBody
                            File.WriteAllText(funcFilePath, processedCode.Replace("\n", Environment.NewLine))

                        lineCnt <- lineCnt + lines.Length
                        funcCnt <- funcCnt + 1

        resDict :> IDictionary<_,_>, fileCnt, funcCnt, lineCnt

    // Generate only CPG files for source code
    let srcCPGGeneration (inputPath: string) (clean: bool) =
        let SRCS_PATH = Path.Combine(inputPath, "input")
        let PROCESSED_PATH = Path.Combine(inputPath, "processed_input")
        let ORG_FUNC_PATH = Path.Combine(inputPath, "processed_input", "orgcode")
        let OUT_FUNC_PATH = Path.Combine(inputPath, "processed_input", "features")
        let INFO_PATH = Path.Combine(inputPath, "processed_input", "info")

        if Directory.Exists(PROCESSED_PATH) then
            if clean then
                Directory.Delete(PROCESSED_PATH, true)
                Directory.CreateDirectory(PROCESSED_PATH) |> ignore

        Directory.GetDirectories(SRCS_PATH)
        |> Array.ofSeq
        |> Array.iter (fun testDir ->
            let testName = Path.GetFileName(testDir)

            // Create required directories
            let requiredDirs = [
                Path.Combine(OUT_FUNC_PATH, testName)
                Path.Combine(INFO_PATH, testName)
                Path.Combine(ORG_FUNC_PATH, testName)
            ]

            for dirPath in requiredDirs do
                Directory.CreateDirectory(dirPath) |> ignore

            let resDict, fileCnt, funcCnt, lineCnt = getSrcFunction testDir (Path.Combine(ORG_FUNC_PATH, testName))

            if resDict.Count > 0 then
                let title = String.Join("\t", [testName; string fileCnt; string funcCnt; string lineCnt])
                let funcInfoPath = Path.Combine(INFO_PATH, testName, "src_info.txt")
                use fres = new StreamWriter(funcInfoPath)
                fres.WriteLine(title)

                for KeyValue(func, funcInfoList) in resDict do
                    if not (String.IsNullOrWhiteSpace(func)) then
                        for (funcPath, funcName, paramCount, funcStartLine, funcEndLine) in funcInfoList do
                            fres.WriteLine($"{funcName}, {paramCount}, {funcPath}, {funcStartLine}, {funcEndLine}")

                generateCPGFiles (Path.Combine(ORG_FUNC_PATH, testName)) (Path.Combine(OUT_FUNC_PATH, testName)) inputPath
        )

    // Generate only control blocks from existing CPG files
    let srcControlBlockGeneration (inputPath: string) =
        let SRCS_PATH = Path.Combine(inputPath, "input")
        let ORG_FUNC_PATH = Path.Combine(inputPath, "processed_input", "orgcode")
        let OUT_FUNC_PATH = Path.Combine(inputPath, "processed_input", "features")

        Directory.GetDirectories(SRCS_PATH)
        |> Array.ofSeq
        |> Array.iter (fun testDir ->
            let testName = Path.GetFileName(testDir)
            generateControlBlocks (Path.Combine(ORG_FUNC_PATH, testName)) (Path.Combine(OUT_FUNC_PATH, testName)) inputPath
        )
