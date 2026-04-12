namespace SBridge

module Main =
    open SBridge.SourceParser
    open SBridge.BinaryParser
    open SBridge.FunctionMatcher
    open SBridge.FCGMaker
    open System
    open SBridge.Types

    type ParseMode =
        | Source
        | Binary
        | Auto
        | Detect
        | FunctionMatch
        | CPG
        | FCG

    let parseArgs argv =
        let rec parseRec (args: string list) (path: Option<string>) (mode: ParseMode) (ida: bool) (clean: bool) =
            match args with
            | [] when path.IsNone -> failwith "Error: Path is required."
            | [] ->
                let normalizedPath =
                    if path.Value.EndsWith("/") || path.Value.EndsWith("\\") then
                        path.Value.TrimEnd('/', '\\')
                    else
                        path.Value
                (normalizedPath, mode, ida, clean)
            | "-s" :: xs | "--source" :: xs -> parseRec xs path Source ida clean
            | "-b" :: xs | "--binary" :: xs -> parseRec xs path Binary ida clean
            | "-a" :: xs | "--auto" :: xs -> parseRec xs path Auto ida clean
            | "-c" :: xs | "--cpg" :: xs -> parseRec xs path CPG ida clean
            | "-d" :: xs | "--detect" :: xs -> parseRec xs path Detect ida clean
            | "-m" :: xs | "--match" :: xs -> parseRec xs path FunctionMatch ida clean
            | "-g" :: xs | "--graph" :: xs -> parseRec xs path FCG ida clean
            | "--ida" :: xs -> parseRec xs path mode true clean
            | "--clean" :: xs -> parseRec xs path mode ida true
            | x :: xs when path.IsNone -> parseRec xs (Some x) mode ida clean
            | x :: _ -> failwithf "Error: Unexpected argument '%s'" x

        let argsList = Array.toList argv
        parseRec argsList None Auto false false

    [<EntryPoint>]
    let main argv =
        try
            let (testPath, mode, ida, clean) = parseArgs argv

            match mode with
            | CPG ->
                srcCPGGeneration testPath clean
                binCPGGeneration testPath ida clean
                0
            | Source ->
                srcCPGGeneration testPath clean
                srcControlBlockGeneration testPath
                fcgMakerSrc testPath
                0
            | Binary ->
                binCPGGeneration testPath ida clean
                binControlBlockGeneration testPath ida
                fcgMakerBin testPath
                0
            | Auto ->
                srcCPGGeneration testPath clean
                srcControlBlockGeneration testPath
                binCPGGeneration testPath ida clean
                binControlBlockGeneration testPath ida
                fcgMakerBin testPath
                fcgMakerSrc testPath
                0
            | Detect ->
                let result = VulDetector.detector testPath ida
                0
            | FunctionMatch ->
                functionMatch testPath
                0
            | FCG ->
                printfn "Generating Control Flow Graph for %s" testPath
                FCGMaker.fcgMakerBin testPath
                FCGMaker.fcgMakerSrc testPath
                0

        with
        | ex ->
            printfn "Error: %s" ex.Message
            printfn "Usage: program [options] <test_path>"
            printfn "Options:"
            printfn "  -s, --source    Source code file Parsing"
            printfn "  -b, --binary    Binary files Parsing"
            printfn "  -a, --auto      Source and Binary files Automatically parse (default)"
            printfn "  -d, --detect    Vulnerable binary detection mode"
            printfn "  -m, --match     Source to Binary function match mode"
            printfn "  -g, --graph     Generate Function Call Graph"
            printfn "  --clean      Clean directories before parsing"
            printfn "Example: program -s /path/to/test/directory"
            1