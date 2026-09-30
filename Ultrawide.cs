using System.Runtime.InteropServices;
using p5rpc.ultrawide.Configuration;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.Enums;
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

        /// <summary>gfdIm2DRenderPrimitive2D for position+colour vertices: (prio, type, vertices, count, flags).</summary>
        [Function(CallingConventions.Microsoft)]
        public delegate nint DrawPrimitiveG4(int prio, int type, nint vertices, int count, int flags);

        [Function(CallingConventions.Microsoft)]
        public delegate nint ImmediateRenderIndexed(int prio, int type, int count, nint indices, int indexCount, int a6,
            nint vertices, int stride, int fvf, nint a10);

        [Function(CallingConventions.Microsoft)]
        public delegate nint CameraUpdate(nint camera, int a2);

        [Function(CallingConventions.Microsoft)]
        public delegate void SetResolution(nint target, int width, int height);

        private readonly ILogger _logger;
        private Config _config;

        private readonly IHook<FitViewport> _fitViewportHook;
        private readonly IHook<ApplyScreenSettings> _applyScreenSettingsHook;
        private readonly IHook<ImmediateRender> _immediateRenderHook;
        private readonly IHook<ImmediateRenderIndexed> _immediateRenderIndexedHook;
        private volatile int _traceRemaining;
        private int _primitiveCount, _indexedPrimitiveCount, _centeredCount, _offscreenCount;

        // Render priority of the 2D draw currently being submitted, written by asm stubs at the entry of every
        // gfdIm2D* wrapper (they all take it as their first argument).
        private readonly int* _currentPrio;
        private readonly List<IAsmHook> _prioHooks = new();
        private readonly DrawPrimitiveG4? _drawPrimitiveG4;
        private readonly nint _barVertices;
        [ThreadStatic] private static bool _drawingBars;
        private int _barCount;
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

            // gfdDevCmdMakeImmediateRenderIndexedPrimitivePkt: indexed variant used for textured sprites.
            var immediateRenderIndexed = scanner.Find("ImmediateRenderIndexed",
                "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 20 0F B6 F2 49 8B F9 BA 78 00 00 00");

            // Solid-colour 2D wrapper, used to draw black side bars behind full-screen images.
            var drawPrimitiveG4 = scanner.Find("gfdIm2DRenderPrimitive2D",
                "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 56 41 57 48 83 EC 40 F6 84 24 80 00 00 00 01 45 8B F1 49 8B F0 44 0F B6 FA 8B F9 75 ?? 41 8B D9 C1 E3 04");
            _drawPrimitiveG4 = hooks.CreateWrapper<DrawPrimitiveG4>(drawPrimitiveG4, out _);
            _barVertices = Marshal.AllocHGlobal(12 * 16);
            _currentPrio = (int*)Marshal.AllocHGlobal(sizeof(int));
            *_currentPrio = 0;

            Log($"FitViewport=0x{fitViewport:x} aspectConst=0x{_aspectConstant:x} window=0x{(nint)_windowSize:x} " +
                $"display=0x{(nint)_displaySize:x} 2D=0x{(nint)_screen2DSize:x} render=0x{(nint)_renderSize:x} " +
                $"uiScale=0x{(nint)_uiScale:x} sysConst=0x{(nint)_systemConstants:x} camera=0x{cameraUpdate:x}");

            _fitViewportHook = hooks.CreateHook<FitViewport>(FitViewportImpl, fitViewport).Activate();
            _applyScreenSettingsHook = hooks.CreateHook<ApplyScreenSettings>(ApplyScreenSettingsImpl, applyScreenSettings).Activate();
            _immediateRenderHook = hooks.CreateHook<ImmediateRender>(ImmediateRenderImpl, immediateRender).Activate();
            _immediateRenderIndexedHook = hooks.CreateHook<ImmediateRenderIndexed>(ImmediateRenderIndexedImpl, immediateRenderIndexed).Activate();

            var wrappers = FindCallingFunctions(scanner.BaseAddress, new[] { immediateRender, immediateRenderIndexed });
            foreach (var wrapper in wrappers)
            {
                _prioHooks.Add(hooks.CreateAsmHook(new[]
                {
                    "use64",
                    "push rax",
                    "mov eax, ecx",
                    $"mov [qword 0x{(long)_currentPrio:X}], eax",
                    "pop rax",
                }, wrapper, AsmHookBehaviour.ExecuteFirst).Activate());
            }
            Log($"Tracking 2D render priority in {wrappers.Count} wrappers");
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
            var changed = UpdateAspectConstant();
            _fitViewportHook.OriginalFunction();
            if (changed && _config.DebugLogging)
                DumpState("FitViewport");
        }

        // Runs every frame, so only touch the constant and log when the window aspect changes.
        private void ApplyScreenSettingsImpl()
        {
            var changed = UpdateAspectConstant();
            _applyScreenSettingsHook.OriginalFunction();
            if (changed && _config.DebugLogging)
                DumpState("ApplyScreenSettings");
        }

        private nint ImmediateRenderImpl(int prio, int type, int count, nint vertices, int stride, int fvf, nint a7)
        {
            _primitiveCount++;
            TryCenterVertices(vertices, count, stride, fvf, prio, 'P');
            return _immediateRenderHook.OriginalFunction(prio, type, count, vertices, stride, fvf, a7);
        }

        private nint ImmediateRenderIndexedImpl(int prio, int type, int count, nint indices, int indexCount, int a6,
            nint vertices, int stride, int fvf, nint a10)
        {
            _indexedPrimitiveCount++;
            TryCenterVertices(vertices, count, stride, fvf, prio, 'I');
            return _immediateRenderIndexedHook.OriginalFunction(prio, type, count, indices, indexCount, a6, vertices, stride, fvf, a10);
        }

        private void TryCenterVertices(nint vertices, int count, int stride, int fvf, int prio, char path)
        {
            if (!_config.CenterUi || _drawingBars || _screenAspect <= Aspect16x9 + 0.001f || vertices == 0 || count <= 0 || stride < 12)
                return;

            var tracing = _traceRemaining > 0;
            var before = tracing ? Bounds(vertices, count, stride) : default;
            string action;

            // While the game renders 2D into an offscreen texture (e.g. keyboard key labels) it sets the UI scale
            // to 1.0; those coordinates are texture pixels, not screen space, so leave them alone.
            if (_uiScale[0] != _uiScale[2] || _uiScale[1] != _uiScale[3])
            {
                _offscreenCount++;
                action = "offscreen";
            }
            else
            {
                action = CenterVertices(vertices, count, stride, (fvf & FvfTexCoord0) != 0);
            }

            if (tracing)
            {
                _traceRemaining--;
                var after = Bounds(vertices, count, stride);
                Log($"trace {path} prio={*_currentPrio} n={count} stride={stride} fvf=0x{fvf:x} scale=({_uiScale[0]:0.###},{_uiScale[1]:0.###}) " +
                    $"x={before.MinX:0.#}..{before.MaxX:0.#} y={before.MinY:0.#}..{before.MaxY:0.#} -> x={after.MinX:0.#}..{after.MaxX:0.#} {action}");
            }
        }

        private static (float MinX, float MaxX, float MinY, float MaxY) Bounds(nint vertices, int count, int stride)
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (var i = 0; i < count; i++)
            {
                var p = (float*)(vertices + i * stride);
                minX = MathF.Min(minX, p[0]); maxX = MathF.Max(maxX, p[0]);
                minY = MathF.Min(minY, p[1]); maxY = MathF.Max(maxY, p[1]);
            }
            return (minX, maxX, minY, maxY);
        }

        /// <summary>
        /// 2D vertices are in 1920x1080 units which the shader stretches over the whole (now wider) screen.
        /// Squeeze them horizontally around the centre so the UI keeps its 16:9 shape.
        /// </summary>
        private string CenterVertices(nint vertices, int count, int stride, bool textured)
        {
            var bounds = Bounds(vertices, count, stride);
            var touchesLeft = bounds.MinX <= 0.5f;
            var touchesRight = bounds.MaxX >= VirtualWidth - 0.5f;
            var fullWidth = touchesLeft && touchesRight;

            // Solid full-screen quads (fades, flashes, dimming, menu backdrops) keep covering the whole screen.
            if (!textured && fullWidth)
                return "fullscreen";

            // Solid shapes that bleed off one edge of the original 16:9 screen (menu panels, bands, the quest log's
            // brush strokes) keep their edge vertices where they are, i.e. pinned to the real screen edge. Textured
            // art is never pinned since stretching it would distort it.
            var pinEdges = _config.ExtendEdgeArt && !textured;

            var ratio = Aspect16x9 / _screenAspect;
            const float centre = VirtualWidth / 2f;
            for (var i = 0; i < count; i++)
            {
                var x = (float*)(vertices + i * stride);
                if (pinEdges && (*x <= 0.5f || *x >= VirtualWidth - 0.5f))
                    continue;
                *x = centre + (*x - centre) * ratio;
            }
            _centeredCount++;

            // Full-screen images (2D backgrounds, loading screens, transitions, movies) stay 16:9 with black bars.
            if (textured && fullWidth && bounds.MinY <= 0.5f && bounds.MaxY >= 1079.5f)
            {
                DrawSideBars(ratio);
                return "image+bars";
            }
            return pinEdges ? "squeeze+pin" : "squeeze";
        }

        private void DrawSideBars(float ratio)
        {
            if (!_config.PillarboxImages || _drawPrimitiveG4 == null)
                return;

            var inner = VirtualWidth / 2f * (1f - ratio);
            var v = (float*)_barVertices;
            WriteQuad(ref v, -8f, inner);
            WriteQuad(ref v, VirtualWidth - inner, VirtualWidth + 8f);

            _drawingBars = true;
            try
            {
                const int TriangleList = 3;
                _drawPrimitiveG4(*_currentPrio, TriangleList, _barVertices, 12, 0);
                _barCount++;
            }
            finally
            {
                _drawingBars = false;
            }
        }

        /// <summary>Two triangles of opaque black position+colour vertices (colour is ABGR, alpha in byte 0).</summary>
        private static void WriteQuad(ref float* v, float left, float right)
        {
            const float top = -8f, bottom = 1088f;
            ReadOnlySpan<float> corners = stackalloc float[]
            {
                left, top, right, top, left, bottom,
                right, top, right, bottom, left, bottom,
            };
            for (var i = 0; i < corners.Length; i += 2)
            {
                v[0] = corners[i];
                v[1] = corners[i + 1];
                v[2] = 0f;
                *(uint*)(v + 3) = 0x000000FF;
                v += 4;
            }
        }

        /// <summary>Finds the functions that directly call any of the targets (rel32 call), by walking back to int3 padding.</summary>
        private static List<nint> FindCallingFunctions(nint moduleBase, nint[] targets)
        {
            var module = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
            var size = module.ModuleMemorySize;
            var code = (byte*)moduleBase;
            var result = new HashSet<nint>();
            for (var i = 0; i < size - 5; i++)
            {
                if (code[i] != 0xE8)
                    continue;
                var target = moduleBase + i + 5 + *(int*)(code + i + 1);
                if (Array.IndexOf(targets, target) < 0)
                    continue;
                for (var back = i; back > i - 0x800 && back > 1; back--)
                {
                    if (code[back - 1] == 0xCC && code[back - 2] == 0xCC)
                    {
                        result.Add(moduleBase + back);
                        break;
                    }
                }
            }
            return result.ToList();
        }

        private bool UpdateAspectConstant()
        {
            var width = _windowSize[0];
            var height = _windowSize[1];
            if (_windowSize[2] != -1 && _windowSize[3] != -1)
            {
                width = _windowSize[2];
                height = _windowSize[3];
            }

            var aspect = height > 0 ? (float)width / height : Aspect16x9;
            var screenAspect = _config.WidenGame ? Math.Max(aspect, Aspect16x9) : Aspect16x9;

            // The game truncates height * constant, so bias it by half a pixel to land exactly on the window width.
            var constant = screenAspect > Aspect16x9 ? (width + 0.5f) / height : Aspect16x9;
            if (screenAspect == _screenAspect && *(float*)_aspectConstant == constant)
                return false;

            _screenAspect = screenAspect;
            Memory.Instance.SafeWrite((nuint)_aspectConstant, constant);
            return true;
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
            Log($"{reason}: primitives={_primitiveCount} indexed={_indexedPrimitiveCount} centered={_centeredCount} offscreen={_offscreenCount} bars={_barCount} aspect={_screenAspect:0.####} const={*(float*)_aspectConstant:0.####} " +
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
            const int VK_F10 = 0x79;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(100);
                    if ((GetAsyncKeyState(VK_F9) & 1) != 0)
                        DumpState("F9");
                    if ((GetAsyncKeyState(VK_F10) & 1) != 0)
                    {
                        Log("F10: tracing the next 400 2D primitives");
                        _traceRemaining = 400;
                    }
                }
            }) { IsBackground = true, Name = "p5rpc.ultrawide debug" };
            thread.Start();
        }

        private void Log(string message) => _logger.WriteLine($"[p5rpc.ultrawide] {message}");
    }
}
