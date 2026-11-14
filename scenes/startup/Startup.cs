// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.IO;
using System.Threading.Tasks;
using Godot;
using Wol.Engine;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Startup;

/// <summary>
/// First run: the player installs from their own US Vagrant Story disc. Without a verified
/// installation the game does not start. <c>--install-disc=PATH</c> installs headlessly and exits.
/// </summary>
public partial class Startup : Control
{
    public const string TitleScene = "res://scenes/title/title.tscn";

    private RetailContent _content = null!;
    private Label _message = null!;
    private LineEdit _path = null!;
    private Button _choose = null!, _install = null!;
    private FileDialog _picker = null!;
    private Task<string>? _worker;

    public override void _Ready()
    {
        _content = GetNode<RetailContent>("/root/RetailContent");
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--verify-package") >= 0)
        {
            try
            {
                if (OS.HasFeature("editor")) throw new InvalidOperationException("Verify the exported executable, not the editor.");
                string legacyData = System.IO.Path.Combine(OS.GetDataDir(), OperatingSystem.IsLinux() ? "godot" : "Godot", "app_userdata", "Vagrant");
                if (!System.IO.Path.GetFullPath(OS.GetUserDataDir()).Equals(System.IO.Path.GetFullPath(legacyData),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("The renamed player must retain existing installations, saves and preferences.");
                if (!ClassDB.ClassExists("NativePs1AudioSandbox")) throw new InvalidOperationException("The compiled engine audio runtime is missing.");
                if (typeof(Wol.Engine.World.FieldScene).Assembly == typeof(Startup).Assembly)
                    throw new InvalidOperationException("The engine must be a separately compiled binary dependency.");
                foreach (var type in typeof(Startup).Assembly.GetTypes())
                    if (type.Namespace == "Wol.Games.Vagrant.Tests") throw new InvalidOperationException("Test code was compiled into the player.");
                var retailHashes = new System.Collections.Generic.HashSet<string>(RetailDisc.Catalog.FileDigests.Values, StringComparer.OrdinalIgnoreCase);
                var retailFormats = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in RetailDisc.Catalog.FileDigests.Keys) retailFormats.Add(System.IO.Path.GetExtension(name));
                foreach (string name in RetailDisc.Catalog.StreamDigests.Keys) retailFormats.Add(System.IO.Path.GetExtension(name));
                retailFormats.UnionWith(new[] { ".xa", ".cue", ".iso", ".chd", ".img" });
                var directories = new System.Collections.Generic.Stack<string>();
                directories.Push("res://");
                while (directories.TryPop(out var directory))
                {
                    using var resources = DirAccess.Open(directory) ?? throw new InvalidOperationException("Cannot audit resource directory: " + directory);
                    resources.IncludeHidden = true;
                    foreach (string child in resources.GetDirectories())
                        if (child is not "." and not "..") directories.Push(directory.PathJoin(child));
                    foreach (string file in resources.GetFiles())
                    {
                        string path = directory.PathJoin(file), extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                        if (path.StartsWith("res://tests/", StringComparison.Ordinal) || path.Contains("/.dependencies/", StringComparison.Ordinal)
                            || path.Contains("/disc/", StringComparison.Ordinal) || path.Contains("/cache/", StringComparison.Ordinal)
                            || path.Contains("/captures/", StringComparison.Ordinal) || path.Contains("/screenshots/", StringComparison.Ordinal)
                            || extension is ".cpp" or ".hpp" or ".h" or ".pdb" or ".cue" or ".iso")
                            throw new InvalidOperationException("Development/disc content in player: " + path);
                        // Godot requires C# resource paths but exports only newline stubs when scripts_content=false.
                        if (extension == ".cs" && !string.IsNullOrWhiteSpace(Godot.FileAccess.GetFileAsString(path)))
                            throw new InvalidOperationException("C# source in player: " + path);
                        // Godot's UID index is generated metadata, not an installed disc BIN.
                        if (retailFormats.Contains(extension) && path != "res://.godot/uid_cache.bin")
                            throw new InvalidOperationException("Retail disc file format in player: " + path);
                        byte[] bytes = Godot.FileAccess.GetFileAsBytes(path);
                        bool rawSector = bytes.Length >= 12 && bytes[0] == 0 && bytes[11] == 0;
                        for (int i = 1; i <= 10 && rawSector; i++) rawSector = bytes[i] == 0xFF;
                        if (rawSector || bytes.AsSpan().StartsWith("PS-X EXE"u8)
                            || bytes.Length >= 32 && retailHashes.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))))
                            throw new InvalidOperationException("Retail disc content/signature in player: " + path);
                    }
                }
                GD.Print("PACKAGE_OK: compiled engine present; player assembly/resources contain no tests, source or disc data");
                GetTree().Quit();
            }
            catch (Exception error) { GD.PrintErr("PACKAGE_FAILED: " + error.Message); GetTree().Quit(1); }
            return;
        }
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (!argument.StartsWith("--install-disc=", StringComparison.Ordinal)) continue;
            string error;
            try
            {
                error = Install(argument["--install-disc=".Length..], message => GD.Print(message));
            }
            catch (Exception failure)
            {
                // A command-line install always ends, so an unexpected failure cannot leave it running.
                GD.PrintErr("INSTALL_FAILED: " + failure);
                GetTree().Quit(1);
                return;
            }
            if (error.Length != 0) GD.PrintErr("INSTALL_REJECTED: " + error);
            else GD.Print("INSTALL_OK: " + _content.GlobalRoot);
            GetTree().Quit(error.Length == 0 ? 0 : 1);
            return;
        }
        if (_content.Installed)
        {
            CallDeferred(MethodName.StartTitle);
            return;
        }
        BuildInstaller();
    }

    private void StartTitle()
    {
        var scenes = GetNode<SceneManager>("/root/SceneManager");
        // Scene changes show retail's own loading picture, centred on the 320 by 240 screen.
        try { scenes.SetLoadingImage(_content.LoadingImage(), new Vector2(320, 240), Scenes.Field.FieldView.PixelAspect); }
        catch (System.IO.InvalidDataException error) { GD.PushWarning(error.Message); }
        _ = scenes.ChangeScene(TitleScene);
    }

    private void BuildInstaller()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });
        var centre = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        AddChild(centre);
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(760, 0) };
        column.AddThemeConstantOverride("separation", 16);
        centre.AddChild(column);
        column.AddChild(new Label
        {
            Text = "Project Vagrant — unofficial Vagrant Story runtime",
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        column.AddChild(new Label
        {
            Text = "This game runs only from your own copy of Vagrant Story (US, SLUS-01040).\n"
                + "Choose your disc image (BIN or CUE). Its files are checked against the retail disc, "
                + "and copied to this computer as the disc holds them. Nothing from the disc is included with this game.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });
        var row = new HBoxContainer();
        column.AddChild(row);
        _path = new LineEdit { PlaceholderText = "Disc image (BIN or CUE)", SizeFlagsHorizontal = SizeFlags.ExpandFill, Editable = false };
        row.AddChild(_path);
        _choose = new Button { Text = "Choose disc…" };
        row.AddChild(_choose);
        _install = new Button { Text = "Install from disc", Disabled = true };
        column.AddChild(_install);
        _message = new Label { Text = _content.Failure, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        column.AddChild(_message);
        _picker = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.cue, *.bin ; Disc image" },
            Title = "Choose your Vagrant Story disc",
            UseNativeDialog = true,
        };
        AddChild(_picker);
        _choose.Pressed += () => _picker.PopupCenteredRatio(0.75f);
        _picker.FileSelected += path =>
        {
            _path.Text = path;
            _install.Disabled = false;
        };
        _install.Pressed += BeginInstall;
        _choose.GrabFocus();
    }

    private void BeginInstall()
    {
        _install.Disabled = true;
        _choose.Disabled = true;
        _message.Text = "Installing from your disc. This takes a minute…";
        string disc = _path.Text;
        _worker = Task.Run(() => Install(disc, message => Callable.From(() => _message.Text = message).CallDeferred()));
    }

    public override void _Process(double delta)
    {
        if (_worker == null || !_worker.IsCompleted) return;
        string error = _worker.Result;
        _worker = null;
        if (error.Length != 0)
        {
            _message.Text = "Installation was not completed.\n\n" + error;
            _install.Disabled = false;
            _choose.Disabled = false;
            return;
        }
        _content.Reload();
        if (!_content.Installed)
        {
            _message.Text = _content.Failure;
            _install.Disabled = false;
            _choose.Disabled = false;
            return;
        }
        StartTitle();
    }

    /// <summary>Returns an empty string on success, otherwise a player-facing reason.</summary>
    private string Install(string disc, Action<string> progress)
    {
        try
        {
            new RetailInstallation(_content.GlobalRoot).Install(disc, progress);
            _content.CallDeferred(RetailContent.MethodName.Reload);
            return "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }
}
