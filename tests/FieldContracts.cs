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
    /// <summary>With <c>--shortcuts</c>, once Ashley is the player's L2 holds the shortcut menu open and CHAIN and ITEMS are taken from it, in place of the room's other steps: each must open its page, and leaving the page must close the menu.</summary>
    private bool _shortcuts;
    /// <summary>With <c>--carry</c>, Ashley lifts the nearest block (kinds 4 to 6), turns about with it and sets it down.</summary>
    private bool _carry;
    private int _carryStep, _carryCrates;
    private string _carryTiles = "";
    /// <summary>With <c>--cube</c>, Ashley draws her weapon facing the nearest crate, opens the attack sphere on it ("Cube") and strikes it, which leaves it standing.</summary>
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
    private int _eventEvery;
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
            if (argument.StartsWith("--watch=", StringComparison.Ordinal)) _watchEvery = int.Parse(argument["--watch=".Length..]);
            // Characters are drawn Original unless --enhanced asks otherwise, whatever the player's saved choice.
            if (argument.StartsWith("--", StringComparison.Ordinal)) CharacterQuality.Choose(System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--enhanced") >= 0, save: false);
            if (argument.StartsWith("--", StringComparison.Ordinal)) FieldLighting.Choose(System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--enhanced") >= 0, save: false);

            if (argument.StartsWith("--tap-every=", StringComparison.Ordinal)) _tapEvery = Math.Max(1, int.Parse(argument["--tap-every=".Length..]));
            if (argument.StartsWith("--room=", StringComparison.Ordinal) && argument["--room=".Length..].Split(',') is [var zone, var map]) _room = (int.Parse(zone), int.Parse(map));
            if (argument.StartsWith("--at=", StringComparison.Ordinal) && argument["--at=".Length..].Split(',') is [var tileX, var tileZ]) _at = (int.Parse(tileX), int.Parse(tileZ));
            if (argument.StartsWith("--yaw=", StringComparison.Ordinal)) _yaw = int.Parse(argument["--yaw=".Length..]);
            if (argument == "--fight") _fight = true;
            if (argument == "--shortcuts") _shortcuts = true;
            if (argument == "--carry") _carry = true;
            if (argument == "--cube") _cube = true;
            if (argument.StartsWith("--status=", StringComparison.Ordinal)) _status = int.Parse(argument["--status=".Length..]);
            if (argument == "--ending") _endingCheck = true;
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
            AddChild(_view);
            return;
        }
        var content = GetNode<RetailContent>("/root/RetailContent");
        if (!content.Installed) { Finish("installation unavailable: " + content.Failure); return; }
        _view = GD.Load<PackedScene>("res://scenes/field/field.tscn").Instantiate<FieldView>();
        AddChild(_view);
        if (_view.Failure.Length != 0) { Finish(_view.Failure); return; }
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
        if (_ending != null)
        {
            _frame++;
            if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && _frame is 300 or 3000 or 6000) Capture($"ending_{_frame}");
            if (_ending.Finished) { GD.Print($"FIELD: the ending ran {_frame} frames to the title"); Finish(""); return; }
            if (_frame > 60 * 600) Finish("the ending did not finish");
            return;
        }
        if (_view == null || _shot < 0) return;
        _frame++;
        if (_view.Failure.Length != 0) { Finish(_view.Failure); return; }
        if (_room != null) { StepRoom(); return; }
        if (_battleFrame != 0) { StepBattle(); return; }
        if (_controlFrame != 0) { StepControl(); return; }
        // Hold Circle for a few frames every second (or every --tap-every frames, at a reader's pace): enough to
        // finish and confirm each line of text.
        if (_frame % _tapEvery == 0) Input.ActionPress("ps_circle");
        if (_frame % _tapEvery == 4) Input.ActionRelease("ps_circle");
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && _frame % 120 == 30) Capture($"event_{_shot++:d3}_{_view.EventNode}");
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
        if (_status >= 0 && _view.Controlled && _view.Battle?.Ashley is { } afflicted && !afflicted.Statuses.ContainsKey(_status))
        {
            afflicted.Statuses[_status] = FieldBattle.Duration(_status);
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
                    Tap("ps_circle");
                    break;
                }
                case 85: Capture("cube_strike"); break;
                case > 75 and < 160: _cubeRisk = Math.Max(_cubeRisk, cubeStriker.Risk); break;
                case 160:
                    Capture("cube_after");
                    if (new List<(string, int, int, int, float, int)>(_view.Crates).Count != _carryCrates || CrateTiles() != _carryTiles) { Finish("striking the crate moved or broke it"); return; }
                    // RISK rises with the blow and falls back soon after (footage: RISK 1, then ZERO).
                    Finish(_cubeRisk <= _cubeRiskBefore ? "striking the crate did not raise her RISK" : "");
                    return;
            }
            return;
        }
        if (_fight && _view.Controlled && _view.Battle is { Ashley: { } brawler } brawl)
        {
            if (_view.Abilities.Count == 0) { _view.Abilities.Add(22); brawl.Assign(_view.Abilities, _view.ChainButtons, _view.DefenseButtons); }
            if (brawl.GameOver) { Finish("Ashley fell in the room's fight"); return; }
            if (brawl.HostileCount() == 0) { Steer(Vector2.Zero); if (++_calm > 180) Finish(""); return; }
            _calm = 0;
            // The sphere is held open long enough to be seen whole, and captured once it is.
            if (!brawl.Paused) _sphereFrames = 0;
            else if (++_sphereFrames < 40) { if (_sphereFrames == 36) Capture($"fight_sphere_{_frame:d5}"); return; }
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
        if (_played && _view.Controlled && _afterPlay == 31) Capture("played_control");
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
    private bool _played;
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
            var definition = _view.Content.Data("characters").GetValueOrDefault(fighter.Actor.Character.ToString("X2"), new Godot.Collections.Dictionary()).AsGodotDictionary();
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
        foreach (var entry in storable)
            if (entry["kind"].AsString() == "weapons" && entry["record"].AsGodotDictionary()["equipped"].AsBool()) return "the weapon in hand could be stored";
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
    /// In a room with a magic seal: Ashley, standing before it and facing it, casts Unlock (action 92), which breaks it
    /// for 3 magic points.
    /// </summary>
    /// <summary>
    /// A rolling crate (kind 0 or 1) pushed from beside it moves a tile on and turns to the orientation BATTLE.PRG's
    /// roll table gives for that direction (facing quadrant q pushes towards -z, -x, +z, +x for q = 0 to 3).
    /// </summary>
    private string CheckCrate()
    {
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
            Godot.Collections.Dictionary? blow = null;
            foreach (var effect in action["effects"].AsGodotArray())
                if (effect.AsGodotDictionary() is var e && e["effect"].AsInt32() == 58) blow = e;
            if (blow == null) continue;
            int hp = fighter.Hp;
            var statuses = new Dictionary<int, int>(fighter.Statuses);
            fighter.Hp = fighter.MaxHp;
            // The blow lands on one limb at random and varies by 5 either way: it must fall within the limbs' range.
            int least = int.MaxValue, most = 0;
            for (int limb = 0; limb < fighter.Limbs.Count; limb++)
            {
                if (fighter.Limbs[limb].MaxHp <= 0) continue;
                int damage = _view.Battle!.TrapDamage(fighter, blow, level, limb, false);
                least = Math.Min(least, damage);
                most = Math.Max(most, damage);
            }
            if (least == int.MaxValue) least = most = _view.Battle!.TrapDamage(fighter, blow, level, -1, false);
            var tile = trap["tile"].AsGodotArray();
            _view.StepOnTrap((tile[0].AsInt32(), tile[1].AsInt32()));
            int lost = fighter.MaxHp - fighter.Hp;
            string text = _view.SystemBox.Text, name = action["name"].AsString();
            GD.Print($"FIELD: the trap {name} (type {trap["type"]}, level {level}) cost {lost} of {fighter.MaxHp}: \"{text}\"");
            fighter.Hp = hp;
            fighter.Statuses.Clear();
            foreach (var (status, frames) in statuses) fighter.Statuses[status] = frames;
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
                int mp = caster.Mp;
                string note = _view.Cast(92);
                if (record["lock"].AsInt32() == 2 || caster.Mp != mp - 3) return $"Unlock left the seal at {x}, {z}: {note}";
                GD.Print($"FIELD: Unlock broke the seal at {x}, {z}");
                return "";
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
                var weapon = new Godot.Collections.Dictionary { ["blade"] = new Godot.Collections.Dictionary(), ["grip"] = new Godot.Collections.Dictionary(), ["name"] = held["record"].AsGodotDictionary()["name"], ["entry"] = held };
                if (!_view.Holds(weapon)) return "Ashley did not hold the inventory's equipped weapon";
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
            string fused = _view.Combine(first, second);
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
    private int _battleFrame, _press, _wonFrame;
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
            ashley.Mp = ashley.MaxMp;
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
        if (_press > 0 && --_press == 0) Input.ActionRelease(_pressed);
        var battle = _view.Battle;
        if (battle?.Ashley is not { } ashley) { if (_leftBattle) { Tap("ps_circle"); return; } Finish("the battle has no Ashley"); return; }
        if (_capture.Length != 0 && DisplayServer.GetName() != "headless" && elapsed % 30 == 0) Capture($"battle_{elapsed:d4}");
        if (battle.GameOver) { Finish("Ashley fell in the first battle"); return; }
        // The attack sphere reads out its target in the system box, as retail's does: the part, then TARGET.
        if (battle.Paused && !_sphereSeen)
        {
            _sphereSeen = true;
            string readout = _view.SystemBox.Text;
            GD.Print($"FIELD: the attack sphere reads \"{readout.Replace("\n", " | ")}\"");
            if (readout.Split('\n').Length != 2 || !readout.Contains("HP ", StringComparison.Ordinal)) { Finish($"the attack sphere's readout is \"{readout}\""); return; }
        }
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
            if (_doorRooms != 0) { WalkThroughDoor(ashley, elapsed); return; }
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
        var route = _view.Path(ashley.Actor.Position, goal);
        while (route.Count > 1 && new Vector2(route[0].X - ashley.Actor.Position.X, route[0].Z - ashley.Actor.Position.Z).Length() < 24) route.RemoveAt(0);
        var waypoint = route.Count > 0 ? route[0] : goal;
        float angle = (ashley.Actor.FacingToward(waypoint) - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
        Steer(new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)));
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
        if (battle.Paused) { Tap("ps_circle"); return; }
        if (battle.Busy) { Steer(Vector2.Zero); return; }
        if (!battle.Drawn) { Steer(Vector2.Zero); Tap("ps_circle"); return; }
        FieldBattle.Combatant? nearest = null;
        foreach (var c in battle.Combatants.Values)
            if (c.Hostile && !c.Dead && (nearest == null || c.Actor.Position.DistanceTo(ashley.Actor.Position) < nearest.Actor.Position.DistanceTo(ashley.Actor.Position))) nearest = c;
        if (nearest == null) { Steer(Vector2.Zero); return; }
        var offset = nearest.Actor.Position - ashley.Actor.Position;
        if (new Vector2(offset.X, offset.Z).Length() < 240)
        {
            Steer(Vector2.Zero);
            if (elapsed % 8 == 0) Tap("ps_circle");
            return;
        }
        // Point the stick along the route to the soldier, relative to the camera: up is the camera's yaw.
        var route = _view.Path(ashley.Actor.Position, nearest.Actor.Position);
        while (route.Count > 1 && new Vector2(route[0].X - ashley.Actor.Position.X, route[0].Z - ashley.Actor.Position.Z).Length() < 24) route.RemoveAt(0);
        var waypoint = route.Count > 0 ? route[0] : nearest.Actor.Position;
        float heading = ashley.Actor.FacingToward(waypoint), angle = (heading - _view.Gameplay.Yaw) * Mathf.Tau / 4096f;
        Steer(new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)));
    }

    private void Capture(string name) => Capture(this, _capture, name);

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
