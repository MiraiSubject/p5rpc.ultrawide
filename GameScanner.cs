using System.Diagnostics;
using Reloaded.Memory.Sigscan;

namespace p5rpc.ultrawide
{
    /// <summary>
    /// Signature scanning over the main executable, plus helpers for decoding rip-relative operands.
    /// </summary>
    public unsafe sealed class GameScanner : IDisposable
    {
        public nint BaseAddress { get; }
        private readonly Scanner _scanner;

        public GameScanner()
        {
            var module = Process.GetCurrentProcess().MainModule!;
            BaseAddress = module.BaseAddress;
            _scanner = new Scanner((byte*)BaseAddress, module.ModuleMemorySize);
        }

        public nint Find(string name, string pattern)
        {
            var result = _scanner.FindPattern(pattern);
            if (!result.Found)
                throw new Exception($"Signature for {name} not found. The game version may be unsupported.");
            return BaseAddress + result.Offset;
        }

        /// <summary>Finds a pattern inside [start, start + length).</summary>
        public static nint FindWithin(string name, nint start, int length, string pattern)
        {
            using var scanner = new Scanner((byte*)start, length);
            var result = scanner.FindPattern(pattern);
            if (!result.Found)
                throw new Exception($"Pattern for {name} not found. The game version may be unsupported.");
            return start + result.Offset;
        }

        /// <summary>Resolves a rip-relative operand: target = next instruction + disp32.</summary>
        public static nint RipTarget(nint instruction, int dispOffset, int instructionLength)
            => instruction + instructionLength + *(int*)(instruction + dispOffset);

        public void Dispose() => _scanner.Dispose();
    }
}
