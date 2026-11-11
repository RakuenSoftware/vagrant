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
            Text = "Vagrant Story",
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
