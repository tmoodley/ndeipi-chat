namespace NdeipiChat.App;

public static class AppConfig
{
    /// <summary>
    /// Where NdeipiChat.Api runs. Phones can't reach "localhost" on your PC; during development
    /// expose the API over HTTPS (e.g. `devtunnel host -p 5057 --allow-anonymous`) and put that URL here.
    /// </summary>
    public const string ApiBaseUrl = "https://chat.example.com/";

    /// <summary>Must match MobileAuth:RedirectUris on the API and the URL scheme registered per platform.</summary>
    public const string RedirectUri = "ndeipichat://auth";
}
