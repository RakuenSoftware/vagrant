// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Godot;
using Wol.Games.Vagrant.Runtime;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Tests;

/// <summary>
/// Loads the installed opening room through the shared field presentation and checks its construction.
/// Requires an installation (<c>--install-root=</c>). <c>--capture=DIR</c> saves views from several headings when rendering.
/// </summary>
public partial class FieldContracts : Node
{
    private FieldView _view = null!;
    private string _capture = "";
    /// <summary>With --near-enemy, Ashley is set down beside the room's first hostile character as she gets control.</summary>
    private bool _nearEnemy;
    /// <summary>A continuous New Game run using pad input, without the focused checks' state grants.</summary>
    private bool _campaign, _campaignBypassEnemies, _campaignRepair, _campaignRepaired, _campaignBoarding, _campaignResting;
    private int _campaignRepairFrame, _campaignUiStep, _campaignUiAt, _campaignUiGeneration;
    private bool _campaignSaveUi, _campaignUiSaved;
    private string _campaignSave = "", _campaignLoad = "";
    private (int Zone, int Map)? _campaignCheckpoint;
    private int _campaignRooms, _campaignDoor = -1, _campaignDoorAt, _campaignBagTries;
    private int _campaignPuzzle, _campaignPuzzleAt, _campaignHallAt;
    private string _campaignTargetText = "";
    private int _campaignCubePicks, _campaignWaitForEnemyUntil, _emptyLimbPicks, _fightCloseUntil;
    private bool _fightWasPaused, _fightNextPart, _fightLinked;
    private string _fightPreferredPart = "", _fightTargetId = "";
    private int _fightPartSearch, _fightProgressAt;
    private int _fightLastHp = -1, _fightStalls;
    private int _campaignHealStep, _campaignHealAt, _campaignHealStart, _campaignHealItem, _campaignHealCount, _campaignHealHp, _campaignHealMp, _campaignHealClass, _campaignHealSpellRow, _campaignHealSelection, _campaignDeathAt;
    private bool _campaignHealMagic;
    private int _campaignHealSpell = 96;
    private int _campaignAbilityStep, _campaignAbilitySelection, _fightChainLinks;
    private Vector3 _campaignRestPoint;
    private (int Zone, int Map)? _campaignLoot;
    private int _campaignLootAt, _campaignLootStart, _campaignLootItem;

    private bool _repairAllCheck;
    private bool _combineCheck;
    private bool _nameCheck, _assemblyCheck;
    private int _assemblyFrame, _campaignStory, _campaignFlag, _campaignFlagValue, _campaignItem;
    private string _assemblyBefore = "";
    private int _nameFrame;
    private string _nameOriginal = "";
    private int _combineFrame;
    private string _combineBefore = "";
    private Godot.Collections.Dictionary? _combineFirst, _combineSecond, _combinePreview;
    private int _repairFrame, _repairDp, _repairPp, _repairWait;
    private Godot.Collections.Dictionary? _repairLoose, _repairAccessory, _repairStored, _repairShield, _repairArmour;
    private readonly Dictionary<string, Vector3> _campaignHiddenPositions = new();
    private readonly Dictionary<string, int> _campaignHops = new();
    // Optional room-by-room pad route, with cutaways left to the event runtime.
    private readonly List<(int Zone, int Map)> _campaignRoute = new();
    private int _campaignRouteAt, _campaignMinePerch, _campaignBoardingMisses;
    private readonly Dictionary<(int Zone, int Map), int> _campaignVisits = new();
    private readonly Dictionary<(int Zone, int Map, int Door), int> _campaignAttempts = new();

    /// <summary>With --defeat, every hostile character falls as Ashley gets control, so the room's defeat event plays.</summary>
    private bool _defeat;
    /// <summary>With --remember, the room's hostile characters are felled and the room entered again: they must stay gone.</summary>
    private bool _remember;
    private int _rememberStage;
    /// <summary>With --skip, Start is pressed as soon as the room's event opens a skippable section; the event must go on to its end.</summary>
    private bool _skip;
    /// <summary>With --effect=N, visual effect N plays from Ashley at the nearest character once she has control; it must draw and end.</summary>
    private int _effectId = -1, _effectAt, _effectsDrawn, _effectsSilent;
    private readonly Queue<int> _effectIds = new();
    private bool _resident;
    private FieldEffect? _effect;
    private int _frame;
    private int _shot;
    private bool _heardMusic;

    /// <summary>A room to play alone (--room=zone,map): its event runs to the end, and a stall is reported.</summary>
    private (int Zone, int Map)? _room;
    /// <summary>With <c>--at=X,Z</c>, Ashley is set down on that tile of the room instead of at a door, as if she had walked there: the room's tile conditions then start its scenes as play would.</summary>
    private (int X, int Z)? _at;
    /// <summary>With <c>--yaw=</c>, the gameplay camera stands at that yaw (4096 a turn, 512 a step of L1 or R1), to match a view the footage shows.</summary>
    private int? _yaw;
    /// <summary>With <c>--fight</c>, once Ashley is the player's in the room she fights what it holds, pressing Circle as each of her blows lands as a player chains them, in place of the room's other steps; it ends when nothing hostile is left.</summary>
    private bool _fight;
    /// <summary>
    /// With <c>--swoop</c> in a fight (Guildenstern's final form, <c>--room=27,1</c>), Ashley keeps her full hit points
    /// and each change of scenario variable 166, by which the boss and the room's scenes hand the swoop over, is
    /// printed with where the boss is; the boss starts at three quarters of its hit points. While it is 1 (a swoop asked for), 5 (the dive made) or 6 (broken off) she
    /// stands still, as the scenes that answer them wait for. It passes once 166 has gone 1, 4, 5 or 6 and back to 0,
    /// and fails without that in 20000 frames.
    /// </summary>
    private bool _swoop;
    /// <summary>
    /// With <c>--movement</c> (a room with a walking enemy, e.g. <c>--room=13,1</c>), once Ashley is the player's the
    /// enemies' body motion is checked against retail's numbers (<see cref="CheckMovement"/>).
    /// </summary>
    private bool _movement;
    private string _swoopTrail = "0";
    private int _swoopAt = -1;
    /// <summary>
    /// With <c>--reveal-traps</c>, once Ashley is the player's the Eye of Argon's effect plays at every trap in the room,
    /// which reveals them; she stands beside the first, and its panel is captured every 4 frames from 80 to 120.
    /// </summary>
    private bool _revealTraps;
    private List<(string Id, int Kind, int X, int Z, float Bottom, int Orientation)> _cubeCrates = new();
    private int _revealedAt = -1;
    /// <summary>
    /// With <c>--art=N</c> in a fight, Ashley uses Break Art action N once she can, captured every other frame; its blow
    /// lands while its effect plays (at the effect's hit, as a spell's), and the art is over within 240 frames.
    /// </summary>
    private int _art = -1, _artAt = -1, _artTargetHp;
    /// <summary>With <c>--shortcuts</c>, once Ashley is the player's L2 holds the shortcut menu open and CHAIN and ITEMS are taken from it, in place of the room's other steps: each must open its page, and leaving the page must close the menu.</summary>
    private bool _shortcuts;
    /// <summary>With <c>--carry</c>, Ashley lifts the nearest block (kinds 4 to 6), turns about with it and sets it down.</summary>
    private bool _carry;
    private int _carryStep, _carryCrates;
    private string _carryTiles = "";
    /// <summary>With <c>--cube</c>, Ashley draws her weapon facing the nearest crate, opens the attack sphere on it ("Cube") and strikes it: no crate moves, and only a kind 2 or 4 crate may break.</summary>
    private bool _cube;
    private int _cubeStep, _cubeRisk, _cubeRiskBefore;
    /// <summary>With <c>--status=N</c>, Ashley takes status effect N once she is the player's, for the HUD to show it.</summary>
    private int _status = -1;
    private int _shortcutStep;
    private string _lastNode = "";
    private int _controlAt;
    private int _nodeFrame;
    /// <summary>The display frame of the room-event capture (<c>--event-frame=</c>).</summary>
    private int _eventFrame = 600;
    /// <summary>With <c>--event-every=</c>, a capture every that many frames while the room's event plays.</summary>
    private int _eventEvery, _storyEvery = 120;
    /// <summary>With <c>--watch=</c>, a capture every that many frames whatever is happening, to follow a fight or a scene through control.</summary>
    private int _watchEvery;
    /// <summary>The SETUP page's own steps, while the room's steps wait at EQUIP's way out.</summary>
    private int _setupStep;
    /// <summary>Using a MISC thing's own steps, while the room's steps wait after the item actions.</summary>
    private int _itemStep;
    /// <summary>DATA's SAVE on a magic circle: its own steps, while the room's wait.</summary>
    private int _dataSaveStep;
    /// <summary>Frames waited while the container reads the memory cards.</summary>
    private int _accessWait;
    /// <summary>With <c>--tap-every=</c>, Circle is pressed that many frames apart while the room's event plays (60 by default), to pace its text as a reader would.</summary>
    private int _tapEvery = 60;

    public override void _Ready()
    {
        // The checks save to their own memory cards, empty, never the player's, and apart from other runs'.
        FieldView.IsolateSaves("user://contract_saves");
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--capture=", StringComparison.Ordinal)) _capture = argument["--capture=".Length..];
            if (argument == "--near-enemy") _nearEnemy = true;
            if (argument == "--campaign") _campaign = true;
            if (argument.StartsWith("--campaign-save=", StringComparison.Ordinal)) _campaignSave = argument["--campaign-save=".Length..];
            if (argument.StartsWith("--campaign-load=", StringComparison.Ordinal)) { _campaign = true; _campaignLoad = argument["--campaign-load=".Length..]; }
            if (argument.StartsWith("--campaign-item=", StringComparison.Ordinal)) _campaignItem = int.Parse(argument["--campaign-item=".Length..]);
            if (argument.StartsWith("--campaign-loot-item=", StringComparison.Ordinal)) _campaignLootItem = int.Parse(argument["--campaign-loot-item=".Length..]);
            if (argument.StartsWith("--campaign-loot=", StringComparison.Ordinal) && argument["--campaign-loot=".Length..].Split(',') is [var lootZone, var lootMap]) _campaignLoot = (int.Parse(lootZone), int.Parse(lootMap));
            if (argument.StartsWith("--campaign-checkpoint=", StringComparison.Ordinal) && argument["--campaign-checkpoint=".Length..].Split(',') is [var checkpointZone, var checkpointMap]) _campaignCheckpoint = (int.Parse(checkpointZone), int.Parse(checkpointMap));
            if (argument == "--workshop-repair") _repairAllCheck = true;
            if (argument.StartsWith("--campaign-route=", StringComparison.Ordinal))
                foreach (string stop in argument["--campaign-route=".Length..].Split('/'))
                {
                    var pair = stop.Split(',');
                    _campaignRoute.Add((int.Parse(pair[0]), int.Parse(pair[1])));
                }
            if (argument == "--campaign-repair") _campaignRepair = true;
            if (argument == "--campaign-save-ui") _campaignSaveUi = true;
            if (argument == "--workshop-combine") _combineCheck = true;
            if (argument.StartsWith("--campaign-flag=", StringComparison.Ordinal) && argument["--campaign-flag=".Length..].Split(':') is [var conditionFlag, var conditionValue]) (_campaignFlag, _campaignFlagValue) = (int.Parse(conditionFlag), int.Parse(conditionValue));
            if (argument.StartsWith("--campaign-story=", StringComparison.Ordinal)) _campaignStory = int.Parse(argument["--campaign-story=".Length..]);
            if (argument == "--workshop-assembly") _assemblyCheck = true;
            if (argument == "--workshop-name") _nameCheck = true;
            if (argument == "--defeat") _defeat = true;
            if (argument == "--remember") _remember = true;
            if (argument == "--skip") _skip = true;
            if (argument.StartsWith("--effect=", StringComparison.Ordinal) || argument.StartsWith("--resident=", StringComparison.Ordinal))
            {
                // One effect, or a range (first-last) played one after another; --resident plays the resident file's small effects.
                _resident = argument.StartsWith("--resident=", StringComparison.Ordinal);
                var bounds = argument[(argument.IndexOf('=') + 1)..].Split('-');
                int first = int.Parse(bounds[0]), last = bounds.Length > 1 ? int.Parse(bounds[1]) : first;
                for (int id = first; id <= last; id++) _effectIds.Enqueue(id);
                _effectId = _effectIds.Dequeue();
            }
            if (argument == "--game-over") _gameOver = true;
            if (argument == "--game-over-load") _gameOver = _loadAfter = true;
            if (argument.StartsWith("--event-frame=", StringComparison.Ordinal)) _eventFrame = int.Parse(argument["--event-frame=".Length..]);
            if (argument.StartsWith("--event-every=", StringComparison.Ordinal)) _eventEvery = int.Parse(argument["--event-every=".Length..]);
            // --story-every=N captures the story's events every N frames (120 by default).
            if (argument.StartsWith("--story-every=", StringComparison.Ordinal)) _storyEvery = Math.Max(1, int.Parse(argument["--story-every=".Length..]));
            if (argument.StartsWith("--watch=", StringComparison.Ordinal)) _watchEvery = int.Parse(argument["--watch=".Length..]);
            // Characters are drawn Original unless --enhanced asks otherwise, whatever the player's saved choice.
            if (argument.StartsWith("--", StringComparison.Ordinal)) CharacterQuality.Choose(System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--enhanced") >= 0, save: false);
            if (argument.StartsWith("--", StringComparison.Ordinal)) FieldLighting.Choose(System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--enhanced") >= 0, save: false);

            if (argument.StartsWith("--tap-every=", StringComparison.Ordinal)) _tapEvery = Math.Max(1, int.Parse(argument["--tap-every=".Length..]));
            if (argument.StartsWith("--room=", StringComparison.Ordinal) && argument["--room=".Length..].Split(',') is [var zone, var map]) _room = (int.Parse(zone), int.Parse(map));
            if (argument.StartsWith("--at=", StringComparison.Ordinal) && argument["--at=".Length..].Split(',') is [var tileX, var tileZ]) _at = (int.Parse(tileX), int.Parse(tileZ));
            if (argument.StartsWith("--yaw=", StringComparison.Ordinal)) _yaw = int.Parse(argument["--yaw=".Length..]);
            if (argument == "--fight") _fight = true;
            if (argument == "--options-window") _optionsWindow = true;
            // --options=N sets the OPTIONS word ([0x80060020]) as a player's choices would: 0x40 WEAPON STATUS ON, 0x80 ARMOR STATUS ON.
            if (argument.StartsWith("--options=", StringComparison.Ordinal)) _options = Convert.ToUInt32(argument["--options=".Length..], 16);
            if (argument == "--swoop") _swoop = true;
            if (argument == "--movement") _movement = true;
            if (argument == "--reveal-traps") _revealTraps = true;
            if (argument.StartsWith("--art=", StringComparison.Ordinal)) _art = int.Parse(argument["--art=".Length..]);
            if (argument == "--shortcuts") _shortcuts = true;
            if (argument == "--carry") _carry = true;
            if (argument == "--cube") _cube = true;
            if (argument.StartsWith("--status=", StringComparison.Ordinal)) _status = int.Parse(argument["--status=".Length..]);
            if (argument == "--ending") _endingCheck = true;
            if (argument == "--event-roundtrip") _eventRoundTrip = true;
            // --play=EVENT plays an event (event_z009_m08_l0, a room's local one) once the player first has control, as
            // the room's condition would, then the check goes on with what follows.
            if (argument.StartsWith("--play=", StringComparison.Ordinal)) _play = argument["--play=".Length..];
            // --flag=N:V (repeatable) sets story flag N to V before the room loads, to reach a room's scene as the story would.
            if (argument.StartsWith("--flag=", StringComparison.Ordinal) && argument["--flag=".Length..].Split(':') is [var flag, var value]) _startFlags[int.Parse(flag)] = int.Parse(value);
        }
        if (_endingCheck)
        {
            // The ending alone: its credits roll over the illustrations to the end.
            var installed = GetNode<RetailContent>("/root/RetailContent");
            if (!installed.Installed || !installed.Has("ending")) { Finish("the ending is not installed"); return; }
            _ending = GD.Load<PackedScene>(Wol.Games.Vagrant.Scenes.Ending.Ending.Scene).Instantiate<Wol.Games.Vagrant.Scenes.Ending.Ending>();
            _ending.KeepScene = true;
            AddChild(_ending);
            return;
        }
        if (_room is { } room)
        {
            var only = GetNode<RetailContent>("/root/RetailContent");
            if (!only.Installed) { Finish("installation unavailable: " + only.Failure); return; }
            _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
            _view.StartZone = room.Zone;
            _view.KeepScene = true;
            _view.StartMap = room.Map;
            // She arrives as play brings her in: through the first door of another room that leads here, of the zone
            // first, then of any zone (a zone's first room is often entered from the zone before). Each room looked at
            // is let go again, so that looking through them all does not hold them all.
            if (_at is { } tile) _view.StartAt = (tile.X, tile.Z, 0, false);
            else
            {
                var rooms = new System.Collections.Generic.List<(int Zone, int Map, string Name)>();
                foreach (string name in only.Field.RoomNames())
                    if (int.Parse(name[1..4]) is var roomZone && int.Parse(name[6..8]) is var roomMap && (roomZone != room.Zone || roomMap != room.Map)) rooms.Add((roomZone, roomMap, name));
                rooms.Sort((a, b) => a.Zone == b.Zone ? a.Map.CompareTo(b.Map) : a.Zone == room.Zone ? -1 : b.Zone == room.Zone ? 1 : a.Zone.CompareTo(b.Zone));
                foreach (var (otherZone, other, name) in rooms)
                {
                    System.Text.Json.Nodes.JsonObject? document;
                    try { document = only.Field.Data("field_" + name); }
                    catch (System.IO.InvalidDataException) { continue; }
                    only.Field.CacheRoom(name);
                    foreach (var door in document?["doors"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
                    {
                        int zone = (int)door!["zone"]! is var named && named != 0 ? named : otherZone;
                        if (zone != room.Zone || (int)door["map"]! != room.Map) continue;
                        var to = door["destination"]!;
                        int x = (int)to[0]!, z = (int)to[1]!;
                        _view.StartAt = (x, z, (int)door["facing"]!, (bool)door["half"]!);
                        GD.Print(otherZone == room.Zone ? $"FIELD: she arrives by the door from map {other} at tile {x}, {z}" : $"FIELD: she arrives by the door from zone {otherZone} map {other} at tile {x}, {z}");
                        break;
                    }
                    if (_view.StartAt != null) break;
                }
            }
            foreach (var (flag, value) in _startFlags) _view.Flags[flag] = value;
            _view.Options = _options;
            AddChild(_view);
            return;
        }
        var content = GetNode<RetailContent>("/root/RetailContent");
        if (!content.Installed) { Finish("installation unavailable: " + content.Failure); return; }
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        _view.KeepScene = _campaign;
        if (_campaignLoad.Length != 0)
        {
            FieldView.PendingSave = Json.ParseString(File.ReadAllText(_campaignLoad)).AsGodotDictionary();
            GD.Print($"CAMPAIGN: resuming earned checkpoint {_campaignLoad}");
        }
        AddChild(_view);
        if (_view.Failure.Length != 0) { Finish(_view.Failure); return; }
        if (_campaignLoad.Length != 0) return;
        var field = _view.Field;
        int surfaces = 0, triangles = 0;
        // The room is one object per geometry group; event actors share the geometry node but are not counted.
        foreach (var item in _view.Document["objects"].AsGodotArray())
        foreach (var node in field.Actors[item.AsGodotDictionary()["id"].AsInt32().ToString()].FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (ArrayMesh)((MeshInstance3D)node).Mesh;
            surfaces += mesh.GetSurfaceCount();
            // An additive or subtractive polygon is drawn twice (its unmasked texels opaque, its masked ones blended):
            // its blended draw is not counted again.
            for (int i = 0; i < mesh.GetSurfaceCount(); i++)
                if (mesh.SurfaceGetMaterial(i) is not BaseMaterial3D { BlendMode: BaseMaterial3D.BlendModeEnum.Add or BaseMaterial3D.BlendModeEnum.Sub }) triangles += mesh.SurfaceGetArrayLen(i) / 3;
        }
        // MAP001 holds 286 triangles and 803 quads.
        if (triangles != 286 + 803 * 2) { Finish($"the opening room has {triangles} triangles"); return; }
        if (surfaces == 0 || !field.HasBounds) { Finish("the opening room has no surfaces"); return; }
        GD.Print($"FIELD: {surfaces} surfaces, {triangles} triangles, bounds {field.Bounds}");
        if (!Json.Stringify(GetNode<RetailContent>("/root/RetailContent").Data("event_z001_m00")).Contains("Have you found Sydney?")) { Finish("the opening event's text did not decode"); return; }
        // MAP001's collision map: 88 walkable 128-unit tiles of 16 sub-cells each, with the hall floor at y -640.
        var walkmesh = _view.Document["walkmeshes"].AsGodotArray()[0].AsGodotDictionary();
        if (walkmesh["triangles"].AsGodotArray().Count != 88 * 16 * 2 || _view.FloorHeight(new Vector3(756, 0, 604)) != -640 || _view.Field.Navigation == null)
        { Finish("the opening room's collision map did not decode"); return; }
        if (_view.EventNode.Length == 0 || _view.EventNode == "complete") { Finish("the opening event did not start (" + _view.EventNode + ")"); return; }
    }

    /// <summary>Frames the event may take, pressing Circle whenever text waits.</summary>
    private const int EventFrames = 60 * 600;

    private bool _endingCheck;
    private Wol.Games.Vagrant.Scenes.Ending.Ending? _ending;

    public override void _PhysicsProcess(double delta)
    {
        if (_yaw is { } yaw && _view != null) _view.Gameplay.Yaw = yaw;
        if (_ending == null && _view is { GameEnded: true })
            foreach (var child in _view.Screen.GetChildren())
                if (child is Wol.Games.Vagrant.Scenes.Ending.Ending ending)
                {
                    _ending = ending;
                    _frame = 0;
                    GD.Print("FIELD: the final event handed the field to the ending");
                    break;
                }
        if (_ending != null)
        {
            _frame++;
            if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && (_frame % _storyEvery == 0 || _frame is 300)) Capture($"ending_{_ending.Frames:d5}");
            // The closing screen waits for a button: Circle once its parts are all in.
            if (_ending.Results != null && _frame % 600 == 0) Input.ActionPress("ps_circle");
            if (_ending.Results != null && _frame % 600 == 4) Input.ActionRelease("ps_circle");
            if (_ending.Finished) { GD.Print($"FIELD: the ending ran {_ending.Frames} frames of credits and its closing screen, {_frame} in all, to the title"); Finish(""); return; }
            if (_frame > 60 * 600) Finish("the ending did not finish");
            return;
        }
        if (_view == null || _shot < 0) return;
        _frame++;
        // A gear-change notice, once, 10 frames after the first shows.
        if (_view.Battle?.Hud is { NoticesShown: > 0 } && _noticeAt == 0) { _noticeAt = _frame + 10; GD.Print($"FIELD: a gear-change notice shows at frame {_frame}"); }
        if (_noticeAt > 0 && _frame == _noticeAt && _capture.Length != 0 && DisplayServer.GetName() != "headless") Capture("battle_notice");
        if (_view.Failure.Length != 0) { Finish(_view.Failure); return; }
        if (_campaign) { StepCampaign(); return; }
        if (_repairAllCheck) { StepWorkshopRepair(); return; }
        if (_combineCheck) { StepWorkshopCombine(); return; }
        if (_nameCheck) { StepWorkshopName(); return; }
        if (_assemblyCheck) { StepWorkshopAssembly(); return; }
        if (_room != null) { StepRoom(); return; }
        if (_battleFrame != 0) { StepBattle(); return; }
        if (_controlFrame != 0) { StepControl(); return; }
        // Hold Circle for a few frames every second (or every --tap-every frames, at a reader's pace): enough to
        // finish and confirm each line of text.
        if (_frame % _tapEvery == 0) Input.ActionPress("ps_circle");
        if (_frame % _tapEvery == 4) Input.ActionRelease("ps_circle");
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && _frame % _storyEvery == 30 % _storyEvery) Capture($"event_{_shot++:d4}_{(_view.Events.Count > 0 ? _view.Events[^1] : "none")}_{_view.EventNode}");
        _heardMusic |= _view.MusicPlaying;
        if (_view.Settled || _view.EndOfContent.Length != 0)
        {
            string rooms = string.Join(" > ", _view.Rooms), events = string.Join(" > ", _view.Events);
            GD.Print($"FIELD: settled after {_frame} frames in {rooms}; events {events}; missing characters: {string.Join(",", _view.MissingCharacters)}; slowest room load {_view.SlowestLoad:f0} ms");
            if (!_heardMusic) { Finish("the opening's music never started"); return; }
            // New Game's prologue: the knights (zone 1), the manor's exterior (zone 2), then the hall where Ashley meets
            // Müllenkamp's soldiers (zone 3), whose scene chains two event files before Ashley takes control.
            if (rooms != "z001_m00 > z002_m00 > z003_m00" || !events.EndsWith("evt0001 > evt0002", StringComparison.Ordinal))
            { Finish("the prologue did not play through to Ashley's first battle"); return; }
            _controlFrame = _frame;
            return;
        }
        if (_frame > EventFrames) Finish($"the prologue did not settle (in {string.Join(" > ", _view.Rooms)} at {_view.EventNode})");
    }

    private int _controlFrame;
    private Vector3 _start, _startEye;

    /// <summary>Assembly cancellation keeps loose parts intact; YES consumes each part/gem exactly once, before naming.</summary>
    private void StepWorkshopAssembly()
    {
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (!_view.Controlled) { if (_frame % 30 == 0) Tap("ps_circle"); return; }
        switch (++_assemblyFrame)
        {
            case 1:
                _view.Disassemble(_view.Weapons()[0]);
                _view.Parts(null, grips: true)[0]["record"].AsGodotDictionary()["gem_slots"] = 2;
                foreach (int id in new[] { 269, 270 }) _view.Equipment.Add(new Godot.Collections.Dictionary { ["kind"] = "gem", ["record"] = new Godot.Collections.Dictionary { ["id"] = id } });
                _assemblyBefore = Json.Stringify(_view.Equipment);
                Tap("ps_triangle"); break;
            case 20: if (!_view.MenuOpen) { _assemblyFrame--; if (_frame % 20 == 0) Tap("ps_triangle"); } break;
            case 30 or 80 or 100 or 140 or 160 or 200 or 220 or 280 or 340 or 360 or 380 or 400 or 440 or 460 or 500 or 520 or 560 or 580 or 640 or 720: Tap("ps_circle"); break;
            case 60 or 120 or 320 or 420 or 480 or 540 or 680 or 700: Tap("ps_down"); break;
            case 180 or 620: Tap("ps_up"); break;
            case 240 or 300 or 600 or 660: Tap("ps_start"); break;
            case 260:
                if (Json.Stringify(_view.Equipment) != _assemblyBefore || _view.Weapons().Count != 0)
                { Finish("assembly consumed components before YES"); return; }
                if (_view.ItemRows.Count != 3 || !_view.ItemHelpShown.Text.Contains("Complete attachment", StringComparison.OrdinalIgnoreCase))
                { Finish($"assembly did not show the native confirmation: {_view.ItemHelpShown.Text}"); return; }
                Capture("assembly_confirmation"); break;
            case 350:
                if (Json.Stringify(_view.Equipment) != _assemblyBefore) { Finish("Cancel assembly changed its loose components"); return; }
                break;
            case 590: Capture("assembly_components"); break;
            case 650:
                if (_view.Weapons().Count != 1 || _view.Gems().Count != 0 || FieldView.GemsOf(_view.Weapons()[0]).Count != 2
                    || _view.Parts(null).Count != 0 || _view.Parts(null, grips: true).Count != 0)
                { Finish("YES did not consume both loose parts and both selected gems exactly once"); return; }
                break;
            case 740:
                if (_view.Weapons().Count != 1 || _view.Weapons()[0]["name"].AsString() != _view.ItemName(_view.Weapons()[0]["blade"].AsGodotDictionary()["id"].AsInt32()))
                { Finish("Cancel change after assembly did not retain the weapon with its blade name"); return; }
                GD.Print("FIELD: assembly grip-first/blade-first, NO/Cancel, two gems, YES ownership and default naming passed");
                Finish(""); return;
        }
        if (_frame > 2400) Finish("the assembly menu did not settle");
    }

    /// <summary>The disc keyboard's replace, insert, delete, backspace, caret, confirmation and twenty-cell limit.</summary>
    private void StepWorkshopName()
    {
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (!_view.Controlled) { if (_frame % 30 == 0) Tap("ps_circle"); return; }
        switch (++_nameFrame)
        {
            case 1: _nameOriginal = _view.Weapons()[0]["name"].AsString(); Tap("ps_triangle"); break;
            case 20: if (!_view.MenuOpen) { _nameFrame--; if (_frame % 20 == 0) Tap("ps_triangle"); } break;
            case 30 or 80 or 160 or 180 or 210 or 250 or 350 or 390 or 510 or 585 or 625 or 1065: Tap("ps_circle"); break;
            case 60 or 110 or 125 or 140 or 490 or 550 or 565: Tap("ps_down"); break;
            case 230: Tap("ps_right"); break;
            case 270: Tap("ps_triangle"); break;
            case 290: Tap("ps_square"); break;
            case 310 or 450: Tap("ps_cross"); break;
            case 330: Tap("ps_l1"); break;
            case 370: Tap("ps_r1"); break;
            case 410 or 470 or 530 or 1045: Tap("ps_start"); break;
            case 200: Capture("name_keyboard"); break;
            case 430:
                if (!_view.ItemHelpShown.Text.Contains("BnBango", StringComparison.Ordinal) || _view.Weapons()[0]["name"].AsString() != _nameOriginal)
                { Finish($"the naming edits or confirmation changed the weapon early: {_view.ItemHelpShown.Text}"); return; }
                Capture("name_confirmation"); break;
            case 605:
                if (_view.Weapons()[0]["name"].AsString() != _nameOriginal) { Finish("Cancel change did not keep the original weapon name"); return; }
                break;
            case 1085:
                if (_view.Weapons()[0]["name"].AsString() != new string('A', 20)) { Finish("the naming keyboard did not commit all twenty cells"); return; }
                GD.Print("FIELD: disc naming keyboard, replace/insert/delete/backspace, L1/R1 caret, No/Cross/Cancel and twenty-cell YES passed");
                Finish(""); return;
        }
        if (_nameFrame is >= 645 and <= 1025 && (_nameFrame - 645) % 20 == 0) Tap("ps_circle");
        if (_frame > 2400) Finish("the naming menu did not settle");
    }

    /// <summary>Combining must show a preview and preserve both inputs until YES, including NO, Cross and CANCEL COMBINE.</summary>
    private void StepWorkshopCombine()
    {
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (!_view.Controlled) { if (_frame % 30 == 0) Tap("ps_circle"); return; }
        if (!_view.Workshop) { Finish("combine check needs a workshop"); return; }
        switch (++_combineFrame)
        {
            case 1:
                _combineFirst = _view.Parts(null, includeSet: true)[0];
                var record = _combineFirst["record"].AsGodotDictionary().Duplicate(true);
                record["slot"] = 13;
                _combineSecond = new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = record };
                _view.Equipment.Add(_combineSecond);
                _combineBefore = Json.Stringify(_view.Equipment);
                _view.Combine(_combineFirst!, _combineSecond!, out _combinePreview, commit: false);
                if (_combinePreview == null) { Finish("combine fixture has no valid result"); return; }
                Tap("ps_triangle");
                break;
            case 20:
                if (!_view.MenuOpen) { _combineFrame--; if (_frame % 20 == 0) Tap("ps_triangle"); }
                break;
            case 30 or 80 or 190 or 210 or 230 or 250 or 290 or 330 or 370 or 410 or 430 or 450 or 470 or 510: Tap("ps_circle"); break;
            case 60 or 110 or 125 or 140 or 155 or 170 or 390: Tap("ps_down"); break;
            case 350: Tap("ps_cross"); break;
            case 490: Tap("ps_up"); break;
            case 270:
            case 345:
            case 385:
                if (Json.Stringify(_view.Equipment) != _combineBefore || _view.Unarmed) { Finish("combining consumed or unequipped an input before YES"); return; }
                if (!_view.ItemRows.Select(r => r.Name).SequenceEqual(new[] { "YES", "NO", "CANCEL COMBINE" }))
                { Finish($"combining did not show the result confirmation: {string.Join(';', _view.ItemRows.Select(r => r.Name))}"); return; }
                Capture("combine_preview_" + _combineFrame);
                break;
            case 530:
                if (_view.Equipment.Contains(_combineFirst!) || _view.Equipment.Contains(_combineSecond!)
                    || !_view.Unarmed || _view.Weapons().Count != 0
                    || !_view.Equipment.Any(v => v.AsGodotDictionary().RecursiveEqual(_combinePreview!)))
                { Finish($"YES did not consume both inputs, disassemble the equipped weapon and keep the previewed result: first {_view.Equipment.Contains(_combineFirst!)}, second {_view.Equipment.Contains(_combineSecond!)}, unarmed {_view.Unarmed}"); return; }
                // A weapon found whole uses nested parts rather than inventory slots. Its gem and grip must
                // return to the bag when that blade is combined, with no dismantling during preview.
                var bladeEntry = _view.Parts(null)[0];
                var gripEntry = _view.Equipment.Select(v => v.AsGodotDictionary()).First(e => e["kind"].AsString() == "grips");
                _view.Assemble(bladeEntry, gripEntry);
                var holder = _view.Weapons()[0];
                holder["grip"].AsGodotDictionary()["gem_slots"] = 1;
                var gem = new Godot.Collections.Dictionary { ["kind"] = "gem", ["record"] = new Godot.Collections.Dictionary { ["id"] = 269 } };
                _view.Equipment.Add(gem); _view.Attach(holder, gem); _view.Equip(holder);
                var nested = _view.Parts(null, includeSet: true).First(e => e.ContainsKey("assembly"));
                var other = new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = nested["record"].AsGodotDictionary().Duplicate(true) };
                other["record"].AsGodotDictionary()["slot"] = 14;
                _view.Equipment.Add(other);
                string intact = Json.Stringify(_view.Equipment);
                _view.Combine(nested, other, out var nestedPreview, commit: false);
                if (nestedPreview == null || _view.Unarmed || Json.Stringify(_view.Equipment) != intact)
                { Finish("preview dismantled a nested equipped weapon"); return; }
                _view.Combine(nested, other, out var nestedMade);
                if (!_view.Unarmed || _view.Weapons().Count != 0 || _view.Gems().Count != 1
                    || _view.Equipment.Contains(holder["entry"]) || nestedMade == null || !nestedMade.RecursiveEqual(nestedPreview))
                { Finish("combining a nested equipped blade lost its gem/grip or differed from preview"); return; }
                foreach (var (kind, id) in new[] { ("shield", 127), ("armour", 159) })
                {
                    var kinds = _view.Content.Data("combining")[kind == "shield" ? "shields" : "armour"].AsGodotDictionary()["kinds"].AsGodotArray();
                    var a = kinds[id - 126].AsGodotDictionary().Duplicate(true);
                    a["id"] = id; a["material"] = 3; a["dp"] = a["max_dp"] = 1000; a["gem_slots"] = 1;
                    var first = new Godot.Collections.Dictionary { ["kind"] = kind, ["record"] = a };
                    var second = new Godot.Collections.Dictionary { ["kind"] = kind, ["record"] = a.Duplicate(true) };
                    second["record"].AsGodotDictionary()["slot"] = 15;
                    _view.Equipment.Add(first); _view.Equipment.Add(second); _view.Wear(a);
                    int gems = _view.Gems().Count;
                    if (kind == "shield")
                    {
                        var jewel = new Godot.Collections.Dictionary { ["kind"] = "gem", ["record"] = new Godot.Collections.Dictionary { ["id"] = 270 } };
                        _view.Equipment.Add(jewel);
                        _view.Attach(new Godot.Collections.Dictionary { ["entry"] = first, ["shield"] = true, ["name"] = "test" }, jewel);
                    }
                    string untouched = Json.Stringify(_view.Equipment);
                    _view.Combine(first, second, out var shown, commit: false);
                    if (shown == null || !_view.IsWorn(first) || Json.Stringify(_view.Equipment) != untouched)
                    { Finish($"preview changed equipped {kind}"); return; }
                    _view.Combine(first, second, out var made);
                    if (_view.IsWorn(first) || _view.Equipment.Contains(first) || _view.Equipment.Contains(second)
                        || made == null || !made.RecursiveEqual(shown) || _view.Gems().Count != gems + (kind == "shield" ? 1 : 0))
                    { Finish($"combining equipped {kind} did not unequip, return gems and commit its preview"); return; }
                }
                GD.Print("FIELD: Combine preview, NO, Cross, CANCEL COMBINE and YES transaction, including an equipped weapon's disassembly, passed");
                Finish("");
                break;
        }
        if (_frame > 1800) Finish("combine menu did not settle");
    }

    /// <summary>Repair's confirmation, cancellation and all-carried transaction through SETUP's pad controls.</summary>
    private void StepWorkshopRepair()
    {
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (!_view.Controlled) { if (_frame % 30 == 0) Tap("ps_circle"); return; }
        if (_view.Battle?.Ashley is not { } ashley || !_view.Workshop) { Finish("repair check needs a workshop and Ashley"); return; }
        switch (++_repairFrame)
        {
            case 1:
            {
                ashley.Blade.Wear(700);
                ashley.Blade.Gain(70);
                (_repairDp, _repairPp) = (ashley.Blade.Dp, ashley.Blade.Pp);
                var source = _view.Weapons()[0]["blade"].AsGodotDictionary();
                _repairLoose = source.Duplicate(true);
                _repairLoose["slot"] = 15;
                _repairLoose["material"] = 4; // Iron at Work of Art, whose combining mask only allows wood/leather/bronze.
                _repairLoose["dp"] = 1300; _repairLoose["max_dp"] = 2000; _repairLoose["pp"] = 70;
                _view.Equipment.Add(new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = _repairLoose });
                _repairShield = new Godot.Collections.Dictionary { ["id"] = 127, ["category"] = 6, ["dp"] = 100, ["max_dp"] = 1000, ["pp"] = 2 };
                _repairArmour = new Godot.Collections.Dictionary { ["id"] = 159, ["category"] = 4, ["dp"] = 1000, ["max_dp"] = 2000, ["pp"] = 100 };
                _view.Equipment.Add(new Godot.Collections.Dictionary { ["kind"] = "shield", ["record"] = _repairShield });
                _view.Equipment.Add(new Godot.Collections.Dictionary { ["kind"] = "armour", ["record"] = _repairArmour });
                _repairAccessory = new Godot.Collections.Dictionary { ["id"] = 207, ["category"] = 7, ["dp"] = 1300, ["max_dp"] = 2000, ["pp"] = 70 };
                _view.Equipment.Add(new Godot.Collections.Dictionary { ["kind"] = "armour", ["record"] = _repairAccessory });
                _repairStored = source.Duplicate(true);
                _repairStored["dp"] = 1300; _repairStored["max_dp"] = 2000; _repairStored["pp"] = 70;
                _view.Stored.Add(new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = _repairStored });
                Tap("ps_triangle");
                break;
            }
            case 20:
                if (!_view.MenuOpen)
                {
                    if (++_repairWait > 600) { Finish($"the workshop menu did not open at {ashley.Actor.Position}, busy {_view.Battle.Busy}"); return; }
                    if (_repairWait % 10 == 0) Tap("ps_triangle");
                    _repairFrame--;
                }
                break;
            case 30: Tap("ps_circle"); break;
            case 50:
            {
                var mounts = _view.FindChildren("Weapon", "BoneAttachment3D", true, false).OfType<BoneAttachment3D>().ToList();
                if (mounts.Count < 2 || mounts.Any(m => m.BoneIdx < 0 || string.IsNullOrEmpty(m.BoneName)))
                { Finish("the carried weapon lost its bone attachment in the item menu's model copy"); return; }
                break;
            }
            case 60: Tap("ps_down"); break;
            case 80: Tap("ps_circle"); break;
            case 110 or 125 or 140 or 155: Tap("ps_down"); break;
            case 180: Tap("ps_circle"); break;
            case 200:
            case 280:
            case 340:
                if (!_view.ItemHelpShown.Caution || !_view.ItemHelpShown.Text.StartsWith("Repair all equipment", StringComparison.Ordinal)) { Finish($"Repair did not ask for confirmation at {_repairFrame}: {_view.ItemHelpShown}, page {_view.MenuPage}, row {_view.MenuRow}, rows {string.Join(';', _view.ItemRows.Select(r => r.Name))}"); return; }
                Capture("repair_confirm_" + _repairFrame);
                break;
            case 210: Tap("ps_down"); break;
            case 230: Tap("ps_circle"); break;
            case 250:
            case 310:
                if (ashley.Blade.Dp != _repairDp || _repairLoose!["dp"].AsInt32() != 1300) { Finish("declining or cancelling Repair changed equipment"); return; }
                break;
            case 260 or 320 or 350: Tap("ps_circle"); break;
            case 290: Tap("ps_cross"); break;
            case 380:
                if (ashley.Blade.Dp != ashley.Blade.MaxDp || ashley.Blade.Pp != Math.Max(0, _repairPp - 7) || _repairLoose!["dp"].AsInt32() != 2000 || _repairLoose["pp"].AsInt32() != 63) { Finish("Repair did not repair all carried blades once, including iron, for the retail PP cost"); return; }
                if (_repairShield!["dp"].AsInt32() != 1000 || _repairShield["pp"].AsInt32() != 0 || _repairArmour!["dp"].AsInt32() != 2000 || _repairArmour["pp"].AsInt32() != 90) { Finish("Repair did not restore shields and armour or clamp PP at zero"); return; }
                if (_repairAccessory!["dp"].AsInt32() != 1300 || _repairStored!["dp"].AsInt32() != 1300) { Finish("Repair changed accessories or stored equipment"); return; }
                if (!_view.ItemHelpShown.Caution || !_view.ItemHelpShown.Text.StartsWith("All equipment repaired", StringComparison.Ordinal)) { Finish("Repair did not show the retail completion message"); return; }
                Capture("repair_complete");
                break;
            case 410: Tap("ps_circle"); break;
            case 430 or 450 or 470: Tap("ps_cross"); break;
            case 490:
                if (_view.MenuOpen) { Finish("Repair did not return through SETUP to play"); return; }
                GD.Print("FIELD: Repair confirmation, NO, Cross, all carried blades/shields/armour, PP cost and floor, iron, accessories and stored equipment passed");
                Finish("");
                break;
        }
    }

    /// <summary>Explores doors and fights through the normal pad path, preserving one New Game's earned state.</summary>
    private void StepCampaign()
    {
        if (_view.Battle != null && _view.Battle.Combatants.Values.Any(c => !GodotObject.IsInstanceValid(c.Actor.Node)))
        { Finish("campaign: battle retained a deleted actor's scene nodes"); return; }
        if (_frame == 1 && _room == null && _campaignLoad.Length == 0 && (_view.Flags[1] != 1 || _view.Flags[0x11C] != 1))
        { Finish("campaign: New Game did not initialize retail's normal-play flags"); return; }
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (_capture.Length != 0 && _frame % 120 == 0) Capture($"campaign_frame_{_frame:d7}");
        if (_view.Rooms.Count != _campaignRooms)
        {
            _campaignRooms = _view.Rooms.Count;
            _campaignHiddenPositions.Clear();
            _campaignHops.Clear();
            _campaignBypassEnemies = false;
            _campaignBoarding = false;
            _campaignResting = false;
            _fightTargetId = "";
            _fightProgressAt = _frame;
            _campaignDoor = -1;
            _campaignPuzzle = 0;
            _campaignHallAt = 0;
            _campaignLootAt = _campaignLootStart = _campaignMinePerch = _campaignBoardingMisses = 0;
            _campaignPuzzleAt = _frame;
            var room = (_view.Zone, _view.Map);
            if (_campaignRouteAt + 1 < _campaignRoute.Count && _campaignRoute[_campaignRouteAt + 1] == room) _campaignRouteAt++;
            _campaignVisits[room] = _campaignVisits.GetValueOrDefault(room) + 1;
            GD.Print($"CAMPAIGN: entered {room}, visits {_campaignVisits[room]}, frame {_frame}, story {_view.Flags[0x82]}");
            if (_campaignVisits[room] > 4) { Finish($"campaign: route driver repeated {room} five times without advancing beyond its obstacles"); return; }
            GD.Print($"CAMPAIGN: blocks on entry {string.Join(';', _view.CrateObjects.Select(c => $"{c.Id}:{c.X},{c.Z},{c.Bottom}"))}; flag 0 {_view.Flags[0]}");
            Capture($"campaign_{_campaignRooms:d4}_{_view.Zone:d3}_{_view.Map:d2}");
        }
        if (!_view.Controlled && _frame % 600 == 0)
            GD.Print($"CAMPAIGN: scene {_view.Zone},{_view.Map}, node {_view.EventNode}, {_view.WaitingOn}");
        if (_view.Battle?.GameOver == true) { Finish($"campaign: Ashley fell in {_view.Zone},{_view.Map} after {_campaignRooms} room entries"); return; }
        // A defeated enemy can open the gadget bag during recovery. Collect its actual drops before menu navigation.
        if (_view.MenuOpen && _view.MenuPage == 11)
        {
            Steer(Vector2.Zero);
            if (_campaignHealStep > 0) { _campaignHealStart++; _campaignHealAt++; }
            if (_press == 0 && _frame % 8 == 0) Tap(++_campaignBagTries <= 2 ? "ps_square" : "ps_cross");
            return;
        }
        _campaignBagTries = 0;
        // Reclaimed abilities can interrupt a long fight, including a retreat for RISK recovery.
        if (_view.MenuOpen && _view.MenuPage == 12)
        {
            Steer(Vector2.Zero);
            if (_campaignHealStep > 0) { _campaignHealStart++; _campaignHealAt++; }
            if (_press == 0 && _frame % 8 == 0) Tap("ps_circle");
            return;
        }

        if (_view.Controlled && _view.Battle is { Ashley: { } observed } observedBattle)
        {
            // A room script may hide a live enemy (Sackheim during Goodwin's encounter). Its AI must stop too.
            foreach (var c in observedBattle.Combatants.Values.Where(c => c.Hostile && !c.Dead))
                if (!c.Actor.Visible && c.HopState == 0 && !_view.Materialising(c.Actor)
                    && (c.Actor.Waiting & (EventActor.Routing | EventActor.Moving)) == 0 && c.Mind?.Script == null)
                {
                    if (_campaignHiddenPositions.TryGetValue(c.Actor.Id, out var held) && held.DistanceTo(c.Actor.Position) > 0.01f)
                    { Finish($"campaign: hidden {c.Name} moved from {held} to {c.Actor.Position}"); return; }
                    _campaignHiddenPositions[c.Actor.Id] = c.Actor.Position;
                }
                else _campaignHiddenPositions.Remove(c.Actor.Id);
            foreach (var c in observedBattle.Combatants.Values.Where(c => c.Hostile && !c.Dead))
            {
                if (c.HopState != 0 && (c.Actor.Flags & 1) != 0)
                {
                    if (!_campaignHops.ContainsKey(c.Actor.Id)) _campaignHops[c.Actor.Id] = 0;
                    if (!observedBattle.Busy && !observedBattle.Paused && !_view.MenuOpen && !c.Suspended && ++_campaignHops[c.Actor.Id] > 240)
                    { Finish($"campaign: {c.Name}'s vanished warp did not reappear in 240 active frames"); return; }
                }
                else if (c.HopState == 0 && _campaignHops.Remove(c.Actor.Id)) GD.Print($"CAMPAIGN: {c.Name} completed its vanish/warp/reappearance");
            }
            if (_frame % 600 == 0)
                GD.Print($"CAMPAIGN: combat HP {observed.Hp}/{observed.MaxHp}, RISK {observed.Risk}, status {observed.Status:x8}, at {observed.Actor.Position}, facing {observed.Actor.Facing}, motion {observed.Actor.Player?.CurrentAnimation}, local flag 48 {_view.Flags[48]}, enemies {string.Join(';', observedBattle.Combatants.Values.Where(c => c.Hostile && !c.Dead).Select(c => $"{c.Name}:{c.Hp}/{c.MaxHp} at {c.Actor.Position}, MP {c.Mp}/{c.MaxMp}, shown {c.Actor.Visible}, hostile {c.Hostile}, suspended {c.Suspended}, dead {c.Dead}"))}");
        }
        if (_frame > 60 * 60 * 240) { Finish($"campaign: four hours elapsed in {_view.Zone},{_view.Map}; {_view.WaitingOn}"); return; }
        // HP reaches zero before the status tick dispatches the defeat event. Do not save or open menus in that gap.
        if (_view.Controlled && _view.Battle is { } dying && dying.Combatants.Values.Any(c => c.Hostile && !c.Dead && c.Hp == 0))
        {
            if (_campaignDeathAt == 0) _campaignDeathAt = _frame;
            if (_frame - _campaignDeathAt > 600) { Finish("campaign: the pending enemy death did not reach its status tick"); return; }
            Steer(Vector2.Zero);
            if (dying.Paused && _press == 0) Tap("ps_cross");
            if (dying.Busy || dying.Paused || _view.MenuOpen) _campaignDeathAt++;
            return;
        }
        _campaignDeathAt = 0;
        // A continuous run repairs its actually worn equipment through SETUP, spending its actual PP.
        // Unlike the focused repair fixture, this neither changes wear beforehand nor creates equipment.
        if (_campaignRepair && !_campaignRepaired && _view.Workshop && _view.Controlled && _view.Battle?.Ashley is { } repairing)
        {
            Steer(Vector2.Zero);
            switch (++_campaignRepairFrame)
            {
                case 1:
                    (_repairDp, _repairPp) = (repairing.Blade.Dp, repairing.Blade.Pp);
                    GD.Print($"CAMPAIGN: workshop repair starts at DP {_repairDp}/{repairing.Blade.MaxDp}, PP {_repairPp}");
                    Tap("ps_triangle"); break;
                case 20:
                    if (!_view.MenuOpen)
                    {
                        if (++_repairWait > 600) { Finish("campaign: workshop menu did not open"); return; }
                        if (_repairWait % 10 == 0) Tap("ps_triangle");
                        _campaignRepairFrame--;
                    }
                    break;
                case 30:
                    if (_view.MenuRow != 3) { Tap("ps_down"); _campaignRepairFrame -= 8; }
                    else Tap("ps_circle");
                    break;
                case 60: Tap("ps_down"); break;
                case 80: Tap("ps_circle"); break;
                case 110 or 125 or 140 or 155: Tap("ps_down"); break;
                case 180: Tap("ps_circle"); break;
                case 200:
                    if (!_view.ItemHelpShown.Caution || !_view.ItemHelpShown.Text.StartsWith("Repair all equipment", StringComparison.Ordinal))
                    { Finish($"campaign: workshop did not ask to repair all equipment: {_view.ItemHelpShown}"); return; }
                    break;
                case 210: Tap("ps_circle"); break;
                case 240:
                    if (repairing.Blade.Dp != repairing.Blade.MaxDp || repairing.Blade.Pp != Math.Max(0, _repairPp - (repairing.Blade.MaxDp - _repairDp) / 100))
                    { Finish("campaign: workshop repair did not restore the worn blade for its PP cost"); return; }
                    GD.Print($"CAMPAIGN: repaired earned equipment to DP {repairing.Blade.Dp}/{repairing.Blade.MaxDp}, PP {repairing.Blade.Pp}");
                    Capture("campaign_repaired"); break;
                case 270: Tap("ps_circle"); break;
                case 290 or 310 or 330: Tap("ps_cross"); break;
                case 350:
                    if (_view.MenuOpen) { Finish("campaign: workshop repair did not return to play"); return; }
                    _campaignRepaired = true; break;
            }
            return;
        }
        // Saving at an earned checkpoint can also go through its actual magic circle and DATA screens.
        if (_campaignSaveUi && !_campaignUiSaved && _campaignCheckpoint == (_view.Zone, _view.Map)
            && (!_campaignRepair || _campaignRepaired) && _view.Controlled && _view.Battle?.HostileCount() == 0)
        {
            if (_campaignUiAt == 0) _campaignUiAt = _frame;
            if (_frame - _campaignUiAt > 2400) { Finish($"campaign: DATA save step {_campaignUiStep} stalled at {_view.CardStage}, page {_view.MenuPage}"); return; }
            if (_campaignUiStep == 0)
            {
                var circle = _view.Document["traps"].AsGodotArray().Select(v => v.AsGodotDictionary()).FirstOrDefault(t => t["type"].AsInt32() == 16);
                if (circle == null) { Finish("campaign: the checkpoint has no magic circle for DATA's SAVE"); return; }
                var circleTile = circle["tile"].AsGodotArray();
                var point = new Vector3(circleTile[0].AsInt32() * 128 + 64, 0, circleTile[1].AsInt32() * 128 + 64);
                if (_view.Player is not { } saver) return;
                if (new Vector2(point.X - saver.Position.X, point.Z - saver.Position.Z).Length() < 12 && _view.SavePoint == circle["location"].AsInt32())
                {
                    Steer(Vector2.Zero);
                    _campaignUiGeneration = FieldView.ReadCardFile(1, 1)?.GetValueOrDefault("generation", 0).AsInt32() ?? 0;
                    Tap("ps_triangle"); _campaignUiStep = 1;
                }
                else
                {
                    var path = _view.Path(saver.Position, point);
                    while (path.Count > 1 && new Vector2(path[0].X - saver.Position.X, path[0].Z - saver.Position.Z).Length() < 16) path.RemoveAt(0);
                    var next = path.Count > 0 ? path[0] : point;
                    float turn = (saver.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                    Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
                }
                return;
            }
            Steer(Vector2.Zero);
            if (_press > 0 || _frame % 8 != 0) return;
            switch (_campaignUiStep)
            {
                case 1:
                    if (_view.MenuOpen) _campaignUiStep = 2;
                    else if (_view.Battle is { Busy: false, EnemyActing: false }) Tap("ps_triangle");
                    break;
                case 2:
                    if (_view.MenuPage == 6) _campaignUiStep = 3;
                    else if (_view.MenuPage >= 0) Tap("ps_cross");
                    else if (_view.MenuRow != 6) Tap("ps_down");
                    else Tap("ps_circle");
                    break;
                case 3: Tap("ps_circle"); _campaignUiStep = 4; break;
                case 4:
                    if (_view.CardStage == "Slots") { Tap("ps_circle"); _campaignUiStep = 5; }
                    break;
                case 5:
                    if (_view.CardStage == "Files") { Tap("ps_circle"); _campaignUiStep = 6; }
                    break;
                case 6:
                    if (_view.CardStage == "Overwrite") { Tap("ps_up"); _campaignUiStep = 7; }
                    else if (_view.CardStage.StartsWith("Saving", StringComparison.Ordinal) || _view.CardStage.StartsWith("Saved", StringComparison.Ordinal)) _campaignUiStep = 8;
                    break;
                case 7:
                    if (_view.CardStage == "Overwrite") { Tap("ps_circle"); _campaignUiStep = 8; }
                    break;
                case 8:
                    if (_view.CardStage != "Saved") break;
                    var saved = FieldView.ReadCardFile(1, 1);
                    if (saved == null || saved["generation"].AsInt32() <= _campaignUiGeneration || saved["location"].AsInt32() != _view.SavePoint
                        || saved["zone"].AsInt32() != _view.Zone || saved["map"].AsInt32() != _view.Map)
                    { Finish("campaign: DATA's SAVE did not write the earned state at this magic circle"); return; }
                    Capture("campaign_save_ui_saved");
                    Tap("ps_circle"); _campaignUiStep = 9;
                    break;
                case 9:
                    if (_view.CardStage == "Saved") Tap("ps_circle");
                    else if (_view.CardStage.Length == 0)
                    {
                        if (_view.MenuOpen) Tap("ps_cross");
                        else { _campaignUiSaved = true; GD.Print($"CAMPAIGN: saved earned state through DATA at magic circle {_view.SavePoint}, card 1 file 1"); }
                    }
                    break;
            }
            return;
        }
        // This mine gate gives fifteen seconds to reach its approach tile and stop; pushing into the
        // closed door never satisfies its standing-still condition. Leave its timer and flags to the script.
        if (_view.Controlled && _view.Battle is { Ashley: { } gateRunner } && (_view.Zone, _view.Map) == (50, 12) && _campaignRouteAt + 1 < _campaignRoute.Count
            && _campaignRoute[_campaignRouteAt + 1] == (50, 13))
        {
            if (_view.Flags[66] != 2) { Finish($"campaign: the mine gate timer expired at {gateRunner.Actor.Position}"); return; }
            var point = new Vector3(1856, 0, 192);
            if (new Vector2(point.X - gateRunner.Actor.Position.X, point.Z - gateRunner.Actor.Position.Z).Length() < 12)
            { Steer(Vector2.Zero); return; }
            float turn = (gateRunner.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            Input.ActionRelease("ps_r2");
            if (_frame % 60 == 1) Tap("ps_square");
            return;
        }
        // Put the reclaimed Raging Ache on Circle through BATTLE ABILITIES, keeping the other earned slots.
        if (_view.Controlled && _campaignHealStep == 0 && _view.Battle is { } abilityBattle
            && (_campaignAbilityStep > 0 || _view.Abilities.Contains(26) && _view.ChainButtons[0] != 26))
        {
            Steer(Vector2.Zero);
            if (_press > 0 || _frame % 8 != 0) return;
            switch (_campaignAbilityStep)
            {
                case 0:
                    if (abilityBattle.Paused) Tap("ps_cross");
                    else if (!abilityBattle.Busy && !abilityBattle.EnemyActing) { Tap("ps_triangle"); _campaignAbilityStep = 1; }
                    break;
                case 1:
                    if (!_view.MenuOpen) _campaignAbilityStep = 0;
                    else if (_view.MenuPage == 2) { Tap("ps_circle"); _campaignAbilityStep = 2; _campaignAbilitySelection = 0; }
                    else if (_view.MenuRow != 2) Tap(_view.MenuRow < 2 ? "ps_down" : "ps_up");
                    else Tap("ps_circle");
                    break;
                case 2:
                    int row = _view.Abilities.FindAll(id => id is >= 22 and < 40).IndexOf(26);
                    if (_campaignAbilitySelection < row) { Tap("ps_down"); _campaignAbilitySelection++; }
                    else { Tap("ps_circle"); _campaignAbilityStep = 3; }
                    break;
                case 3:
                    if (_view.MenuOpen) Tap("ps_cross");
                    else { _campaignAbilityStep = 0; GD.Print($"CAMPAIGN: assigned earned chain abilities {string.Join(',', _view.ChainButtons)} through BATTLE ABILITIES"); }
                    break;
            }
            return;
        }
        if (_campaignHealStep == 0 && _view.Controlled && _view.Battle is { Ashley: { } restRunner } restBattle)
        {
            if (_campaignResting && _view.MenuOpen) { Steer(Vector2.Zero); if (_frame % 8 == 0) Tap("ps_cross"); return; }
            // Once the earned RISK roots run out, sheathe and let the native recovery clock lower RISK.
            // Continuing to swing at a zero-percent preview can otherwise keep a valid fight going indefinitely.
            if (!_campaignResting && restRunner.Risk >= ((_view.Zone, _view.Map) == (50, 17) ? 65 : 50) && restBattle.HostileCount() > 0
                && new[] { 331, 332, 333 }.All(id => _view.Items.GetValueOrDefault(id) == 0))
            {
                _campaignResting = true;
                _campaignRestPoint = restRunner.Actor.Position;
                if ((_view.Zone, _view.Map) == (50, 17))
                    _campaignRestPoint = new[] { new Vector3(320, 0, 448), new Vector3(320, 0, 1984), new Vector3(1344, 0, 1984) }
                        .MaxBy(p => restBattle.Combatants.Values.Where(c => c.Targetable).Min(c => p.DistanceTo(c.Actor.Position)));
                GD.Print($"CAMPAIGN: resting with no RISK roots, RISK {restRunner.Risk}"); }
            if (_campaignResting)
            {
                if (restRunner.Risk <= 15) { _campaignResting = false; GD.Print($"CAMPAIGN: native recovery lowered RISK to {restRunner.Risk}"); }
                else
                {
                    // Keep ahead of the pursuing elemental instead of waiting at a corner for its next cast.
                    if ((_view.Zone, _view.Map) == (50, 17) && restBattle.Combatants.Values.Any(c => c.Targetable && c.Actor.Position.DistanceTo(_campaignRestPoint) < 900))
                        _campaignRestPoint = new[] { new Vector3(320, 0, 448), new Vector3(320, 0, 1984), new Vector3(1344, 0, 1984) }
                            .MaxBy(p => restBattle.Combatants.Values.Where(c => c.Targetable).Min(c => p.DistanceTo(c.Actor.Position)));
                    var path = _view.Path(restRunner.Actor.Position, _campaignRestPoint, 256);
                    while (path.Count > 1 && new Vector2(path[0].X - restRunner.Actor.Position.X, path[0].Z - restRunner.Actor.Position.Z).Length() < 24) path.RemoveAt(0);
                    var next = path.Count > 0 ? path[0] : _campaignRestPoint;
                    float heading = (restRunner.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                    Steer(restRunner.Actor.Position.DistanceTo(_campaignRestPoint) < 24 ? Vector2.Zero : new Vector2(Mathf.Sin(heading), -Mathf.Cos(heading)));
                    _fightProgressAt++;
                    if (restBattle.Paused || !restBattle.Busy && restBattle.Drawn) Tap("ps_cross");
                    if (!(restRunner.Hp < restRunner.MaxHp * 3 / 4)) return;
                }
            }
        }
        // Use earned recovery and the owned Pyro Guard against the mine elemental, through the normal menus.
        bool mineDebuff = (_view.Zone, _view.Map) == (50, 17) && _view.Battle is { Ashley: { } caster } mineFight && caster.Mp >= 7
            && (_view.Spells.Contains(54) || _view.Items.GetValueOrDefault(432) > 0)
            && mineFight.Combatants.Values.Any(c => c.Targetable && !c.Has(FieldBattle.Degenerate) && c.Actor.Position.DistanceTo(caster.Actor.Position) < 400);
        if (_view.Controlled && _view.Battle is { Ashley: { } patient } recovery && _campaignHealStep == 0 && (patient.Hp < patient.MaxHp * 3 / 4 || patient.Risk > 60 || mineDebuff
                || patient.Has(FieldBattle.Paralysis) && _view.Items.GetValueOrDefault(339) > 0
                || patient.Has(FieldBattle.Poison) && _view.Items.GetValueOrDefault(340) > 0
                || patient.Has(FieldBattle.Numbness) && _view.Items.GetValueOrDefault(341) > 0
                || ((_view.Zone, _view.Map) == (50, 17) && recovery.HostileCount() > 0 && ((_view.Spells.Contains(69) || _view.Items.GetValueOrDefault(452) > 0) ? !patient.Has(22)
                        : !patient.Has(24) && (_view.Spells.Contains(71) || _view.Items.GetValueOrDefault(454) > 0)))))
        {
            bool life = patient.Hp < patient.MaxHp * 3 / 4;
            bool frost = _view.Spells.Contains(69) || _view.Items.GetValueOrDefault(452) > 0;
            bool guard = !life && (_view.Zone, _view.Map) == (50, 17) && recovery.HostileCount() > 0 && (frost ? !patient.Has(22) : !patient.Has(24));
            bool weakening = !life && mineDebuff;
            _campaignHealSpell = weakening ? 54 : guard ? frost ? 69 : 71 : 96;
            int cost = _view.Content.Data("spells")["actions"].AsGodotDictionary()[_campaignHealSpell.ToString()].AsGodotDictionary()["cost"].AsInt32();
            _campaignHealMagic = (life || guard || weakening) && _view.Spells.Contains(_campaignHealSpell) && patient.Mp >= cost && !patient.Has(FieldBattle.Silence) && !patient.Has(FieldBattle.Paralysis);
            if ((patient.Has(FieldBattle.Paralysis) && _view.Items.GetValueOrDefault(339) > 0)
                || (patient.Has(FieldBattle.Poison) && _view.Items.GetValueOrDefault(340) > 0)
                || (patient.Has(FieldBattle.Numbness) && _view.Items.GetValueOrDefault(341) > 0)) _campaignHealMagic = false;
            if (_campaignHealMagic)
            {
                var classes = _view.Content.Data("magic_menu")["classes"].AsGodotArray();
                for (int i = 0; i < classes.Count; i++)
                {
                    var known = classes[i].AsGodotArray().Select(v => v.AsInt32()).Where(_view.Spells.Contains).ToList();
                    if (!known.Contains(_campaignHealSpell)) continue;
                    _campaignHealClass = i; _campaignHealSpellRow = known.IndexOf(_campaignHealSpell); break;
                }
                (_campaignHealStep, _campaignHealStart, _campaignHealAt, _campaignHealHp, _campaignHealMp) = (1, _frame, _frame, patient.Hp, patient.Mp);
                GD.Print($"CAMPAIGN: seeking learned spell {_campaignHealSpell} at {patient.Hp}/{patient.MaxHp} HP, {patient.Mp} MP");
            }
            else
            {
                int guardItem = frost ? 452 : 454;
                int cure = patient.Has(FieldBattle.Paralysis) ? 339 : patient.Has(FieldBattle.Poison) ? 340 : patient.Has(FieldBattle.Numbness) ? 341 : 0;
                int[] choices = cure != 0 && _view.Items.GetValueOrDefault(cure) > 0 ? new[] { cure }
                    : weakening && _view.Items.GetValueOrDefault(432) > 0 ? new[] { 432 }
                    : guard && _view.Items.GetValueOrDefault(guardItem) > 0 ? new[] { guardItem }
                    : guard && patient.Mp < cost ? new[] { 327, 328, 329, 330 }
                    : !life ? new[] { 331, 332, 333 }
                    : _view.Spells.Contains(96) && patient.Mp < cost ? new[] { 327, 328, 329, 330, 323, 324, 325, 326 }
                    : new[] { 425, 323, 324, 325, 326 };
                foreach (int item in choices)
                    if (_view.Items.GetValueOrDefault(item) > 0)
                    {
                        (_campaignHealStep, _campaignHealStart, _campaignHealAt, _campaignHealItem, _campaignHealCount, _campaignHealHp, _campaignHealMp) = (1, _frame, _frame, item, _view.Items[item], patient.Hp, patient.Mp);
                        GD.Print($"CAMPAIGN: seeking {_view.ItemName(item)} at {patient.Hp}/{patient.MaxHp} HP");
                        break;
                    }
            }
        }
        if (_campaignHealStep > 0 && _view.Battle is { Ashley: { } recovering } healing)
        {
            Steer(Vector2.Zero);
            if (!_view.Controlled)
            {
                _campaignHealStart++; _campaignHealAt++;
                if (_frame % 30 == 0) Tap("ps_circle");
                return;
            }
            if (_frame - _campaignHealStart > 1800) { Finish($"campaign: recovery step {_campaignHealStep} stalled, page {_view.MenuPage}, help {_view.ItemHelpShown}, {_view.SystemBox.Text}"); return; }
            if (_press > 0 || _frame % 8 != 0) return;
            switch (_campaignHealStep)
            {
                case 1:
                    if (_view.MenuOpen) _campaignHealStep = 2;
                    else if (healing.Paused) Tap("ps_cross");
                    else if (!healing.Busy && !healing.EnemyActing) Tap("ps_triangle");
                    break;
                case 2:
                    int page = _campaignHealMagic ? 0 : 3;
                    if (!_view.MenuOpen) _campaignHealStep = 1;
                    else if (_view.MenuPage == page) { _campaignHealStep = _campaignHealMagic ? 7 : 3; _campaignHealSelection = 0; }
                    else if (_view.MenuRow != page) Tap(_view.MenuRow > page ? "ps_up" : "ps_down");
                    else Tap("ps_circle");
                    break;
                case 3:
                {
                    if (!_view.MenuOpen) { _campaignHealStep = 1; break; }
                    string name = _view.ItemName(_campaignHealItem);
                    var row = _view.ItemRows.FirstOrDefault(r => r.Name == name);
                    if (string.IsNullOrEmpty(row.Name)) Tap("ps_l1");
                    else if (_view.ItemHelpShown.Text != row.Help) Tap("ps_down");
                    else { Tap("ps_circle"); _campaignHealStep = 4; _campaignHealAt = _frame; }
                    break;
                }
                case 4:
                    if (_frame - _campaignHealAt >= 16) { Tap("ps_circle"); _campaignHealStep = 5; }
                    break;
                case 5:
                    if (healing.Paused) Tap("ps_circle");
                    if (_campaignHealMagic ? recovering.Mp < _campaignHealMp : _view.Items.GetValueOrDefault(_campaignHealItem) < _campaignHealCount)
                    { _campaignHealStep = 6; _campaignHealAt = _frame; }
                    else if (!healing.Paused && !healing.Busy && _frame - _campaignHealAt > 120)
                    {
                        // A press during a menu transition can leave its page open instead of casting.
                        // Back out before re-evaluating MP, silence and owned items and opening a fresh page.
                        if (_view.MenuOpen) Tap("ps_cross");
                        else _campaignHealStep = 0;
                    }
                    break;
                case 6:
                    if (_view.MenuOpen) { Tap("ps_cross"); break; }
                    if (!healing.Busy && !healing.Paused && !_view.MenuOpen)
                    {
                        GD.Print(_campaignHealMagic ? $"CAMPAIGN: cast learned spell {_campaignHealSpell}, HP {recovering.Hp}/{recovering.MaxHp}, MP {recovering.Mp}"
                            : $"CAMPAIGN: used {_view.ItemName(_campaignHealItem)}, remaining {_view.Items.GetValueOrDefault(_campaignHealItem)}, HP {recovering.Hp}/{recovering.MaxHp}");
                        _campaignHealStep = 0;
                    }
                    break;
                case 7:
                    if (_campaignHealSelection < _campaignHealClass) { Tap("ps_down"); _campaignHealSelection++; }
                    else { Tap("ps_circle"); _campaignHealStep = 8; _campaignHealSelection = 0; }
                    break;
                case 8:
                    if (_campaignHealSelection < _campaignHealSpellRow) { Tap("ps_down"); _campaignHealSelection++; }
                    else { Tap("ps_circle"); _campaignHealStep = 5; _campaignHealAt = _frame; }
                    break;
            }
            return;
        }
        if (!_view.Controlled || _view.MenuOpen)
        {
            Steer(Vector2.Zero);
            if (_frame % 30 == 0) Tap("ps_circle");
            return;
        }
        if (_view.Battle is not { Ashley: { } ashley } battle) { Finish("campaign: no player combatant"); return; }
        if (_campaignLoot == (_view.Zone, _view.Map) && (_campaignLootItem == 0 || _view.Items.GetValueOrDefault(_campaignLootItem) == 0) && _view.Document["chests"].AsGodotArray()
            .Select(v => v.AsGodotDictionary()).FirstOrDefault(c => (c["lock"].AsInt32() == 0 || c["lock"].AsInt32() == 2 && (_view.Spells.Contains(92) || _view.Items.GetValueOrDefault(446) > 0)) && _view.ChestContents(c).Count > 0) is { } treasure)
        {
            if (_campaignLootStart == 0) _campaignLootStart = _frame;
            if (_frame - _campaignLootStart > 3600) { Finish($"campaign: chest approach stalled at {ashley.Actor.Position}"); return; }
            if (battle.Paused || !battle.Busy && battle.Drawn) { Tap("ps_cross"); return; }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            var chestTile = treasure["tile"].AsGodotArray();
            int facing = treasure["facing"].AsInt32();
            var (dx, dz) = (facing / 1024) switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
            var point = new Vector3((chestTile[0].AsInt32() + dx) * 128 + 64, 0, (chestTile[1].AsInt32() + dz) * 128 + 64);
            point.Y = _view.FloorAt(point) ?? ashley.Actor.Position.Y;
            if ((_view.Zone, _view.Map) is (50, 10) or (50, 14) && _campaignPuzzle == 0)
            {
                point = new Vector3(832, 0, 960);
                if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
                { _campaignPuzzle = 1; Steer(Vector2.Zero); return; }
            }
            if (((_view.Zone, _view.Map) is not ((50, 10) or (50, 14)) || _campaignPuzzle > 0) && (_campaignLootAt > 0 || new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16))
            {
                if (_campaignLootAt == 0) _campaignLootAt = _frame;
                float turn = (facing - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                if (_frame - _campaignLootAt < 4) Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
                else if (treasure["lock"].AsInt32() == 2)
                {
                    Steer(Vector2.Zero);
                    _campaignHealSpell = 92; _campaignHealMagic = _view.Spells.Contains(92);
                    _campaignHealItem = 446; _campaignHealCount = _view.Items.GetValueOrDefault(446);
                    (_campaignHealStep, _campaignHealStart, _campaignHealAt, _campaignHealHp, _campaignHealMp) = (1, _frame, _frame, ashley.Hp, ashley.Mp);
                    if (_campaignHealMagic)
                    {
                        var classes = _view.Content.Data("magic_menu")["classes"].AsGodotArray();
                        for (int i = 0; i < classes.Count; i++)
                        {
                            var known = classes[i].AsGodotArray().Select(v => v.AsInt32()).Where(_view.Spells.Contains).ToList();
                            if (!known.Contains(92)) continue;
                            _campaignHealClass = i; _campaignHealSpellRow = known.IndexOf(92); break;
                        }
                    }
                    GD.Print("CAMPAIGN: casting earned Unlock on the chest's magic seal");
                }
                else { Steer(Vector2.Zero); if (_frame % 30 == 0) Tap("ps_cross"); }
                return;
            }
            var path = _view.Path(ashley.Actor.Position, point, 256);
            while (path.Count > 1 && new Vector2(path[0].X - ashley.Actor.Position.X, path[0].Z - ashley.Actor.Position.Z).Length() < 20) path.RemoveAt(0);
            var next = path.Count > 0 ? path[0] : point;
            float chestAngle = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(chestAngle), -Mathf.Cos(chestAngle)));
            var chestProbe = ashley.Actor.Position.MoveToward(next, 96);
            if (_view.FloorAt(chestProbe) is { } chestAhead && ashley.Actor.Position.Y - chestAhead is > FieldPlayer.StepHeight and <= 256) Input.ActionPress("ps_r2");
            else Input.ActionRelease("ps_r2");
            return;
        }
        // The western branch of room 9,13 is reached from the southwest 256-high step: a running jump grabs
        // the 640-high central platform, then Ashley can drop west to the wolf and the story route.
        if ((_view.Zone, _view.Map) == (9, 13) && ashley.Actor.Position.X >= 768 && _campaignPuzzle < 5
            && _campaignCheckpoint is { Zone: 9, Map: 9 }
            && !battle.Combatants.Values.Any(c => c.Targetable && c.Actor.Position.X >= 768))
        {
            if (battle.Paused) { Tap("ps_cross"); return; }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            if (battle.Drawn) { Tap("ps_cross"); return; }
            if (_campaignHallAt == 0) _campaignHallAt = _frame;
            if (_frame - _campaignHallAt > 1800) { Finish($"campaign: western platform step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
            var point = _campaignPuzzle <= 1 ? new Vector3(832, -256, 1600)
                : _campaignPuzzle <= 3 ? new Vector3(832, -640, 1344) : new Vector3(576, -448, 1088);
            if (_campaignPuzzle == 0 && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16 && ashley.Actor.Position.Y <= -256)
            { _campaignPuzzle = 1; _campaignPuzzleAt = _frame; }
            if (_campaignPuzzle == 1)
            {
                Steer(Vector2.Zero);
                if (_frame - _campaignPuzzleAt < 40) return;
                _campaignPuzzle = 2; _campaignPuzzleAt = _frame;
                point = new Vector3(832, -640, 1344);
            }
            if (_campaignPuzzle == 2 && ashley.Actor.Position.Y <= -640)
            { _campaignPuzzle = 3; _campaignPuzzleAt = _frame; }
            if (_campaignPuzzle == 3)
            {
                Steer(Vector2.Zero);
                if (_frame - _campaignPuzzleAt < 40) return;
                _campaignPuzzle = 4; point = new Vector3(576, -448, 1088);
            }
            if (_campaignPuzzle == 2 && _frame - _campaignPuzzleAt > 120 && ashley.Actor.Position.Y > -384)
            { _campaignPuzzle = 0; _campaignPuzzleAt = _frame; return; }
            var steps = _campaignPuzzle == 0 ? _view.Path(ashley.Actor.Position, point, 256) : new List<Vector3>();
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 2 && (_frame - _campaignPuzzleAt) % 60 == 1) Tap("ps_square");
            return;
        }
        if ((_view.Zone, _view.Map) == (9, 17) && _view.Flags[130] >= 10
            && battle.Combatants.Values.Where(c => c.Targetable).All(c => c.Name == "Dummy")) _campaignBypassEnemies = true;
        // Golem's arena starts its introduction when Ashley stops on the central floor.
        if ((_view.Zone, _view.Map) == (16, 0) && _view.Flags[44] == 0 && _view.Flags[190] <= 3)
        {
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: Golem approach stalled at {ashley.Actor.Position}"); return; }
            var point = new Vector3(704, 0, 704);
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
            { Steer(Vector2.Zero); return; }
            var steps = _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            return;
        }
        // The north end of 14,0 starts the Lizardman encounter only after Ashley stops there.
        if ((_view.Zone, _view.Map) == (14, 0) && _view.Flags[60] == 0)
        {
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: north encounter approach stalled at {ashley.Actor.Position}"); return; }
            var point = new Vector3(832, 0, 448);
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
            { Steer(Vector2.Zero); return; }
            var steps = _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            return;
        }
        // The Catacombs ghost appears when Ashley stops in its western hall; running around the room misses it.
        if ((_view.Zone, _view.Map) == (13, 4) && _view.Flags[191] == 0)
        {
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: ghost approach stalled at {ashley.Actor.Position}"); return; }
            var point = new Vector3(1472, -128, 704);
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
            { Steer(Vector2.Zero); return; }
            var steps = _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            return;
        }
        // Returning from the cleared Lizardman encounter, the open return hall can be traversed
        // without attacking its optional platform enemies; they retain their normal AI and damage.
        if (_campaignCheckpoint is { Zone: 15 } && _view.Zone == 13 && _view.Map == 16
            && battle.Combatants.Values.Where(c => c.Targetable).All(c => !c.Marked)) _campaignBypassEnemies = true;
        if (_campaignCheckpoint is { Zone: 42 } && battle.Combatants.Values.Where(c => c.Targetable).All(c => !c.Marked))
            _campaignBypassEnemies = true;
        if (_campaignRoute.Count > 0 && (_campaignItem > 0 || _campaignCheckpoint != (_view.Zone, _view.Map))
            && battle.Combatants.Values.Where(c => c.Targetable).All(c => !c.Marked)) _campaignBypassEnemies = true;
        if (battle.HostileCount() > 0 && !_campaignBypassEnemies)
        {
            // Hall of Struggle's bowman is behind a 448-unit barrier. The three crates at (4,4) provide the
            // jumping platform; attacking his limbs through the barrier correctly fails the line-of-fire test.
            if ((_view.Zone, _view.Map) == (9, 7) && _view.Flags[180] < 2 && ashley.Actor.Position.Z < 1152
                && battle.Combatants.Values.Where(c => c.Targetable).All(c => c.Actor.Position.Z >= 896))
            {
                if (battle.Paused) { Tap("ps_cross"); return; }
                if (battle.Busy) { Steer(Vector2.Zero); return; }
                if (battle.Drawn) { Tap("ps_cross"); return; }
                if (_campaignHallAt == 0) _campaignHallAt = _frame;
                if (_frame - _campaignHallAt > 1800)
                { Finish($"campaign: Hall of Struggle puzzle step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
                var point = _campaignPuzzle switch
                {
                    0 => new Vector3(320, 0, 704), 1 => new Vector3(320, 0, 384),
                    2 => new Vector3(576, 0, 384), 3 => new Vector3(576, 0, 448),
                    4 => new Vector3(576, -384, 576), 6 => new Vector3(576, -448, 832),
                    _ => new Vector3(576, 0, 1152),
                };
                if (_campaignPuzzle < 4 && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
                { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (_campaignPuzzle == 4 && ashley.Actor.Position.Y <= -384)
                { _campaignPuzzle = 5; _campaignPuzzleAt = _frame; }
                if (_campaignPuzzle == 5)
                {
                    Steer(Vector2.Zero);
                    if (_frame - _campaignPuzzleAt < 40) return;
                    _campaignPuzzle = 6; _campaignPuzzleAt = _frame;
                    point = new Vector3(576, -448, 832);
                }
                if (_campaignPuzzle == 6 && ashley.Actor.Position.Z >= 768 && ashley.Actor.Position.Y <= -448) _campaignPuzzle = 7;
                if (_campaignPuzzle == 6 && _frame - _campaignPuzzleAt > 120 && ashley.Actor.Position.Y > -128)
                { _campaignPuzzle = 0; _campaignPuzzleAt = _frame; }
                float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
                if (_campaignPuzzle is 4 or 6 && (_frame - _campaignPuzzleAt) % 60 == 1) Tap("ps_square");
                return;
            }
            if (_frame < _campaignWaitForEnemyUntil) { Steer(Vector2.Zero); return; }
            if (battle.Paused && _campaignTargetText != _view.SystemBox.Text)
            {
                _campaignTargetText = _view.SystemBox.Text;
                GD.Print($"CAMPAIGN: aiming {_campaignTargetText.Replace('\n', '/')} from {ashley.Actor.Position}");
            }
            Fight(battle, ashley, _frame);
            return;
        }
        // Room 9,12's three-high stack has no adjacent raised floor from which to lift its top block.
        // Break one through the targeting sphere, then carry the two surviving blocks to the south ledge.
        if (_view.Zone == 9 && _view.Map is 11 or 12 && battle.HostileCount() == 0
            && _view.Crates.Count(c => c.X == (_view.Map == 11 ? 2 : 0) && c.Z == (_view.Map == 11 ? 2 : 7)) == 3)
        {
            if (_campaignHallAt == 0) _campaignHallAt = _frame;
            if (_frame - _campaignHallAt > 1800) { Finish($"campaign: could not break a stone block in {_view.Zone},{_view.Map}"); return; }
            if (battle.Paused)
            {
                Steer(Vector2.Zero);
                if (_press > 0) return;
                // Room 9,11's bottom wooden crate rolls and cannot be broken. Strike the stone block above it.
                if (_view.Map == 11 && _campaignCubePicks == 0) { Tap("ps_right"); _campaignCubePicks = 1; }
                else Tap("ps_circle");
                return;
            }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            _campaignCubePicks = 0;
            var point = _view.Map == 11 ? new Vector3(320, 0, 448) : new Vector3(192, 0, 960);
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 20)
            { Steer(Vector2.Zero); if (_frame % 20 == 0) Tap("ps_circle"); return; }
            var steps = _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            return;
        }
        bool breakingStack = (_view.Zone, _view.Map, _campaignDoor, _campaignPuzzle) is (13, 15, 0, 1) or (15, 8, 0, 1) or (50, 18, 1, 6);
        if (battle.Busy && !(battle.Paused && breakingStack))
        { Steer(Vector2.Zero); return; }
        if (battle.Drawn && !breakingStack)
        { Steer(Vector2.Zero); Tap("ps_cross"); return; }
        if (_campaignCheckpoint == (_view.Zone, _view.Map) && _view.Settled && (battle.HostileCount() == 0 || _campaignItem > 0) && (_campaignItem == 0 || _view.Items.GetValueOrDefault(_campaignItem) > 0) && _view.Flags[130] >= _campaignStory && _view.Flags[_campaignFlag] >= _campaignFlagValue && (!_campaignRepair || _campaignRepaired) && (!_campaignSaveUi || _campaignUiSaved))
        {
            Steer(Vector2.Zero);
            if (_campaignSave.Length != 0)
            {
                if (!_campaignSaveUi && _view.SaveGame(1, 1, _view.SavePoint) != Error.Ok || FieldView.ReadCardFile(1, 1) is not { } saved)
                { Finish("campaign: earned checkpoint could not be saved"); return; }
                File.WriteAllText(_campaignSave, Json.Stringify(saved));
                GD.Print($"CAMPAIGN: wrote earned checkpoint {_campaignSave}; " + (_campaignSaveUi ? "saved through DATA's SAVE" : "this diagnostic save does not check the save-point menu"));
            }
            GD.Print($"CAMPAIGN: {(_room == null ? _campaignLoad.Length == 0 ? "New Game" : "resumed campaign" : "isolated room")} checkpoint {_campaignCheckpoint} reached after {_campaignRooms} room entries; this is not a campaign-completion verdict");
            Finish("");
            return;
        }
        // The Wine Cellar's first block puzzle: carry the western block onto the eastern one, climb the stack,
        // then climb the high ledge. These are route instructions for the test player, not changes to room state.
        if ((_view.Zone, _view.Map) == (9, 3) && ashley.Actor.Position.Z < 1408 && _campaignPuzzle < 8)
        {
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: puzzle movement {_campaignPuzzle} stalled at {ashley.Actor.Position}, blocks {string.Join(';', _view.Crates.Select(c => $"{c.X},{c.Z},{c.Bottom}"))}"); return; }
            var target = _campaignPuzzle switch
            {
                0 => new Vector3(192, 0, 832), 1 => new Vector3(192, 0, 960),
                3 => new Vector3(320, 0, 832), 4 => new Vector3(320, 0, 960),
                6 => new Vector3(320, -448, 1088), _ => new Vector3(320, -640, 1344),
            };
            if (_campaignPuzzle is 2 or 5)
            {
                Steer(Vector2.Zero);
                int blocks = _view.Crates.Count();
                if (_frame % 20 == 0) Tap("ps_cross");
                if ((_campaignPuzzle == 2 && blocks == 1) || (_campaignPuzzle == 5 && _view.Crates.Count(c => c.X == 2 && c.Z == 8) == 2))
                {
                    GD.Print($"CAMPAIGN: block puzzle step {_campaignPuzzle} completed through Cross");
                    _campaignPuzzle++;
                    _campaignPuzzleAt = _frame;
                }
                if (_frame - _campaignPuzzleAt > 600) { Finish($"campaign: block interaction {_campaignPuzzle} failed at {ashley.Actor.Position}, facing {ashley.Actor.Facing}, blocks {string.Join(';', _view.Crates.Select(c => $"{c.Id}:{c.X},{c.Z},{c.Bottom}"))}, objects {string.Join(';', _view.CrateObjects.Select(c => c.Id))}, document {_view.Document["crates"].AsGodotArray().Count}, geometry {string.Join(',', _view.Field.Actors.Keys)}"); return; }
                return;
            }
            var path = _view.Path(ashley.Actor.Position, target, 256);
            while (path.Count > 1 && new Vector2(path[0].X - ashley.Actor.Position.X, path[0].Z - ashley.Actor.Position.Z).Length() < 20) path.RemoveAt(0);
            var next = path.Count > 0 ? path[0] : target;
            float direction = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
            Steer(new Vector2(Mathf.Sin(direction), -Mathf.Cos(direction)));
            Input.ActionPress("ps_r2");
            if (new Vector2(target.X - ashley.Actor.Position.X, target.Z - ashley.Actor.Position.Z).Length() < 20 && (_campaignPuzzle < 6 || Math.Abs(target.Y - ashley.Actor.Position.Y) < 2))
            {
                Steer(Vector2.Zero);
                Input.ActionRelease("ps_r2");
                _campaignPuzzle++;
                _campaignPuzzleAt = _frame;
            }
            return;
        }
        Input.ActionRelease("ps_r2");
        var doors = _view.Document["doors"].AsGodotArray();
        if (_campaignDoor >= 0 && _frame - _campaignDoorAt > 1800)
        {
            GD.Print($"CAMPAIGN: door {_campaignDoor} in {_view.Zone},{_view.Map} was not reached from {ashley.Actor.Position}; locks {Json.Stringify(_view.Document["locks"])}, flags 190/191 {_view.Flags[190]}/{_view.Flags[191]}");
            _campaignDoor = -1;
        }
        if (_campaignDoor < 0)
        {
            int best = int.MaxValue;
            for (int i = 0; i < doors.Count; i++)
            {
                var candidate = doors[i].AsGodotDictionary();
                if (_view.Flags[candidate["flag"].AsInt32()] < candidate["flag_value"].AsInt32()) continue;
                int zone = candidate["zone"].AsInt32();
                if (zone == 0) zone = _view.Zone;
                int cost = _campaignAttempts.GetValueOrDefault((_view.Zone, _view.Map, i)) * 1000 + _campaignVisits.GetValueOrDefault((zone, candidate["map"].AsInt32()));
                if (_campaignCheckpoint is { } destination)
                {
                    if (zone == destination.Zone) cost += 20 * Math.Abs(candidate["map"].AsInt32() - destination.Map);
                    else if (_view.Zone == destination.Zone) cost += 10000;
                }
                // A checkpoint in another zone needs the connecting route, rather than treating every local
                // exit as equally useful and walking back to the prologue. This is the test player's route only.
                if (_view.Zone == 9 && (_campaignCheckpoint is { Zone: 12 } || _campaignCheckpoint == null && _view.Flags[130] <= 8))
                {
                    int next = _view.Map switch { 0 => 16, 16 => 3, 3 => 4, 4 => 5, 5 => 6, 6 => 7,
                        7 => _view.Flags[180] >= 2 ? 13 : 8, 8 => 10, 13 => 10, 10 => 11, 11 => 12, 12 => 17, _ => -1 };
                    if (_view.Map == 17 ? zone != 12 : next >= 0 && (zone != 9 || candidate["map"].AsInt32() != next)) cost += 10000;
                }
                if (_view.Zone == 9 && _view.Flags[130] >= 10 && _campaignCheckpoint is { Zone: 11 or 13 })
                {
                    int next = _view.Map switch { 17 => 12, 12 => 11, 11 => 10, 10 => 13, 13 => 7, 7 => 6, 6 => 5, 5 => 4, 4 => 1, 1 => 2, 2 => 15, _ => -1 };
                    if (_view.Map == 15 ? zone != 11 : next >= 0 && (zone != 9 || candidate["map"].AsInt32() != next)) cost += 10000;
                }
                if (_view.Zone == 13 && _campaignCheckpoint is { Zone: 14 } or { Zone: 13, Map: 4 })
                {
                    int next = _view.Map switch { 0 => 1, 1 => 2, 2 => 3, 3 => 6, 6 => _campaignCheckpoint is { Zone: 13, Map: 4 } ? 4 : 8,
                        8 => 10, 10 => 11, 11 => 12, 12 => 13, 13 => 15, 15 => 16, _ => -1 };
                    if (_view.Map == 16 ? zone != 14 : next >= 0 && (zone != 13 || candidate["map"].AsInt32() != next)) cost += 10000;
                }
                if (_view.Zone == 13 && _campaignCheckpoint is { Zone: 15 })
                {
                    int next = _view.Map switch { 16 => 15, 15 => 13, 13 => 12, _ => -1 };
                    if (_view.Map == 12 ? zone != 15 : next >= 0 && (zone != 13 || candidate["map"].AsInt32() != next)) cost += 10000;
                }
                if (_view.Zone == 15 && _campaignCheckpoint is { Zone: 16 })
                {
                    int next = _view.Map switch { 0 => 1, 1 => 12, 12 => 5, 5 => 11, _ => -1 };
                    if (_view.Map == 11 ? zone != 16 : next >= 0 && (zone != 15 || candidate["map"].AsInt32() != next)) cost += 10000;
                }
                if (_campaignCheckpoint is { Zone: 42 })
                {
                    if (_view.Zone == 15)
                    {
                        int next = _view.Map switch { 11 => 5, 5 => 12, 12 => 1, 1 => 0, _ => -1 };
                        if (_view.Map == 0 ? zone != 13 : next >= 0 && (zone != 15 || candidate["map"].AsInt32() != next)) cost += 10000;
                    }
                    if ((_view.Zone, _view.Map) == (13, 12) && zone != 42) cost += 10000;
                }
                if (_campaignCheckpoint is { Zone: 17 })
                {
                    if ((_view.Zone, _view.Map) == (13, 12) && zone != 15) cost += 10000;
                    if (_view.Zone == 15)
                    {
                        int next = _view.Map switch { 0 => 1, 1 => 6, _ => -1 };
                        bool wrong = _view.Map == 6 ? ashley.Actor.Position.Z < 1280
                            ? zone != 15 || candidate["map"].AsInt32() != 8 || candidate["tile"].AsGodotArray()[1].AsInt32() != 3
                            : zone != 17
                            : _view.Map == 8 ? zone != 15 || candidate["map"].AsInt32() != 6 || candidate["tile"].AsGodotArray()[1].AsInt32() != 15
                            : next >= 0 && (zone != 15 || candidate["map"].AsInt32() != next);
                        if (wrong) cost += 10000;
                    }
                }
                if (_campaignCheckpoint is { Zone: 32 })
                {
                    if (_view.Zone == 17 && (zone != 15 || candidate["map"].AsInt32() != 10)) cost += 10000;
                    if ((_view.Zone, _view.Map) == (15, 10) && zone != 32) cost += 10000;
                }
                if (_campaignRouteAt + 1 < _campaignRoute.Count && _campaignRoute[_campaignRouteAt] == (_view.Zone, _view.Map)
                    && (zone, candidate["map"].AsInt32()) != _campaignRoute[_campaignRouteAt + 1]) cost += 100000;
                if (cost < best) { best = cost; _campaignDoor = i; }
            }
            if (_campaignDoor < 0) { Finish($"campaign: no available door in {_view.Zone},{_view.Map}, story {_view.Flags[0x82]}"); return; }
            var key = (_view.Zone, _view.Map, _campaignDoor);
            _campaignAttempts[key] = _campaignAttempts.GetValueOrDefault(key) + 1;
            if (_campaignRoute.Count > 0 && _campaignAttempts[key] > 4)
            { Finish($"campaign: requested route door {_campaignDoor} in {_view.Zone},{_view.Map} remained unreachable from {ashley.Actor.Position}"); return; }
            _campaignDoorAt = _frame;
            if (_campaignPuzzle == 0) _campaignPuzzleAt = _frame;
            GD.Print($"CAMPAIGN: seeking door {_campaignDoor}, HP {ashley.Hp}/{ashley.MaxHp}, carried {_view.Equipment.Count}, learned {_view.Abilities.Count}");
        }
        // Stay on the mine's upper terraces before dropping to the north exit. A direct route from
        // the low western floor approaches its 512-unit wall instead of the two descending terraces.
        if ((_view.Zone, _view.Map, _campaignDoor) == (50, 9, 0) && _campaignPuzzle < 3)
        {
            _campaignDoorAt = _frame;
            var point = _campaignPuzzle switch
            {
                0 => new Vector3(1216, -1536, 832),
                1 => new Vector3(1216, -1024, 576), _ => new Vector3(1216, -512, 320),
            };
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16)
            { _campaignPuzzle++; Steer(Vector2.Zero); return; }
            float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            Input.ActionRelease("ps_r2");
            return;
        }
        // Jump via the moving platform over the mine's pit. Break three crates off its four-high stack,
        // then push the remaining wooden crate east to make a perch below the high exit.
        if ((_view.Zone, _view.Map, _campaignDoor) == (50, 18, 1) && _campaignPuzzle < 14)
        {
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 2400)
            { Finish($"campaign: mine terrace step {_campaignPuzzle} stalled at {ashley.Actor.Position}, blocks {string.Join(';', _view.Crates)}"); return; }
            bool perch = _view.Crates.Any(c => c.Kind == 4 && c.X == 3 && c.Z == 9);
            if (!perch)
            {
                if (_campaignMinePerch is 2 or 5)
                {
                    Steer(Vector2.Zero);
                    bool done = _campaignMinePerch == 2 ? !_view.Crates.Any(c => c.Id == "51") : perch;
                    if (done) { _campaignMinePerch++; _campaignPuzzleAt = _frame; }
                    else if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                    return;
                }
                var perchPoint = _campaignMinePerch switch
                {
                    0 => new Vector3(320, -384, 1472), 1 => new Vector3(192, -384, 1472),
                    3 => new Vector3(448, -384, 1344), _ => new Vector3(448, -384, 1216),
                };
                bool face = _campaignMinePerch is 1 or 4;
                if (face && _frame - _campaignPuzzleAt >= 24)
                { _campaignMinePerch++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (face && _frame - _campaignPuzzleAt > 1) { Steer(Vector2.Zero); return; }
                var perchPath = face ? new List<Vector3>() : _view.Path(ashley.Actor.Position, perchPoint, 256);
                while (perchPath.Count > 1 && new Vector2(perchPath[0].X - ashley.Actor.Position.X, perchPath[0].Z - ashley.Actor.Position.Z).Length() < 16) perchPath.RemoveAt(0);
                var perchNext = perchPath.Count > 0 ? perchPath[0] : perchPoint;
                float perchTurn = (ashley.Actor.FacingToward(perchNext) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                Steer(new Vector2(Mathf.Sin(perchTurn), -Mathf.Cos(perchTurn)));
                Input.ActionPress("ps_r2");
                if (!face && new Vector2(perchPoint.X - ashley.Actor.Position.X, perchPoint.Z - ashley.Actor.Position.Z).Length() < 16)
                { _campaignMinePerch++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
                return;
            }
            if (_campaignPuzzle is >= 1 and <= 4 && ashley.Actor.Position.Z > 1400 && _frame - _campaignPuzzleAt > 90)
            {
                if (++_campaignBoardingMisses > 3) { Finish("campaign: four mine platform boardings missed"); return; }
                _campaignPuzzle = 0; _campaignPuzzleAt = _frame; _campaignBoarding = false;
            }
            if (_campaignPuzzle == 1)
            {
                if (_view.PlatformState(0) is not { } platform) { Finish("campaign: the mine has no active platform"); return; }
                if (_view.PlatformUnder(ashley.Actor.Position) == 0 && new Vector2(ashley.Actor.Position.X - platform.Now.X, ashley.Actor.Position.Z - platform.Now.Z).Length() < 24)
                { _campaignPuzzle = 2; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (!_campaignBoarding)
                {
                    if (platform.Now.X < 440 || platform.Waited is < 0 or > 20) { Steer(Vector2.Zero); return; }
                    _campaignBoarding = true;
                    Tap("ps_square");
                }
                float boardingTurn = (ashley.Actor.FacingToward(platform.Now) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                Steer(new Vector2(Mathf.Sin(boardingTurn), -Mathf.Cos(boardingTurn)));
                Input.ActionRelease("ps_r2");
                return;
            }
            if (_campaignPuzzle == 6)
            {
                Steer(Vector2.Zero);
                if (_view.Crates.Count(c => c.Kind == 2 && c.X == 5 && c.Z == 3) <= 1)
                { _campaignPuzzle = 7; _campaignPuzzleAt = _frame; return; }
                if (battle.Paused) { if (_press == 0) Tap("ps_circle"); }
                else if (!battle.Busy && _frame % 20 == 0) Tap("ps_circle");
                return;
            }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            if (battle.Drawn) { Tap("ps_cross"); return; }
            if (_campaignPuzzle == 8)
            {
                Steer(Vector2.Zero);
                if (_view.Crates.Any(c => c.Kind == 2 && c.X == 8 && c.Z == 3))
                { _campaignPuzzle = 9; _campaignPuzzleAt = _frame; return; }
                if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            if (_campaignPuzzle is 2 or 4 or 10 or 13)
            {
                Steer(Vector2.Zero);
                if (_frame - _campaignPuzzleAt >= 60) { _campaignPuzzle++; _campaignPuzzleAt = _frame; }
                return;
            }
            var point = _campaignPuzzle switch
            {
                0 => new Vector3(448, -512, 1216), 3 => new Vector3(448, -384, 384),
                5 => new Vector3(576, -384, 448), 7 => new Vector3(704, -384, 448),
                9 => new Vector3(1088, -512, 448), 11 => new Vector3(1216, -896, 448),
                _ => new Vector3(1600, -896, 320),
            };
            if (_campaignPuzzle == 7 && _frame - _campaignPuzzleAt >= 24)
            { _campaignPuzzle = 8; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            if (_campaignPuzzle == 7 && _frame - _campaignPuzzleAt > 1) { Steer(Vector2.Zero); return; }
            var steps = _campaignPuzzle is 3 or 7 or 11 ? new List<Vector3>() : _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle is 3 or 11)
            { Input.ActionRelease("ps_r2"); if ((_frame - _campaignPuzzleAt) % 60 == 1) Tap("ps_square"); }
            else Input.ActionPress("ps_r2");
            if (_campaignPuzzle != 7 && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        // The Blackmarket's northeast platform is reached by a diagonal running jump from its southwest corner,
        // rather than trying to climb the taller barrels along its south face.
        if ((_view.Zone, _view.Map, _campaignDoor) == (9, 5, 1) && _campaignPuzzle < 2)
        {
            var corner = _campaignPuzzle == 0 ? new Vector3(1984, 0, 448) : new Vector3(2048, -384, 384);
            if (_campaignPuzzle == 0 && new Vector2(corner.X - ashley.Actor.Position.X, corner.Z - ashley.Actor.Position.Z).Length() < 16)
            { _campaignPuzzle = 1; _campaignPuzzleAt = _frame; }
            else if (_campaignPuzzle == 1 && ashley.Actor.Position.Y <= -384 && ashley.Actor.Position.Z < 384)
            { _campaignPuzzle = 2; Steer(Vector2.Zero); return; }
            var steps = _campaignPuzzle == 0 ? _view.Path(ashley.Actor.Position, corner) : new List<Vector3>();
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var point = steps.Count > 0 ? steps[0] : corner;
            float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 1 && (_frame - _campaignPuzzleAt) % 60 == 1) Tap("ps_square");
            return;
        }
        // The south exit in room 9,10 is 512 units above the floor. Carry two of the north stack's blocks
        // to its foot, then climb their 256-unit stack. Only pad input moves and places the blocks.
        if (((_view.Zone, _view.Map, _campaignDoor) == (9, 10, 2) || (_view.Zone, _view.Map, _campaignDoor) == (9, 12, 1)
            || (_view.Zone, _view.Map, _campaignDoor) == (9, 11, 2)) && _campaignPuzzle < 12)
        {
            bool south = _view.Map == 12;
            bool lever = _view.Map == 11;
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 1800)
            { Finish($"campaign: south ledge puzzle step {_campaignPuzzle} stalled at {ashley.Actor.Position}, blocks {string.Join(';', _view.Crates)}"); return; }
            var point = lever ? _campaignPuzzle switch
            {
                0 or 1 or 5 or 6 => new Vector3(320, 0, 448),
                3 or 8 => new Vector3(832, 0, 1088),
                10 => new Vector3(832, -128, 1216), _ => new Vector3(832, -512, 1472),
            } : south ? _campaignPuzzle switch
            {
                0 or 1 or 5 or 6 => new Vector3(192, 0, 960),
                3 or 8 => new Vector3(448, -128, 1600),
                10 => new Vector3(448, -384, 1728), _ => new Vector3(448, -640, 1984),
            } : _campaignPuzzle switch
            {
                0 => new Vector3(64, -256, 960), 1 => new Vector3(64, -256, 832),
                3 or 8 => new Vector3(320, 0, 1472),
                5 => new Vector3(192, 0, 704), 6 => new Vector3(192, 0, 704),
                10 => new Vector3(320, -256, 1600), _ => new Vector3(320, -512, 1792),
            };
            if (_campaignPuzzle is 2 or 4 or 7 or 9)
            {
                Steer(Vector2.Zero);
                int source = _view.Crates.Count(c => c.X == (lever ? 2 : 0) && c.Z == (lever ? 2 : south ? 7 : 5));
                int placed = _view.Crates.Count(c => c.X == (lever ? 6 : south ? 3 : 2) && c.Z == (lever ? 9 : south ? 13 : 12));
                if ((_campaignPuzzle == 2 && source == (south || lever ? 1 : 2)) || (_campaignPuzzle == 4 && placed == 1)
                    || (_campaignPuzzle == 7 && source == (south || lever ? 0 : 1)) || (_campaignPuzzle == 9 && placed == 2))
                { _campaignPuzzle = lever && _campaignPuzzle == 4 ? 10 : _campaignPuzzle + 1; _campaignPuzzleAt = _frame; return; }
                if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            // Face the interaction tile before stopping; turning takes twelve field frames.
            if (_campaignPuzzle == 6 || (south || lever) && _campaignPuzzle == 1)
            {
                point = lever ? new Vector3(320, 0, 320) : new Vector3(64, 0, south ? 960 : 704);
                if (_frame - _campaignPuzzleAt >= 24)
                { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (_frame - _campaignPuzzleAt > 1) { Steer(Vector2.Zero); return; }
            }
            if (lever && _campaignPuzzle == 11 && _frame - _campaignPuzzleAt < 40) { Steer(Vector2.Zero); return; }
            var steps = _campaignPuzzle == 6 || south || lever ? new List<Vector3>() : _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            Input.ActionPress("ps_r2");
            if (lever && _campaignPuzzle == 11)
            {
                Input.ActionRelease("ps_r2");
                if ((_frame - _campaignPuzzleAt) % 60 == 41) Tap("ps_square");
            }
            if (_campaignPuzzle != 6 && !((south || lever) && _campaignPuzzle == 1) && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 12
                && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        if ((_view.Zone, _view.Map, _campaignDoor) == (9, 11, 2))
        {
            _campaignDoorAt = _frame;
            if (_view.Flags[100] == 0)
            {
                if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: lever step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
                var point = _campaignPuzzle == 12 ? new Vector3(832, -512, 1600) : new Vector3(682, -512, 1600);
                if (_campaignPuzzle == 12 && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 12)
                { _campaignPuzzle = 13; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (_campaignPuzzle == 13 && _frame - _campaignPuzzleAt >= 24)
                { _campaignPuzzle = 14; _campaignPuzzleAt = _frame; }
                if (_campaignPuzzle == 14)
                { Steer(Vector2.Zero); if (_frame % 20 == 0) Tap("ps_cross"); return; }
                if (_campaignPuzzle == 13 && _frame - _campaignPuzzleAt > 1) { Steer(Vector2.Zero); return; }
                float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
                return;
            }
            if (_campaignPuzzle < 15) _campaignPuzzle = 15;
            // Align with the narrow exit before running south, rather than pushing diagonally into its corner.
            var exit = _campaignPuzzle == 15 ? new Vector3(448, 0, 1600) : new Vector3(448, 0, 1728);
            Input.ActionRelease("ps_r2");
            if (new Vector2(exit.X - ashley.Actor.Position.X, exit.Z - ashley.Actor.Position.Z).Length() < 20)
            {
                if (_campaignPuzzle == 15) _campaignPuzzle = 16;
                Steer(Vector2.Zero); return;
            } // The exit's event requires standing still, not another Cross press.
            float direction = (ashley.Actor.FacingToward(exit) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(direction), -Mathf.Cos(direction)));
            return;
        }
        // Catacombs 13,10: carry two stone blocks to the east ledge, climb their stack and jump up.
        if ((_view.Zone, _view.Map, _campaignDoor) == (13, 10, 1) && _campaignPuzzle < 14)
        {
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: east stack step {_campaignPuzzle} stalled at {ashley.Actor.Position}, blocks {string.Join(';', _view.Crates)}"); return; }
            if (_campaignPuzzle is 2 or 5 or 8 or 11)
            {
                Steer(Vector2.Zero);
                int source = _view.Crates.Count(c => c.X == (_campaignPuzzle == 2 ? 6 : 5) && c.Z == 3);
                int stack = _view.Crates.Count(c => c.X == 13 && c.Z == 3);
                if ((_campaignPuzzle is 2 or 8) ? source == 0 : stack == (_campaignPuzzle == 5 ? 1 : 2))
                { _campaignPuzzle++; _campaignPuzzleAt = _frame; return; }
                if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            var point = _campaignPuzzle switch
            {
                0 => new Vector3(832, 0, 576), 1 => new Vector3(832, 0, 448),
                3 or 9 => new Vector3(1600, 0, 448), 4 or 10 => new Vector3(1728, 0, 448),
                6 => new Vector3(704, 0, 576), 7 => new Vector3(704, 0, 448),
                12 => new Vector3(1728, -256, 448), _ => new Vector3(1856, -640, 448),
            };
            bool facing = _campaignPuzzle is 1 or 4 or 7 or 10;
            if (facing && _frame - _campaignPuzzleAt >= 24)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            if (facing && _frame - _campaignPuzzleAt > 1 || _campaignPuzzle == 13 && _frame - _campaignPuzzleAt < 40)
            { Steer(Vector2.Zero); return; }
            var steps = facing || _campaignPuzzle >= 12 ? new List<Vector3>() : _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 13)
            { Input.ActionRelease("ps_r2"); if ((_frame - _campaignPuzzleAt) % 60 == 41) Tap("ps_square"); }
            else Input.ActionPress("ps_r2");
            if (!facing && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 12 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        // On the return trip, the east side's three raised floor steps lead back over the barrier.
        // The flat floor at its foot is too low for a direct running jump.
        if (_view.Zone == 15 && _view.Map == 0 && _campaignDoor is 0 or 1 && _campaignPuzzle < 5)
        {
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 2400) { Finish($"campaign: Sanctum return step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
            var point = _campaignPuzzle switch
            {
                0 => new Vector3(1472, -128, 448), 1 => new Vector3(1344, -256, 448),
                2 => new Vector3(1344, -448, 576), 3 => new Vector3(1216, -704, 576),
                _ => new Vector3(960, 0, 448),
            };
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            Input.ActionPress("ps_r2");
            return;
        }
        // The Sanctum entrance's 704-high east barrier needs its two loose stones stacked on
        // the south pushable crate. Stand on the adjacent crate to place the second stone, then jump east.
        if ((_view.Zone, _view.Map, _campaignDoor) == (15, 0, 2) && _campaignPuzzle < 14)
        {
            _campaignDoorAt = _frame;
            bool built = !_view.Crates.Any(c => c.X == 7 && c.Z == 0) && _view.Crates.Count(c => c.X == 7 && c.Z == 5) == 3;
            bool preset = built && !_view.Crates.Any(c => c.X == 7 && c.Z == 4);
            if (built && _campaignPuzzle < 9) { _campaignPuzzle = 9; _campaignPuzzleAt = _frame; }
            if (_frame - _campaignPuzzleAt > 2400)
            { Finish($"campaign: Sanctum entrance stack {_campaignPuzzle} stalled at {ashley.Actor.Position}, blocks {string.Join(';', _view.Crates)}"); return; }
            if (_campaignPuzzle is 2 or 5 or 8 or 11)
            {
                Steer(Vector2.Zero);
                int source = _view.Crates.Count(c => c.X == 7 && c.Z == 0);
                int stack = _view.Crates.Count(c => c.X == 7 && c.Z == 5);
                if (_campaignPuzzle == 2 ? source == 1 : _campaignPuzzle == 8 ? source == 0 : stack == (_campaignPuzzle == 5 ? 2 : 3))
                { _campaignPuzzle++; _campaignPuzzleAt = _frame; return; }
                if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            var point = _campaignPuzzle switch
            {
                0 or 6 => new Vector3(832, 0, 64),
                1 or 7 => new Vector3(960, 0, 64),
                3 => new Vector3(960, -128, 576),
                9 => preset ? new Vector3(832, 0, 704) : new Vector3(960, -128, 576),
                4 or 10 => new Vector3(960, -128, 704),
                12 => new Vector3(960, -384, 704),
                _ => new Vector3(1088, -704, 704),
            };
            bool facing = _campaignPuzzle is 1 or 4 or 7 or 10;
            if (facing && _frame - _campaignPuzzleAt >= 24)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            if (facing && _frame - _campaignPuzzleAt > 1 || _campaignPuzzle == 13 && _frame - _campaignPuzzleAt < 40)
            { Steer(Vector2.Zero); return; }
            var steps = facing || _campaignPuzzle >= 12 || preset && _campaignPuzzle == 9 ? new List<Vector3>() : _view.Path(ashley.Actor.Position, point, 256);
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 13)
            { Input.ActionRelease("ps_r2"); if ((_frame - _campaignPuzzleAt) % 60 == 41) Tap("ps_square"); }
            else if (preset && _campaignPuzzle == 12)
            { Input.ActionRelease("ps_r2"); if ((_frame - _campaignPuzzleAt) % 60 == 1) Tap("ps_square"); }
            else Input.ActionPress("ps_r2");
            if (!facing && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 12 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle = built && _campaignPuzzle == 9 ? 12 : _campaignPuzzle + 1; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        // Golem unlocks the working platform annex. Lift its stone after breaking the crate above it,
        // make a jumping perch at the southeast ledge, then board and ride the actual platform to the south shore.
        if ((_view.Zone, _view.Map, _campaignDoor) == (15, 8, 0) && _campaignPuzzle < 14)
        {
            _campaignDoorAt = _frame;
            if (_campaignPuzzle >= 10 && ashley.Actor.Position.Y > -640)
            { _campaignPuzzle = 7; _campaignPuzzleAt = _frame; _campaignBoarding = false; GD.Print("CAMPAIGN: retrying the annex after a missed boarding"); }
            bool built = _view.Crates.Any(c => c.Kind == 4 && c.X == 6 && c.Z == 3);
            if (built && _campaignPuzzle < 7) { _campaignPuzzle = 7; _campaignPuzzleAt = _frame; }
            if (_frame - _campaignPuzzleAt > 2400) { Finish($"campaign: platform annex step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
            if (_campaignPuzzle == 1)
            {
                Steer(Vector2.Zero);
                if (!_view.Crates.Any(c => c.Id == "33")) { _campaignPuzzle = 2; _campaignPuzzleAt = _frame; return; }
                if (battle.Paused)
                {
                    if (_press > 0) return;
                    if (_campaignCubePicks == 0) { Tap("ps_right"); _campaignCubePicks++; }
                    else Tap("ps_circle");
                }
                else if (!battle.Busy) { _campaignCubePicks = 0; if (_frame % 20 == 0) Tap("ps_circle"); }
                return;
            }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            if (battle.Drawn) { Tap("ps_cross"); return; }
            if (_campaignPuzzle is 3 or 6)
            {
                Steer(Vector2.Zero);
                bool done = _campaignPuzzle == 3 ? !_view.Crates.Any(c => c.Id == "32") : built;
                if (done) { _campaignPuzzle++; _campaignPuzzleAt = _frame; }
                else if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            if (_campaignPuzzle == 10)
            {
                if (_view.PlatformState(0) is not { } platform) { Finish("campaign: the annex has no active platform"); return; }
                if (_view.PlatformUnder(ashley.Actor.Position) == 0 && new Vector2(ashley.Actor.Position.X - platform.Now.X, ashley.Actor.Position.Z - platform.Now.Z).Length() < 24)
                { _campaignPuzzle = 11; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
                if (!_campaignBoarding)
                {
                    if (platform.Waited is < 0 or > 20 || platform.Now.Z > 965 || Math.Abs(platform.Now.Y + 896) > 16)
                    { Steer(Vector2.Zero); return; }
                    _campaignBoarding = true;
                }
            }
            if (_campaignPuzzle == 11)
            {
                Steer(Vector2.Zero);
                if (_view.PlatformState(0) is { } platform && platform.Now.Z >= 1590 && Math.Abs(platform.Now.Y + 768) < 32)
                { _campaignPuzzle = 12; _campaignPuzzleAt = _frame; }
                return;
            }
            var point = _campaignPuzzle switch
            {
                0 => new Vector3(576, -384, 192), 2 => new Vector3(576, -384, 64),
                4 => new Vector3(832, -384, 320), 5 => new Vector3(832, -384, 448),
                7 => new Vector3(832, -512, 448), 8 => new Vector3(832, -896, 576),
                9 => new Vector3(576, -896, 832), 10 => _view.PlatformState(0)!.Value.Now,
                _ => new Vector3(576, -768, 1728),
            };
            bool facing = _campaignPuzzle is 2 or 5;
            if (facing && _frame - _campaignPuzzleAt >= 24)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            if (facing && _frame - _campaignPuzzleAt > 1 || _campaignPuzzle == 8 && _frame - _campaignPuzzleAt < 40)
            { Steer(Vector2.Zero); return; }
            var steps = _campaignPuzzle is 0 or 4 or 9 ? _view.Path(ashley.Actor.Position, point, 256) : new List<Vector3>();
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 8)
            { Input.ActionRelease("ps_r2"); if ((_frame - _campaignPuzzleAt) % 60 == 41) Tap("ps_square"); }
            else Input.ActionPress("ps_r2");
            if (!facing && _campaignPuzzle != 10 && new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle = _campaignPuzzle >= 12 ? 14 : _campaignPuzzle + 1; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        // The Catacombs south ledge needs a stone on its 128-high step. Break the corner stack's base
        // and carry the top stone after it falls, then climb and jump from the resulting 256-high perch.
        if ((_view.Zone, _view.Map, _campaignDoor) == (13, 15, 0) && _campaignPuzzle < 8)
        {
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 2400) { Finish($"campaign: Catacombs south puzzle {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
            var point = _campaignPuzzle switch
            {
                0 or 1 or 2 => new Vector3(1472, 0, 320),
                3 or 4 => new Vector3(960, 0, 1216),
                5 => new Vector3(960, -256, 1344),
                _ => new Vector3(960, -640, 1536),
            };
            if (_campaignPuzzle == 1)
            {
                Steer(Vector2.Zero);
                if (!_view.Crates.Any(c => c.Id == "46"))
                { _campaignPuzzle = 2; _campaignPuzzleAt = _frame; _campaignCubePicks = 0; return; }
                if (battle.Paused)
                {
                    if (_press > 0) return;
                    Tap("ps_circle");
                }
                else if (!battle.Busy) { _campaignCubePicks = 0; if (_frame % 20 == 0) Tap("ps_circle"); }
                return;
            }
            if (_campaignPuzzle == 2 && _view.Crates.Any(c => c.Id == "48") && _view.CrateObjects.Any(c => c.Id == "48" && (!c.Resting || c.Bottom < -128))) { Steer(Vector2.Zero); return; }
            if (battle.Busy) { Steer(Vector2.Zero); return; }
            if (battle.Drawn) { Tap("ps_cross"); return; }
            bool close = new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16;
            if (_campaignPuzzle is 2 or 4)
            {
                Steer(Vector2.Zero);
                var facing = _campaignPuzzle == 2 ? new Vector3(1600, 0, 320) : new Vector3(960, 0, 1344);
                if (_frame - _campaignPuzzleAt <= 1)
                {
                    float facingTurn = (ashley.Actor.FacingToward(facing) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
                    Steer(new Vector2(Mathf.Sin(facingTurn), -Mathf.Cos(facingTurn)));
                }
                bool done = _campaignPuzzle == 2 ? !_view.Crates.Any(c => c.Id == "48")
                    : _view.Crates.Any(c => c.Id == "48" && c.X == 7 && c.Z == 10);
                if (done) { _campaignPuzzle++; _campaignPuzzleAt = _frame; }
                else if ((_frame - _campaignPuzzleAt) % 30 == 20) Tap("ps_cross");
                return;
            }
            if (close && (_campaignPuzzle < 5 || Math.Abs(point.Y - ashley.Actor.Position.Y) < 2))
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); return; }
            if (_campaignPuzzle == 6 && _frame - _campaignPuzzleAt < 40) { Steer(Vector2.Zero); return; }
            var steps = _campaignPuzzle < 5 ? _view.Path(ashley.Actor.Position, point, 256) : new List<Vector3>();
            while (steps.Count > 1 && new Vector2(steps[0].X - ashley.Actor.Position.X, steps[0].Z - ashley.Actor.Position.Z).Length() < 16) steps.RemoveAt(0);
            var next = steps.Count > 0 ? steps[0] : point;
            float turn = (ashley.Actor.FacingToward(next) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle == 5) Input.ActionPress("ps_r2"); else Input.ActionRelease("ps_r2");
            if (_campaignPuzzle >= 6 && (_frame - _campaignPuzzleAt) % 60 == 41) Tap("ps_square");
            return;
        }
        // Returning north through the lever room uses the two-block stack, then a jump to the 512-high doorway.
        if ((_view.Zone, _view.Map, _campaignDoor) == (9, 11, 1) && _campaignPuzzle < 3)
        {
            _campaignDoorAt = _frame;
            if (_frame - _campaignPuzzleAt > 1800) { Finish($"campaign: north stack step {_campaignPuzzle} stalled at {ashley.Actor.Position}"); return; }
            var point = _campaignPuzzle switch { 0 => new Vector3(320, 0, 448), 1 => new Vector3(320, -256, 320), _ => new Vector3(448, -512, 192) };
            float turn = (ashley.Actor.FacingToward(point) - _view.Gameplay.Yaw) * Mathf.Tau / 4096;
            Steer(new Vector2(Mathf.Sin(turn), -Mathf.Cos(turn)));
            if (_campaignPuzzle > 0 && (_frame - _campaignPuzzleAt) % 80 == 30) Tap("ps_square");
            if (new Vector2(point.X - ashley.Actor.Position.X, point.Z - ashley.Actor.Position.Z).Length() < 16 && Math.Abs(point.Y - ashley.Actor.Position.Y) < 2)
            { _campaignPuzzle++; _campaignPuzzleAt = _frame; Steer(Vector2.Zero); }
            return;
        }
        var tile = doors[_campaignDoor].AsGodotDictionary()["tile"].AsGodotArray();
        var goal = new Vector3(tile[0].AsInt32() * 128 + 64, ashley.Actor.Position.Y, tile[1].AsInt32() * 128 + 64);
        var route = _view.Path(ashley.Actor.Position, goal, _campaign ? 256 : FieldPlayer.StepHeight);
        while (route.Count > 1 && new Vector2(route[0].X - ashley.Actor.Position.X, route[0].Z - ashley.Actor.Position.Z).Length() < 24) route.RemoveAt(0);
        var waypoint = route.Count > 0 ? route[0] : goal;
        float angle = (ashley.Actor.FacingToward(waypoint) - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
        Steer(new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)));
        var probe = ashley.Actor.Position.MoveToward(waypoint, 96);
        if (_view.FloorAt(probe) is { } ahead && ashley.Actor.Position.Y - ahead is > FieldPlayer.StepHeight and <= 256) Input.ActionPress("ps_r2");
        else Input.ActionRelease("ps_r2");
        // At a blocked edge, try the player's interaction and jump controls; never move the actor or set a flag.
        bool atDoor = new Vector2(goal.X - ashley.Actor.Position.X, goal.Z - ashley.Actor.Position.Z).Length() < 200;
        if (_frame % (atDoor ? 20 : 120) == 0) Tap("ps_cross");
        if (!atDoor && _frame % 120 == 60) Tap("ps_square");
    }

    /// <summary>
    /// After the prologue: control passes to the player once the camera is back on Ashley. Running up the stick for a
    /// second moves her away from the camera, the gameplay camera follows, and a held L1 turns it an octant.
    /// </summary>
    private void StepControl()
    {
        int elapsed = _frame - _controlFrame;
        if (elapsed == 1)
        {
            if (!_view.Controlled || _view.Player == null) { Finish("control did not pass to the player after the prologue"); return; }
            if (CheckCombatRules() is { Length: > 0 } rules) { Finish(rules); return; }
            _start = _view.Player.Position;
            _startEye = _view.Gameplay.Eye;
            Input.ActionPress("ps_up");
        }
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && elapsed % 20 == 0) Capture($"control_{elapsed:d3}");
        if (elapsed == 61)
        {
            Input.ActionRelease("ps_up");
            var player = _view.Player!;
            var moved = player.Position - _start;
            // Up on the stick points away from the eye, along the camera's yaw.
            float yaw = _view.Gameplay.Yaw * Mathf.Tau / 4096f;
            var away = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
            if (_view.Player?.Player is { } clips)
                GD.Print($"FIELD: Ashley's idle, walk and run last {string.Join(", ", new[] { 1, 13, 19 }.Select(n => clips.HasAnimation($"base_{n}") ? $"{clips.GetAnimation($"base_{n}").Length:f2} s" : "-"))}");
            GD.Print($"FIELD: control from {_start} moved {moved} (yaw {_view.Gameplay.Yaw}), eye {_startEye} -> {_view.Gameplay.Eye}");
            if (new Vector3(moved.X, 0, moved.Z).Length() < 64 || moved.Normalized().Dot(away) < 0.5f) { Finish("Ashley did not run where the stick pointed"); return; }
            if ((_view.Gameplay.Eye - _startEye).Length() < 32) { Finish("the gameplay camera did not follow Ashley"); return; }
            _startEye = new Vector3(_view.Gameplay.Yaw, 0, 0);
            Input.ActionPress("ps_l1");
        }
        if (elapsed == 66) Input.ActionRelease("ps_l1");
        if (elapsed == 120)
        {
            float turned = Mathf.PosMod(_view.Gameplay.Yaw - _startEye.X, 4096);
            if (turned != 512) { Finish($"L1 turned the camera by {turned}, not an octant"); return; }
            _battleFrame = _frame;
        }
    }

    /// <summary>Plays the one room's event through, confirming text; reports where it stalls.</summary>
    private void StepRoom()
    {
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        if (_optionsWindow && _view.Controlled && StepOptionsWindow()) return;
        if (StepResults()) return;
        if (_frame % _tapEvery == 0 && !_view.Controlled) Tap("ps_circle");
        if (_view.EventNode != _lastNode) { _lastNode = _view.EventNode; _nodeFrame = _frame; GD.Print($"ROOM node {_lastNode} at {_frame}"); }
        if (_effectId >= 0 && _view.Controlled && _view.Player is { } caster)
        {
            if (_effect == null)
            {
                EventActor? target = null;
                foreach (var actor in _view.Actors.Values)
                    if (actor != caster && actor.Visible && (target == null || actor.Position.DistanceTo(caster.Position) < target.Position.DistanceTo(caster.Position))) target = actor;
                _effect = _resident ? _view.PlayResident(_effectId, caster, target ?? caster) : _view.PlayEffect(_effectId, caster, target);
                if (_effect == null)
                {
                    // Not every id has a file; a range passes over them.
                    if (_effectIds.Count == 0) { Finish(_effectsDrawn > 0 ? "" : $"effect {_effectId} is not installed"); return; }
                    _effectId = _effectIds.Dequeue();
                    return;
                }
                _effectAt = _frame;
                GD.Print($"FIELD: effect {_effectId} plays from Ashley at {(target?.Id ?? "Ashley")}");
            }
            int since = _frame - _effectAt;
            if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && since % 10 == 0 && since <= 240) Capture($"effect_{_effectId:d3}_{since:d3}");
            if (_effect.Finished || since > 60 * 40)
            {
                GD.Print($"FIELD: effect {_effectId} ran {_effect.Ticks} ticks, at most {_effect.MostQuads} quads, shaking up to {_effect.MostShake:0.#} units");
                if (!_effect.Finished) { Finish($"effect {_effectId} did not end"); return; }
                // Some effects only shake, tint or sound; a range reports them but fails only if nothing at all drew.
                if (_effect.MostQuads > 0) _effectsDrawn++; else _effectsSilent++;
                if (_effectIds.Count == 0) { GD.Print($"FIELD: {_effectsDrawn} effects drew, {_effectsSilent} drew nothing"); Finish(_effectsDrawn == 0 ? $"effect {_effectId} drew nothing" : ""); return; }
                _effectId = _effectIds.Dequeue();
                _effect = null;
            }
            return;
        }
        if (_remember && _view.Controlled && _view.Battle is { } memoryBattle)
        {
            if (_rememberStage == 0)
            {
                // By their room records: a felled character's slot may go to another.
                var felled = new List<int>();
                foreach (var (id, fighter) in new Dictionary<string, FieldBattle.Combatant>(memoryBattle.Combatants))
                    if (fighter != memoryBattle.Ashley && !fighter.Dead && _view.RoomActorIndices.TryGetValue(id, out int record)) { memoryBattle.Strike(fighter, fighter.Hp); felled.Add(record); }
                if (felled.Count == 0) { Finish("the room has no hostile characters to remember"); return; }
                _felled = felled;
                _felledFrame = _frame;
                _rememberStage = 1;
                return;
            }
            // Four seconds on, a felled enemy has faded away, unless its room record keeps its body.
            if (_rememberStage == 1 && _capture.Length != 0 && DisplayServer.GetName() != "headless" && (_frame - _felledFrame) % 10 == 0 && _frame - _felledFrame <= 120) Capture($"fade_{_frame - _felledFrame:d3}");
            if (_rememberStage == 1 && _frame - _felledFrame < 240) return;
            if (_rememberStage == 1)
            {
                var felled = _felled;
                foreach (var fighter in memoryBattle.Combatants.Values)
                    if (fighter.Dead && fighter != memoryBattle.Ashley && fighter.Actor.Visible != fighter.Marked)
                    { Finish($"{fighter.Actor.Id} {(fighter.Marked ? "lost the body its record keeps" : "did not fade after it died")}"); return; }
                GD.Print($"FIELD: the felled faded, but for {System.Linq.Enumerable.Count(memoryBattle.Combatants.Values, f => f.Dead && f.Marked)} kept by their records");
                // An item a felled enemy always drops opens the gadget bag on it.
                bool sure = false;
                foreach (var fighter in memoryBattle.Combatants.Values)
                    foreach (var value in fighter.Dead ? fighter.DropItems ?? new Godot.Collections.Array() : new Godot.Collections.Array())
                        sure |= value.AsGodotDictionary() is var item && item["id"].AsInt32() != 0 && item["count"].AsInt32() > 0 && item["rate"].AsInt32() > 255;
                GD.Print($"FIELD: drops offered {_view.DropsOffered} (an item sure to drop: {sure})");
                if (sure && _view.DropsOffered == 0) { Finish("a felled enemy's sure item did not open the gadget bag"); return; }
                if (_view.MenuOpen) Tap("ps_square");
                _rememberStage = 2;
                _view.LoadRoom(_view.Zone, _view.Map);
                var back = new List<int>();
                foreach (var (id, record) in _view.RoomActorIndices)
                    if (felled.Contains(record) && _view.Battle?.Combatants.TryGetValue(id, out var c) == true && c.Hostile && !c.Dead) back.Add(record);
                GD.Print($"FIELD: felled {string.Join(", ", felled)}; back on entering again: {string.Join(", ", back)}");
                Finish(back.Count == 0 ? "" : "felled characters came back on entering the room again");
                return;
            }
        }
        if (_view.Controlled && !_handedOver)
        {
            _handedOver = true;
            if (_view.Workshop && CheckWorkshop() is { Length: > 0 } failure) { Finish(failure); return; }
            if (CheckSeal() is { Length: > 0 } sealFailure) { Finish(sealFailure); return; }
            if (CheckContainer() is { Length: > 0 } containerFailure) { Finish(containerFailure); return; }
            if (CheckShields() is { Length: > 0 } shieldFailure) { Finish(shieldFailure); return; }
            if (CheckCrate() is { Length: > 0 } crateFailure) { Finish(crateFailure); return; }
            if (CheckTrap() is { Length: > 0 } trapFailure) { Finish(trapFailure); return; }
            BoardLift();
            // With --game-over-load a save is made first (card 1, file 3), which the game over's LOAD then loads.
            if (_gameOver && _loadAfter && _view.Battle?.Ashley != null && _view.SaveGame(1, 3, 1) != Error.Ok) { Finish("the save before the game over failed"); return; }
            if (_gameOver && _view.Battle?.Ashley is { } fallen) { _view.Battle.Strike(fallen, fallen.Hp); _felledAt = _frame; GD.Print("FIELD: Ashley is felled"); }
            if (_defeat && _view.Battle is { } field)
                foreach (var fighter in new System.Collections.Generic.List<FieldBattle.Combatant>(field.Combatants.Values))
                    if (fighter.Hostile && !fighter.Dead) { field.Strike(fighter, fighter.Hp); GD.Print($"FIELD: {fighter.Name} is felled"); }
            if (_nearEnemy && _view.Player is { } ashley && _view.Battle is { } battle)
                foreach (var fighter in battle.Combatants.Values)
                    if (fighter.Hostile && !fighter.Dead)
                    {
                        ashley.Position = fighter.Actor.Position + new Vector3(160, 0, 160);
                        _view.Gameplay.Place(ashley.Position);
                        break;
                    }
            var topics = _view.Content.Has("manual") ? _view.Content.Data("manual")["topics"].AsGodotArray() : new Godot.Collections.Array();
            int pictures = 0;
            foreach (var topic in topics) pictures += topic.AsGodotDictionary().GetValueOrDefault("pictures", new Godot.Collections.Array()).AsGodotArray().Count;
            GD.Print($"FIELD: the Quick Manual has {topics.Count} topics and {pictures} pictures");
            foreach (var topic in topics)
                if (topic.AsGodotDictionary()["title"].AsString().Length == 0 || topic.AsGodotDictionary()["lines"].AsGodotArray().Count == 0) { Finish("a Quick Manual topic is empty"); return; }
            if (topics.Count is not (0 or 14)) { Finish($"the Quick Manual has {topics.Count} topics"); return; }
        }
        // With captures, a view while the room's event plays, then the HUD and the menu once the player has control.
        if (!_view.Controlled && _frame == _eventFrame && _capture.Length != 0 && DisplayServer.GetName() != "headless")
        {
            Capture("room_event");
            GD.Print($"FIELD: the HUD shows during the event: {_view.HudShown}");
        }
        if (_watchEvery > 0 && _frame % _watchEvery == 0 && _capture.Length != 0 && DisplayServer.GetName() != "headless") Capture($"watch_{_frame:d5}");
        if (_status >= 0 && _view.Controlled && _view.Battle?.Ashley is { } afflicted && !afflicted.Has(_status))
        {
            afflicted.SetStatus(FieldBattle.StatusBit(_status), _view.Content.Field);
            GD.Print($"FIELD: Ashley takes status {_status}");
        }
        if (_shortcuts && (_view.Controlled || _shortcutStep > 0) && _view.Battle is { } shortcutBattle)
        {
            // Four chain abilities, the first three on the buttons in turn.
            if (_view.Abilities.Count == 0) { _view.Abilities.AddRange(new[] { 22, 23, 24, 25 }); shortcutBattle.Assign(_view.Abilities, _view.ChainButtons, _view.DefenseButtons); }
            switch (++_shortcutStep)
            {
                case 30: Input.ActionPress("ps_l2"); break;
                case 34: Capture("shortcut_sliding"); break;
                case 70: Capture("shortcut"); Tap("ps_right"); break;
                case 100:
                    if (!_view.MenuOpen) { Finish("the CHAIN shortcut opened nothing"); return; }
                    Capture("shortcut_chain");
                    Input.ActionRelease("ps_l2");
                    Tap("ps_circle");
                    break;
                case 120: Capture("abilities_chain"); Tap("ps_down"); break;
                case 140: Tap("ps_down"); break;
                case 160: Tap("ps_down"); break;
                case 180:
                    // The fourth, on no button, goes on Triangle; the second, which was there, comes off.
                    if (_view.ChainButtons[1] != 23) { Finish($"Triangle held {_view.ChainButtons[1]} before the change, not 23"); return; }
                    Tap("ps_triangle");
                    break;
                case 200:
                    Capture("abilities_assigned");
                    GD.Print($"FIELD: the chain buttons hold {string.Join(", ", _view.ChainButtons)}");
                    if (_view.ChainButtons[1] != 25 || Array.IndexOf(_view.ChainButtons, 23) >= 0) { Finish($"Triangle did not take the fourth ability: {string.Join(", ", _view.ChainButtons)}"); return; }
                    if (shortcutBattle.ChainSlots[1] != 25) { Finish("the battle does not hold the chosen buttons"); return; }
                    Tap("ps_cross");
                    break;
                case 220: Tap("ps_cross"); break;
                case 250:
                    if (_view.MenuOpen) { Finish("leaving the CHAIN shortcut's page left the menu open"); return; }
                    Input.ActionPress("ps_l2");
                    break;
                case 290: Tap("ps_up"); break;
                case 320:
                    if (!_view.MenuOpen) { Finish("the ITEMS shortcut opened nothing"); return; }
                    Capture("shortcut_items");
                    Input.ActionRelease("ps_l2");
                    Tap("ps_cross");
                    break;
                case 350: if (_view.MenuOpen) Tap("ps_cross"); break;
                case 380:
                    Capture("shortcut_after");
                    Finish(_view.MenuOpen ? "leaving the ITEMS shortcut's page left the menu open" : "");
                    return;
            }
            return;
        }
        if (_carry && _view.Controlled && _view.Player is { } carrier)
        {
            if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
            switch (++_carryStep)
            {
                case 1:
                {
                    _carryCrates = new List<(string, int, int, int, float, int)>(_view.Crates).Count;
                    _carryTiles = CrateTiles();
                    (int X, int Z)? block = null;
                    foreach (var crate in _view.Crates)
                        if (crate.Kind >= 4 && (block == null || new Vector2(crate.X * 128 + 64 - carrier.Position.X, crate.Z * 128 + 64 - carrier.Position.Z).Length() < new Vector2(block.Value.X * 128 + 64 - carrier.Position.X, block.Value.Z * 128 + 64 - carrier.Position.Z).Length()))
                            block = (crate.X, crate.Z);
                    if (block == null) { Finish("the room has no block to carry"); return; }
                    carrier.Facing = carrier.FacingToward(new Vector3(block.Value.X * 128 + 64, carrier.Position.Y, block.Value.Z * 128 + 64));
                    break;
                }
                case 20: Capture("carry_before"); Tap("ps_cross"); break;
                case 70:
                    if (new List<(string, int, int, int, float, int)>(_view.Crates).Count != _carryCrates - 1) { Finish("Cross at the block did not lift it"); return; }
                    Capture("carry_lifted");
                    // She turns about with it and sets it down behind where it stood.
                    carrier.Facing = Mathf.PosMod(carrier.Facing + 2048, 4096);
                    break;
                case 110: Capture("carry_turned"); break;
                case 130: Tap("ps_cross"); break;
                case 180:
                    Capture("carry_set");
                    if (new List<(string, int, int, int, float, int)>(_view.Crates).Count != _carryCrates) { Finish("Cross did not set the block down"); return; }
                    GD.Print($"FIELD: blocks stood at {_carryTiles}, now at {CrateTiles()}");
                    Finish(CrateTiles() == _carryTiles ? "the block was set down where it was lifted from, not behind her" : "");
                    return;
            }
            return;
        }
        if (_cube && _view.Controlled && _view.Player is { } striker && _view.Battle is { Ashley: { } cubeStriker } cubeBattle)
        {
            if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
            switch (++_cubeStep)
            {
                case 1:
                {
                    _carryCrates = new List<(string, int, int, int, float, int)>(_view.Crates).Count;
                    _carryTiles = CrateTiles();
                    (int X, int Z)? nearest = null;
                    foreach (var crate in _view.Crates)
                        if (nearest == null || new Vector2(crate.X * 128 + 64 - striker.Position.X, crate.Z * 128 + 64 - striker.Position.Z).Length() < new Vector2(nearest.Value.X * 128 + 64 - striker.Position.X, nearest.Value.Z * 128 + 64 - striker.Position.Z).Length())
                            nearest = (crate.X, crate.Z);
                    if (nearest == null) { Finish("the room has no crate to strike"); return; }
                    striker.Facing = striker.FacingToward(new Vector3(nearest.Value.X * 128 + 64, striker.Position.Y, nearest.Value.Z * 128 + 64));
                    break;
                }
                case 10: Tap("ps_circle"); break;
                case 60: Tap("ps_circle"); break;
                case 75:
                {
                    Capture("cube_sphere");
                    string readout = _view.SystemBox.Text;
                    GD.Print($"FIELD: the sphere on the crate reads \"{readout.Replace('\n', '/')}\"");
                    if (!cubeBattle.Paused) { Finish("Circle with the weapon drawn did not open the sphere on the crate"); return; }
                    if (!readout.StartsWith("Cube", StringComparison.Ordinal)) { Finish($"the sphere did not name the crate Cube: \"{readout}\""); return; }
                    _cubeRisk = cubeStriker.Risk;
                    _cubeRiskBefore = cubeStriker.Risk;
                    _cubeCrates = new List<(string Id, int Kind, int X, int Z, float Bottom, int Orientation)>(_view.Crates);
                    Tap("ps_circle");
                    break;
                }
                case 85: Capture("cube_strike"); break;
                case > 75 and < 160: _cubeRisk = Math.Max(_cubeRisk, cubeStriker.Risk); break;
                case 160:
                    Capture("cube_after");
                    {
                        // A blow that lands breaks a kind 2 or 4 crate, gone from the room, and the crates stacked on it
                        // come down one crate's height (0x8009f530); any other crate takes the flash alone and nothing
                        // moves (BATTLE.PRG 0x80085718, 0x800838ec).
                        var after = new List<(string Id, int Kind, int X, int Z, float Bottom, int Orientation)>(_view.Crates);
                        var gone = _cubeCrates.FindAll(before => !after.Exists(now => now.Id == before.Id));
                        if (gone.Count > 1 || gone.Exists(crate => crate.Kind is not (2 or 4))) { Finish("striking a crate broke the wrong crates"); return; }
                        if (after.Exists(now => _cubeCrates.Find(before => before.Id == now.Id) is var was && (was.X != now.X || was.Z != now.Z || was.Bottom + (gone.Exists(broken => broken.X == was.X && broken.Z == was.Z && broken.Bottom > was.Bottom) ? 128 : 0) != now.Bottom))) { Finish("striking the crate moved a crate, or left a stack standing on nothing"); return; }
                        GD.Print(gone.Count == 1 ? $"FIELD: the blow broke a kind {gone[0].Kind} crate" : "FIELD: the crate took the blow and stood");
                    }
                    // RISK rises with the blow and falls back soon after (footage: RISK 1, then ZERO).
                    Finish(_cubeRisk <= _cubeRiskBefore ? "striking the crate did not raise her RISK" : "");
                    return;
            }
            return;
        }
        if (_revealTraps && _view.Controlled)
        {
            if (_revealedAt < 0)
            {
                int count = 0;
                foreach (var value in _view.Document.GetValueOrDefault("traps", new Godot.Collections.Array()).AsGodotArray())
                {
                    var trap = value.AsGodotDictionary();
                    if (trap["type"].AsInt32() >= 16) continue;
                    var tile = trap["tile"].AsGodotArray();
                    GD.Print($"FIELD: trap type {trap["type"]} at {tile[0]}, {tile[1]}");
                    count++;
                }
                if (count == 0) { Finish("the room has no traps to reveal"); return; }
                // As the Eye of Argon does: its effect (188) plays at each trap, and its action 2 reveals it.
                if (_view.Player is { } seer && _view.SeeTraps(188, seer).Count != count) { Finish("the Eye of Argon's effect did not play at every trap"); return; }
                // Ashley stands beside the first trap, on a neighbouring tile of its floor, so its panel is in view.
                if (_view.Player is { } stander)
                    foreach (var value in _view.Document["traps"].AsGodotArray())
                    {
                        var trap = value.AsGodotDictionary();
                        if (trap["type"].AsInt32() >= 16) continue;
                        var tile = trap["tile"].AsGodotArray();
                        int tx = tile[0].AsInt32(), tz = tile[1].AsInt32();
                        float? floor = _view.FloorAt(new Vector3(tx * 128 + 64, 0, tz * 128 + 64));
                        foreach (var (dx, dz) in new[] { (0, 1), (-1, 0), (1, 0), (0, -1) })
                        {
                            var spot = new Vector3((tx + dx) * 128 + 64, 0, (tz + dz) * 128 + 64);
                            if (_view.FloorAt(spot) is not { } beside || beside != floor) continue;
                            stander.Position = spot with { Y = beside };
                            _view.Gameplay.Place(stander.Position);
                            break;
                        }
                        break;
                    }
                GD.Print($"FIELD: revealed {count} traps");
                _revealedAt = _frame;
            }
            if (_frame - _revealedAt == 80)
            {
                var camera = _view.Field.Camera;
                foreach (var value in _view.Document.GetValueOrDefault("traps", new Godot.Collections.Array()).AsGodotArray())
                {
                    var tile = value.AsGodotDictionary()["tile"].AsGodotArray();
                    var centre = new Vector3(tile[0].AsInt32() * 128 + 64, _view.FloorAt(new Vector3(tile[0].AsInt32() * 128, 0, tile[1].AsInt32() * 128)) ?? 0, tile[1].AsInt32() * 128 + 64);
                    var world = EventActor.RetailSpace * centre;
                    GD.Print($"FIELD: trap panel at {centre} shows at {camera.UnprojectPosition(world)}{(camera.IsPositionBehind(world) ? " (behind)" : "")}; Ashley at {_view.Player?.Position} shows at {(_view.Player is { } a ? camera.UnprojectPosition(EventActor.RetailSpace * a.Position) : default)}");
                }
            }
            if (_frame - _revealedAt >= 80 && (_frame - _revealedAt) % 4 == 0) Capture($"traps_revealed_{_frame - _revealedAt:d3}");
            if (_frame - _revealedAt >= 120) Finish(_view.RevealedTraps > 0 ? "" : "no trap was revealed");
            return;
        }
        if (_movement && _view.Controlled && _view.Battle is { Ashley: not null } bodies) { Finish(CheckMovement(bodies)); return; }
        if (_fight && _view.Controlled && _view.Battle is { Ashley: { } brawler } brawl)
        {
            if (_view.Abilities.Count == 0) { _view.Abilities.Add(22); brawl.Assign(_view.Abilities, _view.ChainButtons, _view.DefenseButtons); }
            if (brawl.GameOver) { Finish("Ashley fell in the room's fight"); return; }
            if (_swoop)
            {
                brawler.Hp = brawler.MaxHp;
                int swoop = _view.Flags[166];
                var boss = brawl.Combatants.Values.FirstOrDefault(c => c != brawler && c.Hostile && !c.Dead);
                // The boss starts at three quarters of its hit points, so that it asks for its first swoop once its
                // 900-frame cooldown is over rather than after a long fight.
                if (_swoopAt < 0 && boss != null) boss.Hp = boss.MaxHp * 3 / 4;
                if (_swoopAt < 0) _swoopAt = _frame;
                if (swoop.ToString() != _swoopTrail[^1..] || (_frame - _swoopAt) % 120 == 0)
                    GD.Print($"FIELD: variable 166 is {swoop} at frame {_frame}, the boss at {boss?.Actor.Position} with {boss?.Hp} hit points, Ashley at {brawler.Actor.Position}");
                if (swoop.ToString() != _swoopTrail[^1..]) _swoopTrail += swoop;
                if (System.Text.RegularExpressions.Regex.IsMatch(_swoopTrail, "14[56]0")) { Finish(""); return; }
                if (_frame - _swoopAt > 20000) { Finish($"the swoop did not run its course (166 went {_swoopTrail})"); return; }
                if (swoop is 1 or 5 or 6) { Steer(Vector2.Zero); return; }
            }
            if (brawl.HostileCount() == 0) { Steer(Vector2.Zero); if (++_calm > 180) Finish(""); return; }
            _calm = 0;
            // The sphere is held open long enough to be seen whole, and captured once it is.
            if (!brawl.Paused) _sphereFrames = 0;
            else if (++_sphereFrames < 40) { if (_sphereFrames == 36) Capture($"fight_sphere_{_frame:d5}"); return; }
            if (_art >= 0 && _artAt < 0 && brawl.Drawn && _view.UseArt(_art) is { Length: 0 })
            {
                _artAt = _frame;
                _artTargetHp = brawl.Combatants.Values.Where(c => c.Hostile).Sum(c => c.Hp);
                GD.Print($"FIELD: Break Art {_art} under way at frame {_frame}, its effect {_view.Content.Data("spells")["actions"].AsGodotDictionary()[_art.ToString()].AsGodotDictionary().GetValueOrDefault("visual", 0)}");
            }
            if (_artAt >= 0)
            {
                int since = _frame - _artAt;
                if (since % 2 == 0 && since <= 240) Capture($"art_{_art}_{since:d3}");
                int hp = brawl.Combatants.Values.Where(c => c.Hostile).Sum(c => c.Hp);
                if (hp < _artTargetHp)
                {
                    GD.Print($"FIELD: Break Art {_art} landed {since} frames after it began, with {_view.EffectsPlaying} effects playing");
                    if (_view.EffectsPlaying == 0) { Finish($"Break Art {_art} landed with no effect playing"); return; }
                    _artTargetHp = hp;
                }
                if (since > 10 && !brawl.Busy) { GD.Print($"FIELD: Break Art {_art} was over {since} frames after it began"); Finish(""); return; }
                if (since >= 240) { Finish($"Break Art {_art} was not over after 240 frames"); return; }
                return;
            }
            if (brawl.AshleyStrikeFrame is >= 0 and <= 2) Tap("ps_circle");
            if (brawl.Grades > _gradesSeen) { _gradesSeen = brawl.Grades; GD.Print($"FIELD: a chain press graded {brawl.LastGrade} at frame {_frame}"); }
            Fight(brawl, brawler, _frame);
            return;
        }
        if (_eventEvery > 0 && !_view.Controlled && _frame % _eventEvery == 0 && _capture.Length != 0 && DisplayServer.GetName() != "headless")
        {
            Capture($"event_{_frame:d5}");
            // An actor whose bones all sit at rest shows its bind pose: its clip is not reaching the skeleton.
            foreach (var actor in _view.Actors.Values)
                if (actor.Visible && actor.Player != null && actor.Skeleton is { } bones && bones.GetBoneCount() > 2)
                {
                    bool rest = true;
                    for (int b = 1; b < bones.GetBoneCount() && rest; b++) rest = bones.GetBonePoseRotation(b).IsEqualApprox(bones.GetBoneRest(b).Basis.GetRotationQuaternion());
                    if (rest) GD.Print($"FIELD: at frame {_frame} {actor.Id} (character {actor.Character:X2}) stands in its bind pose ({actor.Player.AssignedAnimation})");
                }
        }
        if (_play.Length != 0 && _view.Controlled)
        {
            GD.Print($"FIELD: playing {_play}");
            _view.Play(_play);
            _play = "";
            _played = true;
            _nodeFrame = _frame;
            return;
        }
        if (_view.Controlled && _capture.Length != 0 && DisplayServer.GetName() != "headless" && !_gameOver && _play.Length == 0 && !_played)
        {
            if (_controlAt == 0) _controlAt = _frame;
            int since = _frame - _controlAt;
            if (since == 30)
            {
                Capture("room_control");
                var camera = _view.Field.Camera;
                // Retail draws models at half size: Ashley stands about 250 units tall.
                if (_view.Player?.Skeleton is { } bones)
                {
                    float high = float.MinValue;
                    // Measured from where she stands, so that rooms with raised floors read the same.
                    float feet = _view.Player.Node.GlobalPosition.Y;
                    for (int b = 0; b < bones.GetBoneCount(); b++) high = Math.Max(high, ((bones.GlobalTransform * bones.GetBoneGlobalPose(b).Origin).Y - feet) * 128);
                    GD.Print($"FIELD: in control at {_view.Player.Position} over a floor at {_view.FloorHeight(_view.Player.Position):f0}, Ashley's highest bone is {high:f0} units up, the camera {camera.GlobalPosition.DistanceTo(_view.Player.Node.GlobalPosition) * 128:f0} units from her");
                    // Enhanced characters: Ashley's textures four times their size and smooth, lit per pixel, her head
                    // divided; Original: the disc's textures, sharp and unshaded.
                    {
                        var mesh = _view.Player.Node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().First(m => m.Name != "Shadow" && m.Mesh != null);
                        var material = (StandardMaterial3D)mesh.Mesh.SurfaceGetMaterial(0);
                        var original = mesh.HasMeta("vagrant_original_mesh") ? mesh.GetMeta("vagrant_original_mesh").As<ArrayMesh>() : (ArrayMesh)mesh.Mesh;
                        var disc = material.HasMeta("vagrant_original_texture") ? material.GetMeta("vagrant_original_texture").As<Texture2D>() : material.AlbedoTexture;
                        int width = material.AlbedoTexture?.GetWidth() ?? 0, originalWidth = disc?.GetWidth() ?? 0;
                        int Faces(ArrayMesh m)
                        {
                            int count = 0;
                            for (int i = 0; i < m.GetSurfaceCount(); i++) count += m.SurfaceGetArrayIndexLen(i) > 0 ? m.SurfaceGetArrayIndexLen(i) / 3 : m.SurfaceGetArrayLen(i) / 3;
                            return count;
                        }
                        int faces = Faces((ArrayMesh)mesh.Mesh), originalFaces = Faces(original);
                        GD.Print($"FIELD: characters drawn {(CharacterQuality.Enhanced ? "enhanced" : "original")}: texture {width} wide ({originalWidth} on the disc), {faces} faces ({originalFaces}), {material.ShadingMode}");
                        bool right = CharacterQuality.Enhanced
                            ? width == originalWidth * 4 && faces > originalFaces && material.ShadingMode == BaseMaterial3D.ShadingModeEnum.PerPixel
                            : width == originalWidth && faces == originalFaces && material.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded && material.TextureFilter == BaseMaterial3D.TextureFilterEnum.Nearest;
                        if (!right) { Finish("the characters are not drawn as the Settings choose"); return; }
                    }
                    if (high > 300) { Finish($"Ashley is drawn {high:f0} units tall"); return; }
                }
            }
            if (since == 40) Tap("ps_triangle");
            if (since == 70) Capture("room_menu");
            if (since == 80) Tap("ps_circle");
            if (since == 110) Capture("room_items");
            if (since == 120) Tap("ps_cross");
            if (since == 140) Tap("ps_down");
            if (since == 160) Tap("ps_circle");
            // STATUS (MENU4.PRG): its own screen, the pointer on the first limb, whose condition the help reads.
            if (since == 190)
            {
                Capture("room_status");
                var (help, _) = _view.ItemHelpShown;
                GD.Print($"FIELD: STATUS reads \"{help}\"");
                if (_view.MenuPage != 4 || !help.Contains(" / ", StringComparison.Ordinal)) { Finish($"STATUS did not open its screen (page {_view.MenuPage}, help \"{help}\")"); return; }
            }
            // Square shows the limb's defences, AFFINITY first, its first row's help; Cross goes back, and again leaves.
            if (since == 192) Tap("ps_square");
            if (since == 198)
            {
                Capture("room_status_defences");
                var (help, _) = _view.ItemHelpShown;
                GD.Print($"FIELD: STATUS's defences read \"{help.Replace("\n", " | ")}\"");
                if (!help.Contains("defense", StringComparison.Ordinal)) { Finish($"Square on a limb did not show its defences (help \"{help}\")"); return; }
            }
            if (since == 200) Tap("ps_cross");
            // Circle on the limb opens the equipment on its armour (EQUIP's slot 2 for R.ARM); Cross twice goes back.
            if (since == 205) Tap("ps_circle");
            if (since == 209)
            {
                Capture("room_status_equipment");
                var (stage, slot) = _view.StatusEquipment;
                GD.Print($"FIELD: STATUS's equipment opens at stage {stage} on slot {slot}");
                if (stage != 1 || slot != 2) { Finish($"Circle on a limb did not open its equipment (stage {stage}, slot {slot})"); return; }
            }
            if (since == 210) Tap("ps_cross");
            if (since == 215) Tap("ps_cross");
            if (since == 220) Tap("ps_down");
            if (since == 240) Tap("ps_circle");
            // MAP (MENU5.PRG): its own screen, on the room Ashley is in.
            if (since == 270)
            {
                Capture("room_map");
                GD.Print($"FIELD: the map names \"{_view.MapRoomName}\"");
                if (_view.MenuPage == 5 && _view.Content.Has("map_menu") && _view.MapRoomName.Length == 0) { Finish("the map screen named no room"); return; }
            }
            if (since == 280) Tap("ps_triangle");
            if (since == 300) Tap("ps_triangle");
            if (since == 320) Tap("ps_start");
            if (since == 350) { Capture("room_look"); GD.Print($"FIELD: looking around {_view.LookingAround}"); }
            if (since == 360) Tap("ps_start");
            if (since == 380 && _view.LookingAround) { Finish("Start did not end free-look"); return; }
            // The menu opens on the Map row it was left at; four rows down is the Quick Manual.
            if (since == 390) Tap("ps_triangle");
            if (since is 410 or 425 or 440 or 455) Tap("ps_down");
            if (since == 470) Tap("ps_circle");
            if (since == 500) Capture("room_manual");
            if (since == 510) Tap("ps_circle");
            if (since == 540) Capture("room_manual_topic");
            if (since is 550 or 565 or 580) Tap("ps_triangle");
            // A chest with something in it opens on the gadget bag: Circle takes the chosen thing, Square the rest.
            if (since == 600 && OpenChest() is { } chest) { _chest = chest; _chestCarried = _view.Equipment.Count + _view.Items.Count; }
            // The bag shows once the chest's lid has swung open.
            if (since == 630 && _chest != null && !_view.MenuOpen && _lidWait++ < 300) { _controlAt++; return; }
            if (since == 630 && _chest != null) Capture("room_chest");
            if (since == 640 && _chest != null) Tap("ps_circle");
            if (since == 660 && _chest != null) Tap("ps_square");
            if (since == 690 && _chest != null)
            {
                if (_view.MenuOpen || _view.ChestContents(_chest).Count != 0) { Finish("the gadget bag did not take everything and close"); return; }
                if (_view.Equipment.Count + _view.Items.Count <= _chestCarried) { Finish("the gadget bag took nothing into the inventory"); return; }
                GD.Print("FIELD: the gadget bag took the chest's contents");
            }
            // ITEMS opens on OPTIONS: EQUIP shows the slots, and Circle a slot's list. The menu is left on the Quick
            // Manual's row, six usable rows below ITEMS.
            if (since == 700) Tap("ps_triangle");
            if (since is 715 or 730 or 745 or 760 or 775 or 790) Tap("ps_up");
            if (since == 805) Tap("ps_circle");
            if (since == 830) Tap("ps_circle");
            if (since == 860) Capture("room_equip");
            if (since == 870) Tap("ps_circle");
            if (since == 900) Capture("room_equip_list");
            // SETUP (below EQUIP) over the backdrop: its commands, those that need a workshop greyed elsewhere with
            // the reason under CAUTION; ATTACH GEMS, WEAPONS, and the weapons, one that takes no gem greyed and saying so
            // (footage). Then back out to OPTIONS; the main steps wait meanwhile.
            if (since == 940 && _setupStep < 200 && _view.MenuOpen)
            {
                _controlAt++;
                switch (++_setupStep)
                {
                    case 1: Tap("ps_down"); break;
                    case 15: Tap("ps_circle"); break;
                    case 40:
                    {
                        Capture("room_setup");
                        var (help, caution) = _view.ItemHelpShown;
                        bool refused = help.Contains("workshop", StringComparison.Ordinal) && caution;
                        if (refused == _view.Workshop) { Finish($"SETUP's ASSEMBLE reads \"{help}\" (caution {caution}) {(_view.Workshop ? "at" : "away from")} a workshop"); return; }
                        GD.Print($"FIELD: SETUP's ASSEMBLE reads \"{help.Replace("\n", " ")}\"");
                        break;
                    }
                    case 45: Tap("ps_down"); break;
                    case 60: Tap("ps_circle"); break;
                    case 85:
                    {
                        Capture("room_setup_attach");
                        var (help, _) = _view.ItemHelpShown;
                        if (!help.StartsWith("Attach gems to weapons", StringComparison.Ordinal)) { Finish($"ATTACH GEMS' WEAPONS reads \"{help}\""); return; }
                        break;
                    }
                    case 90: Tap("ps_circle"); break;
                    case 115:
                    {
                        Capture("room_setup_weapons");
                        var (help, caution) = _view.ItemHelpShown;
                        if (!(caution ? help.StartsWith("Gems cannot be attached", StringComparison.Ordinal) : help.StartsWith("Number of gems attachable", StringComparison.Ordinal))) { Finish($"ATTACH: Weapon's first weapon reads \"{help}\" (caution {caution})"); return; }
                        GD.Print($"FIELD: ATTACH: Weapon's first weapon reads \"{help.Replace("\n", " ")}\"");
                        break;
                    }
                    case 120 or 135 or 150: Tap("ps_cross"); break;
                    case 170: _setupStep = 200; break;
                }
                return;
            }
            if (since is 910 or 925 or 940 or 955) Tap("ps_cross");
            if (since == 975 && _view.MenuOpen) { Finish("Cross did not back out of EQUIP and close the menu"); return; }
            // A container: its menu, STORE's list, a thing picked, the confirmation, and the thing stored.
            if (since == 990) { _storedBefore = _view.Stored.Count + _view.StoredItems.Count; _view.OpenContainer(); }
            // The container asks first, YES and NO over the field (MENU7.PRG); YES opens it.
            if (since == 997) Capture("room_container_check");
            if (since == 1000) Tap("ps_circle");
            // Then the memory cards are read, "Accessing MEMORY CARDs..." under CAUTION, before its menu shows.
            if (since == 1006)
            {
                Capture("room_container_access");
                if (!_view.ContainerAccessing || !_view.ContainerCaution.StartsWith("Accessing MEMORY CARDs", StringComparison.Ordinal)) { Finish($"the container did not read the cards first (\"{_view.ContainerCaution}\")"); return; }
            }
            if (since == 1008 && _view.ContainerAccessing && _accessWait++ < 900) { _controlAt++; return; }
            if (since == 1010) Capture("room_container");
            if (since == 1020) Tap("ps_down");
            if (since == 1035) Tap("ps_circle");
            // STORE opens on what has been picked (STORED, empty); left of it is MISC.
            if (since == 1045) Capture("room_container_stored_page");
            if (since == 1050) Tap("ps_left");
            if (since == 1140) Capture("room_container_store");
            // Circle offers the thing's actions, STORE first (footage: STORE, DISCARD, SORT), and STORE picks it.
            if (since == 1150) Tap("ps_circle");
            if (since == 1157)
            {
                Capture("room_container_actions");
                var (help, _) = _view.ItemHelpShown;
                if (!help.StartsWith("Store an item", StringComparison.Ordinal)) { Finish($"the container's STORE reads \"{help}\""); return; }
            }
            if (since == 1160) Tap("ps_circle");
            if (since == 1172) Tap("ps_cross");
            if (since == 1180) Capture("room_container_confirm");
            if (since == 1190) Tap("ps_circle");
            if (since == 1210) Capture("room_container_stored");
            if (since == 1220)
            {
                if (_view.Stored.Count + _view.StoredItems.Count <= _storedBefore) { Finish("the container stored nothing"); return; }
                GD.Print($"FIELD: the container holds {_view.Stored.Count + _view.StoredItems.Count} things");
                Tap("ps_cross");
            }
            // Leaving asks whether to save, YES chosen (MENU7.PRG); YES opens the save.
            if (since == 1240)
            {
                Capture("room_container_leave");
                if (!_view.ContainerCaution.StartsWith("The container transaction", StringComparison.Ordinal)) { Finish($"leaving the container asked \"{_view.ContainerCaution}\""); return; }
                Tap("ps_circle");
            }
            // Ending the transfer saves: SAVE and the slots, the card checked, its files, the save to an empty file.
            if (since == 1270 && _view.CardStage != "Slots" && _saveWait++ < 1800) { _controlAt++; return; }
            if (since == 1270) { Capture("room_save_slots"); Tap("ps_circle"); }
            if (since == 1300) Capture("room_save_checking");
            if (since == 1400 && _view.CardStage != "Files" && _saveWait++ < 1800) { _controlAt++; return; }
            if (since == 1400) Capture("room_save_files");
            if (since == 1410) Tap("ps_circle");
            if (since == 1490) Capture("room_save_saving");
            if (since == 1600) Capture("room_save_saved");
            // Saving takes its time on the screen: the step waits (holding the count) until the file is written.
            if (since == 1620 && FieldView.ReadCardFile(1, 1) == null && _saveWait++ < 900) { _controlAt++; return; }
            if (since == 1620)
            {
                if (FieldView.ReadCardFile(1, 1) is not { } first) { Finish("the container's save wrote no file"); return; }
                if (first["location"].AsInt32() != _view.ContainerLocation) { Finish($"the save's location is {first["location"]}, not the container's"); return; }
                GD.Print($"FIELD: saved to card 1 file 1 (location {first["location"]}, MAP {first["map_percent"]}%, save {first["save_count"]})");
                Tap("ps_circle");
            }
            // A press the screen was not yet taking is pressed again, as a player would.
            if (since is > 1620 and < 1680 && since % 15 == 0 && _view.MenuOpen) Tap("ps_circle");
            if (since == 1680 && _view.MenuOpen) { Finish("the container's menu did not close after saving"); return; }
            // Again, over the file just saved: it asks first, and YES overwrites.
            if (since == 1690) _view.OpenContainer();
            if (since == 1700) Tap("ps_circle");
            if (since == 1708 && _view.ContainerAccessing && _accessWait++ < 1800) { _controlAt++; return; }
            if (since == 1710) Tap("ps_cross");
            if (since == 1730) Tap("ps_circle");
            if (since == 1750 && _view.CardStage != "Slots" && _saveWait++ < 1800) { _controlAt++; return; }
            if (since == 1750) Tap("ps_circle");
            if (since == 1850 && _view.CardStage != "Files" && _saveWait++ < 1800) { _controlAt++; return; }
            if (since == 1850) Capture("room_save_files_used");
            if (since == 1860) Tap("ps_circle");
            if (since == 1890 && _view.CardStage != "Overwrite" && _saveWait++ < 1800) { _controlAt++; return; }
            if (since == 1890) Capture("room_save_overwrite");
            if (since == 1900) Tap("ps_up");
            if (since == 1910) Tap("ps_circle");
            if (since == 2120 && FieldView.ReadCardFile(1, 1)?["save_count"].AsInt32() != 2 && _saveWait++ < 1800)
            {
                // A press the prompt did not take leaves NO chosen (back on the files) or the prompt still waiting: as a
                // player would, the file is chosen again, or Up and Circle pressed again.
                _controlAt += _view.CardStage switch { "Files" => 2120 - 1855, "Overwrite" => 2120 - 1895, _ => 1 };
                return;
            }
            if (since == 2120)
            {
                if (FieldView.ReadCardFile(1, 1) is not { } second || second["save_count"].AsInt32() != 2) { Finish("YES did not overwrite the file"); return; }
                if (FieldView.ReadSave()?["save_count"].AsInt32() != 2) { Finish("Continue would not resume the newest save"); return; }
                GD.Print("FIELD: the file was overwritten and is the one Continue resumes");
                Tap("ps_cross");
            }
            if (since == 2180 && _view.MenuOpen) { Finish("the container's menu did not close after overwriting"); return; }
            // Circle draws the weapon and Circle again opens the sphere: with nothing in range it says so. Cross closes
            // it and sheathes.
            // Only where no enemy is about, which would otherwise be fighting by now.
            bool quiet = _view.Battle?.HostileCount() == 0;
            if (since == 2190 && quiet) Tap("ps_circle");
            if (since == 2260 && quiet) Tap("ps_circle");
            if (since == 2300 && quiet)
            {
                // With an enemy in range the sphere opens on it and reads out its part and target instead.
                if (_view.Battle?.Paused == true)
                {
                    Capture("room_sphere");
                    if (_view.SystemBox.Text.Split('\n').Length != 2) { Finish($"the sphere's readout is \"{_view.SystemBox.Text}\""); return; }
                    GD.Print($"FIELD: the sphere reads \"{_view.SystemBox.Text.Replace("\n", " | ")}\"");
                }
                else
                {
                    Capture("room_no_target");
                    if (!_view.SystemBox.Text.Contains("No target in range", StringComparison.Ordinal)) { Finish($"the sphere did not say there is no target: \"{_view.SystemBox.Text}\""); return; }
                    GD.Print("FIELD: with nothing in range the sphere says so");
                }
            }
            if (since == 2310 && quiet) Tap("ps_cross");
            if (since == 2340 && quiet) Tap("ps_cross");
            // ITEMS, MISC (left of OPTIONS), Circle on the first thing: its actions, USE to SORT, at the left.
            if (since == 2400) Tap("ps_triangle");
            if (since == 2440) Tap("ps_circle");
            if (since == 2480) Tap("ps_left");
            if (since == 2510) Tap("ps_circle");
            if (since == 2550) Capture("room_item_actions");
            if (since == 2560) Tap("ps_cross");
            // GEMS (left of MISC), Circle on the first gem: SEARCH, DISCARD and SORT. A loose gem is no part of a set,
            // so SEARCH stands greyed with that reason under CAUTION (footage: Braveheart).
            if (since == 2566) Tap("ps_left");
            if (since == 2572 && _view.MenuOpen && _view.Gems().Count > 0) Tap("ps_circle");
            if (since == 2580 && _view.MenuOpen && _view.Gems().Count > 0)
            {
                Capture("room_gem_actions");
                var (help, caution) = _view.ItemHelpShown;
                if (!caution || !help.Contains("not part of a set", StringComparison.Ordinal)) { Finish($"a loose gem's SEARCH reads \"{help}\" (caution {caution})"); return; }
                GD.Print($"FIELD: a loose gem's SEARCH reads \"{help.Replace("\n", " ")}\"");
            }
            if (since == 2586 && _view.MenuOpen && _view.Gems().Count > 0) Tap("ps_cross");
            if (since == 2593) Tap("ps_cross");
            if (since == 2600) Tap("ps_cross");
            if (since == 2640 && _view.MenuOpen) { Finish("the menu did not close after the item actions"); return; }
            // Where no enemy is about, a MISC thing used: the menu closes, the system box names its effect, and the HUD
            // is put away while the effect plays and comes back after (footage: an Elixir of Queens' "Item Effect").
            if (since == 2641 && _itemStep < 800 && _view.Battle?.HostileCount() == 0 && _view.Items.Count > 0)
            {
                _controlAt++;
                switch (++_itemStep)
                {
                    case 1: Tap("ps_triangle"); break;
                    case 40: Tap("ps_circle"); break;
                    // The menu opens on OPTIONS: two to the right is BLADES.
                    case 50: Tap("ps_right"); break;
                    case 60: Tap("ps_right"); break;
                    // BLADES: a blade a carried weapon is built from carries SET, the held weapon's EQP (footage: the
                    // container's BLADES, "SET" Scimitar, "EQP" Hand Axe).
                    case 74:
                    {
                        var badges = _view.ItemRows.Select(r => r.Badge).ToList();
                        GD.Print($"FIELD: BLADES' badges: {string.Join(", ", _view.ItemRows.Select(r => $"{r.Name} {(r.Badge == 2 ? "EQP" : r.Badge == 1 ? "SET" : "-")}"))}");
                        Capture("room_blades");
                        if (!badges.Contains(2)) { Finish("the held weapon's blade has no EQP badge"); return; }
                        break;
                    }
                    case 80: Tap("ps_left"); break;
                    case 86: Tap("ps_left"); break;
                    case 92: Tap("ps_left"); break;
                    case 100: Tap("ps_circle"); break;
                    case 120: Tap("ps_circle"); break;
                    // It is aimed at Ashley first ("Ashley Riot 100%", "TARGET: Single"), and Circle uses it.
                    case 128:
                    {
                        string aim = _view.SystemBox.Text.Replace("\n", " | ");
                        if (_view.MenuOpen || !aim.Contains("TARGET:", StringComparison.Ordinal) || !aim.Contains("100%", StringComparison.Ordinal)) { Finish($"USE did not aim the item at Ashley (\"{aim}\")"); return; }
                        GD.Print($"FIELD: the item is aimed: \"{aim}\"");
                        Capture("room_item_aim");
                        Tap("ps_circle");
                        break;
                    }
                    case 140:
                        if (_view.MenuOpen) { Finish("USE did not leave the menu"); return; }
                        if (_view.Battle.Hud is { Acting: false }) { Finish("the HUD stayed up while the item's effect played"); return; }
                        // The item's action closes the camera in on her (its record's camera 1: a projection of 768).
                        if (_view.Gameplay.Projection != 768) { Finish($"the item's action did not close the camera in (projection {_view.Gameplay.Projection})"); return; }
                        GD.Print($"FIELD: an item used reads \"{_view.SystemBox.Text.Replace("\n", " ")}\" with the HUD put away, the camera closed in");
                        Capture("room_item_cast");
                        break;
                    // Its casting (as long as its name shows) and its effect take some 140 frames of 30 Hz play.
                    case 799:
                        if (_view.Battle.Hud is { Acting: true }) { Finish("the HUD did not come back after the item's effect"); return; }
                        if (_view.Gameplay.Projection != GameplayCamera.DefaultProjection) { Finish("the camera did not go back after the item's action"); return; }
                        _itemStep = 800;
                        break;
                }
                return;
            }
            // These menus open only where no enemy is about (Triangle does nothing while one fights).
            if (since == 2645) _menusCalm = _view.Battle?.HostileCount() == 0;
            // BATTLE ABILITIES, once she has one (a chain ability given here), is the row above ITEMS: its two kinds, NEXT and help.
            if (_menusCalm && since == 2650 && _view.Abilities.Count == 0) { _view.Abilities.Add(22); _view.Battle?.Assign(_view.Abilities, _view.ChainButtons, _view.DefenseButtons); }
            if (_menusCalm && since == 2660) Tap("ps_triangle");
            // Up until BATTLE ABILITIES (row 2) is chosen, wherever the list was left.
            if (_menusCalm && since is >= 2680 and < 2720 && since % 10 == 0 && _view.MenuRow != 2) Tap("ps_up");
            if (_menusCalm && since == 2720 && _view.MenuRow != 2) { Finish($"the command list did not reach BATTLE ABILITIES (row {_view.MenuRow})"); return; }
            if (_menusCalm && since == 2725) Tap("ps_circle");
            if (_menusCalm && since == 2742) Capture("room_abilities");
            if (_menusCalm && since == 2750) Tap("ps_down");
            if (_menusCalm && since == 2770) Capture("room_abilities_defense");
            if (_menusCalm && since is 2780 or 2800) Tap("ps_cross");
            if (_menusCalm && since == 2830 && _view.MenuOpen) { Finish("the menu did not close after BATTLE ABILITIES"); return; }
            // A room with a magic circle: Ashley is set down on it, so DATA's SAVE below has a save point to save at.
            if (_menusCalm && since == 2835 && _view.Player is { } walker)
                foreach (var value in _view.Document.GetValueOrDefault("traps", new Godot.Collections.Array()).AsGodotArray())
                {
                    var trap = value.AsGodotDictionary();
                    if (trap["type"].AsInt32() != 16) continue;
                    var tile = trap["tile"].AsGodotArray();
                    var spot = new Vector3(tile[0].AsInt32() * 128 + 64, 0, tile[1].AsInt32() * 128 + 64);
                    walker.Position = spot with { Y = _view.FloorHeight(spot) };
                    break;
                }
            // DATA, four rows below BATTLE ABILITIES: SAVE and LOAD over the backdrop, with the play time.
            if (_menusCalm && since == 2840) Tap("ps_triangle");
            // Down until DATA (row 6) is chosen, wherever the list was left.
            if (_menusCalm && since is >= 2860 and < 2930 && since % 10 == 0 && _view.MenuRow != 6) Tap("ps_down");
            if (_menusCalm && since == 2930 && _view.MenuRow != 6) { Finish($"the command list did not reach DATA (row {_view.MenuRow})"); return; }
            if (_menusCalm && since == 2930) Tap("ps_circle");
            if (_menusCalm && since == 2960) Capture("room_data");
            // On a magic circle SAVE searches the cards first, "Accessing MEMORY CARDs...", about 8.2 seconds as in
            // footage, before the slots; Cross then goes back to DATA. The room's steps wait meanwhile.
            if (_menusCalm && since == 2965 && _view.SavePoint != 0 && _dataSaveStep < 2000)
            {
                _controlAt++;
                ++_dataSaveStep;
                if (_dataSaveStep == 1) Tap("ps_circle");
                else if (_dataSaveStep == 30 && !_view.CardStage.StartsWith("Opening", StringComparison.Ordinal)) { Finish($"DATA's SAVE showed {_view.CardStage}, not the cards being searched"); return; }
                else if (_dataSaveStep > 30 && _dataSaveStep < 1900 && _view.CardStage.StartsWith("Slots", StringComparison.Ordinal))
                {
                    GD.Print($"FIELD: DATA's SAVE searched the cards for {_dataSaveStep} frames");
                    if (_dataSaveStep < 440) { Finish($"DATA's SAVE searched the cards for only {_dataSaveStep} frames"); return; }
                    _dataSaveStep = 1900;
                }
                else if (_dataSaveStep == 1899) { Finish("DATA's SAVE never reached the slots"); return; }
                else if (_dataSaveStep == 1910) Tap("ps_cross");
                else if (_dataSaveStep == 1980) _dataSaveStep = 2000;
                return;
            }
            if (_menusCalm && since == 2970) Tap("ps_down");
            if (_menusCalm && since == 2990) Capture("room_data_load");
            if (_menusCalm && since is 3000 or 3020) Tap("ps_cross");
            if (_menusCalm && since == 3050 && _view.MenuOpen) { Finish("the menu did not close after DATA"); return; }
            // A battle ability reclaimed, as Ashley's action would bring it once the points stand: the two kinds, a chain
            // ability chosen, and its line under CHECK.
            if (_menusCalm && since == 3060)
            {
                var needed = _view.Content.Data("battle_abilities")["requirements"].AsGodotArray();
                _reclaimedFrom = _view.Abilities.Count;
                _view.AbilityPoints = needed[_view.AbilitiesReclaimed].AsInt32();
                _view.ReclaimCheck = true;
            }
            if (_menusCalm && since == 3080) { if (!_view.MenuOpen) { Finish("no reclaim opened with the points standing"); return; } Capture("room_reclaim"); }
            if (_menusCalm && since == 3090) Tap("ps_circle");
            if (_menusCalm && since == 3110) Capture("room_reclaim_list");
            if (_menusCalm && since == 3120) Tap("ps_circle");
            if (_menusCalm && since == 3135)
            {
                Capture("room_reclaim_done");
                string line = _view.SystemBox.Text.Replace("\n", " ");
                GD.Print($"FIELD: the reclaim reads \"{line}\"");
                if (_view.Abilities.Count != _reclaimedFrom + 1 || !line.Contains("reclaimed", StringComparison.Ordinal)) { Finish($"the reclaim gave {_view.Abilities.Count - _reclaimedFrom} abilities and read \"{line}\""); return; }
            }
            if (_menusCalm && since == 3150) Tap("ps_circle");
            if (_menusCalm && since == 3170 && _view.MenuOpen) { Finish("the menu did not close after the reclaim"); return; }
            // BREAK ARTS (row 1), with an art of her weapon's category reclaimed: refused sheathed (CAUTION), then drawn
            // the page: its category bar, the art, View Arts, NEXT and the HP cost; View Arts lists the categories.
            if (_menusCalm && since == 3180 && _view.Battle?.Ashley is { } artist) _view.ReclaimArt(184 + (artist.Category - 1) * 4);
            if (_menusCalm && since == 3190) Tap("ps_triangle");
            if (_menusCalm && since is >= 3210 and < 3260 && since % 10 == 0 && _view.MenuRow != 1) Tap("ps_up");
            if (_menusCalm && since == 3260 && _view.MenuRow != 1) { Finish($"the command list did not reach BREAK ARTS (row {_view.MenuRow})"); return; }
            // Sheathed, Circle on BREAK ARTS refuses under CAUTION; Circle again while it stands opens the page, its
            // arts greyed (Ep. 3 6:57), and Cross twice leaves it and the menu.
            if (_menusCalm && since == 3265) Tap("ps_circle");
            if (_menusCalm && since == 3280)
            {
                string refusal = _view.SystemBox.Text.Replace("\n", " ");
                GD.Print($"FIELD: BREAK ARTS sheathed reads \"{refusal}\"");
                if (_view.MenuPage != -1 || !refusal.Contains("battle mode", StringComparison.Ordinal)) { Finish($"BREAK ARTS was not refused out of battle mode: \"{refusal}\" (page {_view.MenuPage})"); return; }
            }
            if (_menusCalm && since == 3285) Capture("room_arts_refused");
            if (_menusCalm && since == 3290) Tap("ps_circle");
            if (_menusCalm && since == 3300)
            {
                Capture("room_arts_sheathed");
                if (_view.MenuPage != 1) { Finish($"Circle again on the refusal did not open BREAK ARTS (page {_view.MenuPage})"); return; }
            }
            if (_menusCalm && since is 3305 or 3315) Tap("ps_cross");
            if (_menusCalm && since == 3320) Tap("ps_circle");
            if (_menusCalm && since == 3400) Tap("ps_triangle");
            if (_menusCalm && since is >= 3420 and < 3470 && since % 10 == 0 && _view.MenuRow != 1) Tap("ps_up");
            if (_menusCalm && since == 3475) Tap("ps_circle");
            if (_menusCalm && since == 3500) { Capture("room_arts"); if (!_view.MenuOpen) { Finish("BREAK ARTS did not open with the weapon drawn"); return; } }
            if (_menusCalm && since == 3505) Tap("ps_down");
            if (_menusCalm && since == 3515) Tap("ps_circle");
            if (_menusCalm && since == 3535) Capture("room_arts_categories");
            if (_menusCalm && since is 3545 or 3560 or 3575) Tap("ps_cross");
            if (_menusCalm && since == 3600 && _view.MenuOpen) { Finish("the menu did not close after BREAK ARTS"); return; }
            // MAGIC (row 0), with a Warlock and a Shaman spell known: the classes, then the Shaman's with its MP.
            if (_menusCalm && since == 3610) { foreach (int spell in new[] { 97, 96 }) if (!_view.Spells.Contains(spell)) _view.Spells.Add(spell); }
            if (_menusCalm && since == 3620) Tap("ps_triangle");
            if (_menusCalm && since is >= 3640 and < 3700 && since % 10 == 0 && _view.MenuRow != 0) Tap("ps_up");
            if (_menusCalm && since == 3705) Tap("ps_circle");
            if (_menusCalm && since == 3725) { Capture("room_magic"); if (!_view.MenuOpen) { Finish("MAGIC did not open"); return; } }
            if (_menusCalm && since == 3730) Tap("ps_down");
            if (_menusCalm && since == 3740) Tap("ps_circle");
            if (_menusCalm && since == 3760) Capture("room_magic_spells");
            if (_menusCalm && since is 3770 or 3785 or 3800) Tap("ps_cross");
            if (_menusCalm && since == 3830 && _view.MenuOpen) { Finish("the menu did not close after MAGIC"); return; }
            if (since < 3840) return;
        }
        if (_felledAt > 0)
        {
            // The game over offers LOAD; Cross at its slots leaves for the title.
            if (_view.CardStage == "Slots" && !_offeredLoad) { _offeredLoad = true; _slotsAt = _frame; GD.Print("FIELD: the game over offered the LOAD screens"); Capture("gameover_load"); Tap(_loadAfter ? "ps_circle" : "ps_cross"); }
            // A press the screen was not yet taking is pressed again, as a player would.
            else if (_view.CardStage == "Slots" && _offeredLoad && (_frame - _slotsAt) % 30 == 29) Tap(_loadAfter ? "ps_circle" : "ps_cross");
            if (_loadAfter && _view.CardStage == "Files" && !_choseFile) { _choseFile = true; Capture("gameover_load_files"); Tap("ps_circle"); }
            if (_loadAfter && _view.CardStage == "Saved" && !_capturedLoaded) { _capturedLoaded = true; Capture("gameover_loaded"); }
            if (_loadAfter && _view.GameEnded)
            {
                if (FieldView.PendingSave is not { } loaded || loaded["generation"].AsInt32() != FieldView.ReadCardFile(1, 3)?["generation"].AsInt32()) { Finish("the game over's LOAD did not load the saved file"); return; }
                FieldView.PendingSave = null;
                GD.Print("FIELD: the game over's LOAD loaded card 1 file 3 to resume");
                Finish("");
                return;
            }
            if (_view.GameEnded && !_offeredLoad) { Finish("the game over did not offer the LOAD screens"); return; }
            if (_view.GameEnded)
            {
                if (!System.Linq.Enumerable.Contains(_view.Events, "evt0000")) { Finish("the field left without the game-over scene"); return; }
                GD.Print("FIELD: the game-over scene played and the game went back to the title");
                Finish("");
                return;
            }
            if (_frame - _felledAt > 60 * 120) { Finish($"the game-over scene did not end: {_view.EventNode}"); return; }
            return;
        }
        // A ride on a lift is followed for eight seconds before the room is done.
        if (_liftAt > 0 && _view.Controlled)
        {
            if ((_frame - _liftAt) % 40 == 0) GD.Print($"FIELD: riding at {_view.Player!.Position}");
            if (_frame - _liftAt < 240) return;
            var moved = _view.Player!.Position - _liftFrom;
            // Lifts here rise or sink: she must have gone up or down with it, not been put off it.
            if (Math.Abs(moved.Y) < 32 || new Vector2(moved.X, moved.Z).Length() > 64) { Finish($"Ashley rode the lift at {_liftFrom} but ended at {_view.Player.Position}"); return; }
            GD.Print($"FIELD: Ashley rode a lift from {_liftFrom} to {_view.Player.Position}");
            _liftAt = 0;
        }
        if (_skip && _view.Skippable && _view.Skips == 0 && !_view.Controlled) { GD.Print($"FIELD: skipping at {_view.EventNode} (frame {_frame})"); _view.Skip(); }
        // A played event is followed into the rooms it leads to, until the player has control; then play is watched
        // for half a second (a puzzle's timer, say) and captured.
        if (_played && _view.Controlled && _afterPlay++ < 30) return;
        if (_played && _view.Controlled && _afterPlay == 31)
        {
            if (_eventRoundTrip && (_room != (_view.Zone, _view.Map) || _view.Rooms.Count < 3))
            { Finish($"the continuing event handed over in {_view.Zone},{_view.Map} after {string.Join(" > ", _view.Rooms)}, instead of returning to {_room}"); return; }
            Capture("played_control");
        }
        if (_view.Controlled || _view.EndOfContent.Length != 0 || _view.Rooms.Count > 1 && !_played)
        {
            if (_skip && _view.Skips == 0) { Finish("the room's event opened no skippable section"); return; }
            if (_skip) GD.Print($"FIELD: skipped {_view.Skips} section(s); the event went on at frame {_frame}");
            GD.Print($"FIELD: the room's event ran through {string.Join(" > ", _view.Events)} to {(_view.Controlled ? "control" : _view.EndOfContent.Length != 0 ? _view.EndOfContent : _view.Rooms[^1])}");
            Finish("");
            return;
        }
        if (_frame - _nodeFrame > 60 * 60) Finish($"the event stalled at {_view.EventNode}: {_view.WaitingOn}");
    }

    private bool _handedOver;
    private int _storedBefore, _saveWait;
    private readonly Dictionary<int, int> _startFlags = new();
    private string _play = "";
    private bool _played, _eventRoundTrip;
    private int _afterPlay;
    private bool _offeredLoad, _loadAfter, _choseFile, _capturedLoaded;
    private List<int> _felled = new();
    private int _felledFrame;

    /// <summary>Every character in the battle that carries a shield holds its model, where its rig has a shield mount.</summary>
    private string CheckShields()
    {
        if (_view.Battle is not { } battle) return "";
        int held = 0;
        foreach (var fighter in battle.Combatants.Values)
        {
            if (fighter == battle.Ashley || fighter.Shield.Id == 0) continue;
            var definition = _view.Content.Character(fighter.Actor.Character);
            if (definition.GetValueOrDefault("shield_bone", -1).AsInt32() < 0) continue;
            if (fighter.ShieldModel == null) return $"character {fighter.Actor.Character:X2} holds no shield";
            held++;
        }
        if (held > 0) GD.Print($"FIELD: {held} characters hold their shields");
        return "";
    }

    /// <summary>
    /// In a room with a container: the inventory's spare blade goes in and comes back out, and a stack of items goes
    /// in whole; what Ashley holds cannot be stored.
    /// </summary>
    private string CheckContainer()
    {
        bool container = false;
        foreach (var value in _view.Document.GetValueOrDefault("chests", new Godot.Collections.Array()).AsGodotArray())
            container |= value.AsGodotDictionary()["index"].AsInt32() >= 0x40;
        if (!container) return "";
        var storable = _view.Storable();
        foreach (var weapon in _view.Weapons())
            if (_view.Holds(weapon) && storable.Any(entry => entry.RecursiveEqual(weapon["entry"].AsGodotDictionary())))
                return "the weapon in hand could be stored";
        var source = _view.Weapons().FirstOrDefault();
        if (source != null)
        {
            // An unequipped inventory-slot weapon changes representation in the box. Its gems must accompany it.
            var bladeRecord = source["blade"].AsGodotDictionary().Duplicate(true); bladeRecord["slot"] = 200;
            var gripRecord = source["grip"].AsGodotDictionary().Duplicate(true); gripRecord["slot"] = 200;
            var bladePart = new Godot.Collections.Dictionary { ["kind"] = "blades", ["record"] = bladeRecord };
            var gripPart = new Godot.Collections.Dictionary { ["kind"] = "grips", ["record"] = gripRecord };
            var spare = new Godot.Collections.Dictionary { ["kind"] = "weapons", ["record"] = new Godot.Collections.Dictionary {
                ["blade"] = 200, ["grip"] = 200, ["name"] = "Container fixture", ["equipped"] = false,
                ["gems"] = new Godot.Collections.Array { new Godot.Collections.Dictionary { ["id"] = 269 } } } };
            _view.Equipment.Add(bladePart); _view.Equipment.Add(gripPart); _view.Equipment.Add(spare);
            int boxCount = _view.Stored.Count;
            _view.Store(spare);
            if (_view.Equipment.Contains(spare) || _view.Equipment.Contains(bladePart) || _view.Equipment.Contains(gripPart)
                || _view.Stored.Count != boxCount + 1) return "storing a slot weapon did not move all its parts";
            var stored = _view.Stored[boxCount].AsGodotDictionary();
            if (stored["record"].AsGodotDictionary().GetValueOrDefault("gems", new Godot.Collections.Array()).AsGodotArray().Count != 1)
                return "storing a slot weapon lost its attached gem";
            _view.Retrieve(stored);
            var returned = _view.Weapons().First(w => w["entry"].AsGodotDictionary().RecursiveEqual(stored));
            if (FieldView.GemsOf(returned).Count != 1 || FieldView.GemsOf(returned)[0].AsGodotDictionary()["id"].AsInt32() != 269)
                return "retrieving a slot weapon lost its attached gem";
            _view.Equipment.Remove(stored);
        }
        int carried = _view.Equipment.Count;
        var blade = new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = new Godot.Collections.Dictionary { ["id"] = 3, ["category"] = 2 } };
        _view.Equipment.Add(blade);
        _view.Store(blade);
        if (_view.Equipment.Count != carried || _view.Stored.Count != 1) return "storing a blade did not move it";
        _view.Retrieve(_view.Stored[0].AsGodotDictionary());
        _view.Equipment.Remove(blade);
        _view.Items[323] = _view.Items.GetValueOrDefault(323) + 2;
        int count = _view.Items[323];
        _view.MoveItems(323, true);
        if (_view.Items.ContainsKey(323) || _view.StoredItems.GetValueOrDefault(323) != count) return "storing items did not move the stack";
        _view.MoveItems(323, false);
        GD.Print($"FIELD: the container took and gave back a blade and {count} of item 323");
        return "";
    }

    /// <summary>
    /// A rolling crate (kind 0 or 1) pushed from beside it moves a tile on and turns to the orientation BATTLE.PRG's
    /// roll table gives for that direction (facing quadrant q pushes towards -z, -x, +z, +x for q = 0 to 3).
    /// </summary>
    private string CheckCrate()
    {
        var ids = new HashSet<int>();
        foreach (var value in _view.Document["objects"].AsGodotArray())
            if (!ids.Add(value.AsGodotDictionary()["id"].AsInt32())) return "the room has duplicate object ids";
        int groups = _view.Document["room_groups"].AsGodotArray().Count;
        foreach (var value in _view.Document["crates"].AsGodotArray())
            if (value.AsGodotDictionary()["id"].AsInt32() < groups) return "a crate occupies a geometry group's id";
        if (_view.Player is not { } ashley) return "";
        var rolls = _view.Document.GetValueOrDefault("crate_rolls", new Godot.Collections.Array()).AsGodotArray();
        var start = (ashley.Position, ashley.Facing);
        foreach (var crate in new List<(string Id, int Kind, int X, int Z, float Bottom, int Orientation)>(_view.Crates))
        {
            if (crate.Kind > 1 || crate.Orientation < 0) continue;
            for (int quadrant = 0; quadrant < 4; quadrant++)
            {
                var (dx, dz) = quadrant switch { 0 => (0, -1), 1 => (-1, 0), 2 => (0, 1), _ => (1, 0) };
                var behind = new Vector3((crate.X - dx) * 128 + 64, 0, (crate.Z - dz) * 128 + 64);
                if (_view.FloorAt(behind) is not { } floor || floor != crate.Bottom) continue;
                ashley.Position = new Vector3(behind.X, floor, behind.Z);
                ashley.Facing = quadrant * 1024;
                _view.Interact(ashley);
                foreach (var moved in _view.Crates)
                {
                    // The same crate, a tile on; one that could not move stays where it was.
                    if (moved.Id != crate.Id || moved.X != crate.X + dx || moved.Z != crate.Z + dz) continue;
                    int expected = rolls[crate.Orientation].AsGodotArray()[quadrant].AsInt32();
                    if (moved.Orientation != expected) return $"a crate rolled from {crate.X}, {crate.Z} to orientation {moved.Orientation}, not {expected}";
                    GD.Print($"FIELD: a crate rolled from {crate.X}, {crate.Z} towards quadrant {quadrant}, orientation {crate.Orientation} to {expected}");
                    (ashley.Position, ashley.Facing) = start;
                    return "";
                }
            }
        }
        (ashley.Position, ashley.Facing) = start;
        return "";
    }

    /// <summary>
    /// With <c>--options-window</c>, once Ashley is the player's: the field menu's Options opens the settings window, whose
    /// OPTIONS settings WEAPON STATUS and ARMOR STATUS are captured, set ON (the options word gains 0x40 and 0x80) and
    /// the window closed.
    /// </summary>
    private bool _optionsWindow;
    private int _optionsStep;

    private bool StepOptionsWindow()
    {
        if (_optionsStep < 0) return false;
        int step = _optionsStep++;
        if (step == 10) Tap("ps_triangle");
        if (step is >= 30 and < 120 && step % 10 == 0 && _view.MenuRow != 7) Tap("ps_down");
        if (step == 130) { if (_view.MenuRow != 7) { Finish($"the command list did not reach Options (row {_view.MenuRow})"); return true; } Tap("ps_circle"); }
        if (step == 150)
        {
            if (_view.FindChildren("Settings", "", true, false).OfType<Wol.Games.Vagrant.Scenes.SettingsDialog>().FirstOrDefault(d => d.Visible) is not { } dialog) { Finish("Options did not open the settings window"); return true; }
            var weapon = dialog.FindChildren("WEAPON_STATUS", "OptionButton", true, false).OfType<OptionButton>().FirstOrDefault();
            var armour = dialog.FindChildren("ARMOR_STATUS", "OptionButton", true, false).OfType<OptionButton>().FirstOrDefault();
            if (weapon == null || armour == null || !weapon.IsVisibleInTree()) { Finish("the settings window has no WEAPON STATUS or ARMOR STATUS"); return true; }
            if (weapon.Selected != 1 || armour.Selected != 1) { Finish("WEAPON STATUS or ARMOR STATUS is not OFF in a new game"); return true; }
            for (Node? up = weapon; up != null; up = up.GetParent()) if (up is ScrollContainer scroll) { scroll.EnsureControlVisible(weapon); break; }
            _optionsDialog = dialog;
        }
        if (step == 160) Capture("options_off");
        if (step == 165)
        {
            foreach (string name in new[] { "WEAPON_STATUS", "ARMOR_STATUS" })
                if (_optionsDialog!.FindChildren(name, "OptionButton", true, false).OfType<OptionButton>().First() is var choice) { choice.Select(0); choice.EmitSignal(OptionButton.SignalName.ItemSelected, 0L); }
            if ((_view.Options & 0xC0) != 0xC0) { Finish($"choosing ON left the options word 0x{_view.Options:x}"); return true; }
            GD.Print($"FIELD: OPTIONS' WEAPON STATUS and ARMOR STATUS set ON: options word 0x{_view.Options:x}");
        }
        if (step == 175) Capture("options_on");
        if (step == 180) _optionsDialog!.Hide();
        if (step is 200 or 220) Tap("ps_cross");
        if (step == 260)
        {
            if (_view.MenuOpen || _optionsDialog!.Visible) Finish("the menu did not close after Options");
            else if ((_view.Options & 0xC0) != 0xC0) Finish("closing Options lost the weapon or armor status setting");
            else Finish("");
        }
        return true;
    }
    private Wol.Games.Vagrant.Scenes.SettingsDialog? _optionsDialog;

    /// <summary>
    /// Retail facts the battle's rules rest on, read from the disc:
    /// <list type="bullet">
    /// <item>healing's reckoning (13, BATTLE.PRG 0x800829b8) is only ever given an affinity of 2 or more, so its
    /// branches for index 0 (a stale stack slot, 0x80082aa4) and 255 never run;</item>
    /// <item>no character record has a class above 5, so 0x80080a9c's path for classes 6 and 7 (fallers through its
    /// caller's s1 and s2) never runs;</item>
    /// <item>OPTIONS' WEAPON STATUS and ARMOR STATUS (MENU8.PRG) are named and offered ON and OFF in retail's words,
    /// both OFF in a new game, and choosing ON sets bits 0x40 and 0x80;</item>
    /// <item>a trap tile an effect shows is marked 0x400 in the enemies' grid (0x800e68ec), which every walker's shunned
    /// marks (4, 5, 5 or 7, table 0x80069C18) include.</item>
    /// </list>
    /// </summary>
    private string CheckCombatRules()
    {
        // Two independent motor tracks and tagged channels (SLUS 0x800433b4, 0x800434a4, 0x800436b4).
        // The second channel clips their combined level; stopping event tag 2 leaves effect tag 1 running.
        bool vibrationEnabled = VibrationDriver.Enabled;
        VibrationDriver.Enabled = false;
        var vibration = new VibrationDriver();
        try
        {
            byte[] source = { 5, 2, 1, 0, 0, 2, 100, 2, 200, 0, 0, 99 };
            var envelope = VibrationDriver.Decode(source);
            if (envelope.Length != 11) return "the vibration decoder did not stop at the two tracks' ends";
            vibration.Start(envelope, 1);
            vibration.Start(envelope, 2);
            vibration.Start(envelope, 3); // Both channels are occupied.
            vibration.Step();
            if (vibration.Levels != (2, 255)) return $"the combined motors were {vibration.Levels}, not (2, 255)";
            vibration.Stop(2);
            vibration.Step();
            if (vibration.Levels != (1, 200)) return $"stopping event vibration left {vibration.Levels}, not the effect's (1, 200)";
            for (int i = 0; i < 3; i++) vibration.Step();
            if (vibration.Levels != (0, 0)) return $"the vibration envelope ended at {vibration.Levels}";
            GD.Print("FIELD: vibration tracks interpolate, combine and stop by tag as retail does");
        }
        finally { vibration.Stop(); VibrationDriver.Enabled = vibrationEnabled; }
        var content = _view.Content;
        for (int id = 0; id < 256; id++)
            foreach (var effect in content.Field.Action(id).Effects)
                if (effect.Reckoning == 13 && effect.Affinity < 2) return $"action {id} heals with affinity {effect.Affinity}";
        int records = 0;
        for (int zone = 0; zone < 256; zone++)
        {
            byte[] znd;
            try { znd = content.Disc($"MAP/ZONE{zone:d3}.ZND"); }
            catch (Exception) { continue; }
            int units = BitConverter.ToInt32(znd, 8), templates = BitConverter.ToInt32(znd, units), first = units + 4 + templates * 8;
            for (int t = 0; t < templates && first + (t + 1) * 0x464 <= znd.Length; t++, records++)
                if (znd[first + t * 0x464 + 3] >> 5 > 5) return $"zone {zone}'s character {t} has class {znd[first + t * 0x464 + 3] >> 5}";
        }
        var initial = content.Disc("BATTLE/INITBTL.PRG");
        if (initial[Wol.Games.Vagrant.Installation.MapConversion.AshleyRecord + 3] >> 5 > 5) return "Ashley's record has a class above 5";
        GD.Print($"FIELD: {records} character records, every class 0 to 5; healing's affinities all 2 or more");
        var strings = content.Data("options_menu")["strings"].AsGodotArray();
        string Entry(int i) => SystemBox.Entry(strings, i);
        if (Entry(4) != "WEAPON STATUS" || Entry(6) != "ARMOR STATUS" || Entry(30) != "ON" || Entry(32) != "OFF" || Entry(34) != "ON" || Entry(36) != "OFF")
            return $"OPTIONS reads {Entry(4)} {Entry(30)}/{Entry(32)}, {Entry(6)} {Entry(34)}/{Entry(36)}";
        if ((_view.Options & 0xC0) != 0) return $"a new game's options word is 0x{_view.Options:x}";
        GD.Print($"FIELD: OPTIONS offers \"{Entry(4)}\" ({Entry(5).Replace("\n", " ")}) and \"{Entry(6)}\", {Entry(30)} or {Entry(32)}");
        // How an entry shows (0x80085a34): the first of mode fields 0 to 8 that is set, the status showing (bits 16
        // and 17) last; nothing at all is a miss.
        var shown = new (System.Action<FieldBattle.Outcome> Set, int Kind)[]
        {
            (o => { o.MpMode = 2; o.ClassMode = 1; }, 5), (o => o.ClassMode = 1, 0), (o => o.ClassMode = 2, 5), (o => { o.RiskMode = 3; o.HpMode = 1; }, 0),
            (o => o.BladeDpMode = 2, 6), (o => o.Set = 0x20, 6), (_ => { }, 4),
        };
        foreach (var (set, kind) in shown)
        {
            var entry = new FieldBattle.Outcome { Who = _view.Battle!.Ashley! };
            set(entry);
            entry.Showing();
            if (entry.Kind != kind) return $"an entry showed as {entry.Kind}, not {kind}";
        }
        if (_view.Battle is { } battle)
        {
            battle.MarkTrap(31, 31);
            if (!battle.Marked((31, 31), 0x400)) return "a shown trap's tile was not marked 0x400";
            foreach (var c in battle.Combatants.Values)
                if (c.Mind is { } mind && mind.Shuns != 0 && (mind.Shuns & 4) == 0) return $"{c.Name} does not shun a shown trap's tile ({mind.Shuns})";
        }
        return "";
    }

    /// <summary>
    /// Steps Ashley onto the room's first damage trap: it is announced by name under no header, and costs what the
    /// trap's reckoning gives for one of her limbs (<see cref="FieldBattle.TrapDamage"/>), 5 either way. Her hit points and
    /// statuses are put back after.
    /// </summary>
    private string CheckTrap()
    {
        if (_view.Battle?.Ashley is not { } fighter || !_view.Content.Has("trap_actions")) return "";
        var actions = _view.Content.Data("trap_actions");
        int level = _view.Document.GetValueOrDefault("trap_level", 0).AsInt32();
        foreach (var value in _view.Document.GetValueOrDefault("traps", new Godot.Collections.Array()).AsGodotArray())
        {
            var trap = value.AsGodotDictionary();
            if (actions.GetValueOrDefault(trap["type"].AsInt32().ToString(), new Godot.Collections.Dictionary()).AsGodotDictionary() is not { Count: > 0 } action) continue;
            Wol.Games.Vagrant.Installation.ActionEffect? blow = null;
            foreach (var effect in _view.Content.Field.Action(trap["type"].AsInt32() + 6).Effects)
                if (effect.Effect == 58) blow = effect;
            if (blow is not { } struck) continue;
            int hp = fighter.Hp;
            uint statuses = fighter.Status;
            var limbs = fighter.Limbs.ConvertAll(l => l.Hp);
            fighter.Hp = fighter.MaxHp;
            // The blow lands on one limb at random and varies by 5 either way: it must fall within the limbs' range.
            int least = int.MaxValue, most = 0;
            for (int limb = 0; limb < fighter.Limbs.Count; limb++)
            {
                if (fighter.Limbs[limb].MaxHp <= 0) continue;
                int damage = _view.Battle!.TrapDamage(fighter, struck, level, limb, false);
                least = Math.Min(least, damage);
                most = Math.Max(most, damage);
            }
            if (least == int.MaxValue) least = most = _view.Battle!.TrapDamage(fighter, struck, level, -1, false);
            var tile = trap["tile"].AsGodotArray();
            _view.StepOnTrap((tile[0].AsInt32(), tile[1].AsInt32()));
            int lost = fighter.MaxHp - fighter.Hp;
            string text = _view.SystemBox.Text, name = action["name"].AsString();
            GD.Print($"FIELD: the trap {name} (type {trap["type"]}, level {level}) cost {lost} of {fighter.MaxHp}: \"{text}\"");
            fighter.Hp = hp;
            fighter.Status = statuses;
            for (int i = 0; i < limbs.Count; i++) fighter.Limbs[i].Hp = limbs[i];
            fighter.Dead = false;
            GD.Print($"FIELD: the trap's blow ranges {least} to {most} before its variation");
            if (lost < Math.Max(0, least - 5) || lost > Math.Min(fighter.MaxHp - 1, most + 5)) return $"the trap {name} cost {lost}, outside {least} to {most} (and 5 either way)";
            if (!text.StartsWith("Trap", StringComparison.Ordinal) || !text.Contains(name, StringComparison.Ordinal) || _view.SystemBox.Header != 0)
                return $"the trap {name} was announced as \"{text}\" under header {_view.SystemBox.Header}";
            return "";
        }
        return "";
    }

    private int _resultsAt;
    private bool _menusCalm, _fadeCaptured, _balloonCaptured, _dotsCaptured;
    private int _pointerStage, _slotsAt, _numberAt, _enemyHp, _gradesSeen, _gradeAt;
    private bool _gradeCaptured;
    private int _reclaimedFrom;
    private FieldBattle.Combatant? _pointerEnemy;
    private Vector3 _pointerHome;
    private int _lidWait;

    /// <summary>
    /// While a boss's CONGRATULATIONS screen is up: captures as its parts come in (display frames are two to its one),
    /// Circle once the reel turns at full speed and again once it has stopped. It must end within two minutes.
    /// </summary>
    private bool StepResults()
    {
        if (_view.Results is not { Showing: true }) return false;
        if (_resultsAt == 0) { _resultsAt = _frame; GD.Print($"FIELD: the CONGRATULATIONS screen opened at {_frame}"); }
        int since = _frame - _resultsAt;
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && since is 20 or 100 or 200 or 300 or 420 or 560 or 700 or 900 or 1100) Capture($"results_{since:d4}");
        if (since is 800 or 1300) Tap("ps_circle");
        if (since > 60 * 120) Finish("the CONGRATULATIONS screen did not end");
        return true;
    }

    /// <summary>With --game-over, Ashley is felled as she gets control: the game-over scene plays, then the title.</summary>
    private bool _gameOver;
    private int _felledAt;

    private int _liftAt;
    private Vector3 _liftFrom;

    /// <summary>Sets Ashley on the room's first lift that is switched on, on top of it at its middle.</summary>
    private void BoardLift()
    {
        if (_view.Player is not { } ashley) return;
        foreach (var value in _view.Document.GetValueOrDefault("platforms", new Godot.Collections.Array()).AsGodotArray())
        {
            var record = value.AsGodotDictionary();
            var footprint = record["footprint"].AsGodotArray();
            if (!record["lift"].AsBool() || record["off"].AsBool() || footprint.Count != 6) continue;
            var start = record["start"].AsGodotArray();
            var top = new Vector3(start[0].AsSingle() + (footprint[0].AsSingle() + footprint[1].AsSingle()) / 2, start[1].AsSingle() + footprint[2].AsSingle(), start[2].AsSingle() + (footprint[4].AsSingle() + footprint[5].AsSingle()) / 2);
            ashley.Position = top;
            _view.Gameplay.Place(top);
            _liftFrom = top;
            _liftAt = _frame;
            GD.Print($"FIELD: Ashley boards the lift of group {record["group"]} at {top}");
            return;
        }
    }

    /// <summary>
    /// In a room with a magic seal: Ashley, standing before it and facing it, casts Unlock (action 92) for 3 magic points
    /// a time until its roll on her risk breaks it.
    /// </summary>
    private string CheckSeal()
    {
        if (_view.Player is not { } ashley || _view.Battle?.Ashley is not { } caster) return "";
        foreach (string kind in new[] { "locks", "chests" })
            foreach (var value in _view.Document.GetValueOrDefault(kind, new Godot.Collections.Array()).AsGodotArray())
            {
                var record = value.AsGodotDictionary();
                if (record["lock"].AsInt32() != 2) continue;
                var tile = record["tile"].AsGodotArray();
                int x = tile[0].AsInt32(), z = tile[1].AsInt32(), quadrant = kind == "chests" ? record["facing"].AsInt32() / 1024 : 0;
                var (dx, dz) = quadrant switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
                ashley.Position = new Vector3((x + dx) * 128 + 64, ashley.Position.Y, (z + dz) * 128 + 64);
                ashley.Facing = quadrant * 1024;
                for (int cast = 1; cast <= 30; cast++)
                {
                    caster.Mp = Math.Max(caster.Mp, 3);
                    int mp = caster.Mp;
                    string note = _view.Cast(92);
                    if (caster.Mp != mp - 3) return $"Unlock at {x}, {z} did not take 3 magic points: {note}";
                    if (record["lock"].AsInt32() == 2) continue;
                    GD.Print($"FIELD: Unlock broke the seal at {x}, {z} on cast {cast}");
                    return "";
                }
                return $"Unlock left the seal at {x}, {z} after 30 casts";
            }
        return "";
    }

    /// <summary>
    /// In a workshop: a weapon found whole comes apart into a loose blade and grip, the blade takes only grips of its
    /// family, they fit back together, and the weapon in Ashley's hand comes apart only to leave her bare-handed.
    /// </summary>
    private string CheckWorkshop()
    {
        var blade = new Godot.Collections.Dictionary { ["id"] = 3, ["category"] = 2, ["model"] = 3, ["material"] = 1 };
        var grip = new Godot.Collections.Dictionary { ["id"] = 98, ["category"] = 1 };
        var entry = new Godot.Collections.Dictionary { ["kind"] = "weapon", ["record"] = new Godot.Collections.Dictionary { ["blade"] = blade, ["grip"] = grip, ["name"] = "Test" } };
        var pole = new Godot.Collections.Dictionary { ["kind"] = "grip", ["record"] = new Godot.Collections.Dictionary { ["id"] = 118, ["category"] = 3 } };
        _view.Equipment.Add(entry);
        _view.Equipment.Add(pole);
        string taken = _view.Disassemble(new Godot.Collections.Dictionary { ["blade"] = blade, ["grip"] = grip, ["name"] = "Test", ["entry"] = entry });
        var blades = _view.Parts(null);
        var loose = blades.Find(part => part["record"].AsGodotDictionary()["id"].AsInt32() == 3);
        if (loose == null) return $"taking a weapon apart left no blade ({taken})";
        var grips = _view.Parts(loose);
        if (grips.Count != 1 || grips[0]["record"].AsGodotDictionary()["id"].AsInt32() != 98) return $"a sword offered {grips.Count} grips";
        string made = _view.Assemble(loose, grips[0]);
        if (_view.Parts(null).Exists(part => part["record"].AsGodotDictionary()["id"].AsInt32() == 3)) return "the assembled blade was still loose";
        _view.Equipment.Remove(pole);
        foreach (var value in _view.Equipment)
            if (value.AsGodotDictionary()["kind"].AsString() == "weapons" && value.AsGodotDictionary()["record"].AsGodotDictionary()["equipped"].AsBool())
            {
                var held = value.AsGodotDictionary();
                var weapon = _view.Weapons().Find(candidate => candidate["entry"].AsGodotDictionary().RecursiveEqual(held));
                if (weapon == null || !_view.Holds(weapon)) return "Ashley did not hold the inventory's equipped weapon";
                _view.Disassemble(weapon);
                // Taken from her hand, it leaves her bare-handed: battle set 10.
                if (!_view.Unarmed || _view.Battle?.Ashley is { } fighter && (fighter.Category != 10 || fighter.BattleSet != "btA")) return "taking apart the weapon in hand did not leave Ashley bare-handed";
                GD.Print("FIELD: workshop: the weapon in hand came apart and Ashley fights bare-handed");
                break;
            }
        // A gem sets in a weapon with a free gem slot, and comes back out.
        var gemmed = new Godot.Collections.Dictionary { ["kind"] = "weapon", ["record"] = new Godot.Collections.Dictionary { ["blade"] = blade.Duplicate(), ["grip"] = new Godot.Collections.Dictionary { ["id"] = 98, ["category"] = 1, ["gem_slots"] = 1 }, ["name"] = "Gemmed" } };
        var jasper = new Godot.Collections.Dictionary { ["kind"] = "gem", ["record"] = new Godot.Collections.Dictionary { ["id"] = 0x10D, ["affinities"] = new Godot.Collections.Array { 0, 0, -5, 0, 15, 0, 0, 0 } } };
        _view.Equipment.Add(gemmed);
        _view.Equipment.Add(jasper);
        var gemWeapon = new Godot.Collections.Dictionary { ["blade"] = gemmed["record"].AsGodotDictionary()["blade"], ["grip"] = gemmed["record"].AsGodotDictionary()["grip"], ["name"] = "Gemmed", ["entry"] = gemmed };
        string set = _view.Attach(gemWeapon, jasper);
        if (FieldView.GemsOf(gemWeapon).Count != 1 || _view.Gems().Count != 0) return $"attaching a gem failed: {set}";
        if (!_view.Attach(gemWeapon, jasper).Contains("no free")) return "a second gem was set in a one-slot weapon";
        string out_ = _view.Detach(gemWeapon);
        if (FieldView.GemsOf(gemWeapon).Count != 0 || _view.Gems().Count != 1) return $"taking a gem out failed: {out_}";
        GD.Print($"FIELD: workshop: {set} {out_}");
        // Two loose blades combine into the blade and material the tables give, and are used up.
        if (_view.Content.Has("combining"))
        {
            var table = _view.Content.Data("combining");
            // Two metals this workshop works (the first it works, and the next, or the same again).
            var worked = new List<int>();
            for (int m = 3; m <= 7; m++) if (_view.Works(new Godot.Collections.Dictionary { ["material"] = m })) worked.Add(m);
            if (worked.Count == 0) return "the workshop works no metal";
            int metalA = worked[0], metalB = worked.Count > 1 ? worked[1] : worked[0];
            var first = new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = new Godot.Collections.Dictionary { ["id"] = 1, ["category"] = 1, ["material"] = metalA, ["max_dp"] = 100, ["dp"] = 100, ["classes"] = new Godot.Collections.Array { 1, 0, 0, 0, 0, 0, 0, 0 }, ["affinities"] = new Godot.Collections.Array { 0, 0, 0, 0, 0, 0, 0, 0 } } };
            var second = new Godot.Collections.Dictionary { ["kind"] = "blade", ["record"] = new Godot.Collections.Dictionary { ["id"] = 2, ["category"] = 1, ["material"] = metalB, ["max_dp"] = 60, ["dp"] = 60, ["classes"] = new Godot.Collections.Array { 0, 2, 0, 0, 0, 0, 0, 0 }, ["affinities"] = new Godot.Collections.Array { 0, 0, 0, 0, 0, 0, 0, 0 } } };
            _view.Equipment.Add(first);
            _view.Equipment.Add(second);
            int expected = table["results"].AsGodotArray()[1 * 96 + 2].AsInt32(), metal = table["materials"].AsGodotArray()[(metalA - 3) * 500 + 1 * 50 + (metalB - 3) * 10 + 1].AsInt32();
            string before = Json.Stringify(_view.Equipment);
            _view.Combine(first, second, out var preview, commit: false);
            if (Json.Stringify(_view.Equipment) != before) return "previewing a combination changed the inventory";
            string fused = _view.Combine(first, second, out var combined);
            if (preview == null || combined == null || !preview.RecursiveEqual(combined)) return "the committed combination differs from its preview";
            var fusedBlade = _view.Parts(null).Find(part => part["record"].AsGodotDictionary()["id"].AsInt32() == expected && part["record"].AsGodotDictionary()["material"].AsInt32() == metal);
            if (expected != 0 && (fusedBlade == null || _view.Equipment.Contains(first) || _view.Equipment.Contains(second))) return $"combining failed: {fused}";
            if (fusedBlade != null && fusedBlade["record"].AsGodotDictionary()["max_dp"].AsInt32() != 80) return "a combined blade's DP is not the mean";
            GD.Print($"FIELD: workshop: {fused}");
        }
        // A blow wears the weapon and gains it PP; Repair restores the DP for a hundredth of it in PP.
        var worn = new Godot.Collections.Dictionary { ["id"] = 3, ["category"] = 2, ["dp"] = 2000, ["max_dp"] = 2000, ["pp"] = 0, ["max_pp"] = 1000 };
        var gear = FieldBattle.Gear.From(worn);
        gear.Wear(700);
        gear.Gain(70);
        if (worn["dp"].AsInt32() != 1300 || worn["pp"].AsInt32() != 70) return $"wear left DP {worn["dp"]} and PP {worn["pp"]}";
        string repaired = _view.Repair(worn, "Test");
        if (worn["dp"].AsInt32() != 2000 || worn["pp"].AsInt32() != 63) return $"repair left DP {worn["dp"]} and PP {worn["pp"]}: {repaired}";
        // A shield with a slot takes a gem, whose special effect it then carries.
        var buckler = new Godot.Collections.Dictionary { ["id"] = 127, ["gem_slots"] = 1, ["dp"] = 100, ["max_dp"] = 100 };
        var shieldEntry = new Godot.Collections.Dictionary { ["kind"] = "shield", ["record"] = buckler };
        var stone = new Godot.Collections.Dictionary { ["kind"] = "gem", ["record"] = new Godot.Collections.Dictionary { ["id"] = 0x10E, ["special"] = 5 } };
        _view.Equipment.Add(shieldEntry);
        _view.Equipment.Add(stone);
        string shieldSet = _view.Attach(new Godot.Collections.Dictionary { ["entry"] = shieldEntry, ["name"] = "Buckler", ["shield"] = true }, stone);
        if (_view.Battle?.Ashley is { } wearer)
        {
            _view.Wear(buckler);
            if (!wearer.Shield.Has(5)) return $"a worn shield did not carry its gem's effect ({shieldSet})";
        }
        GD.Print($"FIELD: workshop: {repaired} {shieldSet}");
        GD.Print($"FIELD: workshop: {taken} {made}");
        return "";
    }
    private int _battleFrame, _press, _wonFrame, _sphereClock = -1, _noticeAt;
    private uint _options;
    private int _masterySpell = -1, _masteryAt;
    private bool _masteryDone;

    /// <summary>
    /// Once after the first battle: Ashley casts a healing spell she has never cast; as it ends, "Mastered the spell"
    /// must open under CHECK, and the spell must be kept as mastered. Her magic points are given back after.
    /// </summary>
    private bool StepMastery(FieldBattle battle, FieldBattle.Combatant ashley, int elapsed)
    {
        if (_masteryDone) return false;
        if (_masterySpell < 0)
        {
            foreach (var (key, value) in _view.Content.Data("spells")["actions"].AsGodotDictionary())
            {
                int id = int.Parse(key.AsString());
                if (id is < 54 or > 140) continue;
                foreach (var effect in value.AsGodotDictionary().GetValueOrDefault("effects", new Godot.Collections.Array()).AsGodotArray())
                    if (effect.AsGodotDictionary()["effect"].AsInt32() == 62) { _masterySpell = id; break; }
                if (_masterySpell >= 0) break;
            }
            if (_masterySpell < 0) { Finish("no healing spell was found to cast"); return true; }
            // An enemy's spell under way finishes first: the battle makes one action at a time.
            if (battle.EnemyActing) { _masterySpell = -1; return true; }
            ashley.Mp = ashley.MaxMp;
            // The enemies wait while she casts, so that no move of theirs speaks over the message looked for.
            foreach (var c in battle.Combatants.Values) if (c != ashley && c.Mind != null) c.Mind.Held = true;
            string note = _view.Cast(_masterySpell);
            if (note.Length != 0) { Finish($"the healing spell {_masterySpell} could not be cast: {note}"); return true; }
            // It is aimed first: the sphere on Ashley, her name and hit points and "TARGET:", and Circle casts it.
            string aim = _view.SystemBox.Text.Replace("\n", " | ");
            if (!aim.Contains("TARGET:", StringComparison.Ordinal) || !battle.Paused) { Finish($"the spell was not aimed first (\"{aim}\")"); return true; }
            GD.Print($"FIELD: the spell is aimed: \"{aim}\"");
            Tap("ps_circle");
            _masteryAt = elapsed;
            return true;
        }
        // The HUD is put away while the spell plays out, as retail's is.
        if (elapsed - _masteryAt == 10 && battle.Hud is { Acting: false }) { Finish("the HUD stayed up while a spell was cast"); return true; }
        if (_view.SystemBox.Text.StartsWith("Mastered", StringComparison.Ordinal))
        {
            GD.Print($"FIELD: spell {_masterySpell} cast to its end: \"{_view.SystemBox.Text}\" under header {_view.SystemBox.Header}; the HUD is {(battle.Hud is { Acting: true } ? "still away" : "back")} {elapsed - _masteryAt} frames on");
            _masteryDone = true;
            ashley.Mp = ashley.MaxMp;
            foreach (var c in battle.Combatants.Values) if (c != ashley && c.Mind != null) c.Mind.Held = false;
            if (!_view.Mastered.Contains(_masterySpell) || _view.SystemBox.Header != 3) Finish("the mastered spell was not kept, or not under CHECK");
            else if (battle.Hud is { Acting: true }) Finish("the HUD was still put away as the spell's action ended");
            return true;
        }
        if (elapsed - _masteryAt > 400) { Finish($"casting spell {_masterySpell} for the first time did not say it was mastered (\"{_view.SystemBox.Text}\")"); return true; }
        return true;
    }
    private bool _leftBattle;
    private string _pressed = "";

    /// <summary>Where the room's crates stand, in order, for the carry check.</summary>
    private string CrateTiles()
    {
        var tiles = new List<string>();
        foreach (var crate in _view.Crates) tiles.Add($"{crate.X},{crate.Z}");
        tiles.Sort(StringComparer.Ordinal);
        return string.Join(" ", tiles);
    }

    /// <summary>Taps a button for a few frames.</summary>
    private void Tap(string action)
    {
        if (_press > 0) return;
        Input.ActionPress(action);
        _pressed = action;
        _press = 4;
    }

    private void Steer(Vector2 stick)
    {
        foreach (var (action, value) in new[] { ("ps_right", stick.X), ("ps_left", -stick.X), ("ps_down", stick.Y), ("ps_up", -stick.Y) })
            if (value > 0) Input.ActionPress(action, value); else Input.ActionRelease(action);
    }

    /// <summary>
    /// The first battle: draw the weapon, close on the nearest soldier, open the attack sphere and attack until both
    /// Müllenkamp soldiers fall; the room's conditions then play the events after the battle.
    /// </summary>
    private void StepBattle()
    {
        int elapsed = _frame - _battleFrame;
        if (elapsed % 600 == 0) GD.Print($"FIELD: combat progress {_view.Zone},{_view.Map}, control {_view.Controlled}, door {_doorRooms}, hostiles {_view.Battle?.HostileCount()}, {_view.SystemBox.Text.Replace('\n', '/')}");
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        var battle = _view.Battle;
        if (battle?.Ashley is not { } ashley) { if (_leftBattle) { Tap("ps_circle"); return; } Finish("the battle has no Ashley"); return; }
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && elapsed % Math.Min(30, _storyEvery) == 0)
            Capture(_storyEvery < 30 ? $"battle_{elapsed:d5}_{(_view.Events.Count > 0 ? _view.Events[^1] : "none")}" : $"battle_{elapsed:d4}");
        if (battle.GameOver) { Finish("Ashley fell in the first battle"); return; }
        // The attack sphere reads out its target in the system box, as retail's does: the part, then TARGET.
        if (battle.Paused && !_sphereSeen)
        {
            _sphereSeen = true;
            string readout = _view.SystemBox.Text;
            GD.Print($"FIELD: the attack sphere reads \"{readout.Replace("\n", " | ")}\"");
            if (readout.Split('\n').Length != 2 || !readout.Contains("HP ", StringComparison.Ordinal)) { Finish($"the attack sphere's readout is \"{readout}\""); return; }
        }
        // The battle clock stands while the sphere is up (an action is under way, BATTLE.PRG mode 2).
        if (battle.Paused)
        {
            if (_sphereClock < 0) _sphereClock = _view.BattleClock;
            else if (_view.BattleClock != _sphereClock) { Finish("the battle clock ran while the sphere was up"); return; }
        }
        else _sphereClock = -1;
        // The sphere as it opens (its lists sliding in) and once they are in.
        if (battle.Paused && ++_sphereFrames is 4 or 30 && _capture.Length != 0 && DisplayServer.GetName() != "headless") Capture(_sphereFrames == 4 ? "battle_sphere_open" : "battle_sphere");
        // A chain ability to grade: given as the first battle starts, so her presses are graded about each hit.
        if (elapsed == 1 && _view.Abilities.Count == 0) { _view.Abilities.Add(22); battle.Assign(_view.Abilities, _view.ChainButtons, _view.DefenseButtons); }
        // Circle just before her hit lands, as a player times a chain: from two frames before it, so a press that
        // falls while another is still held goes in a frame later, still in time.
        if (battle.AshleyStrikeFrame is >= 0 and <= 2) Tap("ps_circle");
        if (battle.Grades > _gradesSeen)
        {
            _gradesSeen = battle.Grades;
            GD.Print($"FIELD: a chain press graded {battle.LastGrade}");
            if (!_gradeCaptured) { _gradeCaptured = true; _gradeAt = elapsed + 3; }
        }
        if (_gradeAt > 0 && elapsed == _gradeAt) { Capture("battle_timing"); _gradeAt = -1; }
        // A damage number, once, 8 frames after an enemy first loses hit points.
        int enemyHp = battle.Combatants.Values.Where(c => c.Hostile).Sum(c => c.Hp);
        if (_numberAt == 0 && _enemyHp > 0 && enemyHp < _enemyHp) _numberAt = elapsed + 8;
        _enemyHp = enemyHp;
        if (_numberAt > 0 && elapsed == _numberAt) { Capture("battle_number"); _numberAt = -1; }
        // No more enemies telegraph at once than the room allows.
        int telegraphing = battle.Combatants.Values.Count(c => c.Hostile && c.Telegraph > 0), allowed = _view.Document.GetValueOrDefault("telegraphs", 0).AsInt32();
        if (allowed < 1 || telegraphing > allowed) { Finish($"{telegraphing} enemies telegraph at once where the room allows {allowed}"); return; }
        // An enemy's jagged balloon, once, partway through its telegraph.
        if (!_balloonCaptured && _capture.Length != 0 && DisplayServer.GetName() != "headless")
            foreach (var c in battle.Combatants.Values)
                if (c.Hostile && c.Telegraph == 40) { _balloonCaptured = true; Capture("battle_balloon"); GD.Print("FIELD: an enemy telegraphs its move"); break; }
        if (!_dotsCaptured && _capture.Length != 0 && DisplayServer.GetName() != "headless")
            foreach (var c in battle.Combatants.Values)
                if (c.Hostile && c.Telegraph == 20) { _dotsCaptured = true; Capture("battle_dots"); break; }
        // The edge pointer: while an enemy is alert after its move, it stands for a few frames where its head is off
        // the screen's left, and the pointer its jagged balloon becomes is captured, before it goes back.
        if (_pointerStage == 0 && _capture.Length != 0 && DisplayServer.GetName() != "headless" && _view.Field?.Camera is { } eye && eye.IsInsideTree())
            foreach (var c in battle.Combatants.Values)
            {
                if (!c.Hostile || c.Dead || c.Telegraph != 0 || c.Alert < 40) continue;
                var size = _view.GetViewport().GetVisibleRect().Size;
                foreach (var shift in new[] { new Vector3(-1200, 0, 0), new Vector3(1200, 0, 0), new Vector3(0, 0, -1200), new Vector3(0, 0, 1200) })
                {
                    var head = EventActor.RetailSpace * (c.Actor.Position + shift + new Vector3(0, -210, 0));
                    if (eye.IsPositionBehind(head)) continue;
                    var at = eye.UnprojectPosition(head);
                    if (at.X >= 0 || at.Y < size.Y * 0.25f || at.Y > size.Y * 0.75f) continue;
                    _pointerEnemy = c;
                    _pointerHome = c.Actor.Position;
                    c.Actor.Position += shift;
                    _pointerStage = 1;
                    break;
                }
                if (_pointerStage != 0) break;
            }
        else if (_pointerStage is > 0 and < 4 && _pointerEnemy != null)
        {
            if (++_pointerStage == 4)
            {
                Capture("battle_pointer");
                GD.Print("FIELD: an enemy off the screen telegraphs by the edge pointer");
                _pointerEnemy.Actor.Position = _pointerHome;
            }
        }
        if (_wonFrame == 0 && _view.Events.Exists(name => name.Contains("evt0003")))
        {
            _wonFrame = elapsed;
            if (battle.Grades == 0) { Finish("no chain press was graded in the first battle"); return; }
            GD.Print($"FIELD: battle won in {elapsed} frames with {ashley.Hp}/{ashley.MaxHp} HP; weapon DP {ashley.Blade.Dp}/{ashley.Blade.MaxDp}, PP {ashley.Blade.Pp}/{ashley.Blade.MaxPp}; events {string.Join(" > ", _view.Events)}");
            // Her blows wear her weapon.
            if (ashley.Blade.MaxDp > 0 && ashley.Blade.Dp >= ashley.Blade.MaxDp) { Finish("the battle did not wear Ashley's weapon"); return; }
        }
        if (_wonFrame != 0)
        {
            Steer(Vector2.Zero);
            // EVT 0003 heals Ashley and sheathes her weapon before it leaves the room.
            // Ashley arrives in the next room as EVT 0003 left her.
            if (!_leftBattle && _view.Rooms.Count > 3)
            {
                _leftBattle = true;
                if (ashley.Hp != ashley.MaxHp || battle.Drawn) { Finish($"EVT 0003 left Ashley at {ashley.Hp} HP with her weapon {(battle.Drawn ? "drawn" : "sheathed")}"); return; }
            }
            // The first time she is the player's again after the battle she must walk off in her stride, with her weapon
            // as she was given it and again once she has drawn or sheathed it (Circle or Cross).
            if (_leftBattle && _view.Controlled && _strideFrames < 200 && !battle.Paused)
            {
                if (battle.Busy) { Steer(Vector2.Zero); return; }
                _strideFrames++;
                if (_strideFrames == 46) { Steer(Vector2.Zero); _strideDrawn = battle.Drawn; Tap(battle.Drawn ? "ps_cross" : "ps_circle"); return; }
                if (_strideFrames is > 46 and < 120) { Steer(Vector2.Zero); return; }
                Steer(new Vector2(0, _strideFrames < 46 ? -1 : 1));
                if (_strideFrames is 45 or 165)
                {
                    string clip = ashley.Actor.Player?.AssignedAnimation ?? "";
                    GD.Print($"FIELD: after the battle Ashley moves in {clip} at speed {ashley.Actor.Player?.SpeedScale}, playing {ashley.Actor.Player?.IsPlaying()}, weapon {(battle.Drawn ? "drawn" : "sheathed")}, in {_view.Rooms[^1]}");
                    if (!(clip.EndsWith("_13", StringComparison.Ordinal) || clip.EndsWith("_19", StringComparison.Ordinal)) || ashley.Actor.Player?.SpeedScale != 1 || ashley.Actor.Player?.IsPlaying() != true) { Finish($"after the battle Ashley moves in {clip}, not her walk or run"); return; }
                    if (_strideFrames == 165 && (battle.Drawn == _strideDrawn || clip.StartsWith("base_", StringComparison.Ordinal) == battle.Drawn)) { Finish($"after {(_strideDrawn ? "sheathing" : "drawing")} Ashley moves in {clip} with her weapon {(battle.Drawn ? "drawn" : "sheathed")}"); return; }
                }
                if (_strideFrames == 165) { Steer(Vector2.Zero); _strideFrames = 200; }
                return;
            }
            if (_leftBattle && _view.Controlled && _strideFrames == 200 && StepMastery(battle, ashley, elapsed)) return;
            // The story then plays on through its rooms, fighting what it meets, until Ashley is the player's in a room
            // with nothing left to fight and no event to start, or the converted rooms end.
            _calm = _view.Controlled && battle.HostileCount() == 0 ? _calm + 1 : 0;
            if (_leftBattle && _view.EndOfContent.Length != 0) { Finish($"the story ran out of converted rooms at {_view.EndOfContent}"); return; }
            if (_leftBattle && _calm > 180 && _doorRooms == 0)
            {
                GD.Print($"FIELD: the story went on through {string.Join(" > ", _view.Rooms)}; events {string.Join(" > ", _view.Events)}; Ashley is the player's in {_view.Rooms[^1]}, with {ashley.Hp}/{ashley.MaxHp} HP");
                _doorRooms = _view.Rooms.Count;
            }
            if (_doorRooms != 0)
            {
                // A hidden room enemy can enter the fight while she approaches the exit (the prologue wyvern).
                if (_view.Controlled && battle.HostileCount() > 0) { _doorFrame = elapsed; Fight(battle, ashley, elapsed); return; }
                WalkThroughDoor(ashley, elapsed);
                return;
            }
            if (_leftBattle && _view.Controlled && battle.HostileCount() > 0) { Fight(battle, ashley, elapsed); return; }
            if (elapsed - _wonFrame > 60 * 60 * 30) { Finish($"the story stalled in {_view.Rooms[^1]} at {_view.EventNode}: {_view.WaitingOn}"); return; }
            else if (elapsed % 60 == 0) Tap("ps_circle");
            return;
        }
        if (elapsed > 60 * 240) { Finish($"the first battle did not end (hostile {battle.HostileCount()}, events {string.Join(" > ", _view.Events)}, node {_view.EventNode}, controlled {_view.Controlled}, settled {_view.Settled}, camera {_view.CameraIdle:x})"); return; }
        if (!_view.Controlled) { Steer(Vector2.Zero); if (elapsed % 60 == 0) Tap("ps_circle"); return; }
        if (elapsed == 2 && battle.HostileCount() != 2) { Finish($"MAP003 has {battle.HostileCount()} hostile soldiers, not 2"); return; }
        if (battle.Paused) { if (_sphereFrames >= 32 || _capture.Length == 0) Tap("ps_circle"); return; }
        if (battle.Busy) { Steer(Vector2.Zero); return; }
        if (!battle.Drawn) { Steer(Vector2.Zero); Tap("ps_circle"); return; }
        // First let a soldier come round the cart and strike; the battle must hit back.
        if (ashley.Hp == ashley.MaxHp && battle.HostileCount() == 2)
        {
            Steer(Vector2.Zero);
            if (elapsed > 60 * 60) Finish("the soldiers never attacked Ashley");
            return;
        }
        Fight(battle, ashley, elapsed);
    }

    private int _calm, _doorRooms, _doorFrame, _jumpFrame, _strideFrames;
    private Godot.Collections.Dictionary? _chest;
    private int _chestCarried;

    /// <summary>Stands Ashley before the room's first unopened, unlocked chest with something in it and opens it; null without one.</summary>
    private Godot.Collections.Dictionary? OpenChest()
    {
        if (_view.Player is not { } ashley) return null;
        foreach (var value in _view.Document.GetValueOrDefault("chests", new Godot.Collections.Array()).AsGodotArray())
        {
            var chest = value.AsGodotDictionary();
            if (chest["index"].AsInt32() >= 0x40 || chest["lock"].AsInt32() != 0 || _view.ChestContents(chest).Count == 0) continue;
            var tile = chest["tile"].AsGodotArray();
            int x = tile[0].AsInt32(), z = tile[1].AsInt32(), quadrant = chest["facing"].AsInt32() / 1024;
            var (dx, dz) = quadrant switch { 0 => (0, 1), 1 => (1, 0), 2 => (0, -1), _ => (-1, 0) };
            ashley.Position = new Vector3((x + dx) * 128 + 64, ashley.Position.Y, (z + dz) * 128 + 64);
            ashley.Facing = quadrant * 1024;
            _view.Interact(ashley);
            if (!_view.MenuOpen && !_view.LidOpening) { Finish("the chest did not open the gadget bag"); return null; }
            return chest;
        }
        return null;
    }
    private bool _strideDrawn, _sphereSeen;
    private int _sphereFrames;
    private float _ground, _peak;

    /// <summary>Walks Ashley along a route to the room's first door and through it; the next room must load and hand her back.</summary>
    private void WalkThroughDoor(FieldBattle.Combatant ashley, int elapsed)
    {
        if (_doorFrame == 0) _doorFrame = elapsed;
        // Once, the door's fade halfway: it subtracts, so the lit room goes red before black.
        if (!_fadeCaptured && _view.Fade is > 0.4f and < 0.7f && _capture.Length != 0 && DisplayServer.GetName() != "headless") { _fadeCaptured = true; Capture("door_fade"); }
        if (_view.Rooms.Count > _doorRooms)
        {
            Steer(Vector2.Zero);
            if (_view.Controlled && _view.Rooms.Count > _doorRooms + 1)
            { _doorRooms = _view.Rooms.Count; _doorFrame = elapsed; return; }
            if (_view.EndOfContent.Length != 0) { Finish($"the door led out of the converted rooms ({_view.EndOfContent})"); return; }
            if (_view.Controlled && _view.Rooms.Count == _doorRooms + 1)
            {
                if (_jumpFrame == 0)
                {
                    GD.Print($"FIELD: a door led from {_view.Rooms[_doorRooms - 1]} to {_view.Rooms[^1]}; Ashley arrived at {ashley.Actor.Position}");
                    _jumpFrame = elapsed;
                    _ground = ashley.Actor.Position.Y;
                    _peak = _ground;
                }
                int since = elapsed - _jumpFrame;
                // A standing jump: she must rise well clear of the floor and come back down onto it.
                if (since == 10) Tap("ps_square");
                _peak = Math.Min(_peak, ashley.Actor.Position.Y);
                if (since == 100)
                {
                    float rise = _ground - _peak;
                    GD.Print($"FIELD: Ashley jumped {rise} units and landed at {ashley.Actor.Position.Y} (floor {_ground})");
                    if (rise < 100) { Finish($"Ashley's jump rose only {rise} units"); return; }
                    if (Math.Abs(ashley.Actor.Position.Y - _ground) > 1) { Finish("Ashley did not land back on the floor"); return; }
                    Finish("");
                }
                return;
            }
            else if (elapsed % 60 == 0) Tap("ps_circle");
            return;
        }
        if (elapsed - _doorFrame > 60 * 60) { Finish($"Ashley did not reach a door in {_view.Rooms[^1]} (at {ashley.Actor.Position})"); return; }
        var doors = _view.Document["doors"].AsGodotArray();
        if (doors.Count == 0) { Finish($"{_view.Rooms[^1]} has no doors"); return; }
        var tile = doors[0].AsGodotDictionary()["tile"].AsGodotArray();
        var goal = new Vector3(tile[0].AsInt32() * 128 + 64, 0, tile[1].AsInt32() * 128 + 64);
        if (!_view.Controlled) { Steer(Vector2.Zero); return; }
        if (_view.Battle is { Paused: true }) { Tap("ps_cross"); return; }
        var route = _view.Path(ashley.Actor.Position, goal, _campaign ? 256 : FieldPlayer.StepHeight);
        while (route.Count > 1 && new Vector2(route[0].X - ashley.Actor.Position.X, route[0].Z - ashley.Actor.Position.Z).Length() < 24) route.RemoveAt(0);
        var waypoint = route.Count > 0 ? route[0] : goal;
        float angle = (ashley.Actor.FacingToward(waypoint) - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
        Steer(new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)));
        if (elapsed % 60 == 0) Tap("ps_cross");
        // After the battle she must still walk or run as she goes, not glide in another pose.
        string clip = ashley.Actor.Player?.AssignedAnimation ?? "";
        bool striding = clip.EndsWith("_13", StringComparison.Ordinal) || clip.EndsWith("_19", StringComparison.Ordinal);
        if (ashley.Actor.Position.DistanceTo(_lastDoorStep) > 2 && !striding && ++_glides == 30) { Finish($"Ashley moves to the door in {clip}, not her walk or run"); return; }
        _lastDoorStep = ashley.Actor.Position;
    }

    private Vector3 _lastDoorStep;
    private int _glides;

    /// <summary>Closes on the nearest enemy along a route round obstacles and attacks it through the sphere.</summary>
    private void Fight(FieldBattle battle, FieldBattle.Combatant ashley, int elapsed)
    {
        // The native "in time" window starts after the hit; zero is still the "fast" grade.
        // Alternate the earned buttons over a short chain, releasing each before the next cue.
        if (battle.AshleyStrikeFrame is not { } timing) { _fightLinked = false; _fightChainLinks = 0; }
        else
        {
            if (timing < 0) _fightLinked = false;
            var slots = Enumerable.Range(0, 3).Where(i => battle.ChainSlots[i] != 0).ToArray();
            if (_campaign && !_fightLinked && timing is >= 1 and <= 2 && _press == 0 && ashley.Risk < 65
                && slots.Length > 0 && _fightChainLinks < (slots.Length == 1 ? 1 : 6))
            {
                Tap(new[] { "ps_circle", "ps_triangle", "ps_square" }[slots[_fightChainLinks % slots.Length]]);
                _fightLinked = true; _fightChainLinks++;
            }
        }
        bool opened = battle.Paused && !_fightWasPaused;
        _fightWasPaused = battle.Paused;
        if (opened)
        {
            int hp = battle.Combatants.Values.Where(c => c.Targetable).Sum(c => c.Hp);
            bool progress = _fightLastHp < 0 || hp < _fightLastHp;
            _fightStalls = progress ? 0 : _fightStalls + 1;
            if (progress) _fightProgressAt = _frame;
            _fightLastHp = _fightLastHp < 0 ? hp : Math.Min(_fightLastHp, hp);
            _fightPartSearch = 0;
        }
        if (_campaign && _fightProgressAt > 0 && _frame - _fightProgressAt > 7200)
        { Finish($"campaign: no net HP reduction for 7200 frames in {_view.Zone},{_view.Map}, target {_fightTargetId}, {_view.SystemBox.Text}"); return; }
        if (battle.Paused)
        {
            string part = _view.SystemBox.Text.Split(" HP ", 2, StringSplitOptions.None)[0];
            if (_fightNextPart)
            {
                if (_press > 0) return;
                _fightPreferredPart = part;
                _fightNextPart = false;
            }
            if (_fightPreferredPart.Length > 0 && part != _fightPreferredPart)
            {
                if (_press == 0)
                    if (++_fightPartSearch < 6) Tap("ps_down");
                    else { Tap("ps_cross"); _fightPreferredPart = ""; _fightCloseUntil = _frame + 60; }
                return;
            }
            if (_fightStalls >= 3)
            { if (_press == 0) { Tap("ps_cross"); _fightPreferredPart = ""; _fightCloseUntil = _frame + 120; _fightStalls = 0; } return; }
            if (_campaign && _view.SystemBox.Text.StartsWith(_view.Content.Data("battle_text")["cube"].AsString(), StringComparison.Ordinal))
            {
                if (_press == 0)
                {
                    if (++_campaignCubePicks <= 4) Tap("ps_right");
                    else { Tap("ps_cross"); _campaignCubePicks = 0; _campaignWaitForEnemyUntil = _frame + 120; }
                }
            }
            else if (_view.SystemBox.Text.Contains(" HP 0/", StringComparison.Ordinal))
            {
                if (_press == 0)
                    if (++_emptyLimbPicks < 6) { Tap("ps_down"); _fightNextPart = true; }
                    else { Tap("ps_cross"); _emptyLimbPicks = 0; _fightPreferredPart = ""; _fightCloseUntil = _frame + 60; }
            }
            else { _campaignCubePicks = _emptyLimbPicks = 0; Tap("ps_circle"); }
            return;
        }
        if (battle.Busy) { Steer(Vector2.Zero); return; }
        if (!battle.Drawn) { Steer(Vector2.Zero); Tap("ps_circle"); return; }
        FieldBattle.Combatant? nearest = null;
        foreach (var c in battle.Combatants.Values)
            if (c.Targetable && (nearest == null || c.Actor.Position.DistanceTo(ashley.Actor.Position) < nearest.Actor.Position.DistanceTo(ashley.Actor.Position))) nearest = c;
        if (nearest == null) { Steer(Vector2.Zero); return; }
        if (_fightTargetId != nearest.Actor.Id) { _fightTargetId = nearest.Actor.Id; _fightPreferredPart = ""; _fightProgressAt = _frame; _fightLastHp = -1; _fightStalls = 0; }
        var offset = nearest.Actor.Position - ashley.Actor.Position;
        if (_frame >= _fightCloseUntil && FieldBattle.InSphere(ashley, nearest))
        {
            Steer(Vector2.Zero);
            if (elapsed % 8 == 0) Tap("ps_circle");
            return;
        }
        // Point the stick along the route to the soldier, relative to the camera: up is the camera's yaw.
        var route = _view.Path(ashley.Actor.Position, nearest.Actor.Position, _campaign ? 256 : FieldPlayer.StepHeight);
        if (_campaign && route.Count == 0 && !nearest.Marked && (_view.Zone, _view.Map) != (13, 4) && _frame - _fightProgressAt > 600)
        {
            // An optional enemy on another platform need not be slain to use an open exit (room 9,13's wolf).
            // Its flags, HP and AI remain untouched while the test player retreats through the normal door path.
            GD.Print($"CAMPAIGN: leaving unreachable optional {nearest.Name} alive in {_view.Zone},{_view.Map}");
            _campaignBypassEnemies = true;
            Steer(Vector2.Zero);
            return;
        }
        while (route.Count > 1 && new Vector2(route[0].X - ashley.Actor.Position.X, route[0].Z - ashley.Actor.Position.Z).Length() < 24) route.RemoveAt(0);
        var waypoint = route.Count > 0 ? route[0] : nearest.Actor.Position;
        float heading = ashley.Actor.FacingToward(waypoint), angle = (heading - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
        Steer(new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)));
        if (_campaign)
        {
            var probe = ashley.Actor.Position.MoveToward(waypoint, 96);
            if (_view.FloorAt(probe) is { } ahead && ashley.Actor.Position.Y - ahead is > FieldPlayer.StepHeight and <= 256) Input.ActionPress("ps_r2");
            else Input.ActionRelease("ps_r2");
        }
        if (_campaign && _frame - _fightProgressAt > 120 && Math.Abs(waypoint.Y - ashley.Actor.Position.Y) > FieldPlayer.StepHeight && _frame % 120 == 60)
            Tap("ps_square");
    }

    /// <summary>
    /// The enemies' body motion against retail's numbers, run on the room's first walking enemy set on a tile's centre
    /// at its floor: a rising (BATTLE.PRG 0x800a2cd4 state 4: 128 up at 8 frames a 128, so 8 frames of 16, aloft, 7
    /// then 37 queued) and a landing (state 1: 8 frames of 16 down, 9 then 10, grounded); a level jump to the next tile
    /// (0x800a208c: crouching 6 frames, then 11 in the air at 11 across, landing 121 on, 7 short of the centre, on the
    /// floor, 0x800ac690); a turn head first (0x800df720: for a body under 128 wide, 128 a frame); and Ashley walking for
    /// an event by her AI's walk record (opcode 2a, 0x800e5308: 9 a frame, her walk 13, standing by 6 at the point).
    /// </summary>
    private string CheckMovement(FieldBattle battle)
    {
        // A previous jump's heading must not become the direction of a later ground climb.
        var body = new Node3D();
        try
        {
            var walker = new EventActor("climb-history", 0, body);
            var control = new FieldPlayer(walker, p => p.X >= 96 ? -192 : 0) { Animates = false, Turns = false };
            control.Step(0, false, true);
            for (int i = 0; i < 26; i++) control.Step(0, false);
            float before = walker.Position.Z;
            for (int i = 0; i < 80; i++) control.Step(3072, false);
            if (walker.Position.X < 96 || walker.Position.Y != -192 || Math.Abs(walker.Position.Z - before) > 0.01f)
                return $"a ground climb after a northward jump went to {walker.Position}, not east onto the ledge at z {before}";
        }
        finally { body.Free(); }
        var enemy = battle.Combatants.Values.FirstOrDefault(c => c != battle.Ashley && c.Mind != null && c.Mover != null && !c.Dead && c.Gait == FieldBattle.Gait.Walk);
        if (enemy == null) return "the room has no walking enemy";
        var m = enemy.Mind!;
        var actor = enemy.Actor;
        var home = actor.Position;
        // A flat tile (its floors grid floor that of its centre) with a level neighbour across to jump to.
        (int X, int Z) tile = default, to = default;
        bool found = false;
        for (int z = 1; z < 31 && !found; z++)
            for (int x = 1; x < 31 && !found; x++)
            {
                int centre = battle.JumpFloor(x * 128 + 64, z * 128 + 64);
                if (_view.FloorAt(new Vector3(x * 128 + 64, 0, z * 128 + 64)) == null || battle.FloorOf((x, z)) != centre) continue;
                foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    if (_view.FloorAt(new Vector3((x + dx) * 128 + 64, 0, (z + dz) * 128 + 64)) != null && battle.JumpFloor((x + dx) * 128 + 64, (z + dz) * 128 + 64) == centre && battle.FloorOf((x + dx, z + dz)) == centre)
                    { (tile, to, found) = ((x, z), (x + dx, z + dz), true); break; }
            }
        if (!found) return "the room has no flat tile with a level neighbour";
        int floor = battle.FloorOf(tile);
        void Place((int X, int Z) t, int y) { actor.Position = new Vector3(t.X * 128 + 64, y, t.Z * 128 + 64); m.Tile = t; }
        void Frame() => battle.Embody(enemy);
        // An animation is checked only where the enemy's sets have it (a walker has no flyer's).
        bool Shows(int number) => !enemy.Actor.HasClip($"{enemy.Set}_{number}") && !enemy.Actor.HasClip($"base_{number}") || FieldBattle.Pose(enemy) == number;
        // Rising.
        Place(tile, floor);
        m.Airborne = false;
        battle.Issue(enemy, 12);
        battle.Post(enemy);
        Frame();
        if (!m.Airborne || !Shows(7)) return $"rising did not start aloft playing 7 (aloft {m.Airborne}, playing {FieldBattle.Pose(enemy)})";
        var steps = new List<int>();
        for (int i = 0; i < 12 && (m.Settling != 0); i++) { float before = actor.Position.Y; Frame(); steps.Add((int)MathF.Round(actor.Position.Y - before)); }
        if (steps.Count != 8 || steps.Any(d => d != -16)) return $"rising took the steps {string.Join(",", steps)}, not 8 of -16";
        if ((int)MathF.Round(actor.Position.Y) != floor - 128) return $"rising ended at {actor.Position.Y}, not {floor - 128}";
        // Landing.
        Frame();
        m.LeapTile = tile;
        battle.Issue(enemy, 11);
        battle.Post(enemy);
        Frame();
        if (!Shows(9)) return $"landing did not start with 9 (playing {FieldBattle.Pose(enemy)})";
        steps.Clear();
        for (int i = 0; i < 12 && m.Settling == 2; i++) { float before = actor.Position.Y; Frame(); steps.Add((int)MathF.Round(actor.Position.Y - before)); }
        if (steps.Count != 8 || steps.Any(d => d != 16)) return $"landing took the steps {string.Join(",", steps)}, not 8 of 16";
        if (m.Airborne || !Shows(10)) return $"landing did not end grounded playing 10 (aloft {m.Airborne}, playing {FieldBattle.Pose(enemy)})";
        for (int i = 0; i < 120 && m.Settling != 0; i++) Frame();
        // A level jump to the next tile across.
        int ground = battle.JumpFloor(tile.X * 128 + 64, tile.Z * 128 + 64);
        Place(tile, ground);
        m.Run = false;
        m.SpeedA = m.SpeedB = 9;
        m.FlagsA = 2;
        m.FlagsB = 0;
        m.LeapTile = to;
        battle.Issue(enemy, 14);
        battle.Post(enemy);
        var start = actor.Position;
        int crouch = 0, air = 0;
        Frame();
        while (m.Jump == 2 && crouch < 20) { crouch++; Frame(); }
        while (m.Jump == 3 && air < 40) { air++; Frame(); }
        var landed = actor.Position;
        float across = MathF.Round(Math.Abs(landed.X - start.X) + Math.Abs(landed.Z - start.Z));
        if (crouch != 5 || air != 11) return $"the jump crouched {crouch + 1} frames and flew {air}, not 6 and 11";
        if (across != 121 || (int)MathF.Round(landed.Y) != ground) return $"the jump landed {across} across at {landed.Y}, not 121 across on {ground}";
        for (int i = 0; i < 40 && m.Jump != 0; i++) Frame();
        Frame();
        // A turn head first by a body under 128 wide: its body 128 a frame once its twist is spent.
        Place(to, ground);
        actor.Facing = 0;
        m.Twist = 0;
        m.TurnFrames = 0;
        var yaws = new List<int>();
        for (int i = 0; i < 40; i++)
        {
            m.TurnTo = 2048;
            m.TurnRate = 128;
            m.BodyTurn = false;
            m.Command = FieldBattle.Body.Turn;
            battle.Post(enemy);
            Frame();
            yaws.Add((int)MathF.Round(actor.Facing) & 0xFFF);
            if (((int)MathF.Round(actor.Facing) + m.Twist & 0xFFF) == 2048 && m.Twist == 0) break;
        }
        for (int i = 1; i < yaws.Count; i++)
        {
            int step = Math.Abs(((yaws[i] - yaws[i - 1] + 2048) & 0xFFF) - 2048);
            if (enemy.Girth < 128 && step != 0 && step != 128 && i < yaws.Count - 1) return $"the body turned {step} in a frame, not 128 ({string.Join(",", yaws)})";
        }
        if (((int)MathF.Round(actor.Facing) + m.Twist & 0xFFF) != 2048) return $"the turn ended facing {(int)MathF.Round(actor.Facing) + m.Twist & 0xFFF}, not 2048";
        // Ashley's walk for an event (opcode 2a): by her AI's walk record, 9 a frame, standing (6) at the point.
        actor.Position = home;
        var ashley = battle.Ashley!;
        var from = FieldBattle.TileOf(ashley);
        (int X, int Z)? goal = null;
        foreach (var (dx, dz) in new[] { (2, 0), (-2, 0), (0, 2), (0, -2) })
        {
            var g = (from.X + dx, from.Z + dz);
            var half = (from.X + dx / 2, from.Z + dz / 2);
            if (_view.FloorAt(new Vector3(g.Item1 * 128 + 64, 0, g.Item2 * 128 + 64)) is { } a && _view.FloorAt(new Vector3(half.Item1 * 128 + 64, 0, half.Item2 * 128 + 64)) is { } b
                && Math.Abs(a - ashley.Actor.Position.Y) < 1 && Math.Abs(b - ashley.Actor.Position.Y) < 1
                && !battle.Walled(from, FieldBattle.Toward(dx / 2, dz / 2)) && !battle.Walled(half, FieldBattle.Toward(dx / 2, dz / 2))) { goal = g; break; }
        }
        if (goal is not { } aim) return "Ashley has no level ground two tiles away to walk to";
        foreach (var other in battle.Combatants.Values) if (other != ashley && other.Mind != null) other.Mind.Held = true;
        ashley.Actor.Position = new Vector3(from.X * 128 + 64, ashley.Actor.Position.Y, from.Z * 128 + 64);
        if (!battle.ScriptedWalk(ashley.Actor.Id, aim.X * 128 + 64, aim.Z * 128 + 64)) return "Ashley did not take an event's walk by her AI";
        var path = new List<float>();
        for (int i = 0; i < 120 && ashley.Mind!.Script != null; i++)
        {
            var before = ashley.Actor.Position;
            battle.StepScripted();
            path.Add(new Vector2(ashley.Actor.Position.X - before.X, ashley.Actor.Position.Z - before.Z).Length());
        }
        if (ashley.Mind!.Script != null) return $"Ashley's event walk did not end (from {from} to {aim}, at {ashley.Actor.Position}, steps {string.Join(",", path.Select(d => d.ToString("0.#")))}, posted {ashley.Mind.Script.Command} {ashley.Mind.Script.Code})";
        battle.StepScripted();
        if (!path.Any(d => Math.Abs(d - 9) < 0.01f)) return $"Ashley's event walk did not go 9 a frame ({string.Join(",", path.Select(d => d.ToString("0.#")))})";
        if (FieldBattle.Pose(ashley) != 6) return $"Ashley did not stand at the point by 6 (playing {FieldBattle.Pose(ashley)})";
        GD.Print($"FIELD: the movement matched retail (rising and landing 8 frames of 16, the jump 6 and 11 frames landing 121 across, the turn {yaws.Count} frames, Ashley's walk {path.Count} frames)");
        return "";
    }

    private void Capture(string name)
    {
        if (_capture.Length != 0 && _view?.Field is { } field && !name.StartsWith("campaign_frame_", StringComparison.Ordinal))
            GD.Print($"FIELD_CAPTURE: {name}, fade {_view.Fade}, blank {field.ScreenBlanked}, eye {field.CameraEye}, target {field.CameraTarget}");
        Capture(this, _capture, name);
    }

    /// <summary>Saves the window as shown (sRGB, with HDR 2D's linear buffer encoded) to a folder, unless headless or none is given.</summary>
    public static void Capture(Node node, string folder, string name)
    {
        if (folder.Length == 0 || DisplayServer.GetName() == "headless") return;
        Directory.CreateDirectory(folder);
        var image = node.GetViewport().GetTexture().GetImage();
        if (node.GetTree().Root.UseHdr2D)
            for (int y = 0; y < image.GetHeight(); y++)
                for (int x = 0; x < image.GetWidth(); x++)
                    image.SetPixel(x, y, image.GetPixel(x, y).LinearToSrgb());
        image.Convert(Image.Format.Rgba8);
        image.SavePng(Path.Combine(folder, name + ".png"));
    }

    private void Finish(string error)
    {
        _shot = -1;
        Input.ActionRelease("ps_circle");
        if (_view != null) GD.Print($"FIELD: {_view.MenuSounds} menu sounds played");
        if (error.Length == 0) GD.Print("FIELD_CONTRACTS_OK");
        else GD.PrintErr("FIELD_CONTRACTS_FAILED: " + error);
        FieldView.ReleaseSaves();
        GetTree().Quit(error.Length == 0 ? 0 : 1);
    }
}
