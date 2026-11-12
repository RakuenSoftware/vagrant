// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The CONGRATULATIONS screen a boss's defeat opens (MENU/MENUF.PRG, submenu 0), on the 320 by 240 screen over the
/// dimmed field, in RANK.DIS's lettering (<see cref="Installation.ResultsConversion"/>). Its parts come in on one
/// timer T, a frame of retail's 30 Hz each:
/// <list type="bullet">
/// <item>CONGRATULATIONS centred on x 160 at y 40 from T 0, each half shaded across from (128, 96, 64) to (200, 180,
/// 160), the right half the other way; it pops in: a glow rising over seven frames, then grey 192 and 224, then
/// easing to its colours over four frames.</item>
/// <item>SCORE at (48, 80) from T 32 and MAP COMPLETED at (47, 102) from T 112, in (100, 180, 220), wiped in from
/// the left: a sprite whose right edge is 64 behind the edge, which runs eight pixels a frame, is solid; nearer, it
/// glows in twelve-pixel columns by how far the edge is past them. The score is nine digits from x 122 (y 83) 12
/// apart, commas after the third and sixth at y 94 taking seven more, then PTS. at (244, 88); it counts up from T 64
/// to T 96. The percentage is three digits at x 225, 237 and 249 (y 105) and the percent sign at (261, 110),
/// counting up from T 144 to 176.</item>
/// <item>RISKBREAKER RANK at (83, 138) in (200, 80, 20) from T 192, glowing in over eight frames, and the rank's
/// words side by side, centred on x 160 at y 154 in grey from T 208, popping in.</item>
/// <item>BONUS at (63, 189) with its colon at (123, 190) from T 272, and the bonus reel at x 131: sixteen stops of a stat
/// and an amount in the gold palette, a stop every 16 lines, turning from T 273 at an eighth of its speed, which
/// rises by one a frame to 48 from T 288. The PRESS BUTTON prompt slides in with it from the right to x 214, the
/// Circle button bobbing beside it.</item>
/// <item>Circle (or T 2072) slows the reel; it stops on a stop at the middle line, which then takes Circle (or 450
/// frames) to end the screen, which fades over eight frames. The stop's amount is added to the stat for good.
/// Circle before T 273 brings everything to its end at once.</item>
/// </list>
/// The score is MENUF's (0x80108ab0): kills by enemy class and attacks by weapon kind times their points, twenty
/// thousand for each boss time trial recorded, the highest chain by its band, a hundred thousand for each clear, all
/// multiplied by a cleared game's play time bonus, then the kill streaks' score and the other score (the heal bonus: a
/// hundred thousand less a hundred for each heal since the last, which this screen adds as it opens). The rank is
/// the first whose title and score thresholds are met. The bonus reel is chosen by Ashley's stats: 0 if her
/// intelligence and agility are both at least her strength, else 1 if her strength and agility are at least her
/// intelligence, else 2.
/// <para>Sounds (resident ids): 0x72 as a count starts and 0x73 as it ends, 0x77 and 0x78 as the rank comes in and
/// as the reel lands, 0x76 for the Circle that slows it, and the reel's turning loop (0x74 and 0x75) from the eighth
/// frame of its speeding up until it lands. The screen's music is the playing piece's results theme (see
/// FieldView.OpenResults). Not yet done: the additive glows are drawn on a layer of their own over the plain parts
/// rather than in retail's drawing order (each part one quad, additive for its first eight frames, all at one
/// order-table entry so later parts lie under earlier ones), as the order the parts are built in was not read.</para>
/// </summary>
public sealed partial class ResultsScreen : RetailScreen
{
    private const int Congratulations = 22, Score = 18, Map = 21, Colon = 26, Comma = 10, Points = 20, Percent = 19;
    private const int RankTitle = 12, Bonus = 11, Plus = 24;
    private static readonly Color Blue = Rgb(100, 180, 220), Rank = Rgb(200, 80, 20), Grey = Rgb(128, 128, 128);
    private static readonly Color Warm = Rgb(128, 96, 64), Pale = Rgb(200, 180, 160), Prompt = Color.Color8(255, 128, 128);

    /// <summary>A PlayStation draw colour (128 leaves the texture as it is) as a modulation.</summary>
    private static Color Rgb(int r, int g, int b) => new(r / 128f, g / 128f, b / 128f);

    private readonly record struct Quad(Texture2D Sheet, Rect2 Source, Rect2 Target, Color[] Colours);

    private Texture2D? _sheet, _reelSheet, _labels;
    private GDictionary _tables = new();
    private Runtime.HudLettering? _lettering;
    private readonly List<Quad> _plain = new(), _glow = new();
    private Control _glowLayer = null!;
    private readonly RandomNumberGenerator _random = new();

    private int _score, _percent, _shownScore, _shownPercent, _rank, _reel;
    private int _timer, _state, _stateTimer, _speed, _slide;
    private int _selection;

    /// <summary>Plays one of the screen's resident sounds (MENUF.PRG's ids).</summary>
    public Action<int>? Sound;
    /// <summary>Stops one of them that is still playing.</summary>
    public Action<int>? StopSound;

    /// <summary>Whether the screen is up.</summary>
    public bool Showing { get; private set; }

    /// <summary>The bonus the reel stopped on, once the screen has ended: its stat sprite (13 STR, 14 AGL, 15 INT, 16 HP, 17 MP) and amount.</summary>
    public (int Stat, int Amount) Result { get; private set; }

    public void Configure(Texture2D? sheet, Texture2D? reelSheet, GDictionary? tables, Texture2D? labels, Runtime.HudLettering? lettering)
    {
        _sheet = sheet;
        _reelSheet = reelSheet;
        _tables = tables ?? new GDictionary();
        _labels = labels;
        _lettering = lettering;
        Visible = false;
        _glowLayer = new Control { Name = "Glow", MouseFilter = MouseFilterEnum.Ignore, Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add }, TextureFilter = TextureFilterEnum.Nearest };
        _glowLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _glowLayer.Draw += () => { foreach (var quad in _glow) Emit(_glowLayer, quad); };
        AddChild(_glowLayer);
    }

    /// <summary>
    /// The score MENUF reckons, capped at 999,999,999: <paramref name="kills"/> by enemy class and
    /// <paramref name="attacks"/> by weapon kind times their points, <paramref name="maxChain"/> by its band,
    /// <paramref name="clears"/> times a hundred thousand, and the streak and other scores.
    /// </summary>
    public long Reckon(int[] kills, int[] attacks, int maxChain, int clears, int timeTrials, long playMinutes, long streak, long other)
    {
        long score = 0;
        var killPoints = _tables.GetValueOrDefault("kill_points", new GArray()).AsGodotArray();
        var attackPoints = _tables.GetValueOrDefault("attack_points", new GArray()).AsGodotArray();
        for (int i = 0; i < kills.Length && i < killPoints.Count; i++) score += (long)kills[i] * killPoints[i].AsInt32();
        for (int i = 0; i < attacks.Length && i < attackPoints.Count; i++) score += (long)attacks[i] * attackPoints[i].AsInt32();
        score += maxChain * maxChain switch { <= 5 => 10, <= 10 => 20, <= 15 => 40, <= 20 => 80, <= 25 => 160, <= 30 => 320, <= 50 => 640, _ => 1280 };
        score += timeTrials * 20000L + clears * 100000L;
        // A cleared game's play time multiplies all that (MENUF 0x80108ab0): under 300 doubles it, under 540 adds half,
        // under 600 a quarter. The time's unit was not read; minutes is taken from the thresholds.
        if (clears > 0 && playMinutes < 600) score = playMinutes >= 540 ? score + score / 4 : playMinutes >= 300 ? score + score / 2 : score * 2;
        score += streak + other;
        return Math.Min(999999999L, score);
    }

    /// <summary>Opens the screen on <paramref name="score"/>, the map's percentage and the rank's title count, with the reel Ashley's stats choose.</summary>
    public void Open(int score, int percent, int titles, int strength, int intelligence, int agility)
    {
        _score = score;
        _percent = percent;
        _rank = 15;
        var titleThresholds = _tables.GetValueOrDefault("title_thresholds", new GArray()).AsGodotArray();
        var scoreThresholds = _tables.GetValueOrDefault("score_thresholds", new GArray()).AsGodotArray();
        for (int i = 0; i < 16 && i < titleThresholds.Count && i < scoreThresholds.Count; i++)
            if (titles >= titleThresholds[i].AsInt32() && score >= scoreThresholds[i].AsInt64()) { _rank = i; break; }
        _reel = intelligence >= strength && agility >= strength ? 0 : strength >= intelligence && agility >= intelligence ? 1 : 2;
        _selection = (int)(_random.Randi() & 0xF0) | 8;
        _timer = _state = _stateTimer = _speed = _slide = 0;
        _shownScore = _shownPercent = 0;
        Result = (0, 0);
        Showing = Visible = true;
        Compose();
    }

    /// <summary>A frame of the screen; Circle is <paramref name="circle"/>. False once it has ended (see <see cref="Result"/>).</summary>
    public bool Step(bool circle)
    {
        if (!Showing) return false;
        switch (_state)
        {
            case 0:
                if (circle && _timer < 273) { _timer = 272; _shownScore = _score; _shownPercent = _percent; circle = false; }
                _timer++;
                // The score counts up over its element's frames 32 to 64, the percentage over its own.
                int scoreTime = _timer - 32, mapTime = _timer - 112;
                _shownScore = scoreTime < 32 ? 0 : scoreTime < 64 ? Math.Min(_score, (_score >> 5) * (scoreTime - 32)) : _score;
                // The count's ticking from its start, its close as it ends; the rank's chime as its words come in.
                if (scoreTime == 32 || mapTime == 32) Sound?.Invoke(0x72);
                if (scoreTime == 64 || mapTime == 64) Sound?.Invoke(0x73);
                if (_timer == 208) { Sound?.Invoke(0x77); Sound?.Invoke(0x78); }
                if (mapTime >= 64) _shownPercent = _percent;
                else if (mapTime >= 32) _shownPercent = _percent >= 32 ? _percent * (mapTime - 32) / 32 : Math.Min(_percent, _shownPercent + 1);
                if (_timer >= 273) _selection = (_selection + _speed / 8) & 0xFF;
                if (_timer > 288 && _speed < 48)
                {
                    _slide = ++_speed;
                    // The reel's turning, a loop, from the eighth frame of its speeding up until it lands.
                    if (_speed == 8) { Sound?.Invoke(0x74); Sound?.Invoke(0x75); }
                }
                if (_speed == 48 && (circle || _timer > 2072))
                {
                    if (circle) Sound?.Invoke(0x76);
                    _state = 1;
                }
                break;
            case 1:
                _timer++;
                _selection = (_selection + _speed / 8) & 0xFF;
                if (_slide > 0) _slide--;
                if (_speed > 8) _speed--;
                else if ((_selection & 15) == 8)
                {
                    _state = 2;
                    _stateTimer = 0;
                    StopSound?.Invoke(0x74);
                    StopSound?.Invoke(0x75);
                    Sound?.Invoke(0x77);
                    Sound?.Invoke(0x78);
                }
                break;
            case 2:
                _timer++;
                if (circle || ++_stateTimer > 450) { _state = 3; _stateTimer = 0; }
                break;
            case 3:
                if (++_stateTimer >= 8)
                {
                    var stop = Stop(_selection >> 4);
                    Result = (stop[0].AsInt32(), stop[1].AsInt32());
                    Showing = Visible = false;
                    return false;
                }
                break;
        }
        Compose();
        return true;
    }

    private GArray Stop(int index)
    {
        var reels = _tables.GetValueOrDefault("reels", new GArray()).AsGodotArray();
        return reels.Count > _reel ? reels[_reel].AsGodotArray()[index & 15].AsGodotArray() : new GArray { 13, 0 };
    }

    private Rect2 Sprite(int index)
    {
        var sprites = _tables.GetValueOrDefault("sprites", new GArray()).AsGodotArray();
        if (index < 0 || index >= sprites.Count) return new Rect2();
        var s = sprites[index].AsGodotArray();
        return new Rect2(s[0].AsInt32(), s[1].AsInt32(), s[2].AsInt32(), s[3].AsInt32());
    }

    private void Add(int index, float x, float y, Color left, Color right, bool glow, Texture2D? sheet = null, float clipTop = 0, float clipBottom = 240)
    {
        if ((sheet ?? _sheet) is not { } texture) return;
        var source = Sprite(index);
        var target = new Rect2(x, y, source.Size);
        // Clipped to the lines given, as the bonus reel's window is.
        float top = Math.Max(target.Position.Y, clipTop), bottom = Math.Min(target.End.Y, clipBottom);
        if (bottom <= top) return;
        source = new Rect2(source.Position.X, source.Position.Y + top - target.Position.Y, source.Size.X, bottom - top);
        target = new Rect2(target.Position.X, top, target.Size.X, bottom - top);
        (glow ? _glow : _plain).Add(new Quad(texture, source, target, new[] { left, right, right, left }));
    }

    private void Add(int index, float x, float y, Color colour, bool glow) => Add(index, x, y, colour, colour, glow);

    /// <summary>
    /// Pops a sprite in at its element's time <paramref name="t"/>: a glow of its colour rising over seven frames,
    /// grey 192 and 224, then easing from grey 192 to its colour over frames 10 to 13.
    /// </summary>
    private void PopIn(int index, float x, float y, Color left, Color right, int t)
    {
        if (t <= 0) return;
        Color grey(int v) => Rgb(v, v, v);
        if (t < 8) Add(index, x, y, left * (t / 8f), right * (t / 8f), true);
        else if (t == 8) Add(index, x, y, grey(192), grey(192), false);
        else if (t == 9) Add(index, x, y, grey(224), grey(224), false);
        else if (t < 14) Add(index, x, y, grey(192).Lerp(left, (t - 10) / 4f), grey(192).Lerp(right, (t - 10) / 4f), false);
        else Add(index, x, y, left, right, false);
    }

    /// <summary>Fades a sprite in: a glow of its colour over eight frames, then the colour.</summary>
    private void FadeIn(int index, float x, float y, Color colour, int t)
    {
        if (t <= 0) return;
        if (t < 8) Add(index, x, y, colour * (t / 8f), true);
        else Add(index, x, y, colour, false);
    }

    /// <summary>Wipes a sprite in behind an edge eight pixels a frame from <paramref name="start"/>.</summary>
    private void Wipe(int index, float x, float y, float start, int t)
    {
        if (t <= 0) return;
        float edge = start + 8 * t;
        var source = Sprite(index);
        if (x + source.Size.X < edge - 64) { Add(index, x, y, Blue, false); return; }
        if (_sheet == null) return;
        for (float column = 0; column < source.Size.X; column += 12)
        {
            float scale = Math.Clamp(edge - (x + column), 0, 64) / 64;
            if (scale <= 0) continue;
            float width = Math.Min(12, source.Size.X - column);
            _glow.Add(new Quad(_sheet, new Rect2(source.Position.X + column, source.Position.Y, width, source.Size.Y), new Rect2(x + column, y, width, source.Size.Y),
                new[] { Blue * scale, Blue * scale, Blue * scale, Blue * scale }));
        }
    }

    /// <summary>Lays out the frame's sprites, each element at its own time (in the closing fade, all at 8 less the frames gone).</summary>
    private void Compose()
    {
        _plain.Clear();
        _glow.Clear();
        int Time(int start) => _state == 3 ? 8 - _stateTimer : Math.Clamp(_timer - start, 0, 64);

        // CONGRATULATIONS, its halves shaded opposite ways.
        var left = Sprite(Congratulations);
        float x = 160 - (left.Size.X + Sprite(Congratulations + 1).Size.X) / 2;
        PopIn(Congratulations, x, 40, Warm, Pale, Time(0));
        PopIn(Congratulations + 1, x + left.Size.X, 40, Pale, Warm, Time(0));

        // The score.
        int t = Time(32);
        Wipe(Score, 48, 80, 48, t);
        Wipe(Colon, 114, 87, 48, t);
        string digits = Math.Clamp(_shownScore, 0, 999999999).ToString("D9");
        x = 122;
        for (int i = 0; i < 9; i++)
        {
            Wipe(digits[i] - '0', x, 83, 48, t);
            x += 12;
            if (i is 2 or 5) { Wipe(Comma, x, 94, 48, t); x += 7; }
        }
        Wipe(Points, 244, 88, 48, t);

        // The map's percentage.
        t = Time(112);
        Wipe(Map, 47, 102, 47, t);
        Wipe(Colon, 217, 109, 47, t);
        string percent = Math.Clamp(_shownPercent, 0, 999).ToString("D3");
        for (int i = 0; i < 3; i++) Wipe(percent[i] - '0', 225 + 12 * i, 105, 47, t);
        Wipe(Percent, 261, 110, 47, t);

        // The rank.
        FadeIn(RankTitle, 83, 138, Rank, Time(192));
        var ranks = _tables.GetValueOrDefault("ranks", new GArray()).AsGodotArray();
        if (_rank < ranks.Count)
        {
            var words = ranks[_rank].AsGodotArray();
            float width = 0;
            foreach (var word in words) width += Sprite(word.AsInt32()).Size.X;
            x = 160 - width / 2;
            foreach (var word in words)
            {
                PopIn(word.AsInt32(), x, 154, Grey, Grey, Time(208));
                x += Sprite(word.AsInt32()).Size.X;
            }
        }

        // The bonus and its reel.
        t = Time(272);
        FadeIn(Bonus, 63, 189, Pale, t);
        FadeIn(Colon, 123, 190, Pale, t);
        if (t > 0)
        {
            float clip = _speed / 8;
            for (int k = -1; k <= 1; k++)
            {
                int entry = ((_selection >> 4) + k) & 15;
                // The stop before at 198 plus the selection's low nibble, the stop itself 16 above, the next 32: the
                // stop lands on the middle line (190) when the nibble is 8.
                float y = 190 + (_selection & 15) - 8 - 16 * k;
                var stop = Stop(entry);
                int stat = stop[0].AsInt32();
                bool middle = y >= 190 && y < 206;
                float light = middle ? 1 : Math.Max(0, 128 - 16 * Math.Abs(y - 190) / 4) / 128f;
                var colour = Rgb(128, 128, 128) * (t < 8 ? t / 8f : 1) * light;
                float top = 190 - clip, bottom = 190 + clip + 16;
                Add(stat, 131, y - 1, colour, colour, !middle || t < 8, _reelSheet, top, bottom);
                float plusAt = 131 + Sprite(stat).Size.X + 1;
                Add(Plus, plusAt, y + 4, colour, colour, !middle || t < 8, _reelSheet, top, bottom);
                Add(stop[1].AsInt32() % 10, plusAt + Sprite(Plus).Size.X + 1, y - 1, colour, colour, !middle || t < 8, _reelSheet, top, bottom);
            }
        }
        QueueRedraw();
        _glowLayer.QueueRedraw();
    }

    private void Emit(CanvasItem canvas, Quad quad)
    {
        var (unit, offset) = FieldView.Pin(Size, 1);
        var target = new Rect2(offset + quad.Target.Position * unit, quad.Target.Size * unit);
        var size = quad.Sheet.GetSize();
        var points = new[] { target.Position, new Vector2(target.End.X, target.Position.Y), target.End, new Vector2(target.Position.X, target.End.Y) };
        var s = quad.Source;
        var uvs = new[] { s.Position / size, new Vector2(s.End.X, s.Position.Y) / size, s.End / size, new Vector2(s.Position.X, s.End.Y) / size };
        canvas.DrawPolygon(points, quad.Colours, uvs, quad.Sheet);
    }

    public override void _Draw()
    {
        foreach (var quad in _plain) Emit(this, quad);
        // The prompt: PRESS BUTTON in the HUD lettering, sliding in from the right, the Circle button bobbing beside it.
        if (_labels == null || _lettering == null || _slide <= 0 || _state == 3) return;
        var (unit, offset) = FieldView.Pin(Size, 1);
        DrawSetTransform(offset);
        float x = 214 + 288 - 6 * Math.Min(_slide, 48);
        int frame = _timer & 15;
        float bob = frame < 9 ? 193 + frame / 2 : 199 - frame / 4;
        DrawTextureRectRegion(_labels, new Rect2(new Vector2(x, bob - 2) * unit, new Vector2(16, 16) * unit), new Rect2(32, 130, 16, 16), Color.Color8(0xF0, 0x90, 0xB0));
        _lettering!.Draw(this, _tables.GetValueOrDefault("press", "PRESS").AsString(), new Vector2(x + 18, 187), unit, Prompt);
        _lettering!.Draw(this, _tables.GetValueOrDefault("button", "BUTTON").AsString(), new Vector2(x + 18, 197), unit, Prompt);
        DrawSetTransform(Vector2.Zero);
    }
}
