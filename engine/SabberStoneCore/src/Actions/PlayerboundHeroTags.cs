using SabberStoneCore.Enums;
using SabberStoneCore.Model.Entities;

namespace SabberStoneCore.Actions
{
    internal static class PlayerboundHeroTags
    {
        private static readonly GameTag[] Tags = { (GameTag)4717 };

        internal static void Move(Hero from, Hero to)
        {
            foreach (var tag in Tags)
            {
                int value = from[tag];
                if (value == 0) continue;
                to[tag] = value;
                from[tag] = 0;
            }
        }
    }
}
