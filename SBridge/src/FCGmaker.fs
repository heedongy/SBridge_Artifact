namespace SBridge

open System
open System.Collections.Generic
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open SBridge.Types
open SBridge.JsonConverters

module FCGMaker =
    // Generate all call graph
    let generateCallGraphFromFile (filePath: string) =
        let functionName = Path.GetFileNameWithoutExtension(filePath)
        let controlBlocks = JsonConverters.loadJsonControlBlocks filePath

        let callees = ResizeArray<string>()
        for block in controlBlocks do
            if block.block_type = ControlBlockType.CalleeFunc then
                for trueBlockLine in block.key_feature do
                    let parts = trueBlockLine.Split(',')
                    if parts.Length >= 1 then
                        let callee = parts.[0].Trim()
                        if not (String.IsNullOrWhiteSpace(callee)) && not (callee.StartsWith("_")) then
                            callees.Add(callee)

        (functionName, callees)

    // Process source call graph
    let processSfuncFiles (inputPath: string, featurePath: string) =
        if not (Directory.Exists(featurePath)) then
            printfn "Directory not found: %s" featurePath
            Dictionary<string, ResizeArray<string>>()
        else
            let functionCallGraph = Dictionary<string, ResizeArray<string>>()
            let sfuncFiles = Directory.EnumerateFiles(featurePath, "*.sfunc", SearchOption.TopDirectoryOnly)

            for file in sfuncFiles do
                let (funcName, callees) = generateCallGraphFromFile file
                functionCallGraph.Add(funcName, callees)
                printfn "Processed: %s with callees: %A" funcName callees

            let finalCallGraph = Dictionary<string, ResizeArray<string>>()
            for KeyValue(caller, callees) in functionCallGraph do
                if callees.Count > 0 && not (String.IsNullOrWhiteSpace(caller)) then
                    finalCallGraph.Add(caller, callees)

            finalCallGraph

    // Process binary call graph
    let processBfuncFiles (inputPath: string, binaryPath: string) =
        let featurePath = Path.Combine(inputPath, "processed_target", "features", binaryPath)
        if not (Directory.Exists(featurePath)) then
            printfn "Directory not found: %s" featurePath
            Dictionary<string, ResizeArray<string>>()
        else
            let functionCallGraph = Dictionary<string, ResizeArray<string>>()
            let bfuncFiles = Directory.EnumerateFiles(featurePath, "*.bfunc", SearchOption.TopDirectoryOnly)

            for file in bfuncFiles do
                let (funcName, callees) = generateCallGraphFromFile file
                functionCallGraph.Add(funcName, callees)
                printfn "Processed: %s with callees: %A" funcName callees

            let finalCallGraph = Dictionary<string, ResizeArray<string>>()
            for KeyValue(caller, callees) in functionCallGraph do
                if callees.Count > 0 && not (String.IsNullOrWhiteSpace(caller)) then
                    finalCallGraph.Add(caller, callees)

            finalCallGraph

    // Save call graph to JSON
    let saveCallGraphToJson (callGraph: Dictionary<string, ResizeArray<string>>) (outputPath: string) =
        let options = JsonSerializerOptions(WriteIndented = true)
        let jsonOutput = JsonSerializer.Serialize(callGraph, options)
        let outputFile = Path.Combine(outputPath, "call_graph.json")
        File.WriteAllText(outputFile, jsonOutput)
        printfn "Call graph saved to: %s" outputFile

    // Binary call graph generation
    let fcgMakerBin (inputPath: string) =
        let targetPath = Path.Combine(inputPath, "target")
        Directory.GetFiles(targetPath)
        |> Array.iter (fun binPath ->
            let binaryName = Path.GetFileName(binPath)
            let callGraph = processBfuncFiles(inputPath, binaryName)
            let outputPath = Path.Combine(inputPath, "processed_target", "info", binaryName)
            saveCallGraphToJson callGraph outputPath)

    // Source call graph generation
    let fcgMakerSrc (inputPath: string) =
        let codeTypes = ["matchcode"; "patchcode"; "vulcode"]

        for codeType in codeTypes do
            let featurePath = Path.Combine(inputPath, "processed_input", "features", codeType)
            if Directory.Exists(featurePath) then
                let callGraph = processSfuncFiles(inputPath, featurePath)
                let outputPath = Path.Combine(inputPath, "processed_input", "info", codeType)
                Directory.CreateDirectory(outputPath) |> ignore
                saveCallGraphToJson callGraph outputPath
                printfn "Processed call graph for %s" codeType
