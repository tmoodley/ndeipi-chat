namespace NdeipiChat.Web.Platform;

/// <summary>
/// Publisher keys whose signed sub-apps this shell runs (SRS NFR-02-01). Compiled in on purpose:
/// a key fetched at runtime would come from the same server as the bundles it vouches for. To rotate,
/// add the new key here, publish, re-sign with the new key, then remove the old one.
/// </summary>
public static class PublisherKeys
{
    /// <summary>Ndeipi's sub-app publisher key, id 91d1496634a7ec8e (made 2026-09-27).</summary>
    public const string Ndeipi = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEHVwvrH6tc4a4kIDlY2k7x5KJtf8JqRmwZlYXHuQPK+piL+qG3udMlcuHWX30Ow7ED+ZRGgaZvjDr/V/SsCnrmA==";

    public static readonly string[] All = [Ndeipi];
}
