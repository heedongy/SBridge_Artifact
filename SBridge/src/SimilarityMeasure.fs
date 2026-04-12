namespace SBridge

module SimilarityMeasure =
    open System
    open System.IO
    open System.Collections.Generic
    open SBridge.CBVector
    open SBridge.FunctionLists
    open SBridge.JsonConverters
    open SBridge.Types

    // Block Theta for similarity
    let blockThreshold = 0.7

    // Get ground truth from the specified path
    let getGroundTruth (gtPath: string) =
        let lines = File.ReadAllLines(gtPath)
        lines
        |> Array.map (fun line ->
            let parts = line.Split(':')
            let sourceFuncName = parts.[0]
            let targetFuncs = parts.[1].Trim('[', ']').Split(',') |> Array.map (fun s -> s.Trim())
            let targetAddrs = parts.[2].Trim('[', ']').Split(',') |> Array.map (fun s -> s.Trim())

            let isInlinable =
                (targetFuncs.Length = 1 &&
                 not (String.IsNullOrWhiteSpace(targetFuncs.[0])) &&
                 targetFuncs.[0] <> sourceFuncName) ||
                (targetFuncs.Length >= 2)

            (sourceFuncName, targetFuncs, targetAddrs, isInlinable)
        )
        |> Array.filter (fun (_, funcs, _, _) ->
            not (Array.isEmpty funcs && String.IsNullOrWhiteSpace funcs.[0])
        )

    // Check parameter similarity
    let paramCheck (src: ControlBlock) (bin: ControlBlock) =
        let srcParamCount = src.block_content.Length
        let binParamCount = bin.block_content.Length
        let maxParamCount = max srcParamCount binParamCount

        if maxParamCount = 0 then
            1.0
        else
            let calculateElementScore (srcElements: string list) (binElements: string list) =
                let maxElementsInParam = max srcElements.Length binElements.Length
                if maxElementsInParam = 0 then 0.0
                else
                    let (elementScore, _) =
                        srcElements
                        |> List.fold (fun (accScore, usedIndices) srcElement ->
                            let (bestScore, bestIdx) =
                                binElements
                                |> List.indexed
                                |> List.filter (fun (idx, _) -> not (Set.contains idx usedIndices))
                                |> List.fold (fun (currentBest, currentBestIdx) (binIdx, binElement) ->
                                    let score =
                                        if srcElement = binElement then 1.0
                                        else
                                            let srcType = srcElement.Split(',').[0].Trim('(', ')')
                                            let binType = binElement.Split(',').[0].Trim('(', ')')
                                            if srcType = binType then 0.5
                                            elif (srcType = "NUMTYPE" && binType = "NUM") || (srcType = "NUM" && binType = "NUMTYPE") then 1.0
                                            else 0.0
                                    if score > currentBest then (score, binIdx) else (currentBest, currentBestIdx)
                                ) (0.0, -1)

                            if bestIdx >= 0 then
                                (accScore + bestScore, Set.add bestIdx usedIndices)
                            else
                                (accScore, usedIndices)
                        ) (0.0, Set.empty)
                    elementScore / float maxElementsInParam

            let totalScore =
                [0 .. maxParamCount - 1]
                |> List.sumBy (fun i ->
                    let srcParam = if i < srcParamCount then Some(src.block_content.[i]) else None
                    let binParam = if i < binParamCount then Some(bin.block_content.[i]) else None

                    match srcParam, binParam with
                    | Some srcParamStr, Some binParamStr ->
                        let srcElements = srcParamStr.Split(';') |> Array.map (fun x -> x.Trim()) |> Array.toList
                        let binElements = binParamStr.Split(';') |> Array.map (fun x -> x.Trim()) |> Array.toList
                        calculateElementScore srcElements binElements
                    | Some _, None | None, Some _ -> 0.0
                    | None, None -> 1.0
                )

            totalScore / float maxParamCount


    // Check true block similarity
    let trueBlockCheck (src: ControlBlock) (bin: ControlBlock) =
        if bin.block_content.IsEmpty then
            0.0
        else
            trueBlockCheckSimilarity src.block_content bin.block_content

    // Check string similarity
    let stringCheck (src: ControlBlock) (bin: ControlBlock) =
        match src.key_feature, bin.key_feature with
        | srcFeature :: _, binFeature :: _ ->
            if srcFeature = binFeature then 1.0 else 0.0
        | _ -> 0.0

    // Calculate similarity between two blocks
    let calculateBlockSimilarity (srcBlock: ControlBlock) (binBlock: ControlBlock) (isSecondPass: bool) : float =
        if not isSecondPass then
            if srcBlock.block_type <> binBlock.block_type then 0.0
            else
                match srcBlock.block_type with
                | ControlBlockType.Condition
                | ControlBlockType.Loop ->
                    let condSim = trueBlockCheckSimilarity srcBlock.key_feature binBlock.key_feature
                    if condSim = 1.0 then
                        condSim
                    else
                        let t = trueBlockCheck srcBlock binBlock
                        if srcBlock.block_content.IsEmpty then
                            condSim
                        else
                            (condSim * 0.5) + (t * 0.5)
                | ControlBlockType.String -> stringCheck srcBlock binBlock
                | ControlBlockType.LibcFunc ->
                    match srcBlock.key_feature, binBlock.key_feature with
                    | src :: _, bin :: _ ->
                        let srcParts = src.Split(',')
                        let binParts = bin.Split(',')
                        match srcParts, binParts with
                        | [|srcName; srcCount|], [|binName; binCount|] ->
                            if srcName = binName then 1.0
                            elif srcCount = binCount then paramCheck srcBlock binBlock
                            else 0.0
                        | _ -> 0.0
                    | _ -> 0.0
                | ControlBlockType.RecursiveFunc
                | ControlBlockType.CalleeFunc ->
                    match srcBlock.key_feature, binBlock.key_feature with
                    | src :: _, bin :: _ ->
                        let srcParts = src.Split(',')
                        let binParts = bin.Split(',')
                        match srcParts, binParts with
                        | [|srcName; srcCount|], [|binName; binCount|] ->
                            if srcName = binName && srcCount = binCount then 1.0
                            elif srcCount = "0" && binCount = "0" then 1.0
                            elif srcCount = binCount then paramCheck srcBlock binBlock
                            else 0.0
                        | _ -> 0.0
                    | _ -> 0.0
                | ControlBlockType.Else -> trueBlockCheck srcBlock binBlock
        else
            match srcBlock.block_type with
            | ControlBlockType.Condition ->
                match binBlock.block_type with
                | ControlBlockType.Condition
                | ControlBlockType.Loop ->
                    let condSim = trueBlockCheckSimilarity srcBlock.key_feature binBlock.key_feature
                    if condSim = 1.0 then
                        condSim
                    else
                        let t = trueBlockCheck srcBlock binBlock
                        if srcBlock.block_content.IsEmpty then
                            condSim
                        else
                            (condSim * 0.5) + (t * 0.5)
                | ControlBlockType.Else ->
                    trueBlockCheck srcBlock binBlock
                | _ -> 0.0
            | ControlBlockType.Loop ->
                match binBlock.block_type with
                | ControlBlockType.Loop
                | ControlBlockType.Condition ->
                    let condSim = trueBlockCheckSimilarity srcBlock.key_feature binBlock.key_feature
                    if condSim = 1.0 then
                        condSim
                    else
                        let t = trueBlockCheck srcBlock binBlock
                        if srcBlock.block_content.IsEmpty then
                            condSim  // Use only condSim when srcBlock is empty
                        else
                            (condSim * 0.5) + (t * 0.5)
                | _ -> 0.0
            | ControlBlockType.String -> stringCheck srcBlock binBlock
            | ControlBlockType.LibcFunc ->
                match binBlock.block_type with
                | ControlBlockType.LibcFunc
                | ControlBlockType.CalleeFunc ->
                    match srcBlock.key_feature, binBlock.key_feature with
                    | src :: _, bin :: _ ->
                        let srcParts = src.Split(',')
                        let binParts = bin.Split(',')
                        match srcParts, binParts with
                        | [|srcName; srcCount|], [|binName; binCount|] ->
                            if srcName = binName then 1.0
                            elif srcCount = "0" && binCount = "0" then 1.0
                            else paramCheck srcBlock binBlock
                        | _ -> 0.0
                    | _ -> 0.0
                | _ -> 0.0
            | ControlBlockType.RecursiveFunc
            | ControlBlockType.CalleeFunc ->
                match binBlock.block_type with
                | ControlBlockType.LibcFunc
                | ControlBlockType.CalleeFunc ->
                    match srcBlock.key_feature, binBlock.key_feature with
                    | src :: _, bin :: _ ->
                        let srcParts = src.Split(',')
                        let binParts = bin.Split(',')
                        match srcParts, binParts with
                        | [|srcName; srcCount|], [|binName; binCount|] ->
                            if srcName = binName && srcCount = binCount then 1.0
                            elif srcCount = "0" && binCount = "0" then 1.0
                            elif srcCount = binCount then paramCheck srcBlock binBlock
                            else 0.0
                        | _ -> 0.0
                    | _ -> 0.0
                | _ -> 0.0
            | ControlBlockType.Else ->
                match binBlock.block_type with
                | ControlBlockType.Else
                | ControlBlockType.Condition ->
                    trueBlockCheck srcBlock binBlock
                | _ -> 0.0


    let compareFunctions (sourceBlocks: ControlBlock list) (binaryBlocks: ControlBlock list) (checkLength: bool) : float =
        let totalSource = float sourceBlocks.Length
        if totalSource = 0.0 then
            0.0
        else
            let totalBinary = float binaryBlocks.Length
            let remainingBinary = ResizeArray<ControlBlock>(binaryBlocks)

            let mutable matchedCount = 0
            let mutable similaritySum = 0.0

            let tryFindMatch (srcBlock: ControlBlock) =
                if remainingBinary.Count = 0 then
                    false
                else
                    let mutable bestScore = 0.0
                    let mutable bestIndex = -1

                    for i = 0 to remainingBinary.Count - 1 do
                        let binBlock = remainingBinary.[i]
                        let score = calculateBlockSimilarity srcBlock binBlock true
                        if score > bestScore then
                            bestScore <- score
                            bestIndex <- i

                    if bestScore >= blockThreshold then
                        matchedCount <- matchedCount + 1
                        similaritySum <- similaritySum + bestScore
                        remainingBinary.RemoveAt(bestIndex)
                        true
                    else
                        false

            for srcBlock in sourceBlocks do
                tryFindMatch srcBlock |> ignore

            if matchedCount = 0 then
                0.0
            else
                let matchedRatio = float matchedCount / totalSource
                if checkLength then
                    let lengthWeight = 1.0 / (Math.Log(1.0 + totalBinary / totalSource) + 1.0)
                    lengthWeight * matchedRatio
                else
                    matchedRatio


    let compareVulPatchFunctions (vulBlocks: ControlBlock list) (patchBlocks: ControlBlock list) (binaryBlocks: ControlBlock list) (vulOnlyBlocks: ControlBlock list) (patchOnlyBlocks: ControlBlock list) (checkLength: bool) : float * float =
        match vulBlocks, patchBlocks, binaryBlocks with
        | [], _, _ | _, [], _ | _, _, [] -> (0.0, 0.0)
        | _ ->
            let findBestMatches blocks =
                blocks
                |> List.mapi (fun idx block ->
                    binaryBlocks
                    |> List.indexed
                    |> List.map (fun (binIdx, binBlock) ->
                        let score = max (calculateBlockSimilarity block binBlock false)
                                        (calculateBlockSimilarity block binBlock true)
                        (binIdx, score))
                    |> List.maxBy snd
                    |> function
                        | (binIdx, score) when score >= 0.5 -> Some (idx, binIdx, score)
                        | _ -> None)
                |> List.choose id

            let vulMatches = findBestMatches vulBlocks
            let patchMatches = findBestMatches patchBlocks

            let createIndexSet blocks onlyBlocks =
                blocks
                |> List.indexed
                |> List.filter (fun (_, block) -> List.contains block onlyBlocks)
                |> List.map fst
                |> Set.ofList

            let vulOnlyIndices = createIndexSet vulBlocks vulOnlyBlocks
            let patchOnlyIndices = createIndexSet patchBlocks patchOnlyBlocks

            let filterMatches matches indices =
                matches |> List.filter (fun (idx, _, _) -> Set.contains idx indices)

            let vulOnlyMatches = filterMatches vulMatches vulOnlyIndices
            let patchOnlyMatches = filterMatches patchMatches patchOnlyIndices

            let resolveConflicts =
                (vulOnlyMatches |> List.map (fun (idx, binIdx, score) -> ("vul", idx, binIdx, score))) @
                (patchOnlyMatches |> List.map (fun (idx, binIdx, score) -> ("patch", idx, binIdx, score)))
                |> List.groupBy (fun (_, _, binIdx, _) -> binIdx)
                |> List.map (fun (_, candidates) -> candidates |> List.maxBy (fun (_, _, _, score) -> score))

            let vulWinners, patchWinners =
                resolveConflicts
                |> List.partition (fun (side, _, _, _) -> side = "vul")

            let calculateMetrics (winners: (string * int * int * float) list) (onlyBlocks: ControlBlock list) =
                let totalOnlyBlocks = vulOnlyBlocks.Length + patchOnlyBlocks.Length
                let coverage = if List.isEmpty onlyBlocks then 0.0 else float winners.Length / float totalOnlyBlocks
                let avgSim = match winners with
                              | [] -> 0.0
                              | _ -> winners |> List.averageBy (fun (_, _, _, score) -> score)
                coverage * 0.5 + avgSim * 0.5

            let vulFinal = calculateMetrics vulWinners vulOnlyBlocks
            let patchFinal = calculateMetrics patchWinners patchOnlyBlocks

            (vulFinal, patchFinal)

    // Extract string features from blocks
    let extractStringFeatures (blocks: ControlBlock list) =
        blocks
        |> List.filter (fun block -> block.block_type = ControlBlockType.String)
        |> List.collect (fun block -> block.key_feature)
        |> Set.ofList

    // Find the most similar binary function
    let findMostSimilarBinaryFunction (vulFunctionName: string) (sourceBlocks: ControlBlock list) (allBinaryFunctions: (string * ControlBlock list) list): float * string =
        match sourceBlocks with
        | [] ->
            printfn "[*] This source code cannot detect vulnerabilities. (No block)"
            (0.0, "")
        | _ ->
            allBinaryFunctions
            |> List.tryFind (fun (funcName, _) ->
                String.Equals(funcName, vulFunctionName, StringComparison.OrdinalIgnoreCase))
            |> function
                | Some (funcName, blocks) ->
                    let similarity = compareFunctions sourceBlocks blocks true
                    (similarity, funcName)
                | None ->
                    // String block priority filtering
                    let sourceStringFeatures = extractStringFeatures sourceBlocks

                    let candidateFunctions =
                        if Set.isEmpty sourceStringFeatures then
                            allBinaryFunctions
                        else
                            allBinaryFunctions
                            |> List.filter (fun (_, blocks) ->
                                let binaryStringFeatures = extractStringFeatures blocks
                                not (Set.isEmpty (Set.intersect sourceStringFeatures binaryStringFeatures)))
                            |> function
                                | [] -> allBinaryFunctions
                                | filtered -> filtered

                    // Calculate similarities and find best match
                    candidateFunctions
                    |> List.map (fun (funcName, blocks) ->
                        let similarity = compareFunctions sourceBlocks blocks true
                        (similarity, funcName))
                    |> List.sortByDescending fst
                    |> function
                        | (score, funcName) :: _ -> (score, funcName)
                        | [] -> (0.0, "")
