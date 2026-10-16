module Masaryk.Core.Services.Chat

open System
open System.Collections.Generic
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open System.Threading.Tasks
open Discord
open Discord.WebSocket
open Masaryk.Core.Services.Shared
open Masaryk.Core
open Masaryk.Postgres

let private contextSize = 8

type Settings = { Http: HttpClient; Model: string }

let fromEnvironment () : Settings option =
    let read name =
        Environment.GetEnvironmentVariable name
        |> Option.ofObj
        |> Option.filter ((<>) "")

    read "LLM_API_URL"
    |> Option.map (fun url ->
        let http = new HttpClient(BaseAddress = Uri(url.TrimEnd '/' + "/"))

        read "LLM_API_KEY"
        |> Option.iter (fun key ->
            http.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", key))

        {
            Http = http
            Model = read "LLM_MODEL" |> Option.defaultValue "masaryk"
        })

type Message =
    {
        Role: string
        Name: string
        Content: string
    }

type CompletionRequest =
    {
        Model: string
        Messages: Message list
    }

let private json =
    JsonSerializerOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)

let complete (settings: Settings) (messages: Message list) : Task<string> =
    task {
        let request =
            {
                Model = settings.Model
                Messages = messages
            }

        use! response = settings.Http.PostAsJsonAsync("chat/completions", request, json)
        let! body = response.Content.ReadAsStringAsync()

        if not response.IsSuccessStatusCode then
            failwith $"HTTP {int response.StatusCode}: {body}"

        use document = JsonDocument.Parse body
        let choice = document.RootElement.GetProperty("choices")[0]
        return choice.GetProperty("message").GetProperty("content").GetString()
    }

// Discord also counts the replied-to author as mentioned, so check the text.
let private mentionsBot (botId: uint64) (message: IMessage) = message.Content.Contains $"<@{botId}>"

let isCandidate (botId: uint64) (guildId: uint64) (message: IMessage) : bool =
    match message.Channel with
    | :? IGuildChannel as channel ->
        channel.GuildId = guildId
        && not message.Author.IsBot
        && (mentionsBot botId message || (Replies.parentId message).IsSome)
    | _ -> false

let private urls = Regex(@"https?://\S+|www\.\S+")

// The model never saw mention markup or URLs in training.
let text (botId: uint64) (message: IMessage) : string =
    let content = StringBuilder(message.Content)

    for tag in
        message.Tags
        |> Seq.filter (fun t -> t.Type <> TagType.Emoji)
        |> Seq.sortByDescending _.Index do
        let name =
            match tag.Value with
            | :? IUser as user when user.Id <> botId -> MessageEmbed.authorName user
            | :? IChannel as channel -> "#" + channel.Name
            | :? IRole as role -> role.Name
            | _ -> ""

        content.Remove(tag.Index, tag.Length).Insert(tag.Index, name) |> ignore

    urls.Replace(string content, "").Trim()

// Neutral names keep Discord usernames and IDs off the server.
let build (botId: uint64) (modelReplies: Set<uint64>) (messages: IMessage list) : Message list =
    let names = Dictionary<uint64, string>()

    messages
    |> List.choose (fun message ->
        let content = text botId message

        if content = "" then
            None
        elif modelReplies.Contains message.Id then
            Some
                {
                    Role = "assistant"
                    Name = null
                    Content = content
                }
        elif message.Author.IsBot then
            None
        else
            if not (names.ContainsKey message.Author.Id) then
                names[message.Author.Id] <- $"u{names.Count + 1}"

            Some
                {
                    Role = "user"
                    Name = names[message.Author.Id]
                    Content = content
                })

let private history (trigger: IUserMessage) : Task<IMessage list> =
    task {
        let channel = trigger.Channel

        let! earlier =
            if (Replies.parentId trigger).IsSome then
                Replies.ancestors (fun id -> channel.GetMessageAsync(id)) (contextSize - 1) trigger
            else
                task {
                    let pages = channel.GetMessagesAsync(trigger, Direction.Before, contextSize - 1)
                    let! messages = unbox<IAsyncEnumerable<seq<IMessage>>>(box pages) |> _.FlattenAsync()
                    return messages |> Seq.sortBy _.Id |> Seq.toList
                }

        return earlier @ [ trigger ]
    }

let private noPings =
    AllowedMentions(AllowedMentionTypes.None, MentionRepliedUser = false)

let handle (client: DiscordSocketClient) (settings: Settings) (repository: ChatRepository) (trigger: IUserMessage) =
    task {
        let botId = client.CurrentUser.Id
        let! parent = repository.FindReplies(Option.toList (Replies.parentId trigger))

        if mentionsBot botId trigger || not parent.IsEmpty then
            use _typing = trigger.Channel.EnterTypingState()
            let! messages = history trigger
            let! modelReplies = repository.FindReplies(messages |> List.map _.Id)

            try
                let! reply = complete settings (build botId modelReplies messages)

                if reply <> "" then
                    let! sent = trigger.ReplyAsync(reply, allowedMentions = noPings)
                    do! repository.RecordReply(sent.Id, trigger.Channel.Id, trigger.Id)
            with error ->
                eprintfn $"Chat: no reply to {trigger.Channel.Id}/{trigger.Id}: {error.Message}"
                do! trigger.AddReactionAsync(Emoji "💤")
    }

let subscribe (client: DiscordSocketClient) (guildId: uint64) (requests: MailboxProcessor<IUserMessage>) =
    client.add_MessageReceived (fun message ->
        match message with
        | :? SocketUserMessage as user when isCandidate client.CurrentUser.Id guildId user -> requests.Post user
        | _ -> ()

        Task.CompletedTask)

let start: Feature =
    fun context ->
        match fromEnvironment () with
        | None -> printfn "Chat: disabled; set LLM_API_URL to enable it."
        | Some settings ->
            let repository = ChatRepository(context.Database)

            Worker.start
                (fun (message: IUserMessage) -> $"Chat reply to {message.Channel.Id}/{message.Id}")
                (fun message -> handle context.Client settings repository message)
            |> subscribe context.Client context.GuildId

        Task.FromResult []
