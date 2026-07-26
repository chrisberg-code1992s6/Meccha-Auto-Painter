using System.Reflection;

namespace MecchaCamouflage.Controller;

public static class VersionInfo
{
    public static string Current
    {
        get
        {
            var entry = Assembly.GetEntryAssembly();
            var value = entry?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(item => item.Key == "MecchaAppVersion")?.Value;
            return string.IsNullOrWhiteSpace(value) ? "dev" : value;
        }
    }
}
