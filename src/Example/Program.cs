// See https://aka.ms/new-console-template for more information

using Phoenix.ApiWrapper;
using Phoenix.ApiWrapper.Entities;

var phoenix = new PhoenixClients(
    new HttpClient() { Timeout = TimeSpan.FromSeconds(10) },
    new PhoenixApiClientOptions
    {
        TokenEndpoint = new Uri("https://accounts.phoenixnetwork.net/oauth/token"),
        ClientId = "your-client-id",
        ClientSecret = "your-client-secret",
        Scopes = [],
    });

var exchanged = await phoenix.ExchangeOnBehalfOfAsync("", "");
