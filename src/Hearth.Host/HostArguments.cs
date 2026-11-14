namespace Hearth.Host;

public sealed record HostArguments(string Data, bool Initialize, string? OwnerTokenFile, int? Port, string? Bind)
{
    public static HostArguments Parse(string[] args)
    {
        string? Value(string name)
        {
            var index = Array.IndexOf(args, name);
            if (index < 0) return null;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new ArgumentException($"Missing {name} value.");
            return args[index + 1];
        }
        var portValue = Value("--port");
        int? port = portValue is null ? null : int.Parse(portValue);
        if (port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        var bind = Value("--bind");
        if (bind is not null && !System.Net.IPAddress.TryParse(bind, out _)) throw new ArgumentException("Bind must be an IP address.");
        return new(Path.GetFullPath(Value("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hearth", "Host")),
            args.Contains("--init"), Value("--owner-token-file"), port, bind);
    }
}
