namespace NdeipiChat.Contracts;

/// <summary>
/// Moo Ville, the farm game: register cattle and goats, turn manure and urine into compost and
/// bio-fertilizer, run regenerative practices and mint carbon credits on a solar NodeBoss node. It
/// runs entirely on the device (NodeBoss is simulated) and keeps each player's farm in the shell's
/// per-user device storage, so it has no API module yet.
/// </summary>
public static class FarmContract
{
    public const string AppId = "farm";

    /// <summary>The device value the farm is saved under; private to this app and the signed-in user.</summary>
    public const string SaveKey = "farm.v1";
}
