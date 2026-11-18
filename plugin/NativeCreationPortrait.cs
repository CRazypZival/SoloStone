using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Nefarian's Creation (CATA_470t1) takes the art of the first Dragon picked for it (issue #119). The engine publishes the card as
// CATA_470t1 with the dragon's dbf id in MODULAR_ENTITY_PART_1 (tag 471); the client draws a card's portrait from the CardDef of the
// entity's own card id, so this postfix swaps in the portrait texture of the part's CardDef after the actor has set its own.
// Name, text, sounds and effects stay the Creation's. The patched methods (Actor.UpdatePortraitTexture, HistoryCard.LoadTile) are optional:
// a miss logs one warning and leaves the stock art. The client members this file names directly (see its rows in client-bindings.txt)
// are hard rows: a retail rename of one refuses the launch.
internal static class NativeCreationPortrait
{
    internal const string CreationId = "CATA_470t1";
    internal const int FirstPartTag = 471;
    private static ManualLogSource log;
    private static readonly Dictionary<(int, TAG_PREMIUM), Texture> Textures = new Dictionary<(int, TAG_PREMIUM), Texture>();
    // Kept alive for the session: the texture lease is released with its CardDef.
    private static readonly List<DefLoader.DisposableCardDef> Defs = new List<DefLoader.DisposableCardDef>();
    private static readonly List<object> Handles = new List<object>();
    private static MethodInfo getPortraitTexture;
    private static readonly HashSet<(int, TAG_PREMIUM)> Failed = new HashSet<(int, TAG_PREMIUM)>();
    private static readonly HashSet<int> Warned = new HashSet<int>();

    internal static void Install(Harmony harmony, ManualLogSource logger)
    {
        log = logger;
        try
        {
            var original = AccessTools.Method(typeof(Actor), "UpdatePortraitTexture") ?? throw new MissingMethodException("Actor", "UpdatePortraitTexture");
            harmony.Patch(original, postfix: new HarmonyMethod(typeof(NativeCreationPortrait), nameof(UseFirstDragonPortrait)));
            // Cosmetic: a client rename must never refuse a launch over a portrait (the stock art stays).
            ClientChecks.NoteSelfAdapting(original);
        }
        catch (Exception error) { log?.LogWarning("creation portrait hook unavailable: " + error.Message); }
        InstallTile(harmony);
    }

    // The history bar tile copies the entity's own portrait into its material without going through UpdatePortraitTexture, so its
    // init info is patched before HistoryCard.LoadTile reads it. Found by name: a client without it keeps the stock tile.
    private static FieldInfo tileEntity, tilePortrait;
    private static void InstallTile(Harmony harmony)
    {
        try
        {
            var card = AccessTools.TypeByName("HistoryCard") ?? throw new MissingMemberException("HistoryCard");
            var load = AccessTools.Method(card, "LoadTile") ?? throw new MissingMethodException("HistoryCard", "LoadTile");
            var info = load.GetParameters().Single().ParameterType;
            tileEntity = AccessTools.Field(info, "m_entity") ?? throw new MissingFieldException(info.Name, "m_entity");
            tilePortrait = AccessTools.Field(info, "m_portraitTexture") ?? throw new MissingFieldException(info.Name, "m_portraitTexture");
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(NativeCreationPortrait), nameof(UseFirstDragonTile)));
            ClientChecks.NoteSelfAdapting(load);
        }
        catch (Exception error) { log?.LogWarning("creation history tile hook unavailable: " + error.Message); }
    }

    private static void UseFirstDragonTile(object __0)
    {
        try
        {
            if (__0 == null || !(tileEntity.GetValue(__0) is Entity entity)) return;
            var texture = FirstDragonPortrait(entity, (TAG_PREMIUM)entity.GetTag(GAME_TAG.PREMIUM));
            if (texture != null) tilePortrait.SetValue(__0, texture);
        }
        catch (Exception error) { Fail(0, TAG_PREMIUM.NORMAL, error); }
    }

    private static Texture FirstDragonPortrait(Entity entity, TAG_PREMIUM premium)
    {
        int part = PartDbfId(entity.GetCardId(), id => entity.GetTag((GAME_TAG)id));
        return part == 0 ? null : PortraitOf(part, premium);
    }

    // The dragon the Creation borrows from: its part tag when the entity is the Creation, else 0.
    internal static int PartDbfId(string cardId, Func<int, int> tag) => cardId == CreationId ? tag(FirstPartTag) : 0;

    private static void UseFirstDragonPortrait(Actor __instance)
    {
        try
        {
            var entity = __instance?.GetEntity();
            if (entity == null) return;
            var texture = FirstDragonPortrait(entity, __instance.GetPremium());
            if (texture != null) __instance.SetPortraitTexture(texture);
        }
        catch (Exception error) { Fail(0, TAG_PREMIUM.NORMAL, error); }
    }

    private static Texture PortraitOf(int dbfId, TAG_PREMIUM premium)
    {
        var key = (dbfId, premium);
        // A texture Unity has destroyed (Unity null check) is dropped and loaded again.
        if (Textures.TryGetValue(key, out var known)) { if (known != null) return known; Textures.Remove(key); }
        if (Failed.Contains(key)) return null;
        try
        {
            var def = DefLoader.Get()?.GetCardDef(dbfId);
            if (def?.CardDef == null) { def?.Dispose(); Fail(dbfId, premium, new InvalidOperationException("part CardDef unavailable")); return null; }
            Defs.Add(def);
            // AssetHandle<Texture> lives in an assembly the plugin does not reference, so it is read by reflection.
            object handle = (getPortraitTexture ?? (getPortraitTexture = typeof(CardDef).GetMethod("GetPortraitTexture",
                new[] { typeof(TAG_PREMIUM), typeof(bool), typeof(bool) }))).Invoke(def.CardDef, new object[] { premium, true, false });
            Handles.Add(handle);
            // Not op_Implicit: the handle has two (to the asset and to bool), so selecting one by its parameter is ambiguous.
            var texture = handle?.GetType().GetProperty("Asset", BindingFlags.Public | BindingFlags.Instance)?.GetValue(handle, null) as Texture;
            if (texture == null) { Fail(dbfId, premium, new InvalidOperationException("part has no portrait texture")); return null; }
            Textures[(dbfId, premium)] = texture;
            log?.LogInfo("creation portrait: part " + dbfId + " " + premium);
            return texture;
        }
        catch (Exception error) { Fail(dbfId, premium, error); return null; }
    }

    private static void Fail(int dbfId, TAG_PREMIUM premium, Exception error)
    {
        if (dbfId != 0) Failed.Add((dbfId, premium));
        if (!Warned.Add(dbfId * 8 + (int)premium)) return;
        log?.LogWarning("creation portrait unavailable" + (dbfId == 0 ? "" : " for part " + dbfId) + ": " + error.Message);
    }
}
