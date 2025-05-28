open FSharp.Core
open FSharp.Control

open ARSoft.Tools.Net
open ARSoft.Tools.Net.Dns

open System
open System.Net
open System.IO
open System.Threading
open System.Collections.Generic

open Microsoft.Extensions.Logging
open Microsoft.FSharp.Collections


let logger =
    LoggerFactory
        .Create(fun x ->
            x.AddSimpleConsole(fun o ->
                o.SingleLine <- true
                o.IncludeScopes <- false)
            |> ignore)
        .CreateLogger
        "FsDnsProxy"

let mapWhere<'T> (pred: 'T -> bool) (map: 'T -> 'T) (value: 'T list) =
    List.map (fun s -> if pred s then map s else s) value

let serializer = YamlParser.Parser.Create()

let ParseSetting (path: string) =
    serializer.Deserialize<YamlParser.Setting>(File.ReadAllText path)

[<NoComparison>]
type SettingDNSServer =
    { Name: string
      Domains: DomainName list
      Address: IPAddress
      Port: int }

[<NoComparison>]
type Setting =
    { Id: Guid
      Servers: SettingDNSServer list
      Fallback: SettingDNSServer
      PollingInterval: int
      Port: int }

[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Setting =
    let fromSetting (setting: YamlParser.Setting) =
        let remotes =
            setting.Dns
            |> Seq.map (|KeyValue|)
            |> Seq.map (fun (name, d) ->
                { Name = name
                  Domains = d.Domains |> Seq.toList
                  Address = d.Address
                  Port = d.Port })
            |> Seq.toList

        match remotes with
        | [] -> None
        | _ ->
            Some
                { Id = Guid.NewGuid()
                  Servers = remotes |> List.take (remotes.Length - 1)
                  Fallback = List.last remotes
                  PollingInterval = setting.PollingInterval
                  Port = setting.Port }

    let chooseClient (domainName: DomainName) (servers: SettingDNSServer list) =
        servers
        |> List.tryFindIndex (fun r -> r.Domains |> List.exists (fun d -> d = domainName || domainName.IsSubDomainOf d))

[<NoComparison>]
type ProcessorMessage =
    | SettingChanged of Setting
    | DnsQuery of DnsMessage * AsyncReplyChannel<DnsMessage>
    | ReleaseState of Setting

[<NoComparison>]
type SettingState =
    { refCount: int
      setting: Setting
      clients: DnsClient list
      fallbackClient: DnsClient }

[<NoComparison>]
type SettingStateAll =
    { mutable curStateId: Guid option
      mutable states: Dictionary<Guid, SettingState> }

    member this.addState(setting: Setting) =
        if not (this.states.ContainsKey setting.Id) then
            this.states[setting.Id] <-
                { refCount = 1
                  setting = setting
                  clients =
                    setting.Servers
                    |> List.map (fun s -> new DnsClient([ s.Address ], [| new UdpClientTransport(s.Port) |], true))
                  fallbackClient =
                    new DnsClient(
                        [ setting.Fallback.Address ],
                        [| new UdpClientTransport(setting.Fallback.Port) |],
                        true
                    ) }

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
            this.states[stateId].fallbackClient :: this.states[stateId].clients
            |> Seq.iter (fun x -> (x :> IDisposable).Dispose())

            this.states.Remove stateId |> ignore
        | _ ->
            this.states[stateId] <-
                { this.states[stateId] with
                    refCount = this.states[stateId].refCount - 1 }

let ResolveDnsQuery (state: SettingState) (msg: DnsMessage) =
    async {
        let res = msg.CreateResponseInstance()

        match msg.Questions.Count with
        | count when count > 0 ->
            let question = msg.Questions[0]

            let setting, client =
                Setting.chooseClient question.Name state.setting.Servers
                |> Option.map (fun i -> state.setting.Servers[i], state.clients[i])
                |> Option.defaultValue (state.setting.Fallback, state.fallbackClient)

            logger.LogInformation $"'{setting.Name}' selected for domain {question.Name}"

            try
                let! upstreamResponse =
                    client.ResolveAsync(
                        question.Name,
                        question.RecordType,
                        question.RecordClass,
                        DnsQueryOptions(IsRecursionDesired = true, IsEDnsEnabled = true)
                    )
                    |> Async.AwaitTask

                match upstreamResponse |> Option.ofObj with
                | None -> res.ReturnCode <- ReturnCode.ServerFailure
                | Some upstreamResponse ->
                    res.AnswerRecords <- upstreamResponse.AnswerRecords
                    res.AdditionalRecords <- upstreamResponse.AdditionalRecords
                    res.AuthorityRecords <- upstreamResponse.AuthorityRecords
                    res.ReturnCode <- upstreamResponse.ReturnCode
            with _ ->
                res.ReturnCode <- ReturnCode.ServerFailure
        | _ -> res.ReturnCode <- ReturnCode.NoError

        return res
    }

let QueryProcessor =
    MailboxProcessor<ProcessorMessage>.Start(fun inbox ->
        let statesAll =
            { curStateId = None
              states = Dictionary() }

        AsyncSeq.initInfiniteAsync (fun _ -> inbox.Receive())
        |> AsyncSeq.map (fun msg ->
            let stateStr =
                statesAll.states
                |> Seq.map (|KeyValue|)
                |> Seq.map (fun (id, v) ->
                    let isCur = if Some id = statesAll.curStateId then "*" else ""
                    $"{isCur}{id}({v.refCount})")
                |> Seq.toList

            logger.LogInformation $"{msg.GetType().Name} states: %A{stateStr}"

            match msg with
            | SettingChanged setting ->
                // logger.LogInformation $"[{nameof SettingChanged}]"
                statesAll.addState setting
                None

            | DnsQuery(dnsMessage, replyChannel) ->
                logger.LogInformation $"[{nameof DnsQuery}]: %A{dnsMessage.Questions}"
                statesAll.acquireState () |> Option.map (fun s -> s, dnsMessage, replyChannel)

            | ReleaseState setting ->
                // logger.LogInformation $"[{nameof ReleaseState}]"
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

let rec PollSetting (oldSetting: Setting option) (path: string) =
    async {
        let newSetting = ParseSetting path |> Setting.fromSetting

        let curSetting =
            Option.handleUpdate
                (fun a b -> a.Servers <> b.Servers || a.Fallback <> b.Fallback)
                (SettingChanged >> QueryProcessor.Post)
                newSetting
                oldSetting

        let pollingInterval =
            curSetting |> Option.map _.PollingInterval |> Option.defaultValue 1

        do! Async.Sleep(TimeSpan(0, 0, pollingInterval))
        return! PollSetting curSetting path
    }

[<EntryPoint>]
let main args =
    if args.Length = 0 then
        logger.LogInformation "usage: FsDnsProxy <config_file>.yaml\n\n  <config_file>.yaml required"
        1
    else
        let setting =
            Seq.initInfinite (fun _ ->
                Thread.Sleep(TimeSpan(0, 0, 1))
                ParseSetting args[0] |> Setting.fromSetting)
            |> Seq.choose id
            |> Seq.head

        QueryProcessor.Post(SettingChanged setting)
        let server = LocalServer setting.Port
        logger.LogInformation $"Listening on port: {setting.Port}"
        server.Start()
        logger.LogInformation "local server started"

        PollSetting (Some setting) args[0] |> Async.RunSynchronously

        server.Stop()
        logger.LogInformation "local server stopped"
        0
