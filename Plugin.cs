using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MooringLineFix
{
    // Sailwind 0.39 does not save which cleat a mooring line is tied to, only where its end is. Loading, it puts the end back
    // there, outside the shifting world, and ties it only if a switched-on cleat is right there. A ship moored at a port far
    // from where the game was saved loses her lines: the game switches far islands' cleats off and lowers the islands, and
    // the loose ends stay behind each time the world shifts. This ties each saved line back to its cleat: the one under or
    // over its end (islands are only lowered, never moved sideways).
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "DogEggz.Moorlinefix";
        public const string Name = "Mooring Line Fix";
        public const string Version = "1.0.1";
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
        }

        private void Update() { Lines.Retry(); Lines.Rejoin(); }
    }

    public static class Lines
    {
        // How far (seen from above) a cleat may be from a saved line end.
        private const float Reach = .5f;
        // Lines loaded as tied and not tied again yet: their cleat's scene is not loaded, or another line holds the cleat.
        private static readonly List<PickupableBoatMooringRope> waiting = new List<PickupableBoatMooringRope>();
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, bool> wasKinematic =
            AccessTools.FieldRefAccess<PickupableBoatMooringRope, bool>("wasKinematic");
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, SpringJoint> mooredToSpring =
            AccessTools.FieldRefAccess<PickupableBoatMooringRope, SpringJoint>("mooredToSpring");
        private static readonly AccessTools.FieldRef<PickupableBoatMooringRope, Transform> initialParent =
            AccessTools.FieldRefAccess<PickupableBoatMooringRope, Transform>("initialParent");
        private static float nextTry, nextRejoin, nextList;
        // Whether each tied line's cleat and ship were both switched on at the last look.
        private static readonly Dictionary<PickupableBoatMooringRope, bool> live = new Dictionary<PickupableBoatMooringRope, bool>();
        private static PickupableBoatMooringRope[] lines;

        public static int Waiting => waiting.Count;
        public static int Rejoined { get; private set; }

        // The game switches ships off beyond 10 km and island cleats beyond the island's load distance; once switched back on,
        // a tied line's spring no longer pulls (0 N however far it is stretched). Each line is joined to its ship again when
        // its cleat and ship are both back on.
        internal static void Rejoin()
        {
            if (Time.unscaledTime < nextRejoin) return;
            nextRejoin = Time.unscaledTime + .5f;
            if (lines == null || Time.unscaledTime > nextList)
            {
                lines = Resources.FindObjectsOfTypeAll<PickupableBoatMooringRope>().Where(r => r && r.gameObject.scene.IsValid()).ToArray();
                nextList = Time.unscaledTime + 10;
            }
            foreach (var rope in lines)
            {
                if (!rope || !rope.IsMoored()) { if (rope) live.Remove(rope); continue; }
                var spring = mooredToSpring(rope); var boat = rope.GetBoatRigidbody();
                bool on = spring && boat && spring.gameObject.activeInHierarchy && boat.gameObject.activeInHierarchy;
                bool seen = live.TryGetValue(rope, out bool was); live[rope] = on;
                // Switched off: its length is kept, since the game measures it again from wherever the ship lands when she is
                // switched back on and wakes (often metres under the water).
                if (seen && was && !on && spring && spring.maxDistance < 50) kept[rope] = spring.maxDistance;
                if (!on || was) continue;
                var anchor = spring.connectedAnchor;
                spring.connectedBody = null; spring.connectedBody = boat; spring.connectedAnchor = anchor;
                // A line tied while switched off (at loading) has no known length: it is measured when she next wakes.
                if (!kept.ContainsKey(rope)) wasKinematic(rope) = true;
                Rejoined++;
            }
        }

        // Lines whose length is restored after each game update until their ship has been awake for a second.
        private static readonly Dictionary<PickupableBoatMooringRope, float> kept = new Dictionary<PickupableBoatMooringRope, float>();
        private static readonly Dictionary<PickupableBoatMooringRope, float> keptUntil = new Dictionary<PickupableBoatMooringRope, float>();
        internal static void KeepLength(PickupableBoatMooringRope rope)
        {
            if (!kept.TryGetValue(rope, out float length)) return;
            var spring = mooredToSpring(rope); var boat = rope.GetBoatRigidbody();
            if (!spring || !boat) { kept.Remove(rope); keptUntil.Remove(rope); return; }
            if (!spring.gameObject.activeInHierarchy || !boat.gameObject.activeInHierarchy) return;
            spring.maxDistance = length; rope.currentRopeLengthSquared = length * length; wasKinematic(rope) = false;
            if (boat.isKinematic) { keptUntil.Remove(rope); return; }
            if (!keptUntil.TryGetValue(rope, out float until)) keptUntil[rope] = Time.unscaledTime + 1;
            else if (Time.unscaledTime > until) { kept.Remove(rope); keptUntil.Remove(rope); }
        }

        // A line the game has just loaded as tied: until it is tied again its end stays in the shifting world, and a save made
        // meanwhile still keeps it as tied (the game's load leaves that flag as it was).
        internal static void Loaded(PickupableBoatMooringRope rope)
        {
            if (Refs.shiftingWorld) rope.transform.SetParent(Refs.shiftingWorld, true);
            var save = rope.GetComponent<SaveableObject>(); if (save) save.extraSetting = true;
            if (!waiting.Contains(rope)) waiting.Add(rope);
        }

        // The player picked up a waiting line: it is the ship's untied line again (dropped, it goes back aboard).
        internal static void PickedUp(PickupableBoatMooringRope rope)
        {
            if (!waiting.Remove(rope)) return;
            var home = initialParent(rope); if (home) rope.transform.SetParent(home, true);
            var save = rope.GetComponent<SaveableObject>(); if (save) save.extraSetting = false;
        }

        internal static void Retry()
        {
            if (waiting.Count == 0 || Time.unscaledTime < nextTry) return;
            nextTry = Time.unscaledTime + 1;
            TieWaiting();
        }

        // Ties every waiting line whose cleat is found and free; returns how many were tied.
        public static int TieWaiting()
        {
            foreach (var r in waiting.Where(r => r && r.held).ToArray()) PickedUp(r);
            waiting.RemoveAll(r => !r || r.IsMoored());
            if (waiting.Count == 0) return 0;
            var cleats = Resources.FindObjectsOfTypeAll<GPButtonDockMooring>().Where(c => c && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded).ToArray();
            int tied = waiting.RemoveAll(r => Tie(r, cleats));
            if (tied > 0 || GameState.currentlyLoading)
                Plugin.Log.LogInfo("Tied " + tied + " mooring line(s) back to their cleats" + (waiting.Count > 0 ? "; " + waiting.Count + " waiting for theirs" : "") + ".");
            return tied;
        }

        private static bool Tie(PickupableBoatMooringRope rope, GPButtonDockMooring[] cleats)
        {
            var boat = rope.GetBoatRigidbody(); if (!boat) return false;
            var end = rope.transform.position;
            float Flat(GPButtonDockMooring c) { var d = c.transform.position - end; return d.x * d.x + d.z * d.z; }
            var cleat = cleats.Where(c => c && !c.transform.IsChildOf(boat.transform) && Flat(c) <= Reach * Reach)
                .OrderBy(Flat).ThenBy(c => (c.transform.position - end).sqrMagnitude).FirstOrDefault();
            if (!cleat) return false;
            if (!cleat.spring) cleat.spring = cleat.GetComponent<SpringJoint>();
            if (!cleat.spring || cleat.spring.connectedBody) return false;
            rope.MoorTo(cleat);
            // The cleat may be lowered with its far island (or the island not yet set for the loaded player): the game measures
            // the line again when the ship next wakes up, with the island back at its height.
            wasKinematic(rope) = true;
            return true;
        }
    }

    [HarmonyPatch(typeof(SaveableObject), nameof(SaveableObject.Load))]
    internal static class LoadPatch
    {
        private static void Postfix(SaveableObject __instance, SaveObjectData data)
        {
            if (data == null || !data.extraSetting) return;
            var rope = __instance.GetComponent<PickupableBoatMooringRope>();
            // The game left a tied line's end at the scene root; a line it skipped (unbought ship) keeps its parent.
            if (rope && __instance.transform.parent == null && !rope.IsMoored()) Lines.Loaded(rope);
        }
    }

    [HarmonyPatch(typeof(PickupableBoatMooringRope), "Update")]
    internal static class RopeUpdatePatch
    {
        private static void Postfix(PickupableBoatMooringRope __instance) => Lines.KeepLength(__instance);
    }

    [HarmonyPatch(typeof(PickupableBoatMooringRope), nameof(PickupableBoatMooringRope.OnPickup))]
    internal static class PickupPatch
    {
        private static void Postfix(PickupableBoatMooringRope __instance) => Lines.PickedUp(__instance);
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadGame))]
    internal static class LoadGamePatch
    {
        // Still loading: the lines are tied before the world first shifts under the loaded player.
        private static void Postfix() => Lines.TieWaiting();
    }
}
