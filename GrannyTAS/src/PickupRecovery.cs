using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace GrannyTAS
{
    public enum PickupRecoveryOutcome
    {
        Applied,
        AlreadyHeld,
        Failed,
    }

    public readonly struct PickupRecoveryResult
    {
        public PickupRecoveryOutcome Outcome { get; }
        public string Note { get; }

        private PickupRecoveryResult(PickupRecoveryOutcome outcome, string note)
        {
            Outcome = outcome;
            Note = note ?? "";
        }

        public static PickupRecoveryResult Applied(string note = null) =>
            new PickupRecoveryResult(PickupRecoveryOutcome.Applied, note);

        public static PickupRecoveryResult AlreadyHeld(string note = null) =>
            new PickupRecoveryResult(PickupRecoveryOutcome.AlreadyHeld, note);

        public static PickupRecoveryResult Failed(string note) =>
            new PickupRecoveryResult(PickupRecoveryOutcome.Failed, note);
    }

    /// <summary>
    /// Game boundary for putting one validated recorded item in the player's
    /// hand and removing its missed world source.
    /// </summary>
    public interface IPickupRecovery
    {
        PickupRecoveryResult TryRecover(string itemName);
    }

    internal sealed class GamePickupRecovery : IPickupRecovery
    {
        // Unity defers Destroy until the end of the engine frame. Ignore a
        // source already consumed by another recovered pickup in this frame.
        private readonly HashSet<GameObject> _consumedSources = new HashSet<GameObject>();
        private int _consumedFrame = -1;

        public PickupRecoveryResult TryRecover(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return PickupRecoveryResult.Failed("item name is empty");
            if (itemName == "b") return PickupRecoveryResult.Failed("special shotgun pickup recovery is unsupported");

            if (_consumedFrame != Time.frameCount)
            {
                _consumedSources.Clear();
                _consumedFrame = Time.frameCount;
            }

            var ray = UnityEngine.Object.FindObjectOfType<PickRay>();
            if (ray == null) return PickupRecoveryResult.Failed("live PickRay was not found");

            Inventory inventory;
            try { inventory = ray.Inventory; }
            catch (System.Exception e) { return PickupRecoveryResult.Failed("could not read PickRay.Inventory: " + e.Message); }
            if (inventory == null) return PickupRecoveryResult.Failed("live PickRay has no Inventory");

            ItemDefs definition;
            try { definition = inventory.GetItemDefByName(itemName); }
            catch (System.Exception e) { return PickupRecoveryResult.Failed("item lookup failed: " + e.Message); }
            if (definition == null) return PickupRecoveryResult.Failed($"unknown inventory item '{itemName}'");
            if (definition.handObject == null) return PickupRecoveryResult.Failed($"item '{itemName}' has no hand object");

            try
            {
                if (ray.H_CR == null || ray.H_DSH == null || ray.H_FT == null || ray.H_PTRAP == null || ray.Drop1 == null)
                    return PickupRecoveryResult.Failed("live PickRay special-hand references are not initialized");
                if (ray.H_CR.activeSelf && (ray.DropP == null || ray.ItemDrop == null))
                    return PickupRecoveryResult.Failed("crossbow drop references are not initialized");
                if (ray.H_DSH.activeSelf && (ray.DropP == null || ray.ItemDrop == null ||
                    ray.ShotgunHandMain == null || ray.ShotgunHandPipes == null))
                    return PickupRecoveryResult.Failed("shotgun drop references are not initialized");
                if (ray.H_FT.activeSelf && (ray.DropPFreezeTrap == null || ray.O_FT == null))
                    return PickupRecoveryResult.Failed("freeze-trap drop references are not initialized");
                if (ray.H_PTRAP.activeSelf && (ray.DropPFreezeTrap == null || ray.O_PTRAP == null))
                    return PickupRecoveryResult.Failed("poison-trap drop references are not initialized");
                if (definition.handObject.activeSelf) return PickupRecoveryResult.AlreadyHeld();
            }
            catch (System.Exception e) { return PickupRecoveryResult.Failed("could not inspect pickup state: " + e.Message); }

            ItemSeedData source = null;
            try
            {
                foreach (var candidate in UnityEngine.Object.FindObjectsOfType<ItemSeedData>())
                {
                    if (candidate == null || candidate.gameObject == null || candidate.gameObject == definition.handObject ||
                        _consumedSources.Contains(candidate.gameObject) ||
                        !candidate.gameObject.activeInHierarchy ||
                        candidate.itemName != itemName) continue;
                    if (source != null)
                        return PickupRecoveryResult.Failed($"multiple active world sources match '{itemName}'");
                    source = candidate;
                }
            }
            catch (System.Exception e) { return PickupRecoveryResult.Failed("world source lookup failed: " + e.Message); }

            try
            {
                // This is the verified generic path in PickRay.Update. The
                // first call also drops special held objects correctly.
                ray.CheckItemDropping();
                inventory.PickupItem(definition.itemName);
                if (!definition.handObject.activeSelf)
                    return PickupRecoveryResult.Failed($"PickupItem did not activate '{itemName}'");
                if (source != null)
                {
                    UnityEngine.Object.Destroy(source.gameObject);
                    _consumedSources.Add(source.gameObject);
                }
                return PickupRecoveryResult.Applied(source != null
                    ? "equipped item and removed its world source"
                    : "equipped item; no active world source remained");
            }
            catch (System.Exception e)
            {
                return PickupRecoveryResult.Failed("native pickup failed: " + e.Message);
            }
        }
    }
}
