// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The room's weather (BATTLE.PRG 0x80090EEC and its renderers): drops falling through the room in 3D, only over the
/// room's own weather rectangle (so not inside its buildings). A drop starts 2048 to 2560 units up at a random place
/// in the rectangle and moves each frame by the wind: its direction and fall angle (4096 a turn) and a speed from the
/// intensity; it is drawn as a faint streak from where it was a frame before, and starts again once it falls below
/// the map's zero plane. Kind 4 is embers, rising instead. Intensity sets how many fall (8 each, at least 4, at most
/// the rectangle's share, one per four tiles) and how fast. While the weather drifts (opcode 78), the direction,
/// intensity and angle wander a step at a time within the room's ranges.
/// </summary>
public sealed class EventRain
{
    private const int MaximumDrops = 128;
    private readonly Vector3[] _drops = new Vector3[MaximumDrops], _speeds = new Vector3[MaximumDrops];
    private readonly RandomNumberGenerator _random = new() { Seed = 0x5a17 };
    private readonly System.Collections.Generic.List<Wol.Engine.World.FieldLine> _lines = new();
    private int _type, _capacity, _tick, _frames, _time;
    private float _intensity, _yaw, _pitch;
    private (float Intensity, float Yaw, float Pitch) _from, _to;
    private Rect2I _area;
    private GDictionary? _room;
    private int[] _steps = { 1, 1, 1 };

    /// <summary>Whether the weather wanders within the room's ranges (opcode 78 turns it off and on).</summary>
    public bool Drifting = true;

    /// <summary>The engine's field the streaks are drawn in.</summary>
    public Wol.Engine.World.FieldScene? Scene;

    /// <summary>Takes a room's weather (its kind, rectangle and ranges) and starts it as the room loads.</summary>
    public void Room(GDictionary? weather)
    {
        _room = weather;
        _area = new Rect2I();
        _type = 0;
        _capacity = 0;
        if (weather == null) { Draw(); return; }
        var area = weather["area"].AsGodotArray();
        _area = new Rect2I(area[0].AsInt32(), area[1].AsInt32(), area[2].AsInt32(), area[3].AsInt32());
        int kind = weather["type"].AsInt32();
        Range("intensity", out int intensity, out _);
        Range("direction", out int direction, out _);
        Range("pitch", out int pitch, out _);
        Start(kind, intensity, direction, pitch, 0);
    }

    private void Range(string key, out int low, out int high)
    {
        low = high = 0;
        if (_room?.GetValueOrDefault(key).VariantType != Variant.Type.Array) return;
        var range = _room[key].AsGodotArray();
        low = Math.Min(range[0].AsInt32(), range[1].AsInt32());
        high = Math.Max(range[0].AsInt32(), range[1].AsInt32());
    }

    private void Start(int kind, int intensity, int direction, int pitch, int frames)
    {
        if (kind != _type || _capacity == 0)
        {
            _type = kind;
            int tiles = Math.Max(0, _area.Size.X / 128) * Math.Max(0, _area.Size.Y / 128);
            _capacity = kind == 0 || _area.Size.X <= 0 || _area.Size.Y <= 0 ? 0 : Math.Clamp(tiles / (kind == 4 ? 2 : 4), 4, MaximumDrops);
            for (int i = 0; i < _capacity; i++) Respawn(i, true);
        }
        _from = (_intensity, _yaw, _pitch);
        _to = (intensity, direction, pitch);
        _tick = 0;
        _frames = frames;
        if (frames == 0) (_intensity, _yaw, _pitch) = _to;
    }

    /// <summary>Opcode 77: the weather's kind and intensity (0-15), its wind direction and fall angle, over some frames.</summary>
    public void Set(int kind, int intensity, int direction, int pitch, int frames) => Start(kind, intensity, direction, pitch, frames);

    private void Respawn(int i, bool anywhere)
    {
        float x = _area.Position.X + _random.RandfRange(0, Math.Max(1, _area.Size.X));
        float z = _area.Position.Y + _random.RandfRange(0, Math.Max(1, _area.Size.Y));
        // Rain starts high (embers low); at the start they are spread through the fall.
        float y = _type == 4 ? _random.RandfRange(512, 1024) : anywhere ? _random.RandfRange(-2560, 0) : _random.RandfRange(-2560, -2048);
        _drops[i] = new Vector3(x, y, z);
        _speeds[i] = new Vector3(0, (_random.RandiRange(0, 15) + 8) * 2, 0);
    }

    /// <summary>One 30 Hz frame of the weather.</summary>
    public void Step()
    {
        _time++;
        if (_tick < _frames)
        {
            _tick++;
            float t = _tick / (float)_frames;
            _intensity = Mathf.Lerp(_from.Intensity, _to.Intensity, t);
            _yaw = Mathf.Lerp(_from.Yaw, _to.Yaw, t);
            _pitch = Mathf.Lerp(_from.Pitch, _to.Pitch, t);
        }
        else if (Drifting && _room != null) Drift();
        // The wind (0x80090B28): its speed from the intensity, along its direction and fall angle.
        float scale = Math.Clamp(_intensity * 6, 12, 90), speed = scale * 1.75f;
        float Unit(float a) => a * Mathf.Tau / 4096;
        var wind = new Vector3(Mathf.Sin(Unit(_yaw)) * Mathf.Cos(Unit(_pitch)), Mathf.Sin(Unit(_pitch)), -Mathf.Cos(Unit(_yaw)) * Mathf.Cos(Unit(_pitch))) * speed;
        int active = _type == 0 ? 0 : Math.Clamp((int)(_intensity * 8), 4, _capacity);
        for (int i = 0; i < active; i++)
        {
            if (_type == 4)
            {
                _drops[i].Y -= _speeds[i].Y;
                if (_drops[i].Y < -1024) Respawn(i, false);
                continue;
            }
            _drops[i] += wind;
            if (_drops[i].Y > 0) Respawn(i, false);
        }
        Draw(active, wind);
    }

    /// <summary>The ranges' wandering: every 16 to 128 frames a value takes a step, now and then turning back.</summary>
    private void Drift()
    {
        int[] masks = { 0xF, 0x1F, 0x3F, 0x7F };
        var rates = _room!.GetValueOrDefault("rates", new GArray()).AsGodotArray();
        int Rate(int i) => i < rates.Count ? masks[Math.Clamp(rates[i].AsInt32(), 0, 3)] : 0x7F;
        void Walk(ref float value, string key, int step, int offset)
        {
            if (((_time + offset) & Rate(step)) != 0) return;
            if (_random.RandiRange(0, 7) < 3) _steps[step] = -_steps[step];
            Range(key, out int low, out int high);
            value = Math.Clamp(value + _steps[step], low, high);
        }
        Walk(ref _yaw, "direction", 0, 0);
        Walk(ref _pitch, "pitch", 2, 30);
        Walk(ref _intensity, "intensity", 1, 60);
    }

    /// <summary>Hands the engine this frame's streaks, added (on the effects' render layer).</summary>
    private void Draw(int active = 0, Vector3 wind = default)
    {
        _lines.Clear();
        for (int i = 0; i < active; i++)
        {
            Vector3 head = _drops[i], tail = _type == 4 ? head + new Vector3(0, 192, 0) : head - wind;
            // Rain streaks fade from (96, 96, 128) to (32, 32, 100), added at a quarter; embers glow red.
            var (bright, dim) = _type == 4 ? (Color.Color8(192, 64, 64), Color.Color8(32, 8, 32)) : (Color.Color8(96, 96, 128), Color.Color8(32, 32, 100));
            float level = _type == 4 ? 1 : 0.5f;
            _lines.Add(new(EventActor.RetailSpace * head, EventActor.RetailSpace * tail, bright * level, dim * level));
        }
        Scene?.DrawLines("weather", System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_lines), true, FieldView.EffectLayer);
    }

    /// <summary>Stops the weather.</summary>
    public void Clear() { _type = 0; _capacity = 0; _room = null; Draw(); }
}
