using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    // Input suppression alone cannot stop Update-driven state changes at dt=0.
    // FallingHolder.Update sets isFalling from CharacterController.isGrounded;
    // PickRay.Update then hides the pickup ring and refuses MainInteract.
    // Preserve the controller's last real movement and its fall/interaction state.
    // Test the engine's current delta, not a pause request made midway through
    // Update: that request only applies to the following engine frame.
    [HarmonyPatch]
    internal static class GameplayFreeze
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MobileFPS), nameof(MobileFPS.Update));
            yield return AccessTools.Method(typeof(FallingHolder), nameof(FallingHolder.Update));
            yield return AccessTools.Method(typeof(PickRay), nameof(PickRay.Update));
            yield return AccessTools.Method(typeof(DoorRay), nameof(DoorRay.Update));
        }

        internal static bool Prefix() => !VirtualInput.Active || Time.deltaTime > 0f;
    }
}
