namespace SBridge

module CBVector =
    open System
    open System.Numerics
    open FunctionLists
    open Types
    open Utils

    let blockContentVectorVocab () =
        let elementTypes = ["LVAR"; "PVAR"]
        let operators = [
            "equOpr"; "comOpr"; "InfiniteLoop"; "assignOpr";
            "addOpr"; "subOpr"; "mulOpr"; "divOpr"; "modOpr";
            "notOpr"; "xorOpr"; "accOpr"; "shiftOpr";
            "logAndOrOpr"; "bitAndOrOpr"
        ]

        let elementTypeMap = elementTypes |> List.mapi (fun i t -> (t, i)) |> Map.ofList
        let operatorMap = operators |> List.mapi (fun i op -> (op, i + elementTypes.Length)) |> Map.ofList
        let libcFuncMap =
            libcFunctions
            |> List.mapi (fun i func -> (func, i + elementTypes.Length + operators.Length))
            |> Map.ofList
        let staticElementCount = elementTypes.Length + operators.Length + libcFuncMap.Count
        let functionCountIndex = staticElementCount
        let dynamicStartIndex = staticElementCount + 1

        {
            ElementTypeToIndex = elementTypeMap
            OperatorToIndex = operatorMap
            LibcFuncToIndex = libcFuncMap
            FunctionCountIndex = functionCountIndex
            StaticNumberToIndex = Map.empty
            LiteralValueToIndex = Map.empty
            FunctionValueToIndex = Map.empty
            NumberToIndex = Map.empty
            NextIndex = dynamicStartIndex
        }

    let preprocessBVType (srcKey: string list) =
        let mutable elements = []
        let mutable functionNames = Set.empty<string>
        let mutable i = 0

        while i < srcKey.Length do
            let (elementType, value) =
                let arr = srcKey.[i].Trim('(', ')').Split(',')
                if arr.Length < 2 then failwithf "Invalid element format: %s" srcKey.[i]
                (arr.[0].Trim(), arr.[1].Trim())

            match elementType with
            | "NUM" ->
                try
                    let int64Value = System.Int64.Parse(value)
                    elements <- BCVNum int64Value :: elements
                with
                | :? System.FormatException ->
                    match System.Int64.TryParse(value) with
                    | true, int64Val -> elements <- BCVNum int64Val :: elements
                    | false, _ -> ()
                i <- i + 1
            | "NUMTYPE" -> elements <- BCVNumType :: elements; i <- i + 1
            | "IDENTIFIER" | "POINTERVAR" ->
                if value.Contains("PVAR") then elements <- BCVElementType "PVAR" :: elements
                elif value = "false" then elements <- BCVNum 0L :: elements
                elif value = "true" then elements <- BCVNum 1L :: elements
                else elements <- BCVElementType "LVAR" :: elements
                i <- i + 1
            | "LITERAL" ->
                let (literalType, asciiOpt) = processLiteralValue value
                match asciiOpt with
                | Some asciiValue -> elements <- BCVNum asciiValue :: elements
                | None -> elements <- BCVLiteralValue value :: elements
                i <- i + 1
            | "FUNCTION" ->
                functionNames <- functionNames.Add(value)
                elements <- BCVFunctionValue value :: elements; i <- i + 1
            | "FUNCTION_STRIPPED" ->
                functionNames <- functionNames.Add(value)
                elements <- BCVFunctionValue value :: elements; i <- i + 1
            | "LIBCFUNC" ->
                elements <- BCVElementType value :: elements; i <- i + 1
            | "OPERATOR" ->
                let operatorClass = classifyOperator value
                match value with
                | "preIncrement" | "postIncrement" ->
                    elements <- BCVNum 1L :: BCVOperator "addOpr" :: elements
                | "preDecrement" | "postDecrement" ->
                    elements <- BCVNum (-1L) :: BCVOperator "addOpr" :: elements
                | "minus" ->
                    if i + 1 < srcKey.Length then
                        let nextElement = srcKey.[i + 1]
                        let (nextElementType, nextValue) =
                            let arr = nextElement.Trim('(', ')').Split(',')
                            if arr.Length >= 2 then (arr.[0].Trim(), arr.[1].Trim()) else ("", "")

                        match nextElementType with
                        | "NUM" ->
                            match Int64.TryParse(nextValue) with
                            | (true, value) ->
                                elements <- BCVNum (-value) :: elements
                                i <- i + 1
                            | (false, _) ->
                                elements <- BCVOperator "subOpr" :: elements
                        | _ ->
                            elements <- BCVOperator "subOpr" :: elements
                    else
                        elements <- BCVOperator "subOpr" :: elements
                | "lessEqualsThan" ->
                    if i + 1 < srcKey.Length then
                        let nextElement = srcKey.[i + 1]
                        let (nextElementType, nextValue) =
                            let arr = nextElement.Trim('(', ')').Split(',')
                            if arr.Length >= 2 then (arr.[0].Trim(), arr.[1].Trim()) else ("", "")

                        match nextElementType with
                        | "NUM" ->
                            match Int64.TryParse(nextValue) with
                            | (true, numValue) when numValue < System.Int64.MaxValue ->
                                elements <- BCVNum (numValue + 1L) :: BCVOperator "comOpr" :: elements
                                i <- i + 1
                            | _ ->
                                elements <- BCVOperator "comOpr" :: elements
                        | _ ->
                            elements <- BCVOperator "comOpr" :: elements
                    else
                        elements <- BCVOperator "comOpr" :: elements
                | "greaterEqualsThan" ->
                    if i + 1 < srcKey.Length then
                        let nextElement = srcKey.[i + 1]
                        let (nextElementType, nextValue) =
                            let arr = nextElement.Trim('(', ')').Split(',')
                            if arr.Length >= 2 then (arr.[0].Trim(), arr.[1].Trim()) else ("", "")

                        match nextElementType with
                        | "NUM" ->
                            match Int64.TryParse(nextValue) with
                            | (true, numValue) when numValue > System.Int64.MinValue ->
                                elements <- BCVNum (numValue - 1L) :: BCVOperator "comOpr" :: elements
                                i <- i + 1
                            | _ ->
                                elements <- BCVOperator "comOpr" :: elements
                        | _ ->
                            elements <- BCVOperator "comOpr" :: elements
                    else
                        elements <- BCVOperator "comOpr" :: elements
                | _ ->
                    elements <- BCVOperator operatorClass :: elements
                i <- i + 1
            | _ -> i <- i + 1

        let functionCount = functionNames.Count
        let finalElements = if functionCount > 0 then BCVFunctionCount functionCount :: List.rev elements else List.rev elements
        finalElements

    // Convert Logical to Comparison
    let convertLogicalToComparison (elements: BlockContentVectorType list) =
        match elements with
        | [BCVElementType "LVAR"] ->
            [BCVOperator "equOpr"; BCVElementType "LVAR"; BCVNum 0L]
        | [BCVOperator "notOpr"; BCVElementType "LVAR"] ->
            [BCVOperator "equOpr"; BCVElementType "LVAR"; BCVNum 0L]
        | _ -> elements

    let bcVocabulary (srcElements: BlockContentVectorType list) (binElements: BlockContentVectorType list) (excludeFunctionValues: bool) =
        let mutable vocab = blockContentVectorVocab ()

        let allElements = srcElements @ binElements

        for element in allElements do
            match element with
            | BCVLiteralValue value ->
                if not (vocab.LiteralValueToIndex.ContainsKey value) then
                    vocab <- {
                        vocab with
                            LiteralValueToIndex = vocab.LiteralValueToIndex.Add(value, vocab.NextIndex)
                            NextIndex = vocab.NextIndex + 1
                    }
            | BCVFunctionValue value ->
                if not (vocab.FunctionValueToIndex.ContainsKey value) then
                    vocab <- {
                        vocab with
                            FunctionValueToIndex = vocab.FunctionValueToIndex.Add(value, vocab.NextIndex)
                            NextIndex = vocab.NextIndex + 1
                    }
            | BCVNum num ->
                if not (vocab.NumberToIndex.ContainsKey num) then
                    vocab <- {
                        vocab with
                            NumberToIndex = vocab.NumberToIndex.Add(num, vocab.NextIndex)
                            NextIndex = vocab.NextIndex + 1
                    }
            | BCVFunctionCount count ->
                ()
            | BCVNumType ->
                ()
            | _ -> ()

        vocab

    let blockContentVectorize (elements: BlockContentVectorType list) (vocabulary: BlockContentVectorVocabulary) (excludeFunctionValues: bool) (srcFunctionCount: int) =
        let vector = Array.zeroCreate vocabulary.NextIndex
        let mutable numbers = []
        let mutable numTypePositions = []
        let mutable position = 0

        elements |> List.iter (fun element ->
            match element with
            | BCVNum num ->
                numbers <- num :: numbers
                match vocabulary.NumberToIndex.TryFind num with
                | Some index -> vector.[index] <- vector.[index] + 1.0
                | None -> ()
            | BCVNumType ->
                numTypePositions <- position :: numTypePositions
            | BCVElementType elementType ->
                match vocabulary.ElementTypeToIndex.TryFind elementType with
                | Some index ->
                    vector.[index] <- vector.[index] + 1.0
                | None ->
                    match vocabulary.LibcFuncToIndex.TryFind elementType with
                    | Some index ->
                        vector.[index] <- vector.[index] + 1.0
                    | None ->
                        ()
            | BCVOperator op ->
                match vocabulary.OperatorToIndex.TryFind op with
                | Some index -> vector.[index] <- vector.[index] + 1.0
                | None -> ()  // Skip UnknownOpr from vector
            | BCVLiteralValue value ->
                match vocabulary.LiteralValueToIndex.TryFind value with
                | Some index -> vector.[index] <- vector.[index] + 1.0
                | None -> ()
            | BCVFunctionValue value ->
                match vocabulary.FunctionValueToIndex.TryFind value with
                | Some index -> vector.[index] <- vector.[index] + 1.0
                | None -> ()
            | BCVFunctionCount count ->
                // Return 1.0 if count matches source function count, 0.0 otherwise
                vector.[vocabulary.FunctionCountIndex] <- if count = srcFunctionCount then 1.0 else 0.0
            position <- position + 1)

        {
            ExactVector = vector
            NumTypePositions = List.rev numTypePositions
            Numbers = elements |> List.choose (function BCVNum n -> Some n | _ -> None) |> List.rev
        }


    // BlockContentVector similarity
    let blockContentVectorSimilarity (vec1: BlockContentVector) (vec2: BlockContentVector) (vocabulary: BlockContentVectorVocabulary) =
        let getBlockContentVectorNumberIndex (num: int64) =
            match vocabulary.NumberToIndex.TryFind num with
            | Some index -> Some index
            | None -> None

        // 1. Create expanded vectors
        let expandedVec1 = Array.copy vec1.ExactVector
        let expandedVec2 = Array.copy vec2.ExactVector

        // 2. Handle NUMTYPE wildcards
        if not vec1.NumTypePositions.IsEmpty then
            let numTypeCount = vec1.NumTypePositions.Length
            let availableNumbers = vec2.Numbers |> List.filter (fun num ->
                not (List.contains num vec1.Numbers))
            let numbersToAbsorb = availableNumbers |> List.truncate numTypeCount
            numbersToAbsorb |> List.iter (fun num ->
                match getBlockContentVectorNumberIndex num with
                | Some index when index < expandedVec1.Length ->
                    expandedVec1.[index] <- expandedVec1.[index] + 1.0
                | _ -> ())

        if not vec2.NumTypePositions.IsEmpty then
            let numTypeCount = vec2.NumTypePositions.Length
            let availableNumbers = vec1.Numbers |> List.filter (fun num ->
                not (List.contains num vec2.Numbers))
            let numbersToAbsorb = availableNumbers |> List.truncate numTypeCount
            numbersToAbsorb |> List.iter (fun num ->
                match getBlockContentVectorNumberIndex num with
                | Some index when index < expandedVec2.Length ->
                    expandedVec2.[index] <- expandedVec2.[index] + 1.0
                | _ -> ())

        // 3. Calculate cosine similarity
        cosineSimilarity expandedVec1 expandedVec2 true

    // BlockContentVector Calculate Numbers
    let calculateNumbersBV(elements: BlockContentVectorType list) =
        let rec calc acc = function
            | BCVOperator "addOpr" :: BCVNum n1 :: BCVNum n2 :: rest ->
                calc (BCVNum (n1 + n2) :: acc) rest
            | BCVOperator "subOpr" :: BCVNum n1 :: BCVNum n2 :: rest ->
                calc (BCVNum (n1 - n2) :: acc) rest
            | BCVOperator "mulOpr" :: BCVNum n1 :: BCVNum n2 :: rest ->
                calc (BCVNum (n1 * n2) :: acc) rest
            | BCVOperator "divOpr" :: BCVNum n1 :: BCVNum n2 :: rest when n2 <> 0L ->
                calc (BCVNum (n1 / n2) :: acc) rest
            | BCVOperator "modOpr" :: BCVNum n1 :: BCVNum n2 :: rest when n2 <> 0L ->
                calc (BCVNum (n1 % n2) :: acc) rest
            | BCVOperator "notOpr" :: BCVNum n :: rest ->
                calc (BCVNum (~~~n) :: acc) rest  // bitwise NOT
            | BCVOperator "xorOpr" :: BCVNum n1 :: BCVNum n2 :: rest ->
                calc (BCVNum (n1 ^^^ n2) :: acc) rest  // XOR
            | element :: rest ->
                calc (element :: acc) rest
            | [] ->
                List.rev acc
        calc [] elements

    let trueBlockCheckSimilarity (srcTB: string list) (binTB: string list) : float =
        try
            if srcTB.IsEmpty || binTB.IsEmpty then 0.0
            else
                let srcElements = preprocessBVType srcTB
                                |> calculateNumbersBV
                                |> convertLogicalToComparison
                let binElements = preprocessBVType binTB
                                |> calculateNumbersBV
                                |> convertLogicalToComparison
                let srcFunctionCount =
                    srcElements
                    |> List.tryFind (function BCVFunctionCount count -> true | _ -> false)
                    |> function
                        | Some (BCVFunctionCount count) -> count
                        | _ -> 0

                let vocabulary = bcVocabulary srcElements binElements false

                let srcVector = blockContentVectorize srcElements vocabulary false srcFunctionCount
                let binVector = blockContentVectorize binElements vocabulary false srcFunctionCount

                blockContentVectorSimilarity srcVector binVector vocabulary
        with ex ->
            0.0
