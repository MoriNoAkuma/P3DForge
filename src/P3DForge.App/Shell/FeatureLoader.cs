using System.Reflection;

namespace P3DForge.App.Shell;

/// <summary>Discovers every <see cref="IFeature"/> in the app assembly and installs it once.</summary>
public static class FeatureLoader
{
    static bool installed;

    public static void InstallAll()
    {
        if (installed) return;
        installed = true;
        var features = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => typeof(IFeature).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IFeature)Activator.CreateInstance(t)!)
            .OrderBy(f => f.Order)
            .ThenBy(f => f.GetType().FullName, StringComparer.Ordinal);
        foreach (var f in features)
        {
            try
            {
                f.Install();
            }
            catch (Exception ex)
            {
                AppServices.Editor.Log($"Feature {f.GetType().Name} failed to install: {ex.Message}", Editor.LogLevel.Error);
            }
        }
    }
}
