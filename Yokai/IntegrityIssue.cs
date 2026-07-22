namespace Lycoris.Yokai
{
    /// <summary>One problem found by the integrity checker (a dangling reference, a duplicate key, …).</summary>
    public sealed class IntegrityIssue
    {
        public string Category { get; set; }   // "Move", "Évolution", "Drop", "Nom", "Doublon"…
        public IssueLevel Level { get; set; }   // Error (breaks the game) vs Warning (cosmetic)
        public string Subject { get; set; }     // the yo-kai / skill / item concerned
        public string Detail { get; set; }      // human description of the problem

        /// <summary>True if this problem already existed in the files at load (not caused by this session's edits).</summary>
        public bool Preexisting { get; set; }

        public string LevelText => Level == IssueLevel.Error ? "Erreur" : "Avertissement";
        public string OriginText => Preexisting ? "préexistant" : "nouveau";
    }

    public enum IssueLevel { Error, Warning }
}
