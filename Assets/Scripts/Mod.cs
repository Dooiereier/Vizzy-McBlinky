namespace Assets.Scripts
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Assets.Scripts.Vizzy.UI;
    using Assets.Scripts.VizzyOrganizer;
    using UnityEngine;

    public class Mod : ModApi.Mods.GameMod
    {
        private Mod() : base()
        {
        }
        public static Mod Instance { get; } = GetModInstance<Mod>();

        // Talk to Harmony purely via reflection against whatever copy is already
        // loaded (e.g. by the Juno Harmony mod) instead of referencing HarmonyLib
        // directly. A direct reference would make Mod Builder force-bundle its own
        // copy of 0Harmony.dll, which collides at runtime with any other mod's copy.
        private static Type _harmonyType;
        private static Type _harmonyMethodType;
        private static object _harmonyInstance;

        protected override void OnModInitialized()
        {
            try
            {
                InitHarmony();

                ApplyPostfix(
                    typeof(VizzyUIController).GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    typeof(VizzyOrganizerPatches).GetMethod("Postfix_Start", BindingFlags.Static | BindingFlags.NonPublic));

                ApplyPostfix(
                    typeof(VizzyUIController).GetMethod("RefreshUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
                    typeof(VizzyOrganizerPatches).GetMethod("Postfix_RefreshUI", BindingFlags.Static | BindingFlags.NonPublic));

                Debug.Log("[Vizzy McBlinky] Patches applied.");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Vizzy McBlinky] Failed to initialize: " + ex);
            }

            // Separate from the Harmony setup above (and has its own internal try/catch) so
            // a Harmony failure can't also silently skip the update check, or vice versa.
            new ModUpdater().CheckForUpdate();
        }

        private static void InitHarmony()
        {
            var harmonyAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "0Harmony");
            if (harmonyAssembly == null)
                throw new InvalidOperationException(
                    "Harmony (0Harmony.dll) is not loaded. Enable the Juno Harmony mod for this mod to work.");

            _harmonyType = harmonyAssembly.GetType("HarmonyLib.Harmony");
            _harmonyMethodType = harmonyAssembly.GetType("HarmonyLib.HarmonyMethod");
            if (_harmonyType == null || _harmonyMethodType == null)
                throw new InvalidOperationException("Could not find HarmonyLib.Harmony / HarmonyLib.HarmonyMethod types.");

            _harmonyInstance = Activator.CreateInstance(_harmonyType, "Vizzy McBlinky");
        }

        private static void ApplyPrefix(MethodBase original, MethodInfo prefix) => ApplyPatch(original, prefix, "prefix");
        private static void ApplyPostfix(MethodBase original, MethodInfo postfix) => ApplyPatch(original, postfix, "postfix");

        private static void ApplyPatch(MethodBase original, MethodInfo patchMethod, string slot)
        {
            if (original == null)
                throw new InvalidOperationException("Could not find target method for " + slot + ".");
            if (patchMethod == null)
                throw new InvalidOperationException("Could not find patch method for " + slot + ".");

            var harmonyMethod = Activator.CreateInstance(_harmonyMethodType, patchMethod);

            // Resolve Harmony's instance Patch(MethodBase, HarmonyMethod prefix, ...) overload
            // by parameter names rather than assuming an exact arity, since that has changed
            // across Harmony versions (e.g. the "ilmanipulator" parameter was added later).
            var patch = _harmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "Patch") return false;
                    var ps = m.GetParameters();
                    return ps.Length >= 2
                        && ps[0].ParameterType == typeof(MethodBase)
                        && ps.Skip(1).All(p => p.ParameterType == _harmonyMethodType)
                        && ps.Any(p => p.Name == slot);
                });
            if (patch == null)
                throw new InvalidOperationException("Could not find a suitable Harmony.Patch(MethodBase, ...) overload for " + slot + ".");

            var parameters = patch.GetParameters();
            var args = new object[parameters.Length];
            args[0] = original;
            for (int i = 1; i < parameters.Length; i++)
                args[i] = parameters[i].Name == slot ? harmonyMethod : null;

            patch.Invoke(_harmonyInstance, args);
        }
    }
}
