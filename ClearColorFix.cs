using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Mod.Interfaces;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// The game's neutral 3D clear is dark grey. During some loading transitions the scene covers only the
    /// centred 16:9 area, exposing that grey clear at the ultrawide edges. Make only that clear black.
    /// </summary>
    internal unsafe sealed class ClearColorFix
    {
        private const int ClearRenderTargetViewVtableIndex = 50;
        private const float NeutralGrey = 32f / 255f;
        private const float NeutralAlpha = 0.666f;

        [Function(CallingConventions.Microsoft)]
        private delegate void ClearRenderTargetView(nint context, nint target, nint color);

        [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice")]
        private static extern int CreateDevice(nint adapter, int driverType, nint software, uint flags,
            nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device,
            out int featureLevel, out nint context);

        private readonly IHook<ClearRenderTargetView> _hook;
        private readonly Func<bool> _enabled;
        private readonly ILogger _logger;
        private int _matchedClearLogged;

        public ClearColorFix(IReloadedHooks hooks, Func<bool> enabled, ILogger logger)
        {
            _enabled = enabled;
            _logger = logger;
            nint device = 0, context = 0;
            try
            {
                // A temporary hardware context gives us the D3D11 method address without relying on game structs.
                const int hardwareDriver = 1;
                const uint d3d11SdkVersion = 7;
                var hr = CreateDevice(0, hardwareDriver, 0, 0, 0, 0, d3d11SdkVersion,
                    out device, out _, out context);
                if (hr < 0 || context == 0)
                    throw new Exception($"D3D11CreateDevice failed (0x{hr:x8}).");

                var vtable = *(nint**)context;
                var clear = vtable[ClearRenderTargetViewVtableIndex];
                if (clear == 0)
                    throw new Exception("D3D11 ClearRenderTargetView address is null.");
                _hook = hooks.CreateHook<ClearRenderTargetView>(ClearImpl, clear).Activate();
                logger.WriteLine($"[p5rpc.ultrawide] Neutral clear fix installed at 0x{clear:x}.");
            }
            finally
            {
                if (context != 0)
                    Marshal.Release(context);
                if (device != 0)
                    Marshal.Release(device);
            }
        }

        private void ClearImpl(nint context, nint target, nint color)
        {
            if (_enabled() && color != 0)
            {
                var rgba = (float*)color;
                if (MathF.Abs(rgba[0] - NeutralGrey) < 0.00001f &&
                    MathF.Abs(rgba[1] - NeutralGrey) < 0.00001f &&
                    MathF.Abs(rgba[2] - NeutralGrey) < 0.00001f &&
                    MathF.Abs(rgba[3] - NeutralAlpha) < 0.00001f)
                {
                    float* black = stackalloc float[4] { 0f, 0f, 0f, rgba[3] };
                    if (Interlocked.Exchange(ref _matchedClearLogged, 1) == 0)
                        _logger.WriteLine("[p5rpc.ultrawide] Replaced the neutral grey 3D clear with black.");
                    _hook.OriginalFunction(context, target, (nint)black);
                    return;
                }
            }
            _hook.OriginalFunction(context, target, color);
        }
    }
}
