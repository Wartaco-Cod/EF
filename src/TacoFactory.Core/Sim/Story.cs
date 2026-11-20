namespace TacoFactory.Core.Sim;

/// <summary>Story rules shared by the server and tests.</summary>
public static class Story
{
    public static readonly string[] Endings = ["rescue", "vengeance", "darkness", "redemption"];

    /// <summary>
    /// The four endings, from a player's own story unlocks: The Darkness if every enemy who yielded was put to death;
    /// The Rescue if the proof was secured, Thomas trusts you and no more than one of them was killed; The Vengeance if
    /// Clement was condemned; otherwise The Redemption.
    /// </summary>
    public static string Ending(ICollection<string> unlocks)
    {
        int ruthless = new[] { "reginald_killed", "arnaud_killed", "clement_condemned" }.Count(unlocks.Contains);
        if (ruthless == 3) return "darkness";
        if (unlocks.Contains("proof_secured") && unlocks.Contains("thomas_trust") && ruthless <= 1) return "rescue";
        if (unlocks.Contains("clement_condemned")) return "vengeance";
        return "redemption";
    }
}
