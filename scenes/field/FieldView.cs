// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Wol.Engine;
using Wol.Engine.Display;
using Wol.Engine.Flow;
using Wol.Engine.World;
using Wol.Games.Vagrant.Runtime;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// Binds an installed room and its translated event to the shared engine's field presentation.
/// The room's polygons keep their retail per-vertex colours, which the PlayStation applies to texels at up to twice
/// their value; the converted document carries half that factor in display space and every material here restores it.
/// The room event's flows run in the shared flow runtime on the retail 30 Hz event clock; this view presents their
/// commands: the event camera, actors, text boxes, fades and threads.
/// </summary>
public partial class FieldView : Node3D, IScenePresentation, IFlowPresentation<EventCommand>
{
    /// <summary>The room the view opens in (zone, map index); New Game begins in zone 1, map 0.</summary>
    [Export] public int StartZone { get; set; } = 1;
    [Export] public int StartMap { get; set; }
    /// <summary>Where Ashley stands as the first room opens (a tile, a facing, a half-tile nudge), as a door into it would put her; null for the room's default.</summary>
    public (int X, int Z, float Facing, bool Half)? StartAt { get; set; }

    public int Zone { get; private set; }
    public int Map { get; private set; }
    /// <summary>Every room presented so far, as "z001_m00", in order.</summary>
    public readonly List<string> Rooms = new();
    /// <summary>Every event flow started so far, in order: room events and chained event files.</summary>
    public readonly List<string> Events = new();
    /// <summary>Set when the story leaves the rooms this installation holds.</summary>
    public string EndOfContent { get; private set; } = "";

    public FieldScene Field { get; private set; } = null!;
    /// <summary>The installed field document.</summary>
    public GDictionary Document { get; private set; } = new();
    public string Failure { get; private set; } = "";
    public EventCamera EventCamera { get; private set; } = new();
    /// <summary>The field camera around Ashley once the events hand control back.</summary>
    public readonly GameplayCamera Gameplay = new();
    /// <summary>Whether Ashley is under the player's control: no event runs and the camera is back on her.</summary>
    public bool Controlled { get; private set; }
    /// <summary>The player's actor, Ashley, in slot 0.</summary>
    public EventActor? Player => Actors.GetValueOrDefault("slot:0");
    public readonly Dictionary<string, EventActor> Actors = new();
    /// <summary>Characters the room event created that this installation has no rig for.</summary>
    public readonly List<int> MissingCharacters = new();
    /// <summary>Whether the room's music is playing.</summary>
    public bool MusicPlaying => _driver != null && _driver.MusicPlaying;
    /// <summary>Event frames since the room event started.</summary>
    public long EventFrame { get; private set; }

    /// <summary>Camera orbit around the room while no event drives the camera.</summary>
    public float Heading = Mathf.Pi * 0.25f, Pitch = 0.6f, Distance = 1f;
    private Vector3 _focus;

    private RetailContent _content = null!;
    /// <summary>The installed content.</summary>
    public RetailContent Content => _content;
    private FieldMenu _menu = null!;
    private FlowRuntime<EventWorld, EventCommand>? _events;
    /// <summary>The flags, switch and counters the scripts run against, and the bindings they compile onto.</summary>
    private readonly EventWorld _world = new();
    /// <summary>Each installed event file's compiled flows and their ids (the main flow first), compiled once.</summary>
    private readonly Dictionary<string, (FlowProgram<EventWorld, EventCommand>[] Programs, string[] Ids)> _programs = new();
    /// <summary>The running event's flow ids, by slot.</summary>
    private string[] _flows = [];
    private string _mainEvent = "";
    /// <summary>Per flow, the presentation batch it last issued and the skippable section that batch waits in (or -1).</summary>
    private (long Sequence, int Section)[] _sections = [];
    /// <summary>Threads that started themselves again (f5 on their own number), restarted once the frame's flows have run.</summary>
    private readonly List<int> _restarts = new();
    private readonly List<PresentationReceipt> _completions = new();
    private readonly Dictionary<int, GDictionary> _boxes = new();
    private readonly Dictionary<int, EventBalloon> _balloons = new();
    private GDictionary? _balloonShapes;
    private Texture2D? _paper;
    private readonly List<(PresentationReceipt Id, Func<bool> Done)> _waits = new();
    private bool _oddFrame;
    private ColorRect _screen = null!;
    private bool _fadeWhite;

    private const string FadeShader = @"shader_type canvas_item;
uniform sampler2D screen_texture : hint_screen_texture, filter_nearest;
uniform float level = 1.0;
uniform bool white = false;
uniform bool linear = false;
vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
vec3 to_linear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
void fragment() {
    vec3 c = texture(screen_texture, SCREEN_UV).rgb;
    if (linear) c = to_srgb(c);
    c = white ? min(c + level, vec3(1.0)) : max(c - level, vec3(0.0));
    if (linear) c = to_linear(c);
    COLOR = vec4(c, 1.0);
}";
    private EventOverlay _overlay = null!;
    private EventEcho _echo = null!;
    private EventRain _rain = null!;
    /// <summary>The game's own sound driver, playing the disc's sequences and banks live.</summary>
    private SoundDriver _driver = null!;
    /// <summary>The sounds playing by name (without their pan), each with its full key.</summary>
    private readonly Dictionary<string, string> _sounds = new();
    /// <summary>The music's volume (0 to 1) as fades move it.</summary>
    private float _musicLevel = 1;
    /// <summary>Sounds from actors' own banks, playing at their actors.</summary>
    /// <summary>The screen's fade (1 black) and its speed a frame. A field starts black and fades in, as a room entered by a door does, unless its event takes the screen over (the opening fades in itself).</summary>
    private float _fade = 1, _fadeSpeed = -8 / 255f;
    /// <summary>How far the screen has faded (0 clear, 1 black), for the checks.</summary>
    public float Fade => _fade;
    private bool _displayOn = true;
    private bool _skyVisible = true;

    /// <summary>
    /// The frame plus the effects' display-space sum: both views are linear (the field draws 2D in HDR), and the
    /// second holds the frame's light plus the raw texels of every added sprite, so their difference is those texels'
    /// sum, which the PSX adds to the displayed colour.
    /// </summary>
    private MeshInstance3D? _dome;
    private Vector2 _skyOffset;
    /// <summary>The sky's parallax for the camera's yaw and pitch (7e; 4096 a pixel a unit of angle).</summary>
    private Vector2 _skyParallax = new(4096, 4096);

    private string _pendingChain = "", _musicAsset = "";
    // Room geometry group state: the camera-direction cut-away, whether the groups drawn only in first-person view
    // are hidden (as they are in ordinary play), and groups a script hid.
    private bool _cutaway = true;
    /// <summary>
    /// Retail's switch at 0x800F19C8: while set, the room's groups marked 0x8000 are not drawn (the renderer 0x8009723C
    /// takes it as its second argument). A room load clears it (0x8008A09C), the first-person view sets it and leaving
    /// it clears it (0x80074580, 0x800745EC), and 62 sets it as the script says.
    /// </summary>
    private bool _hideFirstPerson;
    private float _roomNear = -1;
    /// <summary>Pad buttons newly pressed since a button wait began, in the retail bit order.</summary>
    private int _pressedButtons;
    /// <summary>The music each retail slot holds; a zone's own music is in slot 3, and 90 loads others.</summary>
    private readonly Dictionary<string, string> _musicSlots = new();
    private Tween? _musicFade;
    private readonly HashSet<int> _hiddenGroups = new();
    // Set when an event has handed the camera back itself (e9), so its end does not do it again.
    private bool _returnSuppressed;
    private bool _zoomPressed;
    private FieldPlayer? _control;
    /// <summary>The room's battle: Ashley and the characters the room placed.</summary>
    public FieldBattle? Battle { get; private set; }
    private CanvasLayer _hud = null!;

    /// <summary>The HUD layer's whole-window control, which what is laid out on the 320 by 240 screen stands in.</summary>
    public Control Screen { get; private set; } = null!;

    /// <summary>
    /// How wide a pixel of retail's 320 by 240 screen is shown, for its height. The PSX's 320-pixel mode does not
    /// fill a television's width: Episode 2's footage, undone from its 16:9 stretch, shows a screen pixel 2.3125 × 3/4
    /// video pixels wide to 1.99 high. Retail stretches its 3D view by 9/8 in screen pixels to make up for it
    /// (BATTLE.PRG 0x8007ACB0), so its rooms and characters are drawn true here; what it lays out on the screen
    /// itself (the HUD, menus, text, pictures) is drawn this much narrower than square.
    /// </summary>
    public const float PixelAspect = 2.3125f * 3 / 4 / 1.99f;

    /// <summary>
    /// The lines of retail's 320 by 240 screen that the television shows. The field projects about line 112
    /// (BATTLE.PRG 0x8007A60C sets the GTE's screen offset to 160, 112) and its display shows 224 lines from the
    /// top of the frame (0x800761A8 sets both display environments' screen to 256 by 224 at line 8), so lines 0 to
    /// 223 are seen, 112 each side of the projection centre, and lines 224 to 239 never are. The window shows the
    /// same: the camera's vertical field of view spans these lines, and what is laid out on the screen is scaled to
    /// them, its top at the window's top.
    /// </summary>
    public const int VisibleLines = 224;

    /// <summary>
    /// Where the 320 by 240 screen lies in a window of <paramref name="size"/>: its scale (window pixels to a screen
    /// pixel, across and down, the across <see cref="PixelAspect"/> of the down), and how far right of the window's
    /// left edge a part laid out on the screen's left (0), middle (1) or right (2) stands. The screen fits the
    /// window's height, or its width when that is narrower.
    /// </summary>
    public static (Vector2 Unit, Vector2 Offset) Pin(Vector2 size, int side)
    {
        var unit = Unit(size);
        var spare = size - new Vector2(320, VisibleLines) * unit;
        return (unit, new Vector2(spare.X * side / 2, spare.Y / 2));
    }

    /// <summary>Window pixels to a screen pixel, across and down, for a window of <paramref name="size"/>.</summary>
    public static Vector2 Unit(Vector2 size)
    {
        float scale = Math.Min(size.X / (320 * PixelAspect), size.Y / VisibleLines);
        return new Vector2(scale * PixelAspect, scale);
    }

    /// <summary>The render layer effects and rain are drawn on, so that they can be drawn over a picture laid over the room.</summary>
    public const uint EffectLayer = 1u << 19;
    private SubViewport _effectsView = null!;
    private Camera3D _effectsCamera = null!;
    private TextureRect _effectsOver = null!;

    /// <summary>
    /// While a picture is laid over the room (an overlay layer in front of it), effects and rain are drawn over the
    /// picture: retail draws them after the screen's layers. They are then seen through a second camera that follows
    /// the field's and draws only them, added over the picture.
    /// </summary>
    private void PlaceEffectsView()
    {
        if (Field == null) return;
        bool over = _overlay.CoversRoom;
        var camera = Field.Camera;
        camera.CullMask = over ? camera.CullMask & ~EffectLayer : camera.CullMask | EffectLayer;
        _effectsOver.Visible = over;
        _effectsView.RenderTargetUpdateMode = over ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        if (!over) return;
        _effectsView.World3D = camera.GetViewport().World3D;
        _effectsView.Size = (Vector2I)GetViewport().GetVisibleRect().Size;
        _effectsCamera.GlobalTransform = camera.GlobalTransform;
        _effectsCamera.KeepAspect = camera.KeepAspect;
        _effectsCamera.Fov = camera.Fov;
        _effectsCamera.Near = camera.Near;
        _effectsCamera.Far = camera.Far;
        _effectsCamera.HOffset = camera.HOffset;
        _effectsCamera.VOffset = camera.VOffset;
    }
    private Minimap _minimap = null!;
    /// <summary>Ashley's hit and magic points, carried from room to room once she has fought.</summary>
    private int _playerHp = -1, _playerMp = -1;
    private bool _playerDrawn;
    /// <summary>Buttons newly pressed since the last 30 Hz step, for the battle.</summary>
    private readonly HashSet<string> _presses = new();
    private static readonly string[] BattleButtons = { "ps_circle", "ps_cross", "ps_left", "ps_right", "ps_triangle", "ps_square", "ps_up", "ps_down", "ps_l1", "ps_r1", "ps_l2", "ps_r2", "ps_start", "ps_select" };
    private bool _gameOverShown, _gameOverEvent;

    /// <summary>Set once the game is over and its scene has played; the field then goes back to the title.</summary>
    public bool GameEnded { get; private set; }
    /// <summary>Keeps the field in place when the game ends (a test that holds the scene sets it).</summary>
    public bool KeepScene;
    /// <summary>The door Ashley is leaving by while the screen fades out, and the tile she was last on.</summary>
    private GDictionary? _door;
    private (int X, int Z) _tile = (-1, -1);
    /// <summary>Whether she walks on into the room she arrives in while the screen fades in.</summary>
    private bool _walkIn;
    /// <summary>The numbers messages print (1b), ten slots.</summary>
    private readonly int[] _integers = new int[10];
    /// <summary>The fade the next room change uses (6c): 0, 4 and 5 black, 1 white, 2 a wipe, 3 none, and levels a frame.</summary>
    private (int Effect, int Speed) _transitionFade = (0, 8);
    /// <summary>Where Ashley enters the next room: a tile and a facing. New Game starts her on tile (4, 2) facing +z.</summary>
    private (int X, int Z, float Facing) _arrival = (4, 2, 2048);
    /// <summary>A door's half-tile nudge: 64 units along x when arriving facing ±z, along z facing ±x.</summary>
    private bool _arrivalHalf;
    /// <summary>
    /// The 1024 retail state flags (unsigned bytes), carried across events and rooms. They have one home, the event
    /// world, which story commands change and event conditions test; the field reads and writes the same table.
    /// </summary>
    public int[] Flags => _world.Flags;
    private GDictionary? _pendingTransition;

    /// <summary>
    /// Where the memory cards are kept: a folder for each of the two slots, card1 and card2, each holding the five
    /// files a card has room for (file1.json to file5.json). The checks point it elsewhere.
    /// </summary>
    public static string SavesRoot = "user://saves";
    /// <summary>The game saved before the memory cards, which Continue still finds.</summary>
    private static string OldSavePath => SavesRoot + "/game.json";
    public static string CardFile(int card, int file) => $"{SavesRoot}/card{card}/file{file}.json";

    /// <summary>
    /// Keeps this process's saves apart under <paramref name="root"/>, in a folder of its own (by process id), so runs
    /// side by side do not empty each other's memory cards; folders left by processes no longer running are removed.
    /// </summary>
    public static void IsolateSaves(string root)
    {
        string shared = ProjectSettings.GlobalizePath(root);
        if (System.IO.Directory.Exists(shared))
            foreach (string folder in System.IO.Directory.GetDirectories(shared))
            {
                if (!int.TryParse(System.IO.Path.GetFileName(folder), out int pid) || pid == OS.GetProcessId()) continue;
                bool running;
                try { running = !System.Diagnostics.Process.GetProcessById(pid).HasExited; }
                catch (ArgumentException) { running = false; }
                if (!running) try { System.IO.Directory.Delete(folder, true); } catch (System.IO.IOException) { }
            }
        SavesRoot = $"{root}/{OS.GetProcessId()}";
        ReleaseSaves();
    }

    /// <summary>Removes this process's saves (its folder under the root <see cref="IsolateSaves"/> gave).</summary>
    public static void ReleaseSaves()
    {
        string own = ProjectSettings.GlobalizePath(SavesRoot);
        if (System.IO.Directory.Exists(own)) try { System.IO.Directory.Delete(own, true); } catch (System.IO.IOException) { }
    }

    /// <summary>The time played, in frames at 60 a second; it runs while the field does.</summary>
    public long PlayFrames { get; private set; }
    /// <summary>How many times this game has been saved.</summary>
    public int SaveCount { get; private set; }

    /// <summary>A saved game the title's Continue asks the next field to resume.</summary>
    public static GDictionary? PendingSave;

    /// <summary>Redraws the characters shown as the Settings now choose.</summary>
    private void RedrawCharacters()
    {
        foreach (var actor in Actors.Values) CharacterQuality.Apply(actor.Node, _content.Character(actor.Character).GetValueOrDefault("head_bone", -1).AsInt32());
    }

    public override void _ExitTree() => CharacterQuality.Changed -= RedrawCharacters;

    public override void _Ready()
    {
        _content = GetNode<RetailContent>("/root/RetailContent");
        CharacterQuality.Changed += RedrawCharacters;
        _driver = new SoundDriver { Name = "SoundDriver" };
        AddChild(_driver);
        try { _content.Boot(_driver); }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { Fail(error.Message); }
        _rain = new EventRain { Name = "Rain" };
        AddChild(_rain);
        // The frame echo copies the scene beneath the HUD; the overlay's fades cover everything.
        var echoLayer = new CanvasLayer { Layer = 6 };
        _echo = new EventEcho { Name = "Echo" };
        echoLayer.AddChild(_echo);
        AddChild(echoLayer);
        var overlayLayer = new CanvasLayer { Layer = 9 };
        // An overlay layer drawn behind the room lies on a canvas layer that the field takes as its background.
        var behindLayer = new CanvasLayer { Layer = BehindLayer, Name = "OverlayBehind" };
        AddChild(behindLayer);
        _overlay = new EventOverlay { Name = "Overlay", Behind = behindLayer };
        overlayLayer.AddChild(_overlay);
        // Effects and rain are drawn over a picture laid over the room, as the opening's fire is over the manor.
        _effectsView = new SubViewport { Name = "EffectsView", TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        _effectsCamera = new Camera3D { CullMask = EffectLayer };
        _effectsView.AddChild(_effectsCamera);
        AddChild(_effectsView);
        _effectsOver = new TextureRect { Name = "EffectsOver", Texture = _effectsView.GetTexture(), AnchorRight = 1, AnchorBottom = 1, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _effectsOver.Material = new ShaderMaterial { Shader = new Shader { Code = "shader_type canvas_item;\nrender_mode blend_add;\nvoid fragment() { COLOR = vec4(texture(TEXTURE, UV).rgb, 1.0); }\n" } };
        overlayLayer.AddChild(_effectsOver);
        AddChild(overlayLayer);
        _hud = new CanvasLayer { Layer = 7, Name = "Hud" };
        AddChild(_hud);
        // The HUD, the system box and the menus are laid out on retail's 320 by 240 screen, scaled by the window's
        // height; in a wider window what stands on the left of it keeps to the window's left edge, what stands on
        // the right to its right edge, and what is in the middle stays centred (see Pin).
        Screen = new Control { Name = "Screen", MouseFilter = Control.MouseFilterEnum.Ignore };
        Screen.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _hud.AddChild(Screen);
        _minimap = new Minimap { Name = "Minimap" };
        Screen.AddChild(_minimap);
        _minimap.Configure(_content.Has("hud_cursor") ? _content.ScreenTexture("hud_cursor") : null);
        SystemBox = new SystemBox { Name = "SystemBox" };
        Texture2D? labels = null;
        try { labels = _content.ScreenTexture("hud_labels", true); } catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { }
        SystemBox.Configure(_content.Has("system_font") ? _content.BitmapFont("system_font") : null, labels);
        Screen.AddChild(SystemBox);
        // The field darkens behind MENUF's screens.
        _dim = new ColorRect { Name = "Dim", Color = new Color(0, 0, 0, 0), MouseFilter = Control.MouseFilterEnum.Ignore };
        _dim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        Screen.AddChild(_dim);
        _menu = new FieldMenu { Name = "Menu" };
        _menu.Configure(this);
        _hud.AddChild(_menu);
        var layer = new CanvasLayer { Layer = 10 };
        // Black from the first frame: the scene change fades in with the tree paused, before the field steps its fade.
        // Fades subtract a grey from the screen (an added one for a white fade), as a PlayStation's do: a lit room keeps
        // its strongest colour longest, so an orange-lit cellar goes red on its way to black (retail footage).
        // The fade reads the whole screen beneath it (room, HUD and windows) and takes the grey off in display values,
        // as the console's subtractive blend does.
        _screen = new ColorRect { Color = new Color(1, 1, 1, 1), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore, Material = new ShaderMaterial { Shader = new Shader { Code = FadeShader } } };
        layer.AddChild(_screen);
        AddChild(layer);
        if (PendingSave is { } save)
        {
            PendingSave = null;
            Resume(save);
            return;
        }
        // A new game starts with the HUD off and the opening turns it on; a room started on its own has it on, as a
        // resumed game does.
        HudShown = (StartZone, StartMap) != (1, 0);
        if (StartAt is { } at) { _arrival = (at.X, at.Z, at.Facing); _arrivalHalf = at.Half; }
        LoadRoom(StartZone, StartMap);
    }

    /// <summary>
    /// Saves the game to a memory card's file: the room, where Ashley stands, the flags, what she carries and her
    /// state, with what the file list shows of it (the save's location number, as a save point or container names
    /// it, MAP%, the save counts, the time played, HP and MP) and a generation one past the newest file's, which
    /// Continue resumes.
    /// </summary>
    public Error SaveGame(int card, int slot, int location)
    {
        if (Player is not { } ashley || Battle?.Ashley is not { } fighter) return Error.Unavailable;
        var flags = new GArray();
        for (int i = 0; i < 1024; i++) flags.Add(FlagValue(i));
        var items = new GDictionary();
        foreach (var (id, count) in Items) items[id.ToString()] = count;
        var save = new GDictionary
        {
            ["version"] = 1, ["zone"] = Zone, ["map"] = Map,
            ["tile"] = new GArray { (int)Mathf.Floor(ashley.Position.X / 128), (int)Mathf.Floor(ashley.Position.Z / 128) }, ["facing"] = ashley.Facing,
            ["flags"] = flags, ["items"] = items, ["equipment"] = Equipment.Duplicate(true), ["stored"] = Stored.Duplicate(true), ["stored_items"] = StoredItemsRecord(),
            ["hp"] = fighter.Hp, ["mp"] = fighter.Mp, ["abilities"] = new GArray(Array.ConvertAll(Abilities.ToArray(), a => (Variant)a)), ["spells"] = new GArray(Array.ConvertAll(Spells.ToArray(), a => (Variant)a)),
            ["visited"] = new GArray(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Visited, room => (Variant)(room.Zone * 256 + room.Map)))),
            ["arts"] = new GArray(Array.ConvertAll(Arts.ToArray(), a => (Variant)a)),
            ["score"] = new GDictionary
            {
                ["kills"] = new GArray(Array.ConvertAll(EnemyKills, a => (Variant)a)), ["attacks"] = new GArray(Array.ConvertAll(WeaponAttacks, a => (Variant)a)),
                ["max_chain"] = MaxChain, ["heals"] = HealCount, ["streak"] = StreakScore, ["other"] = OtherScore, ["ability_kills"] = AbilityPoints, ["abilities_reclaimed"] = AbilitiesReclaimed,
            },
            ["mastered"] = new GArray(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Mastered, a => (Variant)a))),
            ["weapon"] = EquippedWeapon ?? new GDictionary(), ["unarmed"] = Unarmed,
            ["armour"] = new GArray(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(EquippedArmour, a => (Variant)new GDictionary { ["limb"] = a.Key, ["record"] = a.Value }))), ["gains"] = new GArray(Array.ConvertAll(Gains, a => (Variant)a)), ["art_kills"] = new GArray(Array.ConvertAll(ArtKills, a => (Variant)a)), ["drawn"] = Battle.Drawn, ["hud"] = HudShown, ["yaw"] = Gameplay.Yaw, ["distance"] = Gameplay.TargetDistance,
        };
        int generation = 0;
        for (int c = 1; c <= 2; c++)
            for (int f = 1; f <= 5; f++)
                if (ReadCardFile(c, f) is { } other) generation = Math.Max(generation, other.GetValueOrDefault("generation", 0).AsInt32());
        save["generation"] = generation + 1;
        save["location"] = location;
        save["map_percent"] = MapPercent();
        save["save_count"] = Math.Min(9999, SaveCount + 1);
        save["clear_count"] = 0;
        save["frames"] = PlayFrames;
        save["max_hp"] = fighter.MaxHp;
        save["max_mp"] = fighter.MaxMp;
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath($"{SavesRoot}/card{card}"));
        using var file = FileAccess.Open(CardFile(card, slot), FileAccess.ModeFlags.Write);
        if (file == null) return FileAccess.GetOpenError();
        file.StoreString(Json.Stringify(save));
        SaveCount = save["save_count"].AsInt32();
        return Error.Ok;
    }

    /// <summary>A memory card's file, or null when it holds no save that can be read.</summary>
    public static GDictionary? ReadCardFile(int card, int slot) => Read(CardFile(card, slot));

    /// <summary>The save Continue resumes: the newest of the cards' files, or the one saved before the cards.</summary>
    public static GDictionary? ReadSave()
    {
        GDictionary? newest = null;
        for (int card = 1; card <= 2; card++)
            for (int slot = 1; slot <= 5; slot++)
                if (ReadCardFile(card, slot) is { } save && (newest == null || save.GetValueOrDefault("generation", 0).AsInt32() > newest.GetValueOrDefault("generation", 0).AsInt32()))
                    newest = save;
        return newest ?? Read(OldSavePath);
    }

    private static GDictionary? Read(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
        return file != null && Json.ParseString(file.GetAsText()).Obj is GDictionary save && save.GetValueOrDefault("version", 0).AsInt32() == 1 ? save : null;
    }

    /// <summary>
    /// MAP%: of the 361 rooms the disc counts (BATTLE.PRG's mask, by location number), how many she has been in.
    /// Retail marks one room's neighbour seen with it (location 54 with 55).
    /// </summary>
    public int MapPercent()
    {
        if (!_content.Has("memory_card")) return 0;
        var card = _content.Data("memory_card");
        var rooms = card["rooms"].AsGodotArray();
        var counted = card["counted"].AsGodotArray();
        // A room's location number is the first pair naming it.
        var seen = new System.Collections.Generic.HashSet<int>();
        var named = new System.Collections.Generic.HashSet<(int, int)>();
        for (int i = 0; i < rooms.Count; i++)
        {
            var pair = rooms[i].AsGodotArray();
            var room = (pair[0].AsInt32(), pair[1].AsInt32());
            if (named.Add(room) && Visited.Contains(room)) seen.Add(i);
        }
        if (seen.Contains(55)) seen.Add(54);
        int count = 0;
        foreach (int room in seen)
            if (room < 512 && (counted[room >> 5].AsInt64() >> (room & 31) & 1) != 0) count++;
        return count * 100 / 361;
    }

    /// <summary>
    /// After the game over's LOAD screens: the loaded save resumes in a new field, or with none the title comes back
    /// (all installed, so at once, without the loading picture).
    /// </summary>
    public void AfterGameOver(GDictionary? save)
    {
        GameEnded = true;
        PendingSave = save;
        if (KeepScene) return;
        var scenes = GetNode<SceneManager>("/root/SceneManager");
        if (save == null) { _ = scenes.ChangeScene(Wol.Games.Vagrant.Scenes.Startup.Startup.TitleScene, showLoading: false); return; }
        _ = scenes.ChangeScene(Wol.Games.Vagrant.Scenes.Title.TitleSequence.FieldScene, showLoading: !_content.Converted(save["zone"].AsInt32(), save["map"].AsInt32()));
    }

    /// <summary>Resumes a saved game in its room, with Ashley where she was saved.</summary>
    private void Resume(GDictionary save)
    {
        PlayFrames = save.GetValueOrDefault("frames", 0).AsInt64();
        SaveCount = save.GetValueOrDefault("save_count", 0).AsInt32();
        var flags = save["flags"].AsGodotArray();
        for (int i = 0; i < flags.Count && i < EventWorld.FlagCount; i++) Flags[i] = flags[i].AsInt32();
        _inventoryLoaded = true;
        foreach (var (id, count) in save["items"].AsGodotDictionary()) Items[int.Parse(id.AsString())] = count.AsInt32();
        foreach (var record in save["equipment"].AsGodotArray()) Equipment.Add(record);
        foreach (var record in save.GetValueOrDefault("stored", new GArray()).AsGodotArray()) Stored.Add(record);
        foreach (var (id, count) in save.GetValueOrDefault("stored_items", new GDictionary()).AsGodotDictionary()) StoredItems[int.Parse(id.AsString())] = count.AsInt32();
        foreach (var ability in save.GetValueOrDefault("abilities", new GArray()).AsGodotArray()) Abilities.Add(ability.AsInt32());
        foreach (var spell in save.GetValueOrDefault("spells", new GArray()).AsGodotArray()) Spells.Add(spell.AsInt32());
        foreach (var art in save.GetValueOrDefault("arts", new GArray()).AsGodotArray()) Arts.Add(art.AsInt32());
        foreach (var spell in save.GetValueOrDefault("mastered", new GArray()).AsGodotArray()) Mastered.Add(spell.AsInt32());
        var score = save.GetValueOrDefault("score", new GDictionary()).AsGodotDictionary();
        var killed = score.GetValueOrDefault("kills", new GArray()).AsGodotArray();
        for (int i = 0; i < killed.Count && i < 6; i++) EnemyKills[i] = killed[i].AsInt32();
        var attacks = score.GetValueOrDefault("attacks", new GArray()).AsGodotArray();
        for (int i = 0; i < attacks.Count && i < 10; i++) WeaponAttacks[i] = attacks[i].AsInt32();
        MaxChain = score.GetValueOrDefault("max_chain", 0).AsInt32();
        HealCount = score.GetValueOrDefault("heals", 0).AsInt32();
        StreakScore = score.GetValueOrDefault("streak", 0).AsInt64();
        OtherScore = score.GetValueOrDefault("other", 0).AsInt64();
        AbilityPoints = score.GetValueOrDefault("ability_kills", 0).AsInt32();
        AbilitiesReclaimed = score.GetValueOrDefault("abilities_reclaimed", 0).AsInt32();
        foreach (var room in save.GetValueOrDefault("visited", new GArray()).AsGodotArray()) Visited.Add((room.AsInt32() >> 8, room.AsInt32() & 0xFF));
        // What she has on is saved apart from what she carries; it is joined back to the carried records, so that
        // wear and repair change the one record.
        GDictionary Carried(GDictionary saved)
        {
            foreach (var value in Equipment)
            {
                var entry = value.AsGodotDictionary();
                if (entry.RecursiveEqual(saved)) return entry;
                var record = entry["record"].AsGodotDictionary();
                if (record.RecursiveEqual(saved)) return record;
                foreach (string part in new[] { "blade", "grip" })
                    if (record.GetValueOrDefault(part, 0).VariantType == Variant.Type.Dictionary && record[part].AsGodotDictionary().RecursiveEqual(saved)) return record[part].AsGodotDictionary();
            }
            return saved;
        }
        if (save.GetValueOrDefault("weapon", new GDictionary()).AsGodotDictionary() is { Count: > 0 } weapon)
        {
            foreach (string part in new[] { "blade", "grip", "entry" })
                if (weapon.GetValueOrDefault(part, 0).VariantType == Variant.Type.Dictionary) weapon[part] = Carried(weapon[part].AsGodotDictionary());
            EquippedWeapon = weapon;
        }
        Unarmed = save.GetValueOrDefault("unarmed", false).AsBool();
        foreach (var value in save.GetValueOrDefault("armour", new GArray()).AsGodotArray())
            EquippedArmour[value.AsGodotDictionary()["limb"].AsInt32()] = Carried(value.AsGodotDictionary()["record"].AsGodotDictionary());
        var gains = save.GetValueOrDefault("gains", new GArray()).AsGodotArray();
        for (int i = 0; i < gains.Count && i < 5; i++) Gains[i] = gains[i].AsInt32();
        var kills = save.GetValueOrDefault("art_kills", new GArray()).AsGodotArray();
        for (int i = 0; i < kills.Count && i < 10; i++) ArtKills[i] = kills[i].AsInt32();
        _playerHp = save["hp"].AsInt32();
        _playerMp = save["mp"].AsInt32();
        _playerDrawn = save["drawn"].AsBool();
        HudShown = save.GetValueOrDefault("hud", true).AsBool();
        Gameplay.Yaw = save["yaw"].AsSingle();
        Gameplay.Distance = Gameplay.TargetDistance = save["distance"].AsSingle();
        var tile = save["tile"].AsGodotArray();
        _arrival = (tile[0].AsInt32(), tile[1].AsInt32(), save["facing"].AsSingle());
        LoadRoom(save["zone"].AsInt32(), save["map"].AsInt32());
    }

    /// <summary>
    /// Presents a room (zone, map index) and starts its event. Anything the previous room's events created is
    /// released; the music, the screen fade and the event clock carry over, as they do on the retail transition.
    /// </summary>
    public void LoadRoom(int zone, int map)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            string name = $"z{zone:d3}_m{map:d2}";
            if (!_content.Has("field_" + name))
            {
                EndOfContent = $"zone {zone}, room {map}";
                ShowNotice($"The story continues in zone {zone}, room {map}, which is not on the disc.");
                _events = null;
                return;
            }
            if (Field != null)
            {
                RememberEnemies();
                RemoveChild(Field);
                Field.QueueFree();
            }
            EnteredMarker = -1;
            _effects.Clear();
            foreach (var balloon in _balloons.Values) balloon.QueueFree();
            _driver.StopSounds(-1);
            Actors.Clear();
            _fading.Clear();
            _clipEvents.Clear();
            _dropped.Clear();
            _balloons.Clear();
            _sounds.Clear();
            _boxes.Clear();
            _waits.Clear();
            _afterLid = null;
            _completions.Clear();
            _floorLight = null;
            _skyVisible = true;
            _skyOffset = Vector2.Zero;
            _skyParallax = new Vector2(4096, 4096);

            _pendingChain = "";
            _cutaway = true;
            _hideFirstPerson = false;
            _lookAround = false;
            _roomNear = -1;
            _hiddenGroups.Clear();
            _palettes.Clear();
            _unmapped = false;
            // A picture laid over the screen stays across a room change until a script frees it (19): the hall's credit
            // (MAP001) is wiped on before the change and off by the courtyard's script (MAP002), as retail shows it.
            _echo.Clear();
            _rain.Clear();
            EventCamera = new EventCamera();
            Controlled = false;
            _control = null;
            _choice = null;
            _door = null;
            if (Battle?.Ashley is { } fought) { _playerHp = fought.Hp; _playerMp = fought.Mp; _playerDrawn = Battle.Drawn; }
            Battle?.Clear();
            Battle = null;
            Zone = zone;
            Map = map;
            Rooms.Add(name);
            GDictionary document, textures;
            try
            {
                (document, textures) = _content.Room("field_" + name);
            }
            catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException)
            {
                Fail(error.Message);
                return;
            }
            Document = document;
            // The room's own weather starts with it, over its weather rectangle.
            _rain.Room(document.GetValueOrDefault("weather").VariantType == Variant.Type.Dictionary ? document["weather"].AsGodotDictionary() : null);
            ReadTextureFrames(textures);
            ReadSwings();
            ReadGroupScripts();
            // Platforms switched off from the start are not drawn.
            ReadPlatforms();
            foreach (var platform in _platforms) if (platform.Off) _hiddenGroups.Add(platform.Group);
            ReadFloor();
            // The camera's yaw and distance carry over between rooms; its pitch and bounds are the room's.
            var bounds = document.GetValueOrDefault("camera_bounds", new GArray()).AsGodotArray();
            Gameplay.Bounds = bounds.Count == 4 ? new Rect2(bounds[0].AsSingle(), bounds[1].AsSingle(), bounds[2].AsSingle() - bounds[0].AsSingle(), bounds[3].AsSingle() - bounds[1].AsSingle()) : new Rect2();
            Gameplay.Pitch = GameplayCamera.DefaultPitch;
            Gameplay.Distance = Gameplay.TargetDistance;
            // Music slots outlast the room; the zone's own music replaces what its slot held.
            foreach (var (slot, asset) in document.GetValueOrDefault("music", new GDictionary()).AsGodotDictionary()) _musicSlots[slot.AsString()] = asset.AsString();
            Field = new FieldScene { Name = "Field" };
            AddChild(Field);
            MoveChild(Field, 0);
            var result = Field.LoadField(document, textures);
            if (result != Error.Ok)
            {
                Fail($"The installed room {name} could not be constructed ({result}).");
                return;
            }
            ReadRoomFaces(document);
            foreach (var material in Field.Materials)
            {
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.VertexColorIsSrgb = true;
                material.AlbedoColor = new Color(2, 2, 2, material.AlbedoColor.A);
            }
            _dome = document.GetValueOrDefault("sky_colours", default).AsGodotArray() is { Count: 5 } bands ? Dome(bands) : null;
            if (_dome != null) Field.Camera.AddChild(_dome);
            Field.DialogueFont = _content.BitmapFont("dialogue_font");
            Field.DialoguePixelAspect = PixelAspect;
            Field.Camera.Fov = 50;
            Field.SetCameraScriptMode(true);
            _focus = Field.Bounds.GetCenter();
            Distance = Field.Bounds.Size.Length() * 0.8f;
            PlaceCamera();
            GetNode<DisplaySettings>("/root/DisplaySettings").ApplyGraphics();
            for (int i = 0; i < 8; i++) Field.PrepareDialogueWaits("box" + i);
            // Ashley is the player's actor in slot 0 (model and script id 0); she is in every room before its event starts,
            // out of sight until the event shows her or hands control back.
            if (Spawn(new GDictionary { ["actor"] = "slot:0", ["character"] = 0, ["tile"] = new GArray { _arrival.X, _arrival.Z } }) != Error.Ok) return;
            if (Player is { } ashley)
            {
                if (_arrivalHalf)
                {
                    bool alongX = (int)(_arrival.Facing / 1024) % 2 == 0;
                    ashley.Position += alongX ? new Vector3(64, 0, 0) : new Vector3(0, 0, 64);
                    ashley.Position = ashley.Position with { Y = FloorHeight(ashley.Position) };
                    _arrivalHalf = false;
                }
                ashley.Facing = _arrival.Facing;
                ashley.Visible = false;
                ashley.Animate("base_" + FieldPlayer.Idle, 0);
                ashley.Apply();
                // The room fades in on the view play has of her where she arrives, as retail's does, until an event
                // places the camera.
                Gameplay.Place(ashley.Position);
                Field.CameraEye = EventActor.RetailSpace * Gameplay.Eye;
                Field.CameraTarget = EventActor.RetailSpace * Gameplay.LookAt;
                Field.Camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
                Field.Camera.Fov = Mathf.RadToDeg(2 * Mathf.Atan(VisibleLines / 2f / EventCamera.DefaultProjection));
            }
            LoadInventory();
            Battle = new FieldBattle(this, Screen);
            Battle.ShowHud(HudShown);
            Battle.Defeated += defeated => { DropLoot(defeated); CheckConditions(0x08); };
            Battle.AshleyActed += () => _reclaimCheck = true;
            Battle.Felled += (felled, action) =>
            {
                CountKill();
                RecordKill(felled, action);
            };
            if (Player is { } enlisted)
            {
                var ashleyStats = _content.Data("player");
                var combatant = Battle.Enlist(enlisted, ashleyStats, false, false, false, LimbBones(0), LimbParts(0));
                combatant.OwnWeapon = Unarmed ? "" : (EquippedWeapon == null ? ashleyStats["weapon"].AsGodotDictionary() : EquippedWeapon["blade"].AsGodotDictionary()).GetValueOrDefault("asset", "").AsString();
                Arm(combatant, combatant.OwnWeapon);
                if (EquippedArmour.TryGetValue(-3, out var carried)) Arm(combatant, carried.GetValueOrDefault("asset", "").AsString(), shield: true);
                if (_playerHp >= 0) { combatant.Hp = _playerHp; combatant.Mp = _playerMp; }
                if (_playerDrawn) Battle.SetWeapon(enlisted.Id, 1);
                if (EquippedWeapon != null) Battle.EquipWeapon(combatant, EquippedWeapon["blade"].AsGodotDictionary(), EquippedWeapon["grip"].AsGodotDictionary(), EquippedWeapon.ContainsKey("entry") ? GemsOf(EquippedWeapon) : null);
                else if (Unarmed) Battle.EquipWeapon(combatant, new GDictionary { ["category"] = 10, ["damage_type"] = 1 }, new GDictionary());
                foreach (var (limb, record) in EquippedArmour) FieldBattle.EquipArmour(combatant, limb, record);
                combatant.Strength += Gains[0];
                combatant.Intelligence += Gains[1];
                combatant.Agility += Gains[2];
                combatant.MaxHp += Gains[3];
                combatant.MaxMp += Gains[4];
                if (_playerHp < 0) { combatant.Hp = combatant.MaxHp; combatant.Mp = combatant.MaxMp; }
            }
            Battle.Assign(Abilities);
            if (SpawnRoomActors() != Error.Ok) return;
            PlaceCrates();
            StartEvent("event_" + name);
        }
        finally
        {
            SlowestLoad = Math.Max(SlowestLoad, clock.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>The longest a room has taken to load and decode, in milliseconds.</summary>
    public double SlowestLoad { get; private set; }

    private void ShowNotice(string text)
    {
        var notice = new Label { Text = text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, AnchorRight = 1, AnchorBottom = 1, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        var layer = new CanvasLayer { Layer = 20 };
        layer.AddChild(notice);
        AddChild(layer);
    }

    private void Fail(string detail)
    {
        if (Failure.Length != 0) return;
        Failure = detail;
        GD.PushError(detail);
        ShowNotice("This development build cannot continue.\n\n" + detail);
    }

    /// <summary>Whether no event is running or pending, so the player would have control.</summary>
    public bool Settled => _events != null && !Active(0) && _pendingChain.Length == 0 && _pendingTransition == null && _door == null && (EventCamera.IdleMask & 0xff) == 0xff;

    /// <summary>What the event is waiting on: the view's own waits and the open dialogue boxes, for diagnosis.</summary>
    public string WaitingOn => $"waits [{string.Join(", ", _waits.ConvertAll(w => $"{_flows[w.Id.Slot]}.{w.Id.Command}"))}] dialogues [{string.Join(", ", Field.Dialogues.Keys)}] choice {(_choice != null)} completions {_completions.Count}";

    /// <summary>Where the main event is: its instruction; empty before it starts, "complete" once it has ended.</summary>
    public string EventNode => _events == null ? "" : Active(0) ? "i" + _events.Instruction(0) : "complete";

    /// <summary>Whether the running event's flow in <paramref name="slot"/> has started and not yet ended.</summary>
    private bool Active(int slot) =>
        _events != null && slot >= 0 && slot < _flows.Length && _events.Status(slot) is not (FlowStatus.Idle or FlowStatus.Complete or FlowStatus.Cancelled or FlowStatus.Faulted);

    /// <summary>Runs the event runtime; a script that breaks stops the field with its error.</summary>
    private void Drive(Action step)
    {
        try { step(); }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or InvalidCastException or IndexOutOfRangeException or NullReferenceException)
        {
            Fail("The room event stopped: " + error.Message);
        }
    }

    /// <summary>Takes control from the player and plays an event, as a room's condition does.</summary>
    public void Play(string eventName)
    {
        Controlled = false;
        _control?.Halt();
        StartEvent(eventName);
    }

    /// <summary>Starts an installed event (a room's own script or a chained event file) in the shared flow runtime.</summary>
    private void StartEvent(string eventName)
    {
        GDictionary data;
        try
        {
            data = _content.Data(eventName);
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException)
        {
            Fail(error.Message);
            return;
        }
        var events = data["events"].AsGodotArray();
        _scriptBalloons = data.GetValueOrDefault("balloons", new GDictionary()).AsGodotDictionary();
        _mainEvent = events[0].AsGodotDictionary()["id"].AsString();
        // Flag 0xac tells the event whether Ashley's weapon was drawn as it began; from play, 0xaf the octant the
        // gameplay camera was turned to and 0xb0 whether it stood far (BATTLE.PRG 0x80073718). MAP022 frames
        // Ashley from the side play saw her from (footage, Ep. 2 13:20: octant 3, the eye behind her right shoulder).
        Flags[172] = Battle?.Drawn == true ? 1 : 0;
        if (Controlled)
        {
            Flags[175] = (int)(Gameplay.Yaw / 512) & 7;
            Flags[176] = Gameplay.TargetDistance == GameplayCamera.Near ? 0 : 1;
        }
        _returnSuppressed = false;
        _skippable = _skipRequested = false;
        Controlled = false;
        EventCamera.ResetCurves();
        Events.Add(_mainEvent);
        if (!_programs.TryGetValue(eventName, out var compiled))
        {
            var (programs, ids, error) = _world.Compile(events);
            if (error.Length != 0) { Fail("The room event is invalid: " + error); return; }
            _programs[eventName] = compiled = (programs, ids);
        }
        // The previous event's waits and completions belong to its runtime, which this one replaces.
        _waits.Clear();
        _completions.Clear();
        _restarts.Clear();
        _world.BeginScript();
        _flows = compiled.Ids;
        _sections = new (long, int)[_flows.Length];
        Array.Fill(_sections, (-1L, -1));
        _events = new FlowRuntime<EventWorld, EventCommand>(_world, compiled.Programs, this);
        Drive(() => { _events.StartAction("start_" + _mainEvent); _events.Drain(); });
    }

    private DirectionalLight3D? _characterLight;

    private readonly List<FieldEffect> _effects = new();
    /// <summary>The effect an event started last, whose further parts it starts (a1).</summary>
    private FieldEffect? _eventEffect;

    /// <summary>Visual effects playing, for tests.</summary>
    public int EffectsPlaying => _effects.FindAll(effect => !effect.Idle).Count;

    /// <summary>
    /// Plays visual effect <paramref name="id"/> (an action record's byte 1) from a caster at a target, if the
    /// installation has it.
    /// </summary>
    public FieldEffect? PlayEffect(int id, EventActor caster, EventActor? target, bool fromOrigin = false) => PlayEffect(id, caster, target, fromOrigin, -1);

    /// <summary>
    /// Plays one of the resident small effects (E000, which BATTLE.PRG keeps at 0x800EC4BC): number n starts at script
    /// address 3 n. Numbers 23, 25, 40, 41 and 44 to 53 are drawn on the defender, so their caster and target swap.
    /// </summary>
    public FieldEffect? PlayResident(int number, EventActor caster, EventActor target, int bone = -1, int mode = 0) =>
        number is 23 or 25 or 40 or 41 or >= 44 and <= 53 ? PlayEffect(0, target, caster, false, number, bone, mode) : PlayEffect(0, caster, target, false, number, bone, mode);

    private FieldEffect? PlayEffect(int id, EventActor caster, EventActor? target, bool fromOrigin, int resident, int bone = -1, int mode = 0)
    {
        if (id <= 0 && resident < 0 || Field == null || !_content.Has($"effect_{id:d3}")) return null;
        var data = _content.Data($"effect_{id:d3}");
        Texture2D? atlas = data.ContainsKey("atlas") ? _content.Texture(data["atlas"].AsString()) : null;
        // An event's effect comes from the point (0, 0, 0) rather than from an actor, at the actor it was requested on
        // (BATTLE.PRG 0x8007BDC4; the other characters it lists are bystanders, whose shifts are not followed).
        var source = fromOrigin ? new FieldEffect.Entity { Point = Vector3.Zero } : new FieldEffect.Entity { Actor = caster };
        var targets = new List<FieldEffect.Entity> { new() { Actor = target ?? caster, Bone = bone } };
        var effect = new FieldEffect(data, atlas, source, targets, resident >= 0 ? resident * 3 : 0, name => _content.Texture(name), armed: fromOrigin)
        {
            Caster = fromOrigin ? caster : null,
            Mode = mode,
            Slot = slot => Actors.GetValueOrDefault("slot:" + slot),
            Bystanders = () => Actors.Values,
            ColourShift = (actor, operation, change, fade) => TransformPalette(actor?.Id ?? "room", operation, change, fade),
            Sound = key => PlaySound(key, 127),
            Rumble = (_, strength) => { foreach (int pad in Input.GetConnectedJoypads()) Input.StartJoyVibration(pad, Math.Min(1f, strength / 255f), Math.Min(1f, strength / 128f), 0.3f); },
        };
        Field.Geometry.AddChild(effect);
        _effects.Add(effect);
        return effect;
    }

    /// <summary>Whether the running event is inside a Start-skippable section (0d to 0e), and whether b7 2 asked to skip it.</summary>
    private bool _skippable, _skipRequested;

    public bool Skippable => _skippable;

    /// <summary>Count of skips taken, for tests.</summary>
    public int Skips { get; private set; }

    /// <summary>
    /// Skips the open section (BATTLE.PRG 0x800bfbb8): every flow waiting inside it lets go of its wait; the main flow
    /// goes on at the section's fix-up and the others end.
    /// </summary>
    public void Skip()
    {
        if (_events is not { } events || !_skippable) return;
        _skippable = false;
        _skipRequested = false;
        Skips++;
        // Every flow waiting inside a section lets go: each section's flows are interrupted in turn.
        var sections = new SortedSet<int>();
        for (int slot = 0; slot < _flows.Length; slot++)
            if (_sections[slot].Section >= 0 && Active(slot)) sections.Add(_sections[slot].Section);
        foreach (int section in sections)
        {
            _world.Skip = section;
            for (int slot = 0; slot < _flows.Length; slot++)
                if (_sections[slot].Section == section && Active(slot)) events.Invalidate(slot);
            Drive(events.Drain);
            _world.Skip = -1;
            if (Failure.Length != 0) return;
        }
    }

    /// <summary>Presents a flow's command; one that cannot be presented stops the field.</summary>
    public void Present(in PresentationReceipt receipt, in EventCommand command)
    {
        if (Failure.Length != 0) return;
        ref var section = ref _sections[receipt.Slot];
        if (section.Sequence != receipt.Sequence) section = (receipt.Sequence, -1);
        if (command.Args.TryGetValue("skip", out var skip)) section.Section = skip.AsInt32();
        var error = Present(receipt, command.Type, command.Args, command.Awaited);
        if (error != Error.Ok && Failure.Length == 0) Fail($"The room event's {command.Type} could not be presented ({error}).");
    }

    /// <summary>A flow let go of what it waited on (a skip, or the flow ending): the wait is dropped.</summary>
    public void Cancel(in PresentationReceipt receipt)
    {
        var id = receipt;
        _waits.RemoveAll(wait => wait.Id == id);
        _completions.Remove(id);
        Field.CancelDialogueWait(receipt);
    }

    private static Vector3 Retail(GArray point) => new(point[0].AsSingle(), point[1].AsSingle(), point[2].AsSingle());

    /// <summary>
    /// Resolves an actor reference: a slot names the actor created in it; a character id names the actor created with
    /// that character, falling back to slot 1 as the retail lookup does.
    /// </summary>
    private EventActor? Actor(GDictionary args, string key = "actor")
    {
        string reference = args.GetValueOrDefault(key, "").AsString();
        if (Actors.TryGetValue(reference, out var actor)) return actor;
        if (reference.StartsWith("id:", StringComparison.Ordinal))
        {
            foreach (var candidate in Actors.Values)
                if ("id:" + candidate.Character == reference) return candidate;
            return Actors.GetValueOrDefault("slot:1");
        }
        return null;
    }

    /// <summary>Presents one translated command. Retail positions are map units (x right, y down, z forward).</summary>
    private Error Present(PresentationReceipt id, string type, GDictionary args, bool awaited)
    {
        switch (type)
        {
            case "thread.start":
            {
                // A thread started again starts over, dropping what it waited on.
                int slot = Array.IndexOf(_flows, args["event"].AsString());
                if (slot < 0) return Error.DoesNotExist;
                // One that starts itself goes no further than this batch, and starts over once the frame's flows have run.
                if (slot == id.Slot) { _events!.Suspend(slot); _restarts.Add(slot); }
                else
                {
                    _events!.Cancel(slot);
                    _events.Start(slot);
                }
                return Error.Ok;
            }
            case "thread.join":
            {
                int slot = Array.IndexOf(_flows, args["event"].AsString());
                _waits.Add((id, () => !Active(slot)));
                return Error.Ok;
            }
            case "event.section":
                return Error.Ok;
            case "thread.stop":
                return Error.Ok;
            case "actor.spawn":
                return Spawn(args);
            case "actor.flags":
                // Retail 23 (0x800A087C) keeps bit 0 inverted as the actor's hidden bit and bits 1 and 2 as they are: an
                // actor is drawn with bits 0 and 1 both set. Its second byte, with bit 1 clear, shows (bit 0) or hides
                // the weapon in the actor's hand: op 23 hands it on as bit 11 of the flags, which the weapon pass reads.
                foreach (var actor in Targets(args))
                {
                    actor.Flags = args["flags"].AsInt32();
                    actor.Visible = (actor.Flags & 3) == 3;
                    // Bit 2 (kept at +8 bit 2) lets the actor run its frame: with it clear retail skips its animation
                    // and motion step (0x800AF064 takes 0x800AEAE8 instead), so it holds where it is (MAP017's knights
                    // stand through "Now, go!" until 71 lets them turn and go).
                    if (actor.Player != null) actor.Player.SpeedScale = (actor.Flags & 4) != 0 ? 1 : 0;
                    // Bit 6 draws its shadow (MAP172's dead man on the floor, 7, has none).
                    actor.ShadowShown = (actor.Flags & 0x40) != 0;
                    int weapon = args.GetValueOrDefault("weapon", 2).AsInt32();
                    if ((weapon & 2) == 0) actor.WeaponOut = (weapon & 1) != 0;
                }
                Battle?.ShowWeapons();
                return Error.Ok;
            case "actor.walk": case "actor.glide":
            {
                if (Actor(args) is not { } actor) return Error.Ok;
                var target = Retail(args["position"].AsGodotArray());
                bool snap = !args["relative"].AsBool() && target.Y == -32768;
                if (args["relative"].AsBool()) target += actor.Position;
                // A height of 0x8000 places the actor on the floor at its destination.
                if (snap) target.Y = FloorHeight(target);
                if (type == "actor.walk") actor.Walk(target, args["heading"].AsSingle(), args["speed"].AsInt32(), args["turn_speed"].AsInt32());
                else actor.Glide(target, args["speed"].AsInt32());
                return Error.Ok;
            }
            case "actor.turn":
                Actor(args)?.TurnTo(args["heading"].AsSingle(), args["direction"].AsInt32(), args["speed"].AsSingle());
                return Error.Ok;
            case "actor.rotate":
                Actor(args)?.Rotate(Retail(args["angles"].AsGodotArray()), args["relative"].AsBool(), args["speed"].AsInt32());
                return Error.Ok;
            case "screen.echo":
            {
                int ease = args.GetValueOrDefault("ease", 0).AsInt32(), frames = args.GetValueOrDefault("frames", 0).AsInt32();
                if (args.ContainsKey("preset")) _echo.Preset(args["preset"].AsInt32());
                if (args.ContainsKey("enabled")) _echo.Enable(args["enabled"].AsBool());
                if (args.ContainsKey("mode")) _echo.Blend(args["mode"].AsInt32());
                if (args.ContainsKey("scale")) { var v = args["scale"].AsGodotArray(); _echo.Zoom(v[0].AsSingle(), v[1].AsSingle(), ease, frames); }
                if (args.ContainsKey("tint")) { var v = args["tint"].AsGodotArray(); _echo.Tint(v[0].AsInt32(), v[1].AsInt32(), v[2].AsInt32(), ease, frames); }
                if (args.ContainsKey("offset")) { var v = args["offset"].AsGodotArray(); _echo.Offset(v[0].AsInt32(), v[1].AsInt32(), ease, frames); }
                return Error.Ok;
            }
            case "room.cutaway":
                _cutaway = args["enabled"].AsBool();
                return Error.Ok;
            case "room.first_person":
                _hideFirstPerson = args["enabled"].AsBool();
                return Error.Ok;
            case "palette.transform":
            {
                // Retail 70: a colour transform of the room's palettes or of actors' palettes.
                int op = args["op"].AsInt32(), fade = args["fade"].AsInt32();
                var delta = Retail(args["delta"].AsGodotArray()) / 31f;
                if (args["scope"].AsString() == "room") TransformPalette("room", op, delta, fade);
                else foreach (var actor in Targets(args)) TransformPalette(actor.Id, op, delta, fade);
                return Error.Ok;
            }
            case "actor.look":
                foreach (var looking in Targets(args))
                {
                    looking.LookSpeed = args["speed"].AsInt32() == 0 ? 0x1000 : args["speed"].AsInt32();
                    looking.LookPoint = args.ContainsKey("point") ? Retail(args["point"].AsGodotArray()) : null;
                    looking.LookTarget = args.ContainsKey("target") ? Actor(args, "target") : null;
                }
                return Error.Ok;
            case "actor.sway":
                foreach (var swaying in Targets(args)) { swaying.SwayPeriod = args["period"].AsInt32(); swaying.SwayCount = 0; }
                return Error.Ok;
            case "room.animation":
            {
                // A swinging group starts (or swings back), or holds where it is; the other kinds run on their own.
                int index = args["index"].AsInt32();
                foreach (var script in _groupScripts)
                    if (script.Index == index)
                    {
                        if (args["start"].AsBool()) { script.Running = true; script.Counter = 0; }
                        else script.Paused = !script.Paused;
                    }
                foreach (var swing in _swings)
                    if (swing.Index == index)
                    {
                        if (args["start"].AsBool()) { swing.Running = true; swing.Fresh = true; }
                        else swing.Paused = !swing.Paused;
                    }
                return Error.Ok;
            }
            case "map.unvisited":
                _unmapped = true;
                Visited.Remove((Zone, Map));
                return Error.Ok;
            case "actors.shown":
                // Retail b7 5: bit 0 of every actor's flags but Ashley's (slots 1 to 15), set or cleared.
                foreach (var actor in Actors.Values)
                {
                    if (actor.Id == "slot:0" || actor.Character == 0) continue;
                    actor.Flags = args["shown"].AsBool() ? actor.Flags | 1 : actor.Flags & ~1;
                    actor.Visible = (actor.Flags & 3) == 3;
                }
                return Error.Ok;
            case "room.platforms":
                if (args.ContainsKey("frozen")) _platformsFrozen = args["frozen"].AsInt32() != 0;
                if (args.ContainsKey("hold")) _platformHold = args["hold"].AsInt32();
                return Error.Ok;
            case "room.platform":
            {
                // A platform switched off hides its geometry group.
                var platforms = Document.GetValueOrDefault("platforms", new GArray()).AsGodotArray();
                int platform = args["platform"].AsInt32();
                if (platform >= platforms.Count) return Error.Ok;
                int group = platforms[platform].AsGodotDictionary()["group"].AsInt32();
                if (args["on"].AsBool()) _hiddenGroups.Remove(group); else _hiddenGroups.Add(group);
                if (platform < _platforms.Count) _platforms[platform].Off = !args["on"].AsBool();
                return Error.Ok;
            }
            case "room.group":
                if (args["hidden"].AsBool()) _hiddenGroups.Add(args["group"].AsInt32());
                else _hiddenGroups.Remove(args["group"].AsInt32());
                return Error.Ok;
            case "actor.scale":
                Actor(args)?.ScaleTo(Retail(args["scale"].AsGodotArray()) / 4096f, args["frames"].AsInt32());
                return Error.Ok;
            case "actor.animate":
            {
                // An animation the base set has no slot for plays animation 0 instead (BATTLE.PRG 0x800A0204 looks
                // it up again as 0), when there is one.
                string clip = $"{args["set"].AsString()}_{args["number"].AsInt32()}";
                if (Actor(args) is { } animated && !animated.HasClip(clip) && args["set"].AsString() == "base") clip = "base_0";
                Actor(args)?.Animate(clip, args["blend"].AsInt32(), args.GetValueOrDefault("skip", 0).AsInt32());
            }
                return Error.Ok;
            case "actor.queue":
                Actor(args)?.Queue($"{args["set"].AsString()}_{args["number"].AsInt32()}", args["blend"].AsInt32());
                return Error.Ok;
            case "actor.delete":
                // The retail target list ends at slot 1, so a reference that resolves to slot 1 deletes nothing.
                foreach (var actor in new List<EventActor>(Targets(args)))
                {
                    if (actor.Id == "slot:1") continue;
                    Actors.Remove(actor.Id);
                    foreach (var other in Actors.Values) if (other.Parent == actor) other.Parent = null;
                    var removed = Field.RemoveActor(actor.Id);
                    if (removed != Error.Ok) return removed;
                }
                return Error.Ok;
            case "text.duration":
                if (_boxes.TryGetValue(args["box"].AsInt32(), out var opened)) opened["open_frames"] = args["frames"];
                // A balloon already up (or closing) takes the new time too.
                if (_balloons.TryGetValue(args["box"].AsInt32(), out var retimed)) retimed.Retime(args["frames"].AsInt32());
                return Error.Ok;
            case "wait.button":
            {
                // Ends on a newly pressed button in the mask (L2, R2, L1, R1, triangle, circle, cross, square from bit 0)
                // or when the frames run out; 0 frames waits only for a button.
                int mask = args["mask"].AsInt32(), remaining = args["ticks"].AsInt32();
                bool timed = remaining > 0;
                _pressedButtons = 0;
                _waits.Add((id, () => (_pressedButtons & mask) != 0 || timed && --remaining <= 0));
                return Error.Ok;
            }
            case "actor.wait":
            {
                var actor = Actor(args);
                int mask = args["mask"].AsInt32();
                _waits.Add((id, () => actor == null || (actor.Waiting & mask) == 0));
                return Error.Ok;
            }
            case "actor.attach":
            {
                if (Actor(args) is not { } actor) return Error.Ok;
                actor.Parent = Actor(args, "target");
                actor.ParentBone = args["bone"].AsInt32();
                actor.Position = Vector3.Zero;
                actor.Angles = Vector3.Zero;
                return Error.Ok;
            }
            case "actor.tint":
                if (Actor(args) is { } tinted) Tint(tinted, Retail(args["color"].AsGodotArray()));
                return Error.Ok;
            case "camera.stage":
                EventCamera.Stage(Channel(args), Retail(args["position"].AsGodotArray()));
                return Error.Ok;
            case "camera.stage_bone":
            {
                if (Actor(args) is not { } actor) return Error.Ok;
                var offset = Retail(args["offset"].AsGodotArray());
                if (args["local"].AsBool()) offset = new Basis(Vector3.Up, actor.Facing * Mathf.Tau / 4096f) * offset;
                EventCamera.Stage(Channel(args), actor.BonePosition(args["bone"].AsInt32()) + offset);
                return Error.Ok;
            }
            case "camera.snap":
                EventCamera.Snap(Channel(args));
                return Error.Ok;
            case "camera.move":
            {
                Func<Vector3>? track = null;
                if (args["mode"].AsString() == "track" && Actor(args) is { } actor)
                {
                    int bone = args["bone"].AsInt32();
                    var offset = Retail(args["offset"].AsGodotArray());
                    track = () => actor.BonePosition(bone) + offset;
                }
                EventCamera.Move(Channel(args), args, track);
                return Error.Ok;
            }
            case "camera.wait":
            {
                int mask = args["mask"].AsInt32();
                _waits.Add((id, () => (EventCamera.IdleMask & mask) == mask));
                return Error.Ok;
            }
            case "camera.roll":
                EventCamera.SetRoll(args["angle"].AsInt32(), args["direction"].AsInt32(), args["ease"].AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            case "camera.lens":
                EventCamera.SetLens(args["property"].AsString(), args["value"].AsInt32(), args["ease"].AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            case "camera.shake":
                EventCamera.SetShake(args["cycles"].AsInt32(), args["amplitude"].AsInt32(), args["axes"].AsInt32(), args["envelope"].AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            case "screen.fade":
            {
                bool fadeOut = args["direction"].AsString() == "out";
                _fadeSpeed = (fadeOut ? 1 : -1) * args["speed"].AsInt32() / 255f;
                _fadeWhite = args["effect"].AsInt32() == 1;
                if (awaited) _waits.Add((id, () => _fade >= 1));
                return Error.Ok;
            }
            case "screen.display":
            {
                int switches = args["switches"].AsInt32();
                if ((switches & 3) < 2) _displayOn = (switches & 3) == 1;
                return Error.Ok;
            }
            case "text.box":
                _boxes[args["box"].AsInt32()] = args;
                return Error.Ok;
            case "text.show":
            {
                int box = args["box"].AsInt32();
                // An empty message the script does not wait on only times a shot (the US text left blank, as in the
                // vision of EVT 0082): retail shows no box for it.
                var empty = args["message"].AsGodotDictionary();
                if (!awaited && empty["text"].AsString().Length == 0 && empty.GetValueOrDefault("tokens", new GArray()).AsGodotArray().Count == 0)
                {
                    Field.CloseDialogue("box" + box);
                    return Error.Ok;
                }
                var geometry = _boxes.GetValueOrDefault(box, new GDictionary { ["x"] = 16, ["y"] = 136, ["width"] = 288, ["height"] = 56, ["flags"] = 0 });
                if (geometry.ContainsKey("anchor")) Anchor(geometry);
                // A box of no columns or lines takes the size of its message.
                if (geometry["width"].AsInt32() <= 10 || geometry["height"].AsInt32() <= 4)
                {
                    var lines = args["message"].AsGodotDictionary()["text"].AsString().Split('\n');
                    int longest = 1;
                    foreach (string line in lines) longest = Math.Max(longest, line.Length);
                    geometry = geometry.Duplicate();
                    geometry["width"] = Math.Min(304, longest * 12 + 10);
                    geometry["height"] = lines.Length * Installation.DialogueConversion.LineHeight + 4;
                    geometry["x"] = Math.Clamp(geometry["x"].AsInt32(), 4, 316 - geometry["width"].AsInt32());
                }
                int flags = geometry.GetValueOrDefault("flags", 0).AsInt32();
                if ((flags & 15) == 4)
                {
                    // A type-4 box is the system box (0x800CCF08 lays it out whatever the script's place): its lines
                    // from the script's line count, its header from the flags' second byte. It waits for a button.
                    _messageFrames = 0;
                    _scriptBox = box;
                    SystemBox.Open((geometry["height"].AsInt32() - 4) / Installation.DialogueConversion.LineHeight, (flags >> 8) & 15);
                    SystemBox.Text = SystemBox.Fill(WithNumbers(args["message"].AsGodotDictionary()));
                    if (awaited) _waits.Add((id, () => _scriptBox != box || Input.IsActionJustPressed("ps_circle") || Input.IsActionJustPressed("ps_cross")));
                    return Error.Ok;
                }
                bool balloon = (flags & 15) is 1 or 5 or 7;
                // The text sits centred down its box, its tallest page's lines (13 pixels each) in the middle; a centre
                // token (fb 04) centres each line across it too. Lines break only where the message does, as retail's do.
                var message = WithNumbers(args["message"].AsGodotDictionary());
                var tokens = message.GetValueOrDefault("tokens", new GArray()).AsGodotArray();
                bool centred = false;
                var kept = new GArray();
                var pageStarts = new List<int> { 0 };
                foreach (var value in tokens)
                {
                    var token = value.AsGodotDictionary();
                    string kind = token["type"].AsString();
                    if (kind == "centre") { centred = true; continue; }
                    if (kind == "page") pageStarts.Add(token["offset"].AsInt32());
                    kept.Add(token);
                }
                string text = message["text"].AsString();
                pageStarts.Add(text.Length);
                int tallest = 1;
                for (int page = 0; page + 1 < pageStarts.Count; page++)
                {
                    int start = Math.Min(pageStarts[page], text.Length), end = Math.Clamp(pageStarts[page + 1], start, text.Length);
                    tallest = Math.Max(tallest, text[start..end].TrimEnd('\n').Split('\n').Length);
                }
                int padding = Math.Max(2, (geometry["height"].AsInt32() - tallest * Installation.DialogueConversion.LineHeight) / 2);
                var style = new DialogueStyle(new Vector2(geometry["x"].AsInt32(), geometry["y"].AsInt32()), new Vector2(geometry["width"].AsInt32(), geometry["height"].AsInt32()),
                    new Vector2(5, padding), Frameless: balloon, ShowPortrait: false, Wrap: false, Alignment: centred ? HorizontalAlignment.Center : HorizontalAlignment.Left);
                var loaded = DialogueMessage.Load(new GDictionary { ["text"] = text, ["tokens"] = kept }, out var dialogue);
                if (loaded != Error.Ok) return loaded;
                var result = Field.OpenDialogue("box" + box, dialogue!, style);
                if (result != Error.Ok) return result;
                if (balloon) OpenBalloon(box, geometry, flags);
                if (!awaited) return Error.Ok;
                return Field.WaitForDialogue("box" + box, id, (in PresentationReceipt receipt, in DialogueAnswer _) => _completions.Add(receipt));
            }
            case "text.close":
            {
                int box = args["box"].AsInt32();
                if (_scriptBox >= 0 && (box < 0 || box == _scriptBox)) { SystemBox.Text = ""; _scriptBox = -1; }
                for (int i = 0; i < 8; i++)
                    if (box < 0 || box == i) Field.CloseDialogue("box" + i);
                return Error.Ok;
            }
            case "music.play":
            {
                if (!_musicSlots.TryGetValue(args["slot"].AsInt32().ToString(), out var asset)) return Error.Ok;
                // The same music carries on across rooms rather than restarting.
                if (asset == _musicAsset && MusicPlaying) return Error.Ok;
                _musicFade?.Kill();
                PlayMusic(asset, args["volume"].AsInt32() / 127f);
                return Error.Ok;
            }
            case "music.load":
                _musicSlots[args["slot"].AsInt32().ToString()] = args["music"].AsString();
                return Error.Ok;
            case "music.volume":
            {
                // Fades the playing music linearly in amplitude over the frames given, on the 30 Hz event clock.
                if (!MusicPlaying) return Error.Ok;
                _musicFade?.Kill();
                float to = args["volume"].AsInt32() / 127f;
                _musicFade = CreateTween();
                _musicFade.TweenMethod(Callable.From<float>(MusicLevel), _musicLevel, to, Math.Max(1, args["frames"].AsInt32()) / 30.0);
                if (to == 0) _musicFade.TweenCallback(Callable.From(_driver.Stop));
                return Error.Ok;
            }
            case "music.release":
            {
                // The slot's music stops at once and the slot is emptied.
                string slot = args["slot"].AsInt32().ToString();
                if (_musicSlots.TryGetValue(slot, out var released) && released == _musicAsset && MusicPlaying) { _musicFade?.Kill(); _driver.Stop(); }
                _musicSlots.Remove(slot);
                return Error.Ok;
            }
            case "music.stop":
            {
                // Waits while the slot's music is still fading out, then stops it.
                _musicSlots.TryGetValue(args["slot"].AsInt32().ToString(), out var asset);
                _waits.Add((id, () =>
                {
                    if (asset != _musicAsset || !MusicPlaying) return true;
                    if (_musicFade != null && _musicFade.IsRunning()) return false;
                    _driver.Stop();
                    return true;
                }));
                return Error.Ok;
            }
            case "sound.stop":
            {
                // As the game's 0x80045D64: every sound, a group's voices, or the one sound named (whatever its pan).
                if (args.GetValueOrDefault("all", false).AsBool()) { _driver.StopAllSounds(); _sounds.Clear(); return Error.Ok; }
                if (args.ContainsKey("group"))
                {
                    int group = args["group"].AsInt32();
                    if (args.GetValueOrDefault("release", false).AsBool()) _driver.ReleaseEventBank();
                    else _driver.StopSounds(group);
                    foreach (var (name, key) in new Dictionary<string, string>(_sounds))
                        if (_content.Sound(key).Group == group || group == 4 && _content.Sound(key).Group == 3) _sounds.Remove(name);
                    return Error.Ok;
                }
                StopSound(args["sound"].AsString());
                return Error.Ok;
            }
            case "sound.play":
                PlaySound(args["sound"].AsString(), args["volume"].AsInt32(), group: args.GetValueOrDefault("group", -1).AsInt32());
                return Error.Ok;
            case "actor.sound":
            {
                // One of the sounds in the actor's own bank, picked at random; ones its model lacks are left out.
                // It is panned by where the actor stands, as the driver pans it (BATTLE.PRG 0x800ae4fc).
                foreach (var speaker in Targets(args))
                {
                    var choices = new List<string>();
                    foreach (var number in args["sounds"].AsGodotArray())
                        if (_content.HasSound($"c{speaker.Character:X2}_{number.AsInt32()}_p128")) choices.Add($"c{speaker.Character:X2}_{number.AsInt32()}_p128");
                    if (choices.Count > 0) PlayActorSound(speaker, choices[Random.Shared.Next(choices.Count)]);
                }
                return Error.Ok;
            }
            case "sound.volume":
            {
                // The driver fades the one sound (the game's 0x80045BFC), whatever its pan.
                if (!_sounds.TryGetValue(args["sound"].AsString(), out var key)) return Error.Ok;
                var (bankName, bank, number, _, _) = _content.Sound(key);
                _driver.FadeSound(bankName, bank, number, args["volume"].AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            }
            case "weather.drift":
                _rain.Drifting = args["on"].AsBool();
                return Error.Ok;
            case "weather.set":
                _rain.Set(args["type"].AsInt32(), args["intensity"].AsInt32(), args["direction"].AsInt32(), args.GetValueOrDefault("strength", 1024).AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            case "pad.vibrate":
                foreach (int pad in Input.GetConnectedJoypads())
                {
                    if (args["pattern"].AsInt32() < 0) Input.StopJoyVibration(pad);
                    else Input.StartJoyVibration(pad, 0.4f, 0.6f, 0.4f);
                }
                return Error.Ok;
            case "skip.section":
                _skippable = args["open"].AsBool();
                if (!_skippable) _skipRequested = false;
                return Error.Ok;
            case "skip.request":
                _skipRequested = true;
                return Error.Ok;
            case "event.skipped":
            {
                // As retail's skip: the text boxes close, what the flows waited on is dropped, and the music stops
                // unless the section kept it.
                for (int i = 0; i < 8; i++) Field.CloseDialogue("box" + i);
                if (args["stop_music"].AsBool() && MusicPlaying) { _musicFade?.Kill(); _driver.Stop(); }
                return Error.Ok;
            }
            case "camera.pitch":
                Gameplay.Pitch = args["pitch"].AsSingle();
                return Error.Ok;
            case "effect.play":
                // The event's effect request replaces the last (0x800CF0E8 copies it into the one request slot).
                if (_eventEffect is { Armed: true } replaced && _effects.Remove(replaced)) replaced.QueueFree();
                if (Actor(args) is { } struck) _eventEffect = PlayEffect(args["effect"].AsInt32(), struck, struck, fromOrigin: true);
                return Error.Ok;
            case "effect.part":
                _eventEffect?.StartPart(args["part"].AsInt32());
                return Error.Ok;
            case "music.bar":
            {
                // Without music (or a driver) the bar never comes, so the scene goes on.
                int bar = args["bar"].AsInt32();
                _waits.Add((id, () => _driver.MusicBar is var at && (at < 0 || at >= bar)));
                return Error.Ok;
            }
            case "effect.wait":
                _waits.Add((id, () => _effects.TrueForAll(effect => effect.Idle)));
                return Error.Ok;
            case "effect.stop":
                // Every effect is cleared; the event's request stays loaded, so a later part can still start.
                foreach (var effect in _effects)
                    if (effect == _eventEffect && effect.Armed) effect.Clear();
                    else effect.QueueFree();
                _effects.RemoveAll(effect => effect != _eventEffect || !effect.Armed);
                if (_eventEffect is { Armed: false }) _eventEffect = null;
                return Error.Ok;
            case "map.entered":
                EnteredMarker = args["marker"].AsInt32();
                return Error.Ok;
            case "enemies.forget":
                ForgetEnemies(args["zone"].AsInt32(), args["map"].AsInt32());
                return Error.Ok;
            case "sound.ambient":
                // The resident ambience (group 2) is held where it is, or plays on.
                // Held, it stops; let go, the ambience that was playing starts again.
                if (args["held"].AsBool())
                {
                    foreach (var (name, key) in _sounds) if (name.StartsWith("resident_", StringComparison.Ordinal)) _heldAmbience.Add(key);
                    _driver.StopSounds(2);
                }
                else
                {
                    foreach (var key in _heldAmbience) PlaySound(key, 127);
                    _heldAmbience.Clear();
                }
                return Error.Ok;
            case "sky.show":
                _skyVisible = args["visible"].AsBool();
                return Error.Ok;
            case "sky.offset":
                _skyOffset = new Vector2(args["x"].AsSingle(), args["y"].AsSingle());
                return Error.Ok;
            case "sky.parallax":
                _skyParallax = new Vector2(args["x"].AsSingle(), args["y"].AsSingle());
                return Error.Ok;

            case "overlay.load":
                try
                {
                    _overlay.Load(_content.Overlay(args["overlay"].AsInt32()));
                    ShowBehind();
                }
                catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or System.Collections.Generic.KeyNotFoundException)
                {
                    Fail(error.Message);
                }
                return Error.Ok;
            case "overlay.set":
                _overlay.Set(args["layer"].AsInt32(), args["mode"].AsInt32(), new Vector2(args["x"].AsSingle(), args["y"].AsSingle()), args["scale"].AsSingle(), args["brightness"].AsSingle(), args.GetValueOrDefault("behind", false).AsBool());
                ShowBehind();
                return Error.Ok;
            case "overlay.move":
                _overlay.Move(args["layer"].AsInt32(), new Vector2(args["x"].AsSingle(), args["y"].AsSingle()), args["scale"].AsSingle(), args["brightness"].AsSingle(), args["ease"].AsInt32(), args["frames"].AsInt32());
                return Error.Ok;
            case "overlay.wait":
                _waits.Add((id, () => _overlay.Idle));
                return Error.Ok;
            case "overlay.free":
                _overlay.Clear();
                ShowBehind();
                return Error.Ok;
            case "ease.define":
                EventCamera.DefineCurve(args["slot"].AsInt32(), args["shape"].AsInt32(), args["power"].AsInt32());
                return Error.Ok;
            case "camera.anchor":
                EventCamera.Anchor(Actor(args) is { } anchor ? () => anchor.Position : null);
                return Error.Ok;
            case "camera.momentum":
                EventCamera.Momentum(args["look_at"].AsBool(), args["eye"].AsBool());
                return Error.Ok;
            case "camera.stop":
                EventCamera.Stop();
                return Error.Ok;
            case "music.halt":
                _musicFade?.Kill();
                _driver.Stop();
                return Error.Ok;
            case "music.crossfade":
            {
                // The playing music fades out over its frames and the slot's starts at the volume, fading in.
                if (!_musicSlots.TryGetValue(args["slot"].AsInt32().ToString(), out var next)) return Error.Ok;
                _musicFade?.Kill();
                float to = Math.Max(args["volume"].AsInt32(), 1) / 127f;
                // The driver plays one sequence at a time, so the new piece replaces the old and fades in.
                PlayMusic(next, 1 / 127f);
                _musicFade = CreateTween();
                _musicFade.TweenMethod(Callable.From<float>(MusicLevel), 1 / 127f, to, Math.Max(1, args["fade_in"].AsInt32()) / 30.0);
                return Error.Ok;
            }
            case "menu.results":
            {
                // State flag 0xB6 names MENUF's screen; 0xAB below 3 (the opening) shows none of them.
                int op = args["op"].AsInt32();
                if (op is >= 10 and <= 12) SetFlag(0xB6, new[] { 6, 4, 5 }[op - 10]);
                else if (op == 13) SetFlag(0xB6, 1);
                else if (op == 14 && FlagValue(0xB6) == 1) SetFlag(0xB6, 2);
                // A press from before the screen opened is not one it takes.
                _pressedButtons &= ~0x20;
                bool results = op is not (13 or 15) && FlagValue(0xAB) >= 3 && FlagValue(0xB6) == 0 && OpenResults();
                int dim = 0;
                _waits.Add((id, () =>
                {
                    if (results && _results!.Showing)
                    {
                        _dim.Color = new Color(0, 0, 0, ResultsDim * Math.Min(1, ++dim / 8f));
                        bool circle = (_pressedButtons & 0x20) != 0;
                        _pressedButtons &= ~0x20;
                        if (_results.Step(circle)) return false;
                        Gain(_results.Result);
                    }
                    if (results && _dim.Color.A > 0) { _dim.Color = _dim.Color with { A = Math.Max(0, _dim.Color.A - ResultsDim / 8) }; return false; }
                    if (op is not (13 or 15)) SetFlag(0xB6, 0);
                    return true;
                }));
                return Error.Ok;
            }
            case "screen.wait":
                // Waits while the screen is still fading.
                _waits.Add((id, () => _fadeSpeed == 0 || _fadeSpeed > 0 && _fade >= 1 || _fadeSpeed < 0 && _fade <= 0));
                return Error.Ok;
            case "actor.collapse":
                foreach (var falling in Targets(args))
                {
                    if (falling.Id == "slot:1") continue;
                    FieldBattle.Combatant? fighter = null;
                    Battle?.Combatants.TryGetValue(falling.Id, out fighter);
                    string set = fighter?.Set ?? "base";
                    if (args["animate"].AsBool()) falling.Animate($"{set}_{FieldBattle.Death}", 6, once: true);
                    if (fighter != null) fighter.Dead = true;
                }
                return Error.Ok;
            case "actor.tile_flags":
            {
                // Tile x, floor level and tile z, into three flags; a missing actor sets only the first, to 0xff.
                int flag = args["flag"].AsInt32();
                var values = Actor(args) is { } placed ? new[] { (int)Mathf.Floor(placed.Position.X / 128), 0, (int)Mathf.Floor(placed.Position.Z / 128) } : new[] { 0xFF };
                for (int i = 0; i < values.Length; i++) SetFlag(flag + i, values[i]);
                _completions.Add(id);
                return Error.Ok;
            }
            case "text.integer":
            {
                int flag = args["flag"].AsInt32(), low = FlagValue(flag), high = FlagValue(flag + 1);
                _integers[args["slot"].AsInt32() % 10] = args["mode"].AsInt32() switch
                {
                    1 => (sbyte)low, 2 => low | high << 8, 3 => (short)(low | high << 8), _ => low,
                };
                return Error.Ok;
            }
            case "actor.measure":
            {
                if (Actor(args) is { } from && Actor(args, "target") is { } to)
                {
                    float dx = from.Position.X - to.Position.X, dz = from.Position.Z - to.Position.Z;
                    int distance = (int)(Mathf.Sqrt(dx * dx + dz * dz) / args["unit"].AsInt32());
                    int flag = args["flag"].AsInt32();
                    SetFlag(flag, args["bearing"].AsBool() ? Math.Min(255, distance) : distance);
                    if (args["bearing"].AsBool()) SetFlag(flag + 1, (int)(Mathf.PosMod(from.FacingToward(to.Position), 4096) / 16));
                }
                _completions.Add(id);
                return Error.Ok;
            }
            case "ability.learn":
            {
                int action = args["action"].AsInt32();
                bool known = Abilities.Contains(action);
                if (args["learn"].AsBool())
                {
                    if (!known)
                    {
                        Abilities.Add(action);
                        Battle?.Assign(Abilities);
                    }
                    SetFlag(0xB1, known ? 0 : 1);
                }
                else SetFlag(0xB1, known ? 1 : 0);
                _completions.Add(id);
                return Error.Ok;
            }
            case "timer":
            {
                var time = args["time"].AsGodotArray();
                _timer = !args["running"].AsBool() ? 0 : args["count_up"].AsBool() ? 1 : 2;
                (_timerHours, _timerMinutes, _timerSeconds, _timerTicks) = _timer == 2 ? (time[0].AsInt32(), time[1].AsInt32(), time[2].AsInt32(), 0) : (0, 0, 0, 0);
                ShowTimer();
                _completions.Add(id);
                return Error.Ok;
            }
            case "game.ending":
                // The story is over: the ending plays, then the title.
                GameEnded = true;
                if (!KeepScene) _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(Wol.Games.Vagrant.Scenes.Ending.Ending.Scene);
                return Error.Ok;
            case "timer.hold":
                // Holds the timer where it stands (its mode kept aside) or lets it run on again.
                if (args["held"].AsBool()) { if (_timer != 0) { _heldTimer = _timer; _timer = 0; } }
                else if (_heldTimer != 0) { _timer = _heldTimer; _heldTimer = 0; }
                _completions.Add(id);
                return Error.Ok;
            case "hud.show":
                HudShown = args["visible"].AsBool();
                Battle?.ShowHud(HudShown);
                _completions.Add(id);
                return Error.Ok;
            case "actor.weapon_model":
                // An event puts a weapon model in a character's hand, takes it away (no asset), or gives back its own.
                foreach (var armed in Targets(args))
                    if (Battle != null && Battle.Combatants.TryGetValue(armed.Id, out var holder))
                        Arm(holder, args["own"].AsBool() ? holder.OwnWeapon : args.GetValueOrDefault("asset", "").AsString());
                Battle?.ShowWeapons();
                _completions.Add(id);
                return Error.Ok;
            case "flag.set":
                SetFlag(args["flag"].AsInt32(), args["value"].AsInt32());
                _completions.Add(id);
                return Error.Ok;
            case "flag.combine":
            {
                int a = args["flag"].AsInt32(), b = args["other"].AsInt32(), left = FlagValue(a), right = FlagValue(b);
                switch (args["operation"].AsInt32())
                {
                    case 0: SetFlag(a, left + right); break;
                    case 1: SetFlag(a, left - right); break;
                    case 2: SetFlag(a, left * right); break;
                    case 3: if (right != 0) SetFlag(a, left / right); break;
                    case 4: if (right != 0) SetFlag(a, left % right); break;
                    case 5: SetFlag(a, left & right); break;
                    case 6: SetFlag(a, left | right); break;
                    case 7: SetFlag(a, right); break;
                    case 8: SetFlag(a, Math.Abs(left - right)); break;
                    case 9: SetFlag(a, right); SetFlag(b, left); break;
                }
                _completions.Add(id);
                return Error.Ok;
            }
            case "flag.item":
                // Items are not kept yet: only item 0, which always counts as held.
                SetFlag(args["flag"].AsInt32(), args["item"].AsInt32() == 0 ? 1 : 0);
                _completions.Add(id);
                return Error.Ok;
            case "battle.suspend":
                if (Battle != null)
                    foreach (var fighter in Battle.Combatants.Values) if (fighter != Battle.Ashley) fighter.Suspended = args["suspended"].AsBool();
                return Error.Ok;
            case "text.choose":
                BeginChoice(id, args["mask"].AsInt32());
                return Error.Ok;
            case "actor.face_actor":
            {
                if (Actor(args) is not { } turner || Actor(args, "target") is not { } faced) return Error.Ok;
                var centre = new Vector3(Mathf.Floor(faced.Position.X / 128) * 128 + 64, 0, Mathf.Floor(faced.Position.Z / 128) * 128 + 64);
                turner.TurnTo(turner.FacingToward(centre), 0, args["speed"].AsSingle());
                return Error.Ok;
            }
            case "room.swap_groups":
            {
                // Swaps two geometry groups' state, which carries whether each is hidden.
                int first = args["first"].AsInt32(), second = args["second"].AsInt32();
                bool firstHidden = _hiddenGroups.Contains(first), secondHidden = _hiddenGroups.Contains(second);
                if (secondHidden) _hiddenGroups.Add(first); else _hiddenGroups.Remove(first);
                if (firstHidden) _hiddenGroups.Add(second); else _hiddenGroups.Remove(second);
                var groups = Document.GetValueOrDefault("room_groups", new GArray()).AsGodotArray();
                if (first < groups.Count && second < groups.Count) (groups[first], groups[second]) = (groups[second], groups[first]);
                return Error.Ok;
            }
            case "tile.block":
            {
                // A blocked tile has blocking class 5 (bits 10 to 12 of its attributes); unblocking makes it plain floor.
                var tile = args["tile"].AsGodotArray();
                int width = _columns / 4, index = tile[1].AsInt32() * width + tile[0].AsInt32();
                if (index >= 0 && index < _attributes.Length) _attributes[index] = (_attributes[index] & ~(7L << 10)) | (args["blocked"].AsBool() ? 5L << 10 : 0);
                return Error.Ok;
            }
            case "screen.transition":
                _transitionFade = (args["effect"].AsInt32(), args["speed"].AsInt32());
                return Error.Ok;
            case "actor.materialise":
                if (Actor(args) is { } shown && args["state"].AsInt32() is 1 or 2) shown.Visible = args["state"].AsInt32() == 2;
                return Error.Ok;
            case "actor.redraw":
            {
                // Only when Ashley's weapon was out as the event began; set 0 changes it at once.
                if (FlagValue(172) == 0 || Player is not { } ashley || Battle == null) { _completions.Add(id); return Error.Ok; }
                bool drawn = args["drawn"].AsBool();
                Battle.SetWeapon(ashley.Id, drawn ? 1 : 0);
                int set = args["set"].AsInt32();
                string battleSet = Battle.Ashley?.BattleSet ?? "battle";
                if (set == 0 || !ashley.Animate($"{battleSet}_{FieldBattle.Draw + (set - 1) * 2 + (drawn ? 0 : 1)}", 10, once: true)) { _completions.Add(id); return Error.Ok; }
                _waits.Add((id, () =>
                {
                    if ((ashley.Waiting & EventActor.Animating) != 0) return false;
                    ashley.Animate((drawn ? battleSet + "_" : "base_") + FieldPlayer.Idle, 6);
                    return true;
                }));
                return Error.Ok;
            }
            case "actor.path":
            {
                if (Actor(args) is not { } walker) return Error.Ok;
                var point = args["position"].AsGodotArray();
                var goal = new Vector3(point[0].AsSingle(), 0, point[1].AsSingle());
                goal.Y = FloorHeight(goal);
                var route = Path(walker.Position, goal);
                walker.Follow(route.Count > 0 ? route : new List<Vector3> { goal });
                return Error.Ok;
            }
            case "actor.face_tile":
            {
                if (Actor(args) is not { } turner) return Error.Ok;
                var tile = args["tile"].AsGodotArray();
                var centre = new Vector3(tile[0].AsInt32() * 128 + 64, 0, tile[1].AsInt32() * 128 + 64);
                turner.TurnTo(turner.FacingToward(centre), 0, args["speed"].AsSingle());
                return Error.Ok;
            }
            case "actor.weapon":
                foreach (var armed in Targets(args)) Battle?.SetWeapon(armed.Id, args["mode"].AsInt32());
                if (_control != null && Battle?.Ashley is { } wielder) _control.Set = Battle.Drawn ? wielder.BattleSet : "base";
                return Error.Ok;
            case "actor.restore":
                if (Actor(args) is { } restored && Battle != null && Battle.Combatants.TryGetValue(restored.Id, out var healed)) { healed.Hp = healed.MaxHp; healed.Mp = healed.MaxMp; }
                return Error.Ok;
            case "actor.defeated":
                foreach (var fallen in Targets(args))
                    if (Battle != null && Battle.Combatants.TryGetValue(fallen.Id, out var out_)) out_.Dead = args["defeated"].AsBool() || out_.Hp == 0;
                return Error.Ok;
            case "camera.return":
                if (args.GetValueOrDefault("automatic", false).AsBool())
                {
                    if (_returnSuppressed || _pendingChain.Length != 0) return Error.Ok;
                }
                else if (args.GetValueOrDefault("immediate", false).AsBool())
                {
                    // At once, with the yaw and distance the gameplay camera had as the event began.
                    _returnSuppressed = true;
                    ReturnCamera(immediate: true);
                    return Error.Ok;
                }
                else
                {
                    _returnSuppressed = true;
                    if (args["yaw"].AsInt32() >= 0) Gameplay.Yaw = args["yaw"].AsInt32();
                    if (args["distance"].AsInt32() > 0) Gameplay.Distance = Gameplay.TargetDistance = args["distance"].AsInt32();
                }
                ReturnCamera();
                return Error.Ok;
            case "field.transition":
                _pendingTransition = args;
                return Error.Ok;
            case "event.chain":
                _pendingChain = "event_" + args["event"].AsString();
                return Error.Ok;
            default:
                return Error.Unavailable;
        }
    }

    /// <summary>
    /// Opens a retail speech balloon behind a box's text. Types 1 and 5 are the plain and round balloons (shapes 0
    /// and 2); type 7 names its shape. The text shows once the balloon has opened, after the retail open time.
    /// </summary>
    /// <summary>The running script's own balloon shapes (9 to 15), by number.</summary>
    private GDictionary _scriptBalloons = new();

    private void OpenBalloon(int box, GDictionary geometry, int flags)
    {
        try
        {
            _balloonShapes ??= _content.Data("dialogue_balloons");
            _paper ??= _content.ScreenTexture(_balloonShapes["paper"].AsString());
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException)
        {
            Fail(error.Message);
            return;
        }
        int type = flags & 15, shape = type == 1 ? 0 : type == 5 ? 2 : (flags >> 8) & 15;
        var shapes = _balloonShapes["shapes"].AsGodotArray();
        // Shapes 9 to 15 are the running script's own; a box with none there has no balloon (0x800CCE54).
        var outline = shape < shapes.Count ? shapes[shape].AsGodotDictionary() : _scriptBalloons.GetValueOrDefault(shape.ToString()).AsGodotDictionary();
        if (outline == null || outline.Count == 0) return;
        if (_balloons.Remove(box, out var previous)) previous.QueueFree();
        var balloon = new EventBalloon { Name = "Balloon" + box };
        // Open times in frames: {10, 20, 10, 8} by the flags' speed field, unless the script set one (1d).
        int frames = geometry.ContainsKey("open_frames") ? geometry["open_frames"].AsInt32() : ((0x45A5 >> (((flags >> 4) & 3) * 4)) & 0xF) * 2;
        var rect = new Rect2(geometry["x"].AsSingle(), geometry["y"].AsSingle(), geometry["width"].AsSingle(), geometry["height"].AsSingle());
        balloon.Configure(outline, _paper, rect, flags, frames);
        var panel = Field.Dialogues["box" + box].Panel;
        Field.DialogueLayer.AddChild(balloon);
        Field.DialogueLayer.MoveChild(balloon, panel.GetIndex());
        panel.Visible = false;
        _balloons[box] = balloon;
    }

    /// <summary>Opens and closes balloons by one event frame, showing text once open and closing after its dialogue.</summary>
    private void StepBalloons()
    {
        foreach (var (box, balloon) in new List<KeyValuePair<int, EventBalloon>>(_balloons))
        {
            bool showing = Field.Dialogues.TryGetValue("box" + box, out var dialogue);
            if (!showing) balloon.Closing = true;
            balloon.Step();
            if (showing && balloon.Open) dialogue!.Panel.Visible = true;
            if (balloon.Closed) { _balloons.Remove(box); balloon.QueueFree(); }
        }
    }

    /// <summary>
    /// Stages the gameplay view around Ashley on the event camera and moves there: the look-at in a line and the eye
    /// round it, both eased in and out, at 40 units a frame of their combined travel.
    /// </summary>
    private void ReturnCamera(bool immediate = false)
    {
        if (Player is not { } ashley) return;
        Gameplay.Place(ashley.Position);
        var look = Gameplay.LookAt;
        var eye = Gameplay.Eye;
        int frames = (int)(((eye - EventCamera.EyePosition).Length() + (look - EventCamera.LookAtPosition).Length()) / 40);
        EventCamera.Stage(EventCamera.LookAt, look);
        EventCamera.Stage(EventCamera.Eye, eye);
        // Before any event has placed the camera in this room it already stands where play puts it (the room's entry
        // script hands control back at once in retail), so there is nothing to ease back from.
        if (immediate || !EventCamera.Active) { EventCamera.Snap(EventCamera.LookAt); EventCamera.Snap(EventCamera.Eye); return; }
        if (frames == 0) return;
        EventCamera.Move(EventCamera.LookAt, new GDictionary { ["mode"] = "line", ["ease"] = 2, ["frames"] = frames });
        EventCamera.Move(EventCamera.Eye, new GDictionary { ["mode"] = "orbit", ["yaw_ease"] = 2, ["radius_ease"] = 2, ["height_ease"] = 2, ["frames"] = frames });
    }

    private static int Channel(GDictionary args) => args["channel"].AsString() == "eye" ? EventCamera.Eye : EventCamera.LookAt;

    /// <summary>
    /// A palette transform in effect: every colour of the palettes is <c>scale * original + grey * grey(original) +
    /// offset</c> in 5-bit units over 31 (grey is (2r + 3g + b) / 6), clamped, fading from one state to the next over
    /// event frames.
    /// </summary>
    private sealed class Palette
    {
        public float Scale = 1, Grey;
        public Vector3 Offset;
        public (float Scale, float Grey, Vector3 Offset) From, To;
        public int Frames, Elapsed;
        public readonly Dictionary<StandardMaterial3D, ShaderMaterial> Shaders = new();
        public bool Identity => Scale == 1 && Grey == 0 && Offset == Vector3.Zero;
    }

    /// <summary>Palette transforms by what they cover: "room", or an actor's reference.</summary>
    private readonly Dictionary<string, Palette> _palettes = new();

    /// <summary>
    /// Starts a retail palette operation from the state now shown: 0, 10 and 14 add the change to the current colours,
    /// 1 halves them first, 2 and 3 take their grey (3 halved), 4 and 9 add it to the original colours, 5 halves them,
    /// 6 and 7 take their grey (7 halved), 8 and 15 restore them, 12 inverts the current colours, 13 takes three
    /// quarters of the originals, and 11 stops a fade where it is. The fade is retail's (the executable's stepper,
    /// 0x800483FC): 0 changes the colours at once; otherwise they move in 16 steps, one every fade / 8 frames, from 8
    /// up, and from below 8 in steps of 2, 4 or 8 a frame (fade 4 to 7, 2 and 3, 1), so 8, 4 or 2 frames.
    /// </summary>
    private void TransformPalette(string target, int op, Vector3 delta, int fade)
    {
        fade &= 0xFF;
        int frames = fade == 0 ? 0 : fade >= 8 ? 16 * (fade >> 3) : fade >= 4 ? 8 : fade >= 2 ? 4 : 2;
        if (!_palettes.TryGetValue(target, out var palette)) _palettes[target] = palette = new Palette();
        float scale = palette.Scale, grey = palette.Grey;
        var offset = palette.Offset;
        float offsetGrey = (2 * offset.X + 3 * offset.Y + offset.Z) / 6;
        (float, float, Vector3) to = op switch
        {
            1 => (scale / 2, grey / 2, offset / 2 + delta),
            2 => (0, scale + grey, new Vector3(offsetGrey, offsetGrey, offsetGrey) + delta),
            3 => (0, (scale + grey) / 2, new Vector3(offsetGrey, offsetGrey, offsetGrey) / 2 + delta),
            4 or 9 => (1, 0, delta),
            5 => (0.5f, 0, delta),
            6 => (0, 1, delta),
            7 => (0, 0.5f, delta),
            8 or 15 => (1, 0, Vector3.Zero),
            12 => (-scale, -grey, Vector3.One - offset + delta),
            13 => (0.75f, 0, delta),
            _ => (scale, grey, offset + delta),
        };
        if (op == 11) to = (scale, grey, offset);
        palette.From = (scale, grey, offset);
        palette.To = to;
        palette.Frames = op == 11 ? 0 : frames;
        palette.Elapsed = 0;
        if (palette.Frames == 0) (palette.Scale, palette.Grey, palette.Offset) = to;
        ShowPalette(target, palette);
    }

    private void StepPalettes()
    {
        foreach (var (target, palette) in _palettes)
        {
            if (palette.Elapsed < palette.Frames)
            {
                float t = (float)++palette.Elapsed / palette.Frames;
                palette.Scale = Mathf.Lerp(palette.From.Scale, palette.To.Scale, t);
                palette.Grey = Mathf.Lerp(palette.From.Grey, palette.To.Grey, t);
                palette.Offset = palette.From.Offset.Lerp(palette.To.Offset, t);
            }
            if (!palette.Identity || palette.Shaders.Count > 0) ShowPalette(target, palette);
        }
    }

    /// <summary>
    /// Draws a palette transform. Each covered surface takes a copy of its material's own shader with the transform
    /// applied to the texel (in display space, as the palette holds it) before the vertex colour; the identity gives
    /// the surface back its material.
    /// </summary>
    private void ShowPalette(string target, Palette palette)
    {
        if (Field == null) return;
        var nodes = new List<Node3D>();
        foreach (var (key, node) in Field.Actors)
            if (target == "room" ? int.TryParse(key, out _) : key == target) nodes.Add(node);
        bool identity = palette.Identity;
        foreach (var root in nodes)
            foreach (var found in root.FindChildren("*", "MeshInstance3D", true, false))
            {
                var mesh = (MeshInstance3D)found;
                for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                {
                    if (mesh.Mesh.SurfaceGetMaterial(surface) is not StandardMaterial3D material) continue;
                    if (identity) { mesh.SetSurfaceOverrideMaterial(surface, null); continue; }
                    if (!palette.Shaders.TryGetValue(material, out var shader))
                    {
                        if (PaletteShader(material) is not { } made) continue;
                        palette.Shaders[material] = shader = made;
                    }
                    // The material's colour and texture scroll can change while the copy stands in for it.
                    shader.SetShaderParameter("albedo", material.AlbedoColor);
                    shader.SetShaderParameter("uv1_offset", material.Uv1Offset);
                    shader.SetShaderParameter("distance_fade_min", material.DistanceFadeMinDistance);
                    shader.SetShaderParameter("palette_scale", palette.Scale);
                    shader.SetShaderParameter("palette_grey", palette.Grey);
                    shader.SetShaderParameter("palette_offset", palette.Offset);
                    mesh.SetSurfaceOverrideMaterial(surface, shader);
                }
            }
        if (identity) palette.Shaders.Clear();
    }

    /// <summary>
    /// A shader standing in for one of the field's standard materials (see the engine's FieldScene: vertex colour as
    /// albedo, an optional texture cut out at half alpha or blended, unshaded or rough; and this view's sRGB vertex
    /// colours and near cut) with the palette transform applied to its texel.
    /// </summary>
    private static ShaderMaterial? PaletteShader(StandardMaterial3D material)
    {
        // Untextured polygons take no palette; billboards keep their material.
        if (material.BillboardMode != BaseMaterial3D.BillboardModeEnum.Disabled || material.AlbedoTexture == null) return null;
        var modes = new List<string>
        {
            material.BlendMode switch { BaseMaterial3D.BlendModeEnum.Add => "blend_add", BaseMaterial3D.BlendModeEnum.Sub => "blend_sub", _ => "blend_mix" },
            material.CullMode switch { BaseMaterial3D.CullModeEnum.Back => "cull_back", BaseMaterial3D.CullModeEnum.Front => "cull_front", _ => "cull_disabled" },
            material.DepthDrawMode == BaseMaterial3D.DepthDrawModeEnum.Disabled ? "depth_draw_never" : "depth_draw_opaque",
        };
        if (material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded) modes.Add("unshaded");
        string filter = material.TextureFilter switch
        {
            BaseMaterial3D.TextureFilterEnum.Linear => "filter_linear",
            BaseMaterial3D.TextureFilterEnum.NearestWithMipmaps => "filter_nearest_mipmap",
            BaseMaterial3D.TextureFilterEnum.LinearWithMipmaps => "filter_linear_mipmap",
            _ => "filter_nearest",
        };
        string alpha = material.Transparency switch
        {
            BaseMaterial3D.TransparencyEnum.AlphaScissor => "\tALPHA = albedo.a * texel.a;\n\tALPHA_SCISSOR_THRESHOLD = 0.5;\n",
            BaseMaterial3D.TransparencyEnum.Alpha => "\tALPHA = albedo.a * texel.a;\n",
            _ => "",
        };
        string code = $$"""
            shader_type spatial;
            render_mode {{string.Join(", ", modes)}};
            uniform vec4 albedo : source_color = vec4(1.0);
            uniform sampler2D texture_albedo : source_color, {{filter}}, repeat_enable;
            uniform vec3 uv1_offset = vec3(0.0);
            uniform float distance_fade_min = 0.0;
            uniform float palette_scale = 1.0;
            uniform float palette_grey = 0.0;
            uniform vec3 palette_offset = vec3(0.0);

            vec3 palette_apply(vec3 linear_colour) {
            	vec3 c = mix(1.055 * pow(linear_colour, vec3(1.0 / 2.4)) - 0.055, linear_colour * 12.92, lessThan(linear_colour, vec3(0.0031308)));
            	c = clamp(c * palette_scale + vec3(dot(c, vec3(2.0, 3.0, 1.0) / 6.0)) * palette_grey + palette_offset, 0.0, 1.0);
            	return mix(pow((c + 0.055) / 1.055, vec3(2.4)), c / 12.92, lessThan(c, vec3(0.04045)));
            }

            void vertex() {
            	UV = UV + uv1_offset.xy;
            {{(material.VertexColorIsSrgb ? "\tif (!OUTPUT_IS_SRGB) { COLOR.rgb = mix(pow((COLOR.rgb + vec3(0.055)) * (1.0 / (1.0 + 0.055)), vec3(2.4)), COLOR.rgb * (1.0 / 12.92), lessThan(COLOR.rgb, vec3(0.04045))); }\n" : "")}}}

            void fragment() {
            {{(material.DistanceFadeMode != BaseMaterial3D.DistanceFadeModeEnum.Disabled ? "\tif (-VERTEX.z < distance_fade_min) { discard; }\n" : "")}}	vec4 texel = texture(texture_albedo, UV);
            	texel.rgb = palette_apply(texel.rgb);
            	texel *= COLOR;
            	ALBEDO = albedo.rgb * texel.rgb;
            	ROUGHNESS = 1.0;
            {{alpha}}}
            """;
        var shader = new ShaderMaterial { Shader = new Shader { Code = code } };
        shader.SetShaderParameter("texture_albedo", material.AlbedoTexture);
        return shader;
    }

    /// <summary>Plays one of an actor's own sounds: panned by where the actor stands on screen, at full volume whatever the distance.</summary>
    public void PlayActorSound(EventActor speaker, string sound)
    {
        if (!_content.HasSound(sound)) return;
        int pan = 0x80;
        if (Field?.Camera is { } camera && camera.IsInsideTree() && !camera.IsPositionBehind(speaker.Node.GlobalPosition))
        {
            float across = camera.UnprojectPosition(speaker.Node.GlobalPosition).X / Math.Max(1, GetViewport().GetVisibleRect().Size.X);
            pan = Math.Clamp((int)(across * 255), 0, 255);
        }
        PlaySound(sound, 127, pan);
    }

    /// <summary>The resident sounds the menus play: the cursor's change, a choice made, leaving, a refusal and a file written.</summary>
    public const int MenuChange = 4, MenuSelect = 5, MenuLeave = 6, MenuInvalid = 7, MenuFileDone = 8;

    /// <summary>How many menu sounds have played, for the checks.</summary>
    public int MenuSounds { get; private set; }

    /// <summary>A menu's sound: resident sound <paramref name="id"/> on the voices 1 to 6 in turn, centred and at full volume, as the executable's 0x7E player plays it.</summary>
    public void MenuSound(int id)
    {
        MenuSounds++;
        PlaySound($"effect_{id}_p128", 127);
    }

    /// <summary>Plays an installed sound through the driver; a sound the installation could not place in a bank plays nothing.</summary>
    private void PlaySound(string sound, int volume, int pan = -1, int group = -1)
    {
        if (!_content.HasSound(sound)) return;
        try
        {
            var (bankName, bank, id, soundPan, soundGroup) = _content.Sound(sound);
            if (pan < 0) pan = soundPan;
            if (group < 0) group = soundGroup;
            if (bank == null) _driver.PlayResident(id, group, pan, volume);
            else _driver.PlaySound(bankName, bank, id, group, pan, volume);
            // A playing sound is addressed without its pan, as the retail volume command addresses it.
            string playing = sound[..sound.LastIndexOf("_p", StringComparison.Ordinal)];
            _sounds[playing] = sound;
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { Fail(error.Message); }
    }

    /// <summary>Stops one playing sound by name (without its pan): a bank sound by its program, a resident one by its id.</summary>
    private void StopSound(string name)
    {
        if (!_sounds.Remove(name, out var key)) return;
        var (bankName, bank, id, _, _) = _content.Sound(key);
        _driver.StopSound(bankName, bank, id);
    }

    /// <summary>Starts an installed piece at a volume (0 to 1).</summary>
    private void PlayMusic(string asset, float level)
    {
        try
        {
            var (sequence, bank) = _content.Music(asset);
            _musicLevel = level;
            _driver.PlayMusic(sequence, bank, (int)Math.Round(level * 127));
            _musicAsset = asset;
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { Fail(error.Message); }
    }

    private void MusicLevel(float level)
    {
        _musicLevel = level;
        _driver.Volume((int)Math.Round(level * 127));
    }

    /// <summary>The ambience a hold stopped, to start again when it is let go.</summary>
    private readonly List<string> _heldAmbience = new();

    private IEnumerable<EventActor> Targets(GDictionary args)
    {
        if (args["actor"].AsString() == "all") return Actors.Values;
        return Actor(args) is { } actor ? new[] { actor } : Array.Empty<EventActor>();
    }

    /// <summary>Retail 20: creates an actor from a character's rig. It stays hidden until a flags command shows it.</summary>
    private Error Spawn(GDictionary args)
    {
        string reference = args["actor"].AsString();
        int character = args["character"].AsInt32();
        string name = "character_" + character.ToString("X2");
        if (!_content.Has(name)) { MissingCharacters.Add(character); return Error.Ok; }
        Node3D rig;
        try
        {
            rig = _content.Rig(name);
        }
        catch (System.IO.InvalidDataException error)
        {
            Fail(error.Message);
            return Error.FileCorrupt;
        }
        // Creating a slot again replaces the actor that held it.
        if (Field.Actors.ContainsKey(reference)) Field.RemoveActor(reference);
        var node = new Node3D { Name = "Actor_" + reference.Replace(':', '_') };
        Field.Geometry.AddChild(node);
        Field.Actors[reference] = node;
        var result = Field.PrepareRigBank(reference, "base", rig);
        if (result != Error.Ok) return result;
        result = Field.ActivateRig(Field.RigBanks[(reference, "base")]);
        if (result != Error.Ok) return result;
        var actor = new EventActor(reference, character, node) { LoadSet = AddAnimationSet };
        // Drawn as the Settings choose: the disc's textures as they are, or enhanced.
        CharacterQuality.Apply(node, _content.Character(character).GetValueOrDefault("head_bone", -1).AsInt32());
        actor.ShadowRadius = _content.Character(character).GetValueOrDefault("shadow", 0).AsInt32();
        if (actor.ShadowRadius > 0)
        {
            actor.Shadow = new MeshInstance3D { Name = "Shadow", Mesh = ShadowDisc, MaterialOverride = ShadowShade, TopLevel = true, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            node.AddChild(actor.Shadow);
        }
        // Its head can turn: a modifier on its rig after the animation.
        int head = _content.Character(character).GetValueOrDefault("head_bone", -1).AsInt32();
        if (actor.SkeletonBone(head) is >= 0 and var headIndex) { actor.Head = new HeadTurn { Bone = headIndex }; actor.Skeleton!.AddChild(actor.Head); }
        var tile = args["tile"].AsGodotArray();
        actor.Position = new Vector3(tile[0].AsInt32() * 128 + 64, 0, tile[1].AsInt32() * 128 + 64);
        actor.Position = actor.Position with { Y = FloorHeight(actor.Position) };
        Tint(actor, Vector3.Zero);
        Actors[reference] = actor;
        // A new actor stands in its base idle (animation 1) until the script animates it.
        // A prop, with no animations of its own, holds the pose its model was converted with.
        if (!actor.Animate("base_" + FieldPlayer.Idle, 0)) actor.Animate("rest", 0);
        actor.Apply();
        return Error.Ok;
    }

    /// <summary>
    /// Adds an animation set's clips (decoded for the actor's character) to its rig, as the game loads the set. The
    /// imported rig names its skeleton as it pleases, so the clips' tracks are pointed at the rig's own skeleton.
    /// </summary>
    private void AddAnimationSet(EventActor actor, string set)
    {
        if (actor.Player == null || actor.Skeleton == null) return;
        // The set's clip scripts join the character's.
        _clipEvents.Remove(actor.Character);
        var library = actor.Player.GetAnimationLibrary("");
        string skeleton = actor.Player.GetNode(actor.Player.RootNode).GetPathTo(actor.Skeleton).ToString();
        try
        {
            foreach (var (clip, animation) in _content.Field.AnimationSet(actor.Character, set))
            {
                if (library.HasAnimation(clip)) continue;
                var own = (Animation)animation.Duplicate();
                for (int track = 0; track < own.GetTrackCount(); track++)
                {
                    string path = own.TrackGetPath(track).ToString();
                    int bone = path.IndexOf(':');
                    if (bone > 0) own.TrackSetPath(track, skeleton + path[bone..]);
                }
                library.AddAnimation(clip, own);
            }
        }
        catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException) { Fail(error.Message); }
    }

    /// <summary>A remembered room character: its hit and magic points, limbs' hit points, place and facing, and whether it fell.</summary>
    private sealed record EnemyState(int Hp, int Mp, int[] Limbs, Vector3 Position, float Facing, bool Dead);

    /// <summary>
    /// The characters of the last four rooms Ashley was in (BATTLE.PRG's zone enemy cache, four rooms of eight
    /// characters, oldest first out): a room she comes back to has its fallen enemies gone and the rest as she left
    /// them; event opcode 53 forgets a room.
    /// </summary>
    private readonly List<(int Zone, int Map, Dictionary<int, EnemyState> States)> _enemyMemory = new();
    /// <summary>Each room character's index in the room's records, by actor reference.</summary>
    private readonly Dictionary<string, int> _roomActorIndex = new();
    public IReadOnlyDictionary<string, int> RoomActorIndices => _roomActorIndex;

    private void RememberEnemies()
    {
        int at = _enemyMemory.FindIndex(room => room.Zone == Zone && room.Map == Map);
        if (at < 0 || Battle == null) return;
        var states = _enemyMemory[at].States;
        foreach (var (reference, index) in _roomActorIndex)
            if (Battle.Combatants.TryGetValue(reference, out var c) && c.Hostile && (states.Count < 8 || states.ContainsKey(index)))
            {
                var limbs = new int[c.Limbs.Count];
                for (int i = 0; i < limbs.Length; i++) limbs[i] = c.Limbs[i].Hp;
                states[index] = new EnemyState(c.Hp, c.Mp, limbs, c.Actor.Position, c.Actor.Facing, c.Dead);
            }
    }

    /// <summary>Opcode 53: a room's characters are forgotten, so they come back whole.</summary>
    private void ForgetEnemies(int zone, int map) => _enemyMemory.RemoveAll(room => room.Zone == zone && room.Map == map);

    /// <summary>
    /// Places the characters the room itself holds, as it loads and before its event runs. A character stays out
    /// while its record hides it or a test fails: story-stage flags 1 and 2, flag 0x82, or a flag against a value.
    /// Tests on the player's hit points pass until the game keeps them.
    /// </summary>
    private Error SpawnRoomActors()
    {
        int Flag(int index) => Flags[index];
        _roomActorIndex.Clear();
        int memory = _enemyMemory.FindIndex(room => room.Zone == Zone && room.Map == Map);
        var remembered = memory >= 0 ? _enemyMemory[memory].States : new Dictionary<int, EnemyState>();
        if (memory >= 0) _enemyMemory.RemoveAt(memory);
        _enemyMemory.Add((Zone, Map, remembered));
        if (_enemyMemory.Count > 4) _enemyMemory.RemoveAt(0);
        var records = Document.GetValueOrDefault("room_actors", new GArray()).AsGodotArray();
        for (int index = 0; index < records.Count; index++)
        {
            var entry = records[index].AsGodotDictionary();
            if (entry["hidden"].AsBool()) continue;
            remembered.TryGetValue(index, out var state);
            if (state?.Dead == true) continue;
            int scene = entry["scene_test"].AsInt32(), flagTest = entry["flag_test"].AsInt32(), flagValue = entry["flag_value"].AsInt32();
            int statTest = entry["stat_test"].AsInt32(), stat = entry["stat_value"].AsInt32();
            bool absent = scene switch { 1 => Flag(1) != 0, 2 => Flag(1) != 1, 3 => Flag(2) != 1, _ => false }
                || flagTest switch { 1 => Flag(0x82) != flagValue, 2 => Flag(0x82) > flagValue, 3 => Flag(0x82) < flagValue, _ => false }
                || statTest switch { 3 => Flag(stat & 0x3FF) != stat >> 10, 4 => Flag(stat & 0x3FF) > stat >> 10, 5 => Flag(stat & 0x3FF) < stat >> 10, _ => false };
            if (absent) continue;
            // Slot 0 asks for the first free one from 2 to 9.
            int slot = entry["slot"].AsInt32();
            if (slot == 0) for (slot = 2; slot < 10 && Actors.ContainsKey("slot:" + slot); slot++) { }
            if (slot >= 10) continue;
            string reference = "slot:" + slot;
            var result = Spawn(new GDictionary { ["actor"] = reference, ["character"] = entry["character"], ["tile"] = entry["tile"] });
            if (result != Error.Ok) return result;
            if (!Actors.TryGetValue(reference, out var actor)) continue;
            actor.Facing = entry["facing"].AsSingle();
            actor.Visible = true;
            // Stance 2 is a hostile character with its weapon drawn; the record's marked byte flags it for the room's
            // conditions.
            bool hostile = entry["stance"].AsInt32() == 2;
            var combatant = Battle!.Enlist(actor, entry["stats"].AsGodotDictionary(), hostile, entry.GetValueOrDefault("marked", false).AsBool(), hostile, LimbBones(actor.Character), LimbParts(actor.Character));
            combatant.Drops = entry["stats"].AsGodotDictionary().GetValueOrDefault("drops", new GDictionary()).AsGodotDictionary();
            combatant.DropItems = entry.GetValueOrDefault("items", new GArray()).AsGodotArray();
            _roomActorIndex[reference] = index;
            if (state != null)
            {
                combatant.Hp = Math.Min(state.Hp, combatant.MaxHp);
                combatant.Mp = Math.Min(state.Mp, combatant.MaxMp);
                for (int i = 0; i < state.Limbs.Length && i < combatant.Limbs.Count; i++) combatant.Limbs[i].Hp = state.Limbs[i];
                actor.Position = state.Position;
                actor.Facing = state.Facing;
            }
            combatant.OwnWeapon = entry["stats"].AsGodotDictionary()["weapon"].AsGodotDictionary().GetValueOrDefault("asset", "").AsString();
            Arm(combatant, combatant.OwnWeapon);
            Arm(combatant, entry["stats"].AsGodotDictionary().GetValueOrDefault("shield", new GDictionary()).AsGodotDictionary().GetValueOrDefault("asset", "").AsString(), shield: true);
            actor.Animate($"{combatant.Set}_{FieldPlayer.Idle}", 0);
            actor.Apply();
        }
        return Error.Ok;
    }

    /// <summary>The rig bones a character's limbs are aimed at through.</summary>
    /// <summary>The model part each of a character's limbs is, which names it.</summary>
    private GArray LimbParts(int character) =>
        _content.Character(character).GetValueOrDefault("limb_parts", new GArray()).AsGodotArray();

    private GArray LimbBones(int character) =>
        _content.Character(character).GetValueOrDefault("limb_bones", new GArray()).AsGodotArray();

    /// <summary>
    /// Puts a fighter's weapon model in its hand: on the bone its rig marks as the weapon mount, in the rig's retail
    /// space (the weapon's own conversion space is undone there). It shows only while drawn.
    /// </summary>
    private void Arm(FieldBattle.Combatant combatant, string asset, bool shield = false)
    {
        var held = shield ? combatant.ShieldModel : combatant.Weapon;
        held?.GetParent()?.QueueFree();
        if (shield) combatant.ShieldModel = null; else combatant.Weapon = null;
        if (asset.Length == 0 || combatant.Actor.Skeleton is not { } skeleton) return;
        var definition = _content.Character(combatant.Actor.Character);
        int bone = definition.GetValueOrDefault(shield ? "shield_bone" : "weapon_bone", -1).AsInt32();
        int index = combatant.Actor.SkeletonBone(bone);
        if (index < 0) return;
        Node3D model;
        try
        {
            model = _content.Rig(asset);
        }
        catch (System.IO.InvalidDataException error)
        {
            Fail(error.Message);
            return;
        }
        var mount = new BoneAttachment3D { Name = shield ? "Shield" : "Weapon", BoneIdx = index };
        skeleton.AddChild(mount);
        model.Transform = new Transform3D(new Basis(Vector3.Right, Mathf.Pi).Scaled(Vector3.One / Installation.MapConversion.UnitsPerMetre), Vector3.Zero).AffineInverse();
        mount.AddChild(model);
        model.Visible = combatant.Drawn;
        if (shield) combatant.ShieldModel = model; else combatant.Weapon = model;
    }

    /// <summary>What felled enemies dropped, to be offered in the gadget bag once Ashley is free to act.</summary>
    private readonly List<GDictionary> _dropped = new();

    /// <summary>How many times the gadget bag has been opened on dropped loot, for tests.</summary>
    public int DropsOffered { get; private set; }

    /// <summary>
    /// Rolls what a felled enemy drops (BATTLE.PRG's loot roll): its weapon, shield, accessory and each piece of armour,
    /// each when a roll of 0 to 254 falls under its rate; its room record's first two items always, the third at its
    /// rate. What drops waits as chest loot for the gadget bag (menu 12), which opens when the attack has played out.
    /// </summary>
    private void DropLoot(FieldBattle.Combatant felled)
    {
        if (felled.Drops == null) return;
        bool Rolls(GDictionary entry) => Random.Shared.Next(255) < entry.GetValueOrDefault("rate", 0).AsInt32();
        var loot = new GDictionary();
        var armour = new GArray();
        if (felled.Drops.GetValueOrDefault("weapon", default).AsGodotDictionary() is { Count: > 0 } weapon && Rolls(weapon))
            loot["weapon"] = new GDictionary { ["blade"] = weapon["blade"].AsGodotDictionary().Duplicate(true), ["grip"] = weapon["grip"].AsGodotDictionary().Duplicate(true), ["name"] = weapon["name"] };
        if (felled.Drops.GetValueOrDefault("shield", default).AsGodotDictionary() is { Count: > 0 } shield && Rolls(shield)) loot["shield"] = shield["record"].AsGodotDictionary().Duplicate(true);
        if (felled.Drops.GetValueOrDefault("accessory", default).AsGodotDictionary() is { Count: > 0 } accessory && Rolls(accessory)) armour.Add(accessory["record"].AsGodotDictionary().Duplicate(true));
        foreach (var value in felled.Drops.GetValueOrDefault("armour", new GArray()).AsGodotArray())
            if (value.AsGodotDictionary() is var piece && Rolls(piece) && armour.Count < 2) armour.Add(piece["record"].AsGodotDictionary().Duplicate(true));
        var items = new GArray();
        foreach (var value in felled.DropItems ?? new GArray())
        {
            var item = value.AsGodotDictionary();
            if (item["id"].AsInt32() != 0 && item["count"].AsInt32() > 0 && (item["rate"].AsInt32() > 255 || Rolls(item))) items.Add(new GDictionary { ["id"] = item["id"], ["count"] = item["count"] });
        }
        loot["armour"] = armour;
        loot["items"] = items;
        if (loot.ContainsKey("weapon") || loot.ContainsKey("shield") || armour.Count > 0 || items.Count > 0) _dropped.Add(loot);
    }

    /// <summary>Offers what the felled dropped, all together, in the gadget bag.</summary>
    private void OfferDrops()
    {
        if (_dropped.Count == 0 || _menu.Open || Battle == null || Battle.Busy) return;
        var all = new GDictionary { ["armour"] = new GArray(), ["items"] = new GArray() };
        var leftover = new GDictionary();
        foreach (var loot in _dropped)
            foreach (var (key, value) in loot)
            {
                if (key.AsString() == "items")
                    foreach (var entry in value.AsGodotArray())
                    {
                        // The same item from two enemies is one stack.
                        var item = entry.AsGodotDictionary();
                        GDictionary? held = null;
                        foreach (var kept in all["items"].AsGodotArray()) if (kept.AsGodotDictionary()["id"].AsInt32() == item["id"].AsInt32()) held = kept.AsGodotDictionary();
                        if (held != null) held["count"] = held["count"].AsInt32() + item["count"].AsInt32();
                        else all["items"].AsGodotArray().Add(item.Duplicate());
                    }
                else if (value.VariantType == Variant.Type.Array) foreach (var entry in value.AsGodotArray()) all[key].AsGodotArray().Add(entry);
                else if (!all.ContainsKey(key.AsString())) all[key] = value;
                // A second weapon or shield waits for the next offer, as the bag shows one of each.
                else leftover[key] = value;
            }
        _dropped.Clear();
        if (leftover.Count > 0) { leftover["armour"] = new GArray(); leftover["items"] = new GArray(); _dropped.Add(leftover); }
        DropsOffered++;
        // Not a room chest: no flag records it.
        _menu.OpenChest(new GDictionary { ["loot"] = all, ["index"] = -1 });
    }

    /// <summary>Frames each dead enemy has spent fading, by actor.</summary>
    private readonly Dictionary<EventActor, (int Frames, Vector3 Light)> _fading = new();

    /// <summary>
    /// A felled enemy fades once its death animation has played to its end (its script's operation 58, BATTLE.PRG
    /// 0x800AB9A4 states 2 and 3): its palette's red and green drain by 2 of 31 a frame (0x800AC168 mode 0), leaving it
    /// blue, then all three by 4 a frame (mode 1) to black, which it is drawn in semi-transparently and so not seen; then
    /// it is gone. One whose room record marks it (byte 0x24) keeps its body, as the opening's soldiers do.
    /// </summary>
    private void FadeTheDead()
    {
        if (Battle == null) return;
        foreach (var fighter in Battle.Combatants.Values)
        {
            var actor = fighter.Actor;
            if (!fighter.Dead || fighter.Marked || fighter == Battle.Ashley || !actor.Visible) continue;
            if (!_fading.TryGetValue(actor, out var fade))
            {
                if (actor.Player is { } player && player.IsPlaying()) continue;
                fade = (0, actor.Tinted ? actor.Light : FloorLight(actor.Position));
            }
            int frames = fade.Frames + 1;
            _fading[actor] = (frames, fade.Light);
            var light = fade.Light;
            float warm = Math.Max(0, 1 - frames * 2 / 31f), cool = frames <= 16 ? 1 : Math.Max(0, 1 - (frames - 16) * 4 / 31f);
            actor.Tinted = true;
            Light(actor, new Vector3(light.X * warm * cool, light.Y * warm * cool, light.Z * cool));
            if (frames >= 16 + 8)
            {
                actor.Visible = false;
                actor.Flags &= ~1;
                actor.Apply();
            }
        }
    }

    /// <summary>
    /// Retail 31: an actor's light colour, with 128 as neutral. Zero returns the actor to the room's light, which
    /// follows it from tile to tile.
    /// </summary>
    private static void Tint(EventActor actor, Vector3 color)
    {
        actor.Tinted = color != Vector3.Zero;
        if (actor.Tinted) Light(actor, color);
    }

    /// <summary>Ashley's glow at full strength as the attack sphere opens, as channel scales: cyan (from retail footage).</summary>
    public static readonly Vector3 AshleyGlow = new(0.3f, 1.3f, 2.0f);

    /// <summary>A glow over a character as the attack sphere opens, <paramref name="amount"/> from 0 (none) to 1, toward <paramref name="tint"/>.</summary>
    public void Glow(EventActor actor, float amount, Vector3 tint)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (actor.Glow == amount && actor.GlowTint == tint) return;
        actor.Glow = amount;
        actor.GlowTint = tint;
        var light = actor.Light;
        actor.Light = -Vector3.One;
        Light(actor, light.X < 0 ? new Vector3(128, 128, 128) : light);
    }

    private static void Light(EventActor actor, Vector3 color)
    {
        if (actor.Light == color) return;
        actor.Light = color;
        // A glow scales the light's channels toward its tint (the characters are drawn unshaded).
        var tint = Vector3.One.Lerp(actor.GlowTint, actor.Glow);
        var albedo = new Color(color.X / 128f * tint.X, color.Y / 128f * tint.Y, color.Z / 128f * tint.Z);
        foreach (var node in actor.Node.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (MeshInstance3D)node;
            for (int surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                if (mesh.Mesh.SurfaceGetMaterial(surface) is StandardMaterial3D material)
                    material.AlbedoColor = material.VertexColorUseAsAlbedo ? new Color(albedo.R * 2, albedo.G * 2, albedo.B * 2) : albedo;
        }
    }

    /// <summary>
    /// The disc of a character's shadow (BATTLE.PRG 0x800B4B14's tables at 0x800E9A68 and 0x800E9AD4): its centre, a
    /// ring at 50 and one at 100 in eight directions, in 24 triangles; the centre at full strength, the inner ring at
    /// half and the outer at none (in the vertex colour), so it fades out to its edge.
    /// </summary>
    private static readonly ArrayMesh ShadowDisc = BuildShadowDisc();

    private static ArrayMesh BuildShadowDisc()
    {
        var points = new List<Vector3> { Vector3.Zero };
        var colours = new List<Color> { Colors.White };
        for (int i = 0; i < 8; i++)
        {
            float angle = i * Mathf.Tau / 8, x = Mathf.Cos(angle), z = Mathf.Sin(angle);
            points.Add(new Vector3(x * 50, 0, z * 50)); colours.Add(new Color(0.5f, 0.5f, 0.5f));
            points.Add(new Vector3(x * 100, 0, z * 100)); colours.Add(Colors.Black);
        }
        var indices = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            int inner = 1 + i * 2, outer = inner + 1, nextInner = 1 + (i + 1) % 8 * 2, nextOuter = nextInner + 1;
            indices.AddRange(new[] { 0, inner, nextInner, inner, outer, nextOuter, nextOuter, nextInner, inner });
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colours.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = indices.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>
    /// Retail draws the shadow subtracting its colour from the screen (semi-transparency mode 2); the screen is read
    /// back and the subtraction made on its display values, as the console's is.
    /// </summary>
    private static readonly ShaderMaterial ShadowShade = new()
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
                uniform sampler2D screen : hint_screen_texture, filter_nearest;
                instance uniform float strength = 0.75;
                vec3 to_srgb(vec3 c) { return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c)); }
                vec3 to_linear(vec3 c) { return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c)); }
                void fragment() {
                    vec3 shown = to_srgb(texture(screen, SCREEN_UV).rgb);
                    ALBEDO = to_linear(max(shown - vec3(COLOR.r * strength), vec3(0.0)));
                }
                """,
        },
    };

    /// <summary>
    /// Puts an actor's shadow on the floor under it, as 0x800B4B14 does each frame: its radius the model's plus an
    /// eighth of its height above the floor (at least 20), the disc 100 units out scaled by that over 64; tipped to
    /// the floor's slope a radius either way in x and z (the gentler side, none past 0x280 of a turn); its strength
    /// the grey of the actor's light (r + 2g + b over 4, 128 neutral) plus 0x40, over 255.
    /// </summary>
    private void PlaceShadow(EventActor actor)
    {
        if (actor.Shadow is not { } shadow) return;
        shadow.Visible = actor.Visible && actor.ShadowShown && actor.Parent == null;
        if (!shadow.Visible) return;
        var at = actor.Position;
        float floor = Math.Max(FloorHeight(at), at.Y);
        float radius = Math.Max(20, actor.ShadowRadius + (at.Y - floor) / 8);
        float Slope(Vector3 step)
        {
            float before = floor - FloorHeight(at - step), after = FloorHeight(at + step) - floor;
            float rise = Math.Abs(before) < Math.Abs(after) ? before : after;
            return Math.Abs(Mathf.Atan2(rise, radius)) < 0x280 * Mathf.Tau / 4096 ? rise / radius : 0;
        }
        float slopeX = Slope(new Vector3(radius, 0, 0)), slopeZ = Slope(new Vector3(0, 0, radius));
        float scale = radius / 64;
        // A point (x, 0, z) of the disc stands on the floor's plane, y rising with x and z by the slopes; a unit
        // above the floor (y is down), so it is not lost in it.
        var basis = new Basis(new Vector3(scale, slopeX * scale, 0), new Vector3(0, scale, 0), new Vector3(0, slopeZ * scale, scale));
        shadow.GlobalTransform = EventActor.RetailSpace * new Transform3D(basis, new Vector3(at.X, floor - 1, at.Z));
        var light = actor.Light.X < 0 ? new Vector3(128, 128, 128) : actor.Light;
        shadow.SetInstanceShaderParameter("strength", Math.Clamp((light.X + 2 * light.Y + light.Z) / 4 + 0x40, 0, 255) / 255f);
    }

    /// <summary>The room's floor height (retail y) under a map position, from the collision map's 32-unit sub-cells.</summary>
    public float FloorHeight(Vector3 position)
    {
        int x = (int)Mathf.Floor(position.X / 32), z = (int)Mathf.Floor(position.Z / 32);
        if (x < 0 || z < 0 || x >= _columns || z >= _rows) return position.Y;
        return PlatformTop(position) ?? CrateTop(position) ?? _heights[z * _columns + x];
    }

    /// <summary>
    /// A scripted group animation (see MapConversion.GroupScripts): its commands, where it is in them, and the moving
    /// group's position (16ths of a map unit), rotation (4096 a turn), velocity and turning speed.
    /// </summary>
    private sealed class GroupScript
    {
        public int Index, Group = -1, Pc, Counter, Bounces;
        public bool Running, Paused;
        public readonly List<(int Op, int[] Args)> Commands = new();
        /// <summary>Position in 16ths of a map unit; velocity in 16ths a frame; angles 4096 a turn.</summary>
        public Vector3I Position, Velocity, Rotation, Turning;
        /// <summary>Damping of the velocity and of the turning, in 16ths a frame (state +0x2a and +0x32).</summary>
        public int Damping, TurnDamping;

        private static int Damp(int value, int damping) => value - value * damping / 16;

        private void Turn() => Rotation = new Vector3I((Rotation.X + Turning.X) & 0xFFF, (Rotation.Y + Turning.Y) & 0xFFF, (Rotation.Z + Turning.Z) & 0xFFF);

        /// <summary>
        /// Operation 0 (BATTLE.PRG 0x80092c68): a blast from a point (arguments 0 to 2, map units) of a reach
        /// (argument 3). On each axis the group is pushed away from the point at a quarter of the reach less its
        /// distance on that axis, none beyond it; it tumbles by the low bits of its new velocity, and its velocity
        /// damps by argument 4 plus one.
        /// </summary>
        public void Blast(int x, int y, int z, int reach, int damping)
        {
            int Axis(int position, int target)
            {
                int v = (short)(reach - Math.Abs(position / 16 - target));
                if (v <= 0) return 0;
                return (position - target * 16 >= 0 ? v : -v) / 4;
            }
            Velocity = new Vector3I(Axis(Position.X, x), Axis(Position.Y, y), Axis(Position.Z, z));
            Damping = damping + 1;
            Turning = new Vector3I((Velocity.X & 0x1F) - 16, (Velocity.Y & 0x3F) - 32, 0);
            TurnDamping = 0;
        }

        /// <summary>
        /// Operation 5's mode 1 (0x8009306c): it moves and turns, falling 16 a frame faster. Below the floor it
        /// bounces, at most twice while it damps: back on the floor, its fall turned back and divided by its damping
        /// plus two, its other speeds and turning halved; then it rests. Returns whether it still moves.
        /// </summary>
        public bool Fall(Func<int, int, int, int> floor)
        {
            Position += Velocity;
            Turn();
            int ground = floor(Position.X / 16, Position.Y / 16, Position.Z / 16);
            if (ground < Position.Y / 16)
            {
                if (++Bounces >= 3 || Damping <= 0) { Bounces = 0; return false; }
                Position = Position with { Y = ground * 16 };
                Velocity = new Vector3I(Velocity.X / 2, Velocity.Y / -(Math.Abs(Damping) + 2), Velocity.Z / 2);
                Turning = new Vector3I(Turning.X / 2, Turning.Y / 2, Turning.Z / 2);
            }
            if (Damping != 0) Velocity = new Vector3I(Damp(Velocity.X, Damping), Velocity.Y, Damp(Velocity.Z, Damping));
            Velocity = Velocity with { Y = Velocity.Y + 16 };
            if (TurnDamping != 0) Turning = new Vector3I(Damp(Turning.X, TurnDamping), Damp(Turning.Y, TurnDamping), Damp(Turning.Z, TurnDamping));
            return true;
        }

        /// <summary>Operation 5's mode 2 (0x80093364): it moves and turns, both damping.</summary>
        public void Drift()
        {
            Position += Velocity;
            Turn();
            if (Damping != 0) Velocity = new Vector3I(Damp(Velocity.X, Damping), Damp(Velocity.Y, Damping), Damp(Velocity.Z, Damping));
            if (TurnDamping != 0) Turning = new Vector3I(Damp(Turning.X, TurnDamping), Damp(Turning.Y, TurnDamping), Damp(Turning.Z, TurnDamping));
        }
    }

    private readonly List<GroupScript> _groupScripts = new();

    private void ReadGroupScripts()
    {
        _groupScripts.Clear();
        foreach (var value in Document.GetValueOrDefault("group_scripts", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var groups = record["groups"].AsGodotArray();
            // Each of the script's groups runs the commands with its own state (BATTLE.PRG 0x80092B04 steps them in
            // turn): MAP004's script 0x40 stands both leaves of its door ajar.
            for (int g = 0; g < Math.Max(1, groups.Count); g++)
            {
                var script = new GroupScript { Index = record["index"].AsInt32(), Running = record["running"].AsBool(), Group = g < groups.Count ? groups[g].AsInt32() : -1 };
                foreach (var command in record["commands"].AsGodotArray())
                {
                    var entry = command.AsGodotDictionary();
                    var list = entry["args"].AsGodotArray();
                    var numbers = new int[list.Count];
                    for (int i = 0; i < list.Count; i++) numbers[i] = list[i].AsInt32();
                    script.Commands.Add((entry["op"].AsInt32(), numbers));
                }
                // It starts where its group stands.
                if (Field != null && Field.Actors.TryGetValue(script.Group.ToString(), out var node))
                    script.Position = (Vector3I)(EventActor.RetailSpace.AffineInverse() * node.Position * 16).Round();
                _groupScripts.Add(script);
            }
        }
    }

    /// <summary>
    /// Runs each scripted animation one event frame (BATTLE.PRG's interpreter at 0x80092b04): commands run in turn
    /// until one waits. 0 blasts the group away from a point; 1 sets the velocity (arguments 0 to 2), its damping (3),
    /// the turning speed (4 to 6) and its damping (7); 2 does nothing; 3 hides the script's own group, 4 shows group n
    /// and 10 hides it; 5 moves (mode 1 falling, 2 drifting) for n frames (0x8000 while it falls); 8 places the group
    /// (words of 16ths at arguments 0, 2 and 4; angles at 6, 8 and 10); 9 starts room animation n; 11 jumps back; 12
    /// restarts script 0x40 + n. 6 and 7 play and stop a sound from the room's own bank. The end stops it.
    /// </summary>
    private void StepGroupScripts()
    {
        foreach (var script in _groupScripts)
        {
            if (!script.Running || script.Paused) continue;
            for (int steps = 0; steps < 64 && script.Running; steps++)
            {
                if (script.Pc >= script.Commands.Count) { script.Running = false; break; }
                var (op, a) = script.Commands[script.Pc];
                int Arg(int i) => i < a.Length ? a[i] : 0;
                bool wait = false;
                switch (op)
                {
                    case 0:
                        script.Blast(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4));
                        break;
                    case 1:
                        // 0x80092e14: velocity, its damping, turning and the turning's damping.
                        script.Velocity = new Vector3I(Arg(0), Arg(1), Arg(2));
                        script.Damping = Arg(3);
                        script.Turning = new Vector3I(Arg(4), Arg(5), Arg(6));
                        script.TurnDamping = Arg(7);
                        break;
                    case 3: _hiddenGroups.Add(script.Group); break;
                    case 4: _hiddenGroups.Remove(Arg(0)); break;
                    case 10: _hiddenGroups.Add(Arg(0)); break;
                    case 5:
                    {
                        // 0x80092fbc: mode 1 falls and bounces, 2 drifts, others only wait. For n frames, or with
                        // 0x8000 for as long as it falls.
                        bool moving = false;
                        if (Arg(1) == 1) moving = script.Fall((x, y, z) => (int)FloorHeight(new Vector3(x, y, z)));
                        else if (Arg(1) == 2) script.Drift();
                        if (Arg(0) == -32768) wait = moving;
                        else if (++script.Counter < Arg(0)) wait = true;
                        else script.Counter = 0;
                        break;
                    }
                    case 8:
                        script.Position = new Vector3I((Arg(0) & 0xFFFF) | Arg(1) << 16, (Arg(2) & 0xFFFF) | Arg(3) << 16, (Arg(4) & 0xFFFF) | Arg(5) << 16);
                        script.Rotation = new Vector3I(Arg(6) & 0xFFF, Arg(8) & 0xFFF, Arg(10) & 0xFFF);
                        break;
                    case 9:
                        Present(new PresentationReceipt(0, 0, 0, -1), "room.animation", new GDictionary { ["index"] = Arg(0), ["start"] = true }, false);
                        break;
                    case 6:
                        // A sound from the room's own bank (BATTLE.PRG 0x8009352c, on the room's voice 0x200).
                        PlaySound($"room{Zone:d3}{Map:d2}_{Arg(1)}_p128", 127);
                        break;
                    case 7:
                        StopSound($"room{Zone:d3}{Map:d2}_{Arg(1)}");
                        break;
                    case 11: script.Pc = Arg(0); continue;
                    case 12:
                        foreach (var other in _groupScripts)
                            if (other.Index == 0x40 + Arg(0)) { other.Pc = 0; other.Counter = 0; other.Running = true; }
                        break;
                }
                if (wait) break;
                script.Pc++;
            }
            if (Field == null || !Field.Actors.TryGetValue(script.Group.ToString(), out var node)) continue;
            node.Position = EventActor.RetailSpace * ((Vector3)script.Position / 16);
            float unit = Mathf.Tau / 4096f;
            var retail = new Basis(Vector3.Up, script.Rotation.Y * unit) * new Basis(Vector3.Right, script.Rotation.X * unit) * new Basis(Vector3.Back, script.Rotation.Z * unit);
            var flip = new Basis(Vector3.Right, new Vector3(0, -1, 0), new Vector3(0, 0, -1));
            node.Basis = flip * retail * flip;
        }
    }

    /// <summary>A swinging geometry group (see MapConversion.Swings): its spring towards a target angle.</summary>
    private sealed class Swing
    {
        public int Index, Group, Accel, Damping, Target, Start, Velocity, Angle, Speed;
        public string Axis = "y";
        public bool Running, Paused, Fresh, Pending;
    }

    private readonly List<Swing> _swings = new();

    private void ReadSwings()
    {
        _swings.Clear();
        foreach (var value in Document.GetValueOrDefault("swings", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            _swings.Add(new Swing
            {
                Index = record["index"].AsInt32(), Group = record["group"].AsInt32(), Axis = record["axis"].AsString(), Running = record["running"].AsBool(), Fresh = true,
                Accel = record["accel"].AsInt32(), Damping = record["damping"].AsInt32(), Target = record["target"].AsInt32(), Start = record["start"].AsInt32(), Velocity = record["velocity"].AsInt32(),
            });
        }
        // Chests opened before stand open.
        foreach (var value in Document.GetValueOrDefault("chests", new GArray()).AsGodotArray())
            if (value.AsGodotDictionary() is var chest && chest["index"].AsInt32() is int index && index < 0x40 && FlagValue(0x3C0 + index) != 0) OpenLid(chest, true);
    }

    private void StepSwings()
    {
        foreach (var swing in _swings)
        {
            // A lid set open as the room loaded is posed once its group is built.
            if (swing.Pending && Field != null && Field.Actors.ContainsKey(swing.Group.ToString())) { swing.Pending = false; Pose(swing); }
            if (!swing.Running || swing.Paused) continue;
            if (swing.Fresh) { swing.Fresh = false; swing.Angle = swing.Start; swing.Speed = swing.Velocity; }
            swing.Angle += swing.Speed;
            swing.Speed += swing.Accel * 8;
            int apart = (swing.Angle - swing.Target) & 0xFFF;
            if (apart > 0x800) apart = 0x1000 - apart;
            bool settled = false;
            if (apart < Math.Abs(swing.Speed))
            {
                swing.Speed = -(swing.Speed >> Math.Clamp(swing.Damping, 0, 31));
                if (Math.Abs(swing.Speed) <= 32) { swing.Angle = swing.Target; settled = true; }
            }
            Pose(swing);
            if (!settled) continue;
            Settle(swing);
        }
    }

    private void Pose(Swing swing)
    {
        if (Field == null || !Field.Actors.TryGetValue(swing.Group.ToString(), out var node)) return;
        float angle = swing.Angle * Mathf.Tau / 4096f;
        var axis = swing.Axis == "y" ? Vector3.Up : swing.Axis == "x" ? Vector3.Right : Vector3.Back;
        var flip = new Basis(Vector3.Right, new Vector3(0, -1, 0), new Vector3(0, 0, -1));
        node.Basis = flip * new Basis(axis, angle) * flip;
    }

    // At rest, the next start swings it back.
    private static void Settle(Swing swing)
    {
        swing.Running = false;
        (swing.Start, swing.Target) = (swing.Target, swing.Start);
        swing.Velocity = -swing.Velocity;
        swing.Accel = -swing.Accel;
    }

    /// <summary>
    /// A chest's or container's lid: the room animation its record names (sub-section 11's swinging group) swings open
    /// as Ashley opens it, and stays open; with <paramref name="snap"/> it stands open at once, as BATTLE.PRG
    /// 0x8008D710 sets an opened chest's lid as the room loads (0x80093824).
    /// </summary>
    /// <summary>Swings the lid open, then (once it is still, as retail shows the bag or the container's question only then) does <paramref name="then"/>; Ashley stands meanwhile.</summary>
    private void AfterLid(GDictionary chest, Action then)
    {
        _control?.Halt();
        OpenLid(chest, false);
        _afterLid = (chest.GetValueOrDefault("object", -1).AsInt32(), then);
    }

    private (int Lid, Action Then)? _afterLid;

    /// <summary>Whether a lid is still swinging open before its chest's bag or container shows.</summary>
    public bool LidOpening => _afterLid != null;

    private void StepAfterLid()
    {
        if (_afterLid is not { } pending) return;
        foreach (var swing in _swings) if (swing.Index == pending.Lid && swing.Running) return;
        _afterLid = null;
        pending.Then();
    }

    private void OpenLid(GDictionary chest, bool snap)
    {
        int index = chest.GetValueOrDefault("object", -1).AsInt32();
        foreach (var swing in _swings)
        {
            if (swing.Index != index) continue;
            if (!snap) { swing.Running = true; swing.Fresh = true; continue; }
            swing.Angle = swing.Target;
            swing.Pending = true;
            Settle(swing);
        }
    }

    /// <summary>An animated texture image (see MapConversion.TextureFrames): its texture, the area it redraws and its frames.</summary>
    private sealed class TextureFrames
    {
        public ImageTexture Texture = null!;
        public Image Base = null!, Strip = null!;
        public Rect2I Area;
        public int Frames, Ticks, Shown = -1;
    }

    private readonly List<TextureFrames> _textureFrames = new();
    private int _textureTick;

    private void ReadTextureFrames(GDictionary textures)
    {
        _textureFrames.Clear();
        _textureTick = 0;
        // Animations sharing a texture redraw the same image.
        var bases = new Dictionary<string, Image>();
        foreach (var value in Document.GetValueOrDefault("texture_frames", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            string key = record["texture"].AsString();
            if (textures.GetValueOrDefault(key).AsGodotObject() is not ImageTexture texture) continue;
            if (!bases.TryGetValue(key, out var image))
            {
                image = texture.GetImage();
                image.ClearMipmaps();
                bases[key] = image;
            }
            var rect = record["rect"].AsGodotArray();
            Image strip;
            try { strip = _content.LoadImage(record["image"].AsString()); }
            catch (System.IO.InvalidDataException error) { Fail(error.Message); return; }
            _textureFrames.Add(new TextureFrames
            {
                Texture = texture, Base = image, Strip = strip, Area = new Rect2I(rect[0].AsInt32(), rect[1].AsInt32(), rect[2].AsInt32(), rect[3].AsInt32()),
                Frames = Math.Max(1, record["frames"].AsInt32()), Ticks = Math.Max(1, record["ticks_per_frame"].AsInt32()),
            });
        }
    }

    /// <summary>Shows each animated texture's frame for the 60 Hz tick, redrawing a texture only when a frame changes.</summary>
    private void StepTextureFrames()
    {
        _textureTick++;
        var changed = new HashSet<ImageTexture>();
        foreach (var frames in _textureFrames)
        {
            int frame = _textureTick / frames.Ticks % frames.Frames;
            if (frame == frames.Shown) continue;
            frames.Shown = frame;
            frames.Base.BlitRect(frames.Strip, new Rect2I(0, frames.Area.Size.Y * frame, frames.Area.Size.X, frames.Area.Size.Y), frames.Area.Position);
            changed.Add(frames.Texture);
        }
        foreach (var texture in changed)
            foreach (var frames in _textureFrames)
                if (frames.Texture == texture)
                {
                    var image = (Image)frames.Base.Duplicate();
                    image.GenerateMipmaps();
                    texture.Update(image);
                    break;
                }
    }

    /// <summary>
    /// A moving platform (see MapConversion.Platforms): its group, path and where it is now (map units, y down). A lift
    /// moves only while Ashley rides it, and dips as it sets off (BATTLE.PRG's offsets at 0x80068bec).
    /// </summary>
    private sealed class Platform
    {
        public int Group, Last, Target, Leg, Elapsed, Dip = 7;
        public bool Off, Lift, Forward, Dwelling, Ridden;
        public Vector3 Position;
        public readonly List<(Vector3 Point, int Wait)> Waypoints = new();
        public readonly List<(Vector3 Velocity, int Frames)> Legs = new();
        public int MinX, MaxX, Top, MinZ, MaxZ;
        public bool HasFootprint;
    }

    private static readonly int[] PlatformDip = { 8, 14, 16, 11, 7, 3, 1, 0 };
    private readonly List<Platform> _platforms = new();
    /// <summary>Set when the room's event keeps it off the map (retail b7 0x11).</summary>
    private bool _unmapped;
    private bool _platformsFrozen;
    private int _platformHold;

    private void ReadPlatforms()
    {
        _platforms.Clear();
        _platformsFrozen = false;
        _platformHold = 0;
        foreach (var value in Document.GetValueOrDefault("platforms", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var platform = new Platform
            {
                Group = record["group"].AsInt32(), Off = record["off"].AsBool(), Lift = record.GetValueOrDefault("lift", false).AsBool(),
                Target = record.GetValueOrDefault("target", 0).AsInt32(), Forward = record.GetValueOrDefault("forward", true).AsBool(),
                Position = record.ContainsKey("start") ? Retail(record["start"].AsGodotArray()) : Vector3.Zero,
            };
            foreach (var point in record.GetValueOrDefault("waypoints", new GArray()).AsGodotArray())
            {
                var p = point.AsGodotArray();
                platform.Waypoints.Add((Retail(p), p[3].AsInt32()));
            }
            foreach (var leg in record.GetValueOrDefault("legs", new GArray()).AsGodotArray())
            {
                var l = leg.AsGodotArray();
                platform.Legs.Add((Retail(l), l[3].AsInt32()));
            }
            platform.Last = platform.Waypoints.Count - 1;
            var footprint = record.GetValueOrDefault("footprint", new GArray()).AsGodotArray();
            if (footprint.Count == 6)
            {
                platform.HasFootprint = true;
                (platform.MinX, platform.MaxX, platform.Top, platform.MinZ, platform.MaxZ) = (footprint[0].AsInt32(), footprint[1].AsInt32(), footprint[2].AsInt32(), footprint[4].AsInt32(), footprint[5].AsInt32());
            }
            _platforms.Add(platform);
        }
    }

    /// <summary>The top of a platform under a position (y down), if one is there and no more than 160 above it.</summary>
    private float? PlatformTop(Vector3 position)
    {
        float? top = null;
        foreach (var platform in _platforms)
        {
            if (platform.Off || !platform.HasFootprint || platform.Waypoints.Count == 0) continue;
            float x = position.X - platform.Position.X, z = position.Z - platform.Position.Z;
            if (x < platform.MinX || x > platform.MaxX || z < platform.MinZ || z > platform.MaxZ) continue;
            float y = platform.Position.Y + platform.Top + PlatformDip[platform.Dip];
            if (y < position.Y - 160) continue;
            if (top == null || y < top) top = y;
        }
        return top;
    }

    /// <summary>
    /// Moves the platforms one event frame: along the leg in hand, then a wait at the waypoint reached, then on to the
    /// next, turning back at either end. Ashley, standing on one, goes with it.
    /// </summary>
    private void StepPlatforms()
    {
        if (_platformsFrozen) return;
        if (_platformHold > 0) { _platformHold--; return; }
        var ashley = Player;
        foreach (var platform in _platforms)
        {
            if (platform.Off || platform.Waypoints.Count < 2 || platform.Legs.Count == 0) continue;
            bool riding = false;
            if (ashley != null && platform.HasFootprint)
            {
                float x = ashley.Position.X - platform.Position.X, z = ashley.Position.Z - platform.Position.Z;
                float top = platform.Position.Y + platform.Top + PlatformDip[platform.Dip];
                riding = x >= platform.MinX && x <= platform.MaxX && z >= platform.MinZ && z <= platform.MaxZ && Math.Abs(ashley.Position.Y - top) <= 16;
            }
            if (riding && !platform.Ridden && platform.Lift) platform.Dip = 0;
            platform.Ridden = riding;
            if (platform.Lift && !riding) continue;
            var before = platform.Position;
            if (platform.Dip < 7) platform.Dip++;
            if (!platform.Dwelling)
            {
                var (velocity, frames) = platform.Legs[platform.Leg % platform.Legs.Count];
                platform.Position += velocity;
                if (++platform.Elapsed >= frames)
                {
                    platform.Position = platform.Waypoints[Math.Clamp(platform.Target, 0, platform.Last)].Point;
                    platform.Elapsed = 0;
                    platform.Dwelling = true;
                }
            }
            else if (++platform.Elapsed >= platform.Waypoints[Math.Clamp(platform.Target, 0, platform.Last)].Wait)
            {
                int reached = platform.Target;
                platform.Elapsed = 0;
                platform.Dwelling = false;
                if (reached <= 0) platform.Forward = true;
                else if (reached >= platform.Last) platform.Forward = false;
                platform.Target = reached + (platform.Forward ? 1 : -1);
                platform.Leg = (platform.Leg + 1) % platform.Legs.Count;
            }
            if (riding && ashley != null) ashley.Position += platform.Position - before;
            if (Field != null && Field.Actors.TryGetValue(platform.Group.ToString(), out var node))
                node.Position = EventActor.RetailSpace * (platform.Position + new Vector3(0, PlatformDip[platform.Dip], 0));
        }
    }

    /// <summary>A crate or block standing in the room: its kind, tile, the height of its base (y down), and its node.</summary>
    private sealed class Crate
    {
        public string Id = "";
        public int Kind, X, Z, Moves, Orientation = -1;
        public float Bottom;
        public Node3D Node = null!;
        public bool Carried;
    }

    private readonly List<Crate> _crates = new();

    /// <summary>The crates standing in the room: their id, kind, tile, base height (y down) and orientation (-1 none).</summary>
    public IEnumerable<(string Id, int Kind, int X, int Z, float Bottom, int Orientation)> Crates
    {
        get { foreach (var crate in _crates) if (!crate.Carried) yield return (crate.Id, crate.Kind, crate.X, crate.Z, crate.Bottom, crate.Orientation); }
    }
    private Crate? _carried;

    /// <summary>The top of the highest crate on a position's tile, which is floor there; null without one.</summary>
    private float? CrateTop(Vector3 position)
    {
        int x = (int)Mathf.Floor(position.X / 128), z = (int)Mathf.Floor(position.Z / 128);
        float? top = null;
        foreach (var crate in _crates)
            if (!crate.Carried && crate.X == x && crate.Z == z && (top == null || crate.Bottom - 128 < top)) top = crate.Bottom - 128;
        return top;
    }

    /// <summary>The room's crates, where their flag allows: on their tile's floor, as many crates up as they stand.</summary>
    private void PlaceCrates()
    {
        _crates.Clear();
        _carried = null;
        foreach (var value in Document.GetValueOrDefault("crates", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            if (!Field.Actors.TryGetValue(record["id"].AsInt32().ToString(), out var node)) continue;
            int flag = FlagValue(record["flag"].AsInt32());
            bool present = flag >= record["flag_min"].AsInt32() && flag <= record["flag_max"].AsInt32();
            node.Visible = present;
            if (!present) continue;
            var tile = record["tile"].AsGodotArray();
            var crate = new Crate
            {
                Id = record["id"].AsInt32().ToString(), Kind = record["kind"].AsInt32(), X = tile[0].AsInt32(), Z = tile[1].AsInt32(), Moves = record["moves"].AsInt32(),
                Orientation = record.GetValueOrDefault("orientation", -1).AsInt32(), Node = node,
            };
            int x = tile[0].AsInt32(), z = tile[1].AsInt32();
            float ground = _heights.Length == 0 ? 0 : _heights[Math.Clamp(z * 4 + 2, 0, _rows - 1) * _columns + Math.Clamp(x * 4 + 2, 0, _columns - 1)];
            crate.Bottom = ground - record["stack"].AsInt32() * 128;
            _crates.Add(crate);
            ShowCrate(crate);
        }
    }

    /// <summary>Puts a crate's node where it stands: its model is centred, so half a crate above its base.</summary>
    private void ShowCrate(Crate crate, Vector3? at = null)
    {
        var centre = at ?? new Vector3(crate.X * 128 + 64, crate.Bottom - 64, crate.Z * 128 + 64);
        crate.Node.Position = EventActor.RetailSpace * centre;
        // A crate of kind 0 or 1 stands in one of 24 orientations, quarter turns made in retail space.
        var turns = Document.GetValueOrDefault("crate_turns", new GArray()).AsGodotArray();
        if (crate.Orientation < 0 || crate.Orientation >= turns.Count) return;
        var turn = turns[crate.Orientation].AsGodotArray();
        var retail = new Basis(Vector3.Up, turn[1].AsInt32() * Mathf.Pi / 2) * new Basis(Vector3.Right, turn[0].AsInt32() * Mathf.Pi / 2) * new Basis(Vector3.Back, turn[2].AsInt32() * Mathf.Pi / 2);
        var flip = new Basis(Vector3.Right, new Vector3(0, -1, 0), new Vector3(0, 0, -1));
        crate.Node.Basis = flip * retail * flip;
    }

    /// <summary>
    /// Cross at a crate in front of Ashley, its base no more than 128 below her and 64 above. A crate (kinds 0 to 3)
    /// is pushed one tile on, if the tile beyond is free and no higher than its base, and she steps after it; kinds 0
    /// and 1 roll onto their next face as they go, kind 3 slides on while the next tile is level and free, and the
    /// breaking crate (1) breaks after its set number of moves. A block (kinds 4 to 6) is lifted onto her head, and
    /// Cross again sets it down in front of her, on whatever stands there.
    /// </summary>
    private bool MoveCrate(EventActor ashley, (int X, int Z, int Quadrant) front)
    {
        int dx = front.X - (int)Mathf.Floor(ashley.Position.X / 128), dz = front.Z - (int)Mathf.Floor(ashley.Position.Z / 128);
        if (_carried is { } held)
        {
            var place = new Vector3(front.X * 128 + 64, 0, front.Z * 128 + 64);
            float floor = FloorAt(place) ?? float.MaxValue;
            if (floor == float.MaxValue || ashley.Position.Y - floor > 128) return true;
            held.Carried = false;
            held.X = front.X;
            held.Z = front.Z;
            held.Bottom = floor;
            ShowCrate(held);
            _carried = null;
            ashley.Animate("base_76", 4, once: true);
            return true;
        }
        Crate? target = null;
        foreach (var crate in _crates)
            if (!crate.Carried && crate.X == front.X && crate.Z == front.Z && (target == null || crate.Bottom < target.Bottom)) target = crate;
        if (target == null || target.Bottom < ashley.Position.Y - 128 || target.Bottom > ashley.Position.Y + 64) return false;
        int lift = (int)((ashley.Position.Y - target.Bottom) / 64) + 1;
        if (target.Kind >= 4)
        {
            target.Carried = true;
            _carried = target;
            ashley.Animate($"base_{71 + Math.Clamp(lift, 0, 3)}", 4, once: true);
            return true;
        }
        var beyond = new Vector3((front.X + dx) * 128 + 64, 0, (front.Z + dz) * 128 + 64);
        if (FloorAt(beyond) is not { } next || next < target.Bottom) return true;
        target.X += dx;
        target.Z += dz;
        target.Bottom = next;
        var from = target.Node.Transform;
        // A rolled crate's new orientation comes from the push's direction: 0 is -z, 1 -x, 2 +z and 3 +x.
        var rolls = Document.GetValueOrDefault("crate_rolls", new GArray()).AsGodotArray();
        if (target.Orientation >= 0 && target.Orientation < rolls.Count)
            target.Orientation = rolls[target.Orientation].AsGodotArray()[dz < 0 ? 0 : dx < 0 ? 1 : dz > 0 ? 2 : 3].AsInt32();
        // A sliding block (3) carries on over level ground until something stops it.
        while (target.Kind == 3 && FloorAt(new Vector3((target.X + dx) * 128 + 64, 0, (target.Z + dz) * 128 + 64)) is { } level && level == target.Bottom)
        {
            target.X += dx;
            target.Z += dz;
        }
        ShowCrate(target);
        // It moves over the retail motion's fourteen event frames; a rolling crate rises on its edge as it turns.
        var to = target.Node.Transform;
        bool rolling = target.Orientation >= 0;
        target.Node.Transform = from;
        var motion = CreateTween();
        motion.TweenMethod(Callable.From<float>(t =>
        {
            var at = from.InterpolateWith(to, t);
            if (rolling) at.Origin += Vector3.Up * (0.21f * Mathf.Sin(t * Mathf.Pi));
            target.Node.Transform = at;
        }), 0f, 1f, 14 / 30.0);
        ashley.Animate($"base_{79 + Math.Clamp(lift, 0, 3)}", 4, once: true);
        ashley.Position = new Vector3(front.X * 128 + 64, FloorHeight(new Vector3(front.X * 128 + 64, 0, front.Z * 128 + 64)), front.Z * 128 + 64);
        if (target.Kind == 1 && target.Moves > 0 && --target.Moves == 0)
        {
            target.Node.Visible = false;
            _crates.Remove(target);
        }
        return true;
    }

    /// <summary>The room's floor heights by 32-unit sub-cell and its tile attribute words, read once as it loads.</summary>
    private float[] _heights = Array.Empty<float>();
    private long[] _attributes = Array.Empty<long>();
    private int _columns, _rows;

    private void ReadFloor()
    {
        var floor = Document.GetValueOrDefault("floor", new GDictionary()).AsGodotDictionary();
        _columns = floor.Count == 0 ? 0 : floor["columns"].AsInt32();
        _rows = floor.Count == 0 ? 0 : floor["rows"].AsInt32();
        var heights = floor.Count == 0 ? new GArray() : floor["heights"].AsGodotArray();
        _heights = new float[heights.Count];
        for (int i = 0; i < heights.Count; i++) _heights[i] = heights[i].AsSingle();
        var words = Document.GetValueOrDefault("tile_attributes", new GArray()).AsGodotArray();
        _attributes = new long[words.Count];
        for (int i = 0; i < words.Count; i++) _attributes[i] = words[i].AsInt64();
    }

    /// <summary>The floor height under a map position, or null outside the room's collision map.</summary>
    public float? FloorAt(Vector3 position)
    {
        int x = (int)Mathf.Floor(position.X / 32), z = (int)Mathf.Floor(position.Z / 32);
        if (x < 0 || z < 0 || x >= _columns || z >= _rows) return null;
        // A tile whose blocking class is 5 has no floor to stand on.
        // A platform is floor even over the shaft or pit it rides in.
        if (PlatformTop(position) is { } platform) return platform;
        if (Solid(position)) return null;
        return CrateTop(position) ?? _heights[z * _columns + x];
    }

    private bool _jumpPressed;

    /// <summary>Whether a position is on a stair tile (attribute bit 5), where rises are not walls.</summary>
    public bool Stairs(Vector3 position) => (TileAttributes((int)Mathf.Floor(position.X / 128), (int)Mathf.Floor(position.Z / 128)) & 1 << 5) != 0;

    /// <summary>
    /// Whether a position is solid by its tile's blocking class (bits 10 to 12): 5 the whole tile, and 1 to 4 the
    /// half beyond a diagonal or an edge, by the position within the tile (fx, fz from 0 to 127): 1 where fx + fz is
    /// over 128, 2 where fx is over fz, 3 where fx + fz is under 128, 4 where fx is under fz.
    /// </summary>
    public bool Solid(Vector3 position)
    {
        int x = (int)Mathf.Floor(position.X / 128), z = (int)Mathf.Floor(position.Z / 128);
        float fx = position.X - x * 128, fz = position.Z - z * 128;
        return (TileAttributes(x, z) >> 10 & 7) switch
        {
            5 => true, 1 => fx + fz > 128, 2 => fx > fz, 3 => fx + fz < 128, 4 => fx < fz, _ => false,
        };
    }

    /// <summary>Whether a collision tile's blocking class (bits 10 to 12 of its attributes) is 5.</summary>
    public bool Blocked(int x, int z) => (TileAttributes(x, z) >> 10 & 7) == 5;

    /// <summary>
    /// A walking route across the room's collision map from one map position to another, as the centres of the
    /// 32-unit sub-cells it passes, ending at the destination; empty when there is none. A step may climb at most
    /// 96 units (ground any higher is a wall), and no sub-cell within one of a wall is used, so a body of Ashley's
    /// radius clears it. The search is A* over the eight neighbours, and the route keeps only its turning points.
    /// </summary>
    public List<Vector3> Path(Vector3 from, Vector3 to)
    {
        var route = new List<Vector3>();
        if (_heights.Length == 0) return route;
        int columns = _columns, rows = _rows, cell = 32;
        var heights = _heights;
        float Height(int x, int z) => heights[z * columns + x];
        bool Inside(int x, int z) => x >= 0 && z >= 0 && x < columns && z < rows;
        // A sub-cell is clear when nothing within one cell of it rises more than a step above it.
        bool Clear(int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!Inside(x + dx, z + dz) || Height(x, z) - Height(x + dx, z + dz) > FieldPlayer.StepHeight) return false;
            return true;
        }
        (int, int) Cell(Vector3 p) => (Math.Clamp((int)Mathf.Floor(p.X / cell), 0, columns - 1), Math.Clamp((int)Mathf.Floor(p.Z / cell), 0, rows - 1));
        var start = Cell(from);
        var goal = Cell(to);
        var cost = new Dictionary<(int, int), float> { [start] = 0 };
        var came = new Dictionary<(int, int), (int, int)>();
        var open = new PriorityQueue<(int, int), float>();
        open.Enqueue(start, 0);
        bool found = false;
        int visited = 0;
        while (open.Count > 0 && visited++ < columns * rows)
        {
            var current = open.Dequeue();
            if (current == goal) { found = true; break; }
            var (cx, cz) = current;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (!Inside(nx, nz) || Height(cx, cz) - Height(nx, nz) > FieldPlayer.StepHeight || Blocked(nx / 4, nz / 4)) continue;
                    if ((nx, nz) != goal && !Clear(nx, nz)) continue;
                    // A diagonal may not cut a corner.
                    if (dx != 0 && dz != 0 && (!Clear(cx + dx, cz) || !Clear(cx, cz + dz))) continue;
                    float next = cost[current] + (dx != 0 && dz != 0 ? 1.4142f : 1);
                    if (cost.TryGetValue((nx, nz), out var known) && known <= next) continue;
                    cost[(nx, nz)] = next;
                    came[(nx, nz)] = current;
                    open.Enqueue((nx, nz), next + new Vector2(goal.Item1 - nx, goal.Item2 - nz).Length());
                }
        }
        if (!found) return route;
        var cells = new List<(int, int)>();
        for (var c = goal; c != start; c = came[c]) cells.Add(c);
        cells.Reverse();
        (int, int)? direction = null;
        for (int i = 0; i < cells.Count - 1; i++)
        {
            var step = (cells[i + 1].Item1 - cells[i].Item1, cells[i + 1].Item2 - cells[i].Item2);
            if (step != direction && i > 0) route.Add(new Vector3((cells[i].Item1 + 0.5f) * cell, Height(cells[i].Item1, cells[i].Item2), (cells[i].Item2 + 0.5f) * cell));
            direction = step;
        }
        route.Add(to);
        return route;
    }

    private Dictionary<(int, int), List<(int Y, Vector3 Color)>>? _floorLight;

    /// <summary>
    /// The light on a character where it stands: the room's light map (BATTLE.PRG 0x80098b38) gives the colour of the
    /// first record whose rectangle holds the point and whose triangle (wound as the GTE's NCLIP of corners 0, 2, 1
    /// finds it positive) contains it, mixed from its corners by area, or the map's own colour; installations without
    /// the map take the floor tile's baked light.
    /// </summary>
    private Vector3 FloorLight(Vector3 position)
    {
        if (Document.GetValueOrDefault("light_map").VariantType == Variant.Type.Dictionary)
        {
            var map = Document["light_map"].AsGodotDictionary();
            int x = (int)position.X, z = (int)position.Z;
            foreach (var value in map["records"].AsGodotArray())
            {
                var r = value.AsGodotArray();
                int V(int i) => r[i].AsInt32();
                if (x < V(0) || x > V(2) || z < V(1) || z > V(3)) continue;
                // NCLIP of three screen points: the signed doubled area.
                static long Nclip(long ax, long ay, long bx, long by, long cx, long cy) => ax * by + bx * cy + cx * ay - ax * cy - bx * ay - cx * by;
                long whole = Nclip(V(4), V(5), V(8), V(9), V(6), V(7));
                if (whole <= 0) continue;
                long w0 = Nclip(x, z, V(8), V(9), V(6), V(7));
                if (w0 < 0) continue;
                long w2 = Nclip(x, z, V(6), V(7), V(4), V(5));
                if (w2 < 0) continue;
                long w1 = whole - w0 - w2;
                if (w1 < 0) continue;
                float Mix(int channel) => (V(10 + channel) * w0 + V(13 + channel) * w1 + V(16 + channel) * w2) / (float)whole;
                return new Vector3(Mix(0), Mix(1), Mix(2));
            }
            var colour = map["colour"].AsGodotArray();
            return new Vector3(colour[0].AsSingle(), colour[1].AsSingle(), colour[2].AsSingle());
        }
        // A room without a light map lights characters plainly (0x80; 0x80098B38 keeps its 0x808080 when the room has
        // none: MAP172's dead men). Only an installation made before the map was kept has no key for it at all.
        if (Document.ContainsKey("light_map")) return new Vector3(128, 128, 128);
        if (_floorLight == null)
        {
            _floorLight = new();
            foreach (var value in Document.GetValueOrDefault("floor_light", new GArray()).AsGodotArray())
            {
                var entry = value.AsGodotArray();
                var key = (entry[0].AsInt32(), entry[1].AsInt32());
                if (!_floorLight.TryGetValue(key, out var list)) _floorLight[key] = list = new();
                list.Add((entry[2].AsInt32(), new Vector3(entry[3].AsSingle(), entry[4].AsSingle(), entry[5].AsSingle())));
            }
        }
        var tile = ((int)Mathf.Floor(position.X / 128), (int)Mathf.Floor(position.Z / 128));
        if (!_floorLight.TryGetValue(tile, out var floors)) return new Vector3(64, 64, 64);
        var best = floors[0];
        foreach (var floor in floors) if (Math.Abs(floor.Y - position.Y) < Math.Abs(best.Y - position.Y)) best = floor;
        return best.Color;
    }

    /// <summary>Places the free camera on its orbit around the room's focus (used while no event drives the camera).</summary>
    public void PlaceCamera()
    {
        var eye = _focus + new Vector3(Mathf.Sin(Heading) * Mathf.Cos(Pitch), Mathf.Sin(Pitch), Mathf.Cos(Heading) * Mathf.Cos(Pitch)) * Distance;
        Field.SnapCamera(CameraPart.Eye, eye);
        Field.SnapCamera(CameraPart.Target, _focus);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Field == null || Failure.Length != 0) return;
        PlayFrames++;
        if (!EventCamera.Active && !Controlled)
        {
            float turn = Input.GetAxis("ps_left", "ps_right"), tilt = Input.GetAxis("ps_down", "ps_up");
            if (turn != 0 || tilt != 0)
            {
                Heading += turn * 0.03f;
                Pitch = Math.Clamp(Pitch + tilt * 0.02f, -0.2f, 1.5f);
                PlaceCamera();
            }
        }
        // Start (or b7 2) skips an open Start-skippable section to its fix-up.
        if (_events != null && _skippable && !Controlled && (_skipRequested || Input.IsActionJustPressed("ps_start"))) Skip();
        else if (_choice != null) StepChoice();
        else if (_events != null && (Input.IsActionJustPressed("ps_circle") || Input.IsActionJustPressed("ps_start"))) Field.AdvanceDialogue();
        if (Input.IsActionJustPressed("ps_circle")) _pressedButtons |= 0x20;
        if (Input.IsActionJustPressed("ps_cross")) _pressedButtons |= 0x40;
        // The retail event clock runs at 30 Hz: one event frame for every two display frames.
        _oddFrame = !_oddFrame;
        if (Input.IsActionJustPressed("ps_select")) _zoomPressed = true;
        foreach (string button in BattleButtons) if (Input.IsActionJustPressed(button)) _presses.Add(button);
        if (_events != null && _oddFrame) StepEvent();
        if (_oddFrame) _rain.Step();
        if (_oddFrame)
            foreach (var effect in _effects.ToArray())
            {
                effect.Tick();
                if (effect.Finished) { _effects.Remove(effect); effect.QueueFree(); }
            }
        if (_oddFrame && Field != null)
        {
            // The effects' camera shake: across and up the view (map units; y is down). Its push along the view and its
            // roll are not followed.
            var shake = Vector4.Zero;
            foreach (var effect in _effects) shake += effect.Shake;
            Field.Camera.HOffset = shake.X / Installation.MapConversion.UnitsPerMetre;
            Field.Camera.VOffset = -shake.Y / Installation.MapConversion.UnitsPerMetre;
        }
        if (_oddFrame) { StepPalettes(); StepPlatforms(); StepSwings(); StepGroupScripts(); foreach (var actor in Actors.Values) { actor.StepSway(); actor.StepLook(); } }
        // The event camera's roll (opcode e2, 4096 a turn): the view turns clockwise about its axis by it, after the
        // camera looks at its target.
        float roll = EventCamera.Active && !Controlled ? EventCamera.Roll * Mathf.Tau / 4096f : 0;
        var rolled = new Quaternion(Vector3.Back, roll);
        if (!Field!.CameraViewRotation.IsEqualApprox(rolled)) Field.SetCameraViewRotation(rolled);
        ApplyCamera();
        ShowRoomGroups();
        StepTextureFrames();
        Field.AdvancePresentation();
        // Enhanced lighting blooms what is bright.
        if (FieldLighting.Enhanced || Field.FieldEnvironment?.Environment.GlowEnabled == true)
        {
            if (Field.FieldEnvironment == null)
            {
                Field.FieldEnvironment = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.ClearColor } };
                Field.AddChild(Field.FieldEnvironment);
            }
            FieldLighting.Apply(Field.FieldEnvironment.Environment);
        }
        // Enhanced characters are shaded by a light that turns with the camera.
        if (CharacterQuality.Enhanced || _characterLight != null)
        {
            if (Field.FieldEnvironment == null)
            {
                Field.FieldEnvironment = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.ClearColor } };
                Field.AddChild(Field.FieldEnvironment);
            }
            // A room change frees the scene the light stood in; it is made again in the new one.
            if (_characterLight == null || !GodotObject.IsInstanceValid(_characterLight) || !_characterLight.IsInsideTree())
                _characterLight = CharacterQuality.Light(Field.Geometry);
            CharacterQuality.Follow(_characterLight, Field.Camera, Field.FieldEnvironment);
        }
        // Room faces and characters the camera stands close to are drawn as the GTE and GPU would show them.
        DropNearFaces();
        bool staged = EventCamera.Active && !Controlled;
        foreach (var actor in Actors.Values) actor.ProjectAsRetail(Field.Camera, staged ? EventCamera.Projection : Gameplay.Projection, staged ? EventCamera.Far : 4096);
        foreach (var effect in _effects) effect.NearLimit = (staged ? EventCamera.Projection : Gameplay.Projection) / 2 * 4096 / (staged ? EventCamera.Far : 4096);
        PlaceSky();
        // The simple map shows while Ashley is in hand, the HUD is up and nothing pauses the field.
        // It is put away with the HUD while an action plays (footage: Heal's and the Elixir's casting).
        _minimap.Set(Controlled && HudShown && !_menu.Open && Battle?.Paused != true && Battle?.Hud?.Acting != true && !_lookAround, Gameplay.Yaw, Battle?.Drawn == true);
        var fade = (ShaderMaterial)_screen.Material;
        fade.SetShaderParameter("level", _displayOn ? _fade : 1);
        fade.SetShaderParameter("white", _fadeWhite && _displayOn);
        fade.SetShaderParameter("linear", GetTree().Root.UseHdr2D);
        _screen.Visible = _fade > 0 || !_displayOn;
        PlaceEffectsView();
    }

    private void StepEvent()
    {
        EventFrame++;
        EventCamera.Step();
        _overlay.Step();
        _echo.Step();
        StepBalloons();
        foreach (var actor in Actors.Values) actor.Step();
        // Clips' scripts show and hide the held weapon (operations 32 to 35) on their frames.
        if (!Controlled) foreach (var actor in Actors.Values) RunClipScript(actor);
        // Clips with a movement stream carry their actor (an event's getting-up, MAP022's after the vision).
        foreach (var actor in Actors.Values) RunClipMovement(actor);
        // Their sounds play in play too: a footfall, the thud of a body.
        foreach (var actor in Actors.Values) RunClipSounds(actor);
        // Events draw and sheathe weapons too (3f); damage numbers left by the fight run out during them.
        Battle?.ShowWeapons();
        if (!Controlled) Battle?.StepNumbers();
        foreach (var actor in Actors.Values)
        {
            if (!actor.Tinted) Light(actor, actor.Parent != null ? actor.Parent.Light : FloorLight(actor.Position));
            actor.Apply();
        }
        FadeTheDead();
        _fade = Mathf.Clamp(_fade + _fadeSpeed, 0, 1);
        foreach (var (id, done) in _waits.ToArray())
            if (done()) { _waits.Remove((id, done)); _completions.Add(id); }
        var events = _events!;
        // A completion for a wait its flow has since let go of (a restart, a skip) no longer matches and is ignored.
        foreach (var id in _completions) events.Complete(id);
        _completions.Clear();
        Drive(events.AdvanceTick);
        if (Failure.Length != 0) return;
        if (_restarts.Count > 0)
        {
            var restarts = _restarts.ToArray();
            _restarts.Clear();
            Drive(() =>
            {
                foreach (int slot in restarts) { events.Cancel(slot); events.Start(slot); }
                events.Drain();
            });
            if (Failure.Length != 0) return;
        }
        // What the frame's commands moved shows this frame, with the camera they cut to: placed only before them, a
        // character moved on a cut stood for a frame where it was, out of the new shot.
        foreach (var actor in Actors.Values) actor.Apply();
        StepControl();
        foreach (var actor in Actors.Values) PlaceShadow(actor);
        if (_pendingTransition is { } transition)
        {
            // The room change cancels the running event and plays the new room's own script.
            _pendingTransition = null;
            int zone = transition["zone"].AsInt32();
            var tile = transition["tile"].AsGodotArray();
            // 68 fills a door record (0x800B98C0) and arrives through it, Ashley facing its quadrant (with the walk-in
            // flag, bit 1, she walks in along it). Footage, Ep. 2 13:16-13:22: after map017's vision she gets up in
            // map022 with her back to the opening camera and is framed from behind (the script picks that framing by
            // flag 0xaf, the octant play's camera was turned to).
            int changeFlags = transition.GetValueOrDefault("flags", 0).AsInt32();
            float facing = transition["facing"].AsSingle();
            _arrival = (tile[0].AsInt32(), tile[1].AsInt32(), facing);
            _arrivalHalf = (changeFlags & 1) != 0;
            LoadRoom(zone == 0 ? Zone : zone, transition["map"].AsInt32());
            return;
        }
        if (_pendingChain.Length != 0 && !Active(0))
        {
            // A chained event file runs in the same room once the current event's main thread has ended.
            string chain = _pendingChain;
            _pendingChain = "";
            StartEvent(chain);
        }
    }

    /// <summary>
    /// Once no event runs and the camera has come back to Ashley, the player has her and the gameplay camera follows.
    /// </summary>
    private void StepControl()
    {
        if (_door != null) { StepDoor(); return; }
        if (!Controlled && Settled && _gameOverEvent)
        {
            _gameOverEvent = false;
            // Then the LOAD screens: a game loaded from them resumes, and leaving them goes to the title.
            // The GAME OVER picture goes as the screens come.
            FreezeActors(true);
            _overlay.Clear();
            _menu.OpenLoad();
            return;
        }
        if (!Controlled && Settled && Player is { } ashley && _pendingTransition == null)
        {
            Controlled = true;
            if (!_unmapped) Visited.Add((Zone, Map));
            _minimap.SetRooms(Document.GetValueOrDefault("minimap", default).AsGodotArray(), Visited, flag => FlagValue(flag) != 0, EnteredMarker);
            // Presses that answered the event's text are not the player's first moves.
            _presses.Clear();
            ashley.Visible = true;
            // An event's clip scripts show and hide held weapons only while it plays; in play the draw state does.
            foreach (var actor in Actors.Values) actor.WeaponOut = null;
            Battle?.EndSphere();
            _control = new FieldPlayer(ashley, FloorAt, Stairs) { Set = Battle?.Drawn == true && Battle.Ashley != null ? Battle.Ashley.BattleSet : "base", Crowded = (mover, next) => Battle?.Crowded(mover, next) == true };
            Gameplay.Place(ashley.Position);
            if (EventCamera.Active) Gameplay.LookAt = EventCamera.LookAtPosition;
        }
        // The game over's LOAD screens run with no one in control.
        if (!Controlled && _menu.Open) { _menu.Step(button => _presses.Contains(button)); _presses.Clear(); return; }
        if (!Controlled || Player is not { } player || _control == null) { _presses.Clear(); StepMessage(); return; }
        StepMessage();
        // While a lid swings open she waits.
        if (_afterLid != null) { StepAfterLid(); _presses.Clear(); return; }
        OfferDrops();
        // Once the drops are offered, a reclaim due after her action.
        if (!_menu.Open) { CheckReclaim(); if (_menu.Open) FreezeActors(true); }
        // Triangle opens the menu, and time stands still while it is open.
        if (_menu.Open)
        {
            if (!_menu.Step(button => _presses.Contains(button))) FreezeActors(false);
            _presses.Clear();
            return;
        }
        if (_presses.Contains("ps_triangle") && Battle is { Busy: false } && !_control.Busy)
        {
            _presses.Clear();
            _control.Halt();
            FreezeActors(true);
            _menu.Show(true);
            return;
        }
        // Start with the weapon sheathed is the manual's free-look, through Ashley's eyes; Start, Cross or Triangle looks back.
        if (_lookAround)
        {
            if (_presses.Contains("ps_start") || _presses.Contains("ps_cross") || _presses.Contains("ps_triangle")) LookAround(player, false);
            else
            {
                var look = Input.GetVector("ps_left", "ps_right", "ps_up", "ps_down");
                _lookYaw = Mathf.PosMod(_lookYaw - look.X * 32, 4096);
                _lookPitch = Math.Clamp(_lookPitch - look.Y * 24, -0x300, 0x300);
            }
            _presses.Clear();
            return;
        }
        if (_presses.Contains("ps_start") && Battle is { Drawn: false, Busy: false } && !_control.Busy && _carried == null)
        {
            _presses.Clear();
            _control.Halt();
            LookAround(player, true);
            return;
        }
        // Cross with the weapon sheathed acts on what is in front of Ashley.
        if (_presses.Contains("ps_cross") && Battle is { Drawn: false, Busy: false } && !_control.Busy) { _presses.Remove("ps_cross"); Interact(player); }
        if (Battle is { } battle)
        {
            battle.Step(_control, button => !_control.Busy && _presses.Contains(button));
            if (battle.GameOver && !_gameOverShown)
            {
                _gameOverShown = true;
                // The game-over scene (EVENT 0000) plays, then the game returns to the title.
                if (_content.Has("event_evt0000")) { _gameOverEvent = true; StartEvent("event_evt0000"); }
                else ShowNotice("Ashley has fallen.\n\nGame over.");
            }
        }
        _jumpPressed = _presses.Contains("ps_square") && Battle is not { Busy: true };
        _presses.Clear();
        if (Battle is not { Busy: true } && Battle?.GameOver != true)
        {
            var stick = Input.GetVector("ps_left", "ps_right", "ps_up", "ps_down");
            // Crippled legs and slowing halve her pace, quickening adds a quarter; paralysis holds her.
            var fighter = Battle?.Ashley;
            _control.Pace = (Battle?.Lame == true ? 0.5f : 1) * (fighter?.Has(FieldBattle.Slow) == true ? 0.5f : 1) * (fighter?.Has(FieldBattle.Quicken) == true ? 1.25f : 1);
            if (fighter?.Has(FieldBattle.Paralysis) == true) stick = Vector2.Zero;
            // Carrying a block she walks, and does not jump; the block rides on her head.
            _control.Step(stick.Length() > 0.25f ? Gameplay.Heading(stick) : null, Input.IsActionPressed("ps_r2") || _carried != null, _jumpPressed && _carried == null);
            if (_carried != null) ShowCrate(_carried, player.Position + new Vector3(0, -460, 0));
        }
        int turn = (Input.IsActionPressed("ps_l1") ? 1 : 0) - (Input.IsActionPressed("ps_r1") ? 1 : 0);
        // An effect may have the camera look at one of its anchors for a while.
        Vector3? focus = null;
        foreach (var effect in _effects) focus ??= effect.Focus;
        Gameplay.Step(focus ?? player.Position, turn, _zoomPressed);
        _zoomPressed = false;
        // The timer counts two sixtieths a frame of retail's 30 Hz play.
        if (_oddFrame) StepTimer();
        // Conditions on where Ashley is and what is left in the room are tested every frame of play.
        CheckConditions(0x20);
        // Stepping onto a tile tests what the tile holds: its conditions (bit 13), then a door (bit 14).
        var tile = ((int)Mathf.Floor(player.Position.X / 128), (int)Mathf.Floor(player.Position.Z / 128));
        if (tile != _tile && Controlled)
        {
            _tile = tile;
            SavePoint = 0;
            long attributes = TileAttributes(tile.Item1, tile.Item2);
            if ((attributes & 1 << 13) != 0) CheckConditions(0x02);
            if (Controlled && (attributes & 1 << 16) != 0) StepOnTrap(tile);
            // A pit under a platform she stands on is crossed on the platform.
            if (Controlled && (attributes & 1 << 17) != 0 && PlatformTop(player.Position) == null) FallInPit();
            if (Controlled && (attributes & 1 << 14) != 0) EnterDoor(tile);
        }
    }

    /// <summary>Stops or restarts every actor's animation.</summary>
    private void FreezeActors(bool frozen)
    {
        foreach (var actor in Actors.Values) if (actor.Player != null) actor.Player.SpeedScale = frozen ? 0 : 1;
    }

    /// <summary>What Ashley carries: other items by id with their counts, and her equipment records as installed.</summary>
    public readonly Dictionary<int, int> Items = new();
    /// <summary>The abilities Ashley has learned (actions), in the order learned.</summary>
    public readonly List<int> Abilities = new();
    /// <summary>The rooms Ashley has had the run of (zone, map), as the map screen shows them.</summary>
    public readonly HashSet<(int Zone, int Map)> Visited = new();

    /// <summary>The spells Ashley has learned (actions).</summary>
    public readonly List<int> Spells = new();
    /// <summary>The spells Ashley has cast to the end at least once (retail's action unlocked byte), which the save keeps.</summary>
    public readonly HashSet<int> Mastered = new();
    /// <summary>The weapon Ashley has taken up (blade and grip records, and a name), or null for her starting one; her armour by limb (-1 the accessory, -3 the shield).</summary>
    public GDictionary? EquippedWeapon;
    public readonly Dictionary<int, GDictionary> EquippedArmour = new();

    /// <summary>
    /// Takes up a weapon: its stats in battle, and its model in her hand (converted at installation for every blade
    /// she can come by).
    /// </summary>
    public void Equip(GDictionary weapon)
    {
        EquippedWeapon = weapon;
        Unarmed = false;
        if (Battle?.Ashley is not { } ashley) return;
        Battle.EquipWeapon(ashley, weapon["blade"].AsGodotDictionary(), weapon["grip"].AsGodotDictionary(), weapon.ContainsKey("entry") ? GemsOf(weapon) : null);
        // Taking up a two-handed weapon sets the shield aside.
        if (TwoHanded && EquippedArmour.Remove(-3)) { FieldBattle.EquipArmour(ashley, -3, new GDictionary()); Arm(ashley, "", shield: true); }
        if (_control != null && Battle.Drawn) _control.Set = ashley.BattleSet;
        ashley.OwnWeapon = weapon["blade"].AsGodotDictionary().GetValueOrDefault("asset", "").AsString();
        Arm(ashley, ashley.OwnWeapon);
    }

    /// <summary>Whether Ashley has put her weapon away and fights bare-handed (battle set 10, 00_BTA.SEQ).</summary>
    public bool Unarmed;

    /// <summary>Whether Ashley holds this weapon: the one she took up, or else the inventory's equipped one.</summary>
    public bool Holds(GDictionary weapon)
    {
        if (Unarmed) return false;
        if (EquippedWeapon is { } held) return held["blade"].AsGodotDictionary().RecursiveEqual(weapon["blade"].AsGodotDictionary());
        return weapon.GetValueOrDefault("entry", new GDictionary()).AsGodotDictionary().GetValueOrDefault("record", new GDictionary()).AsGodotDictionary().GetValueOrDefault("equipped", false).AsBool();
    }

    /// <summary>Puts her weapon away: she fights with her fists (category 10, blunt, no weapon strength) and holds nothing.</summary>
    public void Unequip()
    {
        EquippedWeapon = null;
        Unarmed = true;
        if (Battle?.Ashley is not { } ashley) return;
        Battle.EquipWeapon(ashley, new GDictionary { ["category"] = 10, ["damage_type"] = 1 }, new GDictionary());
        if (_control != null && Battle.Drawn) _control.Set = ashley.BattleSet;
        ashley.OwnWeapon = "";
        Arm(ashley, "");
    }

    /// <summary>Puts on a piece of armour: a helm on the head, body armour on the body, leggings on the legs, an arm piece on the right arm or else the left; an accessory or a shield (items 127 to 142) goes on alone.</summary>
    public string Wear(GDictionary record, int? arm = null)
    {
        int id = record["id"].AsInt32();
        int limb = id switch { >= 127 and <= 142 => -3, >= 143 and <= 158 => 2, >= 159 and <= 174 => 3, >= 175 and <= 190 => 4, >= 191 and <= 206 => arm ?? (EquippedArmour.ContainsKey(0) && !EquippedArmour[0].RecursiveEqual(record) ? 1 : 0), >= 207 and <= 253 => -1, _ => -2 };
        if (limb == -2) return $"The {ItemName(id)} cannot be worn.";
        if (limb == -3 && TwoHanded) return "A shield cannot be used with a two-handed weapon.";
        EquippedArmour[limb] = record;
        if (Battle?.Ashley is { } ashley)
        {
            FieldBattle.EquipArmour(ashley, limb, record);
            if (limb == -3) { Arm(ashley, record.GetValueOrDefault("asset", "").AsString(), shield: true); Battle.ShowWeapons(); }
        }
        return $"Ashley put on the {ItemName(id)}.";
    }

    /// <summary>The room's own record in its area map, or null.</summary>
    public GDictionary? AreaRoom(int zone, int map)
    {
        int scene = zone == Zone && map == Map ? Document.GetValueOrDefault("scene", -1).AsInt32() : -1;
        for (int area = scene > 0 ? scene : 1; area < (scene > 0 ? scene + 1 : 32); area++)
        {
            if (!_content.Has($"area_{area:d2}")) continue;
            foreach (var value in _content.Data($"area_{area:d2}")["rooms"].AsGodotArray())
                if (value.AsGodotDictionary() is var room && room["zone"].AsInt32() == zone && room["map"].AsInt32() == map) return room;
        }
        return null;
    }

    /// <summary>
    /// The marker on Ashley's room's area map of the door she came in by, which the map blinks (BATTLE.PRG
    /// 0x8008d948; set by the door she came through, or by opcode b1), or -1.
    /// </summary>
    public int EnteredMarker { get; private set; } = -1;

    /// <summary>The room's workshop kind (its sub-section 10, byte 0x30), 0 where there is none.</summary>
    public int WorkshopKind => Document.GetValueOrDefault("workshop", 0).AsInt32();

    /// <summary>
    /// Whether this workshop works a piece's material: a bit per material in the row of the workshops' material table
    /// for its kind (MENUC 0x8010BB30), as its Combine lists only those pieces as usable.
    /// </summary>
    public bool Works(GDictionary record)
    {
        if (!_content.Has("combining") || !_content.Data("combining").ContainsKey("workshop_materials")) return true;
        var rows = _content.Data("combining")["workshop_materials"].AsGodotArray();
        int kind = WorkshopKind;
        return kind > 0 && kind < rows.Count && ((rows[kind].AsInt32() >> record.GetValueOrDefault("material", 0).AsInt32()) & 1) != 0;
    }

    /// <summary>Whether the room is a workshop: its workshop kind, or (in installations without it) its area map's marker flag 8.</summary>
    public bool Workshop
    {
        get
        {
            if (Document.ContainsKey("workshop")) return WorkshopKind > 0;
            int scene = Document.GetValueOrDefault("scene", -1).AsInt32();
            if (scene <= 0 || !_content.Has($"area_{scene:d2}")) return false;
            foreach (var value in _content.Data($"area_{scene:d2}")["rooms"].AsGodotArray())
            {
                var room = value.AsGodotDictionary();
                if (room["zone"].AsInt32() != Zone || room["map"].AsInt32() != Map) continue;
                foreach (var marker in room["markers"].AsGodotArray())
                    if ((marker.AsGodotArray()[2].AsInt32() & 8) != 0) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The grip family a blade takes, by its category: guards and hilts (1) for daggers, swords and great swords;
    /// grips (2) for axes and maces, great axes, staves and heavy maces; poles (3) for polearms; bolts (4) for
    /// crossbows. Staves are an inference: no staff comes assembled.
    /// </summary>
    private static int GripFamily(int category) => category switch { 1 or 2 or 3 => 1, 4 or 5 or 6 or 7 => 2, 8 => 3, 9 => 4, _ => 0 };

    /// <summary>
    /// The loose blades Ashley carries, or with a blade picked, the loose grips that fit it: each the equipment entry.
    /// A carried weapon's own parts are not loose.
    /// </summary>
    public List<GDictionary> Parts(GDictionary? blade)
    {
        var used = SetParts();
        int family = blade == null ? 0 : GripFamily(blade["record"].AsGodotDictionary()["category"].AsInt32());
        var list = new List<GDictionary>();
        foreach (var value in Equipment)
        {
            var entry = value.AsGodotDictionary();
            string kind = entry["kind"].AsString();
            var record = entry["record"].AsGodotDictionary();
            if (kind is "blades" or "grips" && used.Contains((kind, record.GetValueOrDefault("slot", 0).AsInt32()))) continue;
            if (blade == null ? kind is "blade" or "blades" : kind is "grip" or "grips" && record["category"].AsInt32() == family) list.Add(entry);
        }
        return list;
    }

    /// <summary>
    /// Combines two loose blades into a new one, as the workshop's Combine does (MENUC 0x8010b2b4; see
    /// InventoryConversion.Combining): the blade and material the tables give for the pair, the new kind's model,
    /// category, cost, damage type, range and strength, intelligence and agility (plus its material's), DP and PP the
    /// mean of the two, and class and affinity values mixed. The two blades are used up.
    /// </summary>
    public string Combine(GDictionary first, GDictionary second)
    {
        if (!_content.Has("combining")) return "Nothing can be combined in this installation.";
        var table = _content.Data("combining");
        var a = first["record"].AsGodotDictionary();
        var b = second["record"].AsGodotDictionary();
        int Int(GDictionary d, string key) => d.GetValueOrDefault(key, 0).AsInt32();
        if (!Works(a) || !Works(b)) return "This workshop cannot work that material.";
        string kindA = first["kind"].AsString(), kindB = second["kind"].AsString();
        if (kindA is "armour" or "shield" || kindB is "armour" or "shield")
        {
            bool shieldA = Int(a, "id") is >= 127 and <= 142, shieldB = Int(b, "id") is >= 127 and <= 142;
            if (kindA is not ("armour" or "shield") || kindB is not ("armour" or "shield") || shieldA != shieldB) return "These cannot be combined.";
            return CombinePieces(first, second, a, b, table, shieldA);
        }
        if (kindA is not ("blade" or "blades") || kindB is not ("blade" or "blades")) return "These cannot be combined.";
        int id = table["results"].AsGodotArray()[Math.Clamp(Int(a, "id"), 0, 95) * 96 + Math.Clamp(Int(b, "id"), 0, 95)].AsInt32();
        if (id == 0) return "These blades cannot be combined.";
        int materialIndex = (Int(a, "material") - 3) * 500 + Int(a, "category") * 50 + (Int(b, "material") - 3) * 10 + Int(b, "category");
        var materials = table["materials"].AsGodotArray();
        int material = materialIndex >= 0 && materialIndex < materials.Count ? materials[materialIndex].AsInt32() : Int(a, "material");
        var kind = table["kinds"].AsGodotArray()[id].AsGodotDictionary();
        var bonus = table["material_stats"].AsGodotArray()[Math.Clamp(material, 0, 7)].AsGodotArray();
        var made = (GDictionary)a.Duplicate(true);
        made["id"] = id;
        made["material"] = material;
        foreach (string key in new[] { "model", "category", "cost", "damage_type", "range" }) made[key] = kind[key];
        made["strength"] = Int(kind, "strength") + bonus[0].AsInt32();
        made["intelligence"] = Int(kind, "intelligence") + bonus[1].AsInt32();
        made["agility"] = Int(kind, "agility") + bonus[2].AsInt32();
        int dp = (Int(a, "max_dp") + Int(b, "max_dp")) / 2;
        made["dp"] = dp;
        made["max_dp"] = dp;
        made["max_pp"] = (Int(a, "max_pp") + Int(b, "max_pp")) / 2;
        made["pp"] = (Int(a, "pp") + Int(b, "pp")) / 2;
        bool two = Int(a, "material") != Int(b, "material");
        var weights = table[two ? "two_materials" : "same_material"].AsGodotArray();
        made["classes"] = Mix(a, b, "classes", 6, table["class_positions"].AsGodotArray(), weights);
        made["affinities"] = Mix(a, b, "affinities", 7, table["affinity_positions"].AsGodotArray(), weights);
        // The new blade's model in its material's palette, converted for every kind and metal at installation.
        int palette = table["palettes"].AsGodotArray()[Math.Clamp(material, 0, 7)].AsInt32();
        made["asset"] = Int(made, "model") > 0 && _content.Has($"weapon_{Int(made, "model"):X2}_{palette}") ? $"weapon_{Int(made, "model"):X2}_{palette}" : "";
        Equipment.Remove(first);
        Equipment.Remove(second);
        Equipment.Add(new GDictionary { ["kind"] = "blade", ["record"] = made });
        return $"The {ItemName(Int(a, "id"))} and the {ItemName(Int(b, "id"))} made a {ItemName(id)}.";
    }

    /// <summary>
    /// Combines two shields (MENUC 0x8010b598) or two pieces of armour (0x8010b80c): the piece and material the tables
    /// give for their indices (item id less 126), the new piece's category (and a shield's model and gem slots) and its
    /// strength, intelligence and agility plus its material's, DP the mean of the two, and its type, class and affinity
    /// values mixed. The two are used up.
    /// </summary>
    private string CombinePieces(GDictionary first, GDictionary second, GDictionary a, GDictionary b, GDictionary table, bool shields)
    {
        int Int(GDictionary d, string key) => d.GetValueOrDefault(key, 0).AsInt32();
        var pieces = table[shields ? "shields" : "armour"].AsGodotDictionary();
        int indexA = Int(a, "id") - 126, indexB = Int(b, "id") - 126;
        var results = pieces["results"].AsGodotArray();
        int cell = shields ? indexA * 17 + indexB : (indexA - 16) * 65 + (indexB - 16);
        if (cell < 0 || cell >= results.Count || results[cell].AsInt32() == 0) return "These cannot be combined.";
        int index = results[cell].AsInt32();
        var materials = pieces["materials"].AsGodotArray();
        int materialCell = shields ? Int(a, "material") * 8 + Int(b, "material") : Int(a, "material") * 128 + (Int(a, "category") - 2) * 32 + Int(b, "material") * 4 + (Int(b, "category") - 2);
        int material = materialCell >= 0 && materialCell < materials.Count ? materials[materialCell].AsInt32() : Int(a, "material");
        var kind = pieces["kinds"].AsGodotArray()[Math.Clamp(index, 0, pieces["kinds"].AsGodotArray().Count - 1)].AsGodotDictionary();
        var bonus = table["material_stats"].AsGodotArray()[Math.Clamp(material, 0, 7)].AsGodotArray();
        var made = (GDictionary)a.Duplicate(true);
        made["id"] = index + 126;
        made["material"] = material;
        made["category"] = kind["category"];
        if (shields) { made["model"] = kind["model"]; made["gem_slots"] = kind["gem_slots"]; }
        made["strength"] = Int(kind, "strength") + bonus[0].AsInt32();
        made["intelligence"] = Int(kind, "intelligence") + bonus[1].AsInt32();
        made["agility"] = Int(kind, "agility") + bonus[2].AsInt32();
        int dp = (Int(a, "max_dp") + Int(b, "max_dp")) / 2;
        made["dp"] = dp;
        made["max_dp"] = dp;
        bool two = Int(a, "material") != Int(b, "material");
        var weights = table[two ? "two_materials" : "same_material"].AsGodotArray();
        made["classes"] = Mix(a, b, "classes", 6, table["class_positions"].AsGodotArray(), weights);
        made["affinities"] = Mix(a, b, "affinities", 7, table["affinity_positions"].AsGodotArray(), weights);
        made["types"] = MixTypes(a, b, table[two ? "types_two_materials" : "types_same_material"].AsGodotArray());
        if (shields)
        {
            int palette = table["palettes"].AsGodotArray()[Math.Clamp(material, 0, 7)].AsInt32();
            made["asset"] = Int(made, "model") > 0 && _content.Has($"weapon_{Int(made, "model"):X2}_{palette}") ? $"weapon_{Int(made, "model"):X2}_{palette}" : "";
        }
        Equipment.Remove(first);
        Equipment.Remove(second);
        Equipment.Add(new GDictionary { ["kind"] = first["kind"], ["record"] = made });
        return $"The {ItemName(Int(a, "id"))} and the {ItemName(Int(b, "id"))} made a {ItemName(index + 126)}.";
    }

    /// <summary>
    /// Mixes two pieces' type values (MENUC 0x8010b150): types 1 to 3 are the sum times a weight in tenths, the second
    /// when both pieces' strongest type is the same, clamped to 100 either way; type 0 is the first piece's.
    /// </summary>
    private static GArray MixTypes(GDictionary a, GDictionary b, GArray weights)
    {
        var x = a.GetValueOrDefault("types", new GArray()).AsGodotArray();
        var y = b.GetValueOrDefault("types", new GArray()).AsGodotArray();
        int At(GArray list, int i) => i < list.Count ? list[i].AsInt32() : 0;
        int topA = 1, topB = 1, bestA = -128, bestB = -128;
        for (int i = 1; i < 4; i++)
        {
            if (At(x, i) > bestA) { bestA = At(x, i); topA = i; }
            if (At(y, i) > bestB) { bestB = At(y, i); topB = i; }
        }
        int weight = weights[topA == topB ? 1 : 0].AsInt32();
        var mixed = new GArray { At(x, 0) };
        for (int i = 1; i < 4; i++) mixed.Add(Math.Clamp((At(x, i) + At(y, i)) * weight / 10, -100, 100));
        return mixed;
    }

    /// <summary>
    /// Mixes two blades' values (MENUC 0x8010ae38): a value either holds that is the pair's highest (or else lowest)
    /// stays; the rest are their sum, times the weight (tenths) for where each blade's own highest lies, clamped to
    /// 100 either way. The first <paramref name="count"/> are mixed; the rest are the first blade's.
    /// </summary>
    private static GArray Mix(GDictionary a, GDictionary b, string key, int count, GArray positions, GArray weights)
    {
        var x = a.GetValueOrDefault(key, new GArray()).AsGodotArray();
        var y = b.GetValueOrDefault(key, new GArray()).AsGodotArray();
        int At(GArray list, int i) => i < list.Count ? list[i].AsInt32() : 0;
        int highest = -128, lowest = 127, topA = 0, topB = 0, bestA = -128, bestB = -128;
        for (int i = 0; i < count; i++)
        {
            if (At(x, i) > bestA) { bestA = At(x, i); topA = i; }
            highest = Math.Max(highest, At(x, i)); lowest = Math.Min(lowest, At(x, i));
        }
        for (int i = 0; i < count; i++)
        {
            if (At(y, i) > bestB) { bestB = At(y, i); topB = i; }
            highest = Math.Max(highest, At(y, i)); lowest = Math.Min(lowest, At(y, i));
        }
        int weight = weights[positions[topA * count + topB].AsInt32()].AsInt32();
        var mixed = new GArray();
        for (int i = 0; i < Math.Max(x.Count, count); i++)
        {
            if (i >= count) { mixed.Add(At(x, i)); continue; }
            int p = At(x, i), q = At(y, i);
            if (p == highest || q == highest) mixed.Add(highest);
            else if (p == lowest || q == lowest) mixed.Add(lowest);
            else mixed.Add(Math.Clamp((p + q) * weight / 10, -100, 100));
        }
        return mixed;
    }

    /// <summary>The workshop's Rename: a weapon's name, which the save keeps (at most 18 letters; blank keeps it).</summary>
    public string Rename(GDictionary weapon, string name)
    {
        name = name.Trim();
        if (name.Length == 0) return "The name was kept.";
        if (name.Length > 18) name = name[..18];
        weapon["entry"].AsGodotDictionary()["record"].AsGodotDictionary()["name"] = name;
        if (EquippedWeapon is { } held && Holds(weapon)) held["name"] = name;
        return $"The weapon is now called {name}.";
    }

    /// <summary>The loose gems Ashley carries.</summary>
    public List<GDictionary> Gems()
    {
        var list = new List<GDictionary>();
        foreach (var value in Equipment) if (value.AsGodotDictionary()["kind"].AsString() == "gem") list.Add(value.AsGodotDictionary());
        return list;
    }

    /// <summary>The gems set in a weapon (its record's list, which a weapon gets when the first is attached).</summary>
    public static GArray GemsOf(GDictionary weapon) =>
        weapon["entry"].AsGodotDictionary()["record"].AsGodotDictionary().GetValueOrDefault("gems", new GArray()).AsGodotArray();

    /// <summary>
    /// A weapon's gem slots (its grip's byte 4), or a shield's (its record's byte 12); a shield comes as a holder with
    /// "shield" set and its record as the entry's.
    /// </summary>
    public static int GemSlots(GDictionary holder) => holder.ContainsKey("shield")
        ? holder["entry"].AsGodotDictionary()["record"].AsGodotDictionary().GetValueOrDefault("gem_slots", 0).AsInt32()
        : holder["grip"].AsGodotDictionary().GetValueOrDefault("gem_slots", 0).AsInt32();

    /// <summary>After a weapon's or shield's gems change, the one in use takes them up at once.</summary>
    private void Refit(GDictionary holder, bool inUse)
    {
        if (!inUse) return;
        if (!holder.ContainsKey("shield")) { Equip(holder); return; }
        if (Battle?.Ashley is { } ashley) FieldBattle.EquipArmour(ashley, -3, EquippedArmour[-3]);
    }

    private bool InUse(GDictionary holder) => holder.ContainsKey("shield")
        ? EquippedArmour.TryGetValue(-3, out var shield) && shield.RecursiveEqual(holder["entry"].AsGodotDictionary()["record"].AsGodotDictionary())
        : Holds(holder);

    /// <summary>Sets a loose gem in a weapon or shield, up to its gem slots; the one in use takes up its bonuses at once.</summary>
    public string Attach(GDictionary weapon, GDictionary gem)
    {
        var record = weapon["entry"].AsGodotDictionary()["record"].AsGodotDictionary();
        var gems = record.GetValueOrDefault("gems", new GArray()).AsGodotArray();
        int slots = GemSlots(weapon);
        string kind = weapon.ContainsKey("shield") ? "shield" : "weapon";
        if (gems.Count >= slots) return slots == 0 ? $"Gems cannot be attached to this {kind}." : $"This {kind} has no free gem slot.";
        bool inUse = InUse(weapon);
        gems.Add(gem["record"]);
        record["gems"] = gems;
        Equipment.Remove(gem);
        Refit(weapon, inUse);
        return $"The {ItemName(gem["record"].AsGodotDictionary()["id"].AsInt32())} was set in the {weapon["name"]}.";
    }

    /// <summary>The blades and grips (by kind and slot) the carried weapons are assembled from.</summary>
    private HashSet<(string, int)> SetParts()
    {
        var used = new HashSet<(string, int)>();
        foreach (var value in Equipment)
        {
            var entry = value.AsGodotDictionary();
            if (entry["kind"].AsString() != "weapons") continue;
            var record = entry["record"].AsGodotDictionary();
            used.Add(("blades", record["blade"].AsInt32()));
            used.Add(("grips", record["grip"].AsInt32()));
        }
        return used;
    }

    /// <summary>Whether a thing the item menu lists is part of a set: a blade or grip a carried weapon is assembled from (a set gem is kept in its weapon, not listed).</summary>
    public bool InSet(GDictionary entry) =>
        entry["kind"].AsString() is "blades" or "grips" && SetParts().Contains((entry["kind"].AsString(), entry["record"].AsGodotDictionary().GetValueOrDefault("slot", 0).AsInt32()));

    /// <summary>
    /// The badge a thing the item lists show beside its icon (MAINMENU's lists, SYSTEM.DAT's HUD page through the
    /// palette at (976, 223)): 2 "EQP" for what Ashley holds or wears (a weapon, its blade and grip, a shield or
    /// armour on), 1 "SET" for a blade or grip another carried weapon is assembled from, 0 none. A weapon's own is
    /// whether she <see cref="Holds"/> it.
    /// </summary>
    public int Badge(GDictionary entry)
    {
        string kind = entry["kind"].AsString();
        var record = entry["record"].AsGodotDictionary();
        if (kind is "shield" or "armour" or "accessory") return IsWorn(entry) ? 2 : 0;
        if (kind is not ("blades" or "grips")) return 0;
        int slot = record.GetValueOrDefault("slot", 0).AsInt32();
        foreach (var value in Equipment)
        {
            var weapon = value.AsGodotDictionary();
            if (weapon["kind"].AsString() != "weapons") continue;
            var assembled = weapon["record"].AsGodotDictionary();
            if (assembled[kind == "blades" ? "blade" : "grip"].AsInt32() != slot) continue;
            bool holds = !Unarmed && (EquippedWeapon is { } held
                ? held[kind == "blades" ? "blade" : "grip"].AsGodotDictionary().RecursiveEqual(record)
                : assembled.GetValueOrDefault("equipped", false).AsBool());
            return holds ? 2 : 1;
        }
        return 0;
    }

    /// <summary>Whether a thing the item menu lists is worn.</summary>
    public bool IsWorn(GDictionary entry)
    {
        var record = entry["record"].AsGodotDictionary();
        foreach (var worn in EquippedArmour.Values) if (worn == record || worn == entry || worn.RecursiveEqual(record)) return true;
        return false;
    }

    /// <summary>Takes the last gem set in a weapon or shield back out.</summary>
    public string Detach(GDictionary weapon)
    {
        var record = weapon["entry"].AsGodotDictionary()["record"].AsGodotDictionary();
        var gems = record.GetValueOrDefault("gems", new GArray()).AsGodotArray();
        if (gems.Count == 0) return $"No gems are attached to this {(weapon.ContainsKey("shield") ? "shield" : "weapon")}.";
        bool inUse = InUse(weapon);
        var gem = gems[^1].AsGodotDictionary();
        gems.RemoveAt(gems.Count - 1);
        record["gems"] = gems;
        Equipment.Add(new GDictionary { ["kind"] = "gem", ["record"] = gem });
        Refit(weapon, inUse);
        return $"The {ItemName(gem["id"].AsInt32())} was taken out.";
    }

    /// <summary>Fits a loose grip to a loose blade, making a weapon named for its blade.</summary>
    public string Assemble(GDictionary blade, GDictionary grip)
    {
        var bladeRecord = blade["record"].AsGodotDictionary();
        string name = ItemName(bladeRecord["id"].AsInt32());
        Equipment.Remove(blade);
        Equipment.Remove(grip);
        Equipment.Add(new GDictionary { ["kind"] = "weapon", ["record"] = new GDictionary { ["blade"] = bladeRecord, ["grip"] = grip["record"], ["name"] = name } });
        return $"The {name} was assembled.";
    }

    /// <summary>
    /// Takes a weapon apart into its blade and grip. The one in Ashley's hand is put away first (the menu asks), and
    /// she is left bare-handed.
    /// </summary>
    /// <summary>
    /// The workshop's Repair (MENU/MENUC.PRG): a weapon's blade, a shield or armour back to its most DP, at a cost of
    /// a hundredth of the DP restored in its PP, to no less than none.
    /// </summary>
    public string Repair(GDictionary record, string name)
    {
        int dp = record.GetValueOrDefault("dp", 0).AsInt32(), most = record.GetValueOrDefault("max_dp", 0).AsInt32();
        if (dp >= most) return $"The {name} needs no repair.";
        record["pp"] = Math.Max(0, record.GetValueOrDefault("pp", 0).AsInt32() - (most - dp) / 100);
        record["dp"] = most;
        if (Battle?.Ashley is { } ashley)
        {
            if (EquippedWeapon is { } held && held["blade"].AsGodotDictionary().RecursiveEqual(record))
                Battle.EquipWeapon(ashley, held["blade"].AsGodotDictionary(), held["grip"].AsGodotDictionary(), held.ContainsKey("entry") ? GemsOf(held) : null);
            foreach (var (limb, worn) in EquippedArmour)
                if (worn.RecursiveEqual(record)) FieldBattle.EquipArmour(ashley, limb, worn);
        }
        return $"The {name} was repaired.";
    }

    public string Disassemble(GDictionary weapon)
    {
        var entry = weapon["entry"].AsGodotDictionary();
        if (Holds(weapon)) Unequip();
        Equipment.Remove(entry);
        // An inventory weapon's parts stay where they are, now loose; one found whole leaves its parts behind.
        if (entry["kind"].AsString() == "weapon")
        {
            Equipment.Add(new GDictionary { ["kind"] = "blade", ["record"] = weapon["blade"] });
            Equipment.Add(new GDictionary { ["kind"] = "grip", ["record"] = weapon["grip"] });
        }
        return $"The {weapon["name"]} was taken apart.";
    }

    /// <summary>What the containers hold, shared by every container: equipment entries (weapons whole), and items by id.</summary>
    public readonly GArray Stored = new();
    public readonly Dictionary<int, int> StoredItems = new();

    private GDictionary StoredItemsRecord()
    {
        var record = new GDictionary();
        foreach (var (id, count) in StoredItems) record[id.ToString()] = count;
        return record;
    }

    /// <summary>
    /// The equipment Ashley could put in a container: not what she holds or wears, and not a carried weapon's own
    /// blade and grip, which go with it.
    /// </summary>
    public List<GDictionary> Storable()
    {
        var list = new List<GDictionary>();
        var parts = new HashSet<(string, int)>();
        foreach (var value in Equipment)
        {
            var entry = value.AsGodotDictionary();
            if (entry["kind"].AsString() != "weapons") continue;
            parts.Add(("blades", entry["record"].AsGodotDictionary()["blade"].AsInt32()));
            parts.Add(("grips", entry["record"].AsGodotDictionary()["grip"].AsInt32()));
        }
        foreach (var value in Equipment)
        {
            var entry = value.AsGodotDictionary();
            string kind = entry["kind"].AsString();
            var record = entry["record"].AsGodotDictionary();
            if (kind is "blades" or "grips" && parts.Contains((kind, record.GetValueOrDefault("slot", 0).AsInt32()))) continue;
            if (kind == "weapons" && EquippedWeapon == null && !Unarmed && record["equipped"].AsBool()) continue;
            if (kind == "weapon" && EquippedWeapon is { } held && held["blade"].AsGodotDictionary().RecursiveEqual(record["blade"].AsGodotDictionary())) continue;
            if (kind is "armour" or "shield" && System.Linq.Enumerable.Any(EquippedArmour.Values, worn => worn.RecursiveEqual(record))) continue;
            list.Add(entry);
        }
        return list;
    }

    /// <summary>An equipment entry's name: a weapon's own, otherwise its item's.</summary>
    public string EquipmentName(GDictionary entry)
    {
        var record = entry["record"].AsGodotDictionary();
        return entry["kind"].AsString() is "weapons" or "weapon" ? record["name"].AsString() : ItemName(record["id"].AsInt32());
    }

    /// <summary>Puts a piece of equipment in the container; an inventory weapon goes whole, its blade and grip with it.</summary>
    public void Store(GDictionary entry)
    {
        Equipment.Remove(entry);
        if (entry["kind"].AsString() != "weapons") { Stored.Add(entry); return; }
        var record = entry["record"].AsGodotDictionary();
        GDictionary? blade = null, grip = null;
        foreach (var value in Equipment.Duplicate())
        {
            var part = value.AsGodotDictionary();
            var partRecord = part["record"].AsGodotDictionary();
            if (part["kind"].AsString() == "blades" && partRecord["slot"].AsInt32() == record["blade"].AsInt32()) { blade = partRecord; Equipment.Remove(part); }
            if (part["kind"].AsString() == "grips" && partRecord["slot"].AsInt32() == record["grip"].AsInt32()) { grip = partRecord; Equipment.Remove(part); }
        }
        Stored.Add(new GDictionary { ["kind"] = "weapon", ["record"] = new GDictionary { ["blade"] = blade ?? new GDictionary(), ["grip"] = grip ?? new GDictionary(), ["name"] = record["name"] } });
    }

    /// <summary>Takes a piece of equipment back out of the container.</summary>
    public void Retrieve(GDictionary entry)
    {
        Stored.Remove(entry);
        Equipment.Add(entry);
    }

    /// <summary>Moves a whole stack of an item into the container (to true) or out of it.</summary>
    public void MoveItems(int id, bool store)
    {
        var (from, to) = store ? (Items, StoredItems) : (StoredItems, Items);
        if (!from.Remove(id, out int count)) return;
        to[id] = to.GetValueOrDefault(id) + count;
    }

    /// <summary>Whether Ashley's weapon takes both hands: great swords (3), great axes (5), staves (6), heavy maces (7), polearms (8) and crossbows (9).</summary>
    private bool TwoHanded => Battle?.Ashley is { } ashley && ashley.Category is 3 or 5 or 6 or 7 or 8 or 9;

    private ResultsScreen? _results;
    /// <summary>The CONGRATULATIONS screen, once a boss's defeat has opened it.</summary>
    public ResultsScreen? Results => _results;
    private ColorRect _dim = null!;
    /// <summary>How dark the field stands behind the CONGRATULATIONS screen (retail lowers its palettes; measured from footage).</summary>
    private const float ResultsDim = 0.65f;

    /// <summary>Darkens the field under the HUD and the windows over it, by <paramref name="amount"/> (0 none): the gadget bag's 0.55 leaves it at about 45% (measured from footage).</summary>
    public void DimField(float amount) => _dim.Color = new Color(0, 0, 0, amount);

    /// <summary>
    /// Opens the CONGRATULATIONS screen: the heal bonus joins the other score first (a hundred thousand less a hundred
    /// for each heal since the last), then the score, the map's percentage, the rank (no titles are kept yet) and the
    /// bonus reel by Ashley's stats with her elixirs' and bonuses' gains. False when it cannot open.
    /// </summary>
    private bool OpenResults()
    {
        if (Battle?.Ashley is not { } ashley || !_content.Has("results")) return false;
        if (_results == null)
        {
            _results = new ResultsScreen { Name = "Results", Sound = MenuSound, StopSound = id => StopSound($"effect_{id}") };
            _results.Configure(_content.ScreenTexture("results_sheet"), _content.ScreenTexture("results_reel"), _content.Data("results"),
                _content.Has("hud_labels") ? _content.ScreenTexture("hud_labels", true) : null,
                _content.Has("hud") ? _content.Data("hud").GetValueOrDefault("font", default).AsGodotDictionary() : null);
            Screen.AddChild(_results);
        }
        OtherScore = Math.Min(999999999L, OtherScore + Math.Max(0, 100000 - 100 * HealCount));
        HealCount = 0;
        long score = _results.Reckon(EnemyKills, WeaponAttacks, MaxChain, 0, StreakScore, OtherScore);
        _results.Open((int)score, MapPercent(), 0, ashley.Strength, ashley.Intelligence, ashley.Agility);
        GD.Print($"FIELD: CONGRATULATIONS: score {score}, map {MapPercent()}%");
        return true;
    }

    /// <summary>The CONGRATULATIONS bonus (a stat sprite, 13 STR, 14 AGL, 15 INT, 16 HP, 17 MP, and an amount), added to the stat and its gains for good, to at most 999.</summary>
    private void Gain((int Stat, int Amount) bonus)
    {
        if (Battle?.Ashley is not { } ashley || bonus.Amount <= 0) return;
        int amount = bonus.Amount;
        switch (bonus.Stat)
        {
            case 13: amount = Math.Min(amount, 999 - ashley.Strength); ashley.Strength += amount; Gains[0] += amount; break;
            case 15: amount = Math.Min(amount, 999 - ashley.Intelligence); ashley.Intelligence += amount; Gains[1] += amount; break;
            case 14: amount = Math.Min(amount, 999 - ashley.Agility); ashley.Agility += amount; Gains[2] += amount; break;
            case 16: amount = Math.Min(amount, 999 - ashley.MaxHp); ashley.MaxHp += amount; ashley.Hp += amount; Gains[3] += amount; break;
            case 17: amount = Math.Min(amount, 999 - ashley.MaxMp); ashley.MaxMp += amount; ashley.Mp += amount; Gains[4] += amount; break;
        }
        GD.Print($"FIELD: CONGRATULATIONS bonus: stat {bonus.Stat} +{amount}");
    }

    /// <summary>Lasting gains from elixirs and the CONGRATULATIONS bonus: strength, intelligence, agility, most hit points, most magic points.</summary>
    public readonly int[] Gains = new int[5];

    /// <summary>
    /// What the score is reckoned from (retail's score data, kept by the save): kills by enemy class, attacks by
    /// weapon kind (0 for kind 10, as retail files it), the highest chain, the kill streaks' score, the other score
    /// (the heal bonuses) and the heals since the last bonus.
    /// </summary>
    public readonly int[] EnemyKills = new int[6], WeaponAttacks = new int[10];
    /// <summary>
    /// The points toward reclaiming a battle ability, which the Battle Abilities page counts down to the next (retail's
    /// "kills.total", though no kill adds to it: a chain link in time adds one once the story has reached flag 0x82 =
    /// 10, a defense in time one at any time, each only up to the next requirement; BATTLE.PRG 0x8006FD1C), and how
    /// many abilities have been reclaimed so far (of 22).
    /// </summary>
    public int AbilityPoints, AbilitiesReclaimed;

    /// <summary>A link or a defense in time: one point towards the next reclaim, up to its requirement.</summary>
    public void CountAbilityPoint(bool chain)
    {
        if (chain && FlagValue(0x82) < 10) return;
        if (!_content.Has("battle_abilities")) return;
        var needed = _content.Data("battle_abilities")["requirements"].AsGodotArray();
        if (AbilitiesReclaimed < needed.Count && AbilityPoints < needed[AbilitiesReclaimed].AsInt32()) AbilityPoints++;
    }

    /// <summary>Whether enough points stand for the next reclaim (BATTLE.PRG 0x8006F760).</summary>
    public bool AbilityDue()
    {
        if (!_content.Has("battle_abilities")) return false;
        var needed = _content.Data("battle_abilities")["requirements"].AsGodotArray();
        return AbilitiesReclaimed < needed.Count && AbilityPoints >= needed[AbilitiesReclaimed].AsInt32();
    }

    /// <summary>The next Break Art of Ashley's weapon category whose kills stand, or -1 (BATTLE.PRG 0x8006F760).</summary>
    public int ArtDue()
    {
        if (Battle?.Ashley is not { } ashley || ashley.Category <= 0) return -1;
        int category = ashley.Category, row = category % 10, learned = ArtsLearned(category);
        if (learned >= 4) return -1;
        var thresholds = _content.Data("spells")["art_kills"].AsGodotArray()[row].AsGodotArray();
        return ArtKills[row] >= thresholds[learned].AsInt32() ? 184 + (category - 1) * 4 + learned : -1;
    }

    private int ArtsLearned(int category)
    {
        int learned = 0;
        while (learned < 4 && Arts.Contains(184 + (category - 1) * 4 + learned)) learned++;
        return learned;
    }
    public int MaxChain, HealCount;
    public long StreakScore, OtherScore;
    private int _streakClass = -1, _streakKills, _streakCategory = -1, _weaponStreak;

    /// <summary>An attack or Break Art of Ashley's at an enemy: its weapon kind's count rises.</summary>
    public void Attacked()
    {
        if (Battle?.Ashley is not { } ashley) return;
        int kind = ashley.Category == 10 ? 0 : Math.Clamp(ashley.Category, 0, 9);
        WeaponAttacks[kind] = Math.Min(0xFFFF, WeaponAttacks[kind] + 1);
    }

    /// <summary>
    /// An action of Ashley's past her attacks (a spell or an item, 55 and on) breaks her weapon's kill streak; a
    /// healing one (77, 96, 224 to 227, 232 to 235) counts as a heal against the heal bonus.
    /// </summary>
    public void Acted(int action)
    {
        if (action < 55) return;
        _weaponStreak = 0;
        if (action is 0x4D or 0x60 or >= 0xE0 and <= 0xE3 or >= 0xE8 and <= 0xEB) HealCount = Math.Min(1000, HealCount + 1);
    }

    /// <summary>
    /// An enemy Ashley felled: its class's kill count, the run of kills of one class, and the streak
    /// score, its most hit points with half again for each kill of the run and each kill of the weapon's run. An
    /// attack's kill (an action below 40) carries on the weapon's run while the blade's kind stays the same.
    /// </summary>
    private void RecordKill(FieldBattle.Combatant enemy, int action)
    {
        int kind = enemy.Class & 7;
        if (kind < 6) EnemyKills[kind] = Math.Min(0xFFFF, EnemyKills[kind] + 1);
        if (_streakClass != kind) { _streakClass = kind; _streakKills = 0; }
        else if (_streakKills < 100) _streakKills++;
        StreakScore = Math.Min(999999999L, StreakScore + enemy.MaxHp + enemy.MaxHp * _streakKills / 2 + enemy.MaxHp * _weaponStreak / 2);
        if (action >= 40 || Battle?.Ashley is not { } ashley) return;
        if (_streakCategory != ashley.Category) { _streakCategory = ashley.Category; _weaponStreak = 0; }
        else if (_weaponStreak < 100) _weaponStreak++;
    }

    /// <summary>The Break Arts Ashley knows (actions), and her kills by weapon category (category mod 10).</summary>
    public readonly List<int> Arts = new();
    public readonly int[] ArtKills = new int[10];

    /// <summary>Uses a Break Art from the menu; returns a message, or empty once it is under way.</summary>
    public string UseArt(int art) => Battle == null || _control == null ? "Not now." : Battle.Art(art, _control);

    /// <summary>
    /// A kill by Ashley's own hand counts towards the arts of her weapon's category, once the story has reached flag
    /// 0x82 = 10, up to the kills its next art takes (BATTLE.PRG 0x80085500); the art is reclaimed after her action.
    /// </summary>
    private void CountKill()
    {
        if (Battle?.Ashley is not { } ashley || FlagValue(0x82) < 10 || ashley.Category <= 0) return;
        int category = ashley.Category, row = category % 10, learned = ArtsLearned(category);
        if (learned >= 4) return;
        var thresholds = _content.Data("spells")["art_kills"].AsGodotArray()[row].AsGodotArray();
        if (ArtKills[row] < thresholds[learned].AsInt32()) ArtKills[row]++;
    }

    /// <summary>Set as Ashley's action ends, so that a reclaim due then opens (BATTLE.PRG 0x8006F89C).</summary>
    public bool ReclaimCheck { get => _reclaimCheck; set => _reclaimCheck = value; }
    private bool _reclaimCheck;

    /// <summary>
    /// After Ashley's action, once the field is quiet: a Break Art or battle ability due is reclaimed, effect 20 playing on
    /// her as the menu's reclaim opens (0x8006F848, MENU2.PRG's actionUnlock).
    /// </summary>
    private void CheckReclaim()
    {
        if (!_reclaimCheck || _menu.Open || Battle == null || Battle.Busy || !Controlled || Player is not { } ashley) return;
        _reclaimCheck = false;
        int art = ArtDue();
        bool ability = AbilityDue();
        if (art < 0 && !ability) return;
        PlayEffect(20, ashley, ashley);
        _menu.OpenReclaim(art, ability);
    }

    /// <summary>Reclaims a Break Art (once its message is up).</summary>
    public void ReclaimArt(int art) { if (!Arts.Contains(art)) Arts.Add(art); }

    /// <summary>Reclaims a battle ability: it joins her abilities and the buttons, and the count goes on to the next.</summary>
    public void ReclaimAbility(int id)
    {
        if (!Abilities.Contains(id)) Abilities.Add(id);
        AbilitiesReclaimed++;
        Battle?.Assign(Abilities);
    }

    /// <summary>Casts a spell from the menu; returns a message, or empty once the spell is under way.</summary>
    public string Cast(int spell, Action? confirmed = null)
    {
        if (Battle == null || _control == null || Player is not { } ashley) return "Not now.";
        var action = _content.Data("spells")["actions"].AsGodotDictionary().GetValueOrDefault(spell.ToString(), new GDictionary()).AsGodotDictionary();
        var effects = action.GetValueOrDefault("effects", new GArray()).AsGodotArray();
        // Effect 39 (Unlock) breaks the magic seal on the door or chest in front of Ashley.
        if (effects.Count > 0 && effects[0].AsGodotDictionary()["effect"].AsInt32() == 39)
        {
            string note = Unseal(ashley, action["cost"].AsInt32());
            if (note.Length == 0) confirmed?.Invoke();
            return note;
        }
        return Battle.Cast(spell, _control, confirmed);
    }

    /// <summary>Breaks a magic seal (lock 2) on the door or the chest facing Ashley, for the spell's magic points.</summary>
    private string Unseal(EventActor ashley, int cost)
    {
        if (Battle?.Ashley is not { } caster) return "Not now.";
        if (caster.Mp < cost) return "Not enough MP.";
        if (caster.Has(FieldBattle.Silence)) return "Ashley cannot cast while silenced.";
        var (x, z, quadrant) = Front(ashley);
        GDictionary? seal = null;
        foreach (var value in Document.GetValueOrDefault("locks", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var tile = record["tile"].AsGodotArray();
            if (tile[0].AsInt32() == x && tile[1].AsInt32() == z && record["lock"].AsInt32() == 2) seal = record;
        }
        foreach (var value in Document.GetValueOrDefault("chests", new GArray()).AsGodotArray())
        {
            var chest = value.AsGodotDictionary();
            var tile = chest["tile"].AsGodotArray();
            if (tile[0].AsInt32() == x && tile[1].AsInt32() == z && chest["lock"].AsInt32() == 2 && chest["facing"].AsInt32() / 1024 == quadrant) seal = chest;
        }
        if (seal == null) return "There is no magic seal here.";
        caster.Mp -= cost;
        // The seal is gone; whatever key the lock also names still holds it.
        seal["lock"] = seal["key"].AsInt32() > 0 ? 1 : 0;
        return "The magic seal was broken.";
    }
    public readonly GArray Equipment = new();
    private bool _inventoryLoaded;
    private int _messageFrames;
    /// <summary>The script's type-4 box shown in the system box, or -1.</summary>
    private int _scriptBox = -1;
    /// <summary>The save point Ashley stands on (its location id), or 0.</summary>
    public int SavePoint { get; private set; }

    /// <summary>Gives Ashley the New Game inventory, once.</summary>
    private void LoadInventory()
    {
        if (_inventoryLoaded) return;
        _inventoryLoaded = true;
        var inventory = _content.Data("inventory");
        foreach (var value in inventory["items"].AsGodotArray())
        {
            var item = value.AsGodotDictionary();
            Items[item["id"].AsInt32()] = Items.GetValueOrDefault(item["id"].AsInt32()) + item["count"].AsInt32();
        }
        foreach (string kind in new[] { "weapons", "blades", "grips", "armour" })
            foreach (var value in inventory[kind].AsGodotArray()) Equipment.Add(new GDictionary { ["kind"] = kind, ["record"] = value });
        // She starts wearing what the records say she wears: byte 38 is the limb (1 right arm, 2 left arm, 3 head, 4
        // body, 5 legs, 6 shield, 7 accessory; 0 carried).
        foreach (var value in Equipment)
        {
            var entry = value.AsGodotDictionary();
            if (entry["kind"].AsString() != "armour") continue;
            var record = entry["record"].AsGodotDictionary();
            int worn = record.GetValueOrDefault("limb", 0).AsInt32() switch { 1 => 0, 2 => 1, 3 => 2, 4 => 3, 5 => 4, 6 => -3, 7 => -1, _ => int.MinValue };
            if (worn != int.MinValue) EquippedArmour[worn] = record;
        }
    }

    /// <summary>An item's name, from the installed name table.</summary>
    public string ItemName(int id)
    {
        var names = _content.Data("item_names");
        return names.GetValueOrDefault("list", new GArray()).AsGodotArray() is { Count: > 0 } list && id < list.Count ? list[id].AsString() : $"item {id}";
    }

    /// <summary>
    /// Shows a field message in the system box for <paramref name="frames"/> (a second unless told otherwise): as a
    /// scene message (three lines, under CHECK, as 0x800CB2B8 opens it) unless told otherwise.
    /// </summary>
    public void Message(string text, int header = 3, int lines = 3, int frames = 60, bool centred = true)
    {
        _scriptBox = -1;
        SystemBox.Open(lines, header, centred);
        SystemBox.Text = text;
        _messageFrames = frames;
    }

    /// <summary>
    /// Retail's field message <paramref name="index"/> (BATTLE.PRG's table at 0x800EBDDC), filled in with
    /// <paramref name="strings"/>: 0 no target in range; for a chest 1 bound by magic, 2 locked with a key, 3 locked and
    /// 4 unlocked with a key; for a door 5 bound by magic, 6 locked with a key, 7 unlocked with it, 8 locked with a
    /// sigil, 9 unlocked with the sigil, which turns to dust, and 10 locked.
    /// </summary>
    public string RetailMessage(int index, params string[] strings)
    {
        var messages = _content.Has("battle_text") ? _content.Data("battle_text")["messages"].AsGodotArray() : new GArray();
        return index < messages.Count ? SystemBox.Fill(messages[index].AsGodotDictionary(), strings) : "";
    }

    /// <summary>
    /// Announces an action in the system box, as retail names it there: <paramref name="kind"/> is the menu table's
    /// entry (23 a spell, 24 a special attack, 25 a Break Art, 27 an item's effect, 29 a trap) and
    /// <paramref name="name"/> what it is.
    /// </summary>
    public const int AnnounceFrames = 87;

    public void Announce(int kind, string name)
    {
        var lines = _content.Has("battle_text") ? _content.Data("battle_text").GetValueOrDefault("announcements", new GArray()).AsGodotArray() : new GArray();
        if (kind - 23 is not (int index and >= 0) || index >= lines.Count) return;
        // Mastering a spell (30) is a scene message proper, under CHECK, held 120 frames once it is out.
        if (kind == 30) Message(SystemBox.Fill(lines[index].AsGodotDictionary(), new[] { name }), frames: 120);
        // An action's name is a scene message too (0x800CB2F4: held 120 ticks once typed out), on screen 2.9 s in the
        // footage (Ep. 2 29:52, `Spell: "Heal"`); the casting lasts as long.
        else Message(SystemBox.Fill(lines[index].AsGodotDictionary(), new[] { name }), header: 0, frames: AnnounceFrames);
    }

    /// <summary>
    /// While the attack sphere is open the room is drawn darker and bluer, and its characters as they are: retail's
    /// target mode leaves a lit floor at about a quarter of its red and green and half its blue.
    /// </summary>
    public void DimRoom(bool dim)
    {
        if (Field == null) return;
        var rigs = new HashSet<StandardMaterial3D>();
        foreach (var rig in Field.Rigs.Values) rigs.UnionWith(rig.Materials);
        var light = dim ? new Color(0.5f, 0.54f, 1.06f) : new Color(2, 2, 2);
        foreach (var material in Field.Materials)
            if (!rigs.Contains(material)) material.AlbedoColor = light with { A = material.AlbedoColor.A };
        // A palette transform's stand-in shaders take the colour from their materials.
        foreach (var palette in _palettes.Values)
            foreach (var (material, shader) in palette.Shaders) shader.SetShaderParameter("albedo", material.AlbedoColor);
    }

    /// <summary>The open container's location number (0x40 and above), which a save from it records.</summary>
    public int ContainerLocation { get; private set; } = 0x40;

    /// <summary>Opens a container's menu, as Cross at one does.</summary>
    public void OpenContainer(int location = 0x40)
    {
        ContainerLocation = location;
        _control?.Halt();
        FreezeActors(true);
        _menu.OpenContainer();
    }

    /// <summary>The memory card screens' stage, empty when they are not up.</summary>
    public string CardStage => _menu.CardStage;

    /// <summary>Whether the container is reading the memory cards, for tests.</summary>
    public bool ContainerAccessing => _menu.ContainerAccessing;

    /// <summary>What the container's screen says under CAUTION, for tests.</summary>
    public string ContainerCaution => _menu.ContainerCaution;

    /// <summary>The item menu's help as shown, and whether it is under CAUTION, for tests.</summary>
    public (string Text, bool Caution) ItemHelpShown => _menu.ItemHelpShown;

    /// <summary>The item menu's list rows as shown, for tests.</summary>
    public IReadOnlyList<ItemMenu.Row> ItemRows => _menu.ItemRows;

    /// <summary>Whether the field menu (or a window it shows, such as the gadget bag) is open.</summary>
    public bool MenuOpen => _menu.Open;

    /// <summary>The field menu's chosen command row, for the checks.</summary>
    public int MenuRow => _menu.Row;

    /// <summary>STATUS's equipment list or panels while up, and the chosen slot, for the checks.</summary>
    public (int Stage, int Slot) StatusEquipment => _menu.StatusEquipment;

    /// <summary>The map screen's chosen room's name while it is up, for the checks.</summary>
    public string MapRoomName => _menu.MapRoomName;

    /// <summary>The field menu's open page (a command row), or -1 on the command list, for the checks.</summary>
    public int MenuPage => _menu.Page;

    /// <summary>The field's system box, where its messages and the attack readout print.</summary>
    public SystemBox SystemBox { get; private set; } = null!;

    private void StepMessage()
    {
        if (_messageFrames > 0 && --_messageFrames == 0) SystemBox.Text = "";
    }

    /// <summary>Whether the HUD shows (58): scenes hide it and bring it back, and the setting carries from room to room.</summary>
    /// <summary>Whether the HUD is drawn (opcode 58). A new game starts with it off, as the field engine clears it; the opening scene turns it on.</summary>
    public bool HudShown { get; private set; }

    /// <summary>
    /// The puzzle timer (opcode 5a): 0 stopped, 1 counting up, 2 counting down. It counts in sixtieths of a second,
    /// two a frame of play, then seconds, minutes and hours, and writes them into flags 0xa0 (as hundredths) to
    /// 0xa3 for scripts to test. A count down that reaches zero tests the room's conditions of type 0x10, once.
    /// </summary>
    private int _heldTimer, _timer, _timerTicks, _timerSeconds, _timerMinutes, _timerHours;

    private void StepTimer()
    {
        if (_timer == 0) return;
        if (_timer == 1)
        {
            if ((_timerTicks += 2) >= 60) { _timerTicks -= 60; if (++_timerSeconds >= 60) { _timerSeconds = 0; if (++_timerMinutes >= 60) { _timerMinutes = 0; _timerHours = Math.Min(127, _timerHours + 1); } } }
        }
        else if ((_timerTicks -= 2) < 0)
        {
            _timerTicks += 60;
            if (--_timerSeconds < 0) { _timerSeconds = 59; if (--_timerMinutes < 0) { _timerMinutes = 59; if (--_timerHours < 0) (_timerHours, _timerMinutes, _timerSeconds, _timerTicks) = (0, 0, 0, 0); } }
        }
        SetFlag(0xA0, _timerTicks * 5 / 3);
        SetFlag(0xA1, _timerSeconds);
        SetFlag(0xA2, _timerMinutes);
        SetFlag(0xA3, _timerHours);
        ShowTimer();
        if (_timer == 2 && _timerTicks == 0 && _timerSeconds == 0 && _timerMinutes == 0 && _timerHours == 0)
        {
            _timer = 0;
            CheckConditions(0x10);
        }
    }

    private void ShowTimer()
    {
        // Count-ups time boss battles for the Score's records and are not shown; count-downs are puzzles against the clock.
        if (Battle?.Hud is not { } hud) return;
        hud.Timer = _timer != 2 ? null : _timerHours > 0 ? (59, 59, 99) : (_timerMinutes, _timerSeconds, _timerTicks * 5 / 3);
    }

    /// <summary>
    /// Free-look, the first-person view: Ashley stands still and out of sight while the camera looks out from her eyes, 160 units
    /// above her feet. The stick turns the view (32 units a frame across, 24 up and down, up to 0x300 either way),
    /// and the room's first-person parts are drawn while it lasts.
    /// </summary>
    private bool _lookAround;
    private float _lookYaw, _lookPitch;
    private const float EyeHeight = 160;

    /// <summary>Whether the view is Ashley's own.</summary>
    public bool LookingAround => _lookAround;

    private void LookAround(EventActor ashley, bool on)
    {
        _lookAround = on;
        _hideFirstPerson = on;
        ashley.Visible = !on;
        // Facing 0 is towards -z and a quarter turn is +x (as the tile in front reads it); the view starts that way.
        _lookYaw = Mathf.PosMod(-ashley.Facing, 4096);
        _lookPitch = 0;
    }


    /// <summary>The tile in front of Ashley: her facing's quadrant is 0 towards -z, 1 -x, 2 +z and 3 +x.</summary>
    private (int X, int Z, int Quadrant) Front(EventActor ashley)
    {
        int quadrant = (((int)ashley.Facing + 0x200) & 0xFFF) >> 10;
        int x = (int)Mathf.Floor(ashley.Position.X / 128), z = (int)Mathf.Floor(ashley.Position.Z / 128);
        return quadrant switch { 0 => (x, z - 1, 0), 1 => (x - 1, z, 1), 2 => (x, z + 1, 2), _ => (x + 1, z, 3) };
    }

    /// <summary>
    /// Cross acts on the tile in front of Ashley: a locked door there, then a chest facing her. A key or sigil
    /// unlocks (setting flag 0x1b6 + its id; a sigil is used up); a chest opens to give its loot, and its opening
    /// sets flag 0x3c0 + its index.
    /// </summary>
    public void Interact(EventActor ashley)
    {
        var (x, z, quadrant) = Front(ashley);
        if (MoveCrate(ashley, (x, z, quadrant))) return;
        foreach (var value in Document.GetValueOrDefault("locks", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var tile = record["tile"].AsGodotArray();
            if (tile[0].AsInt32() != x || tile[1].AsInt32() != z || record["lock"].AsInt32() == 0) continue;
            if (!Unlock(record, true, out string message)) { Message(message); return; }
            if (message.Length > 0) Message(message);
            record["lock"] = 0;
            // The door's geometry group opens and its tile no longer blocks.
            _hiddenGroups.Add(record["group"].AsInt32());
            int width = _columns / 4, index = z * width + x;
            if (index >= 0 && index < _attributes.Length) _attributes[index] &= ~(7L << 10);
            return;
        }
        foreach (var value in Document.GetValueOrDefault("chests", new GArray()).AsGodotArray())
        {
            var chest = value.AsGodotDictionary();
            var tile = chest["tile"].AsGodotArray();
            int index = chest["index"].AsInt32();
            if (tile[0].AsInt32() != x || tile[1].AsInt32() != z || index < 0x40 && FlagValue(0x3C0 + index) != 0) continue;
            if (chest["facing"].AsInt32() / 1024 != quadrant) continue;
            // A container (index 0x40 and above) opens on what is stored in every container; its lid opens first.
            if (index >= 0x40) { AfterLid(chest, () => OpenContainer(index)); return; }
            if (chest["lock"].AsInt32() != 0 && !Unlock(chest, false, out string message)) { Message(message); return; }
            chest["lock"] = 0;
            // The chest opens on the gadget bag, which takes what is chosen and leaves the rest in it.
            if (ChestContents(chest).Count == 0) { SetFlag(0x3C0 + index, 1); OpenLid(chest, false); return; }
            AfterLid(chest, () => { FreezeActors(true); _menu.OpenChest(chest); });
            return;
        }
    }

    /// <summary>Unlocks with the key or sigil a lock names, when Ashley has it or has used it there before.</summary>
    private bool Unlock(GDictionary record, bool door, out string message)
    {
        int lockType = record["lock"].AsInt32(), key = record["key"].AsInt32();
        bool sigil = key >= 466;
        if (lockType == 2) { message = RetailMessage(door ? 5 : 1); return false; }
        if (key > 0 && FlagValue(0x1B6 + key) != 0) { message = ""; return true; }
        if (key > 0 && Items.GetValueOrDefault(key) > 0)
        {
            SetFlag(0x1B6 + key, sigil ? 2 : 1);
            if (sigil && --Items[key] == 0) Items.Remove(key);
            message = RetailMessage(door ? sigil ? 9 : 7 : 4, ItemName(key));
            return true;
        }
        message = key > 0 ? RetailMessage(door ? sigil ? 8 : 6 : 2, ItemName(key)) : RetailMessage(door ? 10 : 3);
        return false;
    }

    /// <summary>Takes a chest's loot: its items and equipment; returns the message listing them.</summary>
    /// <summary>
    /// A chest's contents as the gadget bag lists them, made once from its loot and kept on the chest so that what is
    /// left stays in it: equipment first (a weapon, blade, grip, shield, gem, then armour), then items with counts.
    /// </summary>
    public GArray ChestContents(GDictionary chest)
    {
        if (chest.TryGetValue("contents", out var kept)) return kept.AsGodotArray();
        var loot = chest["loot"].AsGodotDictionary();
        var contents = new GArray();
        foreach (string kind in new[] { "weapon", "blade", "grip", "shield", "gem" })
            if (loot.TryGetValue(kind, out var record)) contents.Add(new GDictionary { ["kind"] = kind, ["record"] = record });
        foreach (var value in loot.GetValueOrDefault("armour", new GArray()).AsGodotArray()) contents.Add(new GDictionary { ["kind"] = "armour", ["record"] = value });
        foreach (var value in loot.GetValueOrDefault("items", new GArray()).AsGodotArray())
        {
            var item = value.AsGodotDictionary();
            contents.Add(new GDictionary { ["kind"] = "item", ["id"] = item["id"], ["count"] = item["count"] });
        }
        chest["contents"] = contents;
        return contents;
    }

    // The bag's categories: WEAPON, BLADE, GRIP, SHIELD, ARMOR, GEM, MISC.
    public static int BagCategory(string kind) => kind switch { "weapon" or "weapons" => 0, "blade" or "blades" => 1, "grip" or "grips" => 2, "shield" => 3, "armour" => 4, "gem" => 5, _ => 6 };

    /// <summary>What the bag carries in each of its categories.</summary>
    public int[] BagCounts()
    {
        var counts = new int[7];
        foreach (var value in Equipment) counts[BagCategory(value.AsGodotDictionary()["kind"].AsString() switch { "weapons" => "weapon", "blades" => "blade", "grips" => "grip", var k => k })]++;
        foreach (var (_, count) in Items) if (count > 0) counts[6]++;
        return counts;
    }

    /// <summary>A chest entry as the gadget bag shows it: its name, count, category, icon and description.</summary>
    public GadgetBag.Entry BagEntry(GDictionary entry)
    {
        string kind = entry["kind"].AsString();
        var help = _content.Has("item_help") ? _content.Data("item_help")["list"].AsGodotArray() : new GArray();
        string Help(int index) => index >= 0 && index < help.Count ? SystemBox.Fill(help[index].AsGodotDictionary()) : "";
        // ITEMHELP.BIN keeps descriptions by kind, not by item id: blades from entry 0 (id 1), grips from 90 (id 96),
        // gems from 121 (id 261) and the other items from 183 (id 323), each id less 140, and accessories from 610
        // (id 223). Shields and body armour have none; their class line names them.
        if (kind == "item")
        {
            int id = entry["id"].AsInt32();
            return new GadgetBag.Entry(ItemName(id), entry["count"].AsInt32(), 6, -1, Help(id - 140));
        }
        var record = entry["record"].AsGodotDictionary();
        string name = record.ContainsKey("name") ? record["name"].AsString() : ItemName(record.GetValueOrDefault("id", record.GetValueOrDefault("blade", new GDictionary()).AsGodotDictionary().GetValueOrDefault("id", 0)).AsInt32());
        int category = record.GetValueOrDefault("category", record.GetValueOrDefault("blade", new GDictionary()).AsGodotDictionary().GetValueOrDefault("category", 0)).AsInt32();
        // The label page's icon cells (16 a row from y 98): weapons by blade category from cell 4, a gem cell 2, a
        // grip cell 3; shields and armour on the second row from cell 20 by their kind (shield, helm, body, legs,
        // arms), an accessory cell 26. These were matched by eye.
        int icon = kind switch
        {
            "weapon" or "blade" => category is >= 1 and <= 12 ? 3 + category : -1,
            "grip" => 3,
            "gem" => 2,
            "shield" => 20,
            "armour" => category is >= 1 and <= 5 ? 19 + category : 26,
            _ => -1,
        };
        // "Class: Weapon" or "Class: Armor" (item help 651, with 383 and 392). A weapon's or blade's second line is help
        // 652: its material, category, damage type and hands ("Bronze Sword (Edged/One-Handed)"); anything else's is
        // its name.
        string word = Help(kind is "armour" or "shield" ? 392 : 383);
        string second = "\n" + name;
        var blade = kind == "weapon" ? record.GetValueOrDefault("blade", new GDictionary()).AsGodotDictionary() : kind == "blade" ? record : null;
        if (blade != null && blade.Count > 0 && help.Count > 652)
        {
            // The materials are named in help 441 ("Sorts items by material: Wood/Leather/Bronze/..."), 1 for Wood.
            string list = Help(440 + 1);
            var materials = list[(list.IndexOf(':') + 1)..].Replace("\n", "").Trim().Split('/');
            int material = blade.GetValueOrDefault("material", 0).AsInt32(), type = blade.GetValueOrDefault("damage_type", 0).AsInt32();
            bool twoHanded = category is 3 or 5 or 6 or 7 or 8 or 9;
            var slots = new string[10];
            slots[9] = material >= 1 && material <= materials.Length ? materials[material - 1].Trim() : "";
            slots[8] = category is >= 1 and <= 10 ? Help(398 + category) : "";
            slots[7] = type is >= 1 and <= 3 ? Help(408 + type) : "";
            slots[6] = Help(twoHanded ? 413 : 412);
            second = SystemBox.Fill(help[652].AsGodotDictionary(), slots);
        }
        string classLine = help.Count > 651 ? SystemBox.Fill(help[651].AsGodotDictionary(), new[] { word, second }) : word + second;
        int itemId = record.GetValueOrDefault("id", 0).AsInt32();
        // A grip's description, then what it takes (641 to 644 by its category: daggers to great swords, axes to
        // staves, polearms, crossbows); a gem's; an accessory's.
        if (kind == "grip") classLine = Help(itemId - 6) + (category is >= 1 and <= 4 ? "\n" + Help(640 + category) : "");
        else if (kind == "gem") classLine = Help(itemId - 140);
        else if (kind == "armour" && itemId is >= 223 and <= 253) classLine = Help(itemId + 387);
        // The material's letter: a weapon's blade's, a blade's, a shield's or armour's (an accessory has none).
        int materialLetter = kind switch
        {
            "weapon" or "blade" => blade?.GetValueOrDefault("material", 0).AsInt32() ?? 0,
            "shield" => record.GetValueOrDefault("material", 0).AsInt32(),
            "armour" when itemId is not (>= 223 and <= 253) => record.GetValueOrDefault("material", 0).AsInt32(),
            _ => 0,
        };
        return new GadgetBag.Entry(name, 0, BagCategory(kind), icon, classLine, materialLetter);
    }

    /// <summary>Takes a chest entry into the bag; the chest is marked opened once it is empty.</summary>
    public void TakeFromChest(GDictionary chest, int index)
    {
        var contents = ChestContents(chest);
        if (index < 0 || index >= contents.Count) return;
        var entry = contents[index].AsGodotDictionary();
        string kind = entry["kind"].AsString();
        if (kind == "item") Items[entry["id"].AsInt32()] = Items.GetValueOrDefault(entry["id"].AsInt32()) + entry["count"].AsInt32();
        else Equipment.Add(new GDictionary { ["kind"] = kind, ["record"] = entry["record"] });
        contents.RemoveAt(index);
        if (contents.Count == 0 && chest["index"].AsInt32() >= 0) SetFlag(0x3C0 + chest["index"].AsInt32(), 1);
    }

    /// <summary>
    /// A trap tile: the room's trap record there takes effect. A save circle (type 16) is a save point while Ashley
    /// stands on it; any other type is the trap's action (<see cref="FieldBattle.Trap"/>).
    /// </summary>
    public void StepOnTrap((int X, int Z) tile)
    {
        foreach (var value in Document.GetValueOrDefault("traps", new GArray()).AsGodotArray())
        {
            var trap = value.AsGodotDictionary();
            var at = trap["tile"].AsGodotArray();
            if (at[0].AsInt32() != tile.X || at[1].AsInt32() != tile.Z) continue;
            int type = trap["type"].AsInt32();
            if (type == 16) { SavePoint = trap["location"].AsInt32(); return; }
            if (Battle?.Ashley == null) return;
            // The trap is its action (type + 6), announced by name as retail's information setting does it.
            var traps = _content.Has("trap_actions") ? _content.Data("trap_actions") : new GDictionary();
            if (traps.GetValueOrDefault(type.ToString(), new GDictionary()).AsGodotDictionary() is not { Count: > 0 } action) return;
            Announce(22 + action["kind"].AsInt32(), action["name"].AsString());
            Battle.Trap(action, Document.GetValueOrDefault("trap_level", 0).AsInt32());
            return;
        }
    }

    /// <summary>A pit costs Ashley 20 hit points and returns her to the door she came in by; with 20 or fewer, she dies.</summary>
    private void FallInPit()
    {
        if (Battle?.Ashley is not { } ashley || Player is not { } player) return;
        // Retail has no message for it: the cost shows as a damage number, as any hit does.
        Battle.Strike(ashley, Math.Min(20, ashley.Hp));
        if (ashley.Dead) return;
        player.Position = new Vector3(_arrival.X * 128 + 64, 0, _arrival.Z * 128 + 64);
        player.Position = player.Position with { Y = FloorHeight(player.Position) };
        player.Facing = _arrival.Facing;
    }

    /// <summary>A collision tile's attribute word, or 0 outside the room.</summary>
    private long TileAttributes(int x, int z)
    {
        int width = _columns / 4, depth = _rows / 4;
        if (x < 0 || z < 0 || x >= width || z >= depth || z * width + x >= _attributes.Length) return 0;
        return _attributes[z * width + x];
    }

    /// <summary>
    /// Takes the first door on Ashley's tile whose flag test passes (a door needing a flag value skips while the flag
    /// is below it) and starts leaving by it: the screen fades out at 8 levels a frame, and she walks on if the door
    /// asks her to.
    /// </summary>
    private void EnterDoor((int X, int Z) tile)
    {
        foreach (var value in Document.GetValueOrDefault("doors", new GArray()).AsGodotArray())
        {
            var door = value.AsGodotDictionary();
            var at = door["tile"].AsGodotArray();
            if (at[0].AsInt32() != tile.X || at[1].AsInt32() != tile.Z) continue;
            if (Flags[door["flag"].AsInt32()] < door["flag_value"].AsInt32()) continue;
            _door = door;
            Controlled = false;
            // The fade an event set for the next room change (6c), else black at 8 levels a frame; 3 is none.
            var (effect, speed) = _transitionFade;
            _transitionFade = (0, 8);
            _fadeSpeed = effect == 3 ? 1 : Math.Max(1, speed) / 255f;
            _fadeWhite = effect == 1;
            return;
        }
    }

    /// <summary>
    /// While the screen fades out Ashley keeps walking on (at half her run) if the door says so; once it is black the
    /// destination room loads with her on its arrival tile, and the screen fades back in.
    /// </summary>
    private void StepDoor()
    {
        var door = _door!;
        if (door["walk_in"].AsBool() && _control != null && Player is { } walker) _control.Step(walker.Facing, true);
        if (_fade < 1) return;
        _door = null;
        int zone = door["zone"].AsInt32(), map = door["map"].AsInt32();
        var destination = door["destination"].AsGodotArray();
        _arrival = (destination[0].AsInt32(), destination[1].AsInt32(), door["facing"].AsSingle());
        _arrivalHalf = door["half"].AsBool();
        _walkIn = door["walk_in"].AsBool();
        // The door's marker on this room's area map names the marker of the way in on the next room's (its byte 1).
        int entered = -1;
        if (AreaRoom(Zone, Map) is { } here && door.GetValueOrDefault("marker", -1).AsInt32() is >= 0 and var marker)
        {
            var markers = here["markers"].AsGodotArray();
            if (marker < markers.Count) entered = markers[marker].AsGodotArray()[1].AsInt32();
        }
        LoadRoom(zone == 0 ? Zone : zone, map);
        EnteredMarker = entered;
        // The arriving room's own event may take the screen over; otherwise it fades back in.
        _fadeSpeed = -8 / 255f;
    }

    /// <summary>
    /// Tests the room's event conditions of a trigger type while the player has control, and starts the first
    /// event whose tests all hold: one of the room's own, or an event file. Type 0x20 is tested every frame of play,
    /// 0x08 when an enemy falls, 0x10 when the puzzle timer runs out.
    /// </summary>
    private void CheckConditions(int type)
    {
        if (!Controlled || _pendingChain.Length != 0 || Battle?.GameOver == true) return;
        foreach (var value in Document.GetValueOrDefault("conditions", new GArray()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            if ((record["types"].AsInt32() & type) == 0) continue;
            bool holds = true;
            foreach (var test in record["tests"].AsGodotArray())
            {
                var entry = test.AsGodotDictionary();
                if (Condition(entry["op"].AsInt32(), entry["bytes"].AsGodotArray()) == entry["negate"].AsBool()) { holds = false; break; }
            }
            if (!holds) continue;
            int id = record["event"].AsInt32();
            Play(record["local"].AsBool() ? $"event_z{Zone:d3}_m{Map:d2}_l{id}" : $"event_evt{id:d4}");
            return;
        }
    }

    /// <summary>
    /// One retail condition test on its bytes after the opcode:
    /// <list type="bullet">
    /// <item>1 Ashley on a tile, 2 within a tile rectangle;</item>
    /// <item>3 a flag against a value and 15 a flag against a flag, compared by the high nibble (≤, ≥, ==, !=, &lt;, &gt;);</item>
    /// <item>4 an actor present;</item>
    /// <item>5 any living characters of a class (4 is hostile), and 7 an actor's hit-point percentage, either
    /// optionally stored in a flag (which then holds);</item>
    /// <item>9 Ashley standing still;</item>
    /// <item>16 an actor marked by its room record.</item>
    /// </list>
    /// Other tests do not hold yet.
    /// </summary>
    private bool Condition(int op, GArray bytes)
    {
        int B(int i) => bytes[i].AsInt32();
        int U16(int i) => B(i) | B(i + 1) << 8;
        int FlagAt(int index) => Flags[index];
        string Reference(int value) => (value & 0x1000) != 0 ? "slot:" + (value & 0x1F) : FindById(value) ?? "slot:1";
        var ashleyTile = Player is { } a ? ((int)Mathf.Floor(a.Position.X / 128), (int)Mathf.Floor(a.Position.Z / 128)) : (-1, -1);
        bool Compare(int left, int right, int how) => (how & 0xF0) switch
        {
            0x00 => left <= right, 0x10 => left >= right, 0x20 => left == right, 0x30 => left != right, 0x40 => left < right, 0x50 => left > right, _ => false,
        };
        void Store(int flag, int value) => Flags[flag] = value & 0xFF;
        switch (op)
        {
            case 1: return ashleyTile == (B(0), B(1));
            case 2: return ashleyTile.Item1 >= B(0) && ashleyTile.Item1 < B(0) + B(2) && ashleyTile.Item2 >= B(1) && ashleyTile.Item2 < B(1) + B(3);
            case 3: return Compare(FlagAt(((B(0) & 0xF) << 6) | (B(1) >> 2)), B(2), B(0));
            case 15: return Compare(FlagAt(((B(0) & 0xF) << 6) | (B(1) >> 2)), FlagAt(((B(1) & 3) << 8) | B(2)), B(0));
            case 4: return Actors.ContainsKey(Reference(U16(0))) && Actors[Reference(U16(0))].Visible;
            case 5:
            {
                int count = B(0) == 4 ? Battle?.HostileCount() ?? 0 : 0, flag = U16(1);
                if (flag != 0) { Store(flag, count); return true; }
                return count != 0;
            }
            case 7:
            {
                string actor = Reference(U16(0));
                if (!Actors.ContainsKey(actor) || Battle == null || !Battle.Combatants.ContainsKey(actor)) return false;
                int percent = Battle.HitPointPercent(actor), flag = U16(2);
                if (flag != 0) { Store(flag, percent); return true; }
                return percent != 0;
            }
            case 9: return _control != null && !_control.Moving && Battle is not { Busy: true };
            case 16: return Battle != null && Battle.Combatants.TryGetValue(Reference(U16(0)), out var marked) && marked.Marked;
            default: return false;
        }
    }

    /// <summary>
    /// Places a box anchored to an actor (13) on the 320 by 240 screen: centred above the actor's head, or below it
    /// where there is no room above, kept 4 pixels inside the screen.
    /// </summary>
    private void Anchor(GDictionary geometry)
    {
        if (Actor(geometry, "anchor") is not { } speaker) return;
        var head = EventActor.RetailSpace * (speaker.Position + new Vector3(0, -210, 0));
        var feet = EventActor.RetailSpace * speaker.Position;
        // Into the 320 by 240 screen, which stands centred in the window at the window's height (or width).
        var size = GetViewport().GetVisibleRect().Size;
        var scale = Unit(size);
        var origin = (size - new Vector2(320, VisibleLines) * scale) / 2;
        Vector2 Screen(Vector3 point) => (Field.Camera.UnprojectPosition(point) - origin) / scale;
        int width = geometry["width"].AsInt32(), height = geometry["height"].AsInt32();
        var top = Screen(head);
        float x = top.X - width / 2f, y = top.Y - height - 8;
        if (y < 4) y = Screen(feet).Y + 8;
        geometry["x"] = (int)Math.Clamp(x, 4, 320 - width - 4);
        geometry["y"] = (int)Math.Clamp(y, 4, 236 - height);
    }

    /// <summary>A message with the numbers its tokens name written in, the later tokens moved along to match.</summary>
    private GDictionary WithNumbers(GDictionary message)
    {
        var tokens = message.GetValueOrDefault("tokens", new GArray()).AsGodotArray();
        bool any = false;
        foreach (var token in tokens) any |= token.AsGodotDictionary()["type"].AsString() == "number";
        if (!any) return message;
        string text = message["text"].AsString();
        var kept = new GArray();
        int shift = 0;
        foreach (var value in tokens)
        {
            var token = value.AsGodotDictionary().Duplicate();
            int offset = token["offset"].AsInt32() + shift;
            if (token["type"].AsString() == "number")
            {
                int number = _integers[token["slot"].AsInt32()];
                string written = token["hex"].AsBool() ? number.ToString("X") : number.ToString();
                text = text.Insert(Math.Min(offset, text.Length), written);
                shift += written.Length;
                continue;
            }
            token["offset"] = offset;
            kept.Add(token);
        }
        return new GDictionary { ["text"] = text, ["tokens"] = kept };
    }

    /// <summary>A retail state flag, as the view holds it.</summary>
    private int FlagValue(int index) => Flags[index];

    /// <summary>Sets a state flag (an unsigned byte), which the running event tests next.</summary>
    private void SetFlag(int index, int value) => Flags[index] = value & 0xFF;

    /// <summary>A choice among text boxes (1c): its command, the boxes from the top, and the cursor.</summary>
    private (PresentationReceipt Id, List<int> Boxes, int Cursor)? _choice;

    /// <summary>
    /// Starts a choice among the boxes in <paramref name="mask"/>, ordered from the top of the screen; the cursor
    /// starts on the place flag 17 names (from 1), or the first.
    /// </summary>
    private void BeginChoice(PresentationReceipt id, int mask)
    {
        var boxes = new List<int>();
        for (int box = 0; box < 8; box++) if ((mask >> box & 1) != 0 && Field.Dialogues.ContainsKey("box" + box)) boxes.Add(box);
        if (boxes.Count == 0) { _completions.Add(id); return; }
        boxes.Sort((a, b) => _boxes.GetValueOrDefault(a, new GDictionary()).GetValueOrDefault("y", 0).AsInt32().CompareTo(_boxes.GetValueOrDefault(b, new GDictionary()).GetValueOrDefault("y", 0).AsInt32()));
        int cursor = FlagValue(17) - 1;
        _choice = (id, boxes, cursor >= 0 && cursor < boxes.Count ? cursor : 0);
        ShowChoice();
    }

    /// <summary>Up and down move the cursor round the boxes; any face or shoulder button takes the one under it.</summary>
    private void StepChoice()
    {
        var (id, boxes, cursor) = _choice!.Value;
        if (Input.IsActionJustPressed("ps_up")) cursor = (cursor + boxes.Count - 1) % boxes.Count;
        if (Input.IsActionJustPressed("ps_down")) cursor = (cursor + 1) % boxes.Count;
        _choice = (id, boxes, cursor);
        ShowChoice();
        foreach (string button in new[] { "ps_circle", "ps_cross", "ps_triangle", "ps_square", "ps_l1", "ps_r1", "ps_l2", "ps_r2" })
            if (Input.IsActionJustPressed(button))
            {
                SetFlag(16, cursor + 1);
                foreach (int box in boxes)
                {
                    if (Field.Dialogues.TryGetValue("box" + box, out var open)) open.Panel.Modulate = Colors.White;
                    if (_balloons.TryGetValue(box, out var balloon)) balloon.Modulate = Colors.White;
                }
                _choice = null;
                _completions.Add(id);
                return;
            }
    }

    /// <summary>
    /// The box under the cursor is drawn at full brightness and over the others, which are at half, balloon and text
    /// alike (footage: the lever's "Pull lever" and "Leave it" swap places in front as the cursor moves).
    /// </summary>
    private void ShowChoice()
    {
        var (_, boxes, cursor) = _choice!.Value;
        for (int i = 0; i < boxes.Count; i++)
        {
            var shade = i == cursor ? Colors.White : new Color(0.5f, 0.5f, 0.5f);
            _balloons.TryGetValue(boxes[i], out var balloon);
            if (balloon != null) balloon.Modulate = shade;
            if (!Field.Dialogues.TryGetValue("box" + boxes[i], out var open)) continue;
            var panel = open.Panel;
            panel.Modulate = shade;
            if (i != cursor) continue;
            if (balloon != null) Field.DialogueLayer.MoveChild(balloon, -1);
            Field.DialogueLayer.MoveChild(panel, -1);
        }
    }

    /// <summary>The actor created with a script id, by the retail lookup.</summary>
    private string? FindById(int id)
    {
        foreach (var actor in Actors.Values) if (actor.Character == id) return actor.Id;
        return null;
    }

    /// <summary>
    /// Places the sky panorama as retail's drawer does (BATTLE.PRG 0x8008ECB4, 0x8009147C): its top row stands on the
    /// 240-line screen at the camera's pitch (an angle of 4096 a turn, upward positive, as the look-at at 0x8007A850
    /// measures it) times the parallax (7e) over 4096, less the event's offset (7b), from the top of the screen.
    /// </summary>
    private void PlaceSky()
    {
        // The dome is the backdrop of a room drawn without its panorama (BATTLE.PRG 0x8008EC48).
        if (_dome != null) _dome.Visible = !_skyVisible || Field.BackgroundLayers.Count == 0;
        foreach (var layer in Field.BackgroundLayers)
        {
            var material = layer.Material;
            var forward = -Field.Camera.GlobalBasis.Z;
            float level = new Vector2(forward.X, forward.Z).Length();
            if (level < 1e-6f || !_skyVisible) { material.SetShaderParameter("horizon", -100000f); continue; }
            float pitch = Mathf.Atan2(forward.Y, level) * 4096 / Mathf.Tau;
            float top = pitch * _skyParallax.Y / 4096 - _skyOffset.Y;
            // The shader stands the panorama's foot on its horizon.
            material.SetShaderParameter("horizon", top + layer.PixelHeight);
            // Across (0x8008ECB4, 0x8009147C): x is the camera's yaw (4096 a turn) times the parallax over 4096, less
            // the event's offset, and a screen pixel p shows the strip's column (p - x) modulo its width.
            var retailForward = new Vector3(forward.X, -forward.Y, -forward.Z);
            float yaw = Mathf.PosMod(Mathf.Atan2(retailForward.X, retailForward.Z) * 4096 / Mathf.Tau, 4096);
            material.SetShaderParameter("horizontal_offset", _skyOffset.X - yaw * _skyParallax.X / 4096);
        }
    }

    /// <summary>
    /// Plays the sound operations (10) of an actor's clip script up to its current frame (BATTLE.PRG 0x800AE4FC): sound
    /// n of the model's own bank, panned by where it stands; n 4 is one of 4 to 7 at random. An actor far below the
    /// room (y 0xBB8 and lower) is not heard.
    /// </summary>
    /// <summary>A character's clip scripts, by clip, read once a room (an event's set adds to them as it loads).</summary>
    private GDictionary ClipEvents(int character)
    {
        if (!_clipEvents.TryGetValue(character, out var events) || events.Count == 0)
            _clipEvents[character] = events = _content.Character(character).GetValueOrDefault("events", new GDictionary()).AsGodotDictionary();
        return events;
    }

    private readonly Dictionary<int, GDictionary> _clipEvents = new();

    /// <summary>A character's clip movements, by clip: the distance moved by each SEQ frame's end.</summary>
    private GDictionary ClipMovement(int character)
    {
        if (!_clipMovement.TryGetValue(character, out var movement) || movement.Count == 0)
            _clipMovement[character] = movement = _content.Character(character).GetValueOrDefault("movement", new GDictionary()).AsGodotDictionary();
        return movement;
    }

    private readonly Dictionary<int, GDictionary> _clipMovement = new();

    /// <summary>
    /// Moves an actor by its clip's movement stream (BATTLE.PRG 0x800AFA28: a quarter of the stream's value a frame,
    /// in map axes) from the frame last reached to the current one.
    /// </summary>
    private void RunClipMovement(EventActor actor)
    {
        if (actor.Player == null) return;
        string clip = actor.Player.AssignedAnimation;
        if (clip != actor.MoveClip) { actor.MoveClip = clip; actor.MoveFrame = 0; }
        if (clip.Length == 0) return;
        var steps = ClipMovement(actor.Character).GetValueOrDefault(clip, new GArray()).AsGodotArray();
        if (steps.Count == 0) return;
        double position = !actor.Player.CurrentAnimation.IsEmpty ? actor.Player.CurrentAnimationPosition : actor.Player.HasAnimation(clip) ? actor.Player.GetAnimation(clip).Length : 0;
        int frame = Math.Min(steps.Count, (int)(position * Installation.CharacterConversion.FramesPerSecond));
        // A clip that starts over moves on from its start.
        if (frame < actor.MoveFrame) actor.MoveFrame = 0;
        Vector3 At(int f)
        {
            if (f <= 0) return Vector3.Zero;
            var step = steps[f - 1].AsGodotArray();
            return new Vector3(step[0].AsInt32(), step[1].AsInt32(), step[2].AsInt32());
        }
        if (frame == actor.MoveFrame) return;
        actor.Position += At(frame) - At(actor.MoveFrame);
        actor.MoveFrame = frame;
    }

    private void RunClipSounds(EventActor actor)
    {
        if (actor.Player == null || !actor.Visible) return;
        string clip = actor.Player.AssignedAnimation;
        if (clip != actor.SoundClip) { actor.SoundClip = clip; actor.SoundFrame = -1; }
        if (clip.Length == 0 || actor.Position.Y >= 0xBB8) return;
        // A clip played once and ended is no longer current: it stands at its end.
        double position = !actor.Player.CurrentAnimation.IsEmpty ? actor.Player.CurrentAnimationPosition : actor.Player.HasAnimation(clip) ? actor.Player.GetAnimation(clip).Length : 0;
        int frame = (int)(position * Installation.CharacterConversion.FramesPerSecond);
        if (frame < actor.SoundFrame) actor.SoundFrame = -1;
        var events = ClipEvents(actor.Character).GetValueOrDefault(clip, new GArray()).AsGodotArray();
        foreach (var value in events)
        {
            var entry = value.AsGodotArray();
            int at = entry[0].AsInt32(), op = entry[1].AsInt32();
            if (op != 10 || entry.Count < 3 || at <= actor.SoundFrame || at > frame) continue;
            int sound = entry[2].AsInt32();
            if (sound == 4) sound = 4 + Random.Shared.Next(4);
            PlayActorSound(actor, $"c{actor.Character:X2}_{sound}_p128");
        }
        actor.SoundFrame = frame;
    }

    /// <summary>
    /// Runs the weapon operations of an actor's clip script (BATTLE.PRG's interpreter at 0x800AD714) up to the clip's
    /// current SEQ frame: 32 and 33 show the held weapon, 34 and 35 hide it.
    /// </summary>
    private void RunClipScript(EventActor actor)
    {
        if (actor.Player == null) return;
        string clip = actor.Player.AssignedAnimation;
        if (clip != actor.ScriptClip) { actor.ScriptClip = clip; actor.ScriptFrame = -1; }
        if (clip.Length == 0) return;
        // A clip played once and ended is no longer current: it stands at its end.
        double position = !actor.Player.CurrentAnimation.IsEmpty ? actor.Player.CurrentAnimationPosition : actor.Player.HasAnimation(clip) ? actor.Player.GetAnimation(clip).Length : 0;
        int frame = (int)(position * Installation.CharacterConversion.FramesPerSecond);
        if (frame < actor.ScriptFrame) actor.ScriptFrame = -1;
        var events = ClipEvents(actor.Character).GetValueOrDefault(clip, new GArray()).AsGodotArray();
        foreach (var value in events)
        {
            var entry = value.AsGodotArray();
            int at = entry[0].AsInt32(), op = entry[1].AsInt32();
            if (op is < 32 or > 35 || at <= actor.ScriptFrame || at > frame) continue;
            actor.WeaponOut = op <= 33;
        }
        actor.ScriptFrame = frame;
    }

    /// <summary>The canvas layer of overlay layers drawn behind the room.</summary>
    private const int BehindLayer = -10;

    /// <summary>While an overlay layer is drawn behind the room, the field's background is that canvas.</summary>
    private void ShowBehind()
    {
        if (Field == null) return;
        if (Field.FieldEnvironment == null)
        {
            if (!_overlay.AnyBehind) return;
            Field.FieldEnvironment = new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.ClearColor } };
            Field.AddChild(Field.FieldEnvironment);
        }
        var environment = Field.FieldEnvironment.Environment;
        environment.BackgroundMode = _overlay.AnyBehind ? Godot.Environment.BGMode.Canvas : Godot.Environment.BGMode.ClearColor;
        environment.BackgroundCanvasMaxLayer = BehindLayer;
    }

    /// <summary>
    /// The room's sky dome (BATTLE.PRG 0x8009820C): a sphere of 409 screen pixels, its corners turned by the camera
    /// and laid on the 320x240 screen without perspective, so a pixel sees the direction whose view-space x and y are
    /// its offset from the centre over 409. Its bands are Gouraud-shaded between the zenith, the rings at a height of
    /// 204 of 457 (up and down), the horizon and the nadir, drawn behind everything.
    /// </summary>
    private static MeshInstance3D Dome(Godot.Collections.Array bands)
    {
        var shader = new Shader { Code = "shader_type spatial; render_mode unshaded,depth_draw_never,cull_disabled,fog_disabled; uniform vec3 bands[5]; void vertex(){POSITION=vec4(VERTEX.xy,CLIP_SPACE_FAR,1.0);} void fragment(){vec2 p=(SCREEN_UV-0.5)*vec2(224.0*VIEWPORT_SIZE.x/VIEWPORT_SIZE.y,224.0)/409.0; vec3 view=vec3(p.x,-p.y,-sqrt(max(0.0,1.0-dot(p,p)))); float up=normalize(mat3(INV_VIEW_MATRIX)*view).y; const float ring=204.0/457.0; vec3 c; if(up>ring){c=mix(bands[1],bands[0],(up-ring)/(1.0-ring));} else if(up>0.0){c=mix(bands[2],bands[1],up/ring);} else if(up>-ring){c=mix(bands[2],bands[3],-up/ring);} else {c=mix(bands[3],bands[4],(-up-ring)/(1.0-ring));} ALBEDO=c;}" };
        var material = new ShaderMaterial { Shader = shader, RenderPriority = -128 };
        var colours = new Vector3[5];
        for (int i = 0; i < 5; i++)
        {
            var band = bands[i].AsGodotArray();
            var colour = Color.Color8((byte)band[0].AsInt32(), (byte)band[1].AsInt32(), (byte)band[2].AsInt32()).SrgbToLinear();
            colours[i] = new Vector3(colour.R, colour.G, colour.B);
        }
        material.SetShaderParameter("bands", colours);
        return new MeshInstance3D { Name = "Dome", Mesh = new QuadMesh { Size = new(2, 2) }, MaterialOverride = material, CustomAabb = new Aabb(new(-100000, -100000, -100000), new(200000, 200000, 200000)) };
    }

    /// <summary>Gives the event camera to the shared field camera: retail map units become engine units, and the projection distance a field of view.</summary>
    private void ApplyCamera()
    {
        // The retail room renderer drops each room polygon whose nearest corner's depth, over four, is below the near
        // clip (BATTLE.PRG 0x80097A10 takes the least of the three GTE depths, 0x800980F8 compares it), so a camera
        // beside room geometry sees past it; actors keep the ordinary near plane. A corner behind the camera has depth
        // 0, so a face reaching behind it goes too. The view matrix is scaled by ec's value over 4096 (0x8007CD14 into
        // 0x1F800064, applied at 0x8007AF08), which leaves the picture as it is but scales every depth, so the cut stands
        // at four times the near clip over that scale. Only in first-person view (the switch at 0x800F19C8, which the
        // renderer takes as its second argument) are near polygons first subdivided, by their group's levels, and the
        // pieces tested: a cut by the pixel stands in for that.
        bool staged = EventCamera.Active && !Controlled;
        float reject = 4 * (staged ? EventCamera.Near : 64) * 4096 / (staged ? EventCamera.Far : 4096) / Installation.MapConversion.UnitsPerMetre;
        float pixelCut = _hideFirstPerson ? reject : 0;
        if (pixelCut != _roomNear)
        {
            _roomNear = pixelCut;
            var rigs = new HashSet<StandardMaterial3D>();
            foreach (var rig in Field.Rigs.Values) rigs.UnionWith(rig.Materials);
            foreach (var material in Field.Materials)
            {
                if (rigs.Contains(material)) continue;
                material.DistanceFadeMode = pixelCut > 0 ? BaseMaterial3D.DistanceFadeModeEnum.PixelDither : BaseMaterial3D.DistanceFadeModeEnum.Disabled;
                material.DistanceFadeMinDistance = pixelCut;
                material.DistanceFadeMaxDistance = pixelCut + 0.001f;
            }
        }
        if (Controlled)
        {
            // The gameplay projection: H 512 on the 320-pixel screen, near 64 and far 4096.
            Field.CameraEye = EventActor.RetailSpace * Gameplay.Eye;
            Field.CameraTarget = EventActor.RetailSpace * Gameplay.LookAt;
            if (_lookAround && Player is { } viewer)
            {
                // Through her eyes: the facing she had, turned and tilted by the stick (y down, so up is negative).
                float yaw = _lookYaw * Mathf.Tau / 4096, pitch = _lookPitch * Mathf.Tau / 4096;
                var eye = viewer.Position + new Vector3(0, -EyeHeight, 0);
                var ahead = new Vector3(Mathf.Sin(yaw) * Mathf.Cos(pitch), -Mathf.Sin(pitch), -Mathf.Cos(yaw) * Mathf.Cos(pitch));
                Field.CameraEye = EventActor.RetailSpace * eye;
                Field.CameraTarget = EventActor.RetailSpace * (eye + ahead * 256);
            }
            // Retail's screen shows 224 lines about the projection centre; a window wider than 4:3 sees more to the
            // sides rather than less above and below.
            Field.Camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
            Field.Camera.Fov = Mathf.RadToDeg(2 * Mathf.Atan(VisibleLines / 2f / Gameplay.Projection));
            Field.Camera.Near = 64 / Installation.MapConversion.UnitsPerMetre;
            Field.Camera.Far = 32768 / Installation.MapConversion.UnitsPerMetre;
            // The event camera stands with the gameplay one while she plays, so an event that eases from it or hands
            // it back starts from what the screen shows.
            if (EventCamera.Active)
            {
                EventCamera.Stage(EventCamera.LookAt, Gameplay.LookAt);
                EventCamera.Stage(EventCamera.Eye, Gameplay.Eye);
                EventCamera.Snap(EventCamera.LookAt);
                EventCamera.Snap(EventCamera.Eye);
            }
            return;
        }
        if (!EventCamera.Active) return;
        Field.CameraEye = EventActor.RetailSpace * EventCamera.EyePosition;
        Field.CameraTarget = EventActor.RetailSpace * EventCamera.LookAtPosition;
        Field.Camera.KeepAspect = Camera3D.KeepAspectEnum.Height;
        Field.Camera.Fov = Mathf.RadToDeg(2 * Mathf.Atan(VisibleLines / 2f / EventCamera.Projection));
        Field.Camera.Near = Math.Max(0.01f, EventCamera.Near / Installation.MapConversion.UnitsPerMetre);
        // Retail's "far" (ec) is no clip: it is the depth at which room polygons turn to their half-size far
        // textures (BATTLE.PRG 0x80098194 patches it, over 8, into the renderer at 0x80097590 and 0x800978F8), so the
        // whole room stays drawn; the town beyond MAP007's window is 3000 to 5000 units out under ec 0x16.
        Field.Camera.Far = 32768 / Installation.MapConversion.UnitsPerMetre;
    }

    /// <summary>
    /// Draws the room's geometry groups as the retail renderer does. With the cut-away on, a group is drawn only while
    /// its direction mask holds the bit for the camera's octant: its direction from what it looks at, where octant 0
    /// is -z, 2 is +x, 4 is +z and 6 is -x in map space. Scripts may also hide single groups, or the parts marked for
    /// first-person view.
    /// </summary>
    /// <summary>A room group's mesh as converted, face by face, and which of its triangles retail would not draw.</summary>
    private sealed class RoomFaces
    {
        public required MeshInstance3D View;
        public required ArrayMesh Mesh;
        public required GArray[] Arrays;
        public required Material[] Materials;
        /// <summary>Per surface, the triangles of each face in turn (a quad's two).</summary>
        public required int[][] Faces;
        /// <summary>Per surface, whether each triangle is left out.</summary>
        public required bool[][] Dropped;
    }

    private readonly List<RoomFaces> _roomFaces = new();

    /// <summary>Keeps each room group's surfaces as converted, with their faces, for <see cref="DropNearFaces"/>.</summary>
    private void ReadRoomFaces(GDictionary document)
    {
        _roomFaces.Clear();
        var models = document["models"].AsGodotArray();
        foreach (var value in document["objects"].AsGodotArray())
        {
            var item = value.AsGodotDictionary();
            int model = item.GetValueOrDefault("model", -1).AsInt32();
            if (model < 0 || model >= models.Count || !Field.Actors.TryGetValue(item["id"].AsInt32().ToString(), out var node)) continue;
            if (node.GetChildCount() == 0 || node.GetChild(0) is not MeshInstance3D view || view.Mesh is not ArrayMesh mesh || mesh.GetBlendShapeCount() > 0) continue;
            var faces = new List<int[]>();
            foreach (var block in models[model].AsGodotDictionary()["blocks"].AsGodotArray())
                foreach (var group in block.AsGodotDictionary()["groups"].AsGodotArray())
                {
                    var surface = group.AsGodotDictionary();
                    if (surface["vertices"].AsGodotArray().Count == 0) continue;
                    if (!surface.ContainsKey("faces")) { faces.Clear(); break; }
                    faces.Add(surface["faces"].AsInt32Array());
                }
            if (faces.Count != mesh.GetSurfaceCount()) continue;
            var arrays = new GArray[faces.Count];
            var materials = new Material[faces.Count];
            var dropped = new bool[faces.Count][];
            for (int i = 0; i < faces.Count; i++)
            {
                arrays[i] = mesh.SurfaceGetArrays(i);
                materials[i] = mesh.SurfaceGetMaterial(i);
                int triangles = 0;
                foreach (int count in faces[i]) triangles += count;
                dropped[i] = new bool[triangles];
            }
            _roomFaces.Add(new RoomFaces { View = view, Mesh = mesh, Arrays = arrays, Materials = materials, Faces = faces.ToArray(), Dropped = dropped });
        }
    }

    /// <summary>
    /// Leaves out the room faces retail does not draw, outside first-person view (where near faces are subdivided
    /// instead). The room renderer drops a whole face whose nearest corner stands closer to the camera than the near
    /// cut (see ApplyCamera). The GPU then drops a triangle more than 1023 pixels across or 511 down, a quad going to it
    /// as two triangles tested alone: as for characters (<see cref="EventActor.ProjectAsRetail"/>), a corner nearer than
    /// H / 2 after the view's scale, or behind the eye, is projected as if it stood there and its screen place is held
    /// within -1024 to 1023. MAP172's door, ajar beside the eye in the shot over Ashley's shoulder (Episode 1, 16:53),
    /// spans more than that and is not drawn, so the man on the table shows. A group's mesh is rebuilt only when what
    /// it leaves out changes.
    /// </summary>
    private void DropNearFaces()
    {
        bool staged = EventCamera.Active && !Controlled;
        float units = Installation.MapConversion.UnitsPerMetre;
        float projection = staged ? EventCamera.Projection : Gameplay.Projection, far = staged ? EventCamera.Far : 4096;
        float reject = 4 * (staged ? EventCamera.Near : 64) * 4096 / far, least = projection / 2 * 4096 / far;
        var view = Field.Camera.GlobalTransform.AffineInverse();
        var screen = new Vector2[6];
        foreach (var room in _roomFaces)
        {
            var toEye = view * room.View.GlobalTransform;
            bool changed = false;
            for (int surface = 0; surface < room.Faces.Length; surface++)
            {
                var vertices = room.Arrays[surface][(int)Mesh.ArrayType.Vertex].AsVector3Array();
                int at = 0, triangle = 0;
                for (int face = 0; face < room.Faces[surface].Length; face++)
                {
                    int count = room.Faces[surface][face] * 3;
                    if (screen.Length < count) screen = new Vector2[count];
                    float nearest = float.PositiveInfinity;
                    for (int v = 0; v < count; v++)
                    {
                        // The corner in the camera's frame, in retail units, and its screen place (9/8 across, y down).
                        var local = toEye * vertices[at + v] * units;
                        float depth = -local.Z, divisor = Math.Max(depth, least);
                        nearest = Math.Min(nearest, depth);
                        screen[v] = new Vector2(Math.Clamp(projection * local.X * 9 / 8 / divisor, -1024 - 160, 1023 - 160), Math.Clamp(projection * -local.Y / divisor, -1024 - 112, 1023 - 112));
                    }
                    bool near = !_hideFirstPerson && nearest < reject;
                    for (int v = 0; v < count; v += 3, triangle++)
                    {
                        Vector2 a = screen[v], b = screen[v + 1], c = screen[v + 2];
                        bool drop = near || !_hideFirstPerson
                            && (Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X)) > 1023 || Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y)) > 511);
                        if (drop != room.Dropped[surface][triangle]) { room.Dropped[surface][triangle] = drop; changed = true; }
                    }
                    at += count;
                }
            }
            if (!changed) continue;
            room.Mesh.ClearSurfaces();
            for (int surface = 0; surface < room.Faces.Length; surface++)
            {
                var source = room.Arrays[surface];
                var kept = new List<int>();
                for (int triangle = 0; triangle < room.Dropped[surface].Length; triangle++)
                    if (!room.Dropped[surface][triangle]) for (int v = triangle * 3; v < triangle * 3 + 3; v++) kept.Add(v);
                if (kept.Count == 0) kept.AddRange(new[] { 0, 0, 0 });
                var arrays = new GArray();
                arrays.Resize((int)Mesh.ArrayType.Max);
                var vertices = source[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var uv = source[(int)Mesh.ArrayType.TexUV].AsVector2Array();
                var colors = source[(int)Mesh.ArrayType.Color].AsColorArray();
                var normals = source[(int)Mesh.ArrayType.Normal].AsVector3Array();
                arrays[(int)Mesh.ArrayType.Vertex] = kept.ConvertAll(v => vertices[v]).ToArray();
                arrays[(int)Mesh.ArrayType.TexUV] = kept.ConvertAll(v => uv[v]).ToArray();
                arrays[(int)Mesh.ArrayType.Color] = kept.ConvertAll(v => colors[v]).ToArray();
                arrays[(int)Mesh.ArrayType.Normal] = kept.ConvertAll(v => normals[v]).ToArray();
                room.Mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                room.Mesh.SurfaceSetMaterial(surface, room.Materials[surface]);
            }
        }
    }

    private void ShowRoomGroups()
    {
        var back = EventActor.RetailSpace.Basis.Inverse() * Field.Camera.GlobalBasis.Z;
        int octant = (int)Mathf.PosMod(Mathf.Round(Mathf.Atan2(back.X, -back.Z) / (Mathf.Pi / 4)), 8);
        var groups = Document.GetValueOrDefault("room_groups", new GArray()).AsGodotArray();
        for (int group = 0; group < groups.Count; group++)
        {
            if (!Field.Actors.TryGetValue(group.ToString(), out var node)) continue;
            var entry = groups[group].AsGodotDictionary();
            node.Visible = !_hiddenGroups.Contains(group) && !(_hideFirstPerson && entry["first_person"].AsBool()) && (!_cutaway || (entry["mask"].AsInt32() & (1 << octant)) != 0);
        }
    }

    public Task PrepareForPresentation()
    {
        Field?.AdvancePresentation();
        Field?.ResetPhysicsInterpolation();
        return Task.CompletedTask;
    }
}
