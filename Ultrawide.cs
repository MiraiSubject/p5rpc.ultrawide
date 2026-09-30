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
        private const float MaxPinOvershoot = 64f;
        private const float VirtualHeight = 1080f;
        private const float EdgeTolerance = 2f;
        private const float ScreenCopyOvershoot = 16f;
        private const int MaxPinnedVertices = 6;

        [Function(CallingConventions.Microsoft)]
        public delegate void FitViewport();

        [Function(CallingConventions.Microsoft)]
        public delegate void ApplyScreenSettings();

        [Function(CallingConventions.Microsoft)]
        public delegate nint ImmediateRender(int prio, int type, int count, nint vertices, int stride, int fvf, nint a7);

        [Function(CallingConventions.Microsoft)]
        public delegate nint ImmediateRenderIndexed(int prio, int type, int count, nint indices, int indexCount, int a6,
            nint vertices, int stride, int fvf, nint a10);

        [Function(CallingConventions.Microsoft)]
        public delegate nint CameraUpdate(nint camera, int a2);

        // Both key-cap texture builders take two pointer arguments; all four argument registers are passed through.
        [Function(CallingConventions.Microsoft)]
        public delegate nint KeyboardResource(nint a1, nint a2, nint a3, nint a4);

        [Function(CallingConventions.Microsoft)]
        public delegate void SetResolution(nint target, int width, int height);

        private readonly ILogger _logger;
        private Config _config;

        private readonly IHook<FitViewport> _fitViewportHook;
        private readonly IHook<ApplyScreenSettings> _applyScreenSettingsHook;
        private readonly IHook<ImmediateRender> _immediateRenderHook;
        private readonly IHook<ImmediateRenderIndexed> _immediateRenderIndexedHook;
        private volatile int _traceRemaining;
        private int _primitiveCount, _indexedPrimitiveCount, _centeredCount;
        private (float X, float Y)? _pendingBattleButtonGlyph;

        private readonly IHook<CameraUpdate> _cameraUpdateHook;
        private readonly IHook<SetResolution> _setResolutionHook;
        private readonly MouseFix _mouseFix;
        private readonly ClearColorFix? _clearColorFix;

        // Game data
        private readonly nint _aspectConstant;   // float 16/9 used when fitting the game area into the window
        private readonly int* _windowSize;       // { width, height, overrideWidth, overrideHeight }
        private readonly int* _displaySize;      // fitted display size { w, h } followed by render size { w, h }
        private readonly int* _screen2DSize;     // display size copy read by the 2D/UI code { w, h }
        private readonly int* _renderSize;       // render size copy { w, h }
        private readonly float* _uiScale;        // { scaleX, scaleY, baseScaleX, baseScaleY } = size / (1920, 1080)
        private readonly nint* _systemConstants; // -> GFD_VSCONST_SYSTEM mirror, scale2D at +0x20
        private readonly nint _keyboardWidthCallA, _keyboardWidthCallB;
        private readonly byte[] _keyboardWidthOriginalA, _keyboardWidthOriginalB;
        private int _keyboardWidthOverride = -1;
        private readonly IHook<KeyboardResource> _keyboardResourceEventHook, _keyboardResourceDrawHook;
        [ThreadStatic] private static int _keyboardTextureDepth;

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

            // The keyboard button resource task draws its small key-cap textures with the same 2D primitives as
            // the UI. It sizes each key from the display width, so it must see the fitted 16:9 width or labels
            // such as Tab are clipped inside the texture. Its draws are also widened; see KeyboardTexture.
            var keyboardResourceEvent = scanner.Find("KeyboardResourceEvent",
                "4C 8B DC 49 89 53 10 49 89 4B 08 55 41 56 41 57");
            var keyboardResourceDraw = scanner.Find("KeyboardResourceDraw",
                "48 8B C4 48 89 50 10 48 89 48 08 55 48 8D A8 D8 F9 FF FF");
            _keyboardWidthCallA = GameScanner.FindWithin("KeyboardResourceEvent width", keyboardResourceEvent, 0x100,
                "E8 ?? ?? ?? ?? 66 0F 6E F0 0F 5B F6 F3 0F 59 35");
            _keyboardWidthCallB = GameScanner.FindWithin("KeyboardResourceDraw width", keyboardResourceDraw, 0x100,
                "E8 ?? ?? ?? ?? 66 44 0F 6E C0 45 0F 5B C0 F3 44 0F 59 05");
            _keyboardWidthOriginalA = SaveKeyboardWidthCall(_keyboardWidthCallA);
            _keyboardWidthOriginalB = SaveKeyboardWidthCall(_keyboardWidthCallB);

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

            Log($"FitViewport=0x{fitViewport:x} aspectConst=0x{_aspectConstant:x} window=0x{(nint)_windowSize:x} " +
                $"display=0x{(nint)_displaySize:x} 2D=0x{(nint)_screen2DSize:x} render=0x{(nint)_renderSize:x} " +
                $"uiScale=0x{(nint)_uiScale:x} sysConst=0x{(nint)_systemConstants:x} camera=0x{cameraUpdate:x}");

            _fitViewportHook = hooks.CreateHook<FitViewport>(FitViewportImpl, fitViewport).Activate();
            _applyScreenSettingsHook = hooks.CreateHook<ApplyScreenSettings>(ApplyScreenSettingsImpl, applyScreenSettings).Activate();
            _immediateRenderHook = hooks.CreateHook<ImmediateRender>(ImmediateRenderImpl, immediateRender).Activate();
            _immediateRenderIndexedHook = hooks.CreateHook<ImmediateRenderIndexed>(ImmediateRenderIndexedImpl, immediateRenderIndexed).Activate();

            _keyboardResourceEventHook = hooks.CreateHook<KeyboardResource>(KeyboardResourceEventImpl, keyboardResourceEvent).Activate();
            _keyboardResourceDrawHook = hooks.CreateHook<KeyboardResource>(KeyboardResourceDrawImpl, keyboardResourceDraw).Activate();

            _cameraUpdateHook = hooks.CreateHook<CameraUpdate>(CameraUpdateImpl, cameraUpdate).Activate();
            _setResolutionHook = hooks.CreateHook<SetResolution>(SetResolutionImpl, setResolution).Activate();

            _mouseFix = new MouseFix(
                () => _screenAspect > Aspect16x9 ? Aspect16x9 / _screenAspect : 1f,
                () => _config.CenterUi && _config.FixMouse,
                Log);

            try
            {
                _clearColorFix = new ClearColorFix(hooks,
                    () => _config.WidenGame && _screenAspect > Aspect16x9 + 0.001f, logger);
            }
            catch (Exception ex)
            {
                Log($"Neutral clear fix unavailable: {ex.Message}");
            }

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

        private nint KeyboardResourceEventImpl(nint a1, nint a2, nint a3, nint a4) =>
            KeyboardTexture(_keyboardResourceEventHook, a1, a2, a3, a4);

        private nint KeyboardResourceDrawImpl(nint a1, nint a2, nint a3, nint a4) =>
            KeyboardTexture(_keyboardResourceDrawHook, a1, a2, a3, a4);

        // The 2D projection is built for the wide screen, so inside a key-cap texture it narrows each key by
        // 16:9 / screen aspect and leaves the rest of the texture empty. Widen those draws back while it runs.
        private static nint KeyboardTexture(IHook<KeyboardResource> hook, nint a1, nint a2, nint a3, nint a4)
        {
            _keyboardTextureDepth++;
            try { return hook.OriginalFunction(a1, a2, a3, a4); }
            finally { _keyboardTextureDepth--; }
        }

        private void TryCenterVertices(nint vertices, int count, int stride, int fvf, int prio, char path)
        {
            if (_screenAspect <= Aspect16x9 + 0.001f || vertices == 0 || count <= 0 || stride < 12)
                return;

            // This follows the widened screen rather than CenterUi: the narrowing happens either way.
            if (_keyboardTextureDepth > 0)
            {
                var widen = _screenAspect / Aspect16x9;
                for (var i = 0; i < count; i++)
                    *(float*)(vertices + i * stride) *= widen;
                return;
            }

            if (!_config.CenterUi)
                return;

            var tracing = _traceRemaining > 0;
            var before = tracing ? Bounds(vertices, count, stride) : default;

            // The battle command wheel calculates its sprite centres for the fitted 16:9 area already. Keep
            // those centres while squeezing each sprite's own width so its artwork stays in proportion.
            var battleSprite = PrepareBattleSprite(vertices, count, stride, fvf);
            var battleCentre = battleSprite ? MidX(Bounds(vertices, count, stride)) : 0f;

            // Note: the UI scale global is not a usable render-to-texture signal. It reads 1.0 during normal UI
            // submission whenever mouse control is active, so every 2D draw is treated the same way.
            var action = CenterVertices(vertices, count, stride, (fvf & FvfTexCoord0) != 0);
            if (battleSprite)
            {
                var delta = battleCentre - MidX(Bounds(vertices, count, stride));
                for (var i = 0; i < count; i++)
                    *(float*)(vertices + i * stride) += delta;
            }

            if (tracing)
            {
                _traceRemaining--;
                var after = Bounds(vertices, count, stride);
                Log($"trace {path} thread={Environment.CurrentManagedThreadId} n={count} stride={stride} fvf=0x{fvf:x} scale=({_uiScale[0]:0.###},{_uiScale[1]:0.###}) " +
                    $"x={before.MinX:0.#}..{before.MaxX:0.#} y={before.MinY:0.#}..{before.MaxY:0.#} -> x={after.MinX:0.#}..{after.MaxX:0.#} {action}{(battleSprite ? "+battle-layout" : "")}");
            }
        }

        private static float MidX((float MinX, float MaxX, float MinY, float MaxY) bounds) =>
            (bounds.MinX + bounds.MaxX) * 0.5f;

        private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.0002f;

        private bool PrepareBattleSprite(nint vertices, int count, int stride, int fvf)
        {
            if (count != 4 || stride != 24 || (fvf & FvfTexCoord0) == 0)
            {
                _pendingBattleButtonGlyph = null;
                return false;
            }

            var bounds = Bounds(vertices, count, stride);
            var width = bounds.MaxX - bounds.MinX;
            var height = bounds.MaxY - bounds.MinY;
            var uv = (float*)(vertices + 16);
            var inWheel =
                bounds.MinX is > 250f and < 1300f && bounds.MinY is > 350f and < 950f &&
                width is > 25f and < 300f && height is > 30f and < 200f;
            // Prompt frames are about 1.25x larger with controller glyphs than with keyboard ones, so the size
            // windows cover both. Each frame is followed by its glyph, whose atlas depends on the input device.
            var square = MathF.Abs(width - height) < 2f;
            var abxy = inWheel && square && width is > 40f and < 60f &&
                Near(uv[1], 0.0013020834f) &&
                (Near(uv[0], 0.0013020834f) || Near(uv[0], 0.10286458f) ||
                 Near(uv[0], 0.20442709f) || Near(uv[0], 0.30598959f));
            var dpad = inWheel && square && width is > 58f and < 85f &&
                Near(uv[0], 0.80208337f) && Near(uv[1], 0.13411459f);
            var trigger = inWheel && width is > 64f and < 90f && height is > 36f and < 52f &&
                Near(uv[0], 0.40755209f) && Near(uv[1], 0.0013020834f);
            var pending = _pendingBattleButtonGlyph;
            var glyph = inWheel && pending.HasValue &&
                MathF.Abs(bounds.MinX - pending.Value.X) < 16f &&
                MathF.Abs(bounds.MinY - pending.Value.Y) < 4f;

            _pendingBattleButtonGlyph = abxy || dpad || trigger ? (bounds.MinX, bounds.MinY) : null;
            var artwork = inWheel && IsBattleCommandArt(uv[0], uv[1]);
            return abxy || dpad || trigger || glyph || artwork;
        }

        private static bool IsBattleCommandArt(float u, float v) =>
            (Near(u, 0.34375f) && Near(v, 0.23046875f)) ||
            (Near(u, 0.33333334f) && Near(v, 0.11848959f)) ||
            (Near(u, 0.0013020834f) && Near(v, 0.3515625f)) ||
            (Near(u, 0.0013020834f) && Near(v, 0.0013020834f)) ||
            (Near(u, 0.02734375f) && Near(v, 0.11848959f)) ||
            (Near(u, 0.0013020834f) && Near(v, 0.22395834f)) ||
            (Near(u, 0.19270834f) && Near(v, 0.44270834f)) ||
            (Near(u, 0.63802087f) && Near(v, 0.28515625f)) ||
            (Near(u, 0.72265625f) && Near(v, 0.10677084f)) ||
            (Near(u, 0.61979169f) && Near(v, 0.22786459f)) ||
            (Near(u, 0.23046875f) && Near(v, 0.36328125f)) ||
            (Near(u, 0.72265625f) && Near(v, 0.1640625f));

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
            var fullWidth = bounds.MinX <= EdgeTolerance && bounds.MaxX >= VirtualWidth - EdgeTolerance;
            var fullHeight = bounds.MinY <= EdgeTolerance && bounds.MaxY >= VirtualHeight - EdgeTolerance;
            var screenCopy = bounds.MinX >= -ScreenCopyOvershoot && bounds.MaxX <= VirtualWidth + ScreenCopyOvershoot &&
                bounds.MinY >= -ScreenCopyOvershoot && bounds.MaxY <= VirtualHeight + ScreenCopyOvershoot;

            // Solid fades/panels and near-1920x1080 textured screen copies stay wide. Some menu artwork is a
            // 2048x1536 textured quad that also spans the screen; it must be squeezed to preserve its proportions.
            if (fullWidth && (!textured || (fullHeight && screenCopy)))
            {
                // Some of these stop a unit short of the edge (e.g. 0..1919). Invisible at 16:9, but at 21:9 one
                // unit is ~2 pixels and whatever is behind shows through as a thin strip, so snap them to the edge.
                for (var i = 0; i < count; i++)
                {
                    var x = (float*)(vertices + i * stride);
                    if (*x > VirtualWidth - EdgeTolerance && *x < VirtualWidth)
                        *x = VirtualWidth;
                    else if (*x < EdgeTolerance && *x > 0f)
                        *x = 0f;
                }
                return "fullscreen";
            }

            // Only simple solid panels may extend to the real screen edge. Detailed vector art such as the menu's
            // stars and the quest log's brush strokes has many vertices; pinning just its outer vertices distorts it.
            // Textured art is also kept in proportion. Limit pinning to shapes ending near the original screen edge.
            var overshoot = MathF.Max(-bounds.MinX, bounds.MaxX - VirtualWidth);
            var pinEdges = _config.ExtendEdgeArt && !textured && count <= MaxPinnedVertices &&
                overshoot <= MaxPinOvershoot;

            var ratio = Aspect16x9 / _screenAspect;
            const float centre = VirtualWidth / 2f;
            for (var i = 0; i < count; i++)
            {
                var x = (float*)(vertices + i * stride);
                if (pinEdges && (*x <= EdgeTolerance || *x >= VirtualWidth - EdgeTolerance))
                    continue;
                *x = centre + (*x - centre) * ratio;
            }
            _centeredCount++;

            return pinEdges ? "squeeze+pin" : "squeeze";
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
            UpdateKeyboardWidthCalls(width, height);

            // The game truncates height * constant, so bias it by half a pixel to land exactly on the window width.
            var constant = screenAspect > Aspect16x9 ? (width + 0.5f) / height : Aspect16x9;
            if (screenAspect == _screenAspect && *(float*)_aspectConstant == constant)
                return false;

            _screenAspect = screenAspect;
            Memory.Instance.SafeWrite((nuint)_aspectConstant, constant);
            return true;
        }

        private byte[] SaveKeyboardWidthCall(nint call)
        {
            var getter = GameScanner.RipTarget(call, 1, 5);
            if (*(byte*)call != 0xE8 || *(byte*)getter != 0x8B || *(byte*)(getter + 1) != 0x05 ||
                GameScanner.RipTarget(getter, 2, 6) != (nint)_screen2DSize)
                throw new Exception("Keyboard button width call does not target the 2D width getter.");
            var original = new byte[5];
            Marshal.Copy(call, original, 0, original.Length);
            return original;
        }

        private void UpdateKeyboardWidthCalls(int width, int height)
        {
            var fitWidth = height > 0 ? Math.Min(width, (int)(height * Aspect16x9)) : width;
            var overrideWidth = _config.WidenGame && fitWidth < width ? fitWidth : 0;
            if (_keyboardWidthOverride == overrideWidth)
                return;

            if (overrideWidth == 0)
            {
                Memory.Instance.SafeWriteRaw((nuint)_keyboardWidthCallA, _keyboardWidthOriginalA);
                Memory.Instance.SafeWriteRaw((nuint)_keyboardWidthCallB, _keyboardWidthOriginalB);
            }
            else
            {
                var patch = new byte[] { 0xB8, (byte)overrideWidth, (byte)(overrideWidth >> 8),
                    (byte)(overrideWidth >> 16), (byte)(overrideWidth >> 24) };
                Memory.Instance.SafeWriteRaw((nuint)_keyboardWidthCallA, patch);
                Memory.Instance.SafeWriteRaw((nuint)_keyboardWidthCallB, patch);
            }
            _keyboardWidthOverride = overrideWidth;
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
            Log($"{reason}: primitives={_primitiveCount} indexed={_indexedPrimitiveCount} centered={_centeredCount} aspect={_screenAspect:0.####} const={*(float*)_aspectConstant:0.####} " +
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
