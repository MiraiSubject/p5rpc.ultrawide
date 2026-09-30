using System.Runtime.InteropServices;
using p5rpc.ultrawide.Configuration;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Memory.Sources;
using Reloaded.Mod.Interfaces;

namespace p5rpc.ultrawide
{
    public unsafe class Ultrawide
    {
        private const float Aspect16x9 = 16f / 9f;

        // gfdCamera field offsets (P5R, see opengfd object/camera.rs)
        private const int CameraAspectOffset = 0x1ac;
        private const int CameraDirtyOffset = 0x1b4;

        // UI space used by GFD's immediate 2D primitives (vertex positions are in these units).
        private const float VirtualWidth = 1920f;
        private const int FvfTexCoord0 = 0x100;

        [Function(CallingConventions.Microsoft)]
        public delegate void FitViewport();

        [Function(CallingConventions.Microsoft)]
        public delegate void ApplyScreenSettings();

        [Function(CallingConventions.Microsoft)]
        public delegate nint ImmediateRender(int prio, int type, int count, nint vertices, int stride, int fvf, nint a7);

        [Function(CallingConventions.Microsoft)]
        public delegate nint CameraUpdate(nint camera, int a2);

        [Function(CallingConventions.Microsoft)]
        public delegate void SetResolution(nint target, int width, int height);

        private readonly ILogger _logger;
        private Config _config;

        private readonly IHook<FitViewport> _fitViewportHook;
        private readonly IHook<ApplyScreenSettings> _applyScreenSettingsHook;
        private readonly IHook<ImmediateRender> _immediateRenderHook;
        private readonly IHook<CameraUpdate> _cameraUpdateHook;
        private readonly IHook<SetResolution> _setResolutionHook;

        // Game data
        private readonly nint _aspectConstant;   // float 16/9 used when fitting the game area into the window
        private readonly int* _windowSize;       // { width, height, overrideWidth, overrideHeight }
        private readonly int* _displaySize;      // fitted display size { w, h } followed by render size { w, h }
        private readonly int* _screen2DSize;     // display size copy read by the 2D/UI code { w, h }
        private readonly int* _renderSize;       // render size copy { w, h }
        private readonly float* _uiScale;        // { scaleX, scaleY, baseScaleX, baseScaleY } = size / (1920, 1080)
        private readonly nint* _systemConstants; // -> GFD_VSCONST_SYSTEM mirror, scale2D at +0x20

        private float _screenAspect = Aspect16x9;
        private float _appliedCameraAspect = Aspect16x9;

        public Ultrawide(IReloadedHooks hooks, ILogger logger, Config config)
        {
            _logger = logger;
            _config = config;

            using var scanner = new GameScanner();

            // Fits a 16:9 area into the window: h = w * 9/16, or w = h * 16/9 when the window is wider.
            var fitViewport = scanner.Find("FitViewport", "48 83 EC 28 80 3D ?? ?? ?? ?? 00 74 ?? 0F 28 0D ?? ?? ?? ??");
            _windowSize = (int*)GameScanner.RipTarget(fitViewport + 0x0D, 3, 7);
            var aspectUse = GameScanner.FindWithin("FitViewport aspect", fitViewport, 0x90,
                "F3 0F 59 05 ?? ?? ?? ?? F3 48 0F 2C D0");
            _aspectConstant = GameScanner.RipTarget(aspectUse, 4, 8);
            var displayStore = GameScanner.FindWithin("FitViewport stores", fitViewport, 0x90,
                "89 15 ?? ?? ?? ?? 89 0D ?? ?? ?? ?? 89 15 ?? ?? ?? ?? 89 0D ?? ?? ?? ?? 48 83 C4 28 C3");
            _displaySize = (int*)GameScanner.RipTarget(displayStore, 2, 6);

            // Copies the display/render sizes into the globals the rest of the game reads.
            var screenSetup = scanner.Find("ScreenSetup", "48 89 5C 24 10 57 48 83 EC 60 80 3D ?? ?? ?? ?? 00");
            var screen2DWidth = GameScanner.FindWithin("2D width", screenSetup, 0x200, "89 3D ?? ?? ?? ?? 48 8D 3D");
            _screen2DSize = (int*)GameScanner.RipTarget(screen2DWidth, 2, 6);
            var renderSizeStore = GameScanner.FindWithin("render size", screenSetup, 0x200, "0F 11 0D ?? ?? ?? ?? 0F 1F 40 00");
            _renderSize = (int*)GameScanner.RipTarget(renderSizeStore, 3, 7);

            // Restores the UI scale (screen / 1920x1080) and uploads it to the shader system constants.
            var uiScaleRestore = scanner.Find("UiScaleRestore",
                "F3 0F 10 0D ?? ?? ?? ?? F3 0F 10 05 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? F3 0F 11 0D ?? ?? ?? ?? F3 0F 11 05 ?? ?? ?? ?? F3 0F 11 48 20");
            _uiScale = (float*)GameScanner.RipTarget(uiScaleRestore + 0x17, 4, 8);
            _systemConstants = (nint*)GameScanner.RipTarget(uiScaleRestore + 0x10, 3, 7);

            // gfdCameraUpdate: rebuilds the projection from near/far/fovy/aspect when the dirty bit is set.
            var cameraUpdate = scanner.Find("gfdCameraUpdate", "48 8B C4 55 53 56 57 41 56 48 8D 6C 24 90");

            var setResolution = scanner.Find("SetResolution", "85 D2 7E ?? 45 85 C0 7E ?? 89 51 1C 44 89 41 20");

            // Larger screen setup routine containing its own copy of the fit logic; this is the path used in game.
            var applyScreenSettings = scanner.Find("ApplyScreenSettings",
                "40 55 53 48 8D 6C 24 D8 48 81 EC 28 01 00 00 80 3D ?? ?? ?? ?? 00 0F 84 ?? ?? ?? ?? 0F 28 0D");

            // gfdDevCmdMakeImmediateRenderPrimitivePkt: every 2D sprite/primitive is submitted through here.
            var immediateRender = scanner.Find("ImmediateRender",
                "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 20 48 8B 35 ?? ?? ?? ?? 4D 8B F1 41 8B D8 0F B6 EA");

            Log($"FitViewport=0x{fitViewport:x} aspectConst=0x{_aspectConstant:x} window=0x{(nint)_windowSize:x} " +
                $"display=0x{(nint)_displaySize:x} 2D=0x{(nint)_screen2DSize:x} render=0x{(nint)_renderSize:x} " +
                $"uiScale=0x{(nint)_uiScale:x} sysConst=0x{(nint)_systemConstants:x} camera=0x{cameraUpdate:x}");

            _fitViewportHook = hooks.CreateHook<FitViewport>(FitViewportImpl, fitViewport).Activate();
            _applyScreenSettingsHook = hooks.CreateHook<ApplyScreenSettings>(ApplyScreenSettingsImpl, applyScreenSettings).Activate();
            _immediateRenderHook = hooks.CreateHook<ImmediateRender>(ImmediateRenderImpl, immediateRender).Activate();
            _cameraUpdateHook = hooks.CreateHook<CameraUpdate>(CameraUpdateImpl, cameraUpdate).Activate();
            _setResolutionHook = hooks.CreateHook<SetResolution>(SetResolutionImpl, setResolution).Activate();

            if (_config.DebugLogging)
                StartDebugHotkeys();
        }

        public void UpdateConfig(Config config)
        {
            _config = config;
            Log("Config updated. Change the resolution or restart the game to apply.");
        }

        private void FitViewportImpl()
        {
            UpdateAspectConstant();
            _fitViewportHook.OriginalFunction();
            if (_config.DebugLogging)
                DumpState("FitViewport");
        }

        private void ApplyScreenSettingsImpl()
        {
            UpdateAspectConstant();
            _applyScreenSettingsHook.OriginalFunction();
            if (_config.DebugLogging)
                DumpState("ApplyScreenSettings");
        }

        private nint ImmediateRenderImpl(int prio, int type, int count, nint vertices, int stride, int fvf, nint a7)
        {
            if (_config.CenterUi && _screenAspect > Aspect16x9 + 0.001f && vertices != 0 && count > 0 && stride >= 12)
                CenterVertices(vertices, count, stride, (fvf & FvfTexCoord0) != 0);
            return _immediateRenderHook.OriginalFunction(prio, type, count, vertices, stride, fvf, a7);
        }

        /// <summary>
        /// 2D vertices are in 1920x1080 units which the shader stretches over the whole (now wider) screen.
        /// Squeeze them horizontally around the centre so the UI keeps its 16:9 shape.
        /// </summary>
        private void CenterVertices(nint vertices, int count, int stride, bool textured)
        {
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                var x = *(float*)(vertices + i * stride);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
            }

            // Solid full-screen quads (fades, flashes, dimming) should keep covering the whole screen.
            if (!textured && minX <= 0.5f && maxX >= VirtualWidth - 0.5f)
                return;

            var ratio = Aspect16x9 / _screenAspect;
            const float centre = VirtualWidth / 2f;
            for (var i = 0; i < count; i++)
            {
                var x = (float*)(vertices + i * stride);
                *x = centre + (*x - centre) * ratio;
            }
        }

        private void UpdateAspectConstant()
        {
            var width = _windowSize[0];
            var height = _windowSize[1];
            if (_windowSize[2] != -1 && _windowSize[3] != -1)
            {
                width = _windowSize[2];
                height = _windowSize[3];
            }

            var aspect = height > 0 ? (float)width / height : Aspect16x9;
            _screenAspect = _config.WidenGame ? Math.Max(aspect, Aspect16x9) : Aspect16x9;

            // The game truncates height * constant, so bias it by half a pixel to land exactly on the window width.
            var constant = _screenAspect > Aspect16x9 ? (width + 0.5f) / height : Aspect16x9;
            Memory.Instance.SafeWrite((nuint)_aspectConstant, constant);
        }

        private nint CameraUpdateImpl(nint camera, int a2)
        {
            if (camera != 0 && _config.WidenGame)
            {
                var aspect = (float*)(camera + CameraAspectOffset);
                var current = *aspect;
                // Only retarget cameras set up for the full 16:9 screen; render-to-texture cameras keep theirs.
                if (MathF.Abs(current - Aspect16x9) < 0.01f || MathF.Abs(current - _appliedCameraAspect) < 0.0001f)
                {
                    if (current != _screenAspect)
                    {
                        *aspect = _screenAspect;
                        *(int*)(camera + CameraDirtyOffset) |= 1;
                    }
                }
            }
            _appliedCameraAspect = _screenAspect;
            return _cameraUpdateHook.OriginalFunction(camera, a2);
        }

        private void SetResolutionImpl(nint target, int width, int height)
        {
            if (_config.Override)
            {
                width = _config.Width != 0 ? _config.Width : width;
                height = _config.Height != 0 ? _config.Height : height;
            }
            _setResolutionHook.OriginalFunction(target, width, height);
        }

        private void DumpState(string reason)
        {
            var sys = *_systemConstants;
            var scale2D = sys != 0 ? $"({((float*)(sys + 0x20))[0]:0.####}, {((float*)(sys + 0x20))[1]:0.####}, {((float*)(sys + 0x20))[2]:0.####})" : "n/a";
            Log($"{reason}: aspect={_screenAspect:0.####} const={*(float*)_aspectConstant:0.####} " +
                $"window=[{_windowSize[0]},{_windowSize[1]},{_windowSize[2]},{_windowSize[3]}] " +
                $"display={_displaySize[0]}x{_displaySize[1]} renderFit={_displaySize[2]}x{_displaySize[3]} " +
                $"2D={_screen2DSize[0]}x{_screen2DSize[1]} render={_renderSize[0]}x{_renderSize[1]} " +
                $"uiScale=({_uiScale[0]:0.####}, {_uiScale[1]:0.####}) base=({_uiScale[2]:0.####}, {_uiScale[3]:0.####}) scale2D={scale2D}");
        }

        [DllImport("user32")]
        private static extern short GetAsyncKeyState(int key);

        private void StartDebugHotkeys()
        {
            const int VK_F9 = 0x78;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(100);
                    if ((GetAsyncKeyState(VK_F9) & 1) != 0)
                        DumpState("F9");
                }
            }) { IsBackground = true, Name = "p5rpc.ultrawide debug" };
            thread.Start();
        }

        private void Log(string message) => _logger.WriteLine($"[p5rpc.ultrawide] {message}");
    }
}
