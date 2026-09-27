using NdeipiChat.Contracts;
using NdeipiChat.SubAppSigner;

// Signs sub-app bundles with the publisher key (SRS NFR-02-01). See docs/sub-apps.md.
//
//   keygen --out <private key file>
//       Makes a publisher key. Keep the private key off the server and out of the repo; put the
//       printed public key in the shell's trusted keys (src/NdeipiChat.Web/Platform/SubAppTrust.cs).
//
//   sign --site <published wwwroot> --key <private key file> [--out <signatures file>]
//       Signs every sub-app bundle in the published site. The API publish runs this itself.

return args switch
{
    ["keygen", "--out", var path] => KeyGen(path),
    ["sign", .. var rest] when Option(rest, "--site") is { } site && Option(rest, "--key") is { } key =>
        Sign(site, key, Option(rest, "--out") ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(site))!, SubAppSigning.SignaturesFileName)),
    _ => Usage()
};

static int KeyGen(string path)
{
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} already exists. Refusing to overwrite a publisher key.");
        return 1;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    var (privateKey, publicKey) = SubAppBundleSigner.CreateKey();
    File.WriteAllText(path, privateKey);
    Console.WriteLine($"Private key: {Path.GetFullPath(path)} (back it up; never commit it or copy it to the server)");
    Console.WriteLine($"Key id:      {SubAppSigning.KeyId(publicKey)}");
    Console.WriteLine($"Public key:  {publicKey}");
    return 0;
}

static int Sign(string site, string keyPath, string output)
{
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"No publisher key at {keyPath}.");
        return 1;
    }
    if (!Directory.Exists(Path.Combine(site, "_framework")))
    {
        Console.Error.WriteLine($"No _framework folder in {site}. Point --site at the published wwwroot.");
        return 1;
    }
    var signatures = SubAppBundleSigner.SignSite(site, File.ReadAllText(keyPath));
    File.WriteAllText(output, SubAppBundleSigner.ToJson(signatures));
    foreach (var entry in signatures.Signatures)
        Console.WriteLine($"Signed {entry.Assembly} ({entry.Sha256[..12]}...) with key {entry.KeyId}");
    Console.WriteLine($"Wrote {output}");
    return signatures.Signatures.Count > 0 ? 0 : 2;
}

static string? Option(IReadOnlyList<string> args, string name)
{
    for (var i = 0; i < args.Count - 1; i++)
        if (args[i] == name)
            return args[i + 1];
    return null;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: keygen --out <file> | sign --site <wwwroot> --key <file> [--out <file>]");
    return 64;
}
