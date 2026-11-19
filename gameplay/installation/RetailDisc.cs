// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
using System.Text.Json;
using Wol.Engine.Install;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Vagrant Story's retail US disc (SLUS-01040): the identity and per-file digests the shared engine's
/// <see cref="PlayStationDisc"/> verifies, so only a genuine, unmodified retail disc can be installed.
/// </summary>
public static class RetailDisc
{
    public const string VolumeIdentifier = "SLUS-01040";

    /// <summary>
    /// SHA-256 of every Form 1 file on the retail US disc (gameplay/installation/retail_digests.json), so the installer can
    /// verify whatever it consumes.
    /// </summary>
    private static readonly Dictionary<string, string> RetailFileDigests = LoadDigests();

    private static Dictionary<string, string> LoadDigests()
    {
        using var source = typeof(RetailDisc).Assembly.GetManifestResourceStream("Wol.Games.Vagrant.Installation.RetailDigests")
            ?? throw new System.InvalidOperationException("The retail digest catalog is missing.");
        var digests = JsonSerializer.Deserialize<Dictionary<string, string>>(source);
        if (digests is not { Count: > 0 }) throw new System.InvalidOperationException("The retail digest catalog is missing.");
        return digests;
    }

    /// <summary>
    /// SHA-256 over bytes 16..2352 of every raw sector of the streamed files (subheader, payload and
    /// error correction). The sector header is excluded because it only encodes the disc address.
    /// </summary>
    private static readonly Dictionary<string, string> RetailStreamDigests = new()
    {
        ["MOV/TITLE.STR"] = "27eda83a2d4da6c5ee3260c9c3250e5dadb754b276c28a714b64f52b3934f260",
    };

    /// <summary>The US disc as the shared engine verifies it: identity, boot line and the digests above.</summary>
    public static readonly PlayStationDisc.Catalog Catalog = new("Vagrant Story (US, SLUS-01040)", VolumeIdentifier, "BOOT = cdrom:\\SLUS_010.40;1", RetailFileDigests, RetailStreamDigests);

    /// <summary>
    /// The game's rule for content packs: a pack may stand in a whole game file for one the game reads, keyed
    /// <c>disc/</c> and its disc path (<c>disc/MENU/ITEMHELP.BIN</c>, <c>disc/MOV/TITLE.STR</c> in raw sectors as the
    /// installation keeps streams). The installed file is never changed; the game reads the pack's in its place while
    /// the pack is enabled. Nothing else is replaceable.
    /// </summary>
    public static ContentFormat? PackFormat(string key) =>
        key.StartsWith("disc/", System.StringComparison.Ordinal) && key[5..] is var path
        && (RetailFileDigests.ContainsKey(path) || RetailStreamDigests.ContainsKey(path)) ? ContentFormat.File : null;
}
