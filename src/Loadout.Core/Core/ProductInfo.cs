using System.Reflection;

namespace Loadout.Core;

public static class ProductInfo
{
    public const string Name = "Loadout for PS2";

    /// <summary>
    /// The version this Loadout was built as, the whole of VERSION, such as <c>0.9.2</c>. Loadout Bay built
    /// from the same commit has the same version.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
