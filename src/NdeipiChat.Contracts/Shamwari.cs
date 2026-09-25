namespace NdeipiChat.Contracts;

/// <summary>
/// Shamwaris are friends. You add someone by the email or phone number they signed up with; they
/// get a request and become your Shamwari once they accept. Someone not on Ndeipi yet gets a
/// pending invite, which turns into a request when they sign up with that email or number.
/// </summary>
public sealed record ShamwariListDto(
    IReadOnlyList<ShamwariDto> Shamwaris,
    IReadOnlyList<ShamwariRequestDto> Incoming,
    IReadOnlyList<ShamwariRequestDto> Outgoing);

public sealed record ShamwariDto(UserDto User, DateTimeOffset Since);

/// <summary>
/// A request awaiting an answer. <see cref="User"/> is the other person; it's null for an invite
/// to someone not on Ndeipi yet, which <see cref="Contact"/> then names.
/// </summary>
public sealed record ShamwariRequestDto(Guid Id, UserDto? User, string? Contact, DateTimeOffset CreatedAt);

/// <summary><see cref="Contact"/> is an email address, or a phone number with its country code.</summary>
public sealed record AddShamwariRequest(string Contact);

public enum ShamwariAddOutcome
{
    /// <summary>They're on Ndeipi and have been sent a request.</summary>
    RequestSent,

    /// <summary>They'd already asked you, so adding them accepted their request.</summary>
    NowShamwaris,

    /// <summary>No one on Ndeipi uses that email or number yet; the invite waits for them to sign up.</summary>
    Invited,

    AlreadyShamwaris,
    AlreadyRequested
}

public sealed record AddShamwariResponse(ShamwariAddOutcome Outcome, ShamwariListDto List);
