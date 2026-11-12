// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Linq;
using System.Collections.Generic;
using Godot;

namespace Wol.Games.Vagrant.Scenes.Field;

/// <summary>
/// The field HUD as retail draws it (BATTLE.PRG 0x800CA2EC) on the 320 by 240 screen, scaled to the window, from the
/// installed HUD pages.
/// <list type="bullet">
/// <item>HP, MP and RISK are blocks at (10, 18), (80, 18) and (10, 22) (0x800C9B38). Each has a dark track 66 by 5 and,
/// a pixel in, a bar 3 high and ⌈value × 64 / most⌉ long (0x800C99DC). The bar runs from its colour on the right,
/// (0x2d, 0xe0, 0xcf), (0xdc, 0x78, 0x9f) or (0x42, 0xcf, 0x34), toward (0xff, 0xf0, 0x9e) on the left, by its
/// length over 64.</item>
/// <item>HP and MP give their most in the small digits (6 by 10, digit d at (6d, 0)), right to left from (x + 56,
/// y − 8), 5 apart. A dot (5 by 10 at (32, 11)) follows two lower, then the value in the large digits (7 by 12 at
/// (64 + 8d, 0)), 6 apart, from 7 left of the dot (0x800C9950).</item>
/// <item>RISK gives its value in the large digits at (x + 54, y + 3). At none, the block is replaced by "RISK  ZERO"
/// lettered at (12, 22).</item>
/// <item>The labels HP, MP and RISK sit at (10, 10), (80, 10) and (10, 26).</item>
/// <item>The figure at (8, 184) is five pieces (right arm, left arm, head, body, legs; a second pose while the weapon
/// is drawn), each tinted by its limb's state (0x800C9E5C): dark red under 2 HP, orange under a quarter, yellow under
/// three quarters, green when hurt and teal when whole.</item>
/// <item>With the weapon drawn, BATTLE MODE (73 by 10) slides down to (236, 8) over four frames; its red pulses with
/// a sine over half a turn added to the half-grey colour (0x800CA388).</item>
/// </list>
/// </summary>
public sealed partial class BattleHud : RetailScreen
{
    private static readonly Vector2I[] Blocks = { new(10, 18), new(80, 18), new(10, 22) };
    private static readonly Vector2I[] Labels = { new(10, 10), new(80, 10), new(10, 26) };
    private static readonly int[] LabelWidths = { 16, 18, 26 }, LabelColumns = { 0x90, 0xA0, 0xB4 };
    private static readonly Color[] BarColours = { Color.Color8(0x2D, 0xE0, 0xCF), Color.Color8(0xDC, 0x78, 0x9F), Color.Color8(0x42, 0xCF, 0x34) };
    private static readonly Color Track = Color.Color8(0x00, 0x28, 0x40), Light = Color.Color8(0xFF, 0xF0, 0x9E);

    // The figure's pieces (0x800EBCEC, 0x800EBD14, 0x800EBD3C): standing, then with the weapon drawn.
    private static readonly Vector2I[] PieceAt = { new(8, 8), new(24, 8), new(16, 0), new(16, 8), new(8, 16), new(0, 8), new(24, 8), new(16, 0), new(16, 8), new(8, 16) };
    private static readonly Vector2I[] PieceSize = { new(12, 20), new(12, 18), new(12, 12), new(12, 14), new(28, 24), new(18, 20), new(16, 22), new(12, 12), new(12, 14), new(28, 24) };
    private static readonly Vector2I[] PieceSource = { new(0x10, 0xB8), new(0x20, 0xA0), new(0x00, 0xA0), new(0x00, 0xAC), new(0x2C, 0xA0), new(0x0C, 0xA0), new(0x20, 0xB8), new(0x00, 0xA0), new(0x00, 0xAC), new(0x2C, 0xA0) };
    private static readonly Color[] LimbColours = { Color.Color8(0x58, 0x00, 0x18), Color.Color8(0x70, 0x40, 0x00), Color.Color8(0x78, 0x70, 0x08), Color.Color8(0x20, 0x68, 0x18), Color.Color8(0x18, 0x70, 0x68) };

    private Texture2D? _digits, _labels, _banner;
    private Runtime.HudLettering? _lettering;
    public int Hp, MaxHp, Mp, MaxMp, Risk;
    private bool _drawn;
    private readonly int[] _limbs = { 4, 4, 4, 4, 4 };
    private int _slide, _pulse, _ticks;

    public void Configure(Texture2D? digits, Texture2D? labels, Texture2D? banner, Texture2D? emotes, Runtime.HudLettering? lettering, Texture2D? pointer = null)
    {
        _emotes = emotes;
        _pointer = pointer;
        _digits = digits;
        _labels = labels;
        _banner = banner;
        _lettering = lettering;
    }

    /// <summary>The values shown; <paramref name="limbs"/> are Ashley's limbs' HP and most, in retail order.</summary>
    public void Set(int hp, int maxHp, int mp, int maxMp, int risk, bool drawn, IReadOnlyList<(int Hp, int MaxHp)> limbs)
    {
        bool changed = hp != Hp || maxHp != MaxHp || mp != Mp || maxMp != MaxMp || risk != Risk || drawn != _drawn;
        (Hp, MaxHp, Mp, MaxMp, Risk, _drawn) = (hp, maxHp, mp, maxMp, risk, drawn);
        for (int i = 0; i < _limbs.Length && i < limbs.Count; i++)
        {
            int state = LimbState(limbs[i].Hp, limbs[i].MaxHp);
            changed |= state != _limbs[i];
            _limbs[i] = state;
        }
        if (changed) QueueRedraw();
    }

    /// <summary>While the field menu is open its title stands where BATTLE MODE would, which is not drawn.</summary>
    public bool Covered { get => _covered; set { _covered = value; QueueRedraw(); } }
    private bool _covered;

    /// <summary>A window over the left of the screen (the gadget bag) hides the figure.</summary>
    public bool FigureHidden { get => _figureHidden; set { _figureHidden = value; QueueRedraw(); } }
    /// <summary>BATTLE MODE put away while a titled menu page (MENU at the top right) stands over the field.</summary>
    public bool BannerHidden { get => _bannerHidden; set { _bannerHidden = value; QueueRedraw(); } }
    private bool _bannerHidden;
    private bool _figureHidden;

    /// <summary>
    /// While a spell plays out (from its wind-up until its effect has run) the blocks, the figure and BATTLE MODE are
    /// put away; the damage numbers, balloons, cues and timing labels stay (footage: Ashley's Heal, the Dullahan's
    /// Drain Heart).
    /// </summary>
    public bool Acting { get => _acting; set { if (_acting == value) return; _acting = value; QueueRedraw(); } }
    private bool _acting;

    private List<string>? _parts;
    private int _chosen;

    /// <summary>
    /// The attack sphere's target parts, listed down the right of the screen with <paramref name="chosen"/> pointed
    /// at (null when the sphere closes). The figure is not drawn meanwhile.
    /// </summary>
    /// <summary>
    /// The attack sphere's part lists: <paramref name="parts"/> down the right and <paramref name="left"/> down the left
    /// (an enemy on each side of Ashley), the lit part of each (-1 on the list not chosen).
    /// </summary>
    public void Targets(List<string>? parts, int chosen, List<string>? left = null, int leftChosen = -1)
    {
        // A list that newly opens slides in.
        if (_parts == null && _leftParts == null) _slideFrames = 0;
        _parts = parts;
        _chosen = chosen;
        _leftParts = left;
        _leftChosen = leftChosen;
        QueueRedraw();
    }

    private Vector3? _aimedAt;
    private Func<Vector3, Vector2?>? _aimProject;

    /// <summary>
    /// The chosen part's (or whole target's) place in the field, which the chosen bar's grey line runs to: across to
    /// x 226 (94 on the left) and then straight to it on the screen (footage: ABDOMEN's line to the Dullahan's
    /// middle; the Elixir's TARGET line to Ashley's chest). Null for none.
    /// </summary>
    public void AimedAt(Vector3? point, Func<Vector3, Vector2?>? project)
    {
        _aimedAt = point;
        _aimProject = project;
        QueueRedraw();
    }

    private Texture2D? _emotes, _pointer;
    // The edge pointer's eight points about its place (BATTLE.PRG 0x8009651C), from the top left round to the left.
    private static readonly Vector2[] PointerPoints = { new(-8, -8), new(0, -11), new(7, -8), new(10, 0), new(7, 7), new(0, 10), new(-8, 7), new(-11, 0) };
    private List<(Vector3 Head, int Kind, int Side)> _balloons = new();
    private Func<Vector3, Vector2?>? _project;

    /// <summary>The chain timing's "!" sprite (SYSTEM.DAT section 1's page, 22 by 32 at (0xC0, 0)), when installed.</summary>
    public Texture2D? ChainCue { get; set; }
    private Vector3? _cueAt;
    private int _cueFrames;
    private Func<Vector3, Vector2?>? _cueProject;
    private (int Grade, int Word, int Age)? _timing;
    private (int Count, int Age)? _chains;

    /// <summary>Shows the red "!" over Ashley's head (a point in the field) for <paramref name="frames"/> frames (BATTLE.PRG 0x80096FF0).</summary>
    public void Cue(Vector3 head, int frames, Func<Vector3, Vector2?> project) { _cueAt = head; _cueFrames += frames; _cueProject = project; QueueRedraw(); }

    /// <summary>A press's grade (0 too fast, 1 fast, 2 in time, 3 slow, 4 too slow) and, in time, which word praises it.</summary>
    public void Timing(int grade, int word) { _timing = (grade, word, 0); QueueRedraw(); }

    /// <summary>The chain's count after a link.</summary>
    public void Chains(int count) { _chains = (count, 0); QueueRedraw(); }

    /// <summary>Ages the timing labels and the cue by a frame: a grade lasts 46 frames, a count 91 (0x800941FC).</summary>
    public void StepLabels()
    {
        if (_timing is { } t) _timing = t.Age + 1 >= 46 ? null : (t.Grade, t.Word, t.Age + 1);
        if (_chains is { } c) _chains = c.Age + 1 >= 91 ? null : (c.Count, c.Age + 1);
        if (_cueFrames > 0 && --_cueFrames == 0) _cueAt = null;
        QueueRedraw();
    }

    private static readonly string[] Grades = { "TOO   FAST!", "FAST!", "", "SLOW!", "TOO   SLOW!" };
    private static readonly string[] Praise = { "COOL!", "GOOD!", "EXCELLENT!", "RIGHT   ON!", "PERFECT!", "WELL   TIMED!", "NICE!", "GREAT!" };
    private static readonly Color[] GradeColours = { new(0, 1, 1), new(0.25f, 1, 0.75f), new(0.5f, 1, 0.5f), new(0.75f, 1, 0.25f), new(1, 1, 0) };
    private static readonly int[] SlideIn = { 64, 16, 4, 1 };

    /// <summary>
    /// The timing labels (BATTLE.PRG 0x800958A4, 0x80095A4C) in the HUD's lettering, centred on x 44 (their "$"): the grade at
    /// y 48 in its colour (the praise in green), the count at y 64 in (160, 64, 16), each sliding in from the left over
    /// its first 4 frames and out over its last (a count of 1 slides in, later ones change in place).
    /// </summary>
    private void DrawTimingLabels()
    {
        if (_labels == null) return;
        DrawSetTransform(FieldView.Pin(Size, 0).Offset);
        float Slide(int age, int end, bool enter) => age < 4 && enter ? SlideIn[age] : age > end - 4 ? SlideIn[Math.Clamp(end - age, 0, 3)] : 0;
        if (_timing is { } t)
        {
            string text = t.Grade == 2 ? Praise[t.Word] : Grades[t.Grade];
            float x = 44 - Slide(t.Age, 45, true) - _lettering!.Width(text) / 2;
            _lettering!.Draw(this, text, new Vector2(x, 48), Unit, GradeColours[t.Grade]);
        }
        if (_chains is { } c)
        {
            string text = c.Count == 1 ? "1 CHAIN" : $"{c.Count} CHAINS";
            float x = 44 - Slide(c.Age, 90, c.Count == 1) - _lettering!.Width(text) / 2;
            _lettering!.Draw(this, text, new Vector2(x, 64), Unit, new Color(1.25f, 0.5f, 0.125f));
        }
        DrawSetTransform(Vector2.Zero);
    }


    /// <summary>
    /// The red "!" over Ashley (BATTLE.PRG 0x800970BC): its foot at her head's place on the screen, tinted (240, 64, 32);
    /// a head above y 40 is brought down to it and moved across as far (toward the middle near the right edge).
    /// </summary>
    private void DrawCue()
    {
        if (ChainCue == null || _cueAt is not { } head || _cueProject?.Invoke(head) is not { } point) return;
        var viewport = GetViewportRect().Size;
        var scale = FieldView.Unit(viewport);
        var origin = (viewport - new Vector2(320, FieldView.VisibleLines) * scale) / 2;
        var at = ((point - origin) / scale).Round();
        if (at.Y < 40) { float d = 40 - at.Y; at.Y = 40 + d; at.X += at.X + d < 288 ? d : -d; }
        DrawSetTransform(origin, 0, scale);
        DrawTextureRect(ChainCue, new Rect2(at.X - 11, at.Y - 32, 22, 32), false, new Color(240 / 128f, 64 / 128f, 32 / 128f));
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>The damage numbers to draw: each one's point over its target in the field, its value and its age in frames.</summary>
    public void Numbers(List<(Vector3 At, int Value, int Age)> numbers, Func<Vector3, Vector2?> project)
    {
        _numbers = numbers;
        _numberProject = project;
        QueueRedraw();
    }
    private List<(Vector3 At, int Value, int Age)> _numbers = new();
    private Func<Vector3, Vector2?>? _numberProject;

    /// <summary>
    /// The damage numbers in the large digits (6 pixels apart), centred on their point: each digit drops 6 pixels
    /// into place over 6 frames, the left one first and each next 2 frames after (footage).
    /// </summary>
    private void DrawNumbers()
    {
        if (_digits == null || _numberProject == null || _numbers.Count == 0) return;
        var viewport = GetViewportRect().Size;
        var scale = FieldView.Unit(viewport);
        var origin = (viewport - new Vector2(320, FieldView.VisibleLines) * scale) / 2;
        DrawSetTransform(origin, 0, scale);
        foreach (var (at, value, age) in _numbers)
        {
            if (_numberProject(at) is not { } point) continue;
            var centre = ((point - origin) / scale).Round();
            string text = value.ToString();
            float left = centre.X - (text.Length * 6 + 1) / 2;
            for (int i = 0; i < text.Length; i++)
            {
                int digit = text[i] - '0';
                float drop = Math.Clamp(6 - (age - i * 2), 0, 6);
                DrawTextureRectRegion(_digits, new Rect2(new Vector2(left + i * 6, centre.Y - 6 - drop), new Vector2(7, 12)), new Rect2(64 + 8 * digit, 0, 7, 12));
            }
        }
        DrawSetTransform(Vector2.Zero);
    }

    /// <summary>The enemies' balloons to draw: each head's point in the field, the kind (1 to 3 dots, 4 jagged) and the side.</summary>
    public void Balloons(List<(Vector3 Head, int Kind, int Side)> balloons, Func<Vector3, Vector2?> project)
    {
        _balloons = balloons;
        _project = project;
        QueueRedraw();
    }

    // BATTLE.PRG's balloon tables (0x80068F04, 0x80068F64, 0x80068F94): the corners about the head's point and the
    // page's texels for the rounded balloon (right, left) and the jagged one (right, left), and where the dots or mark
    // stand. The page is SYSTEM.DAT's second, through the palette at (992, 220).
    private static readonly Rect2[] BalloonCorners = { new(-6, -22, 24, 23), new(-17, -22, 24, 23), new(-10, -29, 30, 30), new(-19, -29, 28, 30) };
    private static readonly Rect2[] BalloonTexels = { new(64, 16, 24, 23), new(64, 16, 24, 23), new(121, 17, 30, 30), new(121, 17, 30, 30) };
    private static readonly Vector2[] MarkOffsets = { new(-2, -16), new(-14, -16), new(0, -21), new(-7, -21) };

    private void DrawBalloons()
    {
        if (_emotes == null || _project == null || _balloons.Count == 0) return;
        var viewport = GetViewportRect().Size;
        var scale = FieldView.Unit(viewport);
        var origin = (viewport - new Vector2(320, FieldView.VisibleLines) * scale) / 2;
        DrawSetTransform(origin, 0, scale);
        foreach (var (head, kind, side) in _balloons)
        {
            if (_project(head) is not { } at) continue;
            var point = ((at - origin) / scale).Round();
            // A telegraph whose enemy is off the screen shows as a pointer at the screen's edge, turned its way.
            if (kind == 4)
            {
                int flags = 0;
                if (point.X < 16) { point.X = 16; flags = 1; }
                if (point.X > 304) { point.X = 304; flags |= 2; }
                if (flags != 0) { if (point.Y < 40) { point.Y = 40; flags |= 4; } }
                else if (point.Y < 8) { point.Y = 40; flags = 4; }
                if (point.Y > 208) { point.Y = 208; flags |= 8; }
                if (flags != 0)
                {
                    int index = flags switch { 4 => 0, 6 => 1, 2 => 2, 10 => 3, 8 => 4, 9 => 5, 1 => 6, 5 => 7, _ => -1 };
                    if (index >= 0 && _pointer != null)
                    {
                        var size = _pointer.GetSize();
                        Vector2 Uv(float u, float v) => new(u / size.X, v / size.Y);
                        DrawPrimitive(new[] { point + PointerPoints[index], point + PointerPoints[(index + 2) & 7], point + PointerPoints[(index + 4) & 7], point + PointerPoints[(index + 6) & 7] },
                            new[] { Colors.White, Colors.White, Colors.White, Colors.White }, new[] { Uv(48, 48), Uv(64, 48), Uv(64, 64), Uv(48, 64) }, _pointer);
                    }
                    continue;
                }
            }
            int shape = kind == 4 ? 2 + side : side;
            var corners = BalloonCorners[shape];
            var texels = BalloonTexels[shape];
            // The left-hand balloons are the right-hand ones mirrored.
            var target = new Rect2(point + corners.Position, corners.Size);
            if (side == 1) target = new Rect2(target.Position + new Vector2(target.Size.X, 0), new Vector2(-target.Size.X, target.Size.Y));
            DrawTextureRectRegion(_emotes, target, texels);
            if (kind <= 3)
                for (int k = 0; k < kind; k++) DrawTextureRectRegion(_emotes, new Rect2(point + MarkOffsets[side] + new Vector2(k * 6, 0), new Vector2(5, 5)), new Rect2(90, 18, 5, 5));
            else DrawTextureRectRegion(_emotes, new Rect2(point + MarkOffsets[shape], new Vector2(7, 12)), new Rect2(152, 17, 7, 12));
        }
        DrawSetTransform(Vector2.Zero);
    }

    private List<string>? _leftParts;
    private int _leftChosen = -1;
    // Frames (30 Hz) since the part lists opened. Each row slides in from its side by BATTLE.PRG's row steps
    // (0x800EBBC8: 0, 1, 2, 4, 8, 16, 32, 56, 80, ...), from step 7, a frame after the row above, two steps a frame
    // (measured from footage).
    private int _slideFrames;
    private static readonly int[] RowSteps = { 0, 1, 2, 4, 8, 16, 32, 56, 80, 104, 128, 152, 176, 200, 224, 248 };
    private static float RowSlide(int row, int frames) => RowSteps[Math.Clamp(7 + 2 * row - 2 * frames, 0, 15)];

    // As retail's target mode shows them: a row every 11 lines from y 100, 8 high, its bar from x 248 to the edge, dark
    // blue on the left to blue on the right (the chosen one bright on the left, darkening), the name lettered from x 256
    // and a grey line from x 226 to the chosen bar.
    private static readonly Color BarDark = Color.Color8(0x00, 0x04, 0x2C), BarLight = Color.Color8(0x00, 0x3C, 0x66);
    private static readonly Color ChosenLight = Color.Color8(0x18, 0x54, 0x7C), ChosenDark = Color.Color8(0x04, 0x0A, 0x30), Pointer = Color.Color8(0x78, 0x74, 0x7E);

    private void Parts()
    {
        var unit = Unit;
        // The list stands centred on line 122, a part every 11 lines (retail footage: five parts from 92, three
        // from 107).
        // Both lists start on the row the longer one's centring gives (footage: a lone arm on the left level with the
        // right list's first part).
        float first = MathF.Round(122 - 5.5f * Math.Max(_parts?.Count ?? 0, _leftParts?.Count ?? 0));
        for (int i = 0; _parts != null && i < _parts.Count; i++)
        {
            float top = first + 11 * i;
            bool chosen = i == _chosen;
            float dx = RowSlide(i, _slideFrames);
            var (left, right) = chosen ? (ChosenLight, ChosenDark) : (BarDark, BarLight);
            var corners = new[] { new Vector2(248 + dx, top) * unit, new Vector2(320 + dx, top) * unit, new Vector2(320 + dx, top + 8) * unit, new Vector2(248 + dx, top + 8) * unit };
            DrawPolygon(corners, new[] { left, right, right, left });
            if (chosen && dx == 0)
            {
                DrawLine(new Vector2(226, top + 4) * unit, new Vector2(248, top + 4) * unit, Pointer, Math.Max(1, unit.Y));
                if (_aimedAt is { } aim && _aimProject?.Invoke(aim) is { } point)
                    DrawLine(new Vector2(226, top + 4) * unit, point - FieldView.Pin(Size, 2).Offset, Pointer, Math.Max(1, unit.Y));
            }
            Letter(_parts[i], new Vector2(256 + dx, top - 1));
        }
        if (_leftParts == null || _labels == null) return;
        // The left list mirrors the right: its bar from the screen's edge to x 72, light at the edge, the name ending at
        // x 64, the grey line from the bar to x 94.
        DrawSetTransform(FieldView.Pin(Size, 0).Offset);
        for (int i = 0; i < _leftParts.Count; i++)
        {
            float top = first + 11 * i;
            bool chosen = i == _leftChosen;
            float dx = -RowSlide(i, _slideFrames);
            var (outer, inner) = chosen ? (ChosenLight, ChosenDark) : (BarLight, BarDark);
            var corners = new[] { new Vector2(0 + dx, top) * unit, new Vector2(72 + dx, top) * unit, new Vector2(72 + dx, top + 8) * unit, new Vector2(0 + dx, top + 8) * unit };
            DrawPolygon(corners, new[] { outer, inner, inner, outer });
            if (chosen && dx == 0)
            {
                DrawLine(new Vector2(72, top + 4) * unit, new Vector2(94, top + 4) * unit, Pointer, Math.Max(1, unit.Y));
                if (_aimedAt is { } aim && _aimProject?.Invoke(aim) is { } point)
                    DrawLine(new Vector2(94, top + 4) * unit, point - FieldView.Pin(Size, 0).Offset, Pointer, Math.Max(1, unit.Y));
            }
            float width = _lettering!.Width(_leftParts[i]);
            _lettering!.Draw(this, _leftParts[i], new Vector2(64 - width + dx, top - 1), unit);
        }
    }

    /// <summary>
    /// The shortcut menu L2 holds open (the game's manual, "Shortcut Commands"; Episode 4 at 4:36, measured from the
    /// footage on the 320 by 240 screen): the direction pad (the label page's (184, 160), 31 by 30) at (72, 110) with
    /// its four commands, and the four face buttons (16 by 16 from (0, 128): triangle, square, circle and cross, each
    /// tinted its own colour) round (233, 125) with the classes of magic. A command up or right of its button is
    /// lettered from five pixels past it, one left or below it ends three before it, with a dot between; one that
    /// cannot be used now is grey, and a class Ashley has no spell of is not named. Each cluster slides in from its
    /// side by the row steps, one a frame.
    /// </summary>
    private string[]? _shortcuts;
    private bool[] _shortcutUsable = Array.Empty<bool>();
    private int _shortcutFrames;
    private static readonly Vector2[] ShortcutAt = { new(96, 109), new(70, 122), new(105, 122), new(80, 136), new(245, 109), new(213, 122), new(256, 122), new(222, 136) };
    private static readonly Vector2[] ShortcutButtons = { new(225, 108), new(216, 117), new(235, 117), new(225, 127) };
    private static readonly Color ShortcutGrey = Color.Color8(0x80, 0x80, 0x80);

    /// <summary>
    /// Shows the shortcut menu: its eight commands' names (up, left, right and down, then triangle, square, circle and
    /// cross; empty for none), which of them can be used now, and the frames (30 Hz) it has been open. Null puts it away.
    /// </summary>
    public void Shortcuts(string[]? names, bool[] usable, int frames)
    {
        if (names == null && _shortcuts == null) return;
        (_shortcuts, _shortcutUsable, _shortcutFrames) = (names, usable, frames);
        QueueRedraw();
    }

    private void DrawShortcuts()
    {
        if (_shortcuts == null || _labels == null) return;
        var unit = Unit;
        float slide = RowSteps[Math.Clamp(11 - _shortcutFrames, 0, 15)];
        DrawSetTransform(FieldView.Pin(Size, 1).Offset);
        FieldView.Sprite(this, _labels, new Rect2(184, 160, 31, 30), new Vector2(72 - slide, 110), Unit);
        for (int b = 0; b < 4; b++) FieldView.Sprite(this, _labels, new Rect2(16 * b, 128, 16, 16), ShortcutButtons[b] + new Vector2(slide, 0), Unit, MenuList.ButtonTints[b]);
        for (int i = 0; i < 8 && i < _shortcuts.Length; i++)
        {
            string name = _shortcuts[i];
            if (name.Length == 0) continue;
            float dx = i < 4 ? -slide : slide;
            var tint = i < _shortcutUsable.Length && _shortcutUsable[i] ? Colors.White : ShortcutGrey;
            var at = ShortcutAt[i] + new Vector2(dx, 0);
            // Up and right start at their place; left and down end at theirs.
            if (i % 4 is 0 or 2)
            {
                DrawRect(new Rect2(new Vector2(at.X - 4, at.Y + 4) * unit, unit * 1.5f), tint);
                _lettering!.Draw(this, name, at, unit, tint);
            }
            else
            {
                float width = _lettering!.Width(name);
                _lettering!.Draw(this, name, new Vector2(at.X - width, at.Y), unit, tint);
                DrawRect(new Rect2(new Vector2(at.X + 2, at.Y + 4) * unit, unit * 1.5f), tint);
            }
        }
    }

    /// <summary>The orange HUD page (hud_cursor), whose row at y 16 holds the status icons.</summary>
    public Texture2D? StatusPage;
    private int[] _statuses = Array.Empty<int>();

    // A lowered or raised stat's icon on the orange page's row at y 16, 16 by 16: STR, INT and AGL, each lowered then
    // raised. Episode 4 at 9:46 shows Degenerate's (16, 16); the others are taken in the page's order.
    private static readonly Dictionary<int, int> StatusColumns = new()
    {
        [FieldBattle.Degenerate] = 16, [FieldBattle.Herakles] = 32, [FieldBattle.Psychodrain] = 48,
        [FieldBattle.Enlighten] = 64, [FieldBattle.Leadbones] = 80, [FieldBattle.Invigorate] = 96,
    };

    /// <summary>Ashley's statuses, by effect id, shown as icons right of the MP block.</summary>
    public void ShowStatuses(IEnumerable<int> statuses)
    {
        var shown = new List<int>();
        foreach (int status in statuses) if (StatusColumns.ContainsKey(status)) shown.Add(status);
        shown.Sort();
        if (shown.SequenceEqual(_statuses)) return;
        _statuses = shown.ToArray();
        QueueRedraw();
    }

    /// <summary>
    /// The status icons: the first at (169, 8), measured from Episode 4 at 9:46; any further ones 16 to the right of
    /// it (not seen in the footage).
    /// </summary>
    private void DrawStatuses()
    {
        if (StatusPage == null) return;
        for (int i = 0; i < _statuses.Length; i++)
            FieldView.Sprite(this, StatusPage, new Rect2(StatusColumns[_statuses[i]], 16, 16, 16), new Vector2(169 + 16 * i, 8), Unit);
    }

    private static int LimbState(int hp, int most) => hp < 2 ? 0 : 4 * hp < most ? 1 : 4 * hp < 3 * most ? 2 : hp < most ? 3 : 4;

    /// <summary>BATTLE MODE moves on the 30 Hz frame: it slides in a step a frame (its pulse held) and out again.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if ((++_ticks & 1) != 0) return;
        if ((_parts != null || _leftParts != null) && _slideFrames < 16) { _slideFrames++; QueueRedraw(); }
        int slide = _slide;
        if (_drawn && _slide < 4) { _slide++; _pulse = 0; }
        else if (!_drawn && _slide > 0) _slide--;
        else if (_slide > 0) _pulse += 128;
        if (_slide > 0 || slide != _slide) QueueRedraw();
    }

    private Vector2 Unit => FieldView.Pin(Size, 0).Unit;


    // Retail adds the banner, the figure, then HP, MP and RISK, each block's bar before its numbers and label. A
    // primitive added to an ordering-table slot goes before those already there, so they are drawn in reverse.
    /// <summary>
    /// The puzzle timer, when it shows: minutes, seconds and hundredths, printed at (128, 200) in the HUD's letters
    /// (BATTLE.PRG's timer routine prints "00:00:00" there with its raw text routine; an hour or more reads 59:59:99).
    /// </summary>
    public (int Minutes, int Seconds, int Hundredths)? Timer
    {
        get => _timer;
        set { if (_timer != value) { _timer = value; QueueRedraw(); } }
    }
    private (int Minutes, int Seconds, int Hundredths)? _timer;

    public override void _Draw()
    {
        DrawBalloons();
        DrawNumbers();
        DrawCue();
        DrawTimingLabels();
        if (_timer is { } time && _labels != null)
        {
            DrawSetTransform(FieldView.Pin(Size, 1).Offset);
            Letter($"{time.Minutes:d2}:{time.Seconds:d2}:{time.Hundredths:d2}", new Vector2(128, 200));
        }
        // Nothing else is shown until Ashley's values arrive, nor while a spell plays out.
        if (_digits == null || _labels == null || MaxHp == 0 || Acting) return;
        // The blocks and the figure keep to the window's left edge, BATTLE MODE and the part list to its right.
        DrawSetTransform(FieldView.Pin(Size, 0).Offset);
        if (Risk != 0) Block(2, Risk, 100);
        else Letter("RISK  ZERO", new Vector2(12, 22));
        Block(1, Mp, MaxMp);
        Block(0, Hp, MaxHp);
        DrawStatuses();
        DrawSetTransform(FieldView.Pin(Size, 2).Offset);
        if (_parts != null || _leftParts != null) Parts();
        DrawShortcuts();
        DrawSetTransform(FieldView.Pin(Size, 0).Offset);
        // The figure, tinted (the colour doubled: 0x80 leaves a texel as it is); not while targeting.
        for (int i = 4; i >= 0 && _parts == null && _leftParts == null && !FigureHidden; i--)
        {
            int piece = i + (_drawn ? 5 : 0);
            var tint = LimbColours[_limbs[i]];
            FieldView.Sprite(this, _digits, new Rect2(PieceSource[piece], PieceSize[piece]), new Vector2(8, 184) + PieceAt[piece], Unit, new Color(tint.R * 2, tint.G * 2, tint.B * 2));
        }
        if (_slide > 0 && _banner != null && !Covered && !BannerHidden)
        {
            // The pulse, a sine over half a turn (never below zero), raises the red of the half-grey colour.
            int word = 0x404040 + (int)(Mathf.Sin((_pulse & 2047) * Mathf.Tau / 4096) * 4096) / 32;
            float r = (word & 255) / 128f, g = ((word >> 8) & 255) / 128f, b = ((word >> 16) & 255) / 128f;
            DrawSetTransform(FieldView.Pin(Size, 2).Offset);
            FieldView.Sprite(this, _banner, new Rect2(0, 0, 73, 10), new Vector2(236, _slide * 3 - 4), Unit, new Color(r, g, b));
        }
        DrawSetTransform(Vector2.Zero);
    }

    private void Block(int i, int value, int most) => Block(this, _labels!, _digits!, i, value, most, Unit);

    /// <summary>
    /// One of the HUD's blocks on <paramref name="canvas"/>: HP (0), MP (1) or RISK (2), its label, reading and bar in
    /// their places on the 320 by 240 screen. The STATUS screen draws its blocks the same way (MENU4.PRG); for a
    /// character not analysed (<paramref name="known"/> false) its readings are question marks (the small ones, 6 by 9
    /// at (228, 0), 5 apart, the slash, then the large, 7 by 10 at (234, 0), 6 apart, a line higher) and its bars full
    /// (MENU4 0x801035C8).
    /// </summary>
    public static void Block(CanvasItem canvas, Texture2D labels, Texture2D digits, int i, int value, int most, Vector2 unit, bool known = true)
    {
        canvas.DrawTextureRectRegion(labels, new Rect2((Vector2)Labels[i] * unit, new Vector2(LabelWidths[i], 9) * unit), new Rect2(LabelColumns[i], 0, LabelWidths[i], 9));
        if (!known)
        {
            var at = i == 2 ? new Vector2(Blocks[i].X + 54, Blocks[i].Y + 3) : new Vector2(Blocks[i].X + 56, Blocks[i].Y - 8);
            if (i != 2)
            {
                for (int k = 0; k < 3; k++) { canvas.DrawTextureRectRegion(digits, new Rect2(at * unit, new Vector2(6, 9) * unit), new Rect2(228, 0, 6, 9)); at.X -= 5; }
                canvas.DrawTextureRectRegion(digits, new Rect2((at + new Vector2(0, 2)) * unit, new Vector2(5, 10) * unit), new Rect2(32, 11, 5, 10));
                at.X -= 7;
            }
            at.Y -= 1;
            for (int k = 0; k < 3; k++) { canvas.DrawTextureRectRegion(digits, new Rect2(at * unit, new Vector2(7, 10) * unit), new Rect2(234, 0, 7, 10)); at.X -= 6; }
            Bar(canvas, Blocks[i], 1, 1, BarColours[i], unit);
            return;
        }
        if (i == 2)
        {
            // RISK's value in the large digits, right to left from (x + 54, y + 3).
            var at = new Vector2(Blocks[i].X + 54, Blocks[i].Y + 3);
            int number = Math.Max(0, value);
            do
            {
                canvas.DrawTextureRectRegion(digits, new Rect2(at * unit, new Vector2(7, 12) * unit), new Rect2(64 + 8 * (number % 10), 0, 7, 12));
                at.X -= 6;
                number /= 10;
            }
            while (number != 0);
            Bar(canvas, Blocks[i], value, most, BarColours[i], unit);
        }
        else Reading(canvas, digits, Blocks[i], value, most, BarColours[i], unit);
    }

    /// <summary>
    /// A reading as HP and MP are shown, its block's track at <paramref name="at"/> on the 320 by 240 screen: the most
    /// in the small digits right to left from (x + 56, y − 8), the dot, the value in the large digits, then the track
    /// and its bar in <paramref name="colour"/>. The equip lists show DP and PP this way.
    /// </summary>
    public static void Reading(CanvasItem canvas, Texture2D digits, Vector2I at, int value, int most, Color colour, Vector2 unit)
    {
        void Number(int kind, Vector2 to, int number)
        {
            number = Math.Max(0, number);
            do
            {
                int digit = number % 10;
                FieldView.Sprite(canvas, digits, kind == 0 ? new Rect2(6 * digit, 0, 6, 10) : new Rect2(64 + 8 * digit, 0, 7, 12), to, unit);
                to.X -= 5 + kind;
                number /= 10;
            }
            while (number != 0);
        }
        // The most is placed first, but drawn last of the three.
        float x = at.X + 56 - 5 * Math.Max(1, Math.Max(0, most).ToString().Length);
        Number(1, new Vector2(x - 7, at.Y - 10), value);
        FieldView.Sprite(canvas, digits, new Rect2(32, 11, 5, 10), new Vector2(x, at.Y - 8 + 2), unit);
        Number(0, new Vector2(at.X + 56, at.Y - 8), most);
        Bar(canvas, at, value, most, colour, unit);
    }

    /// <summary>A block's dark track, 66 by 5, and a pixel in its bar, 3 high and ⌈value × 64 / most⌉ long, lightening to the left.</summary>
    private static void Bar(CanvasItem canvas, Vector2I at, int value, int most, Color colour, Vector2 unit)
    {
        canvas.DrawRect(new Rect2(at * unit, new Vector2(66, 5) * unit), Track);
        if (most <= 0 || value <= 0) return;
        int length = Math.Min(64, (value * 64 + most - 1) / most);
        var left = colour.Lerp(Light, length / 64f);
        var start = new Vector2(at.X + 1, at.Y + 1);
        var corners = new[] { start * unit, (start + new Vector2(length, 0)) * unit, (start + new Vector2(length, 3)) * unit, (start + new Vector2(0, 3)) * unit };
        canvas.DrawPolygon(corners, new[] { left, colour, colour, left });
    }

    /// <summary>The HP and MP bars' colours, for readings shown the same way.</summary>
    public static Color HpColour => BarColours[0];
    public static Color MpColour => BarColours[1];

    /// <summary>A line of the HUD's lettering (0x800C6540), from its left edge.</summary>
    private void Letter(string text, Vector2 at) => _lettering!.Draw(this, text, at, Unit);

    /// <summary>
    /// A line of the HUD's lettering on <paramref name="canvas"/>, from its left edge at <paramref name="at"/> on the
    /// 320 by 240 screen (<paramref name="unit"/> window pixels to a screen pixel): each character's cell from the label
    /// page by its width and column, capitals closed up by their kerning pairs. Returns where the next would go.
    /// </summary>
}
