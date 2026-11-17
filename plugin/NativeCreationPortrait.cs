using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Nefarian's Creation (CATA_470t1) takes the art of the first Dragon picked for it (issue #119). The engine publishes the card as
// CATA_470t1 with the dragon's dbf id in MODULAR_ENTITY_PART_1 (tag 471); the client draws a card's portrait from the CardDef of the
// entity's own card id, so this postfix swaps in the portrait texture of the part's CardDef after the actor has set its own.
// Name, text, sounds and effects stay the Creation's. Optional hook: a missing member logs one warning and leaves the stock art.
internal static class NativeCreationPortrait
{
    internal const string CreationId = "CATA_470t1";
    internal const int FirstPartTag = 471;
    private static ManualLogSource log;
    private static readonly Dictionary<(int, TAG_PREMIUM), Texture> Textures = new Dictionary<(int, TAG_PREMIUM), Texture>();
    // Kept alive for the session: the texture lease is released with its CardDef.
    private static readonly List<DefLoader.DisposableCardDef> Defs = new List<DefLoader.DisposableCardDef>();
    private static readonly List<object> Handles = new List<object>();
    private static readonly MethodInfo GetPortraitTexture = typeof(CardDef).GetMethod("GetPortraitTexture",
        new[] { typeof(TAG_PREMIUM), typeof(bool), typeof(bool) });
    private static readonly HashSet<int> Failed = new HashSet<int>();

    internal static void Install(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        try
        {
            var original = AccessTools.Method(typeof(Actor), "UpdatePortraitTexture") ?? throw new MissingMethodException("Actor", "UpdatePortraitTexture");
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(NativeCreationPortrait), nameof(UseFirstDragonPortrait)));
        }
        catch (Exception error) { log?.LogWarning("creation portrait hook unavailable: " + error.Message); }
    }

    // The dragon the Creation borrows from: its part tag when the entity is the Creation, else 0.
    internal static int PartDbfId(string cardId, Func<int, int> tag) => cardId == CreationId ? tag(FirstPartTag) : 0;

    private static void UseFirstDragonPortrait(Actor __instance)
    {
        try
        {
            var entity = __instance?.GetEntity();
            if (entity == null) return;
            int part = PartDbfId(entity.GetCardId(), id => entity.GetTag((GAME_TAG)id));
            if (part == 0) return;
            var texture = PortraitOf(part, __instance.GetPremium());
            if (texture != null) __instance.SetPortraitTexture(texture);
        }
        catch (Exception error) { Fail(0, error); }
    }

    private static Texture PortraitOf(int dbfId, TAG_PREMIUM premium)
    {
        if (Textures.TryGetValue((dbfId, premium), out var known)) return known;
        if (Failed.Contains(dbfId)) return null;
        try
        {
            var def = DefLoader.Get()?.GetCardDef(dbfId);
            if (def?.CardDef == null) { def?.Dispose(); Fail(dbfId, new InvalidOperationException("part CardDef unavailable")); return null; }
            Defs.Add(def);
            // AssetHandle<Texture> lives in an assembly the plugin does not reference, so it is read by reflection.
            object handle = GetPortraitTexture.Invoke(def.CardDef, new object[] { premium, true, false });
            Handles.Add(handle);
            // Not op_Implicit: the handle has two (to the asset and to bool), so selecting one by its parameter is ambiguous.
            var texture = handle?.GetType().GetProperty("Asset", BindingFlags.Public | BindingFlags.Instance)?.GetValue(handle, null) as Texture;
            if (texture == null) { Fail(dbfId, new InvalidOperationException("part has no portrait texture")); return null; }
            Textures[(dbfId, premium)] = texture;
            log?.LogInfo("creation portrait: part " + dbfId + " " + premium);
            return texture;
        }
        catch (Exception error) { Fail(dbfId, error); return null; }
    }

    private static void Fail(int dbfId, Exception error)
    {
        if (!Failed.Add(dbfId)) return;
        log?.LogWarning("creation portrait unavailable" + (dbfId == 0 ? "" : " for part " + dbfId) + ": " + error.Message);
    }
}
