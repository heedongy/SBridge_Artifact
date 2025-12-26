namespace SBridge

module FunctionMatcher =
    open System
    open System.IO
    open System.Collections.Generic
    open System.Text.RegularExpressions
    open SimMetrics.Net.Metric
    open SBridge.Utils
    open SBridge.SimilarityMeasure
    open SBridge.CBVector
    open SBridge.JsonConverters
    open SBridge.Types
    open Newtonsoft.Json

    let mutable binaryBlocksCache = Dictionary<string, (string * ControlBlock list) list>()

    let getControlBlocksForBinaryFunction (binaryFuncName: string) (binaryName: string) (inputPath: string) =
        let binaryFuncPath = Path.Combine(inputPath, "processed_target/features", binaryName, binaryFuncName + ".bfunc")
        if File.Exists(binaryFuncPath) then
            try
                let blocks = loadJsonControlBlocks binaryFuncPath
                blocks
                |> List.filter (fun block -> block.block_type = ControlBlockType.CalleeFunc)
                |> List.collect (fun block ->
                    block.key_feature
                    |> List.map (fun keyStr ->
                        match keyStr.Split(',') with
                        | [| funcName; _ |] when not (String.IsNullOrEmpty(funcName)) -> funcName
                        | _ -> keyStr.Split(',').[0]))
                |> List.filter (fun name -> not (String.IsNullOrEmpty(name)) && not (name.StartsWith("(")))
                |> List.distinct
            with
            | ex ->
                []
        else
            []

    let loadCallGraphMap (srcFunctionCallGraphPath: string) =
        if File.Exists(srcFunctionCallGraphPath) then
            printfn "[*] Call graph map from %s loaded" srcFunctionCallGraphPath
            File.ReadAllText(srcFunctionCallGraphPath)
            |> JsonConvert.DeserializeObject<IDictionary<string, string list>>
        else
            Dictionary<string, string list>() :> IDictionary<string, string list>

    let orderFunctionsByCallGraph (srcFunctionFiles: string[]) (callGraphMap: IDictionary<string, string list>) =
        let buildGraph (data: IDictionary<string, string list>) =
            let graph = Dictionary<string, string list>()
            for kvp in data do
                let caller = kvp.Key
                if not (caller.StartsWith("_")) then
                    let callees = kvp.Value |> List.filter (fun callee -> not (callee.StartsWith("_")))
                    graph.[caller] <- callees
                    for callee in callees do
                        if not (graph.ContainsKey callee) then graph.[callee] <- []
            graph

        let findRootNodes (graph: Dictionary<string, string list>) =
            let allCallees =
                graph.Values
                |> Seq.collect id
                |> Set.ofSeq
            graph.Keys
            |> Seq.filter (fun caller -> not (Set.contains caller allCallees))
            |> Seq.toList

        let rec dfs (graph: Dictionary<string, string list>) (node: string) (visited: HashSet<string>) (result: ResizeArray<string>) =
            if not (visited.Contains(node)) then
                visited.Add(node) |> ignore
                result.Add(node)

                for child in graph.[node] do
                    dfs graph child visited result

        let graph = buildGraph callGraphMap
        let roots = findRootNodes graph

        let orderedRoots =
            if List.exists (fun node -> node = "main") roots then
                "main" :: (roots |> List.filter (fun node -> node <> "main"))
            else
                roots

        let visited = HashSet<string>()
        let orderedFunctions = ResizeArray<string>()

        for root in orderedRoots do
            dfs graph root visited orderedFunctions

        for node in graph.Keys do
            if not (visited.Contains(node)) then
                dfs graph node visited orderedFunctions

        let fileNameToPath = srcFunctionFiles |> Array.map (fun path -> (Path.GetFileNameWithoutExtension(path), path)) |> dict

        let orderedPaths =
            orderedFunctions
            |> Seq.filter (fun funcName -> not (funcName.StartsWith("_")))
            |> Seq.choose (fun funcName -> match fileNameToPath.TryGetValue(funcName) with true, path -> Some path | _ -> None)
            |> Seq.toArray

        let remainingPaths =
            srcFunctionFiles
            |> Array.filter (fun path ->
                let fileName = Path.GetFileNameWithoutExtension(path)
                not (Seq.contains fileName orderedFunctions) &&
                not (fileName.StartsWith("_")))

        Array.append orderedPaths remainingPaths

    let calculateSimilarities blocksList sourceBlocks useCallGraph sourceFuncName =
        blocksList
        |> List.map (fun (funcName, blocks) ->
            let similarity = compareFunctions sourceBlocks blocks useCallGraph
            (similarity, funcName)
        )
        |> List.sortByDescending fst

    let getTopResults allSimilarities blocksList =
        let allResults = allSimilarities

        let (score, mostSimilarBinary) =
            match allSimilarities with
            | (s, name)::_ -> (s, name)
            | [] -> (0.0, "")

        (score, mostSimilarBinary, allResults)

    let matchingProcess (binaryFile: string) (srcFunctionlist: string[]) (srcFunctionCallGraphMap: IDictionary<string, string list>) (inputPath: string) (logPath: string) (funcResultPath: string) =
        // Initialize top1Map
        let mutable top1Map = Dictionary<string, (float * string)>()

        // Initialize cache
        binaryBlocksCache.Remove(binaryFile) |> ignore

        let binaryFunctionCallGraphPath = Path.Combine(inputPath, "processed_target/info", binaryFile, "call_graph.json")
        let binaryFunctionCallGraphMap = loadCallGraphMap binaryFunctionCallGraphPath

        // Calculate matching results
        let gtPath = Path.Combine(inputPath, "processed_target/info", binaryFile, "ground_truth.info")
        let resPath = Path.Combine(inputPath, "SBridge_matching_result.csv")
        let groundTruth =
            if File.Exists(gtPath) then
                getGroundTruth gtPath
            else
                Array.empty

        let gtTestedPath = Path.Combine(inputPath, "processed_target/info", binaryFile, "ground_truth_tested.info")
        let groundTruthSourceFuncs =
            groundTruth |> Array.map (fun (src, _, _, _) -> src)

        // Count of empty block functions for checking
        let mutable emptyBlockCount = 0
        let results =
            srcFunctionlist
            |> Array.fold (fun acc matchFunctionPath ->
                let functionName = Path.GetFileNameWithoutExtension(matchFunctionPath)
                let sourceBlocks = loadJsonControlBlocks matchFunctionPath
                let filteredSourceBlocks = blockFilter sourceBlocks

                if List.isEmpty filteredSourceBlocks then
                    emptyBlockCount <- emptyBlockCount + 1
                    acc
                else
                    try
                        let stopwatch = System.Diagnostics.Stopwatch.StartNew()

                        let testBinaryPath = Path.Combine(inputPath, "processed_target/features", binaryFile)
                        let binaryFiles =
                            Directory.GetFiles(testBinaryPath, "*.bfunc")
                        let binaryBlocksList =
                            if binaryBlocksCache.ContainsKey(binaryFile) then
                                binaryBlocksCache.[binaryFile]
                            else
                                let blocks =
                                    Directory.GetFiles(testBinaryPath, "*.bfunc")
                                    |> Array.map (fun file ->
                                        let name = Path.GetFileNameWithoutExtension(file)
                                        let blocks = loadJsonControlBlocks file
                                        let filteredBlocks = blockFilter blocks
                                        (name, filteredBlocks)
                                    )
                                    |> Array.toList
                                binaryBlocksCache.[binaryFile] <- blocks
                                blocks

                        let binaryFunctionsCount = binaryFiles.Length

                        let inlinable =
                            srcFunctionCallGraphMap.Keys
                            |> Seq.exists (fun key ->
                                match srcFunctionCallGraphMap.TryGetValue(key) with
                                | true, callees -> List.contains functionName callees
                                | _ -> false)

                        let tmpRes =
                            if inlinable then
                                let callers =
                                    srcFunctionCallGraphMap.Keys
                                    |> Seq.filter (fun key ->
                                        match srcFunctionCallGraphMap.TryGetValue(key) with
                                        | true, callees -> List.contains functionName callees
                                        | _ -> false)
                                    |> Seq.toList

                                let mutable callCandidateList = []
                                if not (List.isEmpty callers) && top1Map.ContainsKey(callers.[0]) then
                                    let binaryBlocks = getControlBlocksForBinaryFunction (snd top1Map.[callers.[0]]) binaryFile inputPath
                                    callCandidateList <- binaryBlocks
                                else
                                    ()

                                let allSimilarities = calculateSimilarities binaryBlocksList filteredSourceBlocks true functionName
                                let (score, mostSimilarBinary, top5res) = getTopResults allSimilarities binaryBlocksList

                                top1Map.[functionName] <- (score, mostSimilarBinary)

                                let isInCallCandidates =
                                    not (List.isEmpty callCandidateList) &&
                                    List.contains mostSimilarBinary callCandidateList


                                if isInCallCandidates then
                                    (functionName, binaryFile, score, mostSimilarBinary, top5res, true, binaryFunctionsCount)
                                else
                                    // Recalculate similarity
                                    let allSimilarities = calculateSimilarities binaryBlocksList filteredSourceBlocks false functionName
                                    let (score, mostSimilarBinary, top5res) = getTopResults allSimilarities binaryBlocksList

                                    (functionName, binaryFile, score, mostSimilarBinary, top5res, false, binaryFunctionsCount)
                            else
                                // Calculate similarity
                                let allSimilarities = calculateSimilarities binaryBlocksList filteredSourceBlocks true functionName
                                let (score, mostSimilarBinary, top5res) = getTopResults allSimilarities binaryBlocksList

                                top1Map.[functionName] <- (score, mostSimilarBinary)

                                (functionName, binaryFile, score, mostSimilarBinary, top5res, true, binaryFunctionsCount)

                        stopwatch.Stop()
                        let elapsedMs = stopwatch.ElapsedMilliseconds

                        let (sourceFuncName, binFile, score, mostSimilarBinary, top5res, useCallGraphWeight, bfuncsCount) = tmpRes
                        Array.append acc [|(sourceFuncName, binFile, score, mostSimilarBinary, top5res, useCallGraphWeight, elapsedMs, bfuncsCount)|]
                    with
                    | ex ->
                        printfn "Error occurred while processing %s: %s" binaryFile ex.Message
                        Array.append acc [|(functionName, binaryFile, 0.0, "", [], true, 0L, 0)|]
            ) [||]

        let mutable binaryTop1CorrectCount = 0
        let mutable binaryTop5CorrectCount = 0
        let mutable binaryInlineTop1CorrectCount = 0
        let mutable binaryInlineTop5CorrectCount = 0
        let mutable testFunctionCount = 0
        let mutable testInlineFunctionCount = 0
        let mutable totalMatchingTime = 0L
        let mutable totalMrrScore = 0.0
        let mutable binaryFunctionCount = 0

        results
        |> Array.iter (fun (sourceFuncName, _, score, matchedFunc, allResults, useCallGraphWeight, elapsedMs, bfuncsCount) ->
            totalMatchingTime <- totalMatchingTime + elapsedMs

            if binaryFunctionCount = 0 then
                binaryFunctionCount <- bfuncsCount

            let hasGroundTruth = groundTruth |> Array.exists (fun (gtSrc, _, _, _) -> gtSrc = sourceFuncName)

            if hasGroundTruth then
                testFunctionCount <- testFunctionCount + 1

                let isInlinable =
                    groundTruth
                    |> Array.tryFind (fun (gtSrc, _, _, inlinable) -> gtSrc = sourceFuncName)
                    |> Option.map (fun (_, _, _, inlinable) -> inlinable)
                    |> Option.defaultValue false

                if isInlinable then
                    testInlineFunctionCount <- testInlineFunctionCount + 1

                let top5 =
                    allResults
                    |> List.take (min 5 (List.length allResults))

                // top1
                let top1Score =
                    match allResults with
                    | (s, _)::_ -> Math.Round(s, 2)
                    | [] -> 0.0

                let top1Candidates =
                    match allResults with
                    | [] -> []
                    | _ -> allResults |> List.filter (fun (s, _) -> Math.Round(s, 2) = top1Score)

                let top1Correct =
                    top1Candidates
                    |> List.exists (fun (score, fname) ->
                        score > 0.0 &&
                        groundTruth
                        |> Array.exists (fun (gtSrc, gtTargets, gtAddrs, _) ->
                            sourceFuncName = gtSrc &&
                            ((gtTargets |> Array.exists (fun f -> fname = f)) ||
                                (gtAddrs |> Array.exists (fun addr -> fname.Contains(addr))))
                        )
                    )

                // Calculate MRR rank and score
                let mrrRank, mrrScore =
                    // Group by similarity score
                    let groupedByScore =
                        allResults
                        |> List.groupBy (fun (sim, _) -> Math.Round(sim, 2))
                        |> List.sortByDescending fst

                    // Get ground truth
                    let currentGroundTruth =
                        groundTruth
                        |> Array.filter (fun (gtSrc, _, _, _) -> gtSrc = sourceFuncName)

                    let correctRank =
                        groupedByScore
                        |> List.tryFindIndex (fun (score, functions) ->
                            score > 0.0 &&
                            functions |> List.exists (fun (_, fname) ->
                                currentGroundTruth
                                |> Array.exists (fun (_, gtTargets, gtAddrs, _) ->
                                    (gtTargets |> Array.exists (fun f -> fname = f)) ||
                                    (gtAddrs |> Array.exists (fun addr -> fname.Contains(addr)))
                                )
                            )
                        )

                    match correctRank with
                    | Some rank ->
                        let actualRank = rank + 1
                        let score = 1.0 / float actualRank
                        (actualRank, score)
                    | None -> (0, 0.0)

                let top5Correct =
                    if top1Correct then
                        true
                    else
                        top5
                        |> List.exists (fun (sim, fname) ->
                            sim > 0.0 &&
                            groundTruth
                            |> Array.exists (fun (gtSrc, gtTargets, gtAddrs, _) ->
                                sourceFuncName = gtSrc &&
                                ((gtTargets |> Array.exists (fun f -> fname = f)) ||
                                    (gtAddrs |> Array.exists (fun addr -> fname.Contains(addr))))
                            )
                        )

                if top1Correct then binaryTop1CorrectCount <- binaryTop1CorrectCount + 1
                if top5Correct then binaryTop5CorrectCount <- binaryTop5CorrectCount + 1

                if isInlinable && top1Correct then binaryInlineTop1CorrectCount <- binaryInlineTop1CorrectCount + 1
                if isInlinable && top5Correct then binaryInlineTop5CorrectCount <- binaryInlineTop5CorrectCount + 1

                totalMrrScore <- totalMrrScore + mrrScore

                let correctFuncs =
                    groundTruth
                    |> Array.filter (fun (gtSrc, _, _, _) -> gtSrc = sourceFuncName)
                    |> Array.collect (fun (_, gtTargets, gtAddrs, _) ->
                        Array.concat [gtTargets; gtAddrs])
                    |> String.concat ", "

                let logMessage =
                    sprintf "BinaryFileName: %s , sourceFuncName: %s , Top1: %s, Top5: %s , MRR_Rank: %d , MRR_Score: %.4f , Time(ms): %d , Matching candidate: %d , Matching result: [%s] , Ground truth: [%s]\n"
                        binaryFile
                        sourceFuncName
                        (if top1Correct then "O" else "X")
                        (if top5Correct then "O" else "X")
                        mrrRank
                        mrrScore
                        elapsedMs
                        bfuncsCount
                        (top5 |> List.map (fun (sim, fname) -> sprintf "%s(%.2f)" fname sim) |> String.concat ", ")
                        correctFuncs

                let funcResult =
                    sprintf "%s, %s, %s, %s, %d , %.4f, %d , %d\n"
                        binaryFile
                        sourceFuncName
                        (if top1Correct then "O" else "X")
                        (if top5Correct then "O" else "X")
                        mrrRank
                        mrrScore
                        elapsedMs
                        bfuncsCount

                File.AppendAllText(logPath, logMessage)
                File.AppendAllText(funcResultPath, funcResult)

                printfn "\x1b[%dm  - sourceFuncName: %s | Time: %dms | Total binary functions searched: %d\x1b[0m"
                    (if top1Correct then 32 else 31)
                    sourceFuncName
                    elapsedMs
                    bfuncsCount

                top5 |> List.iter (fun (sim, fname) ->
                    let candidateCorrect =
                        groundTruth
                        |> Array.exists (fun (gtSrc, gtTargets, gtAddrs, _) ->
                            sourceFuncName = gtSrc &&
                            ((gtTargets |> Array.exists (fun f -> fname = f)) ||
                                (gtAddrs |> Array.exists (fun addr -> fname.Contains(addr))))
                        )
                    if candidateCorrect then
                        printfn "\x1b[33m    %d. %s (similarity: %.2f)\x1b[0m"
                            (List.findIndex (fun (s, f) -> f = fname) top5 + 1) fname sim
                    else
                        printfn "    %d. %s (similarity: %.2f)"
                            (List.findIndex (fun (s, f) -> f = fname) top5 + 1) fname sim
                )
            else
                printfn "  - sourceFuncName: %s | Time: %dms (no ground truth)" sourceFuncName elapsedMs


                allResults
                |> List.take (min 5 (List.length allResults))
                |> List.iteri (fun idx (sim, fname) ->
                    printfn "    %d. %s (similarity: %.2f)" (idx + 1) fname sim
                )

            printfn ""
        )

        let avgMatchingTime =
            if testFunctionCount > 0 then
                float totalMatchingTime / float testFunctionCount
            else
                0.0

        if testFunctionCount > 0 then
            printfn "[*]'%s': top1 %d/%d, top5 %d/%d (empty blocks: %d) | Inline: top1 %d/%d, top5 %d/%d | Avg matching time: %.2fms"
                binaryFile
                binaryTop1CorrectCount testFunctionCount
                binaryTop5CorrectCount testFunctionCount
                emptyBlockCount
                binaryInlineTop1CorrectCount testInlineFunctionCount
                binaryInlineTop5CorrectCount testInlineFunctionCount
                avgMatchingTime

            Directory.CreateDirectory(Path.GetDirectoryName(gtTestedPath)) |> ignore
            let testedGroundTruth =
                groundTruth
                |> Array.filter (fun (src, _, _, _) ->
                    results |> Array.exists (fun (srcFunc, _, _, _, _, _, _, _) -> srcFunc = src))

            File.WriteAllLines(gtTestedPath,
                testedGroundTruth |> Array.map (fun (src, targetFuncs, targetAddrs, isInlinable) ->
                    let targetFuncsStr = String.Join(",", targetFuncs)
                    let targetAddrsStr = String.Join(",", targetAddrs)
                    let isInlinableStr = if isInlinable then "T" else "F"
                    sprintf "%s:[%s]:[%s]:%s" src targetFuncsStr targetAddrsStr isInlinableStr))

            // Calculate metrics
            let true_positive = binaryTop1CorrectCount
            let true_positive_at5 = binaryTop5CorrectCount

            let accuracy = if testFunctionCount > 0 then float binaryTop1CorrectCount / float testFunctionCount else 0.0
            let accuracy_at5 = if testFunctionCount > 0 then float binaryTop5CorrectCount / float testFunctionCount else 0.0

            let false_positive = testFunctionCount - binaryTop1CorrectCount
            let false_positive_at5 = testFunctionCount - binaryTop5CorrectCount
            let false_negative = testFunctionCount - binaryTop1CorrectCount
            let false_negative_at5 = testFunctionCount - binaryTop5CorrectCount

            let precision = if (true_positive + false_positive) > 0 then float true_positive / float (true_positive + false_positive) else 0.0
            let precision_at5 = if (true_positive_at5 + false_positive_at5) > 0 then float true_positive_at5 / float (true_positive_at5 + false_positive_at5) else 0.0
            let recall = if (true_positive + false_negative) > 0 then float true_positive / float (true_positive + false_negative) else 0.0
            let recall_at5 = if (true_positive_at5 + false_negative_at5) > 0 then float true_positive_at5 / float (true_positive_at5 + false_negative_at5) else 0.0

            // Add result
            let resultLine =
                sprintf "%s,%d,%d,%d,%d,%d,%d,%d,%d,%.4f,%.4f,%.4f,%.4f,%.4f,%.4f,%d,%d,%d,%.2f,%.4f,%d\n"
                    binaryFile
                    testFunctionCount
                    emptyBlockCount
                    true_positive
                    true_positive_at5
                    false_positive
                    false_positive_at5
                    false_negative
                    false_negative_at5
                    accuracy
                    accuracy_at5
                    precision
                    precision_at5
                    recall
                    recall_at5
                    testInlineFunctionCount
                    binaryInlineTop1CorrectCount
                    binaryInlineTop5CorrectCount
                    avgMatchingTime
                    totalMrrScore
                    binaryFunctionCount
            File.AppendAllText(resPath, resultLine)
        else
            printfn "[*]'%s': Matching completed, but it has no ground truth" binaryFile

            let resultLine =
                sprintf "%s,%d,%d,0,0,0,0,0,0,0.0000,0.0000,0.0000,0.0000,0.0000,0.0000,0,0,0,%.2f,0.0000,%d\n"
                    binaryFile
                    0
                    emptyBlockCount
                    avgMatchingTime
                    binaryFunctionCount
            File.AppendAllText(resPath, resultLine)

    let functionMatch (inputPath: string) =
        let matchSrcFunctionPath = inputPath + "/processed_input/features/matchcode"
        let srcFunctionCallGraphPath = inputPath + "/processed_input/info/matchcode/call_graph.json"
        let targetPath = Path.Combine(inputPath, "target")
        let resPath = Path.Combine(inputPath, "SBridge_matching_result.csv")
        let logPath = Path.Combine(inputPath, "SBridge_matching.log")
        let funcResultPath = Path.Combine(inputPath, "SBridge_func_result.csv")

        Directory.CreateDirectory(inputPath) |> ignore

        File.WriteAllText(resPath, "")
        File.WriteAllText(logPath, "")
        File.WriteAllText(funcResultPath, "")

        let srcFunctionCallGraphMap = loadCallGraphMap srcFunctionCallGraphPath

        let isEmptyFunction (sfuncPath: string) =
            try
                let content = File.ReadAllText(sfuncPath)
                let blocks = JsonConvert.DeserializeObject<ControlBlock list>(content)
                blocks.IsEmpty || blocks |> List.forall (fun block ->
                    block.block_content.IsEmpty && block.key_feature.IsEmpty)
            with
            | _ -> false

        let srcFunctionFiles =
            if Directory.Exists(matchSrcFunctionPath) then
                Directory.GetFiles(matchSrcFunctionPath, "*.sfunc")
                |> Array.filter (fun file ->
                    let fileName = Path.GetFileNameWithoutExtension(file)
                    not (isEmptyFunction file) &&
                    not (fileName.StartsWith(".")) &&
                    not (fileName.StartsWith("_")))
            else
                printfn "[!] Warning: Source function path does not exist: %s" matchSrcFunctionPath
                Array.empty

        let srcFunctionlist = orderFunctionsByCallGraph srcFunctionFiles srcFunctionCallGraphMap

        let binaryFiles =
            if Directory.Exists(targetPath) then
                Directory.GetFiles(targetPath)
                |> Array.map Path.GetFileName
                |> Array.toList
            else
                printfn "[!] Warning: Target path does not exist: %s" targetPath
                []

        let mutable top1Map = Dictionary<string, (float * string)>()

        binaryBlocksCache <- Dictionary<string, (string * ControlBlock list) list>()

        for binaryFile in binaryFiles do
            printfn "\n[*] Matching started for binary file: %s" binaryFile
            matchingProcess binaryFile srcFunctionlist srcFunctionCallGraphMap inputPath logPath funcResultPath

        printfn "\n[*] All binary file matching completed"

        exit 0
