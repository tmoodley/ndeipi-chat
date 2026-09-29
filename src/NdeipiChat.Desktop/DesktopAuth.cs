using System.Net;
using System.Net.Sockets;
using System.Text;
using NdeipiChat.Client;
using NdeipiChat.Contracts;

namespace NdeipiChat.Desktop;

/// <summary>
/// The sign-in, in the platform's secure storage (DPAPI on Windows, the Keychain on the Mac). If
/// that isn't available (an unsigned Mac build has no Keychain access group), it's kept for this
/// run only and the next launch asks to sign in again.
/// </summary>
public sealed class DesktopTokenStore : ITokenStore
{
    const string Key = "ndeipi.desktop.tokens";
    TokenResponse? _memory;

    public async Task<TokenResponse?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? _memory : ContractJson.Read<TokenResponse>(json);
        }
        catch (Exception)
        {
            return _memory;
        }
    }

    public async Task SaveAsync(TokenResponse tokens)
    {
        _memory = tokens;
        try
        {
            await SecureStorage.Default.SetAsync(Key, ContractJson.Write(tokens));
        }
        catch (Exception)
        {
            // No secure storage here; the in-memory copy serves this run.
        }
    }

    public Task ClearAsync()
    {
        _memory = null;
        try
        {
            SecureStorage.Default.Remove(Key);
        }
        catch (Exception)
        {
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Windows: opens sign-in in the default browser and waits for it to come back to
/// http://127.0.0.1:47832/auth, a listener on this PC only (RFC 8252's loopback redirect; PKCE
/// protects the code). A raw socket rather than HttpListener, which would need an admin URL ACL.
/// </summary>
public sealed class LoopbackAuthenticator : IBrowserAuthenticator
{
    public const int Port = 47832;
    public const string CallbackUrl = "http://127.0.0.1:47832/auth";

    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, Port);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            throw new InvalidOperationException("Another sign-in is already waiting. Finish it in your browser, or restart Ndeipi.");
        }

        try
        {
            await Launcher.Default.OpenAsync(url);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var target = await ReadRequestTargetAsync(stream, ct);
                if (target is null || !target.StartsWith("/auth", StringComparison.Ordinal))
                {
                    await RespondAsync(stream, "404 Not Found", "Not here.", ct);
                    continue;
                }

                var query = new Uri(new Uri("http://127.0.0.1"), target).Query.TrimStart('?')
                    .Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Split('=', 2))
                    .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "");
                await RespondAsync(stream, "200 OK",
                    "<!doctype html><meta charset=utf-8><title>Ndeipi</title><body style=\"font-family:system-ui;text-align:center;padding:64px;color:#16181D;background:#EEF1F8\">" +
                    "<h1>You're signed in</h1><p>You can close this tab and go back to Ndeipi.</p></body>", ct);
                return query;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, ct);
        var head = Encoding.ASCII.GetString(buffer, 0, read);
        var line = head.Split("\r\n", 2)[0].Split(' ');
        return line is ["GET", var target, ..] ? target : null;
    }

    static async Task RespondAsync(NetworkStream stream, string status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }
}

/// <summary>The Mac: ASWebAuthenticationSession, returning to ndeipichat://auth.</summary>
public sealed class SystemBrowserAuthenticator : IBrowserAuthenticator
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct)
    {
        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = url,
            CallbackUrl = callbackUri,
            PrefersEphemeralWebBrowserSession = false
        });
        return result.Properties;
    }
}
