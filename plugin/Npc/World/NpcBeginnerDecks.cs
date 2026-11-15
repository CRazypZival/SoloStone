using System;
using System.Collections.Generic;
using System.Linq;

// Beginner NPC decks (owner 2026-11-15): what an Easy NPC plays on the Core step below Bronze. One per class, Core cards only,
// coherent but clearly weaker than the player's Core starter (LocalDecks.PlayerCoreStarterDecks): a worse curve, little
// removal, no legendaries. Admission "core" like NpcCoreDecks: never generated, matched or inferred on the ladder, served only to
// an Easy Core-step encounter. A class without a list here falls back to its Core starter. Strength is measured with the
// AiHarness `difficulty` verb (private/docs/roadmap/2026-11-15-npc-difficulty.md); tune the lists, never Hard.
public static class NpcBeginnerDecks
{
    public const string Origin = "beginner", IdPrefix = "npc-beginner-";

    public static string IdFor(string cardClass) => IdPrefix + (cardClass ?? "").ToLowerInvariant();

    // (class, hero, name, archetype, pairs, singles). Pairs are played twice; singles once. 30 cards.
    private static readonly (string Class, string Hero, string Name, DeckArchetype Archetype, string[] Pairs, string[] Singles)[] Lists =
    {
    };

    public static IReadOnlyList<NpcDeckEntry> Entries() => Lists.Select(list => new NpcDeckEntry(IdFor(list.Class), 1, list.Name,
        list.Class, list.Hero, Origin, NpcCoreDecks.Admission, null,
        new NpcDeckTags(list.Archetype, 5, 1, 0, 3, LadderBand.Bronze, LadderBand.Legend),
        new NpcDeckSource("solostone", null, null, null, "Beginner NPC deck"),
        list.Pairs.Select(id => new NpcDeckCard(id, 2)).Concat(list.Singles.Select(id => new NpcDeckCard(id, 1))),
        new NpcDeckSideboard[0])).ToArray();
}
