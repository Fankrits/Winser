using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 1)
{
    throw new ArgumentException("Pass the path to a built Winser.dll.");
}

var assemblyPath = Path.GetFullPath(args[0]);
var appDirectory = Path.GetDirectoryName(assemblyPath)!;
var dataRoot = Path.Combine(Path.GetTempPath(), "Winser-settings-checks-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("WINSER_USER_DATA_FOLDER", dataRoot);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var dependency = Path.Combine(appDirectory, name.Name + ".dll");
    return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
};

try
{
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    var type = assembly.GetType("Winser.ViewModels.SettingsViewModel", throwOnError: true)!;
    var viewModel = Activator.CreateInstance(type)!;

    CheckIndex("ThemeIndex", initial: 0, invalid: [ -1, 3 ], valid: [ 1, 2 ]);
    CheckIndex("StartupIndex", initial: 2, invalid: [ -1, 3 ], valid: [ 0, 1, 2 ]);
    CheckIndex("TrackingIndex", initial: 2, invalid: [ -1, 4 ], valid: [ 0, 1, 2, 3 ]);

    var searchEngines = (System.Collections.ICollection)type.GetProperty("SearchEngines")!.GetValue(viewModel)!;
    CheckIndex("SearchEngineIndex", initial: 0, invalid: [ -1, searchEngines.Count ], valid: [ 1 ]);

    assembly.GetType("Winser.Services.AppServices")!.GetMethod("Shutdown")!.Invoke(null, null);
    using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "Data", "settings.json")));
    var saved = settings.RootElement;
    if (saved.GetProperty("theme").GetInt32() != 2 ||
        saved.GetProperty("startup").GetInt32() != 2 ||
        saved.GetProperty("trackingPrevention").GetInt32() != 3 ||
        saved.GetProperty("searchEngineId").GetString() != "google")
    {
        throw new InvalidOperationException("Valid selector choices were not persisted.");
    }

    Console.WriteLine("SettingsViewModel checks passed.");

    void CheckIndex(string name, int initial, int[] invalid, int[] valid)
    {
        var property = type.GetProperty(name)!;
        if ((int)property.GetValue(viewModel)! != initial)
        {
            throw new InvalidOperationException($"Unexpected initial {name}.");
        }

        foreach (var value in invalid)
        {
            property.SetValue(viewModel, value);
            if ((int)property.GetValue(viewModel)! != initial)
            {
                throw new InvalidOperationException($"Invalid {name} value {value} changed the selection.");
            }
        }

        foreach (var value in valid)
        {
            property.SetValue(viewModel, value);
            if ((int)property.GetValue(viewModel)! != value)
            {
                throw new InvalidOperationException($"Valid {name} value {value} was not selected.");
            }
        }
    }
}
finally
{
    try
    {
        Directory.Delete(dataRoot, recursive: true);
    }
    catch (DirectoryNotFoundException)
    {
    }
}
