using System;
using System.Collections.Generic;
using System.Linq;
using SabberStoneCore.Auras;
using SabberStoneCore.Enums;
using SabberStoneCore.Model;
using SabberStoneCore.Model.Entities;

namespace SabberStoneCore.Actions
{
    public static class DeathrattleRules
    {
        internal static bool IsMultiplier(GameTag tag) => tag == GameTag.EXTRA_DEATHRATTLES_BASE
            || tag == GameTag.EXTRA_MINION_DEATHRATTLES_BASE || tag == GameTag.EXTRA_DEATHRATTLES_ADDITIONAL;
        internal static int AuraValue(Controller owner, GameTag tag)
        {
            int value = owner.Game.Auras?.OfType<Aura>().Sum(a => a.DeathrattleValue(owner,tag)) ?? 0;
            return tag == GameTag.EXTRA_DEATHRATTLES_ADDITIONAL ? value : Math.Min(1,Math.Max(0,value));
        }
        /// <summary>Minions that die in one death phase die at the same moment: a Deathrattle multiplier (Baron Rivendare) dying
        /// with them still counts for each of them. The repeats are read once, before the first one leaves the board.</summary>
        internal static void CaptureBatch(Game game)
        {
            game.DeathrattleBatch = null;
            foreach (var minion in game.DeadMinions)
                if (minion.HasDeathrattle) (game.DeathrattleBatch ??= new Dictionary<int, int>())[minion.Id] = Repeats(minion.Controller, minion);
        }
        public static int Repeats(Controller owner, IEntity source)
        {
            if (source != null && owner.Game.DeathrattleBatch != null && owner.Game.DeathrattleBatch.TryGetValue(source.Id, out int captured)) return captured;
            return 1
                + (owner[GameTag.EXTRA_DEATHRATTLES_BASE] > 0
                    || source is Minion && owner[GameTag.EXTRA_MINION_DEATHRATTLES_BASE] > 0 ? 1 : 0)
                + Math.Max(0, owner[GameTag.EXTRA_DEATHRATTLES_ADDITIONAL]);
        }
    }
}
