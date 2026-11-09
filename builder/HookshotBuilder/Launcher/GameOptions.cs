using System.IO;
using System.Text;

namespace HookshotBuilder.Launcher;

/// One row of the in-game menu (gpu/vk/vk_menu.cpp rows[]): its ini key is the row's name in lower case with spaces as
/// underscores, the value an int clamped to lo..hi. The launcher shows the same rows with the same choices.
sealed record OptionRow(string Tab, string Name, string Key, int Default, int Lo, int Hi, int Step, string[]? Choices, string Help)
{
    /// (vk_menu.cpp: changing these keeps the quality preset; any other row sets it back to Custom)
    public bool KeepsPreset => Key is "fullscreen" or "fps_counter" or "gpu_device" or "invert_camera_x" or "invert_camera_y"
                                  or "mouse_camera" or "mouse_sensitivity" or "invert_mouse_vertical" or "layout_preset" or "quality_preset";
}

/// hookshot_options.ini in a game folder, read and written the way the game does: the menu's rows (vk_menu.cpp menu_init and
/// save) and the controls (host/nn_hid.c binds_load). Lines the launcher does not know are kept.
sealed class GameOptions
{
    public const string FileName = "hookshot_options.ini";

    public static readonly OptionRow[] Rows =
    {
        new("Graphics", "Quality preset", "quality_preset", 0, 0, 5, 1,
            new[] { "Custom", "Minimum (4GB)", "Low (6GB)", "Balanced (8GB)", "High (12GB)", "Ultra (16GB+)" },
            "Sets resolution, upscaling, sharpness, frame generation, pacing, game speed, the handheld profile and video memory limits for a card with that much memory."),
        new("Graphics", "Fullscreen", "fullscreen", 0, 0, 1, 1, new[] { "Off", "On" },
            "Fills the whole screen with the game, without the window's border. The same as F11 in the game."),
        new("Graphics", "GPU device", "gpu_device", 0, 0, 8, 1, null,
            "Which graphics card Vulkan uses. Auto picks the best dedicated GPU. Only cards with Vulkan 1.3 are listed."),
        new("Graphics", "Frame generation", "frame_generation", 0, 0, 8, 1, new[] { "Off", "", "2x", "3x", "4x", "5x", "6x", "7x", "8x" },
            "Makes extra frames between the game's own for smoother motion. Adds a little input delay; fast motion can show artifacts."),
        new("Graphics", "Frame pacing", "frame_pacing", 0, 0, 1, 1, new[] { "Off", "On" },
            "Shows each frame at an even interval instead of as soon as it is ready. Less stutter, up to one frame of delay."),
        new("Graphics", "Resolution", "resolution", 0, 0, 4, 1, new[] { "Auto", "100%", "75%", "67%", "50%" },
            "How sharp the 3D is drawn. Auto lets the game lower it when the GPU is busy. A fixed percentage stays steady: lower is faster."),
        new("Graphics", "Upscaling", "upscaling", 0, 0, 2, 1, new[] { "Off", "Catmull-Rom", "Lanczos" },
            "The filter that scales the picture to your window. Catmull-Rom is sharper, Lanczos sharper still."),
        new("Graphics", "Sharpness", "sharpness", 0, 0, 100, 10, null,
            "Sharpens the picture as it goes to the window, with or without upscaling. Too much shows halos around edges."),
        new("Graphics", "Handheld profile", "handheld_profile", 0, 0, 1, 1, new[] { "Off", "On" },
            "Runs the game as if the console were undocked: lower resolution, lighter on the GPU."),
        new("Graphics", "Ultrawide", "ultrawide", 0, 0, 1, 1, new[] { "16:9", "Monitor's shape" },
            "Widens the camera to fill a screen wider than 16:9. The HUD is still stretched."),
        new("Graphics", "Robust buffers", "robust_buffers", 0, 0, 2, 1, new[] { "Auto", "Off", "On" },
            "A read past a buffer's end is dropped instead of stopping the GPU. Auto is on for every card; Off can be faster on some NVIDIA cards."),

        new("Gameplay", "FPS counter", "fps_counter", 0, 0, 1, 1, new[] { "Off", "On" },
            "Frames per second in the top right corner. With frame generation on, it also shows the game's own rate."),
        new("Gameplay", "Game speed", "game_speed", 1, 0, 1, 1, new[] { "Per frame", "Real time" },
            "Per frame: the game slows down whenever it runs below 60 fps. Real time: it keeps normal speed down to 15 fps."),
        new("Gameplay", "Text speed", "text_speed", 0, 0, 3, 1, new[] { "Normal", "2x", "4x", "Instant" },
            "How fast the letters of dialogue appear. Instant shows each page at once."),

        new("Controls", "Controller in background", "controller_in_background", 0, 0, 1, 1, new[] { "Off", "On" },
            "The controller keeps playing while another window has the focus. The keyboard and mouse still need the game's window."),
        new("Controls", "Mouse camera", "mouse_camera", 0, 0, 1, 1, new[] { "As the stick", "Native" },
            "Native turns the camera with the mouse one to one, as in a PC game. As the stick uses the mouse in place of the right stick."),
        new("Controls", "Mouse sensitivity", "mouse_sensitivity", 100, 10, 500, 10, null,
            "How far the camera turns for a mouse movement. The - and = keys change it in the game too."),
        new("Controls", "Invert mouse vertical", "invert_mouse_vertical", 0, 0, 1, 1, new[] { "Off", "On" },
            "Moving the mouse forward tilts the camera down."),
        new("Controls", "Invert camera X", "invert_camera_x", 0, 0, 1, 1, new[] { "Off", "On" },
            "Inverts left and right on the right stick."),
        new("Controls", "Invert camera Y", "invert_camera_y", 0, 0, 1, 1, new[] { "Off", "On" },
            "Inverts up and down on the right stick."),
        new("Controls", "Layout preset", "layout_preset", 0, 0, 3, 1, new[] { "Nintendo", "Xbox / PC", "Swap Face", "Custom" },
            "Nintendo has A on the right face button; Xbox / PC and Swap Face put A on the bottom one. Changing a button yourself makes it Custom."),
    };

    public static OptionRow Row(string key) => Rows.First(r => r.Key == key);

    // ---- the controls (gpu/vk/vk_api.h HOS_SW_*, HOS_KEY_*; host/nn_hid.c bind_names, pad_default, key_default) ----
    public const int SwitchButtons = 16;   // HOS_SW_N: pad_ lines for these only
    public static readonly string[] BindNames =
    {
        "a", "b", "x", "y", "ls", "rs", "l", "r", "zl", "zr", "plus", "minus", "left", "up", "right", "down",
        "move_up", "move_down", "move_left", "move_right", "cam_up", "cam_down", "cam_left", "cam_right",
    };
    // (HOS_PAD_*: 0 south, 1 east, 2 west, 3 north, 4 back, 5 guide, 6 start, 7 lstick, 8 rstick, 9 lshoulder, 10 rshoulder,
    // 11 up, 12 down, 13 left, 14 right, 15 ltrigger, 16 rtrigger)
    public const int PadInputs = 17;
    static readonly int[] PadDefault = { 1, 0, 3, 2, 7, 8, 9, 10, 15, 16, 6, 4, 13, 11, 14, 12 };
    static readonly int[][] KeyDefault =
    {
        new[] { 'E', 0 }, new[] { 0x10, 0 }, new[] { 0x20, 0 }, new[] { 0x01, 0 }, new[] { 0x11, 0 }, new[] { 'C', 0 },
        new[] { 'Q', 0 }, new[] { 'R', 0 }, new[] { 'Z', 0x02 }, new int[] { 'X', 'F' }, new[] { 0x09, 0x0D }, new[] { 'M', 0 },
        new[] { '1', 0 }, new[] { '2', 0 }, new[] { '3', 0 }, new[] { '4', 0 }, new[] { 'W', 0 }, new[] { 'S', 0 },
        new[] { 'A', 0 }, new[] { 'D', 0 }, new[] { 0x26, 'I' }, new[] { 0x28, 'K' }, new[] { 0x25, 'J' }, new[] { 0x27, 'L' },
    };

    readonly Dictionary<string, int> values = new();
    public readonly int[] Pad = new int[SwitchButtons];
    public readonly int[,] Keys = new int[BindNames.Length, 2];
    readonly List<string> unknown = new();   // (lines the launcher does not own: written back as they were)

    public string Path { get; }
    public bool Existed { get; private set; }

    GameOptions(string path)
    {
        Path = path;
        foreach (var r in Rows) values[r.Key] = r.Default;
        ResetControls();
    }

    public int this[string key]
    {
        get => values[key];
        set { var r = Row(key); values[key] = Math.Clamp(value, r.Lo, r.Hi); }
    }

    /// The game's defaults, for a folder whose file could not be read (saving writes a new one)
    public static GameOptions Defaults(string folder) => new(System.IO.Path.Combine(folder, FileName));

    public static GameOptions Load(string folder)
    {
        var o = new GameOptions(System.IO.Path.Combine(folder, FileName));
        if (!File.Exists(o.Path)) return o;
        o.Existed = true;
        int version = 0, oldDynres = -1;
        bool hadResolution = false, hadKeys = false;
        foreach (var raw in File.ReadAllLines(o.Path))
        {
            var eq = raw.IndexOf('=');
            if (eq < 0) { if (raw.Trim().Length > 0) o.unknown.Add(raw); continue; }
            var key = raw[..eq];
            var val = raw[(eq + 1)..];
            if (Rows.FirstOrDefault(r => r.Key == key) is { } row) { o[key] = Atoi(val); hadResolution |= key == "resolution"; continue; }
            if (key == "options_version") { version = Atoi(val); continue; }
            if (key == "dynamic_resolution") { oldDynres = Atoi(val); continue; }
            var pad = key.StartsWith("pad_");
            var keys = key.StartsWith("key_");
            var i = pad || keys ? Array.IndexOf(BindNames, key[4..]) : -1;
            if (i < 0 || (pad && i >= SwitchButtons)) { o.unknown.Add(raw); continue; }
            if (pad)
            {
                var v = Atoi(val);
                o.Pad[i] = v >= 0 && v < PadInputs ? v : -1;
            }
            else
            {
                hadKeys = true;
                var comma = val.IndexOf(',');
                int a = Atoi(val), b = comma >= 0 ? Atoi(val[(comma + 1)..]) : 0;
                o.Keys[i, 0] = a is > 0 and < 256 ? a : 0;
                o.Keys[i, 1] = b is > 0 and < 256 ? b : 0;
            }
        }
        // (menu_init: the row before Resolution, and Game speed's old default)
        if (!hadResolution && oldDynres >= 0) o["resolution"] = oldDynres != 0 ? 0 : 1;
        if (version == 0) o["game_speed"] = 1;
        if (o["frame_generation"] == 1) o["frame_generation"] = 2;
        // (binds_load: a file from before version 3 had the mouse's buttons implied for Y and ZL)
        if (version < 3 && hadKeys)
            foreach (var (button, vk) in new[] { (3, 0x01), (8, 0x02) })
            {
                var taken = false;
                for (var i = 0; i < BindNames.Length; i++) taken |= o.Keys[i, 0] == vk || o.Keys[i, 1] == vk;
                if (!taken && o.Keys[button, 1] == 0) o.Keys[button, 1] = vk;
            }
        return o;
    }

    /// (the C library's atoi: leading spaces, a sign, digits, the rest ignored)
    static int Atoi(string s)
    {
        s = s.TrimStart();
        var i = 0;
        var neg = false;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) neg = s[i++] == '-';
        long v = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i]) && v < int.MaxValue) v = v * 10 + (s[i++] - '0');
        return (int)Math.Clamp(neg ? -v : v, int.MinValue, int.MaxValue);
    }

    public void Save()
    {
        var sb = new StringBuilder();
        foreach (var r in Rows) sb.Append(r.Key).Append('=').Append(values[r.Key]).Append('\n');
        sb.Append("options_version=3\n");
        for (var i = 0; i < BindNames.Length; i++)
        {
            if (i < SwitchButtons) sb.Append("pad_").Append(BindNames[i]).Append('=').Append(Pad[i]).Append('\n');
            sb.Append("key_").Append(BindNames[i]).Append('=').Append(Keys[i, 0]).Append(',').Append(Keys[i, 1]).Append('\n');
        }
        foreach (var line in unknown) sb.Append(line).Append('\n');
        // (written beside, then moved over: a game reading it at the same moment sees the old file or the new, never half)
        var temp = Path + ".tmp";
        File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
        Existed = true;
    }

    // ---- what the menu does when a row changes ----

    /// A row set by the player: the preset's values when it is the preset, else the preset goes back to Custom where the
    /// game's menu does that; the layout preset rebinds the face buttons
    public void Set(string key, int value)
    {
        this[key] = value;
        if (key == "quality_preset") ApplyPreset(this[key]);
        else if (key == "layout_preset") ApplyLayout(this[key]);
        else if (!Row(key).KeepsPreset) this["quality_preset"] = 0;
    }

    /// (vk_menu.cpp apply_preset)
    void ApplyPreset(int p)
    {
        (int res, int up, int sharp, int fg, int hh)? s = p switch
        {
            1 => (4, 2, 50, 2, 1),
            2 => (2, 2, 35, 2, 1),
            3 => (1, 1, 25, 2, 0),
            4 => (1, 2, 30, 2, 0),
            5 => (1, 2, 40, 3, 0),
            _ => null,
        };
        if (s is not { } v) return;
        this["resolution"] = v.res;
        this["upscaling"] = v.up;
        this["sharpness"] = v.sharp;
        this["frame_generation"] = v.fg;
        this["frame_pacing"] = 1;
        this["game_speed"] = 1;
        this["handheld_profile"] = v.hh;
    }

    // (vk_menu.cpp apply_pad_layout: A, B, X, Y onto HOS_PAD_* positions)
    static readonly int[][] Layouts = { new[] { 1, 0, 3, 2 }, new[] { 0, 1, 2, 3 }, new[] { 0, 1, 3, 2 } };

    void ApplyLayout(int layout)
    {
        if (layout is < 0 or > 2) return;
        for (var i = 0; i < 4; i++) Pad[i] = Layouts[layout][i];
    }

    /// (vk_menu.cpp detect_pad_layout)
    public int DetectLayout()
    {
        for (var l = 0; l < Layouts.Length; l++)
            if (Pad[0] == Layouts[l][0] && Pad[1] == Layouts[l][1] && Pad[2] == Layouts[l][2] && Pad[3] == Layouts[l][3]) return l;
        return 3;
    }

    /// (nn_hid.c hos_bind, the gamepad: the input moved here leaves its old button this one's input)
    public void BindPad(int button, int input)
    {
        var old = Pad[button];
        if (input >= 0)
            for (var i = 0; i < SwitchButtons; i++)
                if (i != button && Pad[i] == input) Pad[i] = old;
        Pad[button] = input;
        this["layout_preset"] = DetectLayout();
    }

    /// (nn_hid.c hos_bind, a key: taken from whatever had it)
    public void BindKey(int button, int slot, int vk)
    {
        if (vk != 0)
            for (var i = 0; i < BindNames.Length; i++)
                for (var k = 0; k < 2; k++)
                    if (Keys[i, k] == vk) Keys[i, k] = 0;
        Keys[button, slot] = vk;
    }

    public void ResetControls()
    {
        for (var i = 0; i < BindNames.Length; i++)
        {
            if (i < SwitchButtons) Pad[i] = PadDefault[i];
            Keys[i, 0] = KeyDefault[i][0];
            Keys[i, 1] = KeyDefault[i][1];
        }
        if (values.Count > 0) values["layout_preset"] = 0;
    }

    public void ResetAll()
    {
        foreach (var r in Rows) values[r.Key] = r.Default;
        ResetControls();
    }
}
