namespace SBridge

module JsonConverters =
    open System
    open System.Text.Json
    open System.Text.Json.Serialization
    open SBridge.Types
    open System.IO


    type OrgTypeConverter() =
        inherit JsonConverter<OrgType>()

        override this.Write(writer, value, options) =
            let strValue =
                match value with
                | OrgType.If -> "If"
                | OrgType.While -> "While"
                | OrgType.DoWhile -> "DoWhile"
                | OrgType.For -> "For"
                | OrgType.Switch -> "Switch"
                | OrgType.Else -> "Else"
                | OrgType.Assignment -> "Assignment"
                | OrgType.FunctionCall -> "FunctionCall"
                | OrgType.String -> "String"
                | OrgType.Return -> "Return"
                | OrgType.PointerCall -> "FunctionCall"
            writer.WriteStringValue(strValue)

        override this.Read(reader, _, _) =
            match reader.GetString() with
            | "If" -> OrgType.If
            | "While" -> OrgType.While
            | "DoWhile" -> OrgType.DoWhile
            | "For" -> OrgType.For
            | "Switch" -> OrgType.Switch
            | "Else" -> OrgType.Else
            | "FunctionCall" -> OrgType.FunctionCall
            | "String" -> OrgType.String
            | "Return" -> OrgType.Return
            | "PointerCall" -> OrgType.FunctionCall
            | x -> failwithf "Unknown OrgType: %s" x



    type ControlBlockTypeConverter() =
        inherit JsonConverter<ControlBlockType>()

        override this.Write(writer, value, options) =
            let strValue =
                match value with
                | ControlBlockType.Condition -> "Condition"
                | ControlBlockType.Loop -> "Loop"
                | ControlBlockType.String -> "String"
                | ControlBlockType.Else -> "Else"
                | ControlBlockType.LibcFunc -> "LibcFunc"
                | ControlBlockType.RecursiveFunc -> "RecursiveFunc"
                | ControlBlockType.CalleeFunc -> "CalleeFunc"
            writer.WriteStringValue(strValue)

        override this.Read(reader, _, _) =
            match reader.GetString() with
            | "Condition" -> ControlBlockType.Condition
            | "Loop" -> ControlBlockType.Loop
            | "String" -> ControlBlockType.String
            | "Else" -> ControlBlockType.Else
            | "LibcFunc" -> ControlBlockType.LibcFunc
            | "RecursiveFunc" -> ControlBlockType.RecursiveFunc
            | "CalleeFunc" -> ControlBlockType.CalleeFunc
            | x -> failwithf "Unknown ControlBlockType: %s" x



    let loadJsonControlBlocks (filePath: string): ControlBlock list =
        try
            let json = File.ReadAllText(filePath)
            let rawBlocks = Newtonsoft.Json.JsonConvert.DeserializeObject<Map<string, obj> list>(json)
            rawBlocks
            |> List.map (fun rawBlock ->
                {
                    block_type = parseControlBlockType (rawBlock.["block_type"] :?> string)
                    org_type = parseOrgType (rawBlock.["org_type"] :?> string)
                    line = Convert.ToInt32(rawBlock.["line"])
                    node_id = rawBlock.["node_id"] :?> string
                    block_content =
                        (rawBlock.["block_content"] :?> Newtonsoft.Json.Linq.JArray)
                        |> Seq.map (fun x -> x.ToString())
                        |> List.ofSeq
                    key_feature =
                        (rawBlock.["key_feature"] :?> Newtonsoft.Json.Linq.JArray)
                        |> Seq.map (fun x -> x.ToString())
                        |> List.ofSeq
                }
            )
        with
        | :? IOException as ex ->
            printfn "Error reading file: %s" ex.Message
            []
        | :? JsonException as ex ->
            printfn "Error parsing JSON in file: %s" ex.Message
            []
        | ex ->
            printfn "Unexpected error: %s" ex.Message
            []