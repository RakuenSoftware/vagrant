// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Godot;

namespace Wol.Games.Vagrant.Installation;

/// <summary>
/// Converts one retail room (an MPD map within its ZND zone) into a shared-engine field document.
/// The zone's texture images are replayed into an emulated 1024x512 VRAM exactly as the retail game uploads them,
/// and every texture page and palette pair the room's polygons sample is exported as an ordinary image.
/// Polygons keep the PlayStation's per-vertex colour, which modulates texels in display space at up to twice
/// their value (128 is unity); the document stores half that factor and the field presentation restores it.
/// </summary>
public sealed class MapConversion
{
    /// <summary>Retail map units per engine unit.</summary>
    public const float UnitsPerMetre = 128f;
    private const int TriangleSize = 32, QuadSize = 40, GroupHeaderSize = 64, RoomHeaderSize = 96;

    private readonly ushort[] _vram = new ushort[1024 * 512];
    /// <summary>Per floor tile (x, z, height): the summed corner colours of the room's horizontal polygons and their count.</summary>
    private readonly SortedDictionary<(int X, int Z, int Y), (float R, float G, float B, int Count)> _floorLight = new();
    private readonly byte[] _map, _zone;
    private readonly string _name;

    public MapConversion(string name, byte[] zone, byte[] map)
    {
        _name = name;
        _map = map;
        _zone = zone;
        if (zone.Length < 32) throw new InvalidDataException("The zone file is truncated.");
        int imagePointer = (int)BitConverter.ToUInt32(zone, 16), imageLength = (int)BitConverter.ToUInt32(zone, 20);
        if (imagePointer + imageLength > zone.Length || imageLength < 20) throw new InvalidDataException("The zone's texture section is malformed.");
        int count = (int)BitConverter.ToUInt32(zone, imagePointer + 16), offset = imagePointer + 20;
        for (int i = 0; i < count; i++)
        {
            int size = (int)BitConverter.ToUInt32(zone, offset);
            // Each entry is a 16-bit TIM image: a VRAM rectangle uploaded verbatim (texture pages and palettes alike).
            if (BitConverter.ToUInt32(zone, offset + 4) != 0x10 || BitConverter.ToUInt32(zone, offset + 8) != 2) throw new InvalidDataException("A zone texture is not a direct VRAM image.");
            int x = BitConverter.ToUInt16(zone, offset + 16), y = BitConverter.ToUInt16(zone, offset + 18), width = BitConverter.ToUInt16(zone, offset + 20), height = BitConverter.ToUInt16(zone, offset + 22);
            if (x + width > 1024 || y + height > 512 || offset + 24 + width * height * 2 > zone.Length) throw new InvalidDataException("A zone texture lies outside VRAM.");
            for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                    _vram[(y + row) * 1024 + x + column] = BitConverter.ToUInt16(zone, offset + 24 + (row * width + column) * 2);
            offset += 4 + size;
        }
        if (offset != imagePointer + imageLength) throw new InvalidDataException("The zone's texture section has an unexpected length.");
    }


    private byte[]? _sky, _skyLayout;
    private byte[]? _battle;

    /// <summary>Gives the conversion BATTLE.PRG, whose three object models the room's crates are built from.</summary>
    public void AddObjects(byte[] battle) => _battle = battle;

    /// <summary>
    /// Adds the room's sky backdrop (BG/*.TIM with its .FAR layout). The layout is a column and row count, a word,
    /// then one 24-byte cell per tile, row by row: a source rectangle in the image (x, y and a signed width and
    /// height; a negative one reads leftward or upward, mirroring it) and four corner colours (128 is full
    /// brightness). BATTLE.PRG's drawer (0x8009147C) lays the cells on a grid of 32 by 32, each stretching its
    /// rectangle over its square, and skips an empty one; the grid repeats across the scroll.
    /// </summary>
    public void AddSky(byte[] image, byte[] layout)
    {
        _sky = image;
        _skyLayout = layout;
    }

    private Image Sky(out int width, out int height)
    {
        byte[] tim = _sky!, far = _skyLayout!;
        // TIM files start with id 0x10; some carry a version byte above it.
        if ((BitConverter.ToUInt32(tim, 0) & 0xFF) != 0x10 || (BitConverter.ToUInt32(tim, 4) & 8) == 0) throw new InvalidDataException("The sky image is not an indexed TIM.");
        int clutBlock = (int)BitConverter.ToUInt32(tim, 8), colours = BitConverter.ToUInt16(tim, 16) * BitConverter.ToUInt16(tim, 18);
        int pixels = 8 + clutBlock, imageWidth = BitConverter.ToUInt16(tim, pixels + 8) * 2, imageHeight = BitConverter.ToUInt16(tim, pixels + 10);
        int columns = (int)BitConverter.ToUInt32(far, 0), rows = (int)BitConverter.ToUInt32(far, 4);
        if (12 + columns * rows * 24 > far.Length) throw new InvalidDataException("The sky layout is truncated.");
        const int Cell = 32;
        width = Math.Max(1, columns * Cell);
        height = Math.Max(1, rows * Cell);
        // Pixels are written to a buffer and handed to Godot whole: one call, not one per pixel.
        var data = new byte[width * height * 4];
        for (int cell = 0; cell < columns * rows; cell++)
        {
            int at = 12 + cell * 24;
            int x = BitConverter.ToUInt16(far, at), y = BitConverter.ToUInt16(far, at + 2), w = (short)BitConverter.ToUInt16(far, at + 4), h = (short)BitConverter.ToUInt16(far, at + 6);
            if (w == 0 && h == 0) continue;
            int column = cell % columns, row = cell / columns;
            var corners = new float[4];
            for (int c = 0; c < 4; c++) corners[c] = far[at + 8 + c * 4] / 128f;
            for (int v = 0; v < Cell; v++)
                for (int u = 0; u < Cell; u++)
                {
                    int sx = Math.Clamp(x + (int)Math.Floor(u * w / (float)Cell), 0, imageWidth - 1), sy = Math.Clamp(y + (int)Math.Floor(v * h / (float)Cell), 0, imageHeight - 1);
                    int index = tim[pixels + 12 + sy * imageWidth + sx];
                    ushort colour = index < colours ? BitConverter.ToUInt16(tim, 20 + index * 2) : (ushort)0;
                    float fx = u / (float)(Cell - 1), fy = v / (float)(Cell - 1);
                    float shade = Mathf.Lerp(Mathf.Lerp(corners[0], corners[1], fx), Mathf.Lerp(corners[2], corners[3], fx), fy);
                    int r = colour & 31, g = (colour >> 5) & 31, b = (colour >> 10) & 31;
                    int at4 = ((row * Cell + v) * width + column * Cell + u) * 4;
                    data[at4] = (byte)Math.Clamp(Math.Round(r / 31f * shade * 255), 0, 255);
                    data[at4 + 1] = (byte)Math.Clamp(Math.Round(g / 31f * shade * 255), 0, 255);
                    data[at4 + 2] = (byte)Math.Clamp(Math.Round(b / 31f * shade * 255), 0, 255);
                    data[at4 + 3] = 255;
                }
        }
        return Image.CreateFromData(width, height, false, Image.Format.Rgba8, data);
    }

    /// <summary>Returns the room's field document, putting its textures into <paramref name="images"/> by the names the document gives them.</summary>
    public JsonObject Export(IDictionary<string, Image> images)
    {
        int room = (int)BitConverter.ToUInt32(_map, 0), roomLength = (int)BitConverter.ToUInt32(_map, 4);
        if (room + roomLength > _map.Length || roomLength < RoomHeaderSize + 4) throw new InvalidDataException("The room section is malformed.");
        int geometryLength = (int)BitConverter.ToUInt32(_map, room);
        int geometry = room + RoomHeaderSize, groups = (int)BitConverter.ToUInt32(_map, geometry);
        int cursor = geometry + 4 + groups * GroupHeaderSize;
        var textures = new SortedDictionary<string, (int Page, int Clut, int Mask)>(StringComparer.Ordinal);
        // Each geometry group is its own model and object, so the field can hide groups as the retail renderer does.
        // A group header opens with its flags: the low byte is the camera-direction mask (the group is drawn while
        // the bit for the camera's octant is set), 0x0800 marks corner offsets in map units rather than eighths,
        // and 0x8000 marks parts drawn only in first-person view (black boxes that close off the view out of the room).
        // Words 4, 8 and 12 place the group: its faces are modelled about that point (platforms move it).
        var models = new JsonArray();
        var objects = new JsonArray();
        var roomGroups = new JsonArray();
        var scrolls = FaceScrolls(room);
        for (int group = 0; group < groups; group++)
        {
            int flags = BitConverter.ToUInt16(_map, geometry + 4 + group * GroupHeaderSize);
            int header = geometry + 4 + group * GroupHeaderSize;
            var origin = ((int)BitConverter.ToUInt32(_map, header + 4), (int)BitConverter.ToUInt32(_map, header + 8), (int)BitConverter.ToUInt32(_map, header + 12));
            _groupBounds[group] = (int.MaxValue, int.MinValue, int.MaxValue, int.MinValue, int.MaxValue, int.MinValue);
            _boundsGroup = group;
            int scale = (flags & 0x0800) != 0 ? 1 : 8;
            int triangles = (int)BitConverter.ToUInt32(_map, cursor), quads = (int)BitConverter.ToUInt32(_map, cursor + 4);
            cursor += 8;
            var surfaces = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
            for (int face = 0; face < triangles + quads; face++)
            {
                bool quad = face >= triangles;
                // A face whose texture scrolls is drawn as a surface of its own, which carries the scroll.
                scrolls.TryGetValue(cursor - geometry, out var scroll);
                ReadFace(cursor, quad, scale, origin, surfaces, textures, scroll == null ? "" : $"_scroll{cursor - geometry:x}");
                if (scroll != null)
                    foreach (var (surfaceName, surface) in surfaces)
                        if (surfaceName.EndsWith($"_scroll{cursor - geometry:x}", StringComparison.Ordinal)) surface["uv_animation"] = scroll.DeepClone();
                cursor += quad ? QuadSize : TriangleSize;
            }
            roomGroups.Add(new JsonObject { ["mask"] = flags & 0xFF, ["first_person"] = (flags & 0x8000) != 0 });
            if (surfaces.Count == 0) continue;
            var groupList = new JsonArray();
            foreach (var surface in surfaces.Values) groupList.Add(surface);
            objects.Add(new JsonObject { ["id"] = group, ["model"] = models.Count, ["position"] = new JsonArray { Round(origin.Item1 / UnitsPerMetre), Round(-origin.Item2 / UnitsPerMetre), Round(-origin.Item3 / UnitsPerMetre) } });
            models.Add(new JsonObject { ["blocks"] = new JsonArray { new JsonObject { ["groups"] = groupList } } });
        }
        if (cursor != geometry + geometryLength) throw new InvalidDataException("The room geometry has an unexpected length.");

        // Crates add models, objects and a texture of their own before the textures are written.
        var crates = Crates(models, objects, textures, groups);
        var trapPanel = TrapPanel(textures);
        var textureFiles = new JsonObject();
        foreach (var (key, (page, clut, mask)) in textures)
        {
            images[$"{_name}.{key}"] = Page(page, clut, mask);
            textureFiles[key] = $"{_name}.{key}";
        }
        var textureFrames = TextureFrames(room, textures, images);
        // Each animation's frames are among the room's textures, by name, for the engine to draw from.
        foreach (var frames in textureFrames) textureFiles[(string)frames!["strip"]!] = (string)frames["strip"]!;
        // Actors take their light from the floor tile under them: x, z, floor height and the mean colour (128 is unity).
        var light = new JsonArray();
        foreach (var ((x, z, y), (r, g, b, count)) in _floorLight)
            light.Add(new JsonArray { x, z, y, MathF.Round(r / count), MathF.Round(g / count), MathF.Round(b / count) });
        var (walkmeshes, floor) = Collision(room);
        var backgrounds = new JsonArray();
        if (_sky != null)
        {
            var sky = Sky(out int skyWidth, out int skyHeight);
            images[_name + ".sky"] = sky;
            textureFiles["sky"] = _name + ".sky";
            // The panorama is drawn at screen scale on the 320x240 display and wraps once per turn of the camera.
            backgrounds.Add(new JsonObject
            {
                ["texture"] = "sky", ["reference_size"] = new JsonArray { 320, 224 }, ["pixel_size"] = new JsonArray { skyWidth, skyHeight },
                ["distance"] = 64, ["height"] = 640 / UnitsPerMetre, ["turns"] = 1,
            });
        }
        return new JsonObject
        {
            ["schema"] = 1,
            ["presentation_hz"] = 60,
            ["models"] = models,
            ["objects"] = objects,
            ["room_groups"] = roomGroups,
            ["walkmeshes"] = walkmeshes,
            ["floor"] = floor,
            ["textures"] = textureFiles, ["texture_frames"] = textureFrames,
            ["background_layers"] = backgrounds,
            ["floor_light"] = light, ["light_map"] = LightMap(room),
            ["camera_bounds"] = CameraBounds(room, floor),
            ["tile_attributes"] = TileAttributes(room),
            ["doors"] = Doors(room),
            ["chests"] = Chests(room),
            ["traps"] = Traps(room), ["trap_panel"] = trapPanel, ["zone_links"] = ZoneLinks(room), ["platform_docks"] = PlatformDocks(room), ["enemy_map"] = EnemyMap(room),
            ["locks"] = Locks(room),
            ["platforms"] = Platforms(room), ["swings"] = Swings(room), ["group_scripts"] = GroupScripts(room),
            ["room_actors"] = RoomActors(),
            ["crates"] = crates,
            ["crate_turns"] = CrateTurns(), ["crate_rolls"] = CrateRolls(),
            ["scene"] = Scene(room), ["trap_level"] = TrapLevel(room), ["telegraphs"] = Telegraphs(room), ["cooldown"] = Cooldown(room), ["crate_attacks"] = CrateAttacks(room), ["patrol_routes"] = PatrolRoutes(room), ["special_spots"] = SpecialSpots(room), ["minimap"] = Minimap(room), ["workshop"] = WorkshopKind(room), ["weather"] = Weather(room), ["sky_dome"] = SkyColours(room) is JsonArray bands && bands.Count == 5 ? new JsonObject { ["colours"] = bands, ["ring"] = 204 / 457f, ["radius"] = 409, ["lines"] = 224 } : null,
            ["conditions"] = Conditions(),
        };
    }

    /// <summary>
    /// The field's simple map of the room and its neighbours (sub-section 14): an area map in SMALL/SCENnnn.ARM's
    /// layout without the names, the room first and at the origin, its neighbours placed around it. Null without one.
    /// </summary>
    private JsonArray? Minimap(int room)
    {
        int at = SubSection(room, 14, out int length);
        if (length < 4 || at + length > _map.Length) return null;
        var map = AreaMapConversion.Export(_map.AsSpan(at, length).ToArray());
        var rooms = map["rooms"]!.AsArray();
        map.Remove("rooms");
        return rooms;
    }

    /// <summary>How strong the room's traps are: the byte at 0x32 of room sub-section 10 (section B); 0 without one.</summary>
    private int TrapLevel(int room)
    {
        int at = SubSection(room, 10, out int length);
        return length > 0x32 ? _map[at + 0x32] : 0;
    }

    /// <summary>
    /// How many enemies may telegraph a move at once (BATTLE.PRG 0x800D836C against the count the battle keeps): one
    /// more than the top three bits of the word at 0x450 of room sub-section 5, which BATTLE.PRG 0x800E6F9C reads as
    /// the battle starts; one in a room without the section. Nearly every room allows one.
    /// </summary>
    private int Telegraphs(int room)
    {
        int at = SubSection(room, 5, out int length);
        return length >= 0x454 ? (int)(BitConverter.ToUInt32(_map, at + 0x450) >> 29) + 1 : 1;
    }

    /// <summary>
    /// The frames an enemy waits after a move before it may plan one sooner (BATTLE.PRG 0x800E71A4 keeps it at the
    /// battle's +8, which 0x800dad74 gives the mover's cooldown): bits 21 to 28 of the word at 0x450 of room
    /// sub-section 5; 150 in a room without the section.
    /// </summary>
    private int Cooldown(int room)
    {
        int at = SubSection(room, 5, out int length);
        return length >= 0x454 ? (int)(BitConverter.ToUInt32(_map, at + 0x450) >> 21 & 0xFF) : 150;
    }

    /// <summary>
    /// Whether the room's enemies may strike its breakable crates (the battle AI block's +10, which BATTLE.PRG 0x800e6f9c
    /// sets from bit 20 of the word at 0x450 of room sub-section 5; 0x800e5710 then marks the actions able to hit
    /// objects); false in a room without the section.
    /// </summary>
    private bool CrateAttacks(int room)
    {
        int at = SubSection(room, 5, out int length);
        return length >= 0x454 && (BitConverter.ToUInt32(_map, at + 0x450) >> 20 & 1) != 0;
    }

    /// <summary>
    /// The room's four patrol routes (room sub-section 5 at 0x400, BATTLE.PRG's [0x800f58d8]): 16 bytes each, eight
    /// halfword points of a tile x (bits 0 to 4) and z (5 to 9), bit 15 set on a point in use; kept as the halfwords.
    /// Empty for a room without the section.
    /// </summary>
    private JsonArray PatrolRoutes(int room)
    {
        var routes = new JsonArray();
        int at = SubSection(room, 5, out int length);
        if (length < 0x440) return routes;
        for (int r = 0; r < 4; r++)
        {
            var points = new JsonArray();
            for (int i = 0; i < 8; i++) points.Add((int)BitConverter.ToUInt16(_map, at + 0x400 + r * 16 + i * 2));
            routes.Add(points);
        }
        return routes;
    }

    /// <summary>
    /// The room's eight special spots (room sub-section 5 at 0x440, BATTLE.PRG's [0x800f58c8]): halfwords of a tile x
    /// (bits 0 to 4) and z (5 to 9), bit 15 set on one in use, where an enemy with a long-reaching harmful move goes
    /// before making it (0x800dd7cc). Empty for a room without the section.
    /// </summary>
    private JsonArray SpecialSpots(int room)
    {
        var spots = new JsonArray();
        int at = SubSection(room, 5, out int length);
        if (length < 0x450) return spots;
        for (int i = 0; i < 8; i++) spots.Add((int)BitConverter.ToUInt16(_map, at + 0x440 + i * 2));
        return spots;
    }

    /// <summary>The area the room belongs to: the scene id in room sub-section 10 (section B), at 0x50; -1 without one.</summary>
    private int Scene(int room)
    {
        int at = SubSection(room, 10, out int length);
        return length >= 0x52 ? BitConverter.ToInt16(_map, at + 0x50) : -1;
    }

    /// <summary>
    /// The room's light map (sub-section 4), which lights its characters (BATTLE.PRG 0x80098b38 by way of 0x8008c458):
    /// a colour word for anywhere it does not cover, two record counts, then 32-byte records of a bounding rectangle
    /// (min x, min z, max x, max z), three corners (x, z) and their three colours. Characters use the first set only.
    /// </summary>
    private JsonObject? LightMap(int room)
    {
        int at = SubSection(room, 4, out int length);
        if (length < 12) return null;
        int count = (int)BitConverter.ToUInt32(_map, at + 4);
        var triangles = new JsonArray();
        for (int i = 0; i < count && 12 + (i + 1) * 32 <= length; i++)
        {
            int p = at + 12 + i * 32;
            var record = new JsonArray();
            for (int h = 0; h < 10; h++) record.Add((int)BitConverter.ToInt16(_map, p + h * 2));
            for (int c = 0; c < 3; c++) for (int k = 0; k < 3; k++) record.Add((int)_map[p + 20 + c * 4 + k]);
            triangles.Add(record);
        }
        return new JsonObject { ["colour"] = new JsonArray { (int)_map[at], (int)_map[at + 1], (int)_map[at + 2] }, ["records"] = triangles };
    }

    /// <summary>
    /// The room's own sound bank (room sub-section 19), laid out as an event bank: a count, then offsets from the
    /// section's start to AKAO sound programs. Its scripted groups play from it (operation 6).
    /// </summary>
    public byte[] SoundBank
    {
        get
        {
            int room = (int)BitConverter.ToUInt32(_map, 0);
            if ((int)BitConverter.ToUInt32(_map, 4) < RoomHeaderSize) return Array.Empty<byte>();
            int at = SubSection(room, 19, out int length);
            return length >= 8 && at + length <= _map.Length ? _map[at..(at + length)] : Array.Empty<byte>();
        }
    }

    /// <summary>
    /// The colours of the room's sky dome (sub-section 10's first five words, BATTLE.PRG 0x8008E95C): the zenith, the
    /// upper ring, the horizon, the lower ring and the nadir. Without a panorama the field is drawn over a sphere of
    /// 34 Gouraud corners in those bands (0x8008EB30, its corners at 0x80068C1C), turned with the camera and laid on
    /// the screen without perspective (0x8009820C); five equal colours fill the screen with one. Null without any.
    /// </summary>
    private JsonArray? SkyColours(int room)
    {
        int at = SubSection(room, 10, out int length);
        if (length < 20) return null;
        var colours = new JsonArray();
        bool any = false;
        for (int i = 0; i < 5; i++)
        {
            colours.Add(new JsonArray { (int)_map[at + i * 4], (int)_map[at + i * 4 + 1], (int)_map[at + i * 4 + 2] });
            any |= (BitConverter.ToUInt32(_map, at + i * 4) & 0xFFFFFF) != 0;
        }
        return any ? colours : null;
    }

    /// <summary>
    /// The room's weather (sub-section 10, BATTLE.PRG 0x80090C2C on loading the room): its kind at 0x34 (1 and 2 rain,
    /// 3 another fall, 4 rising embers, 0 none), the rectangle it falls in at 0x48 (x, z, width, depth), and the ranges
    /// its wind's direction (0x36 to 0x38), intensity (0x3c to 0x3e) and fall angle (0x42 to 0x44) wander within, each
    /// stepping every 16, 32, 64 or 128 frames by its rate index (0x3a, 0x40 and 0x46).
    /// </summary>
    private JsonObject? Weather(int room)
    {
        int at = SubSection(room, 10, out int length);
        if (length < 0x50) return null;
        short S(int offset) => BitConverter.ToInt16(_map, at + offset);
        return new JsonObject
        {
            ["type"] = (int)S(0x34),
            ["area"] = new JsonArray { (int)S(0x48), (int)S(0x4A), (int)S(0x4C), (int)S(0x4E) },
            ["direction"] = new JsonArray { (int)S(0x36), (int)S(0x38) }, ["intensity"] = new JsonArray { (int)S(0x3C), (int)S(0x3E) },
            ["pitch"] = new JsonArray { (int)S(0x42), (int)S(0x44) }, ["rates"] = new JsonArray { (int)S(0x3A), (int)S(0x40), (int)S(0x46) },
        };
    }

    /// <summary>The room's workshop kind (sub-section 10, byte 0x30): 0 for none, else the row of the workshops' material table.</summary>
    private int WorkshopKind(int room)
    {
        int at = SubSection(room, 10, out int length);
        return length > 0x30 ? _map[at + 0x30] : 0;
    }

    /// <summary>The start of room sub-section <paramref name="index"/> (geometry 0, collision map 1, tile attributes 2, doors 3, ...).</summary>
    private int SubSection(int room, int index, out int length)
    {
        int at = room + RoomHeaderSize;
        for (int i = 0; i < index; i++) at += (int)BitConverter.ToUInt32(_map, room + i * 4);
        length = (int)BitConverter.ToUInt32(_map, room + index * 4);
        return at;
    }

    /// <summary>
    /// The tile attribute word of each collision-map tile, by row (z * width + x): bits 0 to 4 the surface, 10 to 12
    /// a blocking class (5 is blocked), 13 a tile that tests the room's type-2 event conditions, 14 a door tile, 16 a
    /// trap or lock and 17 a pit.
    /// </summary>
    private JsonArray TileAttributes(int room)
    {
        var words = new JsonArray();
        int at = SubSection(room, 2, out int length);
        for (int i = 0; i + 4 <= length; i += 4) words.Add((long)BitConverter.ToUInt32(_map, at + i));
        return words;
    }

    /// <summary>
    /// The room's doors (room sub-section 3, 12 bytes each). Bytes 0 and 1 are the destination zone (0 is this one)
    /// and room. The u16 at 2 holds this door's tile x (bits 0 to 4), z (5 to 9) and floor level (10 to 12), and the
    /// destination level (13 to 15). The u16 at 4 holds the flag value a door needs (0 to 7), the destination tile x
    /// (8 to 12), the arrival facing quadrant (13 to 14) and a half-tile nudge (15). The u16 at 6 holds the flag it
    /// tests (0 to 9), the destination tile z (10 to 14) and walk-in (15).
    /// </summary>
    private JsonArray Doors(int room)
    {
        var doors = new JsonArray();
        int at = SubSection(room, 3, out int length);
        for (int i = 0; i + 12 <= length; i += 12)
        {
            int p = at + i, own = BitConverter.ToUInt16(_map, p + 2), to = BitConverter.ToUInt16(_map, p + 4), gate = BitConverter.ToUInt16(_map, p + 6);
            doors.Add(new JsonObject
            {
                ["zone"] = (int)_map[p], ["map"] = (int)_map[p + 1],
                ["tile"] = new JsonArray { own & 31, (own >> 5) & 31 }, ["level"] = (own >> 10) & 7,
                ["destination"] = new JsonArray { (to >> 8) & 31, (gate >> 10) & 31 }, ["destination_level"] = own >> 13,
                ["facing"] = ((to >> 13) & 3) * 1024, ["half"] = (to & 0x8000) != 0, ["walk_in"] = (gate & 0x8000) != 0,
                ["flag"] = gate & 0x3FF, ["flag_value"] = to & 0xFF, ["marker"] = (int)_map[p + 8],
            });
        }
        return doors;
    }

    /// <summary>
    /// The room's crates and blocks: the MPD actor list's records of a type other than 0 (40 bytes each). Each holds a
    /// state flag (u16 at 4, low ten bits) whose value must lie from byte 6 to byte 7 for it to be there, a slot, the
    /// tile (bytes 10 and 11), a byte at 12 whose bit 0 is the floor level and whose high nibble how many crates up it
    /// stands, a facing quadrant (13), and a model word (14): its kind in the top four bits (0 and 1 the textured
    /// crate, 2 and 4 to 6 plain blocks; 4 and up can be carried, 5 and 6 stacked, 1 breaks after the moves in bits 8
    /// to 11), and with the facing a material that turns kinds 0 and 1 to one of 24 orientations.
    /// The models are BATTLE.PRG's three (pointers at 0x800E8FB4, by kind through the bytes at 0x800E919C): a 16-byte
    /// header, then an SHP-like body whose part offsets sit at 0x30 (vertices of four s16, then pre-lit polygons: a
    /// triangle of 0x18 bytes with indices, a texel and three colours, then two texels; a quad of 0x20 bytes with four
    /// indices, four colours and four texels). They span 264 units and are drawn at half size, so a crate fills its
    /// tile. Their texture is the zone page at (384, 0), each kind in its own 64 by 32 cell (kind 6 shares kind 5's),
    /// through the zone palette at (768 + 16 × kind, 227).
    /// </summary>
    private JsonArray Crates(JsonArray models, JsonArray objects, SortedDictionary<string, (int Page, int Clut, int Mask)> surfacesTextures, int groupCount)
    {
        var crates = new JsonArray();
        if (_battle == null) return crates;
        int at = (int)BitConverter.ToUInt32(_map, 0x20), length = (int)BitConverter.ToUInt32(_map, 0x24);
        const uint Base = 0x80068800;
        int Address(uint address) => (int)(address - Base);
        for (int record = at; record + 40 <= at + length; record += 40)
        {
            if (_map[record] == 0) continue;
            int model = BitConverter.ToUInt16(_map, record + 14), kind = model >> 12;
            if (kind > 6) continue;
            int index = _battle[Address(0x800E919C) + kind];
            int pointer = (int)(BitConverter.ToUInt32(_battle, Address(0x800E8FB4) + index * 4) - Base);
            int body = pointer + 0x10, polygons = (int)BitConverter.ToUInt32(_battle, pointer + 8);
            int vertexAt = body + (int)BitConverter.ToUInt32(_battle, body + 0x38), polygonAt = body + (int)BitConverter.ToUInt32(_battle, body + 0x3C);
            int cell = Math.Min(kind, 5), cellU = cell % 3 * 64, cellV = cell / 3 * 32;
            int page = 384 / 64, clut = (768 + 16 * kind) / 16 | 227 << 6;
            string key = $"p{page:x3}_c{clut:x4}";
            surfacesTextures[key] = (page, clut, 0);
            // Kinds 0 and 1 stand in one of 24 orientations (see CrateTurns), which the field applies to the model.
            int material = ((model >> 3) & 0x1C) | (_map[record + 13] & 3);
            int orientation = kind < 2 && material < 24 ? material : -1;
            var surface = new JsonObject { ["texture"] = key, ["blend"] = "opaque", ["cull"] = "back", ["unshaded"] = true, ["vertex_color_srgb"] = true, ["colour_gain"] = 2, ["vertices"] = new JsonArray(), ["uv"] = new JsonArray(), ["colors"] = new JsonArray() };
            int cursor = polygonAt;
            for (int p = 0; p < polygons; p++)
            {
                bool quad = _battle[cursor + 11] == 0x3C;
                int corners = quad ? 4 : 3;
                var corner = new (Vector3 Position, int U, int V, int Colour)[corners];
                for (int c = 0; c < corners; c++)
                {
                    int vertex = vertexAt + BitConverter.ToUInt16(_battle, cursor + c * 2) / 4 * 8;
                    var position = new Vector3(BitConverter.ToInt16(_battle, vertex), BitConverter.ToInt16(_battle, vertex + 2), BitConverter.ToInt16(_battle, vertex + 4)) * 0.5f;
                    int texel = quad ? cursor + 0x18 + c * 2 : c == 0 ? cursor + 6 : cursor + 0x12 + c * 2;
                    corner[c] = (position, _battle[texel], _battle[texel + 1], cursor + 8 + c * 4);
                }
                // A face sampling one texel throughout takes its centre, as the rooms' faces do.
                bool flat = true;
                for (int k = 1; k < (quad ? 4 : 3); k++) flat &= corner[k].Item2 == corner[0].Item2 && corner[k].Item3 == corner[0].Item3;
                float centre = flat ? 0.5f : 0;
                foreach (int c in quad ? new[] { 0, 1, 2, 1, 3, 2 } : new[] { 0, 1, 2 })
                {
                    var (position, u, v, colour) = corner[c];
                    // Flat lists (x, y, z; u, v; r, g, b, a per vertex), which the field reads in one piece.
                    foreach (float value in new[] { Round(position.X / UnitsPerMetre), Round(-position.Y / UnitsPerMetre), Round(-position.Z / UnitsPerMetre) }) surface["vertices"]!.AsArray().Add(value);
                    foreach (float value in new[] { Round((u + cellU + centre) / 256f), Round((v + cellV + centre) / 256f) }) surface["uv"]!.AsArray().Add(value);
                    foreach (float value in new[] { Round(_battle[colour] / 256f), Round(_battle[colour + 1] / 256f), Round(_battle[colour + 2] / 256f), 1f }) surface["colors"]!.AsArray().Add(value);
                }
                cursor += quad ? 0x20 : 0x18;
            }
            // Geometry keeps its original group ids, including empty groups that produced no object. Crates start
            // after that entire range so neither geometry nor group commands can address a crate by accident.
            int id = groupCount + crates.Count;
            int x = _map[record + 10], z = _map[record + 11], stack = _map[record + 12] >> 4;
            objects.Add(new JsonObject { ["id"] = id, ["model"] = models.Count, ["position"] = new JsonArray { 0, 0, 0 } });
            models.Add(new JsonObject { ["blocks"] = new JsonArray { new JsonObject { ["groups"] = new JsonArray { surface } } } });
            crates.Add(new JsonObject
            {
                ["id"] = id, ["kind"] = kind, ["tile"] = new JsonArray { x, z }, ["level"] = _map[record + 12] & 1, ["stack"] = stack,
                ["flag"] = BitConverter.ToUInt16(_map, record + 4) & 0x3FF, ["flag_min"] = (int)_map[record + 6], ["flag_max"] = (int)_map[record + 7],
                ["moves"] = kind == 1 ? (model >> 8) & 0xF : 0, ["orientation"] = orientation,
            });
        }
        return crates;
    }

    /// <summary>
    /// The 24 orientations of kinds 0 and 1: quarter turns about x, y and z (BATTLE.PRG 0x800E9030, four bytes each), in
    /// retail space, applied about y after x after z.
    /// </summary>
    private JsonArray CrateTurns()
    {
        var turns = new JsonArray();
        if (_battle == null) return turns;
        int at = (int)(0x800E9030 - 0x80068800);
        for (int i = 0; i < 24; i++) turns.Add(new JsonArray { (int)_battle[at + i * 4], (int)_battle[at + i * 4 + 1], (int)_battle[at + i * 4 + 2] });
        return turns;
    }

    /// <summary>
    /// The orientation a pushed crate of kind 0 or 1 rolls to: by orientation, then by the push's direction (0 -z, 1 -x,
    /// 2 +z, 3 +x), from BATTLE.PRG 0x800E8FD0 (the push at 0x800a3ec0).
    /// </summary>
    private JsonArray CrateRolls()
    {
        var rolls = new JsonArray();
        if (_battle == null) return rolls;
        int at = (int)(0x800E8FD0 - 0x80068800);
        for (int i = 0; i < 24; i++) rolls.Add(new JsonArray { (int)_battle[at + i * 4], (int)_battle[at + i * 4 + 1], (int)_battle[at + i * 4 + 2], (int)_battle[at + i * 4 + 3] });
        return rolls;
    }

    /// <summary>
    /// The room's chests (room sub-section 18, 0x234 bytes each): a header of s16s (tile x, z, floor level, the room
    /// object that animates as the lid opens, two run-time words, the lock (0 none, 1 key, 2 magic seal, 3 locked), the
    /// key's item id, the chest's index and the quadrant Ashley must face), then 0x220 bytes of loot. An index below
    /// 0x40 is a chest whose opening sets flag 0x3c0 + index; 0x40 and above is a container.
    /// The MPD's treasure section (offset and length at 0x28 and 0x2c) is the first chest's loot.
    /// </summary>
    private JsonArray Chests(int room)
    {
        var chests = new JsonArray();
        int at = SubSection(room, 18, out int length);
        int treasure = (int)BitConverter.ToUInt32(_map, 0x28), treasureLength = (int)BitConverter.ToUInt32(_map, 0x2C);
        for (int i = 0; i + 0x234 <= length; i += 0x234)
        {
            int p = at + i;
            short S(int offset) => BitConverter.ToInt16(_map, p + offset);
            var chest = new JsonObject
            {
                ["tile"] = new JsonArray { (int)S(0), (int)S(2) }, ["level"] = (int)S(4), ["object"] = (int)S(6),
                ["lock"] = (int)S(0xC), ["key"] = (int)S(0xE), ["index"] = (int)S(0x10), ["facing"] = S(0x12) * 1024,
            };
            int loot = i == 0 && treasureLength >= 0x220 ? treasure : p + 0x14;
            chest["loot"] = Loot(loot);
            chests.Add(chest);
        }
        return chests;
    }

    /// <summary>
    /// A loot block (0x220 bytes). Each entry opens with a difficulty mask (it drops when bit 0 is set, at normal
    /// difficulty) and three spare bytes: a weapon at 0 (its blade, grip and gems), a blade at 0xac, a grip at 0xdc,
    /// a shield at 0xf0, two pieces of armour at 0x170, an accessory at 0x1c8, a gem at 0x1f4, and three items at
    /// 0x214 (id, mask, count).
    /// </summary>
    private JsonObject Loot(int at)
    {
        bool Drops(int entry) => (_map[at + entry] & 1) != 0;
        var loot = new JsonObject();
        if (Drops(0) && _map[at + 4] != 0) loot["weapon"] = new JsonObject { ["blade"] = InventoryConversion.Blade(_map, at + 4), ["grip"] = InventoryConversion.Grip(_map, at + 4 + 0x2C), ["name"] = DialogueConversion.Decode(_map, at + 4 + 0x2C + 0x10 + 0x54).Text };
        if (Drops(0xAC) && _map[at + 0xB0] != 0) loot["blade"] = InventoryConversion.Blade(_map, at + 0xB0);
        if (Drops(0xDC) && BitConverter.ToUInt16(_map, at + 0xE0) != 0) loot["grip"] = InventoryConversion.Grip(_map, at + 0xE0);
        if (Drops(0xF0) && _map[at + 0xF4] != 0) loot["shield"] = InventoryConversion.Armour(_map, at + 0xF4);
        var armour = new JsonArray();
        for (int i = 0; i < 2; i++)
            if (Drops(0x170 + i * 0x2C) && _map[at + 0x174 + i * 0x2C] != 0) armour.Add(InventoryConversion.Armour(_map, at + 0x174 + i * 0x2C));
        if (Drops(0x1C8) && _map[at + 0x1CC] != 0) armour.Add(InventoryConversion.Armour(_map, at + 0x1CC));
        loot["armour"] = armour;
        if (Drops(0x1F4) && BitConverter.ToUInt16(_map, at + 0x1F8) != 0) loot["gem"] = InventoryConversion.Gem(_map, at + 0x1F8);
        var items = new JsonArray();
        for (int i = 0; i < 3; i++)
        {
            int p = at + 0x214 + i * 4, id = BitConverter.ToUInt16(_map, p), count = _map[p + 3];
            if (id != 0 && count != 0 && (_map[p + 2] & 1) != 0) items.Add(new JsonObject { ["id"] = id, ["count"] = count });
        }
        loot["items"] = items;
        return loot;
    }

    /// <summary>
    /// How an enemy gets from one zone of the room (tile attribute bits 23 to 26) to another (room sub-section 5, from its
    /// start: 16 by 16 words, to-zone by from-zone; BATTLE.PRG 0x800dfbcc): two connector tiles a word, each a halfword of
    /// z (bits 0 to 4), x (5 to 9), a door or event step (10), a direction (11 and 12) and steps less one (13 and 14); a
    /// word without bits 0x1be0 has none. Empty for a room without the section.
    /// </summary>
    private JsonArray ZoneLinks(int room)
    {
        var links = new JsonArray();
        int at = SubSection(room, 5, out int length);
        if (length >= 1024) for (int i = 0; i < 256; i++) links.Add((long)BitConverter.ToUInt32(_map, at + i * 4));
        return links;
    }

    /// <summary>
    /// Where enemies board the room's platforms (room sub-section 5 at 0x454, BATTLE.PRG's table at 0x800f5910, read by
    /// 0x800dfaf8, 0x800df9a8 and 0x800dfa54): 8 pairs of tiles (x bits 0 to 4, z 5 to 9), two entries a platform (2p
    /// and 2p + 1): the tile an enemy steps on from, and where platform p must stand for that. Empty for a room without
    /// the section.
    /// </summary>
    private JsonArray PlatformDocks(int room)
    {
        var docks = new JsonArray();
        int at = SubSection(room, 5, out int length);
        if (length < 0x454 + 32) return docks;
        for (int i = 0; i < 8; i++)
        {
            int access = BitConverter.ToUInt16(_map, at + 0x454 + i * 4), dock = BitConverter.ToUInt16(_map, at + 0x454 + i * 4 + 2);
            docks.Add(new JsonArray { access & 31, access >> 5 & 31, dock & 31, dock >> 5 & 31 });
        }
        return docks;
    }

    /// <summary>
    /// What the enemies' movement reads of the room beyond its tiles (BATTLE.PRG's battle start, 0x800e6f9c): the
    /// bounds they keep within (the word at 0x494 of room sub-section 5: bytes most x, most z, least x, least z), the
    /// steps out of each tile that are walled (the bytes from 0x498, one a tile by rows of the map's width: bit d the
    /// step in direction d), and the flyers' ceiling (the halfword at 0x56 of sub-section 10, in 64-unit levels, 0
    /// none). Bounds and walls are empty for a room without sub-section 5.
    /// </summary>
    private JsonObject EnemyMap(int room)
    {
        int at = SubSection(room, 5, out int length);
        SubSection(room, 2, out int words);
        int tiles = words / 4;
        var bounds = new JsonArray();
        var walls = new JsonArray();
        if (length >= 0x498)
        {
            for (int i = 0; i < 4; i++) bounds.Add((int)_map[at + 0x494 + i]);
            for (int i = 0; i < tiles && 0x498 + i < length; i++) walls.Add((int)_map[at + 0x498 + i]);
        }
        int info = SubSection(room, 10, out int infoLength);
        return new JsonObject { ["bounds"] = bounds, ["walls"] = walls, ["ceiling"] = infoLength >= 0x58 ? (int)BitConverter.ToInt16(_map, info + 0x56) : 0 };
    }

    /// <summary>
    /// The revealed-trap panel (BATTLE.PRG's quad at 0x800e8548, drawn by 0x8008e19c): its texture, the zone image at
    /// (384, 0) (retail uploads zone images 320 left and 256 down, to the page at (64, 256)) through the palette at
    /// (832, 226), and six 32-texel tiles along row 128: the panel at rest, then the five frames 0x8008e0a8 copies over
    /// it in turn.
    /// </summary>
    private static JsonObject TrapPanel(SortedDictionary<string, (int Page, int Clut, int Mask)> surfacesTextures)
    {
        int page = 384 / 64, clut = 832 / 16 | 226 << 6;
        string key = $"p{page:x3}_c{clut:x4}";
        surfacesTextures[key] = (page, clut, 0);
        var tiles = new JsonArray();
        for (int k = 0; k < 6; k++) tiles.Add(new JsonArray { 32 * k, 128, 32, 32 });
        return new JsonObject { ["texture"] = key, ["tiles"] = tiles };
    }

    /// <summary>
    /// The room's traps and magic circles (room sub-section 8, 12 bytes each): tile x, z and floor level, a type (0 to
    /// 15 traps, 16 a save circle), a word and a byte that is a circle's save location, or a trap's revealed flag (BATTLE.PRG reads +10 so).
    /// </summary>
    private JsonArray Traps(int room)
    {
        var traps = new JsonArray();
        int at = SubSection(room, 8, out int length);
        for (int i = 0; i + 12 <= length; i += 12)
        {
            int p = at + i;
            traps.Add(new JsonObject
            {
                ["tile"] = new JsonArray { (int)BitConverter.ToInt16(_map, p), (int)BitConverter.ToInt16(_map, p + 2) }, ["level"] = (int)BitConverter.ToInt16(_map, p + 4),
                ["type"] = (int)BitConverter.ToInt16(_map, p + 6), ["value"] = (int)BitConverter.ToInt16(_map, p + 8), ["location"] = (int)_map[p + 0xA],
            });
        }
        return traps;
    }

    /// <summary>
    /// The room's locked doors (room sub-section 9, 20 bytes each): tile x, z and level, a kind (0 one tile; 4 to 7
    /// two, the second towards -z, +x, +z or -x), the lock (0 unlocked, 1 key or sigil, 2 magic, 3 locked), the room
    /// animation that opens it, the key or sigil's item id and whether it already stands open.
    /// </summary>
    private JsonArray Locks(int room)
    {
        var locks = new JsonArray();
        int at = SubSection(room, 9, out int length);
        for (int i = 0; i + 20 <= length; i += 20)
        {
            int p = at + i;
            locks.Add(new JsonObject
            {
                ["tile"] = new JsonArray { (int)BitConverter.ToInt16(_map, p), (int)BitConverter.ToInt16(_map, p + 2) }, ["level"] = (int)BitConverter.ToInt16(_map, p + 4),
                ["kind"] = (int)BitConverter.ToInt16(_map, p + 6), ["lock"] = (int)_map[p + 8], ["object"] = (int)_map[p + 9], ["key"] = (int)BitConverter.ToInt16(_map, p + 0xC),
                ["opened"] = BitConverter.ToInt16(_map, p + 0xE) != 0, ["linked"] = (int)BitConverter.ToInt16(_map, p + 0x10), ["enabled"] = _map[p + 0xA] != 0,
            });
        }
        return locks;
    }

    /// <summary>
    /// The room's animated texture images: kind 1 of room sub-section 11 (BATTLE.PRG 0x80091fe8) copies VRAM every
    /// period (byte 7, plus one, frames). Its rectangle (0xC to 0x12: x, y in halfwords, then w, h) is in the zone
    /// images' own coordinates; the game also repeats each copy 320 left and 256 down, and again at half size, in
    /// copies of the textures this field does not draw. With word 8 clear it is a flipbook: frame n of byte 2
    /// (counting 1 up, from byte 1) is the cell n mod byte 3 across and n / byte 3 down of a sheet of such rectangles,
    /// copied onto it. Otherwise it scrolls: its right half is copied onto its left half, starting byte-8 rows further
    /// down each time and wrapping round. For every room texture whose page the copies touch, the frames of the
    /// touched area are rendered into one image, a frame a row, for the field to show in turn.
    /// </summary>
    private JsonArray TextureFrames(int room, SortedDictionary<string, (int Page, int Clut, int Mask)> textures, IDictionary<string, Image> images)
    {
        var result = new JsonArray();
        int at = SubSection(room, 11, out int length);
        int[] sizes = { 24, 20, 32, 0, 0, 0, 36 };
        int number = 0;
        for (int p = at; p + 8 <= at + length;)
        {
            int kind = _map[p + 6];
            if (kind >= sizes.Length || sizes[kind] == 0 || p + sizes[kind] > at + length) break;
            if (kind == 1 && _map[p + 5] == 0) AddFrames(p, number++);
            p += sizes[kind];
        }
        return result;

        void AddFrames(int record, int index)
        {
            int x = BitConverter.ToInt16(_map, record + 0xC), y = BitConverter.ToInt16(_map, record + 0xE);
            int w = BitConverter.ToInt16(_map, record + 0x10), h = BitConverter.ToInt16(_map, record + 0x12);
            int frame = _map[record + 1], count = _map[record + 2], columns = Math.Max(1, (int)_map[record + 3]);
            bool scrolling = BitConverter.ToUInt32(_map, record + 8) != 0;
            int speed = _map[record + 8];
            if (w <= 0 || h <= 0 || x < 0 || y < 0 || x + w > 1024 || y + h > 512) return;
            // Each step's copies: (source x, y, width, height, destination x, y).
            var steps = new List<List<(int, int, int, int, int, int)>>();
            int start = frame;
            for (int step = 0; step < 256; step++)
            {
                var copies = new List<(int, int, int, int, int, int)>();
                if (!scrolling)
                {
                    if (++frame > count) frame = 1;
                    int sx = x + w * (frame % columns), sy = y + h * (frame / columns);
                    copies.Add((sx, sy, w, h, x, y));
                }
                else
                {
                    frame += speed;
                    if (frame >= h) frame = 0;
                    copies.Add((x + w / 2, y + frame, w / 2, h - frame, x, y));
                    if (frame != 0) copies.Add((x + w / 2, y, w / 2, frame, x, y + h - frame));
                }
                steps.Add(copies);
                if (frame == start) break;
            }
            // The textures whose page holds a destination, and the touched area of each in its pixels.
            foreach (var (key, (page, clut, mask)) in textures)
            {
                int pageX = (page & 15) * 64, pageY = ((page >> 4) & 1) * 256, depth = (page >> 7) & 3, perWord = depth == 0 ? 4 : 2;
                int minU = 256, minV = 256, maxU = -1, maxV = -1;
                foreach (var copies in steps)
                    foreach (var (_, _, cw, ch, dx, dy) in copies)
                    {
                        int u0 = (dx - pageX) * perWord, u1 = (dx + cw - pageX) * perWord, v0 = dy - pageY, v1 = dy + ch - pageY;
                        if (u1 <= 0 || v1 <= 0 || u0 >= 256 || v0 >= 256 || cw <= 0 || ch <= 0) continue;
                        minU = Math.Min(minU, Math.Max(0, u0)); maxU = Math.Max(maxU, Math.Min(256, u1));
                        minV = Math.Min(minV, Math.Max(0, v0)); maxV = Math.Max(maxV, Math.Min(256, v1));
                    }
                if (maxU < 0) continue;
                int width = maxU - minU, height = maxV - minV;
                var vram = (ushort[])_vram.Clone();
                var strip = Image.CreateEmpty(width, height * steps.Count, false, Image.Format.Rgba8);
                for (int i = 0; i < steps.Count; i++)
                {
                    foreach (var (sx, sy, cw, ch, dx, dy) in steps[i])
                    {
                        // A copy reads its whole source before writing, as the GPU's does.
                        var source = new ushort[Math.Max(0, cw) * Math.Max(0, ch)];
                        for (int r = 0; r < ch; r++) for (int c = 0; c < cw; c++) source[r * cw + c] = vram[((sy + r) & 511) * 1024 + ((sx + c) & 1023)];
                        for (int r = 0; r < ch; r++) for (int c = 0; c < cw; c++) vram[((dy + r) & 511) * 1024 + ((dx + c) & 1023)] = source[r * cw + c];
                    }
                    strip.BlitRect(Page(vram, page, clut, mask), new Rect2I(minU, minV, width, height), new Vector2I(0, height * i));
                }
                string name = $"{_name}_{key}_frames{index}";
                images[name] = strip;
                result.Add(new JsonObject
                {
                    ["texture"] = key, ["rect"] = new JsonArray { minU, minV, width, height }, ["frames"] = steps.Count,
                    ["ticks_per_frame"] = 2 * (_map[record + 7] + 1), ["strip"] = name,
                });
            }
        }
    }

    /// <summary>
    /// The room's scripted group animations: room sub-section 12 (BATTLE.PRG's loader near 0x800926c4, interpreter at
    /// 0x80092b04), numbered from 0x40 for `79`. Each opens with a count of geometry groups and a running byte (2),
    /// the groups (padded to four bytes), then commands of a halfword size and a halfword operation until a size of 0.
    /// Operation 11 jumps back by its size. Each command's halfwords after the first two are its arguments.
    /// </summary>
    private JsonArray GroupScripts(int room)
    {
        var scripts = new JsonArray();
        int at = SubSection(room, 12, out int length), p = 0;
        while (p + 4 <= length)
        {
            int count = _map[at + p], skip = count % 4 == 0 ? count + 4 : count + 8 - (count & 3);
            var groups = new JsonArray();
            for (int i = 0; i < count; i++) groups.Add((int)_map[at + p + 4 + i]);
            bool running = _map[at + p + 2] != 0;
            int q = p + skip;
            var commands = new JsonArray();
            var starts = new List<int>();
            bool ended = false;
            while (q + 4 <= length)
            {
                int size = BitConverter.ToInt16(_map, at + q), op = BitConverter.ToInt16(_map, at + q + 2);
                if (size == 0) { q += 4; ended = true; break; }
                starts.Add(q);
                var args = new JsonArray();
                if (op == 11) { args.Add(q + size); q += 4; }
                else
                {
                    if (size < 4 || q + size > length) break;
                    for (int k = 4; k + 2 <= size; k += 2) args.Add((int)BitConverter.ToInt16(_map, at + q + k));
                    q += size;
                }
                commands.Add(new JsonObject { ["op"] = op, ["args"] = args });
            }
            if (!ended) break;
            // Jumps name the command they go back to.
            foreach (var command in commands)
                if ((int)command!["op"]! == 11) command["args"] = new JsonArray { Math.Max(0, starts.IndexOf((int)command["args"]![0]!)) };
            scripts.Add(new JsonObject { ["index"] = 0x40 + scripts.Count, ["groups"] = groups, ["running"] = running, ["commands"] = commands });
            p = q;
        }
        return scripts;
    }

    /// <summary>
    /// The room's swinging groups: kind 6 of room sub-section 11 (BATTLE.PRG 0x800923f8), started by script (`79 0 n`,
    /// n counting all of the sub-section's animations). Word 0 locates the group's header in the geometry; byte 7 the
    /// angle it turns (1 about y, 2 about x, else about z; 4096 a turn). Started, the angle sets off from word 0x1C
    /// with the velocity at 0x20; each frame it adds the velocity, and the velocity eight times word 0xC. Passing the
    /// target (0x18) closer than its velocity, the velocity turns back, shifted down by word 0x10; at 32 or less the
    /// group comes to rest on the target, and its start and target change places, so the next start swings it back.
    /// </summary>
    private JsonArray Swings(int room)
    {
        var swings = new JsonArray();
        int at = SubSection(room, 11, out int length), geometry = room + RoomHeaderSize;
        int[] sizes = { 24, 20, 32, 0, 0, 0, 36 };
        int index = 0;
        for (int p = at; p + 8 <= at + length; index++)
        {
            int kind = _map[p + 6];
            if (kind >= sizes.Length || sizes[kind] == 0 || p + sizes[kind] > at + length) break;
            if (kind == 6)
            {
                int header = (int)BitConverter.ToUInt32(_map, p);
                swings.Add(new JsonObject
                {
                    ["index"] = index, ["group"] = (header - 4) / GroupHeaderSize, ["axis"] = _map[p + 7] == 1 ? "y" : _map[p + 7] == 2 ? "x" : "z",
                    ["running"] = _map[p + 5] == 0, ["accel"] = BitConverter.ToInt32(_map, p + 0xC), ["damping"] = BitConverter.ToInt32(_map, p + 0x10),
                    ["target"] = BitConverter.ToInt32(_map, p + 0x18), ["start"] = BitConverter.ToInt32(_map, p + 0x1C), ["velocity"] = BitConverter.ToInt32(_map, p + 0x20),
                });
            }
            p += sizes[kind];
        }
        return swings;
    }

    /// <summary>
    /// The room's scrolling textures: room sub-section 11 lists animations of three kinds by byte 6 (1, 20 bytes; 2,
    /// 32 bytes; 6, 36 bytes; sizes from BATTLE.PRG 0x80068EE8), each started unless byte 5 is set. Kind 2 scrolls one
    /// face's texture (BATTLE.PRG 0x80091e10): word 0 is the face's offset in the geometry; every period (byte 7, plus
    /// one, frames) its offset (bytes 0xC, 0xD) moves by a signed step (0x10, 0x11) within a window (from bytes 8 and
    /// 9, 0xA and 0xB wide, the offset taking 0xE and 0xF of it): below the window it comes round to its far end, and
    /// past the far end back to the start. The face's texture coordinates are a base (bytes 0x14 to 0x17 for u, 0x18
    /// to 0x1B for v) plus that offset. Returned by face offset, as the field's texture-offset cycle (in 256ths, about
    /// the face's stored coordinates) on the 60 Hz presentation clock.
    /// </summary>
    private Dictionary<int, JsonObject> FaceScrolls(int room)
    {
        var scrolls = new Dictionary<int, JsonObject>();
        int at = SubSection(room, 11, out int length), geometry = room + RoomHeaderSize;
        int[] sizes = { 24, 20, 32, 0, 0, 0, 36 };
        for (int p = at; p + 8 <= at + length;)
        {
            int kind = _map[p + 6];
            if (kind >= sizes.Length || sizes[kind] == 0 || p + sizes[kind] > at + length) break;
            if (kind == 2 && _map[p + 5] == 0)
            {
                int face = (int)BitConverter.ToUInt32(_map, p), stored = geometry + face;
                if (stored + 0x28 <= _map.Length)
                {
                    int u = _map[p + 0xC], v = _map[p + 0xD];
                    int referenceU = (_map[stored + 0x18] - _map[p + 0x14]) & 255, referenceV = (_map[stored + 0x19] - _map[p + 0x18]) & 255;
                    var offsets = new JsonArray();
                    int startU = u, startV = v;
                    for (int step = 0; step < 512; step++)
                    {
                        u = Wrap(u + (sbyte)_map[p + 0x10], _map[p + 8], _map[p + 0xA], _map[p + 0xE]);
                        v = Wrap(v + (sbyte)_map[p + 0x11], _map[p + 9], _map[p + 0xB], _map[p + 0xF]);
                        offsets.Add(new JsonArray { Round((sbyte)(byte)(u - referenceU) / 256f), Round((sbyte)(byte)(v - referenceV) / 256f) });
                        if (u == startU && v == startV) break;
                    }
                    scrolls[face] = new JsonObject { ["offsets"] = offsets, ["ticks_per_frame"] = 2 * (_map[p + 7] + 1) };
                }
            }
            p += sizes[kind];
        }
        return scrolls;
        static int Wrap(int position, int start, int span, int size)
        {
            if (position < start) return start + span - size;
            if (start + span < position + size) return start;
            return position;
        }
    }

    /// <summary>
    /// The room's platforms (room sub-section 17, 184 bytes each): geometry groups that move. Byte 0 is the group (0xFF
    /// none). In the first word, bit 8 marks a lift, which moves only while someone rides it (others move on their
    /// own), bit 9 one switched off, whose group is not drawn, bits 10 and 11 the last waypoint, bit 26 the direction
    /// and bits 27 and 28 the waypoint it travels to first. From byte 8 come four waypoints of 16 bytes (x, y and z in
    /// 4096ths of a map unit, y up, then the frames it waits there), from 0x48 up to six legs of 16 bytes (a velocity a frame
    /// and the frames it takes, in the order it travels: out to the last waypoint and back), and at 0xA8 where it
    /// starts (BATTLE.PRG's update near 0x8008c9a8). Positions here are map units, y down; the footprint is its group's
    /// extent about that point. Byte 5's low two bits designate the door stop; the word at 0xB4 holds the platform's
    /// tile attributes (0x8008b764). Reaching that stop sets its door bit until departure (0x8008cc3c..0x8008ccfc).
    /// </summary>
    private JsonArray Platforms(int room)
    {
        var platforms = new JsonArray();
        int at = SubSection(room, 17, out int length);
        JsonArray Point(int p, float scale) => new() { Round(BitConverter.ToInt32(_map, p) * scale), Round(-BitConverter.ToInt32(_map, p + 4) * scale), Round(BitConverter.ToInt32(_map, p + 8) * scale) };
        for (int i = 0; i + 184 <= length; i += 184)
        {
            int p = at + i, group = _map[p];
            uint flags = BitConverter.ToUInt32(_map, p);
            int last = (int)(flags >> 10) & 3;
            var waypoints = new JsonArray();
            for (int w = 0; w <= last; w++)
            {
                var point = Point(p + 8 + w * 16, 1 / 4096f);
                point.Add((int)BitConverter.ToUInt16(_map, p + 8 + w * 16 + 12));
                waypoints.Add(point);
            }
            var legs = new JsonArray();
            for (int l = 0; l < Math.Max(1, last * 2); l++)
            {
                var velocity = Point(p + 0x48 + l * 16, 1 / 4096f);
                velocity.Add(BitConverter.ToInt32(_map, p + 0x48 + l * 16 + 12));
                legs.Add(velocity);
            }
            var footprint = new JsonArray();
            if (_groupBounds.TryGetValue(group, out var bounds) && bounds.MinX <= bounds.MaxX)
                footprint = new JsonArray { bounds.MinX, bounds.MaxX, bounds.MinY, bounds.MaxY, bounds.MinZ, bounds.MaxZ };
            platforms.Add(new JsonObject
            {
                ["group"] = group, ["off"] = (flags & 0x200) != 0, ["lift"] = (flags & 0x100) != 0, ["start"] = Point(p + 0xA8, 1 / 4096f),
                ["target"] = (int)(flags >> 27) & 3, ["forward"] = (flags & 1u << 26) != 0, ["leg"] = (int)(flags >> 29) & 7,
                ["door_stop"] = (_map[p + 5] & 3), ["attributes"] = (long)BitConverter.ToUInt32(_map, p + 0xB4),
                ["waypoints"] = waypoints, ["legs"] = legs, ["footprint"] = footprint,
            });
        }
        return platforms;
    }

    /// <summary>Lengths of the event-condition tests, by opcode, including the opcode.</summary>
    private static readonly int[] ConditionLengths = { 0, 3, 5, 4, 3, 4, 5, 5, 3, 1, 5, 6, 5, 5, 7, 4, 3 };

    /// <summary>
    /// The room's event conditions (the MPD section at bytes 8 and 12): up to 16 u16 offsets ending at 0xFFFF, each
    /// to a record of a trigger mask, a u16 event (the top bit marks one of the room's own events, otherwise an
    /// EVENT file), then tests until 0xFF; 0xFE negates the test after it. When every test holds, the event starts.
    /// Tests keep their bytes after the opcode.
    /// </summary>
    private JsonArray Conditions()
    {
        var records = new JsonArray();
        int at = (int)BitConverter.ToUInt32(_map, 8), length = (int)BitConverter.ToUInt32(_map, 12);
        if (length < 2 || at + length > _map.Length) return records;
        for (int i = 0; i < 16 && BitConverter.ToUInt16(_map, at + i * 2) != 0xFFFF; i++)
        {
            int p = at + BitConverter.ToUInt16(_map, at + i * 2), end = at + length;
            int types = _map[p], eventId = BitConverter.ToUInt16(_map, p + 1);
            p += 3;
            var tests = new JsonArray();
            bool negate = false;
            while (p < end && _map[p] != 0xFF)
            {
                int op = _map[p];
                if (op == 0xFE) { negate = true; p++; continue; }
                if (op >= ConditionLengths.Length || ConditionLengths[op] == 0) throw new InvalidDataException($"The room has an unknown event condition {op:x2}.");
                var bytes = new JsonArray();
                for (int b = 1; b < ConditionLengths[op]; b++) bytes.Add((int)_map[p + b]);
                tests.Add(new JsonObject { ["op"] = op, ["negate"] = negate, ["bytes"] = bytes });
                negate = false;
                p += ConditionLengths[op];
            }
            records.Add(new JsonObject { ["types"] = types, ["event"] = eventId & 0x7FFF, ["local"] = (eventId & 0x8000) != 0, ["tests"] = tests });
        }
        return records;
    }

    /// <summary>Where the room's own event <paramref name="index"/> starts: the MPD section at bytes 24 and 28 opens with a u16 offset table.</summary>
    public int LocalEvent(int index)
    {
        int at = (int)BitConverter.ToUInt32(_map, 24), length = (int)BitConverter.ToUInt32(_map, 28);
        if (index * 2 + 2 > length) throw new InvalidDataException($"The room has no local event {index}.");
        return at + BitConverter.ToUInt16(_map, at + index * 2);
    }

    /// <summary>The size of a retail character record: a zone's character templates and Ashley's starting record in INITBTL.PRG share the layout.</summary>
    public const int CharacterRecordSize = 0x464;
    /// <summary>Ashley's record in INITBTL.PRG, which loads at 0x800F9800.</summary>
    public const int AshleyRecord = unchecked((int)(0x800FAB18u - 0x800F9800u));

    /// <summary>
    /// Reads a character record:
    /// <list type="bullet">
    /// <item>its character id (the u16 at 0; BATTLE.PRG copies it to the actor's +0x1a, 0x80076f24, where the AI
    /// tells Guildenstern's final form, 173, by it);</item>
    /// <item>the model (low byte of the u16 at 2) and reach (low five bits of byte 3);</item>
    /// <item>hit and magic points (s16 at 0x1C and 0x1E);</item>
    /// <item>strength, intelligence and agility (bytes 0x20 to 0x22);</item>
    /// <item>two movement profiles, each flags and a speed (0x24 and 0x25, 0x26 and 0x27): flags 0x42 let it walk and
    /// 0x84 run at the profile's speed (BATTLE.PRG 0x800def8c), 0x08 hop up to 128 units a point of speed, 0x10 and
    /// 0x20 fly at it (0x800e6828);</item>
    /// <item>the status immunities (the word at 0x28), the AI parameter word (0x2C) and the limb a whole-body hit lands on (0x32);</item>
    /// <item>the weapon: the blade's equipment record at 0x34 and the grip's at 0x64, each 0x30 bytes. An
    /// equipment record holds its weapon model (byte 3), category (4), signed strength, intelligence and agility
    /// (5 to 7), material (0x13) and range (x, y, z, angle at 0x18).</item>
    /// </list>
    /// </summary>
    public static JsonObject CharacterRecord(byte[] data, int at)
    {
        if (at < 0 || at + CharacterRecordSize > data.Length) throw new System.IO.InvalidDataException("A character record is truncated.");
        int blade = at + 0x34, grip = at + 0x64;
        return new JsonObject
        {
            ["id"] = (int)BitConverter.ToUInt16(data, at),
            ["model"] = (int)data[at + 2],
            ["reach"] = data[at + 3] & 31,
            ["hp"] = (int)BitConverter.ToInt16(data, at + 0x1C),
            ["mp"] = (int)BitConverter.ToInt16(data, at + 0x1E),
            ["strength"] = (int)data[at + 0x20],
            ["intelligence"] = (int)data[at + 0x21],
            ["agility"] = (int)data[at + 0x22],
            ["moves"] = new JsonArray { new JsonArray { (int)data[at + 0x24], (int)data[at + 0x25] }, new JsonArray { (int)data[at + 0x26], (int)data[at + 0x27] } },
            ["ai"] = (long)BitConverter.ToUInt32(data, at + 0x2C),
            // The statuses it cannot take (its data's +0x944; a negative word also makes it immune to magic, status bit
            // 31, BATTLE.PRG 0x80076cec).
            ["immunity"] = (long)BitConverter.ToUInt32(data, at + 0x28),
            // The limb a blow or spell on the whole character lands on (BATTLE.PRG copies it to the actor's +0x36).
            ["default_limb"] = (int)data[at + 0x32],
            // The two limbs whose loss keeps it slowed as an enemy reckons ahead (copied to its data's +0x34 and +0x35,
            // BATTLE.PRG 0x80076a5c; 0x800de104), and what its moves add to its recovery (byte 0x23's low six bits,
            // its data's +0x954 bits 18 to 23; 0x800859b4).
            ["slowing_limbs"] = new JsonArray { (int)data[at + 0x30], (int)data[at + 0x31] },
            ["recovery"] = data[at + 0x23] & 0x3F,
            ["class"] = data[at + 3] >> 5,
            ["name"] = DialogueConversion.Decode(data, at + 4).Text,
            ["weapon"] = new JsonObject
            {
                ["id"] = (int)BitConverter.ToUInt16(data, blade),
                ["model"] = (int)data[blade + 3],
                ["category"] = (int)data[blade + 4],
                ["strength"] = (sbyte)data[blade + 5] + (sbyte)data[grip + 5],
                ["agility"] = (sbyte)data[blade + 7] + (sbyte)data[grip + 7],
                ["intelligence"] = (sbyte)data[blade + 6] + (sbyte)data[grip + 6],
                ["material"] = (int)data[at + 0x34 + 0xF0],
                ["range"] = new JsonArray { (int)data[blade + 0x18], (int)data[blade + 0x19], (int)data[blade + 0x1A], (int)data[blade + 0x1B] },
                ["damage_type"] = (int)data[blade + 0x10], ["cost_kind"] = (int)data[blade + 0x11], ["cost"] = (int)data[blade + 0x12],
                ["dp"] = (int)BitConverter.ToUInt16(data, blade + 8), ["max_dp"] = (int)BitConverter.ToUInt16(data, blade + 0xA),
                ["pp"] = (int)BitConverter.ToUInt16(data, blade + 0xC), ["max_pp"] = (int)BitConverter.ToUInt16(data, blade + 0xE),
                ["types"] = Bytes(data, grip + 0x1C, 4), ["classes"] = Bytes(data, blade + 0x20, 8), ["affinities"] = Bytes(data, blade + 0x28, 8),
            },
            ["shield"] = Shield(data, at + 0x140),
            ["accessory"] = Equipment(data, at + 0x204),
            ["limbs"] = Limbs(data, at + 0x238),
            ["drops"] = Drops(data, at),
        };
    }

    /// <summary>
    /// What a character may drop when felled (BATTLE.PRG's loot roll), each with its rate out of
    /// 255 and its record as the inventory keeps it: the weapon (blade at 0x34, grip at 0x64, its rate at 0x125, its
    /// name at 0x128), the shield (0x140, rate 0x201), the accessory (0x204, rate 0x234) and each limb's armour (the
    /// limb's equipment record at 0x20, rate at 0x51). An equipment record (0x30 bytes) gives the id, the model (3),
    /// category (4), strength, intelligence and agility (5 to 7), DP and PP (8 to 0xE), damage type (0x10), cost
    /// (0x12), material (0x13), gem slots (0x15), range (0x18), and the type, class and affinity values (0x1C, 0x20,
    /// 0x28).
    /// </summary>
    private static JsonObject Drops(byte[] data, int at)
    {
        JsonObject Common(int p) => new()
        {
            ["id"] = (int)BitConverter.ToUInt16(data, p), ["model"] = (int)data[p + 3], ["category"] = (int)data[p + 4],
            ["strength"] = (int)(sbyte)data[p + 5], ["intelligence"] = (int)(sbyte)data[p + 6], ["agility"] = (int)(sbyte)data[p + 7],
            ["dp"] = (int)BitConverter.ToUInt16(data, p + 8), ["max_dp"] = (int)BitConverter.ToUInt16(data, p + 0xA),
            ["pp"] = (int)BitConverter.ToUInt16(data, p + 0xC), ["max_pp"] = (int)BitConverter.ToUInt16(data, p + 0xE),
        };
        JsonObject Armour(int p, int material)
        {
            var record = Common(p);
            record["gem_slots"] = (int)data[p + 0x15];
            record["types"] = Bytes(data, p + 0x1C, 4); record["classes"] = Bytes(data, p + 0x20, 8); record["affinities"] = Bytes(data, p + 0x28, 8);
            record["material"] = material;
            return record;
        }
        int blade = at + 0x34, grip = at + 0x64;
        var weapon = Common(blade);
        weapon["damage_type"] = data[blade + 0x10] & 3; weapon["cost_kind"] = (int)data[blade + 0x11]; weapon["cost"] = (int)data[blade + 0x12];
        weapon["range"] = new JsonArray { (int)data[blade + 0x18], (int)data[blade + 0x19], (int)data[blade + 0x1A], data[blade + 0x1B] & 7 };
        weapon["classes"] = Bytes(data, blade + 0x20, 8); weapon["affinities"] = Bytes(data, blade + 0x28, 8);
        weapon["material"] = (int)data[at + 0x34 + 0xF0];
        var hilt = new JsonObject
        {
            ["id"] = (int)BitConverter.ToUInt16(data, grip), ["category"] = (int)data[grip + 4], ["gem_slots"] = (int)data[grip + 0x15],
            ["strength"] = (int)(sbyte)data[grip + 5], ["intelligence"] = (int)(sbyte)data[grip + 6], ["agility"] = (int)(sbyte)data[grip + 7],
            ["types"] = Bytes(data, grip + 0x1C, 4),
        };
        var armour = new JsonArray();
        for (int i = 0; i < 6; i++)
        {
            int p = at + 0x238 + i * 92;
            if (BitConverter.ToUInt16(data, p + 0x20) != 0) armour.Add(new JsonObject { ["rate"] = (int)data[p + 0x51], ["record"] = Armour(p + 0x20, data[p + 0x50]) });
        }
        var drops = new JsonObject { ["armour"] = armour };
        if (data[blade] != 0)
            drops["weapon"] = new JsonObject { ["rate"] = (int)data[at + 0x34 + 0xF1], ["blade"] = weapon, ["grip"] = hilt, ["name"] = DialogueConversion.Decode(data, at + 0x34 + 0xF4).Text };
        if (BitConverter.ToUInt16(data, at + 0x140) != 0) drops["shield"] = new JsonObject { ["rate"] = (int)data[at + 0x140 + 0xC1], ["record"] = Armour(at + 0x140, data[at + 0x140 + 0xC0]) };
        if (BitConverter.ToUInt16(data, at + 0x204) != 0) drops["accessory"] = new JsonObject { ["rate"] = (int)data[at + 0x234], ["record"] = Armour(at + 0x204, 0) };
        return drops;
    }

    /// <summary>
    /// The shield (0xc4 bytes): an equipment record, its three gem slots, and its material at 0xc0. Like a blade, it
    /// keeps its model at 3 and its category at 4.
    /// </summary>
    private static JsonObject Shield(byte[] data, int at)
    {
        var shield = Equipment(data, at);
        shield["model"] = (int)data[at + 3];
        shield["category"] = (int)data[at + 4];
        shield["material"] = (int)data[at + 0xC0];
        return shield;
    }

    private static JsonArray Bytes(byte[] data, int at, int count)
    {
        var values = new JsonArray();
        for (int i = 0; i < count; i++) values.Add((int)(sbyte)data[at + i]);
        return values;
    }

    /// <summary>
    /// An equipment record (0x30 bytes): id at 0, signed strength, intelligence and agility at 5 to 7, DP at 8 and
    /// 0xa, PP at 0xc and 0xe, and the type, class and affinity values at 0x1c, 0x20 and 0x28.
    /// </summary>
    private static JsonObject Equipment(byte[] data, int at) => new()
    {
        ["id"] = (int)BitConverter.ToUInt16(data, at),
        ["strength"] = (int)(sbyte)data[at + 5], ["intelligence"] = (int)(sbyte)data[at + 6], ["agility"] = (int)(sbyte)data[at + 7],
        ["dp"] = (int)BitConverter.ToUInt16(data, at + 8), ["max_dp"] = (int)BitConverter.ToUInt16(data, at + 0xA),
        ["pp"] = (int)BitConverter.ToUInt16(data, at + 0xC), ["max_pp"] = (int)BitConverter.ToUInt16(data, at + 0xE),
        ["types"] = Bytes(data, at + 0x1C, 4), ["classes"] = Bytes(data, at + 0x20, 8), ["affinities"] = Bytes(data, at + 0x28, 8),
    };

    /// <summary>
    /// The six limbs (92 bytes each): hit points, an agility bonus to evade, chain evasion, four type and eight
    /// affinity defences, four actions (id and slot), the limb's armour (an equipment record) and the share of a
    /// hit's damage each limb takes when this one is struck.
    /// </summary>
    private static JsonArray Limbs(byte[] data, int at)
    {
        var limbs = new JsonArray();
        for (int i = 0; i < 6; i++)
        {
            int p = at + i * 92;
            var actions = new JsonArray();
            for (int a = 0; a < 4; a++) actions.Add((int)BitConverter.ToUInt16(data, p + 0x10 + a * 4));
            limbs.Add(new JsonObject
            {
                ["hp"] = (int)BitConverter.ToUInt16(data, p), ["agility"] = (int)(sbyte)data[p + 2], ["chain_evasion"] = (int)data[p + 3],
                ["types"] = Bytes(data, p + 4, 4), ["affinities"] = Bytes(data, p + 8, 8), ["actions"] = actions,
                ["armour"] = Equipment(data, p + 0x20), ["split"] = Bytes(data, p + 0x54, 6),
            });
        }
        return limbs;
    }

    /// <summary>
    /// The characters the room places itself when it loads: the MPD's actor list (offset and length at bytes 32 and
    /// 36, 40 bytes a record) of which character template in the zone each slot holds. A ZND template (0x464 bytes,
    /// after the zone's unit file list) names its model in the low byte of the u16 at byte 2. A record gives the slot,
    /// the tile, the floor level, a facing quadrant, a stance (1 is weapon drawn) and tests that keep it out of the
    /// room: hidden, the story-stage flags 1 and 2, flag 0x82, or a flag or the player's HP against a value.
    /// </summary>
    private JsonArray RoomActors()
    {
        var actors = new JsonArray();
        int at = (int)BitConverter.ToUInt32(_map, 0x20), length = (int)BitConverter.ToUInt32(_map, 0x24);
        int units = (int)BitConverter.ToUInt32(_zone, 8), unitsLength = (int)BitConverter.ToUInt32(_zone, 12);
        if (length < 40 || unitsLength < 4 || at + length > _map.Length) return actors;
        int templates = (int)BitConverter.ToUInt32(_zone, units), first = units + 4 + templates * 8;
        for (int record = at; record + 40 <= at + length; record += 40)
        {
            // Type 0 is a character; other types place objects.
            if (_map[record] != 0) continue;
            int template = _map[record + 4];
            // Template 0x7f is an actor without a model (0x80077330), used as an interaction target.
            int unit = 0;
            var stats = new JsonObject();
            if (template != 0x7F)
            {
                if (template >= templates || first + (template + 1) * 0x464 > _zone.Length) throw new InvalidDataException("A room actor names a missing character template.");
                // The template's last word indexes the zone's unit files (sector, size), which hold its model, weapon and
                // animations.
                unit = (int)BitConverter.ToUInt32(_zone, first + template * 0x464 + 0x460);
                if (unit >= templates) throw new InvalidDataException("A room actor's template names a missing unit file.");
                // The record's word at 0x14 goes to the actor's +0x34 (BATTLE.PRG 0x80077c30): with bit 0 its bits 1 to 11
                // take the place of the AI word's bits 21 to 31 (0x800e5a74); bit 13 lets its planner fall back on its last
                // resorts, bit 12 has it weigh the moves flagged 0x10 without reckoning them (0x800e5dc8).
                stats = CharacterRecord(_zone, first + template * CharacterRecordSize);
                uint planning = BitConverter.ToUInt32(_map, record + 0x14);
                if ((planning & 1) != 0) stats["ai"] = (long)(((uint)(long)stats["ai"]! & 0x1FFFFFu) | (planning & 0xFFE) << 20);
                stats["last_resorts"] = (planning >> 13 & 1) != 0;
                // Bit 14 chooses how often a patroller turns back on its route (BATTLE.PRG's table at 0x80069C08, 0x800e5dd4).
                stats["patrol_turns"] = (int)(planning >> 14 & 1);
                stats["weighs_lightly"] = (planning >> 12 & 1) != 0;
                // The record's word at 0x10 goes to the actor's +0x30: bits 4 to 8 choose its behaviour routine (BATTLE.PRG's
                // table at 0x800F1790, 0x800e5e70), bits 0 to 3 are its group and bits 9 to 13 the group of the leader it
                // keeps by (0x800e5998).
                uint behaviour = BitConverter.ToUInt32(_map, record + 0x10);
                stats["behaviour"] = (int)(behaviour >> 4 & 0x1F);
                stats["group"] = (int)(behaviour & 0xF);
                stats["leader"] = (int)(behaviour >> 9 & 0x1F);
                // Bits 14 and 15 are the room's patrol route it walks (0x800e5e04).
                stats["route"] = (int)(behaviour >> 14 & 3);
                // Bits 22 to 26 and 27 to 31 are the home tile it wanders about and bits 16 to 21 how far (0x800e5e14, 63 a
                // free wander); byte 0x26 has it lie dormant until stirred (0x800e6538).
                stats["home"] = new JsonArray { (int)(behaviour >> 22 & 0x1F), (int)(behaviour >> 27) };
                stats["home_radius"] = (int)(behaviour >> 16 & 0x3F);
                stats["dormant"] = _map[record + 0x26] != 0;
            }
            actors.Add(new JsonObject
            {
                ["virtual"] = template == 0x7F,
                ["unit_sector"] = template == 0x7F ? 0L : (long)BitConverter.ToUInt32(_zone, units + 4 + unit * 8),
                ["stats"] = stats,
                ["slot"] = (int)_map[record + 5],
                ["character"] = template == 0x7F ? 127 : (int)_zone[first + template * 0x464 + 2],
                ["tile"] = new JsonArray { (int)_map[record + 12], (int)_map[record + 14] },
                ["level"] = _map[record + 13] & 1,
                ["facing"] = _map[record + 15] * 1024 % 4096,
                ["stance"] = (int)_map[record + 0x23],
                ["marked"] = _map[record + 0x24] != 0,
                // Its items: two it always drops (id at 0x1A and 0x1C, count at 0x20 and 0x21) and one at a rate (id at
                // 0x1E, rate out of 255 at 0x22).
                ["items"] = new JsonArray
                {
                    new JsonObject { ["id"] = (int)BitConverter.ToUInt16(_map, record + 0x1A), ["count"] = (int)_map[record + 0x20], ["rate"] = 256 },
                    new JsonObject { ["id"] = (int)BitConverter.ToUInt16(_map, record + 0x1C), ["count"] = (int)_map[record + 0x21], ["rate"] = 256 },
                    new JsonObject { ["id"] = (int)BitConverter.ToUInt16(_map, record + 0x1E), ["count"] = 1, ["rate"] = (int)_map[record + 0x22] },
                },
                ["hidden"] = _map[record + 2] != 0,
                ["scene_test"] = (int)_map[record + 0x27],
                ["flag_test"] = (int)_map[record + 6], ["flag_value"] = (int)BitConverter.ToUInt16(_map, record + 8),
                ["stat_test"] = (int)_map[record + 7], ["stat_value"] = (int)BitConverter.ToUInt16(_map, record + 10),
            });
        }
        return actors;
    }

    /// <summary>
    /// The tile rectangle the gameplay camera's look-at stays within (min x, min z, max x, max z), from the room's last
    /// header section. A room without one uses its collision map's size.
    /// </summary>
    private JsonArray CameraBounds(int room, JsonObject floor)
    {
        int at = room + RoomHeaderSize, length = (int)BitConverter.ToUInt32(_map, room + 23 * 4);
        for (int section = 0; section < 23; section++) at += (int)BitConverter.ToUInt32(_map, room + section * 4);
        if (length >= 8 && at + 8 <= _map.Length)
            return new JsonArray { BitConverter.ToInt16(_map, at), BitConverter.ToInt16(_map, at + 2), BitConverter.ToInt16(_map, at + 4), BitConverter.ToInt16(_map, at + 6) };
        int columns = floor.ContainsKey("columns") ? (int)floor["columns"]! / 4 : 0, rows = floor.ContainsKey("rows") ? (int)floor["rows"]! / 4 : 0;
        return new JsonArray { 0, 0, columns, rows };
    }

    /// <summary>Each geometry group's extent about its origin (x, y and z, least then greatest), read with its faces.</summary>
    private readonly Dictionary<int, (int MinX, int MaxX, int MinY, int MaxY, int MinZ, int MaxZ)> _groupBounds = new();
    private int _boundsGroup;
    private readonly HashSet<string> _opaqueFaces = new(StringComparer.Ordinal);

    private void ReadFace(int at, bool quad, int scale, (int X, int Y, int Z) origin, SortedDictionary<string, JsonObject> surfaces, SortedDictionary<string, (int, int, int)> textures, string suffix = "")
    {
        int corners = quad ? 4 : 3;
        var position = new (int X, int Y, int Z)[corners];
        position[0] = (BitConverter.ToInt16(_map, at), BitConverter.ToInt16(_map, at + 2), BitConverter.ToInt16(_map, at + 4));
        int[] deltaOffsets = { 6, 9, 32 };
        for (int corner = 1; corner < corners; corner++)
        {
            int d = at + deltaOffsets[corner - 1];
            position[corner] = (position[0].X + (sbyte)_map[d] * scale, position[0].Y + (sbyte)_map[d + 1] * scale, position[0].Z + (sbyte)_map[d + 2] * scale);
        }
        var extent = _groupBounds[_boundsGroup];
        foreach (var (px, py, pz) in position)
            extent = (Math.Min(extent.MinX, px), Math.Max(extent.MaxX, px), Math.Min(extent.MinY, py), Math.Max(extent.MaxY, py), Math.Min(extent.MinZ, pz), Math.Max(extent.MaxZ, pz));
        _groupBounds[_boundsGroup] = extent;
        // The room is drawn through an ordering table, where of two identical opaque faces facing the same way the
        // earlier group's is drawn last, over the other (MAP005 has its floor's centre twice, lit warm and cold). A
        // later copy is set half a unit behind its face (the side its winding's normal points to), so it shows only
        // where the earlier one is hidden.
        var behind = Vector3.Zero;
        {
            var (ax, ay, az) = position[0];
            var u = new Vector3(position[1].X - ax, position[1].Y - ay, position[1].Z - az);
            var v = new Vector3(position[2].X - ax, position[2].Y - ay, position[2].Z - az);
            var normal = u.Cross(v);
            var placed = new (int, int, int)[corners];
            for (int corner = 0; corner < corners; corner++) placed[corner] = (origin.X + position[corner].X, origin.Y + position[corner].Y, origin.Z + position[corner].Z);
            Array.Sort(placed);
            string face = $"{string.Join(';', placed)}/{Math.Sign(normal.X)}{Math.Sign(normal.Y)}{Math.Sign(normal.Z)}";
            if ((_map[at + 15] & 0x02) == 0 && !_opaqueFaces.Add(face) && normal != Vector3.Zero) behind = normal.Normalized() * 0.5f;
        }
        int[] colourOffsets = { 12, 16, 20, 36 };
        // Texture coordinates are stored rotated by one corner relative to positions: p1 uses the second pair.
        (int U, int V)[] stored = { (_map[at + 19], _map[at + 23]), (_map[at + 24], _map[at + 25]), (_map[at + 28], _map[at + 29]), quad ? (_map[at + 35], _map[at + 39]) : (0, 0) };
        (int U, int V)[] uv = quad ? new[] { stored[1], stored[2], stored[0], stored[3] } : new[] { stored[1], stored[2], stored[0] };
        int type = _map[at + 15], clut = BitConverter.ToUInt16(_map, at + 26), page = BitConverter.ToUInt16(_map, at + 30);
        bool semi = (type & 0x02) != 0;
        // Bit 0 marks a double-sided polygon: the room renderer draws it facing away too (BATTLE.PRG 0x800973C8) and
        // clears the bit before the GPU sees it (0x8009756C), so every room polygon is modulated by its colours.
        bool twoSided = (type & 0x01) != 0;
        int rate = (page >> 5) & 3;
        string blend = !semi ? "opaque" : rate switch { 0 => "mix", 2 => "subtract", _ => "add" };
        // Quarter-additive polygons add a quarter of their texel colour.
        float gain = semi && rate == 3 ? 0.25f : 1f;
        // The GPU blends a semi-transparent polygon's texels only where their mask bit is set; the rest it draws
        // opaque. Half-and-half polygons carry that in alpha (the masked texels at half). An additive or subtractive
        // polygon is drawn twice: its unmasked texels opaque, and its masked texels blended.
        var passes = new List<(string Blend, int Mask, float Gain)>();
        if (blend is "add" or "subtract") { passes.Add(("opaque", 2, 1f)); passes.Add((blend, 3, gain)); }
        else passes.Add((blend, blend == "mix" ? 1 : 0, gain));
        var targets = new List<(JsonObject Surface, float Gain)>();
        foreach (var (passBlend, mask, passGain) in passes)
        {
            string key = $"p{page & 0x1FF:x3}_c{clut:x4}" + mask switch { 1 => "_s", 2 => "_o", 3 => "_m", _ => "" };
            textures[key] = (page & 0x1FF, clut, mask);
            string surfaceKey = key + "_" + passBlend + (twoSided ? "_both" : "") + suffix;
            if (!surfaces.TryGetValue(surfaceKey, out var surface))
            {
                surface = new JsonObject { ["texture"] = key, ["blend"] = passBlend, ["cull"] = twoSided ? "disabled" : "back", ["unshaded"] = true, ["vertex_color_srgb"] = true, ["colour_gain"] = 2, ["vertices"] = new JsonArray(), ["uv"] = new JsonArray(), ["colors"] = new JsonArray(), ["faces"] = new JsonArray(), ["order_corners"] = new JsonArray() };
                surfaces[surfaceKey] = surface;
            }
            targets.Add((surface, passGain));
        }
        // Horizontal polygons record the room's baked light on each 128-unit tile they cover.
        bool level = true;
        for (int corner = 1; corner < corners; corner++) level &= position[corner].Y == position[0].Y;
        if (level)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            float r = 0, g = 0, b = 0;
            for (int corner = 0; corner < corners; corner++)
            {
                minX = Math.Min(minX, origin.X + position[corner].X); maxX = Math.Max(maxX, origin.X + position[corner].X);
                minZ = Math.Min(minZ, origin.Z + position[corner].Z); maxZ = Math.Max(maxZ, origin.Z + position[corner].Z);
                int c = at + colourOffsets[corner];
                r += _map[c]; g += _map[c + 1]; b += _map[c + 2];
            }
            for (int tx = minX >> 7; tx <= (maxX - 1) >> 7; tx++)
                for (int tz = minZ >> 7; tz <= (maxZ - 1) >> 7; tz++)
                {
                    var tile = (tx, tz, origin.Y + position[0].Y);
                    var sum = _floorLight.GetValueOrDefault(tile);
                    _floorLight[tile] = (sum.R + r / corners, sum.G + g / corners, sum.B + b / corners, sum.Count + 1);
                }
        }
        int[] order = quad ? new[] { 0, 1, 2, 1, 3, 2 } : new[] { 0, 1, 2 };
        // The GPU takes texel (u, v) at a corner's (u, v); a face whose corners all share one samples that texel
        // throughout (MAP026's black boxes, all at (248, 248)), which here is its centre, not the edge it shares with
        // the texel before (transparent there).
        bool single = true;
        for (int k = 1; k < uv.Length; k++) single &= uv[k] == uv[0];
        float centre = single ? 0.5f : 0;
        // Each face's triangles (a quad's two), so that the field can drop a face whole as the retail renderer does.
        foreach (var (surface, _) in targets) surface["faces"]!.AsArray().Add(quad ? 2 : 1);
        foreach (var (surface, passGain) in targets)
            foreach (int corner in order)
            {
                float x = position[corner].X + behind.X, y = position[corner].Y + behind.Y, z = position[corner].Z + behind.Z;
                // Retail space is x right, y down, z forward; a half turn about x gives the engine's y-up space.
                // Flat lists (x, y, z; u, v; r, g, b, a per vertex), which the field reads in one piece.
                foreach (float value in new[] { Round(x / UnitsPerMetre), Round(-y / UnitsPerMetre), Round(-z / UnitsPerMetre) }) surface["vertices"]!.AsArray().Add(value);
                // Each primitive goes in the room ordering table at its farthest corner (0x80097ab4,
                // the fourth corner at 0x8009787c), not at each fragment's interpolated depth.
                for (int k = 0; k < 4; k++)
                {
                    var p = position[Math.Min(k, corners - 1)];
                    foreach (float value in new[] { Round((p.X + behind.X) / UnitsPerMetre), Round(-(p.Y + behind.Y) / UnitsPerMetre), Round(-(p.Z + behind.Z) / UnitsPerMetre) }) surface["order_corners"]!.AsArray().Add(value);
                }
                foreach (float value in new[] { Round((uv[corner].U + centre) / 256f), Round((uv[corner].V + centre) / 256f) }) surface["uv"]!.AsArray().Add(value);
                int c = at + colourOffsets[corner];
                int r = _map[c], g = _map[c + 1], b = _map[c + 2];
                foreach (float value in new[] { Round(r * passGain / 256f), Round(g * passGain / 256f), Round(b * passGain / 256f), 1f }) surface["colors"]!.AsArray().Add(value);
            }
    }

    private static float Round(float value) => MathF.Round(value, 5);

    /// <summary>
    /// Decodes the room's collision map (the room sub-section after the geometry). Its header gives the width and depth
    /// in 128-unit tiles, a base height and the cell count per layer. Two layers follow, floor then ceiling, one u16
    /// per tile: the top five bits are a height base and the low eleven bits index a 16-byte block that follows the
    /// layers, one signed byte per 32-unit sub-cell of the tile's 4x4. A sub-cell's height is (base + byte) &amp; 0x7F, its
    /// world height (map base - height * 2) * 8, and the byte's top bit is a flag. Returns the engine walkmesh, walkable
    /// where the floor lies below the ceiling, the floor heights by sub-cell for placing actors, and by sub-cell whether
    /// its floor byte has the flag (bit 15 of what BATTLE.PRG's 0x8008dc7c returns for it: "no floor" to the enemies).
    /// </summary>
    private (JsonArray Walkmeshes, JsonObject Floor) Collision(int room)
    {
        int at = room + RoomHeaderSize + (int)BitConverter.ToUInt32(_map, room);
        int length = (int)BitConverter.ToUInt32(_map, room + 4);
        if (length < 8) return (new JsonArray(), new JsonObject());
        int width = BitConverter.ToInt16(_map, at), depth = BitConverter.ToInt16(_map, at + 2), baseHeight = BitConverter.ToInt16(_map, at + 4);
        int cells = width * depth, grid = at + 8, blocks = grid + cells * 4;
        int Height(int layer, int x, int z, out bool flag)
        {
            int cell = BitConverter.ToInt16(_map, grid + (layer * cells + (z >> 2) * width + (x >> 2)) * 2);
            int block = blocks + (cell & 0x7FF) * 16, value = (sbyte)_map[block + (x & 3) + (z & 3) * 4];
            flag = (value & 0x80) != 0;
            return (baseHeight - ((((cell & 0xF800) >> 9) + value) & 0x7F) * 2) * 8;
        }
        var vertices = new JsonArray();
        var triangles = new JsonArray();
        var heights = new JsonArray();
        var marks = new JsonArray();
        var ceilings = new JsonArray();
        for (int z = 0; z < depth * 4; z++)
            for (int x = 0; x < width * 4; x++)
            {
                int floorY = Height(0, x, z, out bool marked), ceilingY = Height(1, x, z, out _);
                heights.Add(floorY);
                marks.Add(marked ? 1 : 0);
                ceilings.Add(ceilingY);
                // Retail y is down: a floor is walkable when it lies below its ceiling.
                if (floorY <= ceilingY) continue;
                int first = vertices.Count;
                foreach (var (cx, cz) in new[] { (x, z), (x + 1, z), (x + 1, z + 1), (x, z + 1) })
                    vertices.Add(new JsonArray { Round(cx * 32 / UnitsPerMetre), Round(-floorY / UnitsPerMetre), Round(-cz * 32 / UnitsPerMetre) });
                triangles.Add(new JsonObject { ["vertices"] = new JsonArray { first, first + 2, first + 1 } });
                triangles.Add(new JsonObject { ["vertices"] = new JsonArray { first, first + 3, first + 2 } });
            }
        var walkmeshes = new JsonArray { new JsonObject { ["vertices"] = vertices, ["triangles"] = triangles } };
        return (walkmeshes, new JsonObject { ["columns"] = width * 4, ["rows"] = depth * 4, ["cell"] = 32, ["heights"] = heights, ["marks"] = marks, ["ceilings"] = ceilings });
    }

    /// <summary>
    /// Decodes a 256x256 texture page through one palette. Colour 0x0000 is the hardware's transparent texel.
    /// On semi-transparent polygons only texels with the mask bit blend. <paramref name="mask"/> 1 gives those half
    /// alpha (exactly the hardware's half-and-half mode) and leaves the rest opaque; 2 keeps only the unmasked texels
    /// and 3 only the masked ones, for the two draws of an additive or subtractive polygon; 0 ignores the bit.
    /// </summary>
    private Image Page(int page, int clut, int mask) => Page(_vram, page, clut, mask);

    private static Image Page(ushort[] vram, int page, int clut, int mask)
    {
        int pageX = (page & 15) * 64, pageY = ((page >> 4) & 1) * 256, depth = (page >> 7) & 3;
        int clutX = (clut & 63) * 16, clutY = (clut >> 6) & 0x1FF;
        if (depth > 1) throw new InvalidDataException("A room polygon uses a direct-colour texture page.");
        var data = new byte[256 * 256 * 4];
        for (int v = 0; v < 256; v++)
            for (int u = 0; u < 256; u++)
            {
                int index = depth == 0
                    ? (vram[(pageY + v) * 1024 + Math.Min(1023, pageX + u / 4)] >> ((u & 3) * 4)) & 15
                    : (vram[(pageY + v) * 1024 + Math.Min(1023, pageX + u / 2)] >> ((u & 1) * 8)) & 255;
                ushort colour = vram[clutY * 1024 + Math.Min(1023, clutX + index)];
                CharacterConversion.Put(data, (v * 256 + u) * 4, colour, mask, 1);
            }
        return Image.CreateFromData(256, 256, false, Image.Format.Rgba8, data);
    }
}
