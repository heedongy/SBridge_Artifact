namespace SBridge

module Utils =
    open System
    open System.IO
    open System.Numerics
    open System.Text.RegularExpressions
    open System.Diagnostics
    open SBridge.Types

   
    // Operator classification
    let classifyOperator (operatorValue: string) : string =
        match operatorValue with
        | "equals" | "notEquals" -> "equOpr"
        | "lessThan" | "greaterThan" | "lessEqualsThan" | "greaterEqualsThan" -> "comOpr"
        | "InfiniteLoop" -> "InfiniteLoop"
        | "addition" | "assignmentPlus" -> "addOpr"
        | "subtraction" | "assignmentMinus" -> "subOpr"
        | "multiplication" | "assignmentMultiplication" -> "mulOpr"
        | "division" | "assignmentDivision" -> "divOpr"
        | "modulo" | "assignmentModulo" -> "modOpr"
        | "logicalNot" | "not" -> "notOpr"
        | "logicalXor" | "xor" -> "xorOpr"
        | "indirectFieldAccess" | "fieldAccess" | "indirectIndexAccess" -> "accOpr"
        | "shiftLeft" | "arithmeticShiftLeft" | "shiftRight" | "arithmeticShiftRight"
        | "assignmentArithmeticShiftLeft" | "assignmentArithmeticShiftRight" -> "shiftOpr"
        | "logicalAnd" | "logicalOr" -> "logAndOrOpr"
        | "or" | "and"  -> "bitAndOrOpr"
        | "assignment" -> "assignOpr"
        | _ ->
            "UnknownOpr"

    // cosine similarity for vectors
    let cosineSimilarity (vec1: float[]) (vec2: float[]) (expandToSameLength: bool) =
        let (finalVec1, finalVec2) = 
            if expandToSameLength then
                let maxLength = max vec1.Length vec2.Length
                let expandedVec1 = Array.zeroCreate maxLength
                let expandedVec2 = Array.zeroCreate maxLength
                Array.Copy(vec1, expandedVec1, vec1.Length)
                Array.Copy(vec2, expandedVec2, vec2.Length)
                (expandedVec1, expandedVec2)
            else
                if vec1.Length <> vec2.Length then
                    failwith "Vector lengths must be equal when expandToSameLength is false"
                (vec1, vec2)
        
        let len = finalVec1.Length
        let mutable dotProduct = 0.0
        let mutable norm1Sq = 0.0
        let mutable norm2Sq = 0.0
        
        let vectorSize = Vector<float>.Count
        let vectorizedLength = (len / vectorSize) * vectorSize
        
        let mutable i = 0
        while i < vectorizedLength do
            let v1 = Vector<float>(ReadOnlySpan<float>(finalVec1, i, vectorSize))
            let v2 = Vector<float>(ReadOnlySpan<float>(finalVec2, i, vectorSize))
            
            dotProduct <- dotProduct + Vector.Dot(v1, v2)
            norm1Sq <- norm1Sq + Vector.Dot(v1, v1)
            norm2Sq <- norm2Sq + Vector.Dot(v2, v2)
            
            i <- i + vectorSize
        
        // Handle remaining elements
        while i < len do
            let a = finalVec1.[i]
            let b = finalVec2.[i]
            dotProduct <- dotProduct + a * b
            norm1Sq <- norm1Sq + a * a
            norm2Sq <- norm2Sq + b * b
            i <- i + 1
        
        let norm1 = sqrt norm1Sq
        let norm2 = sqrt norm2Sq
        
        if norm1 = 0.0 || norm2 = 0.0 then 0.0
        else dotProduct / (norm1 * norm2)

    // Wrapper functions for backward compatibility
    let blockContentVectorCosineSimilarity (vec1: float[]) (vec2: float[]) =
        cosineSimilarity vec1 vec2 true



    // String normalization
    let normalizeString (input: string) : string =
        input.Replace("\\n", "")
             .Replace("\\r", "")
             .Replace("\\t", "")
             .Replace("\n", "")
             .Replace("\r", "")
             .Replace("\t", "")
             .Replace("{", "")
             .Replace("}", "")
             .Replace(" ", "")
             .Replace("\\", "")
             .Replace("'", "")
             .ToLower()

    let getAsciiValue (charValue: string) : int64 =
        match charValue with
        | "\\0" -> 0L
        | "\\n" -> 10L
        | "\\r" -> 13L
        | "\\t" -> 9L
        | "\\b" -> 8L
        | "\\f" -> 12L
        | "\\v" -> 11L
        | "\\\\" -> 92L
        | "\\'" -> 39L
        | "\\\"" -> 34L
        | _ when charValue.Length = 1 -> int64 (char charValue)
        | _ -> 0L

    // Common literal processing
    let processLiteralValue (value: string) : (string * int64 option) =
        if value.Length = 3 && value.StartsWith("'") && value.EndsWith("'") then
            let charValue = value.Substring(1, 1)
            let asciiValue = getAsciiValue charValue
            ("ASCII", Some asciiValue)
        elif value.Length = 4 && value.StartsWith("'\\") && value.EndsWith("'") then
            let escapeSeq = value.Substring(1, 2)
            let asciiValue = getAsciiValue escapeSeq
            ("ASCII", Some asciiValue)
        else
            ("LITERAL", None)

     // Helper function to filter out invalid CalleeFunc blocks
    let blockFilter (blocks: ControlBlock list) : ControlBlock list =
        blocks
        |> List.filter (fun block ->
            not (block.block_type = ControlBlockType.CalleeFunc &&
                 (block.key_feature.IsEmpty ||
                  (match block.key_feature with
                   | head::_ ->
                       let parts = head.Split(',')
                       parts.Length < 2 || String.IsNullOrEmpty(parts.[0])
                   | [] -> true))))

    // Function extraction functions
    let createCtagFunction fileName = {
        ParentFile = fileName
        ParentNumLoc = 0
        Name = ""
        Lines = (0,0)
        FuncId = 0
        ParameterList = []
        VariableList = []
        DataTypeList = []
        FuncCalleeList = []
        FuncBody = ""
    }

    let extractFunctionInfo (filePath: string) =
        let delimiter = "\r\0?\r?\0\r"
        let command = sprintf "ctags -f - --kinds-C=* --fields=neKSt \"%s\"" filePath

        let astString =
            try
                let startInfo = System.Diagnostics.ProcessStartInfo(
                    FileName = "bash",
                    Arguments = sprintf "-c \"%s\"" command,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                )
                use proc = System.Diagnostics.Process.Start(startInfo)
                let output = proc.StandardOutput.ReadToEnd()
                proc.WaitForExit()
                output
            with
                | ex -> ""

        let normalizedFile = filePath.Replace("\\", "/").Trim()

        let lines =
            try
                File.ReadAllLines(normalizedFile)
            with
                | _ -> [||]

        if lines.Length = 0 then
            ([], [], [])
        else
            let local = Regex(@"local")
            let parameter = Regex(@"parameter")
            let func = Regex(@"(function)")
            let dataType = Regex(@"(typeref:)\w*(:)")
            let number = Regex(@"(\d+)")

            let functionList = astString.Split('\n')
            let mutable funcId = 1
            let functionInstanceList = ResizeArray<CtagFunction>()

            let variables = ResizeArray<string[]>()
            let parameters = ResizeArray<string[]>()

            // Collect variables and parameters
            for line in functionList do
                try
                    let elemList = Regex.Replace(line, @"[\t\s ]{2,}", "").Split('\t')
                    if line <> "" && elemList.Length >= 6 then
                        if local.IsMatch(elemList.[3]) || local.IsMatch(elemList.[4]) then
                            variables.Add(elemList)
                        elif parameter.IsMatch(elemList.[3]) || parameter.IsMatch(elemList.[4]) then
                            parameters.Add(elemList)
                with _ -> ()

            // Collect function information - only first function
            let firstFunctionLine = 
                functionList 
                |> Array.tryFind (fun line ->
                    try
                        let elemList = Regex.Replace(line, @"[\t\s ]{2,}", "").Split('\t')
                        line <> "" && elemList.Length >= 8 && func.IsMatch(elemList.[3])
                    with _ -> false)
                    
            match firstFunctionLine with
            | Some line ->
                try
                    let elemList = Regex.Replace(line, @"[\t\s ]{2,}", "").Split('\t')
                    let functionInstance = createCtagFunction(filePath)

                    functionInstance.Name <- elemList.[0]
                    functionInstance.ParentFile <- elemList.[1]
                    let declaredStartLine = int (number.Match(elemList.[4]).Groups.[0].Value)

                    let startLine =
                        if declaredStartLine > 1 &&
                           not (String.IsNullOrWhiteSpace(lines.[declaredStartLine - 2])) &&
                           not (lines.[declaredStartLine - 2].Contains("{")) then
                            declaredStartLine - 1
                        else
                            declaredStartLine
                    let endLine = int (number.Match(elemList.[7]).Groups.[0].Value)
                    functionInstance.Lines <- (startLine, endLine)

                    // Process parameters
                    for param in parameters do
                        let lineNum =
                            if number.IsMatch(param.[4]) then int (number.Match(param.[4]).Groups.[0].Value)
                            elif param.Length > 5 && number.IsMatch(param.[5]) then int (number.Match(param.[5]).Groups.[0].Value)
                            else 0

                        if param.Length >= 4 &&
                           lineNum >= startLine &&
                           lineNum <= endLine then
                            functionInstance.ParameterList <- param.[0] :: functionInstance.ParameterList

                            if param.Length >= 6 && dataType.IsMatch(param.[5]) then
                                functionInstance.DataTypeList <-
                                    Regex.Replace(dataType.Replace(param.[5], ""), @" \*$", "")
                                    :: functionInstance.DataTypeList
                            elif param.Length >= 7 && dataType.IsMatch(param.[6]) then
                                functionInstance.DataTypeList <-
                                    Regex.Replace(dataType.Replace(param.[6], ""), @" \*$", "")
                                    :: functionInstance.DataTypeList

                    // Process variables
                    for variable in variables do
                        let lineNum =
                            if number.IsMatch(variable.[4]) then int (number.Match(variable.[4]).Groups.[0].Value)
                            elif variable.Length > 5 && number.IsMatch(variable.[5]) then int (number.Match(variable.[5]).Groups.[0].Value)
                            else 0

                        if variable.Length >= 4 &&
                           lineNum >= startLine &&
                           lineNum <= endLine then
                            functionInstance.VariableList <- variable.[0] :: functionInstance.VariableList

                            if variable.Length >= 6 && dataType.IsMatch(variable.[5]) then
                                functionInstance.DataTypeList <-
                                    Regex.Replace(dataType.Replace(variable.[5], ""), @" \*$", "")
                                    :: functionInstance.DataTypeList
                            elif variable.Length >= 7 && dataType.IsMatch(variable.[6]) then
                                functionInstance.DataTypeList <-
                                    Regex.Replace(dataType.Replace(variable.[6], ""), @" \*$", "")
                                    :: functionInstance.DataTypeList

                    functionInstance.FuncId <- funcId
                    functionInstanceList.Add(functionInstance)
                with _ -> ()
            | None -> ()

            // Return result from first function or empty lists
            match List.ofSeq functionInstanceList with
            | [] -> ([], [], [])
            | func::_ ->
                let parameters = func.ParameterList |> List.distinct
                let dtypes = func.DataTypeList |> List.distinct
                let vars = func.VariableList |> List.distinct
                (parameters, dtypes, vars)

    // Clang preprocessing functions
    let generateClangCommand (filePath: string) (rootPath: string) (outputFile: string) =
        $"clang -E -P -Ilib -Isrc -I{rootPath} {filePath} > {outputFile}"

    let processFilesInDirectory (rootPath: string) =
        let possibleExtensions = [".c"; ".cc"; ".cpp";]

        let rec processDirectory (dirPath: string) =
            for file in Directory.GetFiles(dirPath) do
                if List.exists (fun (ext: string) -> file.EndsWith(ext: string)) possibleExtensions then
                    printfn "[*] Processing Source file: %s" file
                    let outputFile = Path.ChangeExtension(file, ".i")
                    if File.Exists(outputFile) then
                        printfn "[*] Source file already preprocessed: %s" file
                    else
                        printfn "[*] Source file Preprocessing start: %s" file
                        let clangCommand = generateClangCommand file rootPath outputFile

                        try
                            let psi = ProcessStartInfo("sh", $"-c \"{clangCommand}\"")
                            psi.UseShellExecute <- false
                            psi.RedirectStandardOutput <- true
                            psi.RedirectStandardError <- true
                            use p = Process.Start(psi)
                            p.WaitForExit()

                            if p.ExitCode <> 0 then
                                printfn "[!] Clang preprocessing failed for: %s" file
                        with ex ->
                            printfn "[!] Error processing file %s: %s" file ex.Message

            for dir in Directory.GetDirectories(dirPath) do
                processDirectory dir

        processDirectory rootPath

