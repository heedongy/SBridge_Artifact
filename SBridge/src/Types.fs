namespace SBridge

module Types =

    type ControlBlockType =
        | Condition
        | Loop
        | String
        | Else
        | LibcFunc
        | RecursiveFunc
        | CalleeFunc

    type OrgType =
        | If
        | While
        | DoWhile
        | For
        | Switch
        | FunctionCall
        | Assignment
        | Return
        | String
        | Else
        | PointerCall

    type ControlBlock = {
        block_type : ControlBlockType
        org_type: OrgType
        line: int
        node_id: string
        block_content: string list
        key_feature: string list
    }

    type CtagFunction = {
        mutable ParentFile: string
        mutable ParentNumLoc: int
        mutable Name: string
        mutable Lines: int * int
        mutable FuncId: int
        mutable ParameterList: string list
        mutable VariableList: string list
        mutable DataTypeList: string list
        mutable FuncCalleeList: string list
        mutable FuncBody: string
    }

    type BlockContentVector = {
        ExactVector: float[]
        NumTypePositions: int list
        Numbers: int64 list
    }


    type BlockContentVectorType =
        | BCVNum of int64
        | BCVNumType
        | BCVElementType of string
        | BCVOperator of string
        | BCVLiteralValue of string
        | BCVFunctionValue of string
        | BCVFunctionCount of int

    type BlockContentVectorVocabulary = {
        ElementTypeToIndex: Map<string, int>
        OperatorToIndex: Map<string, int>
        LibcFuncToIndex: Map<string, int>
        FunctionCountIndex: int
        StaticNumberToIndex: Map<string, int>
        LiteralValueToIndex: Map<string, int>
        FunctionValueToIndex: Map<string, int>
        NumberToIndex: Map<int64, int>
        NextIndex: int
    }

    type OrgTypeKind =
        | If of content:string * line:int
        | While of content:string * line:int
        | DoWhile of content:string * line:int
        | For of content:string * line:int
        | Switch of content:string * line:int
        | Assignment of content:string * line:int
        | Literal of content:string * line:int
        | Return of content:string * line:int
        | FuncCall of content:string * line:int
        | Else of content:string * line:int
        | PointerCall of content:string * line:int

    let parseOrgType (orgTypeStr: string) : OrgType =
        match orgTypeStr with
        | "If" -> OrgType.If
        | "While" -> OrgType.While
        | "DoWhile" -> OrgType.DoWhile
        | "For" -> OrgType.For
        | "Switch" -> OrgType.Switch
        | "FunctionCall" -> OrgType.FunctionCall
        | "Assignment" -> OrgType.Assignment
        | "Return" -> OrgType.Return
        | "String" -> OrgType.String
        | "Else" -> OrgType.Else
        | _ -> failwithf "Unknown OrgType: %s" orgTypeStr

    let parseControlBlockType (orgTypeStr: string) : ControlBlockType =
        match orgTypeStr with
        | "Condition" -> ControlBlockType.Condition
        | "Loop" -> ControlBlockType.Loop
        | "String" -> ControlBlockType.String
        | "Else" -> ControlBlockType.Else
        | "LibcFunc" -> ControlBlockType.LibcFunc
        | "RecursiveFunc" -> ControlBlockType.RecursiveFunc
        | "CalleeFunc" -> ControlBlockType.CalleeFunc
        | _ -> failwithf "Unknown ControlBlockType: %s" orgTypeStr





    let structurePattern = "\\(CONTROL_STRUCTURE,([^,]+),(.+?)\\)<SUB>(\\d+)</SUB>"
    let literalPattern = "\\(LITERAL,\"(.*?)\",[^<]*\\)<SUB>(\\d+)</SUB>"
    let assignmentPattern = "\\(<operator>.assignment,(.*?)\\)<SUB>(\\d+)</SUB>"
    let returnPattern = "\\(RETURN,(.*?)\\)<SUB>(\\d+)</SUB>"
    let functionPattern = "\\((\\w+),(\\1\\s*\\([^<]*)\\)<SUB>(\\d+)</SUB>"
    let pointerCallPattern = "\(<operator>.pointerCall,(.*?)\)<SUB>(\d+)</SUB>"
    let switchPattern = "\\(JUMP_TARGET,case\\)<SUB>(\\d+)</SUB>"
    let switchDefaultPattern = "\\(JUMP_TARGET,default\\)<SUB>(\\d+)</SUB>"
    let conditionOperatorPattern = @"\(<operator>\.(or|and|not|equals|notEquals|lessThan|greaterThan|lessEqualsThan|greaterEqualsThan),(.*?)\)<SUB>(\d+)</SUB>"
    let pathPattern = "(?:[a-zA-Z]:[\\/\\\\]|\\/)(?:[^\\/\\\\\\n\\r]+[\\/\\\\])*[^\\/\\\\\\n\\r]*"
    let cfgPattern = "\"(\\d+)\"\\s*->\\s*\"(\\d+)\"\\s*\\[\\s*label\\s*=\\s*\"CFG:\\s*\"\\s*\\]"
    let astPattern = "\"(\\d+)\"\\s*->\\s*\"(\\d+)\"\\s*\\[\\s*label\\s*=\\s*\"AST:\\s*\"\\s*\\]"
    let ddgPattern = "\"(\\d+)\"\\s*->\\s*\"(\\d+)\"\\s*\\[\\s*label\\s*=\\s*\"DDG:\\s*\"\\s*\\]"
    let ddgLabelPattern = "\"(\\d+)\"\\s*->\\s*\"(\\d+)\"\\s*\\[\\s*label\\s*=\\s*\"DDG:\\s*(.+?)\"\\s*\\]"
    let cdgPattern = "\"(\\d+)\"\\s*->\\s*\"(\\d+)\"\\s*\\[\\s*label\\s*=\\s*\"CDG:\\s*\"\\s*\\]"
