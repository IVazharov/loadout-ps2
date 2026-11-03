using System.Reflection;

namespace Loadout.Core;

public static class ProductInfo
{
    public const string Name = "Loadout for PS2";

    /// <summary>
    /// The version this Loadout was built as: VERSION's major.minor, then the build number (the count of
    /// commits after the first), such as <c>0.9.0</c>. Loadout Bay built from the same commit has the same version.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
