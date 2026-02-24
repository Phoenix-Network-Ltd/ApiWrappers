// See https://aka.ms/new-console-template for more information

using Phoenix.ApiWrapper;
using Phoenix.ApiWrapper.Entities;

var phoenix = new PhoenixApiClient(
    new HttpClient(),
    new PhoenixApiClientOptions
    {
        TokenEndpoint = new Uri("https://accounts.phoenixnetwork.net/oauth/token"),
        ClientId = "your-client-id",
        ClientSecret = "your-client-secret",
        GalaxyLifeBaseUrl = new Uri("https://api.galaxylifegame.net"),
        GalaxyLifeScopes = [],
    });

var alliances = await phoenix.GalaxyLife.Alliances.GetPath.GetAsync();

var exchanged = await phoenix.ExchangeOnBehalfOfAsync("", "");

