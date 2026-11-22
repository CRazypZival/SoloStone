using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

/// <summary>Safety net for Card.ShowSecretTrigger. The client activates the root Spell of the card's actor there and does not
/// check that it exists; a Sigil whose actor is not a secret actor (#137) throws, which stops SigilSpellController and freezes
/// the match. Skipping the flash instead costs a missing indicator, so the controller goes on to its banner and effects.</summary>
internal static class NativeSigilGuard
{
    private static ManualLogSource log;
    private static int warnings;

    internal static void Initialize(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        try
        {
            var method = AccessTools.Method(typeof(Card), "ShowSecretTrigger", Type.EmptyTypes);
            if (method == null || method.IsStatic || method.ReturnType != typeof(void)) return;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NativeSigilGuard), nameof(BeforeShowSecretTrigger)));
        }
        catch (Exception error) { log?.LogWarning("SIGIL_GUARD_INSTALL_FAILED " + error.Message); }
    }

    private static bool BeforeShowSecretTrigger(Card __instance)
    {
        try
        {
            var actor = __instance.GetActor();
            if (actor != null && actor.GetComponent(typeof(Spell)) != null) return true;
            if (warnings++ < 5)
                log?.LogWarning("SIGIL_TRIGGER_NO_ACTOR_SPELL card=" + __instance.GetEntity()?.GetCardId()
                    + " entity=" + __instance.GetEntity()?.GetEntityId() + " actor=" + (actor == null ? "null" : actor.name));
            return false;
        }
        catch (Exception) { return true; }
    }
}
