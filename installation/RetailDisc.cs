// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Generic;
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
    /// SHA-256 of every Form 1 file on the retail US disc (installation/retail_digests.json), so the installer can
    /// verify whatever it consumes.
    /// </summary>
    private static readonly Dictionary<string, string> RetailFileDigests = LoadDigests();

    private static Dictionary<string, string> LoadDigests()
    {
        var parsed = Godot.Json.ParseString(Godot.FileAccess.GetFileAsString("res://installation/retail_digests.json"));
        var digests = new Dictionary<string, string>(System.StringComparer.Ordinal);
        foreach (var (path, digest) in parsed.AsGodotDictionary()) digests[path.AsString()] = digest.AsString();
        if (digests.Count == 0) throw new System.InvalidOperationException("The retail digest catalog is missing.");
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
}
