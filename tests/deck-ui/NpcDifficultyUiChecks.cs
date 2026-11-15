internal static class NpcDifficultyUiChecks
{
    internal static void Run(Action<bool, string> check)
    {
        string selected = NpcDifficultyIds.NewSaveDefault;
        foreach (string id in NpcDifficultyIds.All)
        {
            string before = selected;
            int logs = DifficultySelectionHarness.Logs.Count;
            bool accepted = DifficultySelectionHarness.Choose(NpcDifficultyIds.IsLocked(id), id, () => selected = id);
            check(accepted == NpcDifficultyIds.IsSelectable(id) && selected == (accepted ? id : before),
                "native row selection accepts known choices and keeps locked selection unchanged: " + id);
            check(DifficultySelectionHarness.Logs.Count == logs + (accepted ? 0 : 1), "locked native click logs exactly once");
        }
        string path = Path.GetTempFileName();
        try
        {
            foreach (string id in NpcDifficultyIds.All)
            {
                File.WriteAllText(path, "<LocalProfileData><NpcDifficulty>" + id + "</NpcDifficulty></LocalProfileData>");
                check(DifficultyReadHarness.ReadDifficulty(path) == id, "picker reads root difficulty: " + id);
            }
            File.WriteAllText(path, "<LocalProfileData><History><Match><NpcDifficulty>easy</NpcDifficulty></Match></History></LocalProfileData>");
            check(DifficultyReadHarness.ReadDifficulty(path) == "hard", "old save defaults Hard; match-history difficulty cannot override it");
            File.WriteAllText(path, "<LocalProfileData><NpcDifficulty>medium</NpcDifficulty><History>unread tail");
            check(DifficultyReadHarness.ReadDifficulty(path) == "medium", "picker reads only header field, without deserializing history");
            foreach (string xml in new[] {
                "<LocalProfileData><NpcDifficulty>unknown</NpcDifficulty></LocalProfileData>",
                "<!DOCTYPE LocalProfileData [<!ENTITY level 'easy'>]><LocalProfileData><NpcDifficulty>&level;</NpcDifficulty></LocalProfileData>",
                "<LocalProfileData><broken>"
            })
            {
                File.WriteAllText(path, xml);
                bool refused = false;
                try { DifficultyReadHarness.ReadDifficulty(path); }
                catch (Exception error) when (error is InvalidOperationException || error is System.Xml.XmlException) { refused = true; }
                check(refused, "picker refuses unknown difficulty, DTD and unreadable XML");
            }
        }
        finally { File.Delete(path); }
    }
}
