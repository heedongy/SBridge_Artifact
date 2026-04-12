namespace SBridge

module CBMaker =
    open System
    open System.IO
    open System.Text.RegularExpressions
    open System.Text.Json
    open System.Text.Json.Serialization
    open System.Numerics
    open SBridge.FunctionLists
    open SBridge.Types
    open SBridge.JsonConverters
    open SBridge.Utils

    // Node parsing
    let parseNodes (data: string) =
        let nodePattern = "\"(\\d+)\"\\s*\\[label\\s*=\\s*<(.+?)>\\s*\\]"
        let regex = Regex(nodePattern, RegexOptions.Singleline)
        regex.Matches(data)
        |> Seq.cast<Match>
        |> Seq.map (fun m ->
            let nodeId = m.Groups.[1].Value
            let wsRegex = Regex(@"\s+")
            let label = wsRegex.Replace(m.Groups.[2].Value, " ").Trim()
            (nodeId, label)
        )
        |> List.ofSeq

    // Relationships parsing
    let parseRelationships (data: string) (pattern: string) =
        let regex = Regex(pattern)
        regex.Matches(data)
        |> Seq.cast<Match>
        |> Seq.fold (fun (acc: Map<string, string list>) m ->
            let sourceNode = m.Groups.[1].Value
            let targetNode = m.Groups.[2].Value
            let updatedList = (Map.tryFind sourceNode acc |> Option.defaultValue []) @ [targetNode]
            Map.add sourceNode updatedList acc
        ) Map.empty

    let removePrefix (fName: string) =
        let prefix = functionPrefixList |> List.tryFind (fun p -> fName.StartsWith(p))
        match prefix with
        | Some p -> fName.Substring(p.Length)
        | None -> fName

    let findSingleAssignmentLinesAndVars (vars: string list) (cpgData: string) : (string list * int list) =
        let nodes = parseNodes cpgData
        let ddgLabelRegex = Regex(ddgLabelPattern)
        let astAssignmentRegex = Regex(@"<\(.*<operator>\.assignment,(\w+)\s*=")

        let ddgAssignments =
            ddgLabelRegex.Matches(cpgData)
            |> Seq.cast<Match>
            |> Seq.choose (fun m ->
                let label = m.Groups.[3].Value.Trim()
                if not (label.Contains("RETURN")) && not (label.Contains("<RET>")) && label.Contains(" = ") then
                    let matchedVar = vars |> List.tryFind (fun v -> label.Contains(v + " ="))
                    match matchedVar with
                    | Some var ->
                        let nodeId = m.Groups.[1].Value
                        nodes |> List.tryFind (fun (id, nodeLabel) -> id = nodeId)
                              |> Option.bind (fun (_, nodeLabel) ->
                                  let linePattern = @"<SUB>(\d+)</SUB>"
                                  let lineMatch = Regex.Match(nodeLabel, linePattern)
                                  if lineMatch.Success then
                                      Some(var, int lineMatch.Groups.[1].Value)
                                  else None)
                    | None -> None
                else None)
            |> Seq.toList

        let astAssignments =
            nodes
            |> List.choose (fun (nodeId, label) ->
                if label.Contains("<operator>.assignment") then
                    let assignPattern = @"(\w+)\s*="
                    let assignMatch = Regex.Match(label, assignPattern)
                    if assignMatch.Success then
                        let varName = assignMatch.Groups.[1].Value
                        if List.contains varName vars then
                            let linePattern = @"<SUB>(\d+)</SUB>"
                            let lineMatch = Regex.Match(label, linePattern)
                            if lineMatch.Success then
                                Some(varName, int lineMatch.Groups.[1].Value)
                            else None
                        else None
                    else None
                else None)

        let allAssignments = ddgAssignments @ astAssignments |> List.distinctBy snd

        let varAssignCounts =
            allAssignments
            |> List.groupBy fst
            |> List.map (fun (var, assignments) -> (var, List.length assignments))

        let singleAssignVars =
            varAssignCounts
            |> List.filter (fun (_, count) -> count = 1)
            |> List.map fst

        let singleAssignLines =
            allAssignments
            |> List.filter (fun (var, _) -> List.contains var singleAssignVars)
            |> List.map snd
            |> List.distinct

        (singleAssignVars, singleAssignLines)

    let isPointerOperator (nodeLabel: string) =
        nodeLabel.Contains("<operator>.indirectFieldAccess") ||
        nodeLabel.Contains("<operator>.fieldAccess") ||
        nodeLabel.Contains("<operator>.indirectIndexAccess")

    let isAssignmentOperator (nodeLabel: string) =
        nodeLabel.Contains("<operators>.assignmentPlus") ||
        nodeLabel.Contains("<operators>.assignmentMinus") ||
        nodeLabel.Contains("<operators>.assignmentMultiplication") ||
        nodeLabel.Contains("<operators>.assignmentDivision") ||
        nodeLabel.Contains("<operators>.assignmentModulo") ||
        nodeLabel.Contains("<operators>.assignmentArithmeticShiftLeft") ||
        nodeLabel.Contains("<operators>.assignmentArithmeticShiftRight") ||
        nodeLabel.Contains("<operators>.assignmentAnd") ||
        nodeLabel.Contains("<operators>.assignmentOr")

    // Collect operator nodes
    let rec collectOperatorNodes (nodeId: string) (nodes: (string * string) list) (astRelations: Map<string, string list>) : string list =
        let nodeLabel = nodes |> List.find (fun (id, _) -> id = nodeId) |> snd
        match Map.tryFind nodeId astRelations with
        | Some children ->
            let currentNode = [nodeLabel]
            let childrenNodes =
                children
                |> List.collect (fun childId ->
                    let childLabel = nodes |> List.find (fun (id, _) -> id = childId) |> snd
                    if childLabel.Contains("<operator>.bracketedPrimary") then
                        let children = Map.find childId astRelations
                        let childrenNodes =
                            children
                            |> List.map (fun id -> (nodes |> List.find (fun (nid, _) -> nid = id) |> snd))
                            |> List.collect (fun node ->
                                if node.Contains("expressionList") then
                                    let childId = children |> List.find (fun cid ->
                                        let (_, label) = (nodes |> List.find (fun (nid, _) -> nid = cid))
                                        label = node)
                                    match Map.tryFind childId astRelations with
                                    | Some explist ->
                                        explist |> List.collect (fun id ->
                                            let childLabel = (nodes |> List.find (fun (nid, _) -> nid = id) |> snd)
                                            if not (childLabel.Contains("<operator>.assignment")) then
                                                collectOperatorNodes id nodes astRelations
                                            else
                                                []
                                        )
                                    | None -> []
                                else
                                    []
                            )
                        childrenNodes

                    elif childLabel.Contains("<operator>.minus") then
                        let children = Map.find childId astRelations
                        children
                        |> List.map (fun id -> (nodes |> List.find (fun (nid, _) -> nid = id) |> snd))
                        |> List.collect (fun node ->
                            let pattern = @"\(LITERAL,([^,]+),"
                            let m = Regex.Match(node, pattern)
                            if m.Success then
                                let value = m.Groups.[1].Value
                                let negValue = "-" + value.Trim()
                                [node.Replace(value, negValue)]
                            else
                                [node])
                    elif childLabel.Contains("<operator>.pointerCall") then
                        match Map.tryFind childId astRelations with
                        | Some (firstChild :: _) ->
                            [childLabel]
                        | _ -> [nodeLabel]

                    elif childLabel.Contains("<operator>.conditional") then
                        match Map.tryFind childId astRelations with
                        | Some children ->
                            match children with
                            | conditionChild :: trueBranch :: falseBranch :: _ ->
                                let conditionLabel = nodes |> List.find (fun (nid, _) -> nid = conditionChild) |> snd
                                if Regex.IsMatch(conditionLabel, @"!!\s*sizeof\s*\(struct\s*{[^}]*_Static_assert") then
                                    collectOperatorNodes trueBranch nodes astRelations
                                else
                                    let trueBranchNodes = collectOperatorNodes trueBranch nodes astRelations
                                    let falseBranchNodes = collectOperatorNodes falseBranch nodes astRelations
                                    if trueBranchNodes = falseBranchNodes then
                                        trueBranchNodes
                                    else
                                        trueBranchNodes @ falseBranchNodes |> List.distinct
                            | firstChild :: _ ->
                                collectOperatorNodes firstChild nodes astRelations
                            | [] -> [nodeLabel]
                        | _ -> [nodeLabel]

                    elif childLabel.Contains("<operator>") && not (childLabel.Contains("<operator>.sizeOf")) && not (childLabel.Contains("<operator>.addressOf")) then
                        collectOperatorNodes childId nodes astRelations

                    else
                        if isPointerOperator nodeLabel then
                            let pattern = @"\((IDENTIFIER|LITERAL|FIELD_IDENTIFIER),([^,]+)"
                            let m = Regex.Match(childLabel, pattern)
                            if m.Success then
                                let restOfString = childLabel.Substring(m.Index + m.Groups.[1].Length + 1)
                                ["(POINTERVAR" + restOfString]
                            else
                                [childLabel]
                        else
                            [childLabel]
                )
            (currentNode @ childrenNodes)
            |> List.distinct
            |> List.sortBy (fun label ->
                let subPattern = "<SUB>(\\d+)</SUB>"
                let m = Regex.Match(label, subPattern)
                if m.Success then int m.Groups.[1].Value else Int32.MaxValue
            )
        | None -> [nodeLabel]

    // Collect true block nodes - returns both content and line numbers
    let rec collectBlockNodes (nodeId: string)
                             (nodes: (string * string) list)
                             (astRelations: Map<string, string list>)
                             (absCodeList: (int * string) list)
                             : string list * Set<int> =

        let getLineNumber (label: string) : int option =
            let subPattern = "<SUB>(\\d+)</SUB>"
            let m = Regex.Match(label, subPattern)
            if m.Success then Some(int m.Groups.[1].Value) else None

        let currentLabel = nodes |> List.find (fun (id, _) -> id = nodeId) |> snd

        let mutable usedLines = Set.empty<int>
        let mutable collectedLines = Set.empty<int>

        let processLabel label =
            match getLineNumber label with
            | Some lineNum when not (Set.contains lineNum usedLines) ->
                usedLines <- Set.add lineNum usedLines
                collectedLines <- Set.add lineNum collectedLines
                absCodeList |> List.tryFind (fun (idx, _) -> idx = lineNum)
                          |> Option.map (fun (_, code) -> code.Trim())
                          |> Option.toList
            | Some _ -> []  // Skip already loaded lines
            | None -> [label]

        match Map.tryFind nodeId astRelations with
        | Some children ->
            let controlStructurePatterns = [
                "CONTROL_STRUCTURE,FOR"
                "CONTROL_STRUCTURE,WHILE"
                "CONTROL_STRUCTURE,DO"
                "CONTROL_STRUCTURE,IF"
                "CONTROL_STRUCTURE,ELSE"
                "CONTROL_STRUCTURE,SWITCH"
                "JUMP_TARGET,case"
            ]

            let isControlStructure (label: string) =
                controlStructurePatterns |> List.exists (fun (pattern: string) -> label.Contains(pattern: string))
            let content =
                children
                |> List.collect (fun childId ->
                    let childLabel = nodes |> List.find (fun (id, _) -> id = childId) |> snd
                    if childLabel.Contains("<operator>") && not (isControlStructure childLabel) then
                        collectOperatorNodes childId nodes astRelations
                        |> List.collect processLabel
                    elif not (isControlStructure childLabel) then
                        [childLabel] |> List.collect processLabel
                    else []
                )
                |> List.sortBy (fun label ->
                    let subPattern = "<SUB>(\\d+)</SUB>"
                    let m = Regex.Match(label, subPattern)
                    if m.Success then int m.Groups.[1].Value else Int32.MaxValue
                )
            (content, collectedLines)
        | None -> ([], Set.empty)



    // Control structure kind parsing
    let parseControlStructureKind (label: string) : OrgTypeKind option =
        let m = Regex.Match(label, structurePattern)
        let literalM = Regex.Match(label, literalPattern)
        let assignmentM = Regex.Match(label, assignmentPattern)
        let returnM = Regex.Match(label, returnPattern)
        let functionM = Regex.Match(label, functionPattern)
        let pointerCallM = Regex.Match(label, pointerCallPattern)
        let switchM = Regex.Match(label, switchPattern)
        let switchDefaultM = Regex.Match(label, switchDefaultPattern)

        if switchM.Success then
            Some(Switch("case", int switchM.Groups.[1].Value))
        elif switchDefaultM.Success then
            Some(Switch("default", int switchDefaultM.Groups.[1].Value))
        elif m.Success then
            let structureType = m.Groups.[1].Value
            let content = m.Groups.[2].Value
            let line = int m.Groups.[3].Value
            match structureType with
            | "IF" -> Some(If(content, line))
            | "WHILE" -> Some(While(content, line))
            | "DO" -> Some(DoWhile(content, line))
            | "FOR" -> Some(For(content, line))
            | "ELSE" -> Some(Else(content, line))
            | _ -> None
        elif literalM.Success then
            Some(Literal(literalM.Groups.[1].Value, int literalM.Groups.[2].Value))
        elif assignmentM.Success then
            Some(Assignment(assignmentM.Groups.[1].Value, int assignmentM.Groups.[2].Value))
        elif returnM.Success then
            Some(Return(returnM.Groups.[1].Value, int returnM.Groups.[2].Value))
        elif functionM.Success && functionM.Groups.[1].Value <> "CONTROL_STRUCTURE" then
            Some(FuncCall(functionM.Groups.[1].Value, int functionM.Groups.[3].Value))
        elif pointerCallM.Success then
            Some(PointerCall(pointerCallM.Groups.[1].Value, int pointerCallM.Groups.[2].Value))
        else
            None

    // Condition operator check
    let isConditionOperator (label: string) =
        Regex.IsMatch(label, conditionOperatorPattern)

    // Parent node finding
    let rec findParentNode (targetNodeId: string) (nodes: (string * string) list) (astRelations: Map<string, string list>) =
        astRelations
        |> Map.tryFindKey (fun _ children -> List.contains targetNodeId children)

    // Grandparent node finding
    let rec findGrandParentNode (targetNodeId: string) (nodes: (string * string) list) (astRelations: Map<string, string list>) =
        match findParentNode targetNodeId nodes astRelations with
        | Some parentId -> findParentNode parentId nodes astRelations
        | None -> None


    // Libc normalization
    let getNormalizedLibcName (funcName: string) : string =
        libcNormalization
        |> Map.tryPick (fun normalizedName functions ->
            if List.contains funcName functions then
                Some normalizedName
            else
                None)
        |> Option.defaultValue funcName

        // Condition conversion - Enhanced to extract strings from original source
    let condConvert (condList: string list)
                   (parameters: string list)
                   (dtypes: string list)
                   (vars: string list)
                   (absCodeList: (int * string) list) =
        let parsedList = ResizeArray<string>()
        let pattern = @"\((.*?),([^,]*?)(?:,|\))"

        for cond in condList do
            let matchResult = Regex.Match(cond, pattern)
            if matchResult.Success then
                let mutable firstElem = matchResult.Groups.[1].Value
                let mutable secondElem = matchResult.Groups.[2].Value

                let skipType =
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "cast") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "indirection") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "bracketedPrimary") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "expressionList") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "addressOf") ||
                    firstElem.Contains("CONTROL_STRUCTURE") ||
                    firstElem.Contains("JUMP_TARGET") ||
                    firstElem.Contains("RETURN") ||
                    firstElem.Contains("UNKNOWN") ||
                    firstElem.Contains("LOCAL")


                if not skipType then
                // Handle LITERAL with source extraction and normalization
                    if firstElem = "LITERAL" then
                        let cleanedString = secondElem.Trim('"').Trim()

                        // Check if it's a single character literal (e.g., 'a', 'b')
                        if secondElem.StartsWith("'") && secondElem.EndsWith("'") && secondElem.Length >= 3 then
                            let charValue = secondElem.[1..secondElem.Length - 2]
                            let asciiValue = Utils.getAsciiValue charValue
                            firstElem <- "NUM"
                            secondElem <- asciiValue.ToString()
                        elif not (String.IsNullOrWhiteSpace(cleanedString)) then
                            let pathPattern = "(?:[a-zA-Z]:[\\/\\\\]|\\/)(?:[^\\/\\\\\\n\\r]+[\\/\\\\])*[^\\/\\\\\\n\\r]*"
                            if Regex.IsMatch(cleanedString, pathPattern) then
                                secondElem <- "PATH"
                            else
                                secondElem <- normalizeString cleanedString
                        else
                            let linePattern = @"<SUB>(\d+)</SUB>"
                            let lineMatch = Regex.Match(cond, linePattern)
                            if lineMatch.Success then
                                let lineNum = int lineMatch.Groups.[1].Value
                                let getString =
                                    absCodeList
                                    |> List.tryFind (fun (ln, _) -> ln = lineNum)
                                    |> Option.bind (fun (_, code) ->
                                        let stringPattern = @"""([^""]*)"""
                                        let stringMatch = Regex.Match(code, stringPattern)
                                        if stringMatch.Success then
                                            Some stringMatch.Groups.[1].Value
                                        else
                                            None
                                    )
                                    |> Option.defaultValue ""

                                if not (String.IsNullOrWhiteSpace(getString)) && Regex.IsMatch(getString, pathPattern) then
                                    secondElem <- "PATH"
                                else
                                    secondElem <- normalizeString getString

                    if firstElem.StartsWith("<operator>.pointerCall") then
                        firstElem <- "FUNCTION"
                        let funcMatch = Regex.Match(cond, @"pointerCall,([^(]+)")
                        if funcMatch.Success then
                            secondElem <- funcMatch.Groups.[1].Value.Trim()

                    if firstElem.StartsWith("<operator>") || isAssignmentOperator firstElem then
                        let operatorParts = firstElem.Split('.')
                        if operatorParts.Length > 1 then
                            firstElem <- "OPERATOR"
                            secondElem <- operatorParts.[1]
                            if secondElem = "sizeOf" then
                                firstElem <- "NUMTYPE"
                                secondElem <- "NUMTYPE"

                    if libcFunctions |> List.contains firstElem then
                        secondElem <- getNormalizedLibcName firstElem
                        firstElem <- "LIBCFUNC"

                    let funcMatch = Regex.Match(cond, @"(\w+)\s*\((.*?)\)")
                    if funcMatch.Success then
                        let funcName = funcMatch.Groups.[1].Value
                        if firstElem = funcName then
                            if funcName.StartsWith("sub_") || funcName.StartsWith("FUN_") then
                                firstElem <- "FUNCTION_STRIPPED"
                            else
                                firstElem <- "FUNCTION"
                            secondElem <- removePrefix funcName

                    if firstElem.Contains("LOCAL") then
                        ()
                    elif firstElem = "IDENTIFIER" then
                        secondElem <-
                            if List.contains secondElem vars then
                                "LVAR"
                            elif List.contains secondElem parameters then
                                "PVAR"
                            elif secondElem.StartsWith("DAT_") then
                                "LVAR"
                            elif secondElem.StartsWith("PTR_DAT_") then
                                "LVAR"
                            else
                                secondElem
                    elif firstElem = "POINTERVAR" then
                        secondElem <-
                            if List.contains secondElem vars then
                                "LVAR"
                            elif List.contains secondElem parameters then
                                "PVAR"
                            else
                                secondElem
                    elif firstElem = "RETURN" then
                        let returnMatch = Regex.Match(cond, @"return\s*(?:\((.*?)\)|([^;]*))")
                        if returnMatch.Success then
                            secondElem <-
                                if not (String.IsNullOrEmpty(returnMatch.Groups.[1].Value)) then
                                    returnMatch.Groups.[1].Value
                                else
                                    returnMatch.Groups.[2].Value

                    // Handle numbers
                    try
                        let finalSecondElem =
                            let parseNumberWithType (s: string) =
                                let cleanStr = s.Trim()

                                let analyzeNumberType (numStr: string) =
                                    let lower = numStr.ToLower()
                                    if lower.EndsWith("ull") then ("unsigned long long", numStr.Substring(0, numStr.Length - 3))
                                    elif lower.EndsWith("ul") then ("unsigned long", numStr.Substring(0, numStr.Length - 2))
                                    elif lower.EndsWith("ll") then ("long long", numStr.Substring(0, numStr.Length - 2))
                                    elif lower.EndsWith("us") then ("unsigned short", numStr.Substring(0, numStr.Length - 2))
                                    elif lower.EndsWith("u") then ("unsigned", numStr.Substring(0, numStr.Length - 1))
                                    elif lower.EndsWith("l") then ("long", numStr.Substring(0, numStr.Length - 1))
                                    elif lower.EndsWith("s") then ("short", numStr.Substring(0, numStr.Length - 1))
                                    else ("int", numStr)

                                let (numberType, cleanNumber) = analyzeNumberType cleanStr
                                (numberType, cleanNumber)

                            let convertToInt64WithOverflow (value: BigInteger) : string =
                                if value > BigInteger(Int64.MaxValue) then
                                    Int64.MaxValue.ToString()
                                elif value < BigInteger(Int64.MinValue) then
                                    Int64.MinValue.ToString()
                                else
                                    (int64 value).ToString()

                            if secondElem.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
                                let (numType, hexValue) = parseNumberWithType(secondElem.Substring(2))
                                try
                                    let parsedValue = BigInteger.Parse(hexValue, System.Globalization.NumberStyles.HexNumber)
                                    convertToInt64WithOverflow parsedValue
                                with
                                | :? System.FormatException | :? OverflowException ->
                                    secondElem

                            elif secondElem.StartsWith("'") && secondElem.EndsWith("'") then
                                let charValue = secondElem.[1..secondElem.Length - 2]
                                let asciiValue = Utils.getAsciiValue charValue
                                asciiValue.ToString()
                            else
                                let (numType, cleanedNumber) = parseNumberWithType(secondElem)
                                match BigInteger.TryParse(cleanedNumber) with
                                | true, value -> convertToInt64WithOverflow value
                                | false, _ ->
                                    match Double.TryParse(cleanedNumber) with
                                    | true, value ->
                                        try
                                            let intValue = BigInteger(Math.Abs(value))
                                            let result = convertToInt64WithOverflow intValue
                                            if value < 0.0 then
                                                let negResult = -1L * (int64 (Math.Abs(value)))
                                                if negResult < Int64.MinValue then Int64.MinValue.ToString()
                                                else negResult.ToString()
                                            else result
                                        with
                                        | :? OverflowException ->
                                            if value >= 0.0 then Int64.MaxValue.ToString()
                                            else Int64.MinValue.ToString()
                                        | _ -> secondElem
                                    | false, _ -> secondElem

                        let finalFirstElem =
                            if Regex.IsMatch(finalSecondElem, @"^-?\d+$") then "NUM"
                            else firstElem

                        parsedList.Add(sprintf "(%s,%s)" finalFirstElem finalSecondElem)
                    with
                    | :? System.FormatException ->
                        parsedList.Add(sprintf "(%s,%s)" firstElem secondElem)

        parsedList |> List.ofSeq


    let getVariableAssignmentFromDDG (varName: string)
                                     (currentNodeId: string)
                                     (nodes: (string * string) list)
                                     (ddgRelations: Map<string, string list>)
                                     (astRelations: Map<string, string list>)
                                     (parameters: string list)
                                     (dtypes: string list)
                                     (vars: string list)
                                     (absCodeList: (int * string) list) : string list option =

        let findDataDependencies nodeId =
            ddgRelations
            |> Map.toList
            |> List.filter (fun (_, targets) -> List.contains nodeId targets)
            |> List.map fst

        let isVariableAssignment (nodeLabel: string) (targetVar: string) =
            let assignPattern = sprintf @"%s\s*=" targetVar
            Regex.IsMatch(nodeLabel, assignPattern) || nodeLabel.Contains(sprintf "IDENTIFIER,%s" targetVar)

        let rec findAssignmentValue nodeId depth =
            if depth > 5 then None
            else
                let dependencies = findDataDependencies nodeId

                dependencies
                |> List.tryPick (fun depNodeId ->
                    let nodeLabel = nodes |> List.tryFind (fun (id, _) -> id = depNodeId) |> Option.map snd |> Option.defaultValue ""

                    if isVariableAssignment nodeLabel varName then
                        match Map.tryFind depNodeId astRelations with
                        | Some children ->
                            let assignmentValueNodes =
                                children
                                |> List.skip 1
                                |> List.collect (fun childId ->
                                    if nodes |> List.exists (fun (id, _) -> id = childId) then
                                        let childLabel = nodes |> List.find (fun (id, _) -> id = childId) |> snd
                                        if childLabel.Contains("<operator>") then
                                            collectOperatorNodes childId nodes astRelations
                                        else
                                            [childLabel]
                                    else []
                                )

                            if List.isEmpty assignmentValueNodes then None
                            else Some (condConvert assignmentValueNodes parameters dtypes vars absCodeList)
                        | None -> None
                    else
                        findAssignmentValue depNodeId (depth + 1)
                )

        findAssignmentValue currentNodeId 0


    let condConvertWithDDG (condList: string list)
                           (parameters: string list)
                           (dtypes: string list)
                           (vars: string list)
                           (absCodeList: (int * string) list)
                           (currentNodeId: string)
                           (nodes: (string * string) list)
                           (ddgRelations: Map<string, string list>)
                           (astRelations: Map<string, string list>) =
        let parsedList = ResizeArray<string>()
        let pattern = @"\((.*?),([^,]*?)(?:,|\))"

        for cond in condList do
            let matchResult = Regex.Match(cond, pattern)
            if matchResult.Success then
                let mutable firstElem = matchResult.Groups.[1].Value
                let mutable secondElem = matchResult.Groups.[2].Value

                let skipType =
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "cast") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "indirection") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "bracketedPrimary") ||
                    (firstElem.StartsWith("<operator>") && firstElem.Split('.').[1] = "expressionList") ||
                    firstElem.Contains("CONTROL_STRUCTURE") ||
                    firstElem.Contains("JUMP_TARGET") ||
                    firstElem.Contains("RETURN") ||
                    firstElem.Contains("UNKNOWN") ||
                    firstElem.Contains("LOCAL")

                if not skipType then
                    if firstElem = "IDENTIFIER" then
                        match getVariableAssignmentFromDDG secondElem currentNodeId nodes ddgRelations astRelations parameters dtypes vars absCodeList with
                        | Some assignedValues when not (List.isEmpty assignedValues) ->
                            parsedList.AddRange(assignedValues)
                        | _ ->
                            secondElem <-
                                if List.contains secondElem vars then "LVAR"
                                elif List.contains secondElem parameters then "PVAR"
                                elif secondElem.StartsWith("DAT_") then "LVAR"
                                elif secondElem.StartsWith("PTR_DAT_") then "LVAR"
                                else secondElem
                            parsedList.Add(sprintf "(%s,%s)" firstElem secondElem)

                    elif firstElem = "POINTERVAR" then
                        match getVariableAssignmentFromDDG secondElem currentNodeId nodes ddgRelations astRelations parameters dtypes vars absCodeList with
                        | Some assignedValues when not (List.isEmpty assignedValues) ->
                            parsedList.AddRange(assignedValues)
                        | _ ->
                            secondElem <-
                                if List.contains secondElem vars then "LVAR"
                                elif List.contains secondElem parameters then "PVAR"
                                else secondElem
                            parsedList.Add(sprintf "(%s,%s)" firstElem secondElem)

                    else
                        let originalResult = condConvert [cond] parameters dtypes vars absCodeList
                        parsedList.AddRange(originalResult)

        parsedList |> List.ofSeq

    // Condition conversion with parameter grouping
    let condConvertWithParameterGrouping (condList: string list)
                                       (parameters: string list)
                                       (dtypes: string list)
                                       (vars: string list)
                                       (absCodeList: (int * string) list)
                                       (astChildNodes: string list)
                                       (nodes: (string * string) list)
                                       (astRelations: Map<string, string list>) =

        // For each child node (parameter), collect and convert its elements
        let parameterGroups =
            astChildNodes
            |> List.map (fun childNodeId ->
                // Get all nodes related to this parameter (including sub-expressions)
                let rec collectParameterNodes nodeId =
                    let nodeLabel = nodes |> List.tryFind (fun (nid, _) -> nid = nodeId) |> Option.map snd |> Option.defaultValue ""

                    // If this is a simple parameter (literal or identifier), return it
                    if nodeLabel.Contains("(LITERAL,") || nodeLabel.Contains("(IDENTIFIER,") then
                        [nodeLabel]
                    // If this is an operator (like d+e), collect all its children
                    elif nodeLabel.Contains("<operator>") then
                        match Map.tryFind nodeId astRelations with
                        | Some children ->
                            [nodeLabel] @ (children |> List.collect collectParameterNodes)
                        | None -> [nodeLabel]
                    else
                        [nodeLabel]

                let paramNodes = collectParameterNodes childNodeId
                let convertedNodes = condConvert paramNodes parameters dtypes vars absCodeList
                convertedNodes |> String.concat ";"
            )
            |> List.filter (fun group -> not (String.IsNullOrWhiteSpace(group)))

        parameterGroups

    // Extract condition nodes
    let getCondNodes (childNodes: string list)
                    (nodes: (string * string) list)
                    (astRelations: Map<string, string list>)
                    (varCondMap: Map<string, string list>) =
        let lvarPattern = "\(IDENTIFIER,([^,]+)"
        let nodes =
            childNodes
            |> List.choose (fun id ->
                let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                if not (label.Contains("(BLOCK,")) && not (label.Contains("CONTROL_STRUCTURE,ELSE")) then
                    if label.Contains("<operator>.bracketedPrimary") then
                        let children = Map.find id astRelations
                        let childrenNodes =
                            children
                            |> List.map (fun id -> (nodes |> List.find (fun (nid, _) -> nid = id) |> snd))
                            |> List.collect (fun node ->
                                if node.Contains("expressionList") then
                                    let childId = children |> List.find (fun cid ->
                                        let (_, label) = (nodes |> List.find (fun (nid, _) -> nid = cid))
                                        label = node)
                                    match Map.tryFind childId astRelations with
                                    | Some explist ->
                                        explist |> List.collect (fun id ->
                                            let childLabel = (nodes |> List.find (fun (nid, _) -> nid = id) |> snd)
                                            if not (childLabel.Contains("<operator>.assignment")) then
                                                collectOperatorNodes id nodes astRelations
                                            else
                                                []
                                        )
                                    | None -> []
                                else
                                    []
                            )
                        Some(childrenNodes)

                    elif label.Contains("<operator>.conditional") then
                        match Map.tryFind id astRelations with
                        | Some children ->
                            match children with
                            | conditionChild :: trueBranch :: falseBranch :: _ ->
                                let conditionLabel = nodes |> List.find (fun (nid, _) -> nid = conditionChild) |> snd
                                if Regex.IsMatch(conditionLabel, @"!!\s*sizeof\s*\(struct\s*{[^}]*_Static_assert") then
                                    Some(collectOperatorNodes trueBranch nodes astRelations)
                                else
                                    let trueBranchNodes = collectOperatorNodes trueBranch nodes astRelations
                                    let falseBranchNodes = collectOperatorNodes falseBranch nodes astRelations
                                    if trueBranchNodes = falseBranchNodes then
                                        Some(trueBranchNodes)
                                    else
                                        Some(trueBranchNodes @ falseBranchNodes |> List.distinct)
                            | firstChild :: _ ->
                                Some(collectOperatorNodes firstChild nodes astRelations)
                            | [] -> None
                        | _ -> None

                    elif label.Contains("<operator>.pointerCall") then
                        match Map.tryFind id astRelations with
                        | Some (firstChild :: _) ->
                            Some([label])
                        | _ -> None
                    elif label.Contains("<operator>.indirectFieldAccess") || label.Contains("<operator>.fieldAccess") || label.Contains("<operator>.indirectIndexAccess") then
                        Some(collectOperatorNodes id nodes astRelations)
                    elif label.Contains("<operator>") then
                        Some(collectOperatorNodes id nodes astRelations)
                    else
                        Some([label])
                else
                    None
            )
            |> List.concat

        // Replace variables
        let matchIdentifier str =
            let m = Regex.Match(str, lvarPattern)
            if m.Success then Some(m.Groups.[1].Value) else None

        nodes |> List.collect (fun node ->
            match matchIdentifier node with
            | Some varName when Map.containsKey varName varCondMap ->
                let replacedNodes = varCondMap.[varName]
                let hasOperator = replacedNodes |> List.exists (fun n ->
                    n.Contains("EQUALS") ||
                    n.Contains("NOT_EQUALS") ||
                    n.Contains("GREATER_THAN") ||
                    n.Contains("LESS_THAN") ||
                    n.Contains("GREATER_EQUALS") ||
                    n.Contains("LESS_EQUALS") ||
                    n.Contains("LOGICAL_AND") ||
                    n.Contains("LOGICAL_OR")
                )
                if hasOperator then
                    [node]
                else
                    replacedNodes
            | _ ->
                [node]
        )

        // Collect block nodes with condConvert - directly from AST relations to condNodes
    let collectBlockNodesWithCondConvert (nodeId: string)
                                       (nodes: (string * string) list)
                                       (astRelations: Map<string, string list>)
                                       (absCodeList: (int * string) list)
                                       (parameters: string list)
                                       (dtypes: string list)
                                       (vars: string list)
                                       : string list * Set<int> =

        match Map.tryFind nodeId astRelations with
        | Some children ->
            let allCondNodes =
                children
                |> List.collect (fun childId ->
                    getCondNodes [childId] nodes astRelations Map.empty
                )

            let condResult = condConvert allCondNodes parameters dtypes vars absCodeList

            (condResult, Set.empty<int>)
        | None -> ([], Set.empty)

    // Variable condition map for single assignments
    let mutable varCondMap = Map.empty<string, string list>

    // Control block creation
    let createControlBlock (nodeId: string)
                         (nodes: (string * string) list)
                         (astRelations: Map<string, string list>)
                         (ddgRelations: Map<string, string list>)
                         (absCodeList: (int * string) list)
                         (orgTypeKind: OrgTypeKind)
                         (inputFuncName: string)
                         (parameters: string list)
                         (dtypes: string list)
                         (vars: string list)
                         (singleAssignVars: string list) =

        let childNodes = Map.tryFind nodeId astRelations |> Option.defaultValue []
        let condNodes = getCondNodes childNodes nodes astRelations varCondMap

        let findAbsCode line = absCodeList |> List.tryFind (fun (idx, _) -> idx = line)
                                    |> Option.map snd
                                    |> Option.defaultValue ""


        let blockContent, _ =
            let filteredNodes =
                childNodes
                |> List.filter (fun id ->
                    let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                    label.Contains("(BLOCK,") && not (label.Contains("(BLOCK,<empty>,<empty>"))
                )

            if List.isEmpty filteredNodes then
                let results =
                    childNodes
                    |> List.filter (fun id ->
                        let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                        label.Contains("(BLOCK,")
                    )
                    |> List.map (fun id ->
                        collectBlockNodesWithCondConvert id nodes astRelations absCodeList parameters dtypes vars)
                let content = results |> List.collect fst
                let lines = results |> List.fold (fun acc (_, lines) -> Set.union acc lines) Set.empty
                (content, lines)
            else
                let results =
                    filteredNodes
                    |> List.map (fun id ->
                        collectBlockNodesWithCondConvert id nodes astRelations absCodeList parameters dtypes vars)
                let content = results |> List.collect fst
                let lines = results |> List.fold (fun acc (_, lines) -> Set.union acc lines) Set.empty
                (content, lines)

        match orgTypeKind with
        | If(content, line) -> // if node
            if blockContent = ["__stack_chk_fail();"] then
                None
            else
                // Get lines from condition nodes
                let conditionLines =
                    condNodes
                    |> List.choose (fun node ->
                        let subPattern = "<SUB>(\\d+)</SUB>"
                        let m = Regex.Match(node, subPattern)
                        if m.Success then Some(int m.Groups.[1].Value) else None)
                    |> Set.ofList
                Some {
                    block_type = ControlBlockType.Condition
                    org_type = OrgType.If
                    line = line
                    node_id = nodeId
                    block_content = blockContent
                    key_feature = condConvertWithDDG condNodes parameters dtypes vars absCodeList nodeId nodes ddgRelations astRelations
                }

        | Switch(content, line) -> // switch node
            let findLastSiblingNodes (nodeId: string) (astRelations: Map<string, string list>) : string list =
                match findParentNode nodeId [] astRelations with
                | Some parentId ->
                    match Map.tryFind parentId astRelations with
                    | Some siblings ->
                        siblings
                        |> List.filter (fun id -> id <> nodeId)
                        |> List.tryLast
                        |> Option.toList
                    | None -> []
                | None -> []

            let extractSwitchContent (switchTrueNodes: (string * string) list) =
                let nodeIds = switchTrueNodes |> List.map fst
                let allCondNodes =
                    nodeIds
                    |> List.collect (fun nodeId ->
                        getCondNodes [nodeId] nodes astRelations Map.empty
                    )

                let condResult = condConvert allCondNodes parameters dtypes vars absCodeList
                (condResult, Set.empty<int>)

            if content = "case" then
                // switch-case: condition node
                let switchCondNodes =
                    match findGrandParentNode nodeId nodes astRelations with
                    | Some grandParentId ->
                        let grandParentChildren = Map.find grandParentId astRelations
                        grandParentChildren
                        |> List.choose (fun id ->
                            let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                            if not (label.Contains("(BLOCK,")) then
                                if label.Contains("<operator>") then
                                    Some(collectOperatorNodes id nodes astRelations)
                                else
                                    Some([label])
                            else
                                None
                        )
                        |> List.concat
                    | None -> []

                let caseCondNodes, nextCaseCond =
                    match
                        nodes
                        |> List.skipWhile (fun (nid, _) -> nid <> nodeId)
                        |> List.tail
                        |> List.tryHead
                    with
                    | Some (nextNodeId, label) ->
                        let result =
                            if label.Contains("<operator>") then
                                collectOperatorNodes nextNodeId nodes astRelations
                            else
                                [label]

                        let finalNodeId =
                            match List.rev result with
                            | lastLabel :: _ ->
                                nodes
                                |> List.tryFind (fun (_, lbl) -> lbl = lastLabel)
                                |> Option.map fst
                                |> Option.defaultValue ""
                            | [] -> ""

                        result, finalNodeId
                    | None -> [], ""

                let switchTrueNodes =
                    let lastSiblingNodes = findLastSiblingNodes nextCaseCond astRelations
                    nodes
                    |> List.skipWhile (fun (nid, _) -> nid <> nextCaseCond)
                    |> List.tail
                    |> List.takeWhile (fun (nid, label) ->
                        not (label.Contains("JUMP_TARGET,case")) &&
                        not (label.Contains("CONTROL_STRUCTURE,BREAK")) &&
                        not (label.Contains("JUMP_TARGET,default")) &&
                        not (List.contains nid lastSiblingNodes))
                    |> fun takenNodes ->
                        match List.tryFind (fun (nid, _) -> List.contains nid lastSiblingNodes) nodes with
                        | Some lastNode -> takenNodes @ [lastNode]
                        | None -> takenNodes

                let extractedOrgCode, switchExtractedLines = extractSwitchContent switchTrueNodes
                let finalCond = ["(<operator>.equals,none)"] @ switchCondNodes @ caseCondNodes

                // Get condition node lines
                let conditionLines =
                    (switchCondNodes @ caseCondNodes)
                    |> List.choose (fun node ->
                        let subPattern = "<SUB>(\\d+)</SUB>"
                        let m = Regex.Match(node, subPattern)
                        if m.Success then Some(int m.Groups.[1].Value) else None)
                    |> Set.ofList

                Some {
                    block_type = ControlBlockType.Condition
                    org_type = OrgType.Switch
                    line = line
                    node_id = nodeId
                    block_content = extractedOrgCode
                    key_feature = condConvert finalCond parameters dtypes vars absCodeList
                }

            else
                // switch-default: Else node
                let defaultTrueNodes =
                    let lastSiblingNodes = findLastSiblingNodes nodeId astRelations
                    nodes
                    |> List.skipWhile (fun (nid, _) -> nid <> nodeId)
                    |> List.tail
                    |> List.takeWhile (fun (nid, label) ->
                        not (label.Contains("JUMP_TARGET,case")) &&
                        not (label.Contains("CONTROL_STRUCTURE,BREAK")) &&
                        not (List.contains nid lastSiblingNodes))
                    |> fun takenNodes ->
                        match List.tryFind (fun (nid, _) -> List.contains nid lastSiblingNodes) nodes with
                        | Some lastNode -> takenNodes @ [lastNode]
                        | None -> takenNodes

                let defaultTrueNodesContent, defaultExtractedLines = extractSwitchContent defaultTrueNodes
                Some {
                    block_type = ControlBlockType.Else
                    org_type = OrgType.Switch
                    line = line
                    node_id = nodeId
                    block_content = defaultTrueNodesContent
                    key_feature = []
                }

        | Else(content, line) ->
            let elseKeywordLine =
                absCodeList
                |> List.filter (fun (ln, code) ->
                    ln <= line && code.Trim() = "else")
                |> List.tryLast
                |> Option.map fst
                |> Option.defaultValue (line - 1)

            let nextNonEmpty =
                absCodeList
                |> List.filter (fun (ln, code) -> ln > line && not (String.IsNullOrWhiteSpace(code.Trim())))
                |> List.tryHead

            let blockLines =
                match nextNonEmpty with
                | Some (ln, code) when code.Trim() = "{" ->
                    let closingBrace =
                        absCodeList
                        |> List.filter (fun (lineNum, c) -> lineNum > ln && c.Trim() = "}")
                        |> List.tryHead
                        |> Option.map fst
                        |> Option.defaultValue ln
                    [line..closingBrace] |> Set.ofList
                | _ ->
                    Set.singleton line
            Some {
                block_type = ControlBlockType.Else
                org_type = OrgType.Else
                line = line
                node_id = nodeId
                block_content = blockContent
                key_feature = []
            }

        | While(content, line) -> // while node
            let isInfiniteLoop =
                match condNodes with
                | [str] when str.StartsWith("(LITERAL,1") -> ["(OPERATOR,InfiniteLoop)"]
                | [str] when str.StartsWith("(IDENTIFIER,true") -> ["(OPERATOR,InfiniteLoop)"]
                | _ -> condConvertWithDDG condNodes parameters dtypes vars absCodeList nodeId nodes ddgRelations astRelations

            Some {
                block_type = ControlBlockType.Loop
                org_type = OrgType.While
                line = line
                node_id = nodeId
                block_content = blockContent
                key_feature = isInfiniteLoop
            }

        | DoWhile(content, line) -> // do-while node
            let isInfiniteLoop =
                match condNodes with
                | [str] when str.StartsWith("(LITERAL,1") -> ["(OPERATOR,InfiniteLoop)"]
                | [str] when str.StartsWith("(IDENTIFIER,true") -> ["(OPERATOR,InfiniteLoop)"]
                | _ -> condConvert condNodes parameters dtypes vars absCodeList

            Some {
                block_type = ControlBlockType.Loop
                org_type = OrgType.DoWhile
                line = line
                node_id = nodeId
                block_content = blockContent
                key_feature = isInfiniteLoop
            }

        | For(content, line) -> // for node
            let parseForContent (content: string) =
                let forPattern = @"for\s*\((.*?)\s*;\s*(.*?)\s*;\s*(.*?)\)"
                let m = Regex.Match(content, forPattern)
                if m.Success then
                    let _ = m.Groups.[1].Value.Trim()
                    let cond = m.Groups.[2].Value.Trim()
                    let incr = m.Groups.[3].Value.Trim()
                    (cond, incr)
                else
                    ("", "")

            let (cond, incr) = parseForContent (findAbsCode line)

            let forCondNodes =
                if String.IsNullOrWhiteSpace(cond) then
                    ["(OPERATOR,InfiniteLoop)"]
                else
                    childNodes
                    |> List.tryItem 1
                    |> Option.map (fun id ->
                        let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                        if not (label.Contains("(BLOCK,")) && not (label.Contains("CONTROL_STRUCTURE,ELSE")) then
                            if label.Contains("<operator>") || isAssignmentOperator label then
                                collectOperatorNodes id nodes astRelations
                            else
                                [label]
                        else
                            []
                    )
                    |> Option.defaultValue []

            let forTrueNodes, forTrueLines =
                childNodes
                |> List.tryLast
                |> Option.map (fun id ->
                    collectBlockNodesWithCondConvert id nodes astRelations absCodeList parameters dtypes vars)
                |> Option.defaultValue ([], Set.empty)

            let incrCondNodes =
                if String.IsNullOrWhiteSpace(incr) then
                    []
                else
                    childNodes
                    |> List.tryItem 2
                    |> Option.map (fun id ->
                        let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                        if label.Contains("<operator>") || isAssignmentOperator label then
                            collectOperatorNodes id nodes astRelations
                        else
                            [label]
                    )
                    |> Option.defaultValue []

            let incrContent =
                if List.isEmpty incrCondNodes then
                    []
                else
                    condConvert incrCondNodes parameters dtypes vars absCodeList

            let blockContent = incrContent @ forTrueNodes
            let allForLines = forTrueLines
            let blockLines = Set.singleton line
            Some {
                block_type = ControlBlockType.Loop
                org_type = OrgType.For
                line = line
                node_id = nodeId
                block_content = blockContent
                key_feature = condConvert forCondNodes parameters dtypes vars absCodeList
            }

        | Literal(content, line) ->
            let actualContent =
                absCodeList
                |> List.tryFind (fun (lineNum, _) -> lineNum = line)
                |> Option.bind (fun (_, code) ->
                    let cleanContent = content.Trim('"').Trim()
                    let escapedContent = Regex.Escape(cleanContent)
                    let specificPattern = $"\"({escapedContent})\""
                    let specificMatch = Regex.Match(code, specificPattern)
                    if specificMatch.Success then
                        Some specificMatch.Groups.[1].Value
                    else
                        let stringPattern = @"""([^""]*)"""
                        let matches = Regex.Matches(code, stringPattern)
                        matches
                        |> Seq.cast<Match>
                        |> Seq.map (fun m -> m.Groups.[1].Value)
                        |> Seq.tryFind (fun s -> s = cleanContent)
                )
                |> Option.defaultValue (content.Trim('"'))
                |> fun s -> s.Trim()

            if String.IsNullOrWhiteSpace(actualContent) then
                None
            else
                let isPath = Regex.IsMatch(actualContent, pathPattern)
                if isPath then
                    None
                else
                    let normalizedContent = normalizeString actualContent
                    Some {
                        block_type = ControlBlockType.String
                        org_type = OrgType.String
                        line = line
                        node_id = nodeId
                        block_content = []
                        key_feature = [normalizedContent]
                    }

        | Assignment(content, line) ->
            match condNodes with
            | firstNode :: rest ->
                let lvarPattern = "\(IDENTIFIER,([^,]+)"
                let varMatch = Regex.Match(firstNode, lvarPattern)
                if varMatch.Success then
                    let extractedVar = varMatch.Groups.[1].Value.Trim()
                    if List.contains extractedVar singleAssignVars then
                        varCondMap <- Map.add extractedVar rest varCondMap
            | _ ->  ()

            let hasCondOperator =
                childNodes
                |> List.exists (fun id ->
                    let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                    isConditionOperator label)

            if hasCondOperator then
                let asChildNodes = childNodes |> List.tail
                let asCondNodes = getCondNodes asChildNodes nodes astRelations varCondMap
                Some {
                    block_type = ControlBlockType.Condition
                    org_type = OrgType.Assignment
                    line = line
                    node_id = nodeId
                    block_content = []
                    key_feature = condConvertWithDDG asCondNodes parameters dtypes vars absCodeList nodeId nodes ddgRelations astRelations
                }
            else None

        | Return(content, line) -> // return node with a condition
            let hasCondOperator =
                childNodes
                |> List.exists (fun id ->
                    let label = nodes |> List.find (fun (nid, _) -> nid = id) |> snd
                    isConditionOperator label)

            if hasCondOperator then
                Some {
                    block_type = ControlBlockType.Condition
                    org_type = OrgType.Return
                    line = line
                    node_id = nodeId
                    block_content = []
                    key_feature = condConvertWithDDG condNodes parameters dtypes vars absCodeList nodeId nodes ddgRelations astRelations
                }
            else None

        | FuncCall(content, line) ->
            if excludedSymbols |> List.exists (fun x -> content.StartsWith(x)) then
                None
            else
                let funcName =
                    if content.StartsWith("__") && content.EndsWith("_chk") then
                        content.Substring(2, content.Length - 6)
                    else
                        content

                let blockType =
                    if funcName = inputFuncName then ControlBlockType.RecursiveFunc
                    elif List.contains funcName libcFunctions then ControlBlockType.LibcFunc
                    else ControlBlockType.CalleeFunc

                let normalizedFuncName =
                    if List.contains funcName libcFunctions then
                        getNormalizedLibcName funcName
                    else
                        funcName

                let parameterNodes =
                    match Map.tryFind nodeId astRelations with
                    | Some children ->
                        children
                        |> List.choose (fun childId ->
                            nodes |> List.tryFind (fun (nid, _) -> nid = childId)
                                 |> Option.map snd)
                        |> List.filter (fun node ->
                            node.Contains("(LITERAL,") ||
                            node.Contains("(IDENTIFIER,"))
                    | None -> []

                let allParameterNodes = (condNodes @ parameterNodes) |> List.distinct

                let blockContent = condConvertWithParameterGrouping allParameterNodes parameters dtypes vars absCodeList childNodes nodes astRelations

                let argLines =
                    allParameterNodes
                    |> List.choose (fun nodeLabel ->
                        let subPattern = "<SUB>(\\d+)</SUB>"
                        let m = Regex.Match(nodeLabel, subPattern)
                        if m.Success then Some(int m.Groups.[1].Value) else None
                    )
                    |> Set.ofList
                Some {
                    block_type = blockType
                    org_type = OrgType.FunctionCall
                    line = line
                    node_id = nodeId
                    block_content = blockContent
                    key_feature = [sprintf "%s,%s" normalizedFuncName (string childNodes.Length)]
                }

        // Handle pointer functions
        | PointerCall(content, line) ->
            let paramNodes = childNodes |> List.tail
            let paramCondNodes = getCondNodes paramNodes nodes astRelations varCondMap
            let pointCallName =  content.Split('(').[0]

            if String.IsNullOrWhiteSpace(pointCallName) then
                None
            else
                let argLines =
                    paramCondNodes
                    |> List.choose (fun nodeLabel ->
                        let subPattern = "<SUB>(\\d+)</SUB>"
                        let m = Regex.Match(nodeLabel, subPattern)
                        if m.Success then Some(int m.Groups.[1].Value) else None
                    )
                    |> Set.ofList
                Some {
                    block_type = ControlBlockType.CalleeFunc
                    org_type = OrgType.PointerCall
                    line = line
                    node_id = nodeId
                    block_content = condConvertWithParameterGrouping paramCondNodes parameters dtypes vars absCodeList childNodes nodes astRelations
                    key_feature = [sprintf "%s,%s" pointCallName (string childNodes.Length)]
                }

    // Extract function calls
    let extractFunctionCalls inputFuncName (controlBlocks: ControlBlock list) : (string * string list) =
        let callerName = inputFuncName

        let callees =
            controlBlocks
            |> List.filter (fun block ->
                block.block_type = ControlBlockType.CalleeFunc &&
                block.org_type = OrgType.FunctionCall)
            |> List.map (fun block ->
                block.key_feature.[0].Split(',').[0]
            )

        (callerName, callees)

    let generateControlBlock (inputFuncName: string)
                        (dotcpgOutput: string)
                        (absCodeList: (int * string) list)
                        (outputPath: string)
                        (parameters: string list)
                        (dtypes: string list)
                        (vars: string list) : (string * string list) =
        varCondMap <- Map.empty
        try
            let nodes = parseNodes dotcpgOutput
            let astRelations = parseRelationships dotcpgOutput astPattern
            let ddgRelations = parseRelationships dotcpgOutput ddgPattern
            let cdgRelations = parseRelationships dotcpgOutput cdgPattern
            let (singleAssignVars, singleAssignLines) = findSingleAssignmentLinesAndVars vars dotcpgOutput

            let mutable controlBlocks = []
            let mutable processedNodes = Set.empty<string>

            for (nodeId, label) in nodes do
                if label.Contains("<operator>.conditional") then
                    match Map.tryFind nodeId astRelations with
                    | Some children ->
                        match children with
                        | conditionChild :: trueBranch :: falseBranch :: _ ->
                            // Remove unnecessary condition nodes
                            let getDDGLabelsForNode nodeId =
                                let ddgPattern = sprintf @"""%s""\s*->\s*""[^""]+""\s*\[\s*label\s*=\s*""DDG:\s*([^""]+)""" nodeId
                                let matches = Regex.Matches(dotcpgOutput, ddgPattern)
                                matches
                                |> Seq.cast<Match>
                                |> Seq.map (fun m -> m.Groups.[1].Value.Trim())
                                |> Set.ofSeq

                            let trueBranchLabels = getDDGLabelsForNode trueBranch
                            let falseBranchLabels = getDDGLabelsForNode falseBranch

                            let hasIdenticalBranches =
                                not (Set.isEmpty trueBranchLabels) &&
                                not (Set.isEmpty falseBranchLabels) &&
                                trueBranchLabels = falseBranchLabels

                            if hasIdenticalBranches then
                                let rec markDescendantsAsProcessed nodeId =
                                    processedNodes <- Set.add nodeId processedNodes
                                    match Map.tryFind nodeId astRelations with
                                    | Some children -> children |> List.iter markDescendantsAsProcessed
                                    | None -> ()
                                markDescendantsAsProcessed falseBranch
                        | _ -> ()
                    | None -> ()

            for (nodeId, label) in nodes do
                if not (Set.contains nodeId processedNodes) then
                    match parseControlStructureKind label with
                    | Some structureKind ->
                        match createControlBlock nodeId nodes astRelations ddgRelations absCodeList structureKind inputFuncName parameters dtypes vars singleAssignVars with
                        | Some stmt ->
                            controlBlocks <- stmt :: controlBlocks
                        | None -> ()
                    | None -> ()

            controlBlocks <- controlBlocks |> List.sortBy (fun stmt -> stmt.line)
            let functionCalls = extractFunctionCalls inputFuncName controlBlocks
            let options = JsonSerializerOptions(
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            )
            options.Converters.Add(new OrgTypeConverter())
            options.Converters.Add(new ControlBlockTypeConverter())
            let json = JsonSerializer.Serialize(controlBlocks, options)

            File.WriteAllText(outputPath, json)

            functionCalls

        with
        | ex ->
            printfn "[!] Error for condition block generation: %s" ex.Message
            ("", [])

    let findSiblingNodes (nodeId: string) (astRelations: Map<string, string list>) : string list =
        match findParentNode nodeId [] astRelations with
        | Some parentId ->
            match Map.tryFind parentId astRelations with
            | Some siblings ->
                siblings
                |> List.filter (fun id -> id <> nodeId)
                |> List.tryLast
                |> Option.toList
            | None -> []
        | None -> []
