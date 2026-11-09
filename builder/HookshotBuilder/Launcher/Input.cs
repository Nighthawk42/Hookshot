using System.Runtime.InteropServices;
using System.Windows.Input;

namespace HookshotBuilder.Launcher;

/// Windows key codes as the game binds and names them (gpu/vk/vk_menu.cpp bindable_keys and key_name)
static class KeyCodes
{
    static readonly HashSet<int> Bindable = BuildBindable();

    static HashSet<int> BuildBindable()
    {
        var k = new HashSet<int>();
        for (var c = 'A'; c <= 'Z'; c++) k.Add(c);
        for (var c = '0'; c <= '9'; c++) k.Add(c);
        for (var c = 0x71; c <= 0x7B; c++) k.Add(c);   // F2..F12 (F1 opens the in-game menu)
        for (var c = 0x60; c <= 0x69; c++) k.Add(c);   // the number pad's digits
        foreach (var c in new[] { 0x20, 0x10, 0x11, 0x12, 0x09, 0x0D, 0x08, 0x14, 0x26, 0x28, 0x25, 0x27, 0x24, 0x23, 0x21, 0x22,
                                  0x6B, 0x6D, 0x6A, 0x6F, 0x6E, 0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0, 0xDB, 0xDC, 0xDD, 0xDE,
                                  0x01, 0x02, 0x04, 0x05, 0x06 })
            k.Add(c);
        return k;
    }

    /// The game's code for a key WPF reports, or 0 when the game cannot bind it. Left and right Shift, Ctrl and Alt are the one
    /// key each, as the game reads them.
    public static int FromKey(Key key)
    {
        var vk = KeyInterop.VirtualKeyFromKey(key);
        vk = vk switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, _ => vk };
        return Bindable.Contains(vk) ? vk : 0;
    }

    public static int FromMouse(MouseButton b) => b switch
    {
        MouseButton.Left => 0x01, MouseButton.Right => 0x02, MouseButton.Middle => 0x04,
        MouseButton.XButton1 => 0x05, MouseButton.XButton2 => 0x06, _ => 0,
    };

    public static string Name(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x7B) return "F" + (vk - 0x6F);
        if (vk is >= 0x60 and <= 0x69) return "Num " + (vk - 0x60);
        return vk switch
        {
            0 => "",
            0x20 => "Space", 0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt", 0x09 => "Tab", 0x0D => "Enter", 0x08 => "Backspace",
            0x14 => "Caps Lock", 0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right", 0x24 => "Home", 0x23 => "End",
            0x21 => "Page Up", 0x22 => "Page Down", 0x6B => "Num +", 0x6D => "Num -", 0x6A => "Num *", 0x6F => "Num /", 0x6E => "Num .",
            0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`", 0xDB => "[", 0xDC => "\\",
            0xDD => "]", 0xDE => "'",
            0x01 => "Mouse left", 0x02 => "Mouse right", 0x04 => "Mouse middle", 0x05 => "Mouse 4", 0x06 => "Mouse 5",
            _ => $"Key {vk}",
        };
    }
}

/// The gamepad's inputs by position (HOS_PAD_*), as the game names them to the player
static class Pad
{
    public static readonly string[] Names =
    {
        "Bottom face", "Right face", "Left face", "Top face", "Select / Back", "Home / Guide", "Start", "Left stick press",
        "Right stick press", "Left shoulder", "Right shoulder", "D-pad up", "D-pad down", "D-pad left", "D-pad right",
        "Left trigger", "Right trigger",
    };

    /// (Xbox labels, shown small beside the position: what most PC players hold)
    public static readonly string[] Xbox = { "A", "B", "X", "Y", "View", "Guide", "Menu", "LS", "RS", "LB", "RB", "↑", "↓", "←", "→", "LT", "RT" };

    public static string Name(int input) => input >= 0 && input < Names.Length ? Names[input] : "None";

    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public XInputGamepad Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint GetState(uint user, out XInputState state);

    // (host/nn_hid.c: the XInput buttons onto HOS_PAD_* positions)
    static readonly (ushort Mask, int Input)[] Map =
    {
        (0x1000, 0), (0x2000, 1), (0x4000, 2), (0x8000, 3), (0x0040, 7), (0x0080, 8), (0x0100, 9), (0x0200, 10), (0x0010, 6),
        (0x0020, 4), (0x0004, 13), (0x0001, 11), (0x0008, 14), (0x0002, 12),
    };

    /// The inputs held now on any controller, XInput's and SDL's together (as host/nn_hid.c merges them), 1 << HOS_PAD_*
    public static uint Held() => XInputHeld() | SdlPads.Held;

    public static bool Connected() => SdlPads.Count > 0 || XInputConnected();

    static uint XInputHeld()
    {
        uint held = 0;
        try
        {
            for (uint u = 0; u < 4; u++)
            {
                if (GetState(u, out var s) != 0) continue;
                foreach (var (mask, input) in Map)
                    if ((s.Gamepad.Buttons & mask) != 0) held |= 1u << input;
                if (s.Gamepad.LeftTrigger > 64) held |= 1u << 15;
                if (s.Gamepad.RightTrigger > 64) held |= 1u << 16;
            }
        }
        catch (Exception) { }   // (no xinput1_4.dll: no controller capture, the list still works)
        return held;
    }

    static bool XInputConnected()
    {
        try
        {
            for (uint u = 0; u < 4; u++)
                if (GetState(u, out _) == 0) return true;
        }
        catch (Exception) { }
        return false;
    }
}

/// Every controller the game's SDL knows (DualSense, DualShock 4, Switch Pro, Xbox, DirectInput pads with a mapping), read
/// as host/pad_sdl.c and host/hos_sdl.c read them: the game folder's SDL3.dll, else its SDL2.dll, else the system's; every
/// gamepad opened, and ones plugged in later; buttons by position, in SDL's order, which is HOS_PAD_* for the first 15. A
/// thread of its own polls them while the launcher is open. No SDL: nothing here, and XInput still works.
static class SdlPads
{
    static volatile uint held;
    static volatile int count;
    static int started;

    public static uint Held => held;
    public static int Count => count;
    public static int Version { get; private set; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte InitBool(uint flags);           // SDL3: bool
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitInt(uint flags);             // SDL2: 0 on success
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr OpenPad(int what);            // SDL3: an id; SDL2: a device index
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void PadOnly(IntPtr pad);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte PadBool(IntPtr pad);            // (SDL3's bool and SDL2's SDL_bool/Uint8: the low byte)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate byte PadButton(IntPtr pad, int button);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate short PadAxis(IntPtr pad, int axis);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetGamepads(out int count);   // (SDL3)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void Free(IntPtr p);                 // (SDL3)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntNoArgs();                     // (SDL2)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IntFromInt(int i);               // (SDL2)

    /// Starts the thread once, with the SDL of the game in folder
    public static void Start(string folder)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        if (Environment.GetEnvironmentVariable("HOS_NO_SDL") is { Length: > 0 }) return;
        new Thread(() => Run(folder)) { IsBackground = true, Name = "gamepads (SDL)" }.Start();
    }

    static IntPtr Load(string folder, string dll)
    {
        foreach (var path in new[] { System.IO.Path.Combine(folder, "bin", dll), System.IO.Path.Combine(folder, dll) })
            if (System.IO.File.Exists(path) && NativeLibrary.TryLoad(path, out var h)) return h;
        return NativeLibrary.TryLoad(dll, out var sys) ? sys : IntPtr.Zero;
    }

    static T Fn<T>(IntPtr lib, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    static void Run(string folder)
    {
        try
        {
            var three = true;
            var lib = Load(folder, "SDL3.dll");
            if (lib == IntPtr.Zero) { three = false; lib = Load(folder, "SDL2.dll"); }
            if (lib == IntPtr.Zero) return;
            string N(string n3, string n2) => three ? n3 : n2;

            var hint = Fn<SetHint>(lib, "SDL_SetHint");
            hint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            hint("SDL_GAMECONTROLLER_USE_BUTTON_LABELS", "0");   // (SDL2's Nintendo pads by position, as the game reads them)
            var ok = three ? Fn<InitBool>(lib, "SDL_InitSubSystem")(0x2000) != 0 : Fn<InitInt>(lib, "SDL_InitSubSystem")(0x2000) == 0;
            if (!ok) return;
            Version = three ? 3 : 2;

            var open = Fn<OpenPad>(lib, N("SDL_OpenGamepad", "SDL_GameControllerOpen"));
            var close = Fn<PadOnly>(lib, N("SDL_CloseGamepad", "SDL_GameControllerClose"));
            var connected = Fn<PadBool>(lib, N("SDL_GamepadConnected", "SDL_GameControllerGetAttached"));
            var update = Fn<NoArgs>(lib, N("SDL_UpdateGamepads", "SDL_GameControllerUpdate"));
            var button = Fn<PadButton>(lib, N("SDL_GetGamepadButton", "SDL_GameControllerGetButton"));
            var axis = Fn<PadAxis>(lib, N("SDL_GetGamepadAxis", "SDL_GameControllerGetAxis"));
            GetGamepads? list3 = three ? Fn<GetGamepads>(lib, "SDL_GetGamepads") : null;
            Free? free = three ? Fn<Free>(lib, "SDL_free") : null;
            IntNoArgs? numJoysticks = three ? null : Fn<IntNoArgs>(lib, "SDL_NumJoysticks");
            IntFromInt? isPad = three ? null : Fn<IntFromInt>(lib, "SDL_IsGameController");
            IntFromInt? instanceId = three ? null : Fn<IntFromInt>(lib, "SDL_JoystickGetDeviceInstanceID");

            var pads = new Dictionary<int, IntPtr>();   // (instance id: the open gamepad)
            var nextScan = 0L;
            while (true)
            {
                update();
                var now = Environment.TickCount64;
                if (now >= nextScan)
                {
                    nextScan = now + 1000;
                    foreach (var (id, pad) in pads.ToList())
                        if (connected(pad) == 0) { close(pad); pads.Remove(id); }
                    var found = new List<(int Id, int Open)>();
                    if (three)
                    {
                        var arr = list3!(out var n);
                        for (var i = 0; arr != IntPtr.Zero && i < n; i++) { var id = Marshal.ReadInt32(arr, i * 4); found.Add((id, id)); }
                        if (arr != IntPtr.Zero) free!(arr);
                    }
                    else
                        for (var j = 0; j < numJoysticks!(); j++)
                            if (isPad!(j) != 0) found.Add((instanceId!(j), j));
                    foreach (var (id, which) in found)
                        if (!pads.ContainsKey(id) && open(which) is var pad && pad != IntPtr.Zero) pads[id] = pad;
                }
                uint b = 0;
                foreach (var pad in pads.Values)
                {
                    for (var k = 0; k <= 14; k++)   // (A ... D-pad right: HOS_PAD_SOUTH ... HOS_PAD_RIGHT)
                        if (button(pad, k) != 0) b |= 1u << k;
                    if (axis(pad, 4) > 8000) b |= 1u << 15;   // (the triggers, as pad_sdl.c)
                    if (axis(pad, 5) > 8000) b |= 1u << 16;
                }
                held = b;
                count = pads.Count;
                Thread.Sleep(8);
            }
        }
        catch (Exception) { held = 0; count = 0; }   // (an SDL without these calls: XInput only)
    }
}
