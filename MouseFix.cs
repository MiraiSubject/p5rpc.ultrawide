using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// The game maps the cursor from window pixels to its 1920x1080 UI space using the full window width, but the UI is
    /// drawn squeezed into the centred 16:9 area. Expand the cursor's x around the window centre by the inverse factor
    /// so hover/click line up with what is on screen (the game's own cursor sprite is squeezed back onto the pointer).
    /// </summary>
    public unsafe class MouseFix
    {
        [Function(CallingConventions.Microsoft)]
        public delegate int GetCursorPos(int* point);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        [DllImport("user32")]
        private static extern int GetClientRect(nint hwnd, int* rect);

        [DllImport("user32")]
        private static extern int ClientToScreen(nint hwnd, int* point);

        private readonly Func<float> _widthRatio;
        private readonly Func<bool> _enabled;
        private readonly IHook<GetCursorPos>? _hook;
        private nint _window;

        /// <param name="widthRatio">16:9 width divided by the current screen width (1 when not widened).</param>
        public MouseFix(IReloadedHooks hooks, Func<float> widthRatio, Func<bool> enabled)
        {
            _widthRatio = widthRatio;
            _enabled = enabled;
            var address = GetProcAddress(GetModuleHandleW("user32.dll"), "GetCursorPos");
            if (address != 0)
                _hook = hooks.CreateHook<GetCursorPos>(GetCursorPosImpl, address).Activate();
        }

        private int GetCursorPosImpl(int* point)
        {
            var result = _hook!.OriginalFunction(point);
            if (result == 0 || point == null || !_enabled())
                return result;

            var ratio = _widthRatio();
            if (ratio >= 0.999f)
                return result;

            if (_window == 0)
                _window = Process.GetCurrentProcess().MainWindowHandle;
            if (_window == 0)
                return result;

            int* rect = stackalloc int[4];
            int* origin = stackalloc int[2];
            if (GetClientRect(_window, rect) == 0 || ClientToScreen(_window, origin) == 0)
                return result;

            var centre = origin[0] + rect[2] / 2f;
            point[0] = (int)MathF.Round(centre + (point[0] - centre) / ratio);
            return result;
        }
    }
}
