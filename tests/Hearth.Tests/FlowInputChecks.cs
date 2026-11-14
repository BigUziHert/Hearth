using Hearth.Core;

internal static class FlowInputChecks
{
    public static void Run(Action<bool, string> check)
    {
        var original = new Invite("https://packs.example.com/hearth", "fixture-pack", "fixture-code");
        check(Invite.Parse("  " + original + "  ") == original, "A copied invite round-trips its host, pack, and access code");
        foreach (var value in new[]
        {
            "not an invite", "hearth://join", "hearth://join?host=https%3A%2F%2Fexample.com&pack=pack",
            "hearth://join?host=https%3A%2F%2Fexample.com&pack=&code=code",
            "hearth://join?host=https%3A%2F%2Fexample.com&pack=pack&code=code&code=duplicate"
        })
        {
            try { Invite.Parse(value); throw new InvalidOperationException("Invalid invite was accepted"); }
            catch (ArgumentException exception)
            {
                check(exception.Message.Contains("invite", StringComparison.OrdinalIgnoreCase), "An incomplete or malformed invite offers an actionable invite error");
            }
        }
        try
        {
            Invite.Parse(new Invite("http://remote.example.com", "pack", "code").ToString());
            throw new InvalidOperationException("Insecure remote invite was accepted");
        }
        catch (ArgumentException exception) { check(exception.Message.Contains("HTTPS"), "Invite validation retains HTTPS protection for remote hosts"); }
    }
}
