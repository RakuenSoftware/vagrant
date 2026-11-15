// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Godot;
using Wol.Engine.Display;
using Wol.Engine.Ui;
using Wol.Games.Vagrant.Installation;

namespace Wol.Games.Vagrant.Runtime;

/// <summary>
/// Loads the content installed from the player's own disc. Without a verified installation
/// nothing from Vagrant Story is available and the game stays on its installer screen.
/// </summary>
public partial class RetailContent : Node
{
    // Register the separately compiled engine before Godot creates its autoloads.
    static RetailContent() => Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(typeof(Wol.Engine.SceneManager).Assembly);

    public const string DefaultRoot = "user://vagrant-story";

    public string Root { get; private set; } = "";
    /// <summary>Empty when a complete installation is loaded; otherwise the reason the game cannot start.</summary>
    public string Failure { get; private set; } = "Vagrant Story has not been installed from your disc yet.";
    public bool Installed => Failure.Length == 0;

    public Texture2D Publisher { get; private set; } = null!;
    public Texture2D Developer { get; private set; } = null!;
    public Texture2D TitleBackground { get; private set; } = null!;
    public Texture2D TitlePanel { get; private set; } = null!;
    public Texture2D TitleCopyright { get; private set; } = null!;
    public Image[] TitleWords { get; private set; } = Array.Empty<Image>();
    public Texture2D[] TitleGlows { get; private set; } = Array.Empty<Texture2D>();
    public Vector2I[] TitleGlowOrigins { get; private set; } = Array.Empty<Vector2I>();
    public ushort[][] TitlePalettes { get; private set; } = Array.Empty<ushort[]>();
    public Vector2I PublisherOrigin { get; private set; }
    public Vector2I DeveloperOrigin { get; private set; }
    public Vector2I CopyrightOrigin { get; private set; }
    public Vector2I PanelOrigin { get; private set; }
    public VideoStream IntroVideo { get; private set; } = null!;
    public AudioStream IntroMusic { get; private set; } = null!;
    /// <summary>The ending's verified XA stream, decoded on first use from the owned installation.</summary>
    public AudioStream EndingMusic() => AudioStreamOggVorbis.LoadFromFile(new RetailInstallation(GlobalRoot).EndingMusic());

    public override void _Ready()
    {
        Root = DefaultRoot;
        foreach (string argument in OS.GetCmdlineUserArgs())
            if (argument.StartsWith("--install-root=", StringComparison.Ordinal)) Root = argument["--install-root=".Length..];
        Reload();
        // The display settings may be added after this node, so their signal is joined once both are in the tree.
        Callable.From(() => { if (GetNodeOrNull<DisplaySettings>("/root/DisplaySettings") is { } display) display.AssetScalingChanged += Rescale; }).CallDeferred();
    }

    public override void _ExitTree()
    {
        // Release managed references before Godot's exit-time leak check.
        IntroMusic?.Dispose();
        IntroVideo?.Dispose();
        IntroMusic = null!;
        IntroVideo = null!;
        // Godot's collections free their native memory from their finalizers. Run every pending one now, while the
        // engine is whole: one left to the finalizer thread can run during the engine's teardown and lock a mutex
        // that is already gone, which crashed the process at exit after its work was done.
        _field = null;
        _effectData.Clear();
        _effectTextures.Clear();
        foreach (var rig in _rigs.Values) rig.Free();
        _rigs.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public string GlobalRoot => ProjectSettings.GlobalizePath(Root);

    /// <summary>An image decoded from the disc, by the name the document that has it gives.</summary>
    public Image LoadImage(string name) =>
        Field.Image(name) is { } pixels ? PixelArtScaler.FromRaster(pixels) : throw new InvalidDataException($"The disc has no image {name}.");

    private Texture2D LoadTexture(string name) => ImageTexture.CreateFromImage(LoadImage(name));

    /// <summary>Whether the disc has a document, image or model of that name.</summary>
    public bool Has(string name)
    {
        if (name.StartsWith("character_", StringComparison.Ordinal) || name.StartsWith("weapon_", StringComparison.Ordinal)) return Field.HasModel(name);
        return Field.Data(name) != null || Field.Image(name) != null;
    }

    /// <summary>A verified disc file as the disc holds it, by its disc path.</summary>
    public byte[] Disc(string path) => Field.Read(path);

    /// <summary>The picture retail shows while it loads ("NOW LOADING..."; see <see cref="TitleConversion.Loading"/>).</summary>
    public Texture2D LoadingImage() => LoadTexture("loading");

    private FieldContent? _field;
    /// <summary>The game's content, decoded from the installed disc files as the game asks for it.</summary>
    public FieldContent Field => _field ?? throw new InvalidOperationException("Vagrant Story is not installed.");

    /// <summary>Whether a room loads quickly, everything it needs already decoded into the cache; otherwise the loading picture shows.</summary>
    public bool Converted(int zone, int map) => Field.IsCached(zone, map);

    /// <summary>A music id's sequence and instrument bank, as the disc holds them.</summary>
    public (byte[] Sequence, byte[] Bank) Music(string name) =>
        Field.Music(name) ?? throw new InvalidDataException($"The disc has no music {name}.");

    /// <summary>Whether the disc has a sound, in the bank it plays from.</summary>
    public bool HasSound(string name) => Field.Sound(name) != null;

    /// <summary>A sound's bank (none for a resident one), id, pan and voice group.</summary>
    public (string BankName, byte[]? Bank, int Id, int Pan, int Group) Sound(string name) =>
        Field.Sound(name) ?? throw new InvalidDataException($"The disc has no sound {name}.");

    /// <summary>A character's definition: bones, mounts, limbs and clip events (empty when the disc has no model).</summary>
    /// <summary>An effect's sound sequence (its block 13), empty when it has none.</summary>
    public byte[] EffectSequence(int effect) => Field.EffectSequence(effect);

    public Godot.Collections.Dictionary Character(int character) =>
        Field.Character($"character_{character:X2}") is { } built ? Parse(built.Definition.ToJsonString()) : new Godot.Collections.Dictionary();

    /// <summary>The weapons' swing sounds, by category.</summary>
    public int[] SwingSounds => Field.SwingSoundIds();

    /// <summary>Boots the game's sound driver from the disc: the executable's code and the resident banks.</summary>
    public void Boot(SoundDriver driver) =>
        driver.Boot(Disc("SLUS_010.40"), Disc("SOUND/WAVE0000.DAT"), Disc("SOUND/WAVE0005.DAT"), Disc("SOUND/WAVE0200.DAT"), Disc("SE/EFFECT00.DAT"));

    private static Godot.Collections.Dictionary Parse(string json)
    {
        var parsed = Json.ParseString(json);
        if (parsed.VariantType != Variant.Type.Dictionary) throw new InvalidDataException("A decoded document could not be read.");
        return parsed.AsGodotDictionary();
    }

    // Effects are immutable for an installation. Reuse their parsed documents and GPU atlases across hits.
    private readonly Dictionary<string, Godot.Collections.Dictionary> _effectData = new();
    private readonly Dictionary<string, Texture2D> _effectTextures = new();

    /// <summary>Loads an installed image as a texture.</summary>
    public Texture2D Texture(string name)
    {
        if (!name.StartsWith("effect_", StringComparison.Ordinal)) return LoadTexture(name);
        if (!_effectTextures.TryGetValue(name, out var texture)) _effectTextures[name] = texture = LoadTexture(name);
        return texture;
    }

    /// <summary>A document decoded from the disc, such as a room's event.</summary>
    /// <summary>The HUD's hand-lettered alphabet on its labels page, made once; null when the installation lacks it.</summary>
    public HudLettering? HudLettering => _hudLettering ??= Has("hud_labels") && Has("hud") && Data("hud").GetValueOrDefault("font", default).AsGodotDictionary() is { Count: > 0 } font
        ? new HudLettering(ScreenTexture("hud_labels", true), font) : null;
    private HudLettering? _hudLettering;

    public Godot.Collections.Dictionary Data(string name)
    {
        if (name.StartsWith("effect_", StringComparison.Ordinal) && _effectData.TryGetValue(name, out var known)) return known;
        var document = Parse((Field.Data(name) ?? throw new InvalidDataException($"The disc has no {name}.")).ToJsonString());
        if (name.StartsWith("effect_", StringComparison.Ordinal)) _effectData[name] = document;
        return document;
    }

    /// <summary>An installed full-screen overlay's layer images, in layer order.</summary>
    public (Texture2D Texture, Color? Fill)[] Overlay(int id)
    {
        var layers = Parse(Field.Overlay(id).ToJsonString())["layers"].AsGodotArray();
        var result = new (Texture2D, Color?)[layers.Count];
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i].AsGodotDictionary();
            // The layer's fill: colour 0 of its palette, none when that is 0 (as the GPU leaves such texels out).
            int fill = layer.GetValueOrDefault("fill", 0).AsInt32();
            result[i] = (ScreenTexture(layer["image"].AsString()), fill == 0 ? null : DisplaySpace.FromRgba32(TitleConversion.ToColor((ushort)fill, false)));
        }
        return result;
    }

    /// <summary>
    /// An installed retail font as a bitmap font, 12-pixel cells drawn at their native size: the balloons'
    /// "dialogue_font" or the system box's "system_font". Its atlas takes the enhanced text scaling of the display
    /// settings (the glyphs keep their sizes, so layout is unchanged); each font is made once and kept, and follows
    /// a change of the setting in place.
    /// </summary>
    public FontFile BitmapFont(string asset)
    {
        if (_fonts.TryGetValue(asset, out var kept)) return kept;
        var font = LoadFont(asset);
        _fonts[asset] = font;
        return font;
    }

    private readonly Dictionary<string, BitmapFontAsset> _fonts = new(StringComparer.Ordinal);

    private BitmapFontAsset LoadFont(string asset)
    {
        using var definition = System.Text.Json.JsonDocument.Parse((Field.Data(asset) ?? throw new InvalidDataException($"The disc has no {asset}.")).ToJsonString());
        using var image = LoadImage(definition.RootElement.GetProperty("texture").GetString()!);
        var display = GetNodeOrNull<DisplaySettings>("/root/DisplaySettings");
        BitmapFontAsset font;
        try { font = BitmapFontAsset.Load(definition.RootElement, image, display?.FontScaling.Factor ?? 1); }
        catch (Exception error) when (error is ArgumentException or KeyNotFoundException or InvalidOperationException) { throw new InvalidDataException($"The installed font {asset} could not be read: {error.Message}"); }
        // The font's own settings are left as loaded: changing one (its antialiasing, say) clears the glyphs it was
        // given, and the text then falls to the default font.
        // A pixel of a message's horizontal offset.
        int size = font.FixedSize;
        font.SetGlyphAdvance(0, size, DialogueConversion.HairSpace, new Vector2(1, 0));
        font.SetGlyphSize(0, new Vector2I(size, 0), DialogueConversion.HairSpace, Vector2.Zero);
        return font;
    }

    /// <summary>A screen picture, or null where the installation lacks it or it cannot be read.</summary>
    public Texture2D? ScreenTextureIfAny(string name)
    {
        try { return ScreenTexture(name); }
        catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// An installed picture drawn on the 320 by 240 screen (the HUD, menus, balloons, overlays): enlarged as the display
    /// settings ask, crisply for art or anti-aliased for <paramref name="lettering"/>, while it reports its original
    /// size, so everything that draws regions of it in screen pixels is unchanged. Each is made once and kept, and
    /// follows a change of the setting in place.
    /// </summary>
    public Texture2D ScreenTexture(string name, bool lettering = false)
    {
        if (_screenTextures.TryGetValue(name, out var kept)) return kept.Texture;
        var texture = new ImageTexture();
        Enlarge(texture, name, lettering);
        _screenTextures[name] = (texture, lettering);
        return texture;
    }

    private readonly Dictionary<string, (ImageTexture Texture, bool Lettering)> _screenTextures = new(StringComparer.Ordinal);

    private void Enlarge(ImageTexture texture, string name, bool lettering)
    {
        using var image = LoadImage(name);
        var display = GetNodeOrNull<DisplaySettings>("/root/DisplaySettings");
        var scaling = display == null ? TextureScaling.Original : lettering ? display.FontScaling : display.SpriteScaling;
        if (scaling.Enlarges) { using var enlarged = scaling.Apply(image); texture.SetImage(enlarged); }
        else texture.SetImage(image);
        texture.SetSizeOverride(image.GetSize());
    }

    /// <summary>The enhanced 2D art or text setting changed: the kept pictures and fonts are remade in place.</summary>
    private void Rescale()
    {
        foreach (var (name, (texture, lettering)) in _screenTextures) Enlarge(texture, name, lettering);
        foreach (var (asset, font) in _fonts)
        {
            using var fresh = LoadFont(asset);
            int size = font.FixedSize;
            var cache = new Vector2I(size, 0);
            font.SetTextureImage(0, cache, 0, fresh.GetTextureImage(0, cache, 0));
            foreach (int glyph in fresh.GetGlyphList(0, cache)) font.SetGlyphUVRect(0, cache, glyph, fresh.GetGlyphUVRect(0, cache, glyph));
        }
    }

    /// <summary>
    /// Loads an installed character rig: a scene with one skinned mesh and one AnimationPlayer holding the character's clips.
    /// Throws <see cref="InvalidDataException"/> when the rig is missing or damaged.
    /// </summary>
    /// <summary>Each rig built this session, kept as a template: later actors take a copy rather than parse it again.</summary>
    private readonly Dictionary<string, Node3D> _rigs = new(StringComparer.Ordinal);

    public Node3D Rig(string name)
    {
        if (!_rigs.TryGetValue(name, out var template)) _rigs[name] = template = BuildRig(name);
        var copy = (Node3D)template.Duplicate();
        // Each actor lights and tints its own materials: a copy takes its own mesh and materials (the vertex data
        // stays shared until written).
        foreach (var node in copy.FindChildren("*", "MeshInstance3D", true, false))
        {
            var instance = (MeshInstance3D)node;
            if (instance.Mesh is not ArrayMesh shared) continue;
            var mesh = (ArrayMesh)shared.Duplicate();
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                if (mesh.SurfaceGetMaterial(surface) is Material material) mesh.SurfaceSetMaterial(surface, (Material)material.Duplicate());
            instance.Mesh = mesh;
        }
        return copy;
    }

    private Node3D BuildRig(string name)
    {
        byte[] model = name.StartsWith("weapon_", StringComparison.Ordinal) ? Field.Weapon(name)
            : Field.Character(name)?.Model ?? throw new InvalidDataException($"The disc has no model for {name}.");
        using var document = new GltfDocument();
        using var state = new GltfState();
        if (document.AppendFromBuffer(model, "", state) != Error.Ok || document.GenerateScene(state) is not Node3D scene)
            throw new InvalidDataException($"The installed {name} could not be read.");
        // glTF loses loop flags. The decoder owns them: half-strides that hand off to the
        // adjacent slot must finish, whereas a script's restart keeps its clip looping.
        var looping = name.StartsWith("weapon_", StringComparison.Ordinal) ? null : Field.Character(name)?.Definition["looping"]?.AsArray();
        foreach (var node in scene.FindChildren("*", "AnimationPlayer", true, false))
            foreach (var clip in ((AnimationPlayer)node).GetAnimationList())
            {
                bool loop = looping == null;
                if (looping != null) foreach (var value in looping) loop |= value?.GetValue<string>() == clip.ToString();
                ((AnimationPlayer)node).GetAnimation(clip).LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            }
        // glTF keeps a mesh's vertex colours but not the material's use of them: a Gouraud-shaded model's colours
        // (the dragons', the shaded props') multiply its texture, doubled, as the PSX's 0x80 leaves a texel as it is.
        foreach (var node in scene.FindChildren("*", "MeshInstance3D", true, false))
        {
            if (((MeshInstance3D)node).Mesh is not ArrayMesh mesh) continue;
            for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
            {
                // Semi-transparent faces' surfaces are named by their blend (the conversion's), which glTF does not keep;
                // the export numbers repeated names ("mix2").
                if (mesh.SurfaceGetMaterial(surface) is StandardMaterial3D blended && blended.ResourceName.TrimEnd("0123456789".ToCharArray()) is ("mix" or "add" or "subtract") and var blend)
                {
                    blended.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                    blended.BlendMode = blend switch { "add" => BaseMaterial3D.BlendModeEnum.Add, "subtract" => BaseMaterial3D.BlendModeEnum.Sub, _ => BaseMaterial3D.BlendModeEnum.Mix };
                    blended.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled;
                }
                if (mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material && (mesh.SurfaceGetFormat(surface) & Mesh.ArrayFormat.FormatColor) != 0)
                {
                    material.VertexColorUseAsAlbedo = true;
                    material.VertexColorIsSrgb = true;
                    material.AlbedoColor = new Color(2, 2, 2, material.AlbedoColor.A);
                }
            }
        }
        return scene;
    }

    /// <summary>
    /// Loads an installed field: its shared-engine document and the textures it names, keyed as the document refers to them.
    /// Throws <see cref="InvalidDataException"/> when the field is missing or damaged.
    /// </summary>
    public (Godot.Collections.Dictionary Document, Godot.Collections.Dictionary Textures) Room(string name)
    {
        var document = Data(name);
        var textures = new Godot.Collections.Dictionary();
        foreach (KeyValuePair<Variant, Variant> texture in document.GetValueOrDefault("textures", new Godot.Collections.Dictionary()).AsGodotDictionary())
        {
            var image = LoadImage(texture.Value.AsString());
            image.GenerateMipmaps();
            textures[texture.Key] = ImageTexture.CreateFromImage(image);
        }
        return (document, textures);
    }


    /// <summary>Opens the installation, rejecting it when a disc file it needs is missing or no longer the retail file.</summary>
    public void Reload()
    {
        _field = null;
        _effectData.Clear();
        _effectTextures.Clear();
        foreach (var rig in _rigs.Values) rig.Free();
        _rigs.Clear();
        try
        {
            _field = new FieldContent(GlobalRoot);
            Vector2I Point(JsonNode node) => new((int)node[0]!, (int)node[1]!);

            var layout = Field.Data("title_layout")!;
            Publisher = LoadTexture("publisher");
            Developer = LoadTexture("developer");
            TitleBackground = LoadTexture("title_background");
            TitlePanel = LoadTexture("title_panel");
            TitleCopyright = LoadTexture("title_copyright");
            PublisherOrigin = Point(layout["publisher_origin"]!);
            DeveloperOrigin = Point(layout["developer_origin"]!);
            CopyrightOrigin = Point(layout["copyright_origin"]!);
            PanelOrigin = Point(layout["panel_origin"]!);
            var palettes = layout["palettes"]!.AsArray();
            TitlePalettes = new ushort[palettes.Count][];
            for (int i = 0; i < palettes.Count; i++)
            {
                var colours = palettes[i]!.AsArray();
                TitlePalettes[i] = new ushort[colours.Count];
                for (int j = 0; j < colours.Count; j++) TitlePalettes[i][j] = (ushort)(int)colours[j]!;
            }
            int words = layout["words"]!.AsArray().Count;
            TitleWords = new Image[words];
            TitleGlows = new Texture2D[words];
            TitleGlowOrigins = new Vector2I[words];
            for (int i = 0; i < words; i++)
            {
                TitleWords[i] = LoadImage($"title_word_{i}");
                if (TitleWords[i].GetFormat() != Image.Format.L8) TitleWords[i].Convert(Image.Format.L8);
                TitleGlows[i] = LoadTexture($"title_glow_{i}");
                var glow = layout["glows"]![i]!;
                TitleGlowOrigins[i] = new Vector2I((int)glow["x"]!, (int)glow["y"]!);
            }
            Field.DiscFile("MOV/TITLE.STR");
            var (video, music) = new RetailInstallation(GlobalRoot).IntroMovie();
            IntroVideo = new VideoStreamTheora { File = video };
            IntroMusic = AudioStreamOggVorbis.LoadFromFile(music);
            if (!System.IO.File.Exists(System.IO.Path.Combine(GlobalRoot, "disc", RetailInstallation.EndingStream)))
                throw new InvalidDataException("This older installation omits the ending music. Install again from your US disc.");
            Failure = "";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NullReferenceException or InvalidOperationException or FormatException)
        {
            _field = null;
            _effectData.Clear();
            _effectTextures.Clear();
            Failure = error.Message;
        }
    }
}
