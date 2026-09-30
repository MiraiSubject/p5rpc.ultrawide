using System.Diagnostics;
using System.Runtime.InteropServices;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// The game maps the cursor from window pixels to its 1920x1080 UI space using the full window width, but the UI is
    /// drawn squeezed into the centred 16:9 area. Expand the cursor's x around the window centre by the inverse factor
    /// so hover/click line up with what is on screen (the game's own cursor sprite is squeezed back onto the pointer).
    ///
    /// Only the game's own calls are redirected, by pointing P5R.exe's GetCursorPos import slot at a small native stub.
    /// A managed detour crashes (other mods call GetCursorPos from managed WndProc hooks → ReversePInvokeBadTransition),
    /// and an inline hook on user32 crashed on load. A background thread keeps the slot patched (in case something
    /// rewrites it) and updates the parameters the stub reads.
    ///
    /// When the game warps the cursor itself (SetCursorPos, e.g. mouse camera re-centring), expanding x would feed back
    /// into the warp every frame and the camera spins off. So SetCursorPos is redirected too: it switches the expansion
    /// off immediately, and it only comes back once the game has stopped warping for a while.
    /// </summary>
    public unsafe class MouseFix
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Parameters
        {
            public int Enabled;
            public float Centre;
            public float InverseRatio;
            public int SetCalls;
            public nint Original;
            public nint SetOriginal;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string? name);

        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        private static extern nint GetProcAddress(nint module, string name);

        [DllImport("kernel32")]
        private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

        [DllImport("kernel32")]
        private static extern int VirtualProtect(nint address, nuint size, uint protect, out uint oldProtect);

        [DllImport("kernel32")]
        private static extern byte RtlAddFunctionTable(nint functionTable, uint entryCount, ulong baseAddress);

        [DllImport("user32")]
        private static extern int GetClientRect(nint hwnd, int* rect);

        [DllImport("user32")]
        private static extern int ClientToScreen(nint hwnd, int* point);

        private const uint MEM_COMMIT_RESERVE = 0x3000;
        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const uint PAGE_READWRITE = 0x04;

        private readonly Func<float> _widthRatio;
        private readonly Func<bool> _enabled;
        private readonly Action<string> _log;
        private const long WarpQuietMs = 500;

        private readonly nint _stub;
        private readonly nint _setStub;
        private readonly nint* _slot;
        private readonly nint* _setSlot;
        private int _lastSetCalls;
        private long _warpQuietUntil;
        private readonly Parameters* _parameters;
        private nint _window;

        /// <param name="widthRatio">16:9 width divided by the current screen width (1 when not widened).</param>
        public MouseFix(Func<float> widthRatio, Func<bool> enabled, Action<string> log)
        {
            _widthRatio = widthRatio;
            _enabled = enabled;
            _log = log;

            _slot = FindImportSlot(GetModuleHandleW(null), "user32.dll", "GetCursorPos");
            _setSlot = FindImportSlot(GetModuleHandleW(null), "user32.dll", "SetCursorPos");
            if (_slot == null || _setSlot == null)
            {
                _log("MouseFix: cursor imports not found; mouse fix disabled.");
                return;
            }

            var memory = VirtualAlloc(0, 0x1000, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);
            if (memory == 0)
                return;

            _stub = memory;
            _parameters = (Parameters*)(memory + 0x800);
            *_parameters = default;
            var user32 = GetModuleHandleW("user32.dll");
            _parameters->Original = GetProcAddress(user32, "GetCursorPos");
            _parameters->SetOriginal = GetProcAddress(user32, "SetCursorPos");

            var length = WriteStub((byte*)memory, (nint)_parameters);
            RegisterUnwindInfo(memory, length);
            _setStub = memory + 0x200;
            WriteSetStub((byte*)_setStub, (nint)_parameters);

            new Thread(UpdateLoop) { IsBackground = true, Name = "p5rpc.ultrawide mouse" }.Start();
        }

        /// <summary>
        /// int GetCursorPos(POINT* p):
        ///   r = original(p); if (r &amp;&amp; p &amp;&amp; enabled) p->x = round((p->x - centre) * inverseRatio + centre); return r;
        /// </summary>
        private static int WriteStub(byte* code, nint parameters)
        {
            var c = new List<byte>();
            void Emit(params byte[] bytes) => c.AddRange(bytes);
            void Emit64(long value) => c.AddRange(BitConverter.GetBytes(value));

            Emit(0x48, 0x83, 0xEC, 0x28);                   // sub rsp, 28h
            Emit(0x48, 0x89, 0x4C, 0x24, 0x30);             // mov [rsp+30h], rcx
            Emit(0x49, 0xBA); Emit64(parameters);           // mov r10, parameters
            Emit(0x41, 0xFF, 0x52, 0x10);                   // call [r10+10h]        (Original)
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
            return c.Count;
        }

        /// <summary>
        /// int SetCursorPos(int x, int y): enabled = 0; SetCalls++; tail-jump to the original (a leaf, no unwind data).
        /// </summary>
        private static void WriteSetStub(byte* code, nint parameters)
        {
            var c = new List<byte>();
            c.AddRange(new byte[] { 0x49, 0xBA }); c.AddRange(BitConverter.GetBytes((long)parameters)); // mov r10, parameters
            c.AddRange(new byte[] { 0x41, 0xC7, 0x02, 0x00, 0x00, 0x00, 0x00 });  // mov dword [r10], 0    (Enabled)
            c.AddRange(new byte[] { 0xF0, 0x41, 0xFF, 0x42, 0x0C });              // lock inc dword [r10+0Ch] (SetCalls)
            c.AddRange(new byte[] { 0x41, 0xFF, 0x62, 0x18 });                    // jmp [r10+18h]        (SetOriginal)
            for (var i = 0; i < c.Count; i++)
                code[i] = c[i];
        }

        /// <summary>
        /// x64 needs unwind data for any non-leaf function, otherwise exception dispatch or a stack walk passing
        /// through the stub cannot unwind it. The stub's only prolog instruction is "sub rsp, 28h" (4 bytes).
        /// </summary>
        private static void RegisterUnwindInfo(nint memory, int length)
        {
            const int unwindInfoOffset = 0x400;
            const int functionTableOffset = 0x600;

            var unwind = (byte*)(memory + unwindInfoOffset);
            unwind[0] = 0x01;                // version 1, no flags
            unwind[1] = 0x04;                // size of prolog
            unwind[2] = 0x01;                // one unwind code
            unwind[3] = 0x00;                // no frame register
            unwind[4] = 0x04;                // code offset: end of "sub rsp, 28h"
            unwind[5] = (4 << 4) | 0x02;     // UWOP_ALLOC_SMALL, (0x28 - 8) / 8 = 4
            unwind[6] = 0x00;                // padding to an even number of codes
            unwind[7] = 0x00;

            var function = (uint*)(memory + functionTableOffset);
            function[0] = 0;                        // BeginAddress
            function[1] = (uint)length;             // EndAddress
            function[2] = unwindInfoOffset;         // UnwindInfoAddress
            RtlAddFunctionTable((nint)function, 1, (ulong)memory);
        }

        /// <summary>Finds the import address table slot for dll!function in a loaded PE image.</summary>
        private static nint* FindImportSlot(nint module, string dll, string function)
        {
            if (module == 0)
                return null;

            var image = (byte*)module;
            var nt = image + *(int*)(image + 0x3C);
            var importDirectory = *(uint*)(nt + 0x18 + 0x70 + 1 * 8);    // optional header data directory 1 (PE32+)
            if (importDirectory == 0)
                return null;

            for (var descriptor = (uint*)(image + importDirectory); descriptor[3] != 0; descriptor += 5)
            {
                var name = Marshal.PtrToStringAnsi((nint)(image + descriptor[3]));
                if (!string.Equals(name, dll, StringComparison.OrdinalIgnoreCase))
                    continue;

                var names = (ulong*)(image + (descriptor[0] != 0 ? descriptor[0] : descriptor[4]));
                var slots = (nint*)(image + descriptor[4]);
                for (var i = 0; names[i] != 0; i++)
                {
                    if ((names[i] & (1UL << 63)) != 0)
                        continue;   // imported by ordinal
                    var importName = Marshal.PtrToStringAnsi((nint)(image + (uint)names[i] + 2));
                    if (importName == function)
                        return &slots[i];
                }
            }
            return null;
        }

        /// <summary>Points an import slot at a stub, chaining to whatever it held before.</summary>
        private void PatchSlot(nint* slot, nint stub, nint* original, string name)
        {
            var current = *slot;
            if (current == stub)
                return;
            if (current != 0)
                *original = current;

            if (VirtualProtect((nint)slot, (nuint)sizeof(nint), PAGE_READWRITE, out var oldProtect) == 0)
                return;
            *slot = stub;
            VirtualProtect((nint)slot, (nuint)sizeof(nint), oldProtect, out _);
            _log($"MouseFix: redirected {name} import (previous target 0x{current:X}).");
        }

        private void UpdateLoop()
        {
            int* rect = stackalloc int[4];
            int* origin = stackalloc int[2];
            while (true)
            {
                try
                {
                    // Patch SetCursorPos first so a warp can never meet an expanded GetCursorPos unnoticed.
                    PatchSlot(_setSlot, _setStub, &_parameters->SetOriginal, "SetCursorPos");
                    PatchSlot(_slot, _stub, &_parameters->Original, "GetCursorPos");

                    var now = Environment.TickCount64;
                    var setCalls = Volatile.Read(ref _parameters->SetCalls);
                    if (setCalls != _lastSetCalls)
                    {
                        _lastSetCalls = setCalls;
                        _warpQuietUntil = now + WarpQuietMs;
                    }

                    var ratio = _widthRatio();
                    if (_window == 0)
                        _window = Process.GetCurrentProcess().MainWindowHandle;

                    origin[0] = origin[1] = 0;
                    var valid = now >= _warpQuietUntil && _enabled() && ratio < 0.999f && _window != 0 &&
                                GetClientRect(_window, rect) != 0 && ClientToScreen(_window, origin) != 0;
                    if (!valid)
                    {
                        _parameters->Enabled = 0;
                    }
                    else
                    {
                        _parameters->Centre = origin[0] + rect[2] / 2f;
                        _parameters->InverseRatio = 1f / ratio;
                        _parameters->Enabled = 1;
                    }
                }
                catch
                {
                    _parameters->Enabled = 0;
                }
                Thread.Sleep(200);
            }
        }
    }
}
