// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using Wol.Games.Vagrant.Runtime;
using Wol.Games.Vagrant.Scenes;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Content packs stand game files in for the installed ones and never change the installation. Arguments:
/// <c>--install-root=</c> (an installed disc) and a disposable <c>--pack-root=</c>, which this check empties. A fixture
/// pack is made at run time from the installation's own MENU/ITEMNAME.BIN with item 1's name changed (nothing from the
/// disc is kept): installed it changes nothing until enabled; enabled, the game reads the pack's file; switched off in
/// the Settings window's Packs tab, the game reads the disc's again; the installed file stays byte for byte. With
/// <c>--pack-zip=</c> a real pack (one <c>tools/ppf_pack.py</c> built, say) is installed and enabled too: every file it
/// names is read from it, the game's content loads through it (the intro movie played from the pack's when it has one),
/// and switched off the disc's files are read again.
/// </summary>
public partial class PackContracts : Node
{
    public override void _Ready()
    {
        try
        {
            Run();
            GD.Print("PACK_CONTRACTS_OK: an installed pack changes nothing until enabled; enabled, its game file is read in place of the disc's; switched off in the Packs tab, the disc's is read again; the installation is never changed");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PrintErr("PACK_CONTRACTS_FAILED: " + error);
            GetTree().Quit(1);
        }
    }

    private static string ItemName(RetailContent content, int id) => content.Field.Data("item_names")!["list"]![id]!.GetValue<string>();

    private void Run()
    {
        var content = GetNode<RetailContent>("/root/RetailContent");
        if (content.PackRoot == "user://packs") throw new InvalidOperationException("pass a disposable --pack-root=");
        if (!content.Installed) throw new InvalidOperationException("pass --install-root= of an installed disc: " + content.Failure);
        string packs = ProjectSettings.GlobalizePath(content.PackRoot);
        if (Directory.Exists(packs)) Directory.Delete(packs, true);
        content.EnablePacks([]);

        string installed = Path.Combine(content.GlobalRoot, "disc", "MENU", "ITEMNAME.BIN");
        byte[] disc = File.ReadAllBytes(installed);
        string digest = Convert.ToHexStringLower(SHA256.HashData(disc)), original = ItemName(content, 1);
        // Item 1's name (24-byte records) becomes "Prueba": P r u e b a in the game's glyph codes, then the terminator.
        byte[] changed = (byte[])disc.Clone();
        new byte[] { 25, 53, 56, 40, 37, 36, 0xE7 }.CopyTo(changed, 24);

        string zip = Path.Combine(OS.GetUserDataDir(), "pack-contract-fixture.zip");
        if (File.Exists(zip)) File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pack.json").Open()))
                writer.Write("{\"schema\":1,\"id\":\"fixture\",\"name\":\"Fixture\",\"assets\":{\"disc/MENU/ITEMNAME.BIN\":{\"file\":\"MENU/ITEMNAME.BIN\"}}}");
            using var stream = archive.CreateEntry("MENU/ITEMNAME.BIN").Open();
            stream.Write(changed);
        }
        if (content.InstallPack(zip) != "fixture") throw new InvalidOperationException("The fixture pack did not install under its ID.");
        if (ItemName(content, 1) != original) throw new InvalidOperationException("An installed pack must change nothing until enabled.");

        content.EnablePacks(["fixture"]);
        if (ItemName(content, 1) != "Prueba") throw new InvalidOperationException($"Enabled, the pack's file must be read: item 1 is {ItemName(content, 1)}.");

        // The Settings window's Packs tab: the pack's switch turned off applies when the window closes.
        var settings = new SettingsDialog { Name = "Settings" };
        AddChild(settings);
        bool applied = false;
        settings.PacksApplied += () => applied = true;
        settings.Open();
        var toggle = settings.GetNode<CheckButton>("Tabs/Packs/Packs/List/fixture/Enabled");
        if (!toggle.ButtonPressed) throw new InvalidOperationException("The Packs tab must show the enabled pack switched on.");
        toggle.ButtonPressed = false;
        settings.Hide();
        if (!applied || content.Packs.Enabled.Count != 0 || ItemName(content, 1) != original) throw new InvalidOperationException("Switched off in the Packs tab, the disc's file must be read again.");

        if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(installed))) != digest) throw new InvalidOperationException("A pack changed the installed file.");

        string real = OS.GetCmdlineUserArgs().FirstOrDefault(argument => argument.StartsWith("--pack-zip=", StringComparison.Ordinal))?["--pack-zip=".Length..] ?? "";
        if (real.Length > 0)
        {
            string id = content.InstallPack(real);
            content.EnablePacks([id]);
            if (!content.Installed) throw new InvalidOperationException("The game's content did not load through the pack: " + content.Failure);
            var pack = content.Packs.Packs.Single(known => known.Id == id);
            foreach (var (key, asset) in pack.Assets)
                if (content.Field.DiscFile(key["disc/".Length..]) != asset.Path) throw new InvalidOperationException($"{key} is not read from the pack.");
            string video = content.IntroVideo.File;
            if (pack.Assets.TryGetValue("disc/MOV/TITLE.STR", out var movie) && video != movie.Path)
                throw new InvalidOperationException("The intro movie must play the pack's MOV/TITLE.STR: " + video);
            GD.Print($"PACK_CONTRACTS_REAL: {id} stands in {pack.Assets.Count} files; intro movie {video}");
            content.EnablePacks([]);
            foreach (var key in pack.Assets.Keys)
                if (content.Field.DiscFile(key["disc/".Length..]) != Path.Combine(content.GlobalRoot, "disc", key["disc/".Length..])) throw new InvalidOperationException($"Switched off, {key} must be read from the disc.");
        }
        Directory.Delete(packs, true);
        File.Delete(zip);
    }
}
