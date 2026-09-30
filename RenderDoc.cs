using System.Runtime.InteropServices;
using System.Text;
using Reloaded.Mod.Interfaces;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// Debug helper: loads RenderDoc in-process before the game creates its D3D11 device so frames can be
    /// captured with F12. Only used when enabled in the config.
    /// </summary>
    public static unsafe class RenderDoc
    {
        private const int ApiVersion_1_6_0 = 10600;
        private const int SetCaptureFilePathTemplateIndex = 11;

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
                logger.WriteLine($"[p5rpc.ultrawide] RenderDoc loaded. Press F12 in game to capture; captures go to {template}_*.rdc");
            }
            else
            {
                logger.WriteLine("[p5rpc.ultrawide] RenderDoc loaded. Press F12 in game to capture.");
            }
        }
    }
}
