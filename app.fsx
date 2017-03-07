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
#r "/Users/Ashley/code/fsharpstarterbot/packages/System.IdentityModel.Tokens.Jwt/lib/net45/System.IdentityModel.Tokens.Jwt.dll"
#load @"packages/SwaggerProvider/SwaggerProvider.fsx"
open SwaggerProvider

// open Encodings
open System
open System.Security.Claims
open System.IdentityModel.Tokens
open System.Security.Cryptography
// open System.Globalization.Encodings
//System.IdentityModel.Tokens.Jwt

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

let [<Literal>]schema = __SOURCE_DIRECTORY__ + "/botConnectorv3.json"
type BotConnector = SwaggerProvider<schema, "Content-Type=application/json">    

let envAsOption (envVarName : string) =
    let envVarValue = Environment.GetEnvironmentVariable(envVarName)
    if ((isNull envVarValue) || (envVarValue.Trim().Length = 0)) then None else Some envVarValue

let appId = defaultArg (envAsOption "MicrosoftAppID") "John"
let appSecret = defaultArg (envAsOption "MicrosoftAppPassword") "Secret"

[<AutoOpen>]
module Helpers = 

    type JwtConfig = {
        Issuer : string
        SecurityKey : SecurityKey
        ClientId : string
    }

    type TokenValidationRequest = {
        Issuer : string
        SecurityKey : SecurityKey
        ClientId : string
        AccessToken : string
    }

    let toJsonWeb v =
        let jsonSerializerSettings = new JsonSerializerSettings()
        let contractResolver = DefaultContractResolver()
        // contractResolver.NamingStrategy <- new SnakeCaseNamingStrategy();
        jsonSerializerSettings.ContractResolver <- contractResolver
        JsonConvert.SerializeObject(v, jsonSerializerSettings)
    let toJson v =
        let jsonSerializerSettings = new JsonSerializerSettings()
        jsonSerializerSettings.ContractResolver <- new CamelCasePropertyNamesContractResolver()

        JsonConvert.SerializeObject(v, jsonSerializerSettings) |> OK
        >=> Writers.setMimeType "application/json; charset=utf-8"

    let fromJson<'a> json =
        JsonConvert.DeserializeObject(json, typeof<'a>) :?> 'a

    let jwtAuthenticate jwtConfig webpart (ctx: HttpContext) =

        let updateContextWithClaims claims =
            { ctx with userState = ctx.userState.Remove("Claims").Add("Claims", claims) }

        match ctx.request.header "token" with
        | Choice1Of2 accessToken ->
            let tokenValidationRequest =  {
                Issuer = jwtConfig.Issuer
                SecurityKey = jwtConfig.SecurityKey
                ClientId = jwtConfig.ClientId
                AccessToken = accessToken
            }
            let validationResult = validate tokenValidationRequest
            match validationResult with
            | Choice1Of2 claims -> webpart (updateContextWithClaims claims)
            | Choice2Of2 err -> FORBIDDEN err ctx

        | _ -> BAD_REQUEST "Invalid Request. Provide both clientid and token" ctx

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

    let tokenHolder msftAppId msftPassword = 

        let getToken () = 
            let formData = [
                "grant_type=client_credentials"
                (sprintf "client_id=%s" msftAppId)
                (sprintf "client_secret=%s" msftPassword) 
                "scope=https://api.botframework.com/.default" ]
                
            Request.createUrl Post "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token"
            |> Request.bodyString (String.Join("&", formData))
            |> Request.setHeader (ContentType (ContentType.create("application", "x-www-form-urlencoded")))
            |> (fun x -> printfn "Making request: %A" x; x)
            |> Request.responseAsString
            |> run
            |> (fun x -> printfn "Repsonse: %A" x; x)
            |> fromJson<MicrosfotTokenResposne>
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

    let getToken msftAppId msftPassword = 
        let token = tokenHolder msftAppId msftPassword
        fun () -> token.PostAndReply Fetch

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
                        Type = activity.Type,
                        Value = activity.Value,
                        LocalTimestamp = (activity.LocalTimestamp |> Option.ofNullable ))
                        
let botHandler (msftToken: unit -> BotAuth.Token) (message : Activity) =
    printfn "Received message of type %s: %s" message.Type message.Text
    printfn "Token expires in: %A" ((msftToken().ExpiresUtc) - DateTime.UtcNow)

    let botConnector = BotConnector(message.ServiceUrl.Replace("http://", ""), 
                                    CustomizeHttpRequest =
                                        fun (req:System.Net.HttpWebRequest) -> 
                                            let token = msftToken ()
                                            // req.Headers.Add(sprintf "Authorization: Bearer %s" token.AccessToken)
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
                    botConnector.ConversationsReplyToActivity(message.Conversation.Id, message.Id, activity) //.ToString()
                    |> (fun x -> printfn "Response: %A" x.Id)

        | "deleteuserData"
        | "botAddedToConversation"
        | "BotRemovedFromConversation"
        | "UserRemovedFromConversation"
        | "EndOfConversation"
        | _ -> return ()
    } |> Async.Start


/// Suave application
let app = 
    choose [ 
        path "/" >=> OK "Hello World!" 
        path "/api/messages" >=> (request validateJwtToken) >=> request (getResourceFromReq >> botHandler (BotAuth.getToken appId appSecret) >> toJson ) ]
            //  Authentication.authenticateBasic ((=) (appId, appSecret)) <|
            //      choose [

// This is handled in the host specific code within build.fsx, such as Azure/Heroku
//startWebServer defaultConfig app
