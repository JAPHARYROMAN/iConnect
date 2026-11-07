namespace IConnect.AppModel;

public sealed record DesktopLaunch(string Profile, bool Background, bool Development, bool SignIn = false)
{
    public static DesktopLaunch Parse(string[] arguments)
    {
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iConnect", "desktop");
        var background = false; var development = false; var hasProfile = false; var signIn = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--state-directory":
                    if (hasProfile || ++i >= arguments.Length || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith("--", StringComparison.Ordinal))
                        throw new InvalidDataException("Supply one local profile directory.");
                    profile = arguments[i]; hasProfile = true; break;
                case "--background": background = true; break;
                case "--sign-in": signIn = true; background = true; break;
                case "--development": development = true; break;
                default: throw new InvalidDataException("Unknown iConnect launch option.");
            }
        }
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), background, development, signIn);
    }
}
