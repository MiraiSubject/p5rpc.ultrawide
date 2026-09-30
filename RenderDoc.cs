using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Mod.Interfaces;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// Debug helper: loads RenderDoc in-process before the game creates its D3D11 device so frames can be
    /// captured with F12, or with L3+R3 on a controller. Only used when enabled in the config.
    /// </summary>
    public static unsafe class RenderDoc
    {
        private const int ApiVersion_1_6_0 = 10600;
        private const int SetCaptureFilePathTemplateIndex = 11;
        private const int TriggerCaptureIndex = 15;
        private const ushort XInputLeftThumb = 0x0040, XInputRightThumb = 0x0080;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
        }

        [DllImport("xinput1_4", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(int user, out XInputState state);

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint LoadLibraryW(string path);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        public static void TryLoad(string dllPath, string captureTemplate, ILogger logger)
        {
            var module = LoadLibraryW(dllPath);
            if (module == 0)
            {
                logger.WriteLine($"[p5rpc.ultrawide] Could not load RenderDoc from {dllPath} (error {Marshal.GetLastWin32Error()}).");
                return;
            }

            var getApi = (delegate* unmanaged<int, void**, int>)GetProcAddress(module, "RENDERDOC_GetAPI");
            void* api = null;
            if (getApi == null || getApi(ApiVersion_1_6_0, &api) != 1 || api == null)
            {
                logger.WriteLine("[p5rpc.ultrawide] RenderDoc loaded but RENDERDOC_GetAPI failed.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(captureTemplate))
            {
                var template = Environment.ExpandEnvironmentVariables(captureTemplate);
                Directory.CreateDirectory(Path.GetDirectoryName(template)!);
                var bytes = Encoding.UTF8.GetBytes(template + "\0");
                var setTemplate = (delegate* unmanaged<byte*, void>)((nint*)api)[SetCaptureFilePathTemplateIndex];
                fixed (byte* p = bytes)
                    setTemplate(p);
                logger.WriteLine($"[p5rpc.ultrawide] RenderDoc loaded. Press F12 or L3+R3 in game to capture; captures go to {template}_*.rdc");
            }
            else
            {
                logger.WriteLine("[p5rpc.ultrawide] RenderDoc loaded. Press F12 or L3+R3 in game to capture.");
            }

            StartControllerTrigger(((nint*)api)[TriggerCaptureIndex], logger);
        }

        // Pressing a key switches the game's button prompts to keyboard glyphs, so F12 can never capture a frame
        // with controller prompts. Holding both stick buttons triggers a capture without leaving controller mode.
        private static void StartControllerTrigger(nint triggerCapture, ILogger logger)
        {
            var thread = new Thread(() =>
            {
                const ushort sticks = XInputLeftThumb | XInputRightThumb;
                var wasHeld = false;
                try
                {
                    while (true)
                    {
                        Thread.Sleep(50);
                        var held = false;
                        for (var user = 0; user < 4 && !held; user++)
                            held = XInputGetState(user, out var state) == 0 && (state.Buttons & sticks) == sticks;
                        if (held && !wasHeld)
                        {
                            ((delegate* unmanaged<void>)triggerCapture)();
                            logger.WriteLine("[p5rpc.ultrawide] L3+R3: RenderDoc capture triggered.");
                        }
                        wasHeld = held;
                    }
                }
                catch (Exception ex)
                {
                    // An unhandled exception on this thread would take the game down with it.
                    logger.WriteLine($"[p5rpc.ultrawide] Controller capture trigger stopped: {ex.Message}");
                }
            }) { IsBackground = true, Name = "p5rpc.ultrawide renderdoc trigger" };
            thread.Start();
        }
    }
}
