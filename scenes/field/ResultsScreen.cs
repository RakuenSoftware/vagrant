// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using Godot;
using GArray = Godot.Collections.Array;
using GDictionary = Godot.Collections.Dictionary;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The CONGRATULATIONS screen a boss's defeat opens (MENU/MENUF.PRG, submenu 0), on the 320 by 224 frame over the
/// dimmed field, in RANK.DIS's lettering (loaded as MENUF loads it: its three pictures at (832 + 64 i, 256), 255
/// lines, and its first eight palettes at (768, 511) with the first two colours cleared). Its parts come in on one
/// timer T, a frame of retail's 30 Hz each, and each is a Gouraud-shaded quad of a sprite of MENUF's table, all in one
/// ordering-table entry (so the first built lies on top), shaded left to right by its colour pair:
/// <list type="bullet">
/// <item>CONGRATULATIONS's halves centred on x 160 at y 40 from T 0, (128, 96, 64) to (200, 180, 160) and the other
/// way, popping in (0x80106A80): for its first seven frames its colour times t / 8 through the next palette, added;
/// then grey 192 and 224, still added; then easing from 192 to its colour over frames 10 to 13.</item>
/// <item>SCORE, its colon, nine digits (commas after the third and sixth) and PTS. centred on x 160 at y 80 from T
/// 32, and MAP COMPLETED, its colon, three digits and the percent sign at y 102 from T 112, in (100, 180, 220), wiped
/// in (0x80107140) behind an edge at x 160 + 8 t: a sprite whose right edge is 64 behind it is plain, one starting
/// past it is not drawn, and the rest glow in twelve-pixel columns, each corner by how far the edge is past it (of
/// 64). The score counts up from T 64 to T 96, the percentage from T 144 to 176.</item>
/// <item>RISKBREAKER RANK centred at y 138 in (200, 80, 20) from T 192, fading in (0x8010664C: its colour times t / 8,
/// added, for seven frames), and the rank's words centred at y 154 in grey from T 208, popping in.</item>
/// <item>BONUS and its colon at y 190, centred on 130, (128, 96, 64) to (200, 180, 160), fading in from T 272, and
/// the reel after them, seen through a window from 190 less an eighth of its speed to 16 lines more than as far
/// below: while it turns, three stops a line at a time (0x80107698), the lines from 190 to 205 in the gold palette,
/// the others fading by 16 a line outside it and drawn subtracted and then added (so the lighter of them and the
/// field shows); once it has stopped, the stop alone (0x80105DD8), glowing in for eight frames. A stop is its stat's
/// sprite, a plus and its amount's digits. The PRESS BUTTON prompt slides in with it from the right to x 214, the
/// Circle button bobbing beside it, under everything.</item>
/// <item>Circle (or T 2072) slows the reel; it stops on a stop at the middle line, which then takes Circle (or 450
/// frames) to end the screen, its parts fading out over eight frames (the score and map now fading, not wiping).
/// The stop's amount is added to the stat for good. Circle before T 273 brings everything to its end at once.</item>
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
/// FieldView.OpenResults). The prompt's words are printed by BATTLE.PRG's HUD lettering and its Circle comes from the
/// HUD's texture page, which the HUD draws.</para>
/// </summary>
public sealed partial class ResultsScreen : RetailScreen
{
    private const int Congratulations = 22, Score = 18, Map = 21, Colon = 26, Comma = 10, Points = 20, Percent = 19;
    private const int RankTitle = 12, Bonus = 11, Plus = 24;
    /// <summary>The colour pairs MENUF shades its parts by (0x80109738 on, four bytes each): CONGRATULATIONS, the score's fade and wipe, the map's, the rank's title and words, and BONUS.</summary>
    private const int CongratulationsShade = 0, ScoreFade = 3, ScoreWipe = 5, MapFade = 7, MapWipe = 9, RankShade = 11, WordsShade = 13, BonusShade = 19;
    /// <summary>The reel's palettes: the gold one, and the next for its glow.</summary>
    private const int ReelClut = 32754;
    private static readonly Color Prompt = Color.Color8(255, 128, 128);

    private GDictionary _tables = new();
    private Texture2D? _labels;
    private Runtime.HudLettering? _lettering;
    private readonly RandomNumberGenerator _random = new();

    private int _score, _percent, _shownScore, _shownPercent, _rank, _reel;
    private int _timer, _state, _stateTimer, _speed, _slide;
    private int _selection;
    /// <summary>Whether this is ENDING.PRG's copy (0x8006C214): over the rank's picture, without the bonus, any button closing it.</summary>
    private bool _ending;

    /// <summary>Plays one of the screen's resident sounds (MENUF.PRG's ids).</summary>
    public Action<int>? Sound;
    /// <summary>Stops one of them that is still playing.</summary>
    public Action<int>? StopSound;

    /// <summary>Whether the screen is up.</summary>
    public bool Showing { get; private set; }

    /// <summary>The bonus the reel stopped on, once the screen has ended: its stat sprite (13 STR, 14 AGL, 15 INT, 16 HP, 17 MP) and amount.</summary>
    public (int Stat, int Amount) Result { get; private set; }

    /// <summary>Takes RANK.DIS (loaded into video memory as MENUF loads it), MENUF's tables, and the HUD's labels and lettering for the prompt.</summary>
    public void Configure(byte[] rank, GDictionary? tables, Texture2D? labels, Runtime.HudLettering? lettering)
    {
        for (int part = 0; part < 3; part++)
        {
            int at = part * 0x8220, image = at + 8 + BitConverter.ToInt32(rank, at + 8);
            if (part == 0)
            {
                // The first palettes, their first two colours cleared (0x80102E68).
                var palettes = (byte[])rank.Clone();
                Array.Clear(palettes, at + 20, 4);
                LoadImage(768, 511, 128, 1, palettes, at + 20);
            }
            LoadImage(832 + 64 * part, 256, BitConverter.ToUInt16(rank, image + 8), 255, rank, image + 12);
        }
        _tables = tables ?? new GDictionary();
        _labels = labels;
        _lettering = lettering;
        Visible = false;
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
        // under 600 a quarter. The time is the game record's word at +0x90 (0x8005FF30), in minutes: ENDING marks a
        // clear under 600 of it in the record's flags (0x801031FC), which only ten hours makes reachable.
        if (clears > 0 && playMinutes < 600) score = playMinutes >= 540 ? score + score / 4 : playMinutes >= 300 ? score + score / 2 : score * 2;
        score += streak + other;
        return Math.Min(999999999L, score);
    }

    /// <summary>The rank a score and a count of titles earn: the first whose title and score thresholds are met (MENUF; ENDING 0x8006DFD0).</summary>
    public int Rank(long score, int titles)
    {
        var titleThresholds = _tables.GetValueOrDefault("title_thresholds", new GArray()).AsGodotArray();
        var scoreThresholds = _tables.GetValueOrDefault("score_thresholds", new GArray()).AsGodotArray();
        for (int i = 0; i < 16 && i < titleThresholds.Count && i < scoreThresholds.Count; i++)
            if (titles >= titleThresholds[i].AsInt32() && score >= scoreThresholds[i].AsInt64()) return i;
        return 15;
    }

    /// <summary>Opens the screen on <paramref name="score"/>, the map's percentage and the rank's title count, with the reel Ashley's stats choose.</summary>
    public void Open(int score, int percent, int titles, int strength, int intelligence, int agility)
    {
        _score = score;
        _percent = percent;
        _rank = Rank(score, titles);
        _reel = intelligence >= strength && agility >= strength ? 0 : strength >= intelligence && agility >= intelligence ? 1 : 2;
        _selection = (int)(_random.Randi() & 0xF0) | 8;
        _timer = _state = _stateTimer = _speed = _slide = 0;
        _shownScore = _shownPercent = 0;
        Result = (0, 0);
        Showing = Visible = true;
        Compose();
    }

    /// <summary>
    /// Opens ENDING.PRG's copy of the screen after the credits (0x8006BD78): the rank's picture (ENDSCRnn.DIS, a TIM
    /// whose picture is loaded at (0, 256)) fading in to 128 over its first 64 frames behind the same parts but the
    /// bonus, drawn in the ending's draw environment (cleared to black, no dithering).
    /// </summary>
    public void OpenEnding(byte[] picture, int score, int percent, int titles)
    {
        int block = 8 + ((BitConverter.ToInt32(picture, 4) & 8) != 0 ? BitConverter.ToInt32(picture, 8) : 0);
        LoadImage(0, 256, BitConverter.ToUInt16(picture, block + 8), BitConverter.ToUInt16(picture, block + 10), picture, block + 12);
        _ending = true;
        Open(score, percent, titles, 0, 0, 0);
    }

    /// <summary>A frame of the screen; Circle is <paramref name="circle"/> (any button for the ending's). False once it has ended (see <see cref="Result"/>).</summary>
    public bool Step(bool circle)
    {
        if (!Showing) return false;
        switch (_state)
        {
            case 0 when _ending:
                // The ending's (0x8006C214): its timer stops at 4096; a button closes it after this frame.
                _timer = Math.Min(4096, _timer + 1);
                Count();
                Compose();
                if (circle) { _state = 3; _stateTimer = 0; }
                return true;
            case 0:
                if (circle && _timer < 273) { _timer = 272; _shownScore = _score; _shownPercent = _percent; circle = false; }
                _timer++;
                Count();
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


    /// <summary>The score's and the percentage's counting up, and their sounds and the rank's (MENUF 0x80104DBC, 0x8010516C, 0x8010384C).</summary>
    private void Count()
    {
        // The score counts up over its element's frames 32 to 64, the percentage over its own.
        int scoreTime = _timer - 32, mapTime = _timer - 112;
        _shownScore = scoreTime < 32 ? 0 : scoreTime < 64 ? Math.Min(_score, (_score >> 5) * (scoreTime - 32)) : _score;
        // The count's ticking from its start, its close as it ends; the rank's chime as its words come in.
        if (scoreTime == 32 || mapTime == 32) Sound?.Invoke(0x72);
        if (scoreTime == 64 || mapTime == 64) Sound?.Invoke(0x73);
        if (_timer == 208) { Sound?.Invoke(0x77); Sound?.Invoke(0x78); }
        if (mapTime >= 64) _shownPercent = _percent;
        else if (mapTime >= 32) _shownPercent = _percent >= 32 ? _percent * (mapTime - 32) / 32 : Math.Min(_percent, _shownPercent + 1);
    }

    private GArray Stop(int index)
    {
        var reels = _tables.GetValueOrDefault("reels", new GArray()).AsGodotArray();
        return reels.Count > _reel ? reels[_reel].AsGodotArray()[index & 15].AsGodotArray() : new GArray { 13, 0 };
    }

    /// <summary>A sprite of MENUF's table (0x801091D8): its corner and size on its texture page, the page and its palette.</summary>
    private (int U, int V, int Width, int Height, int Page, int Clut) Sprite(int index)
    {
        var sprites = _tables.GetValueOrDefault("sprites", new GArray()).AsGodotArray();
        if (index < 0 || index >= sprites.Count) return default;
        var s = sprites[index].AsGodotArray();
        return (s[0].AsInt32(), s[1].AsInt32(), s[2].AsInt32(), s[3].AsInt32(), s[4].AsInt32(), s[5].AsInt32());
    }

    private int Width(int index) => Sprite(index).Width;

    /// <summary>A colour pair of MENUF's: the left (or only) colour and the right.</summary>
    private ((int R, int G, int B) Left, (int R, int G, int B) Right) Shade(int pair)
    {
        var colours = _tables.GetValueOrDefault("colours", new GArray()).AsGodotArray();
        (int, int, int) At(int i) => i < colours.Count ? (colours[i].AsGodotArray()[0].AsInt32(), colours[i].AsGodotArray()[1].AsInt32(), colours[i].AsGodotArray()[2].AsInt32()) : (128, 128, 128);
        return (At(pair), At(pair + 1));
    }

    /// <summary>A sprite's quad with its corners' colours (left at 0 and 2, right at 1 and 3), in the screen's one entry.</summary>
    private void Quad(int index, int x, int y, (int R, int G, int B) left, (int R, int G, int B) right, bool glow)
    {
        var s = Sprite(index);
        int u1 = (s.U + s.Width) & 255, v1 = (s.V + s.Height) & 255;
        var corners = new Corner[]
        {
            new(x, y, s.U, s.V, left.R, left.G, left.B), new(x + s.Width, y, u1, s.V, right.R, right.G, right.B),
            new(x, y + s.Height, s.U, v1, left.R, left.G, left.B), new(x + s.Width, y + s.Height, u1, v1, right.R, right.G, right.B),
        };
        Order(0, () => Polygon(corners, glow ? s.Page | 0x20 : s.Page, glow ? s.Clut + 1 : s.Clut, true, true));
    }

    private static (int R, int G, int B) Times((int R, int G, int B) c, Func<int, int> by) => (by(c.R), by(c.G), by(c.B));

    /// <summary>A part fading in (0x8010664C): its colour times t / 8, glowing, for seven frames, then its colour.</summary>
    private void Fade(int index, int x, int y, int pair, int t)
    {
        if (t <= 0) return;
        var (left, right) = Shade(pair);
        if (t < 8) Quad(index, x, y, Times(left, c => c * t >> 3), Times(right, c => c * t >> 3), true);
        else Quad(index, x, y, left, right, false);
    }

    /// <summary>A part popping in (0x80106A80): glowing, its colour times t / 8 then grey 192 and 224; then easing from 192 to its colour over frames 10 to 13.</summary>
    private void Pop(int index, int x, int y, int pair, int t)
    {
        if (t <= 0) return;
        var (left, right) = Shade(pair);
        Func<int, int> by = t switch
        {
            < 8 => c => c * t >> 3,
            8 => _ => 192,
            9 => _ => 224,
            < 14 => c => (c * (t - 10) + (4 - (t - 10)) * 192) >> 2,
            _ => c => c,
        };
        Quad(index, x, y, Times(left, by), Times(right, by), t < 10);
    }

    /// <summary>A part wiped in behind <paramref name="edge"/> (0x80107140).</summary>
    private void Wipe(int index, int x, int y, int pair, int edge)
    {
        var s = Sprite(index);
        var (left, right) = Shade(pair);
        if (x + s.Width < edge - 64) { Quad(index, x, y, left, right, false); return; }
        if (x >= edge) return;
        for (int column = 0; column < s.Width; column += 12, x += 12)
        {
            int width = Math.Min(12, s.Width - column);
            int near = Math.Clamp(edge - x, 0, 64), far = Math.Clamp(edge - (x + width), 0, 64);
            int u0 = (s.U + column) & 255, u1 = (s.U + column + width) & 255, v1 = (s.V + s.Height) & 255;
            var corners = new Corner[]
            {
                new(x, y, u0, s.V, left.R * near >> 6, left.G * near >> 6, left.B * near >> 6),
                new(x + width, y, u1, s.V, right.R * far >> 6, right.G * far >> 6, right.B * far >> 6),
                new(x, y + s.Height, u0, v1, left.R * near >> 6, left.G * near >> 6, left.B * near >> 6),
                new(x + width, y + s.Height, u1, v1, right.R * far >> 6, right.G * far >> 6, right.B * far >> 6),
            };
            Order(0, () => Polygon(corners, s.Page | 0x20, s.Clut + 1, true, true));
        }
    }

    /// <summary>A flat quad of a sprite's rows <paramref name="row"/> on, <paramref name="rows"/> high, at a texture page and palette.</summary>
    private void Flat(int index, int x, int y, int row, int rows, int page, int clut, int shade)
    {
        var s = Sprite(index);
        int u1 = (s.U + s.Width) & 255, v0 = (s.V + row) & 255, v1 = (s.V + row + rows) & 255;
        var corners = new Corner[]
        {
            new(x, y, s.U, v0, shade, shade, shade), new(x + s.Width, y, u1, v0, shade, shade, shade),
            new(x, y + rows, s.U, v1, shade, shade, shade), new(x + s.Width, y + rows, u1, v1, shade, shade, shade),
        };
        Order(0, () => Polygon(corners, page, clut, false, true));
    }

    /// <summary>
    /// A turning stop's sprite a line at a time (0x80107698): the lines from 190 to 205 plain in the gold palette, the
    /// others 16 darker a line outside them, through its glow palette subtracted and then added.
    /// </summary>
    private void Lines(int index, int x, int y)
    {
        var s = Sprite(index);
        for (int row = 0; row < s.Height; row++, y++)
        {
            if (y >= 190 && y < 206) { Flat(index, x, y, row, 1, s.Page, ReelClut, 128); continue; }
            int shade = Math.Max(0, 128 - (y < 190 ? 189 - y : y - 206) * 16);
            Flat(index, x, y, row, 1, s.Page | 0x20, ReelClut + 1, shade);
            Flat(index, x, y, row, 1, s.Page | 0x40, ReelClut + 1, shade);
        }
    }

    /// <summary>The stopped stop's sprite (0x80105DD8): glowing in at 16 a frame for eight frames, then plain in the gold palette.</summary>
    private void Landed(int index, int x, int y, int t)
    {
        if (t <= 0) return;
        var s = Sprite(index);
        if (t < 8) Flat(index, x, y, 0, s.Height, s.Page | 0x20, ReelClut + 1, t * 16);
        else Flat(index, x, y, 0, s.Height, s.Page, ReelClut, 128);
    }

    /// <summary>The frame's parts in the order MENUF builds them (0x8010384C); in the closing fade all at 8 less the frames gone.</summary>
    private void Compose()
    {
        bool closing = _state == 3;
        int Time(int start) => closing ? 8 - _stateTimer : Math.Clamp(_timer - start, 0, 64);
        if (_ending)
        {
            // The ending's draw environment (cleared to black, page 32, no dithering), and the rank's picture
            // (0x8006C3CC): five 64-wide quads of its 15-bit picture at (64 i, 256), lines 8 to 232, at twice t, behind
            // the parts.
            BeginFrame(2, true, 32, false);
            int shade = Math.Clamp(Time(0), 0, 64) * 2;
            for (int i = 0; i < 5; i++)
            {
                var corners = new Corner[] { new(i * 64, 8, 0, 0, shade, shade, shade), new(i * 64 + 64, 8, 64, 0, shade, shade, shade), new(i * 64, 232, 0, 224, shade, shade, shade), new(i * 64 + 64, 232, 64, 224, shade, shade, shade) };
                int page = (i + 16) | 0x180;
                if (shade > 0) Order(1, () => Polygon(corners, page, 0, false, false));
            }
        }
        // The field's draw environment: nothing cleared, dithering on (BATTLE.PRG 0x80076208).
        else BeginFrame(1, false, 0x0A, true);

        // CONGRATULATIONS (0x80104B8C).
        int t = Time(0);
        if (t > 0)
        {
            int x = 160 - ((Width(Congratulations) + Width(Congratulations + 1)) >> 1);
            Pop(Congratulations, x, 40, CongratulationsShade, t);
            Pop(Congratulations + 1, x + Width(Congratulations), 40, CongratulationsShade + 1, t);
        }

        // The score (0x80104DBC; fading out, 0x80104C40).
        t = Time(32);
        if (t > 0)
        {
            int x = 160 - ((Width(Score) + Width(Colon) + 2 * Width(Comma) + Width(Points) + 116) >> 1), edge = 160 + 8 * t, pair = closing ? ScoreFade : ScoreWipe;
            void Part(int index, int px, int py) { if (closing) Fade(index, px, py, pair, t); else Wipe(index, px, py, pair, edge); }
            string digits = Math.Clamp(closing ? _score : _shownScore, 0, 999999999).ToString("D9");
            Part(Score, x, 80);
            x += Width(Score);
            Part(Colon, x, 87);
            x += 2 + Width(Colon);
            for (int i = 0; i < 9; i++)
            {
                Part(digits[i] - '0', x, 83);
                x += 12;
                if (i is 2 or 5) { Part(Comma, x, 94); x += 3 + Width(Comma); }
            }
            Part(Points, x, 88);
        }

        // The map's percentage (0x8010516C; fading out, 0x80105020).
        t = Time(112);
        if (t > 0)
        {
            int x = 160 - ((Width(Map) + Width(Colon) + Width(Percent) + 38) >> 1), edge = 160 + 8 * t, pair = closing ? MapFade : MapWipe;
            void Part(int index, int px, int py) { if (closing) Fade(index, px, py, pair, t); else Wipe(index, px, py, pair, edge); }
            string percent = Math.Clamp(closing ? _percent : _shownPercent, 0, 999).ToString("D3");
            Part(Map, x, 102);
            x += Width(Map);
            Part(Colon, x, 109);
            x += 2 + Width(Colon);
            for (int i = 0; i < 3; i++, x += 12) Part(percent[i] - '0', x, 105);
            Part(Percent, x, 110);
        }

        // The rank's title (0x801053B0) and words (0x8010540C).
        Fade(RankTitle, 160 - (Width(RankTitle) >> 1), 138, RankShade, Time(192));
        var ranks = _tables.GetValueOrDefault("ranks", new GArray()).AsGodotArray();
        t = Time(208);
        if (t > 0 && _rank < ranks.Count)
        {
            var words = ranks[_rank].AsGodotArray();
            int width = 0;
            foreach (var word in words) width += Width(word.AsInt32());
            int x = 160 - width / 2;
            foreach (var word in words)
            {
                Pop(word.AsInt32(), x, 154, WordsShade, t);
                x += Width(word.AsInt32());
            }
        }

        // BONUS and its reel (0x801060A8); the ending's has none.
        t = _ending ? 0 : Time(272);
        if (t > 0)
        {
            int x = 130 - ((Width(Bonus) + Width(Colon) + Width(14) + Width(Plus) + 14) >> 1);
            Fade(Bonus, x, 190, BonusShade, t);
            x += Width(Bonus);
            Fade(Colon, x, 190, BonusShade, t);
            x += 2 + Width(Colon);
            // The window: the draw area put back after the stops (built first, drawn last), and the reel's own (built last, drawn first).
            int open = _speed >> 3;
            Order(0, () => DrawArea(0, 0, FrameWidth, FrameHeight));
            if (_state < 2 && !closing)
            {
                int y = 198 + (_selection & 15);
                for (int k = 0; k < 3; k++, y -= 16)
                {
                    var stop = Stop((_selection >> 4) + k - 1);
                    int stat = stop[0].AsInt32(), at = x;
                    Lines(stat, at, y - 1);
                    at += Width(stat);
                    Lines(Plus, at, y + 4);
                    at += Width(Plus);
                    foreach (char digit in stop[1].AsInt32().ToString()) { Lines(digit - '0', at, y - 1); at += 12; }
                }
            }
            else
            {
                var stop = Stop(_selection >> 4);
                int stat = stop[0].AsInt32(), at = x;
                Landed(stat, at, 189, t);
                at += Width(stat);
                Landed(Plus, at, 194, t);
                at += Width(Plus);
                foreach (char digit in stop[1].AsInt32().ToString()) { Landed(digit - '0', at, 189, t); at += 12; }
            }
            Order(0, () => DrawArea(0, 190 - open, FrameWidth, open * 2 + 16));
        }
        EndFrame();
    }

    public override void _Draw()
    {
        // The prompt, built last and so under every other part: PRESS BUTTON in the HUD lettering, sliding in from the right, the Circle button bobbing beside it.
        if (_labels != null && _lettering != null && _slide > 0 && _state != 3)
        {
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
        base._Draw();
    }
}
