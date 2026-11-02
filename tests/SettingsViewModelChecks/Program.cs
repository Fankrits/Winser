using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

if (args.Length != 1)
{
    throw new ArgumentException("Pass the path to a built Winser.dll.");
}

var assemblyPath = Path.GetFullPath(args[0]);
var appDirectory = Path.GetDirectoryName(assemblyPath)!;
var tempDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
var dataRootName = "Winser-settings-checks-" + Guid.NewGuid().ToString("N");
var dataRoot = Path.GetFullPath(Path.Combine(tempDirectory, dataRootName));
Environment.SetEnvironmentVariable("WINSER_USER_DATA_FOLDER", dataRoot);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var dependency = Path.Combine(appDirectory, name.Name + ".dll");
    return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
};

Assembly? assembly = null;
var selectorsChecked = false;
try
{
    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
    var type = assembly.GetType("Winser.ViewModels.SettingsViewModel", throwOnError: true)!;
    var viewModel = Activator.CreateInstance(type)!;

    CheckIndex("ThemeIndex", initial: 0, invalid: [ -1, 3 ], valid: [ 1, 2 ]);
    CheckIndex("StartupIndex", initial: 2, invalid: [ -1, 3 ], valid: [ 0, 1, 2 ]);
    CheckIndex("TrackingIndex", initial: 2, invalid: [ -1, 4 ], valid: [ 0, 1, 2, 3 ]);

    var searchEngines = (System.Collections.ICollection)type.GetProperty("SearchEngines")!.GetValue(viewModel)!;
    CheckIndex("SearchEngineIndex", initial: 0, invalid: [ -1, searchEngines.Count ], valid: [ 1 ]);

    selectorsChecked = true;

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
    if (assembly is not null)
    {
        assembly.GetType("Winser.Services.AppServices")!.GetMethod("Shutdown")!.Invoke(null, null);
    }

    try
    {
        if (selectorsChecked)
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "Data", "settings.json")));
            var saved = settings.RootElement;
            if (saved.GetProperty("theme").GetInt32() != 2 ||
                saved.GetProperty("startup").GetInt32() != 2 ||
                saved.GetProperty("trackingPrevention").GetInt32() != 3 ||
                saved.GetProperty("searchEngineId").GetString() != "google")
            {
                throw new InvalidOperationException("Valid selector choices were not persisted.");
            }
        }
    }
    finally
    {
        DeleteIsolatedRoot();
    }
}

Console.WriteLine("SettingsViewModel checks passed.");

void DeleteIsolatedRoot()
{
    var target = Path.GetFullPath(dataRoot);
    if (!string.Equals(Path.GetDirectoryName(target), tempDirectory, StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetFileName(target), dataRootName, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Refusing to delete a test data path outside its unique temp folder.");
    }

    if (!Directory.Exists(target))
    {
        return;
    }

    if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
    {
        throw new InvalidOperationException("Refusing to recursively delete a reparse-point test data folder.");
    }

    Directory.Delete(target, recursive: true);
}
