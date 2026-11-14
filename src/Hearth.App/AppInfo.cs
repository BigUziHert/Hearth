namespace Hearth.App;
internal static class AppInfo
{
    public const string DefaultUpdateRepository = "BigUziHert/Hearth";
    public static readonly Version Version = new((typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0)).ToString(3));
}
