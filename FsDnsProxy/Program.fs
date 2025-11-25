open FSharp.Core
open FSharp.Control
open Microsoft.FSharp.Collections

open System
open System.Net
open System.IO
open System.Threading
open System.Collections.Generic

open ARSoft.Tools.Net
open ARSoft.Tools.Net.Dns
open Serilog


let logger =
    LoggerConfiguration()
        .WriteTo.Console(outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}{NewLine}{Exception}")
        .CreateLogger()

let mapWhere<'T> (pred: 'T -> bool) (map: 'T -> 'T) (value: 'T list) =
    List.map (fun s -> if pred s then map s else s) value

let serializer = YamlParser.Parser.Create()

let ParseSetting (path: string) =
    try
        serializer.Deserialize<YamlParser.Setting>(File.ReadAllText path) |> Some
    with e ->
        logger.Error(e, "Deserialize Yaml Failed")
        None


[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module YamlSetting =
    [<NoComparison>]
    type SettingDNSServer =
        { Name: string
          Domains: DomainName list
          Address: IPEndPoint list
          Address6: IPEndPoint list }

    [<NoComparison>]
    type Setting =
        { Id: Guid
          Servers: SettingDNSServer list
          PollingInterval: int
          Port: int }

    let fromSetting (setting: YamlParser.Setting) =
        let remotes =
            setting.Dns
            |> Seq.map (|KeyValue|)
            |> Seq.map (fun (name, d) ->
                { Name = name
                  Domains = d.Domains |> List.ofSeq
                  Address = d.Address |> List.ofSeq
                  Address6 = d.Address6 |> List.ofSeq })
            |> Seq.toList

        match remotes with
        | [] -> None
        | _ ->
            Some
                { Id = Guid.NewGuid()
                  Servers = remotes
                  PollingInterval = setting.PollingInterval
                  Port = setting.Port }

    let chooseClient (domainName: DomainName) (servers: SettingDNSServer list) =
        servers
        |> List.tryFindIndex (fun r ->
            r.Domains.Length = 0
            || r.Domains |> List.exists (fun d -> d = domainName || domainName.IsSubDomainOf d))

module SettingStore =
    [<NoComparison>]
    type State =
        { refCount: int
          setting: YamlSetting.Setting
          clients: DnsClient list list }

    [<NoComparison>]
    type Store =
        { mutable curStateId: Guid option
          mutable states: Dictionary<Guid, State> }

        member this.addState(setting: YamlSetting.Setting) =
            if not (this.states.ContainsKey setting.Id) then
                this.states[setting.Id] <-
                    { refCount = 1
                      setting = setting
                      clients =
                        setting.Servers
                        |> List.map (fun s ->
                            let c4 =
                                s.Address
                                |> List.map (fun e ->
                                    new DnsClient([ e.Address ], [| new UdpClientTransport(e.Port) |], true, 1000))

                            let c6 =
                                s.Address6
                                |> List.map (fun e ->
                                    new DnsClient([ e.Address ], [| new UdpClientTransport(e.Port) |], true, 1000))

                            c4 @ c6) }

            this.curStateId |> Option.iter this.releaseState
            this.curStateId <- Some setting.Id

        member this.acquireState() =
            this.curStateId
            |> Option.map (fun x ->
                this.states[x] <-
                    { this.states[x] with
                        refCount = this.states[x].refCount + 1 }

                this.states[x])

        member this.releaseState(stateId: Guid) =
            match this.states[stateId].refCount with
            | 1 ->
                this.states[stateId].clients
                |> Seq.concat
                |> Seq.iter (fun x -> (x :> IDisposable).Dispose())

                this.states.Remove stateId |> ignore
            | _ ->
                this.states[stateId] <-
                    { this.states[stateId] with
                        refCount = this.states[stateId].refCount - 1 }

let ResolveDnsQuery (state: SettingStore.State) (msg: DnsMessage) =
    async {
        let res = msg.CreateResponseInstance()

        match msg.Questions.Count with
        | count when count > 0 ->
            let question = msg.Questions[0]
            logger.Debug("Question: {question}", question)

            let clients =
                YamlSetting.chooseClient question.Name state.setting.Servers
                |> Option.map (fun i -> state.clients[i])

            match clients with
            | Some clients ->
                try
                    let! upstreamResponse =
                        clients
                        |> List.map (fun client ->
                            async {
                                let! x =
                                    client.ResolveAsync(
                                        question.Name,
                                        question.RecordType,
                                        question.RecordClass,
                                        DnsQueryOptions(IsRecursionDesired = true, IsEDnsEnabled = true)
                                    )
                                    |> Async.AwaitTask

                                return Some x
                            })
                        |> Async.Choice

                    match upstreamResponse |> Option.map Option.ofObj with
                    | None
                    | Some None -> res.ReturnCode <- ReturnCode.ServerFailure
                    | Some(Some upstreamResponse) ->
                        res.AnswerRecords <- upstreamResponse.AnswerRecords
                        res.AdditionalRecords <- upstreamResponse.AdditionalRecords
                        res.AuthorityRecords <- upstreamResponse.AuthorityRecords
                        res.ReturnCode <- upstreamResponse.ReturnCode
                with e ->
                    logger.Error(e, "Resolve Error")
                    res.ReturnCode <- ReturnCode.ServerFailure
            | None -> res.ReturnCode <- ReturnCode.ServerFailure
        | _ -> res.ReturnCode <- ReturnCode.NoError

        return res
    }

[<NoComparison>]
type ProcessorMessage =
    | SettingChanged of YamlSetting.Setting
    | DnsQuery of DnsMessage * AsyncReplyChannel<DnsMessage>
    | ReleaseState of YamlSetting.Setting

let QueryProcessor =
    MailboxProcessor<ProcessorMessage>.Start(fun inbox ->
        let statesAll: SettingStore.Store =
            { curStateId = None
              states = Dictionary() }

        AsyncSeq.initInfiniteAsync (fun _ -> inbox.Receive())
        |> AsyncSeq.map (fun msg ->

            match msg with
            | SettingChanged setting ->
                statesAll.addState setting
                None

            | DnsQuery(dnsMessage, replyChannel) ->
                statesAll.acquireState () |> Option.map (fun s -> s, dnsMessage, replyChannel)

            | ReleaseState setting ->
                statesAll.releaseState setting.Id
                None)
        |> AsyncSeq.choose id
        |> AsyncSeq.iterAsyncParallel (fun (state, dnsMessage, replyChannel) ->
            async {
                try
                    let! response = ResolveDnsQuery state dnsMessage
                    replyChannel.Reply response
                finally
                    inbox.Post(ReleaseState state.setting)
            }))


let LocalServer (port: int) =
    let server =
        new DnsServer(
            new UdpServerTransport(IPEndPoint(IPAddress.Any, port)),
            new TcpServerTransport(IPEndPoint(IPAddress.Any, port))
        )

    server.add_QueryReceived (fun x e ->
        task {
            let query = e.Query :?> DnsMessage
            let! response = QueryProcessor.PostAndAsyncReply(fun replyChannel -> DnsQuery(query, replyChannel))
            e.Response <- response
            ()
        })

    server

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Option =
    let handleUpdate (hasChanged: 'T -> 'T -> bool) (callback: 'T -> unit) (a: 'T option) (b: 'T option) =
        match a, b with
        | None, None -> None
        | None, Some b -> Some b
        | Some a, None ->
            callback a
            Some a
        | Some a, Some b ->
            if hasChanged a b then
                callback a

            Some a

let rec PollSetting (oldSetting: YamlSetting.Setting option) (path: string) =
    async {
        match ParseSetting path with
        | None -> return! PollSetting oldSetting path
        | Some _setting ->
            let newSetting = YamlSetting.fromSetting _setting

            let curSetting =
                (newSetting, oldSetting)
                ||> Option.handleUpdate
                        (fun a b -> a.Servers <> b.Servers)
                        (SettingChanged
                         >> QueryProcessor.Post
                         >> fun x ->
                             logger.Information "Setting reloaded"
                             x)

            let pollingInterval =
                curSetting |> Option.map _.PollingInterval |> Option.defaultValue 1

            do! Async.Sleep(TimeSpan(0, 0, pollingInterval))
            return! PollSetting curSetting path
    }

[<EntryPoint>]
let main args =
    if args.Length = 0 then
        logger.Information "Usage: FsDnsProxy <config_file>.yaml\n\n  <config_file>.yaml required"
        1
    else
        let setting =
            Seq.initInfinite (fun _ ->
                Thread.Sleep(TimeSpan(0, 0, 1))
                ParseSetting args[0] |> Option.bind YamlSetting.fromSetting)
            |> Seq.choose id
            |> Seq.head

        QueryProcessor.Post(SettingChanged setting)
        let server = LocalServer setting.Port
        logger.Information $"Listening on port: {setting.Port}"
        server.Start()
        logger.Information "Local server started"

        PollSetting (Some setting) args[0] |> Async.RunSynchronously

        server.Stop()
        logger.Information "Local server stopped"
        0
