namespace Assets.Scripts.VizzyOrganizer
{
    using Assets.Scripts.Vizzy.UI;

    /// <summary>
    /// Harmony patch targets for hooking the folder panel into the stock Vizzy editor.
    /// Applied manually (via reflection against the Harmony instance) from Mod.cs rather
    /// than through [HarmonyPatch] attributes/PatchAll, since this mod doesn't reference
    /// HarmonyLib directly - see the comment on Mod.cs for why.
    /// </summary>
    internal static class VizzyOrganizerPatches
    {
        // Runs once when the Vizzy editor UI is created; attach our panel to it.
        private static void Postfix_Start(VizzyUIController __instance)
        {
            VizzyFolderPanel.AttachTo(__instance).Refresh();
        }

        // Runs whenever the controller redraws the program (load/import/undo/edit/etc);
        // keep the folder tree and the current filter in sync with it.
        private static void Postfix_RefreshUI(VizzyUIController __instance)
        {
            VizzyFolderPanel.AttachTo(__instance).Refresh();
        }
    }
}
