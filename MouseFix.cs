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
    ///
    /// GetCursorPos is called from inside other mods' managed window-procedure hooks, and entering a managed hook from
    /// there crashes the runtime (ReversePInvokeBadTransition). So the detour is a small native stub; a background
    /// thread only updates the parameters it reads.
    /// </summary>
    public unsafe class MouseFix
    {
        [Function(CallingConventions.Microsoft)]
        public delegate int GetCursorPos(int* point);

        [StructLayout(LayoutKind.Sequential)]
        private struct Parameters
        {
            public int Enabled;
            public float Centre;
            public float InverseRatio;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32")]
        private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

        [DllImport("user32")]
        private static extern int GetClientRect(nint hwnd, int* rect);

        [DllImport("user32")]
        private static extern int ClientToScreen(nint hwnd, int* point);

        private const uint MEM_COMMIT_RESERVE = 0x3000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;

        private readonly Func<float> _widthRatio;
        private readonly Func<bool> _enabled;
        private readonly IHook<GetCursorPos>? _hook;
        private readonly Parameters* _parameters;
        private nint _window;

        /// <param name="widthRatio">16:9 width divided by the current screen width (1 when not widened).</param>
        public MouseFix(IReloadedHooks hooks, Func<float> widthRatio, Func<bool> enabled)
        {
            _widthRatio = widthRatio;
            _enabled = enabled;

            var target = GetProcAddress(GetModuleHandleW("user32.dll"), "GetCursorPos");
            if (target == 0)
                return;

            var memory = VirtualAlloc(0, 0x1000, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (memory == 0)
                return;

            _parameters = (Parameters*)(memory + 0x800);
            *_parameters = default;

            // Create the hook first to learn the trampoline address, write the stub, then activate.
            _hook = hooks.CreateHook<GetCursorPos>((void*)memory, target);
            WriteStub((byte*)memory, _hook.OriginalFunctionAddress, (nint)_parameters);
            _hook.Activate();

            new Thread(UpdateLoop) { IsBackground = true, Name = "p5rpc.ultrawide mouse" }.Start();
        }

        /// <summary>
        /// int GetCursorPos(POINT* p):
        ///   r = original(p); if (r &amp;&amp; p &amp;&amp; enabled) p->x = round((p->x - centre) * inverseRatio + centre); return r;
        /// </summary>
        private static void WriteStub(byte* code, nint original, nint parameters)
        {
            var c = new List<byte>();
            void Emit(params byte[] bytes) => c.AddRange(bytes);
            void Emit64(long value) => c.AddRange(BitConverter.GetBytes(value));

            Emit(0x48, 0x83, 0xEC, 0x28);                   // sub rsp, 28h
            Emit(0x48, 0x89, 0x4C, 0x24, 0x30);             // mov [rsp+30h], rcx
            Emit(0x48, 0xB8); Emit64(original);             // mov rax, original
            Emit(0xFF, 0xD0);                               // call rax
            Emit(0x48, 0x8B, 0x4C, 0x24, 0x30);             // mov rcx, [rsp+30h]
            Emit(0x85, 0xC0);                               // test eax, eax
            var jz1 = c.Count; Emit(0x74, 0x00);            // jz done
            Emit(0x48, 0x85, 0xC9);                         // test rcx, rcx
            var jz2 = c.Count; Emit(0x74, 0x00);            // jz done
            Emit(0x49, 0xBA); Emit64(parameters);           // mov r10, parameters
            Emit(0x41, 0x8B, 0x12);                         // mov edx, [r10]        (Enabled)
            Emit(0x85, 0xD2);                               // test edx, edx
            var jz3 = c.Count; Emit(0x74, 0x00);            // jz done
            Emit(0xF3, 0x0F, 0x2A, 0x01);                   // cvtsi2ss xmm0, dword [rcx]
            Emit(0xF3, 0x41, 0x0F, 0x5C, 0x42, 0x04);       // subss xmm0, [r10+4]   (Centre)
            Emit(0xF3, 0x41, 0x0F, 0x59, 0x42, 0x08);       // mulss xmm0, [r10+8]   (InverseRatio)
            Emit(0xF3, 0x41, 0x0F, 0x58, 0x42, 0x04);       // addss xmm0, [r10+4]
            Emit(0xF3, 0x0F, 0x2D, 0xD0);                   // cvtss2si edx, xmm0
            Emit(0x89, 0x11);                               // mov [rcx], edx
            var done = c.Count;
            Emit(0x48, 0x83, 0xC4, 0x28);                   // add rsp, 28h
            Emit(0xC3);                                     // ret

            foreach (var jz in new[] { jz1, jz2, jz3 })
                c[jz + 1] = (byte)(done - (jz + 2));

            for (var i = 0; i < c.Count; i++)
                code[i] = c[i];
        }

        private void UpdateLoop()
        {
            int* rect = stackalloc int[4];
            int* origin = stackalloc int[2];
            while (true)
            {
                Thread.Sleep(200);
                try
                {
                    var ratio = _widthRatio();
                    if (_window == 0)
                        _window = Process.GetCurrentProcess().MainWindowHandle;

                    origin[0] = origin[1] = 0;
                    var valid = _enabled() && ratio < 0.999f && _window != 0 &&
                                GetClientRect(_window, rect) != 0 && ClientToScreen(_window, origin) != 0;
                    if (!valid)
                    {
                        _parameters->Enabled = 0;
                        continue;
                    }

                    _parameters->Centre = origin[0] + rect[2] / 2f;
                    _parameters->InverseRatio = 1f / ratio;
                    _parameters->Enabled = 1;
                }
                catch
                {
                    _parameters->Enabled = 0;
                }
            }
        }
    }
}
