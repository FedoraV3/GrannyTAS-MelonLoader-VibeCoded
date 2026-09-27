using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace GrannyTAS
{
    /// <summary>
    /// Reads <c>UnityEngine.Random.state</c> without consuming it.
    ///
    /// The managed <c>Random.State</c> type and its property were stripped from
    /// this build, so the interop proxies do not offer it — but the engine still
    /// registers the native getter, so it is called directly. Reading the state
    /// is side-effect free, which is the point: a determinism check that drew a
    /// random number to see whether the random stream matched would itself put
    /// the stream out of step. If the icall cannot be resolved the RNG area of
    /// the sync check is simply absent.
    /// </summary>
    internal static class RandomProbe
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public int S0, S1, S2, S3;
        }

        private delegate void GetStateDelegate(out State state);

        private static GetStateDelegate _get;
        private static bool _resolved;

        public static bool TryRead(int[] into)
        {
            if (!_resolved)
            {
                _resolved = true;
                try { _get = IL2CPP.ResolveICall<GetStateDelegate>("UnityEngine.Random::get_state_Injected"); }
                catch { _get = null; }
            }
            if (_get == null || into == null || into.Length < 4) return false;

            try
            {
                _get(out var s);
                into[0] = s.S0;
                into[1] = s.S1;
                into[2] = s.S2;
                into[3] = s.S3;
                return true;
            }
            catch
            {
                // A missing icall resolves to a delegate that throws; stop asking.
                _get = null;
                return false;
            }
        }
    }
}
