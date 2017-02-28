using System.Net;  
using Microsoft.Bot.Connector;

public static async Task<HttpResponseMessage> Run(HttpRequestMessage req, TraceWriter log)
{   
    log.Verbose($"C# HTTP trigger function processed a request. RequestUri={req.RequestUri}");

    var msg = await req.Content.ReadAsAsync<Message>();

    if (msg.Type == "Message")
    {
        var responseMsg = String.Empty;
        if (msg.Text == "Quote")
        {
            Random r = new Random();
            int rInt = r.Next(0,9);
            responseMsg = GetQuote(rInt);
        }
        else
        {
            responseMsg = "What do you want?";
        }
        var reply = msg.CreateReplyMessage($"Bender says: {responseMsg}");
    
        return msg == null 
        ? req.CreateResponse(HttpStatusCode.BadRequest, "Please pass a name on the query string or in the request body") 
        : req.CreateResponse(HttpStatusCode.OK,  reply );
    }
    else
    {
        var sysMsg = HandleSystemMessage(msg);
        
        return sysMsg == null 
        ? req.CreateResponse(HttpStatusCode.BadRequest, "Please pass a name on the query string or in the request body") 
        : req.CreateResponse(HttpStatusCode.OK, sysMsg );
    }        
}

public static Message HandleSystemMessage(Message message)
{
    if(message.Type == "Ping")
    {
        Message reply = message.CreateReplyMessage();
        reply.Type = "Ping";
        return reply;
    }
    else if(message.Type == "DeleteUserData")
    {}
    else if(message.Type == "BotAddedToConversation")
    {
        Message reply = message.CreateReplyMessage();
        reply.Type = "BotAddedToConversation";
        reply.Text = "BotAddedToConversation";
        return reply;
    }
    else if(message.Type == "BotRemovedFromConversation")
    {}
    else if(message.Type == "UserAddedToConversation")
    {}
    else if(message.Type == "UserRemovedFromConversation")
    {}
    else if(message.Type == "EndOfConversation")
    {}
    return null;
}

public static string GetQuote(int i)
{
    switch (i)
    {
        case 0:
        return "Who are you, and why should I care?";
        
        case 1:
        return "Hasta la vista, meatbag!";
        
        case 2:
        return "Do the Bender! Do the Bender! It's your birthday! Do the Bender!";
        
        case 3:
        return "Bite my shiny metal ass!";
        
        case 4:
        return "Well, we're boned.";
        
        case 5:
        return "Hey sexy mama, wanna kill all humans?";
        
        case 6:
        return "Too bad losers whom I've always hated!";
        
        case 7:
        return "Would you kindly shut your noise hole?";
        
        case 8:
        return "Get up and pay attention to me; Bender!";
        
        case 9:
        return "Well, that was dumb...";
        
        default:
        return "Oh noes, out of quotes!";
    }
}
