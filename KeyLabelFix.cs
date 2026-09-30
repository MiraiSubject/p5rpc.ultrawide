using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Mod.Interfaces;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// Keyboard key labels ("ALT", "CTRL", ...) are rendered with GDI into small textures. The game sizes that text
    /// from the horizontal UI scale, which is wider than 16:9 once the screen is widened, so the labels overflow
    /// their key caps. Hook the GDI calls to log what is requested and scale explicit font widths back to 16:9.
    /// </summary>
    public unsafe class KeyLabelFix
    {
        [Function(CallingConventions.Microsoft)]
        public delegate nint CreateFontIndirectA(LogFontA* logFont);

        [Function(CallingConventions.Microsoft)]
        public delegate int DrawTextW(nint hdc, char* text, int count, int* rect, uint format);

        [StructLayout(LayoutKind.Sequential)]
        public struct LogFontA
        {
            public int Height, Width, Escapement, Orientation, Weight;
            public byte Italic, Underline, StrikeOut, CharSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
            public fixed byte FaceName[32];
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        private readonly ILogger _logger;
        private readonly Func<float> _widthRatio;
        private readonly Func<bool> _enabled;
        private readonly IHook<CreateFontIndirectA>? _createFontHook;
        private readonly IHook<DrawTextW>? _drawTextHook;
        private int _logBudget = 60;

        /// <param name="widthRatio">16:9 width divided by the current screen width (1 when not widened).</param>
        public KeyLabelFix(IReloadedHooks hooks, ILogger logger, Func<float> widthRatio, Func<bool> enabled)
        {
            _logger = logger;
            _widthRatio = widthRatio;
            _enabled = enabled;

            var createFont = GetProcAddress(GetModuleHandleW("gdi32.dll"), "CreateFontIndirectA");
            var drawText = GetProcAddress(GetModuleHandleW("user32.dll"), "DrawTextW");
            if (createFont != 0)
                _createFontHook = hooks.CreateHook<CreateFontIndirectA>(CreateFontImpl, createFont).Activate();
            if (drawText != 0)
                _drawTextHook = hooks.CreateHook<DrawTextW>(DrawTextImpl, drawText).Activate();
        }

        private nint CreateFontImpl(LogFontA* logFont)
        {
            if (logFont != null)
            {
                var ratio = _widthRatio();
                var originalWidth = logFont->Width;
                if (_enabled() && ratio < 0.999f && logFont->Width != 0)
                    logFont->Width = (int)MathF.Round(logFont->Width * ratio);

                if (_logBudget > 0)
                {
                    _logBudget--;
                    var face = Marshal.PtrToStringAnsi((nint)logFont->FaceName);
                    _logger.WriteLine($"[p5rpc.ultrawide] CreateFontIndirectA face='{face}' height={logFont->Height} " +
                                      $"width={originalWidth}->{logFont->Width} weight={logFont->Weight}");
                }
            }
            return _createFontHook!.OriginalFunction(logFont);
        }

        private int DrawTextImpl(nint hdc, char* text, int count, int* rect, uint format)
        {
            if (_logBudget > 0 && text != null)
            {
                _logBudget--;
                var length = count >= 0 ? Math.Min(count, 24) : Math.Min(new string(text).Length, 24);
                var str = new string(text, 0, length);
                var r = rect != null ? $"({rect[0]},{rect[1]},{rect[2]},{rect[3]})" : "null";
                _logger.WriteLine($"[p5rpc.ultrawide] DrawTextW '{str}' rect={r} format=0x{format:x}");
            }
            return _drawTextHook!.OriginalFunction(hdc, text, count, rect, format);
        }
    }
}
