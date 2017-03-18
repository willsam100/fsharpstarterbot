#r "packages/Suave/lib/net40/Suave.dll"
#r "packages/Microsoft.Bot.Builder/lib/net46/Microsoft.Bot.Connector.dll"
#r "packages/Microsoft.Bot.Builder/lib/net46/Microsoft.Bot.Builder.dll"
#r "packages/Microsoft.Rest.ClientRuntime/lib/net45/Microsoft.Rest.ClientRuntime.dll"
#r "packages/Microsoft.WindowsAzure.ConfigurationManager/lib/net40/Microsoft.WindowsAzure.Configuration.dll"
#r "packages/Newtonsoft.Json/lib/net45/Newtonsoft.Json.dll"
#r "packages/Autofac/lib/net40/Autofac.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/System.Net.Http/lib/net46/System.Net.Http.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Hopac/lib/net45/Hopac.Platform.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Hopac/lib/net45/Hopac.Core.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Hopac/lib/net45/Hopac.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Http.fs/lib/net40/HttpFs.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Microsoft.IdentityModel.Protocol.Extensions/lib/net45/Microsoft.IdentityModel.Protocol.Extensions.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/System.Security.Cryptography.X509Certificates/lib/net461/System.Security.Cryptography.X509Certificates.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/System.IdentityModel.Tokens.Jwt/lib/net451/System.IdentityModel.Tokens.Jwt.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Microsoft.IdentityModel.Tokens/lib/net451/Microsoft.IdentityModel.Tokens.dll"
#r "/Users/Ashley/code/fsharpstarterbot/packages/Microsoft.IdentityModel.Logging/lib/net451/Microsoft.IdentityModel.Logging.dll"
#load @"packages/SwaggerProvider/SwaggerProvider.fsx"
#load "/Users/Ashley/code/fsharpstarterbot/.paket/load/microsoft.identitymodel.tokens.fsx"
#load "/Users/Ashley/code/fsharpstarterbot/.paket/load/microsoft.identitymodel.logging.fsx"
#load "/Users/Ashley/code/fsharpstarterbot/.paket/load/system.identitymodel.tokens.jwt.fsx"

open SwaggerProvider

// open Encodings
open System
open System.Security.Claims
open System.IdentityModel.Tokens
open System.Security.Cryptography
// open System.Globalization.Encodings
open System.Security.Claims
open System.IdentityModel.Tokens.Jwt
open System.Security.Cryptography

open Suave
open Suave.Successful
open Suave.Web
open Suave.Operators
open Suave.Filters
open Newtonsoft.Json
open Newtonsoft.Json.Serialization
open Microsoft.Bot.Connector
open Microsoft.Bot.Builder
open Microsoft.Bot.Builder.Dialogs
open System
open System.Threading.Tasks
open Hopac
open HttpFs.Client
open System.IdentityModel

let [<Literal>]Schema = __SOURCE_DIRECTORY__ + "/botConnectorv3.json"
type BotConnector = SwaggerProvider<Schema, "Content-Type=application/json">    

let envAsOption (envVarName : string) =
    let envVarValue = Environment.GetEnvironmentVariable(envVarName)
    if ((isNull envVarValue) || (envVarValue.Trim().Length = 0)) then None else Some envVarValue

let appId = defaultArg (envAsOption "MicrosoftAppID") "John"
let appSecret = defaultArg (envAsOption "MicrosoftAppPassword") "Secret"

[<AutoOpen>]
module Helpers = 

    let defaultSettings =   
        JsonSerializerSettings()  

    let useSnakeCase (settings: JsonSerializerSettings) = 
        let contractResolver = DefaultContractResolver()
        contractResolver.NamingStrategy <- new SnakeCaseNamingStrategy();
        settings.ContractResolver <- contractResolver
        settings

    let useCamelCase (settings: JsonSerializerSettings)  = 
        settings.ContractResolver <- new CamelCasePropertyNamesContractResolver()
        settings

    let toJsonWeb v =
        JsonConvert.SerializeObject(v, defaultSettings |> useSnakeCase)

    let toJson v =
        JsonConvert.SerializeObject(v, defaultSettings |> useCamelCase) |> OK
        >=> Writers.setMimeType "application/json; charset=utf-8"

    let fromJsonWeb<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>, defaultSettings |> useSnakeCase) :?> 'a

    let fromJson<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>, defaultSettings |> useCamelCase) :?> 'a

    let getResourceFromReq<'a> (req : HttpRequest) =
        let getString rawForm =
            System.Text.Encoding.UTF8.GetString(rawForm)
        req.rawForm |> getString |> fromJson<'a>


module BotAuth = 

    [<CLIMutable>]    
    type MicrosfotTokenResposne = {
        TokenType: string
        ExpiresIn:int
        ExtExpiresIn:int
        AccessToken: string 
    }

    type Token = {
        ExpiresUtc: DateTime 
        AccessToken: string 
    }

    type GetMicrosoftTokenMessage = 
        | Fetch of AsyncReplyChannel<Token>

    let tokenHolder () = 

        let getToken () = 
            let formData = [
                "grant_type=client_credentials"
                (sprintf "client_id=%s" appId)
                (sprintf "client_secret=%s" appSecret) 
                "scope=https://api.botframework.com/.default" ]
                
            Request.createUrl Post "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token"
            |> Request.bodyString (String.Join("&", formData))
            |> Request.setHeader (ContentType (ContentType.create("application", "x-www-form-urlencoded")))
            // |> (fun x -> printfn "Making request: %A" x; x)
            |> Request.responseAsString
            |> run
            // |> (fun x -> printfn "Repsonse: %A" x; x)
            |> fromJsonWeb<MicrosfotTokenResposne>
            |> (fun x -> {ExpiresUtc = DateTime.UtcNow.AddSeconds( (float) x.ExpiresIn); AccessToken = x.AccessToken})


        MailboxProcessor.Start(fun inbox -> 
            let rec messageLoop (state: Token) = async {

                let! (Fetch replyChannel) = inbox.Receive()
                let newState = 
                    if (state.ExpiresUtc < (DateTime.UtcNow.AddSeconds(60.))) then 
                        printfn "Token is invalid, refreshing.."
                        getToken ()
                    else 
                        state

                replyChannel.Reply newState
                return! messageLoop newState
            }
            messageLoop {ExpiresUtc = DateTime.MinValue; AccessToken = ""}
        )

    let getToken () = 
        let token = tokenHolder ()
        token.PostAndReply Fetch

[<Serializable>]
type MyBot () =
    // A count that shows the number of the current message
    let mutable count = 0

    // Called from PromptDialog.confirm
    member this.confirmReset (ctx : IDialogContext) (a : IAwaitable<bool>) =
        Task.Factory.StartNew(fun () ->
            let confirm = a.GetAwaiter().GetResult()

            if (confirm) then
                count <- 0
                "Count was reset" |> ctx.PostAsync |> ignore
            else
                "Count was not reset" |> ctx.PostAsync |> ignore

            ctx.Wait <| ResumeAfter(this.messageReceived)
        )
    
    // Handle received message 
    member this.messageReceived (ctx : IDialogContext) (a : IAwaitable<IMessageActivity>) = 
        Task.Factory.StartNew(fun () ->
            let message = a.GetAwaiter().GetResult()

            if (message.Text = "reset") then
                    PromptDialog.Confirm(ctx, ResumeAfter(this.confirmReset), "Are you sure you want to reset the count?", "Didn't get that!", 2, PromptStyle.None)
            else
                count <- count + 1
                let t = (sprintf "%d : You said: %s" count message.Text) |> ctx.PostAsync 
                ctx.Wait <| ResumeAfter(this.messageReceived)
        )

    interface IDialog with
        member this.StartAsync ctx = 
            Task.Factory.StartNew(fun () ->
                ctx.Wait <| ResumeAfter(this.messageReceived)
            )

/// Handle messages
let catchWebException f = 
    try 
        f ()
    with 
    | ex -> 
        let rec printException (ex: exn) = 
            match (ex.GetType().ToString()) with 
            | "System.Net.WebException" -> 
                let ex = ex :?> System.Net.WebException
                printfn "Web exception: %s %A %A\n%s" ex.Message ex.Data ex.HResult ex.StackTrace
                if (isNull ex.InnerException |> not) then 
                    printException ex.InnerException
            | e -> 
                printfn "Unknown Error: %s %s %s\n%s" ex.Message (ex.GetType().ToString()) e ex.StackTrace
                if (isNull ex.InnerException |> not) then 
                    printException ex.InnerException
        printException ex

let toSwaggerActivity (activity: Activity) = 
            BotConnector.Activity( 
                        Action = activity.Action,
                        AttachmentLayout = activity.AttachmentLayout,
                        From = (BotConnector.ChannelAccount(Id = activity.From.Id, Name = activity.From.Name)),
                        Recipient = (BotConnector.ChannelAccount(Id = activity.Recipient.Id, Name = activity.Recipient.Name)),
                        ChannelData = activity.ChannelData,
                        Conversation = (BotConnector.ConversationAccount(Id = activity.Conversation.Id, Name = activity.Conversation.Name)),
                        ReplyToId = activity.ReplyToId,
                        ChannelId = activity.ChannelId,
                        Locale = activity.Locale,
                        ServiceUrl = activity.ServiceUrl,
                        Text = activity.Text,
                        Type = activity.Type )
                    
                        
let botHandler (req: HttpRequest) (msftToken: unit -> BotAuth.Token) (message : Activity) =
    printfn "Received message of type %s: %s" message.Type message.Text
    printfn "Token expires in: %A" ((msftToken().ExpiresUtc) - DateTime.UtcNow)

    let isAuthenticated (token : string) = 
      
        let handler = JwtSecurityTokenHandler()
        let parsedToken = handler.ReadJwtToken <| token.Replace ("Bearer ", "")
        [
            parsedToken.Audiences |> Seq.contains ("https://graph.microsoft.com")
            parsedToken.Issuer = "https://sts.windows.net/72f988bf-86f1-41af-91ab-2d7cd011db47/"
            // parsedToken.Payload |> (fun x -> x.ContainsKey "appid")
            parsedToken.Payload |> (fun x -> x.ContainsValue appId)
            parsedToken.ValidTo > DateTime.UtcNow.AddMinutes 5.
        ]
        |> List.fold (&&) true

    match req.header "authorization" with 
    | Choice2Of2 _ -> printfn "No authorization header supplied";
    | Choice1Of2 x -> 
        match isAuthenticated x with 
        | false -> printfn "Invalid authorizaton supplied";
        | true -> 
            printfn "Authorization checks passed"
            let botConnector = BotConnector(message.ServiceUrl, 
                                            CustomizeHttpRequest =
                                                fun (req:System.Net.HttpWebRequest) -> 
                                                    let token = msftToken ()
                                                    printfn "Sending respones..."
                                                    req.Headers.Add(sprintf "Authorization: Bearer %s" token.AccessToken)
                                                    req.ContentType <- "application/json"; req )
            async {
                match (message.Type.ToLower()) with
                | "message" -> 
                    return catchWebException <| fun () -> 
                        let activity = message.CreateReply("Im fsharpy") |> toSwaggerActivity
                        botConnector.ConversationsReplyToActivity(message.Conversation.Id, message.Id, activity) //.ToString()
                        |> (fun x -> printfn "Response: %A" x.Id)

                                        
                | "ping" -> 
                    return catchWebException <| fun () -> 
                        let activity = message.CreateReply("ping") |> toSwaggerActivity
                        botConnector.ConversationsReplyToActivity(message.Conversation.Id, message.Id, activity) //.ToString()
                        |> (fun x -> printfn "Response: %A" x.Id)

                | "conversationupdate" -> 
                    printfn "System event type: %s" message.Action
                    return catchWebException <| fun () -> 
                        let newMembers = message.MembersAdded |> Seq.map (fun x -> x.Name) 
                                        |> Seq.filter (fun x -> message.Recipient.Name <> x)
                                        |> Seq.toArray
                        if newMembers.Length > 0 then                  
                            let activity = message.CreateReply("Hello " + String.Join(",", newMembers)) |> toSwaggerActivity 
                            activity.Attachments <- List.toArray <| [BotConnector.Attachment(ContentType = "image/png", ContentUrl = "http://fsharp.org/img/logo/fsharp256.png")] 
                            botConnector.ConversationsReplyToActivity(message.Conversation.Id, message.Id, activity) //.ToString()
                            |> (fun x -> printfn "Response: %A" x.Id)
                | "deleteuserData"
                | "botAddedToConversation"
                | "BotRemovedFromConversation"
                | "UserRemovedFromConversation"
                | "EndOfConversation"
                | _ -> return ()
                } |> Async.Start;


/// Suave application
let app = 

    //printfn "Environment auth details are %s %s" appId appSecret
    choose [ 
        path "/" >=> OK "Hello World!" 
        path "/api/messages" >=> request (fun req -> req |> getResourceFromReq |> botHandler req (BotAuth.getToken) |> toJson ) ]
            //  Authentication.authenticateBasic ((=) (appId, appSecret)) <|
            //      choose [

// This is handled in the host specific code within build.fsx, such as Azure/Heroku
//startWebServer defaultConfig app
