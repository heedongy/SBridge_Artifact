namespace SBridge

module ParserQuery =
    open System
    open System.Web
    open System.Net.Http
    open System.IO
    open System.Text
    open System.Text.Json
    open System.Threading.Tasks
    open System.Text.RegularExpressions
    open Config
    open System.Diagnostics

    let decodeHtmlEntities (input: string) : string =
        HttpUtility.HtmlDecode(input)

    let sendQueryAndGetUuid (client: HttpClient) (query: string) : Async<string> = async {
        try
            let payload = JsonSerializer.Serialize({| query = query |})
            use content = new StringContent(payload, Encoding.UTF8, "application/json")
            use! response = client.PostAsync("/query", content) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore
            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            let doc = JsonDocument.Parse(responseBody)
            let mutable uuidProp = Unchecked.defaultof<JsonElement>
            if doc.RootElement.TryGetProperty("uuid", &uuidProp) then
                return uuidProp.GetString()
            else
                return failwithf "uuid not found in response: %s" responseBody
        with ex ->
            printfn "[!] An error occurred while sending the request: %s" ex.Message
            return failwith "Request sending failed"
    }

    let rec getResultByUuid (client: HttpClient) (uuid: string) = async {
        let startTime = DateTime.Now

        let rec tryGetResult () = async {
            if (DateTime.Now - startTime).TotalMinutes >= 2.0 then
                return failwith "[!] Problem with the Joern server, the container will be deleted and restarted."

            use! response = client.GetAsync("/result/" + uuid) |> Async.AwaitTask
            response.EnsureSuccessStatusCode() |> ignore

            let! responseBody = response.Content.ReadAsStringAsync() |> Async.AwaitTask

            // Parse JSON
            let doc = JsonDocument.Parse(responseBody)

            // Check success field
            let mutable successProp = Unchecked.defaultof<JsonElement>
            let successValue =
                if doc.RootElement.TryGetProperty("success", &successProp) then
                    successProp.GetBoolean()
                else
                    false

            if successValue then
                // Extract stdout and stderr
                let mutable stdoutProp = Unchecked.defaultof<JsonElement>
                let stdoutValue =
                    if doc.RootElement.TryGetProperty("stdout", &stdoutProp) then
                        stdoutProp.GetString()
                    else
                        ""

                let mutable stderrProp = Unchecked.defaultof<JsonElement>
                let stderrValue =
                    if doc.RootElement.TryGetProperty("stderr", &stderrProp) then
                        stderrProp.GetString()
                    else
                        ""

                return stdoutValue, stderrValue
            else
                do! Async.Sleep(500)
                return! tryGetResult()
        }

        return! tryGetResult()
    }

    let executeQuery (client: HttpClient) (query: string) = async {
        let! uuid = sendQueryAndGetUuid client query
        let! (stdoutVal, stderrVal) = getResultByUuid client uuid
        return stdoutVal, stderrVal
    }


    let joernParserQuery (codePath: string) (inputFunctionName: string) (basePath: string) =
        use httpClient = new HttpClient(BaseAddress = Uri(sprintf "http://localhost:%d" JOERN_SERVER_PORT))

        try
            let importQuery = sprintf "importCode(\"%s\", \"%s\")" codePath "cpro2s"
            let importStdout, importStderr = executeQuery httpClient importQuery |> Async.RunSynchronously
            if not (String.IsNullOrWhiteSpace(importStderr)) then
                printfn "Import error: %s" importStderr

            let queryForFunction = sprintf "cpg.method.name(\"%s\").dotCpg14.l" inputFunctionName
            let dotcpgOutput, dotcpgStderr = executeQuery httpClient queryForFunction |> Async.RunSynchronously
            let dotcpgStr = decodeHtmlEntities dotcpgOutput

            inputFunctionName, dotcpgStr
        with
        | ex ->
            printfn "[*] Unable to connect to the advice server or no response: %s" ex.Message
            Environment.Exit(1)
            failwith "SBridge has terminated by joern server"