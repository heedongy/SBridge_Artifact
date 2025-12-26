namespace SBridge

module VulDetector =
    open System
    open System.IO
    open System.Collections.Generic
    open System.Text.RegularExpressions
    open SimMetrics.Net.Metric
    open SBridge.CBVector
    open SBridge.JsonConverters
    open SBridge.Types
    open SBridge.SimilarityMeasure



    type CveGroundTruth = {
        VulnerablePattern: string
        PatchPattern: string
    }

    type DetectionResult = {
        FunctionName: string
        MatchedFunction: string
        VulnerabilityScore: float
        PatchScore: float
        IsVulnerable: bool
        IsCorrectMatch: bool
        CveMatch: string option
    }

    type BinaryResult = {
        BinaryName: string
        IsVulnerable: bool
        FunctionResults: DetectionResult list
        FuncGT: (string * string array * string array * bool) array
        CveGroundTruth: CveGroundTruth option
        CveMatch: string option
    }

    // Load CVE ground truth
    let private loadCveGroundTruth (cveGroundTruthPath: string) =
        if File.Exists(cveGroundTruthPath) then
            try
                let lines = File.ReadAllLines(cveGroundTruthPath)
                let vulLine = lines |> Array.tryFind (fun line -> line.StartsWith("vul,"))
                let patchLine = lines |> Array.tryFind (fun line -> line.StartsWith("patch,"))

                match vulLine, patchLine with
                | Some vul, Some patch ->
                    let vulParts = vul.Split(',', 2)
                    let patchParts = patch.Split(',', 2)
                    if vulParts.Length >= 2 && patchParts.Length >= 2 then
                        let vulPattern = vulParts.[1].Trim()
                        let patchPattern = patchParts.[1].Trim()
                        Some { VulnerablePattern = vulPattern; PatchPattern = patchPattern }
                    else
                        None
                | _ -> None
            with ex ->
                printfn "Failed to load CVE ground truth from %s: %s" cveGroundTruthPath ex.Message
                None
        else
            None

    // Check CVE
    let private checkCveMatch (binaryFileName: string) (cveGroundTruth: CveGroundTruth option) =
        match cveGroundTruth with
        | Some cve ->
            if binaryFileName.StartsWith(cve.VulnerablePattern) then Some "Vulnerable"
            elif binaryFileName.StartsWith(cve.PatchPattern) then Some "Patched"
            else None
        | None -> None

    // Extract unique blocks
    let codePatchBlocks (vulBlocks: ControlBlock list) (patchBlocks: ControlBlock list) =
        let listsAreEqual (list1: string list) (list2: string list) =
            let sorted1 = List.sort list1
            let sorted2 = List.sort list2
            sorted1 = sorted2

        let blocksEqual (block1: ControlBlock) (block2: ControlBlock) =
            block1.block_type = block2.block_type &&
            listsAreEqual block1.block_content block2.block_content &&
            listsAreEqual block1.key_feature block2.key_feature

        let isUniqueBlock block otherList =
            not (otherList |> List.exists (fun otherBlock -> blocksEqual block otherBlock))

        let vulOnly =
            vulBlocks
            |> List.filter (fun vulBlock -> isUniqueBlock vulBlock patchBlocks)

        let patchOnly =
            patchBlocks
            |> List.filter (fun patchBlock -> isUniqueBlock patchBlock vulBlocks)

        (vulOnly, patchOnly)

    // Load function blocks
    let private loadFunctionBlocks (functionPath: string) =
        if File.Exists functionPath then
            try
                Some (loadJsonControlBlocks functionPath)
            with ex ->
                printfn "Failed to load function from %s: %s" functionPath ex.Message
                None
        else
            printfn "Function file not found: %s" functionPath
            None

    // Load binary function blocks
    let private loadBinaryBlocks (testBinaryPath: string) =
        try
            Directory.GetFiles(testBinaryPath, "*.bfunc")
            |> Array.map (fun file ->
                let name = Path.GetFileNameWithoutExtension(file)
                let blocks = loadJsonControlBlocks file
                (name, blocks))
            |> Array.toList
        with ex ->
            printfn "Failed to load binary blocks from %s: %s" testBinaryPath ex.Message
            []

    // Check ground truth match
    let private checkGroundTruthMatch (vulFunctionName: string) (matchingName: string) (funcGT: (string * string array * string array * bool) array) =
        if Array.isEmpty funcGT then
            true
        else
            funcGT
            |> Array.exists (fun (gtSrc, gtTargets, gtAddrs, _) ->
                vulFunctionName = gtSrc &&
                ((gtTargets |> Array.exists (fun f -> matchingName = f)) ||
                 (gtAddrs |> Array.exists (fun addr -> matchingName.Contains(addr)))))



    let vulnerabilityDetect (vulFunctionName: string) (vulSrcPath: string) (patchSrcPath: string)
                               (binaryBlocksList: (string * ControlBlock list) list) (funcGT: (string * string array * string array * bool) array)
                               (binaryFileName: string) (cveGroundTruth: CveGroundTruth option) =
        try
            let vulFunctionPath = Path.Combine(vulSrcPath, vulFunctionName + ".sfunc")
            let patchFunctionPath = Path.Combine(patchSrcPath, vulFunctionName + ".sfunc")

            printfn "\n[*] Target function: %s" vulFunctionName

            match loadFunctionBlocks vulFunctionPath, loadFunctionBlocks patchFunctionPath with
            | Some vulBlocks, Some patchBlocks ->
                let (vulOnly, patchOnly) = codePatchBlocks vulBlocks patchBlocks

                // Find most similar functions
                let vulMatch = findMostSimilarBinaryFunction vulFunctionName vulBlocks binaryBlocksList
                let patchMatch = findMostSimilarBinaryFunction vulFunctionName patchBlocks binaryBlocksList
                let vulMatches = if fst vulMatch > 0.0 then [vulMatch] else []
                let patchMatches = if fst patchMatch > 0.0 then [patchMatch] else []

                let selectBestMatch (matches: (float * string) list) (isVul: bool) =
                    if Array.isEmpty funcGT then
                        match matches with
                        | (score, funcName) :: _ -> (score, funcName)
                        | [] -> (0.0, "")
                    else // Only for Paper Evaluation
                        let gtMatches =
                            matches
                            |> List.filter (fun (_, funcName) ->
                                checkGroundTruthMatch vulFunctionName funcName funcGT)

                        match gtMatches with
                        | (score, funcName) :: _ -> (score, funcName)
                        | [] -> (0.0, "")

                let vulSim, mostSimilarBinaryForVul = selectBestMatch vulMatches true
                let patchSim, mostSimilarBinaryForPatch = selectBestMatch patchMatches false

                // Select the better matching function
                let matchingName, selectedSim =
                    if vulSim = 0.0 && patchSim = 0.0 then
                        ("", 0.0)
                    elif vulSim >= patchSim then
                        (mostSimilarBinaryForVul, vulSim)
                    else
                        (mostSimilarBinaryForPatch, patchSim)

                let isCorrectMatch =
                    if String.IsNullOrEmpty(matchingName) then false
                    else checkGroundTruthMatch vulFunctionName matchingName funcGT

                let cveMatch = checkCveMatch binaryFileName cveGroundTruth

                if String.IsNullOrEmpty(matchingName) then
                    {
                        FunctionName = vulFunctionName
                        MatchedFunction = ""
                        VulnerabilityScore = 0.0
                        PatchScore = 0.0
                        IsVulnerable = false
                        IsCorrectMatch = false
                        CveMatch = None
                    }
                else
                    // Calculate similarity
                    let binaryBlocks =
                        binaryBlocksList
                        |> List.find (fun (name, _) -> name = matchingName)
                        |> snd

                    let vulCoverage, patchCoverage = compareVulPatchFunctions vulBlocks patchBlocks binaryBlocks vulOnly patchOnly false
                    let isVul = vulCoverage >= patchCoverage

                    {
                        FunctionName = vulFunctionName
                        MatchedFunction = matchingName
                        VulnerabilityScore = vulCoverage
                        PatchScore = patchCoverage
                        IsVulnerable = isVul
                        IsCorrectMatch = isCorrectMatch
                        CveMatch = cveMatch
                    }
            | _ ->
                {
                    FunctionName = vulFunctionName
                    MatchedFunction = ""
                    VulnerabilityScore = 0.0
                    PatchScore = 0.0
                    IsVulnerable = false
                    IsCorrectMatch = false
                    CveMatch = checkCveMatch binaryFileName cveGroundTruth
                }
        with ex ->
            printfn "Error processing function %s: %s" vulFunctionName ex.Message
            {
                FunctionName = vulFunctionName
                MatchedFunction = ""
                VulnerabilityScore = 0.0
                PatchScore = 0.0
                IsVulnerable = false
                IsCorrectMatch = false
                CveMatch = checkCveMatch binaryFileName cveGroundTruth
            }

    let private logFunctionResult (result: DetectionResult) (hasGroundTruth: bool) =
        printfn "  - Function: %s" result.FunctionName
        printfn "    Function detect result: %s"
            (if not hasGroundTruth then "NO Ground Truth"
             elif result.IsCorrectMatch then "O"
             else "X")
        printfn "    Vulnerability similarity: %.2f (matched with: %s)" result.VulnerabilityScore result.MatchedFunction
        printfn "    Patch similarity: %.2f (matched with: %s)" result.PatchScore result.MatchedFunction
        printfn "    Vulnerability Result : %s" (if result.IsVulnerable then "Vulnerable binary" else "Patched binary")
        printfn "    Ground Truth check: %s"
            (match result.CveMatch with
             | Some status ->
                 match status with
                 | s when s = "Vulnerable" && result.IsVulnerable -> "O"
                 | s when s = "Patched" && not result.IsVulnerable -> "O"
                 | _ -> "X"
             | None -> "None")

    let private generateSummaryCsv (binaryResults: BinaryResult list) (inputPath: string) =
        let testName = Path.GetFileName(inputPath)
        let data =
            binaryResults
            |> List.sortBy (fun binaryResult -> binaryResult.BinaryName)
            |> List.map (fun binaryResult ->
                let status =
                    if Array.isEmpty binaryResult.FuncGT then
                        if binaryResult.IsVulnerable then "Vulnerable" else "Patched"
                    else
                        let validResults =
                            binaryResult.FunctionResults
                            |> List.filter (fun (result: DetectionResult) ->
                                result.MatchedFunction <> "" && result.IsCorrectMatch)

                        if List.isEmpty validResults then "None"
                        elif List.exists (fun (result: DetectionResult) -> result.IsVulnerable) validResults then "Vulnerable"
                        else "Patched"

                let cveCorrect =
                    match binaryResult.CveMatch with
                    | Some cveStatus when cveStatus = "Vulnerable" && binaryResult.IsVulnerable -> "O"
                    | Some cveStatus when cveStatus = "Patched" && not binaryResult.IsVulnerable -> "O"
                    | Some _ -> "X"
                    | None -> "None"

                sprintf "%s,%s,%s,%s" testName binaryResult.BinaryName status cveCorrect)
            |> String.concat "\n"
        data

    let private generateSBridgeVulnerabilityInfo (binaryResults: BinaryResult list) (inputPath: string) =
        let testName = Path.GetFileName(inputPath)
        let header = "TestName,BinaryName,SBridge_Result\n"
        let data =
            binaryResults
            |> List.sortBy (fun binaryResult -> binaryResult.BinaryName)
            |> List.map (fun binaryResult ->
                let sbridgeResult = if binaryResult.IsVulnerable then "Vulnerable" else "Patched"
                sprintf "%s,%s,%s" testName binaryResult.BinaryName sbridgeResult)
            |> String.concat "\n"
        header + data

    // Main detector function
    let detector (inputPath: string) (ida: bool) =
        let testName = Path.GetFileName(inputPath)
        let targetInfoPath = Path.Combine(inputPath, "target.info")

        if not (File.Exists targetInfoPath) then
            failwithf "Target info file not found: %s" targetInfoPath

        let vulFunctionNames =
            File.ReadAllLines(targetInfoPath)
            |> Array.toList
            |> List.filter (fun line -> not (String.IsNullOrWhiteSpace(line)))

        if List.isEmpty vulFunctionNames then
            failwith "No target functions found in target.info"

        printfn "Target function List: %A" vulFunctionNames

        let patchSrcFunctionPath = Path.Combine(inputPath, "processed_input/features/patchcode")
        let vulSrcFunctionPath = Path.Combine(inputPath, "processed_input/features/vulcode")
        let targetPath = Path.Combine(inputPath, "target")

        if not (Directory.Exists vulSrcFunctionPath) then
            failwithf "Vulnerable source path not found: %s" vulSrcFunctionPath
        if not (Directory.Exists patchSrcFunctionPath) then
            failwithf "Patch source path not found: %s" patchSrcFunctionPath
        if not (Directory.Exists targetPath) then
            failwithf "Target path not found: %s" targetPath

        let binaryFiles =
            Directory.GetFiles(targetPath)
            |> Array.map Path.GetFileName
            |> Array.toList

        if List.isEmpty binaryFiles then
            failwith "No binary files found in target directory"

        printfn "Test Target binary files: %A" binaryFiles


        let cveGroundTruthPath = Path.Combine(inputPath, "cve_ground_truth.info")
        let cveGroundTruth = loadCveGroundTruth cveGroundTruthPath

        match cveGroundTruth with
        | Some cve ->
            printfn "CVE Ground Truth loaded"
        | None ->
            printfn "No CVE ground truth found"

        let binaryResults: BinaryResult list =
            binaryFiles
            |> List.sort
            |> List.toArray
            |> Array.map (fun binaryfile ->
                printfn "\n[*] Binary file: %s" binaryfile

                let gtPath = Path.Combine(inputPath, "processed_target/info", binaryfile, "ground_truth.info")
                let funcGT =
                    if File.Exists(gtPath) then getGroundTruth gtPath
                    else Array.empty

                let testBinaryPath =
                    if ida then
                        Path.Combine(inputPath, "processed_target/features_ida", binaryfile)
                    else
                        Path.Combine(inputPath, "processed_target/features", binaryfile)

                let binaryBlocksList = loadBinaryBlocks testBinaryPath

                let functionResults =
                    vulFunctionNames
                    |> List.map (fun vulFunctionName ->
                        vulnerabilityDetect vulFunctionName vulSrcFunctionPath patchSrcFunctionPath binaryBlocksList funcGT binaryfile cveGroundTruth)

                let binaryVul = functionResults |> List.exists (fun result -> result.IsVulnerable)

                let binaryCveMatch =
                    if not (Array.isEmpty funcGT) &&
                       functionResults |> List.forall (fun result -> not result.IsCorrectMatch) then
                        None
                    else
                        checkCveMatch binaryfile cveGroundTruth

                functionResults |> List.iter (fun (result: DetectionResult) ->
                    logFunctionResult result (not (Array.isEmpty funcGT)))

                {
                    BinaryName = binaryfile
                    IsVulnerable = binaryVul
                    FunctionResults = functionResults
                    FuncGT = funcGT
                    CveGroundTruth = cveGroundTruth
                    CveMatch = binaryCveMatch
                })
            |> Array.toList


        printfn "\n [*] %s Result" testName

        let summaryResultPath = Path.Combine(inputPath, "vultest_result.csv")
        let sbridgeVulInfoPath = Path.Combine(inputPath, "SBridge_vulnerable_info.csv")

        let summaryCsvContent = generateSummaryCsv binaryResults inputPath
        let sbridgeVulInfoContent = generateSBridgeVulnerabilityInfo binaryResults inputPath

        File.WriteAllText(summaryResultPath, summaryCsvContent)
        File.WriteAllText(sbridgeVulInfoPath, sbridgeVulInfoContent)


        printfn "\n[*] Vulnerability Detection Results:"
        binaryResults
        |> List.sortBy (fun binaryResult -> binaryResult.BinaryName)
        |> List.iter (fun binaryResult ->
            match binaryResult.CveMatch with
            | None ->
                printfn "Binary: %s : none" binaryResult.BinaryName
            | Some status ->
                let cveCorrect =
                    (status = "Vulnerable" && binaryResult.IsVulnerable) ||
                    (status = "Patched" && not binaryResult.IsVulnerable)

                if cveCorrect then
                    if binaryResult.IsVulnerable then
                        printfn "\u001b[32mBinary: %s : vulnerable\u001b[0m" binaryResult.BinaryName
                    else
                        printfn "\u001b[32mBinary: %s : patched\u001b[0m" binaryResult.BinaryName
                else
                    if binaryResult.IsVulnerable then
                        printfn "\u001b[31mBinary: %s : vulnerable\u001b[0m" binaryResult.BinaryName
                    else
                        printfn "\u001b[31mBinary: %s : patched\u001b[0m" binaryResult.BinaryName
            )

        printfn "[*] Results saved to: %s" summaryResultPath
        printfn "[*] SBridge vulnerability info saved to: %s" sbridgeVulInfoPath

        binaryResults