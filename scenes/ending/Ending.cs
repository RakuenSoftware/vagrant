// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using Wol.Engine;
using Wol.Games.Vagrant.Runtime;

namespace Wol.Games.Vagrant.Scenes.Ending;

/// <summary>
/// The ending: the credits (from ENDING.PRG) roll up over the ending's illustrations, which fade from one to the next,
/// and the game then returns to the title. Retail's own timing and layout (ENDING.PRG's script) are approximated:
/// each line moves up by the step its script gives, the credits roll at 24 pixels a second on the 320 by 240
/// display, and the illustrations share that time evenly.
/// </summary>
public partial class Ending : Control
{
    public const string Scene = "res://scenes/ending/ending.tscn";
    private const float RollSpeed = 24, Display = 240;

    private readonly List<TextureRect> _pictures = new();
    private Control _roll = null!;
    private float _length, _elapsed;

    /// <summary>Whether the credits have run out (the title follows).</summary>
    public bool Finished { get; private set; }

    public override void _Ready()
    {
        var content = GetNode<RetailContent>("/root/RetailContent");
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1 });
        var data = content.Data("ending");
        foreach (var asset in data["illustrations"].AsGodotArray())
        {
            var picture = new TextureRect { Texture = content.ScreenTexture(asset.AsString()), StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, AnchorRight = 1, AnchorBottom = 1, Modulate = new Color(1, 1, 1, 0) };
            AddChild(picture);
            _pictures.Add(picture);
        }
        _roll = new Control { AnchorRight = 1, AnchorBottom = 1 };
        AddChild(_roll);
        float y = Display;
        foreach (var value in data["credits"].AsGodotArray())
        {
            var line = value.AsGodotDictionary();
            y += Math.Max(8, line["step"].AsInt32());
            var label = new Label { Text = line["text"].AsString(), HorizontalAlignment = HorizontalAlignment.Center, AnchorRight = 1, Position = new Vector2(0, y) };
            label.AddThemeFontSizeOverride("font_size", line["heading"].AsBool() ? 9 : 11);
            label.AddThemeColorOverride("font_color", line["heading"].AsBool() ? new Color(0.85f, 0.78f, 0.6f) : Colors.White);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
            label.AddThemeConstantOverride("outline_size", 3);
            _roll.AddChild(label);
        }
        _length = (y + Display) / RollSpeed;
    }

    public override void _Process(double delta)
    {
        if (Finished) return;
        _elapsed += (float)delta;
        // The roll is laid out on the 320 by 240 display and drawn at the window's scale.
        float scale = Size.Y / Display;
        _roll.Scale = new Vector2(scale, scale);
        _roll.Size = new Vector2(Size.X / scale, Display);
        _roll.Position = new Vector2(0, -_elapsed * RollSpeed * scale);
        // Each illustration holds its share of the roll, fading in and out over two seconds.
        if (_pictures.Count > 0)
        {
            float share = _length / _pictures.Count;
            for (int i = 0; i < _pictures.Count; i++)
            {
                float local = _elapsed - i * share;
                float alpha = Math.Clamp(Math.Min(local, share - local) / 2, 0, 1) * 0.6f;
                _pictures[i].Modulate = new Color(1, 1, 1, alpha);
            }
        }
        if (_elapsed < _length && !Input.IsActionJustPressed("ps_start")) return;
        Finished = true;
        if (!KeepScene) _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(Wol.Games.Vagrant.Scenes.Startup.Startup.TitleScene);
    }

    /// <summary>Keeps the ending in place when it finishes (a test that holds the scene sets it).</summary>
    public bool KeepScene;
}
