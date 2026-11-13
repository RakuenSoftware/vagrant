// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Numerics;
using Godot;
using Wol.Engine;
using Wol.Games.Vagrant.Installation;
using Wol.Games.Vagrant.Runtime;
using Wol.Games.Vagrant.Scenes.Field;

namespace Wol.Games.Vagrant.Scenes.Ending;

/// <summary>
/// The ending: ENDING/ENDING.PRG run as retail runs it, loaded over BATTLE.PRG at 0x80068800. Its frame is one vertical
/// blank (60 a second, NTSC), the 320 by 224 picture shown eight lines down the 240-line screen. Each frame the
/// credits script steps (0x8006ABF0), then the 32 tasks (0x8006AAA0, slot 0 the fades, the rest taken first free from
/// slot 1), then Start (once) starts the skip; the ordering table is then drawn (<see cref="RetailScreen"/>).
/// </summary>
/// <remarks>
/// The script (at 0x8006E3FC, file offset 0x5BFC) is a run of commands: 1 n waits 2n frames; 2 starts a line of text,
/// its length byte and characters following; 3 starts the illustrations; 4 k starts piece k (0 the SQUARE logo, 1
/// the title, 2 the closing mark, 3 the rippling title); 5 fades everything in and 6 out over 128 frames (the fades'
/// slot 0); 7 stops the roll; 8 i a b sets illustration i's hold to a + 256 b - 160 frames; 255 ends.
/// <list type="bullet">
/// <item>The parchment (0x8006A0D8, the first task): a 4-bit picture at (0, 10), 320 by 204, at the fade's brightness,
/// in the last ordering-table entry; over everything but the fade, black bars of ten lines at the top and bottom and
/// Gouraud quads twenty lines high below and above them taking 255 to nothing away.</item>
/// <item>A line of text (0x80069BC0): centred by its width (each character's width less one, 0x1F one pixel back),
/// starting at line 224 and rolling up by the roll's speed (half a line a frame) until it is 12 above the top; 0x01
/// and 0x02 choose the role and name letters (the second from character 112 on), 0x03 underlines the line 17 (18 with
/// the name letters) below: a 16 by 3 end, the middle stretched over the text's width, and the other end.</item>
/// <item>The illustrations (0x80069730): sixteen pictures in turn, each fading in over 80 frames to the fade's
/// brightness, held at 128 for its hold, fading out over 80 frames, and the last held; each drawn subtracted from the
/// parchment as two 160 by 224 halves, 8-bit, at (i mod 5 times 192, 256) through palette row 480 + i mod 5. The first
/// five are loaded with the program; while picture i (4 to 14) shows, ILLUST(i + 2).BIN is unpacked, and it is loaded
/// as picture i + 1 comes up.</item>
/// <item>The rippling title (0x80068938): a 16 by 4 grid of 9 by 4 cells through the GTE at depth 512, each corner's
/// depth rippling by the sine of the phase (200 more a frame) plus 400 times its distance from the middle, the
/// ripple's height easing in from 260 to nothing as it fades in over 90 frames; held 120 frames, faded out over 90;
/// added. The distances are a 64-entry table and the grid's last row reads the 17 bytes past it, which hold the
/// illustrations' brightness, picture place and unpacking state (all 0 while the title shows, before the
/// illustrations start).</item>
/// <item>The title (0x80069088): VAGRANT STORY at (61, 80), its dark outline subtracted and its letters added, fading
/// in over 60 frames, held 60, then rolling up with the credits. The SQUARE logo (0x80069F9C) rolls up from line 224
/// at x 100 until the roll stops; the closing mark (0x80068EBC) at (248, 184) fades in over 60 frames, holds 240 and
/// fades out over 60.</item>
/// <item>Start (0x8006A4D8) takes the screen to black over 128 frames by the sine's ease, subtracting, and ends it.</item>
/// </list>
/// After the credits ENDING shows its own copy of the CONGRATULATIONS screen (<see cref="ResultsScreen"/>) over the
/// rank's picture, then the game goes to the title. The ending's music, an XA stream from sector 270000 started as
/// the credits begin (0x8006B9B4), lies outside every file on the disc, so the installation (the disc's files) does
/// not hold it.
/// </remarks>
public partial class Ending : RetailScreen
{
    public const string Scene = "res://scenes/ending/ending.tscn";
    private const uint Base = 0x80068800;
    /// <summary>The script's start, the TIMs the program loads first, and the width of each character.</summary>
    private const uint Script = 0x8006E3FC, Widths = 0x8006FF7C;
    private static readonly uint[] Tims = { 0x8007005C, 0x800CF33C, 0x800D7F7C, 0x800D975C, 0x800D837C, 0x800DB09C, 0x800DB26C, 0x8007709C, 0x80088ABC, 0x8009A4DC, 0x800ABEFC, 0x800BD91C };
    private const double FrameTime = 1.001 / 60;

    private enum Job { None, Background, Text, Illustrations, Ripple, Title, Mark, Logo, FadeIn, FadeOut, Skip }

    /// <summary>A task's record (48 bytes in retail): its job, state (+8), counter (+10), text or picture (+4, +12) and place (+16, +20).</summary>
    private sealed class Task
    {
        public Job Job;
        public int State, Count, Text, Index, X, Y;
    }

    private readonly Task[] _tasks = new Task[32];
    private byte[] _program = Array.Empty<byte>();
    private readonly byte[]?[] _files = new byte[16][];
    private byte[]? _unpacked;
    private ushort[] _sine = Array.Empty<ushort>();
    private int _pc, _wait, _master, _speed = -128, _phase, _rippleCount;
    private bool _done, _skipping;
    private readonly ushort[] _holds = new ushort[16];
    /// <summary>The ripple's distances (0x800DBB28) and the 17 bytes past them its last row reads.</summary>
    private readonly byte[] _distances = new byte[81];
    private int _shade, _pictureX, _pictureClut;
    private double _time;
    private bool _start;

    /// <summary>Whether the ending is over: the credits and the closing screen (the title follows).</summary>
    public bool Finished { get; private set; }

    /// <summary>Frames the ending has run.</summary>
    public int Frames { get; private set; }

    /// <summary>Keeps the ending in place when it finishes (a test that holds the scene sets it).</summary>
    public bool KeepScene;

    /// <summary>The game's score by the closing screen's tables (the field's record; none when the ending runs alone), and the map's percentage.</summary>
    public Func<ResultsScreen, long>? Score;
    public int MapPercent;
    /// <summary>Plays one of the closing screen's resident sounds.</summary>
    public Action<int>? Sound;

    /// <summary>The closing CONGRATULATIONS screen, once the credits are over.</summary>
    public ResultsScreen? Results { get; private set; }
    private double _resultsTime;
    private bool _pressed;

    public override void _Ready()
    {
        var content = GetNode<RetailContent>("/root/RetailContent");
        SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1, AnchorBottom = 1, ShowBehindParent = true });
        _program = content.Disc("ENDING/ENDING.PRG");
        var executable = content.Disc("SLUS_010.40");
        // rsin's table (0x80040210): sine and cosine of each 4096th of a turn to an eighth.
        int sine = (int)(0x80040210 - BitConverter.ToUInt32(executable, 0x18)) + 0x800;
        _sine = new ushort[1026];
        for (int i = 0; i < _sine.Length; i++) _sine[i] = BitConverter.ToUInt16(executable, sine + i * 2);
        foreach (uint tim in Tims) LoadTim(_program, (int)(tim - Base));
        for (int entry = 5; entry <= 15; entry++) _files[entry] = content.Disc($"ENDING/ILLUST{entry + 1:d2}.BIN");
        for (int i = 0; i < _tasks.Length; i++) _tasks[i] = new Task();
        Spawn(Job.Background);
        _pc = (int)(Script - Base);
    }

    public override void _Process(double delta)
    {
        if (Finished) return;
        if (Results != null) { Close(delta); return; }
        _start |= Input.IsActionJustPressed("ps_start");
        _time += delta;
        while (_time >= FrameTime && !Finished)
        {
            _time -= FrameTime;
            Step();
            _start = false;
        }
    }

    /// <summary>One frame of the ending's loop (0x8006A888).</summary>
    private void Step()
    {
        // The draw environment clears to black, with texture page 32 and no dithering.
        BeginFrame(2048, true, 32, false);
        Advance();
        for (int slot = 0; slot < _tasks.Length; slot++)
        {
            var task = _tasks[slot];
            if (task.State < 0) task.State = 0;
            else if (task.State > 0) Run(task);
        }
        if (_start && !_skipping)
        {
            Spawn(Job.Skip);
            _skipping = true;
        }
        EndFrame();
        Frames++;
        if (_done) Conclude();
    }

    /// <summary>
    /// After the credits (0x8006BD78): END.DIS and the rank's picture ENDSCRnn.DIS are read (the frames between are
    /// the drive's), then ENDING's copy of the CONGRATULATIONS screen runs at 30 frames a second until a button and its
    /// closing fade end it; the title follows.
    /// </summary>
    private void Conclude()
    {
        var content = GetNode<RetailContent>("/root/RetailContent");
        Results = new ResultsScreen { Name = "Results", Sound = Sound };
        AddChild(Results);
        Results.Configure(content.Disc("SMALL/END.DIS"), content.Data("ending")["results"].AsGodotDictionary(), null, null);
        long score = Score?.Invoke(Results) ?? 0;
        // The ranks' titles are not kept, so none count.
        int rank = Results.Rank(score, 0);
        Results.OpenEnding(content.Disc($"SMALL/ENDSCR{rank:d2}.DIS"), (int)score, MapPercent, 0);
        _resultsTime = 0;
    }

    /// <summary>A step of the closing screen: two vertical blanks a frame; a button newly pressed closes it.</summary>
    private void Close(double delta)
    {
        foreach (var action in InputMap.GetActions())
            if (action.ToString().StartsWith("ps_", StringComparison.Ordinal) && Input.IsActionJustPressed(action)) _pressed = true;
        _resultsTime += delta;
        while (_resultsTime >= FrameTime * 2 && !Finished)
        {
            _resultsTime -= FrameTime * 2;
            bool pressed = _pressed;
            _pressed = false;
            if (Results!.Step(pressed)) continue;
            Finished = true;
            if (!KeepScene) _ = GetNode<SceneManager>("/root/SceneManager").ChangeScene(Wol.Games.Vagrant.Scenes.Startup.Startup.TitleScene);
        }
    }

    /// <summary>Takes the first free task slot from 1 (0x8006AB44).</summary>
    private Task? Spawn(Job job)
    {
        for (int slot = 1; slot < _tasks.Length; slot++)
        {
            if (_tasks[slot].State != 0) continue;
            _tasks[slot] = new Task { Job = job, State = 1 };
            return _tasks[slot];
        }
        GD.PushWarning("ENDING: every task slot is taken");
        return null;
    }

    /// <summary>The script's commands for this frame (0x8006ABF0).</summary>
    private void Advance()
    {
        if (_wait != 0) { _wait--; return; }
        while (true)
        {
            switch (_program[_pc])
            {
                case 1:
                    _wait = _program[_pc + 1] * 2;
                    _pc += 2;
                    return;
                case 2:
                    _pc++;
                    if (Spawn(Job.Text) is { } line) line.Text = _pc;
                    _pc += _program[_pc] + 1;
                    break;
                case 3:
                    _pc++;
                    Spawn(Job.Illustrations);
                    break;
                case 4:
                    int piece = _program[_pc + 1];
                    _pc += 2;
                    if (piece <= 3) Spawn(piece switch { 0 => Job.Logo, 1 => Job.Title, 2 => Job.Mark, _ => Job.Ripple });
                    break;
                case 5 or 6:
                    // The fades take slot 0 when it is free.
                    if (_tasks[0].State == 0) _tasks[0] = new Task { Job = _program[_pc] == 5 ? Job.FadeIn : Job.FadeOut, State = 1 };
                    _pc++;
                    break;
                case 7:
                    _pc++;
                    _speed = 0;
                    break;
                case 8:
                    _holds[_program[_pc + 1]] = (ushort)(_program[_pc + 2] + _program[_pc + 3] * 256 - 160);
                    _pc += 4;
                    break;
                case 255:
                    _done = true;
                    return;
                default:
                    return;
            }
        }
    }

    private void Run(Task task)
    {
        switch (task.Job)
        {
            case Job.Background: Background(); break;
            case Job.Text: Text(task); break;
            case Job.Illustrations: Illustrations(task); break;
            case Job.Ripple: Ripple(task); break;
            case Job.Title: Title(task); break;
            case Job.Mark: Mark(task); break;
            case Job.Logo: Logo(task); break;
            case Job.FadeIn:
                if (task.State == 1) { task.Count = 0; task.State = 2; }
                if (++task.Count == 128) task.State = -1;
                _master = Ease(0, task.Count, 128) >> 5;
                break;
            case Job.FadeOut:
                if (task.State == 1) { task.State = 2; task.Count = 0; }
                // Its +14 would end the ending as it finishes; command 6 clears it.
                if (++task.Count == 128) task.State = -1;
                _master = 128 - (Ease(0, task.Count, 128) << 7 >> 12);
                break;
            case Job.Skip: Skip(task); break;
        }
    }

    /// <summary>The ending's easings (0x8006AE54) from 0 to 4096 over <paramref name="length"/>: 0 even, 1 the sine's rise, 2 eased both ways, 3 eased in.</summary>
    private int Ease(int kind, int time, int length) => kind switch
    {
        0 => (short)time * 4096 / (short)length,
        1 => Sine((short)time * 1024 / (short)length),
        2 => (Sine((short)time * 2048 / (short)length + 2048 + 1024) + 4096) >> 1,
        _ => Sine((short)time * 1024 / (short)length - 1024) + 4096,
    };

    /// <summary>rsin (0x80040F28; the ending's own copy at 0x800688D4): the sine of a 4096th of a turn, 4096 the whole.</summary>
    private int Sine(int angle)
    {
        int value;
        if ((angle & 0x400) != 0)
        {
            if ((angle & 0x3FF) == 0) return (angle & 0x800) != 0 ? -4096 : 4096;
            angle = 2048 - angle;
        }
        int step = angle & 0x1FF;
        value = (angle & 0x200) != 0 ? _sine[(512 - step) * 2 + 1] : _sine[step * 2];
        return (angle & 0x800) != 0 ? -value : value;
    }

    /// <summary>A sprite primitive put in an ordering-table entry.</summary>
    private void Sprite(int entry, int x, int y, int width, int height, int u, int v, int clut, int shade, bool semi) =>
        Order(entry, () => Sprite(x, y, width, height, u, v, clut, shade, shade, shade, semi));

    /// <summary>A draw-mode primitive (0x8006A958: SetDrawTPage with dithering) put in an ordering-table entry.</summary>
    private void Mode(int entry, int page) => Order(entry, () => DrawMode(page, true));

    /// <summary>The parchment, the bars and the edges' shading (0x8006A0D8).</summary>
    private void Background()
    {
        int shade = _master & 255;
        Sprite(2047, 0, 10, 256, 204, 0, 10, 16368, shade, false);
        Mode(2047, 44);
        Sprite(2047, 256, 10, 64, 204, 0, 10, 16368, shade, false);
        Mode(2047, 45);
        Order(2, () => Polygon(new Corner[] { new(0, 10, 0, 0, 255, 255, 255), new(320, 10, 0, 0, 255, 255, 255), new(0, 30, 0, 0, 0, 0, 0), new(320, 30, 0, 0, 0, 0, 0) }, -1, 0, true, true));
        Order(2, () => Polygon(new Corner[] { new(0, 194, 0, 0, 0, 0, 0), new(320, 194, 0, 0, 0, 0, 0), new(0, 214, 0, 0, 255, 255, 255), new(320, 214, 0, 0, 255, 255, 255) }, -1, 0, true, true));
        Order(2, () => Tile(0, 0, 320, 10, 0, 0, 0, false));
        Order(2, () => Tile(0, 0, 320, 10, 0, 0, 0, false));
        Order(2, () => Tile(0, 214, 320, 10, 0, 0, 0, false));
        Mode(2, 192);
    }

    /// <summary>A line's width (0x80069AEC): each character's width less one, 0x1F one pixel less.</summary>
    private int Width(int text)
    {
        int width = 0, letters = 0;
        for (int i = 0; i < _program[text]; i++)
        {
            int c = _program[text + 1 + i];
            if (c >= 32) width += _program[Widths - Base + ((letters + c - 32) & 255)] - 1;
            else if (c == 1) letters = 0;
            else if (c == 2) letters = 112;
            else if (c == 31) width--;
        }
        return (short)width;
    }

    /// <summary>A line of the credits (0x80069BC0).</summary>
    private void Text(Task task)
    {
        int width = Width(task.Text);
        if (task.State == 1)
        {
            task.X = ((320 - width) >> 1) << 8;
            task.Y = 0xE000;
            task.State = 2;
        }
        task.Y += _speed;
        if (task.Y < -3072) { task.State = -1; return; }
        int y = (short)(task.Y >> 8), x = task.X >> 8, letters = 0, line = 0, shade = _master & 255;
        for (int i = 0; i < _program[task.Text]; i++)
        {
            int c = _program[task.Text + 1 + i];
            if (c < 32)
            {
                if (c == 1) letters = 0;
                else if (c == 2) letters = 112;
                else if (c == 3) line = letters == 0 ? 17 : 18;
                else if (c == 31) x--;
                continue;
            }
            int glyph = (letters + c - 32) & 255, w = _program[Widths - Base + glyph];
            Sprite(3, x, y, w, 16, (glyph & 15) << 4, glyph & 0xF0, 30780, shade, false);
            x += w - 1;
        }
        if (line != 0)
        {
            int left = 160 - ((width + 32) >> 1), at = y + line;
            Sprite(3, left, at, 16, 3, 0, 208, 30780, shade, false);
            left += 16;
            int end = left + width;
            Order(3, () => Polygon(new Corner[] { new(left, at, 8, 208, shade, shade, shade), new(end, at, 16, 208, shade, shade, shade), new(left, at + 3, 8, 211, shade, shade, shade), new(end, at + 3, 16, 211, shade, shade, shade) }, 31, 30780, false, false));
            Sprite(3, end, at, 16, 3, 168, 208, 30780, shade, false);
        }
        Mode(3, 31);
    }

    /// <summary>The illustrations (0x80069730).</summary>
    private void Illustrations(Task task)
    {
        switch (task.State)
        {
            case 1:
                if (task.Index >= 4 && task.Index != 15) _unpacked = EndingConversion.Decompress(_files[task.Index + 1]!);
                task.State = 2;
                task.Count = 0;
                _pictureX = task.Index % 5 * 192;
                _pictureClut = task.Index % 5 + 480;
                goto case 2;
            case 2:
                _shade = _master * task.Count / 80;
                if (++task.Count == 80)
                {
                    task.Count = 0;
                    task.State = task.Index == 15 ? 5 : 3;
                }
                break;
            case 3:
                _shade = 128;
                if (++task.Count == _holds[task.Index]) { task.State = 4; task.Count = 0; }
                break;
            case 4:
                _shade = _master * (80 - task.Count) / 80;
                if (++task.Count == 80)
                {
                    if (++task.Index >= 5) LoadTim(_unpacked!, 0);
                    task.State = 1;
                }
                break;
            case 5:
                _shade = 128;
                break;
        }
        int shade = _shade & 255, clut = _pictureClut << 6, page = _pictureX;
        Sprite(4, 0, 0, 160, 224, 0, 0, clut, shade, true);
        Mode(4, ((page & 0x3FF) >> 6) | 0xD0);
        Sprite(4, 160, 0, 160, 224, 32, 0, clut, shade, true);
        Mode(4, (((short)page + 64) & 0x3FF) >> 6 | 0xD0);
    }

    /// <summary>The rippling title (0x80068938).</summary>
    private void Ripple(Task task)
    {
        int shade = 0;
        switch (task.State)
        {
            case 1:
                _phase = 0;
                task.Count = 0;
                task.State = 2;
                _rippleCount = 0;
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 16; column++)
                    {
                        int across = Math.Abs(2 - row) << 2, down = Math.Abs(8 - column) * 18;
                        _distances[row * 16 + column] = (byte)SquareRoot(across * across + down * down);
                    }
                goto case 2;
            case 2:
                shade = task.Count * 128 / 90;
                task.Count++;
                _rippleCount++;
                if (task.Count == 90) { task.State = 3; task.Count = 0; }
                break;
            case 3:
                shade = 128;
                if (++task.Count == 120) { task.State = 4; task.Count = 0; }
                break;
            case 4:
                shade = (90 - task.Count) * 128 / 90;
                if (++task.Count == 90) task.State = -1;
                break;
        }
        _phase += 200;
        int height = (int)((uint)(Ease(3, (short)(90 - _rippleCount), 90) * 65) >> 10) & 0xFFFF;
        shade &= 255;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 16; column++)
            {
                int Depth(int index) => Sine(_phase + _distances[index] * 400) * height >> 12;
                var a = Project(column * 9 - 72, row * 4 - 8, Depth(row * 16 + column));
                var b = Project((column + 1) * 9 - 72, row * 4 - 8, Depth(row * 16 + column + 1));
                var c = Project(column * 9 - 72, (row + 1) * 4 - 8, Depth((row + 1) * 16 + column));
                var d = Project((column + 1) * 9 - 72, (row + 1) * 4 - 8, Depth((row + 1) * 16 + column + 1));
                int u0 = column * 9, u1 = (column + 1) * 9, v0 = (row * 4 - 76) & 255, v1 = ((row + 1) * 4 - 76) & 255;
                var corners = new Corner[] { new(a.X, a.Y, u0, v0, shade, shade, shade), new(b.X, b.Y, u1, v0, shade, shade, shade), new(c.X, c.Y, u0, v1, shade, shade, shade), new(d.X, d.Y, u1, v1, shade, shade, shade) };
                Order(3, () => Polygon(corners, 46, 5368, false, true));
            }
    }

    /// <summary>SquareRoot0 (0x80041B94): the integer square root.</summary>
    private static int SquareRoot(int value) => (int)Math.Sqrt(value);

    /// <summary>
    /// A point through the GTE as the ripple sets it (RotTransPers: no turn, 512 deep, the screen 512 away and its
    /// centre at (160, 112)): the depth's division by the GTE's own reciprocal table.
    /// </summary>
    private static Vector2I Project(int x, int y, int z)
    {
        int depth = Math.Clamp(512 + z, 0, 0xFFFF);
        long n = Divide(512, depth);
        return new Vector2I((int)Math.Clamp((n * x + (160L << 16)) >> 16, -1024, 1023), (int)Math.Clamp((n * y + (112L << 16)) >> 16, -1024, 1023));
    }

    /// <summary>The GTE's division of the projection distance by a depth (its UNR reciprocal), at most 0x1FFFF.</summary>
    private static long Divide(int distance, int depth)
    {
        if (distance >= depth * 2) return 0x1FFFF;
        int shift = BitOperations.LeadingZeroCount((uint)depth) - 16;
        long n = (long)distance << shift;
        long d = (long)depth << shift;
        long u = Math.Max(0, (0x40000 / ((d - 0x7FC0 >> 7) + 0x100) + 1) / 2 - 0x101) + 0x101;
        d = (0x2000080 - d * u) >> 8;
        d = (0x0000080 + d * u) >> 8;
        return Math.Min(0x1FFFF, (n * d + 0x8000) >> 16);
    }

    /// <summary>VAGRANT STORY (0x80069088): its outline subtracted, its letters added.</summary>
    private void Title(Task task)
    {
        int shade = 0;
        switch (task.State)
        {
            case 1:
                task.X = 15616;
                task.Y = 20480;
                task.Count = 0;
                task.State = 2;
                goto case 2;
            case 2:
                shade = _master * ++task.Count / 60;
                if (task.Count == 60) { task.State = 3; task.Count = 0; }
                break;
            case 3:
                shade = _master;
                if (++task.Count == 60) task.State = 4;
                break;
            case 4:
                task.Y += _speed;
                shade = _master;
                if (task.Y < -15360) { task.State = -1; return; }
                break;
        }
        shade &= 255;
        Sprite(3, task.X >> 8, task.Y >> 8, 208, 48, 0, 96, 5176, shade, true);
        Mode(3, 46);
        Sprite(3, task.X >> 8, task.Y >> 8, 208, 48, 0, 96, 5240, shade, true);
        Mode(3, 78);
    }

    /// <summary>The closing mark (0x80068EBC).</summary>
    private void Mark(Task task)
    {
        int shade = 0;
        switch (task.State)
        {
            case 1:
                task.Count = 0;
                task.State = 2;
                goto case 2;
            case 2:
                shade = task.Count * 128 / 60;
                if (++task.Count == 60) { task.State = 3; task.Count = 0; }
                break;
            case 3:
                shade = 128;
                if (++task.Count == 240) { task.State = 4; task.Count = 0; }
                break;
            case 4:
                shade = (60 - task.Count) * 128 / 60;
                if (++task.Count == 60) task.State = -1;
                break;
        }
        Sprite(1, 248, 184, 36, 17, 0, 160, 5304, shade & 255, false);
        Mode(1, 46);
    }

    /// <summary>The SQUARE logo (0x80069F9C), rolling up with the credits.</summary>
    private void Logo(Task task)
    {
        if (task.State == 1)
        {
            task.X = 25600;
            task.Y = 0xE000;
            task.State = 2;
        }
        task.Y += _speed;
        if (task.Y < -3072) { task.State = -1; return; }
        Sprite(3, task.X >> 8, task.Y >> 8, 120, 16, 0, 48, 4156, _master & 255, false);
        Mode(3, 47);
    }

    /// <summary>Start's fade to black (0x8006A4D8), which ends the ending.</summary>
    private void Skip(Task task)
    {
        if (task.State == 1) { task.Count = 0; task.State = 2; }
        if (++task.Count == 128) _done = true;
        int level = (Ease(1, task.Count, 128) * 255) >> 12;
        Order(0, () => Tile(0, 0, 320, 240, level, level, level, true));
        Mode(0, 64);
    }
}
