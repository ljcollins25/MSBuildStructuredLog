using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Logging.StructuredLogger;
using StructuredLogViewer.Core;

#nullable enable

namespace StructuredLogViewer
{
    public class SettingsService
    {
        private const int maxCount = 10;

        /// <summary>Where settings are kept; a head without a file system (the browser) replaces it at startup.</summary>
        public static ISettingsStore Store { get; set; } = new FileSettingsStore();

        // TODO: protect access to these with a Mutex
        private static readonly string recentLogsFilePath = Path.Combine(GetRootPath(), "RecentLogs.txt");
        private static readonly string recentProjectsFilePath = Path.Combine(GetRootPath(), "RecentProjects.txt");
        private static readonly string recentMSBuildLocationsFilePath = Path.Combine(GetRootPath(), "RecentMSBuildLocations.txt");
        private static readonly string recentSearchesFilePath = Path.Combine(GetRootPath(), "Recent$Searches.txt");
        private static readonly string customArgumentsFilePath = Path.Combine(GetRootPath(), "CustomMSBuildArguments.txt");
        private static readonly string disableUpdatesFilePath = Path.Combine(GetRootPath(), "DisableUpdates.txt");
        private static readonly string settingsFilePath = Path.Combine(GetRootPath(), "Settings.txt");
        private static readonly string tempFolder = Path.Combine(GetRootPath(), "Temp");

        private static bool settingsRead = false;

        public static void AddRecentLogFile(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            AddRecentItem(fullPath, recentLogsFilePath);
        }

        public static void AddRecentProject(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            AddRecentItem(fullPath, recentProjectsFilePath);
        }

        public static void AddRecentMSBuildLocation(string filePath)
        {
            var fullPath = Path.GetFullPath(filePath);
            cachedRecentMSBuildLocations = null;
            AddRecentItem(fullPath, recentMSBuildLocationsFilePath);
        }

        private static IEnumerable<string>? cachedRecentMSBuildLocations;

        public static IEnumerable<string> GetRecentMSBuildLocations(IEnumerable<string>? extraLocations = null)
        {
            extraLocations = extraLocations ?? Enumerable.Empty<string>();

            if (cachedRecentMSBuildLocations == null)
            {
                cachedRecentMSBuildLocations = GetRecentItems(recentMSBuildLocationsFilePath)
                    .Where(File.Exists)
                    .Union(extraLocations, StringComparer.OrdinalIgnoreCase)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            return cachedRecentMSBuildLocations;
        }

        public static void AddRecentSearchText(string searchText, bool discardPrefixes = false, string? recentItemsCategory = null)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return;
            }

            var filePath = GetRecentSearchFilePath(recentItemsCategory ?? "");
            AddRecentItem(searchText, filePath, discardPrefixes);
        }

        public static IEnumerable<string> GetRecentLogFiles()
        {
            return GetRecentItems(recentLogsFilePath);
        }

        public static IEnumerable<string> GetRecentProjects()
        {
            return GetRecentItems(recentProjectsFilePath);
        }

        public static void RemoveRecentLogFile(string filePath)
        {
            RemoveRecentItem(filePath, recentLogsFilePath);
        }

        public static void RemoveAllRecentLogFiles()
        {
            SaveText(recentLogsFilePath, Enumerable.Empty<string>());
        }

        public static void RemoveRecentProject(string filePath)
        {
            RemoveRecentItem(filePath, recentProjectsFilePath);
        }

        public static void RemoveAllRecentProjects()
        {
            SaveText(recentProjectsFilePath, Enumerable.Empty<string>());
        }

        public static void RemoveAllRecentSearchText(string recentItemsCategory = "")
        {
            var file = GetRecentSearchFilePath(recentItemsCategory);
            SaveText(file, Enumerable.Empty<string>());
        }

        private static string GetRecentSearchFilePath(string category = "")
        {
            return recentSearchesFilePath.Replace("$", category);
        }

        public static IEnumerable<string> GetRecentSearchStrings(string category = "")
        {
            var filePath = GetRecentSearchFilePath(category);
            return GetRecentItems(filePath);
        }

        private static void AddRecentItem(string item, string storageFilePath, bool discardPrefixes = false)
        {
            var list = GetRecentItems(storageFilePath).ToList();
            if (AddOrPromote(list, item, discardPrefixes))
            {
                SaveText(storageFilePath, list);
            }
        }

        private static void RemoveRecentItem(string item, string storageFilePath)
        {
            var list = GetRecentItems(storageFilePath).ToList();
            if (list.Remove(item))
            {
                SaveText(storageFilePath, list);
            }
        }

        private static IEnumerable<string> GetRecentItems(string storageFilePath)
        {
            using (Store.Lock(Path.GetFileName(storageFilePath)))
            {
                if (!Store.Exists(storageFilePath))
                {
                    return Array.Empty<string>();
                }

                var lines = Store.ReadAllLines(storageFilePath);
                return lines;
            }
        }

        public static string GetRootPath()
        {
            if (Environment.GetEnvironmentVariable("MSBUILDSTRUCTUREDLOG_DATA_DIR") is {} dataDir)
            {
                return Path.GetFullPath(dataDir);
            }

            var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            path = Path.Combine(path, "Microsoft", "MSBuildStructuredLog");
            return path;
        }

        private static void SaveText(string storageFilePath, IEnumerable<string> lines)
        {
            using (Store.Lock(Path.GetFileName(storageFilePath)))
            {
                Store.WriteAllLines(storageFilePath, lines);
            }
        }

        private static bool AddOrPromote(List<string> list, string item, bool discardPrefixes = false)
        {
            if (list.Count > 0 && list[0] == item)
            {
                // if the first item is exact match, don't do anything
                return false;
            }

            int index = list.FindIndex(i => StringComparer.OrdinalIgnoreCase.Compare(i, item) == 0);
            if (index >= 0)
            {
                list.RemoveAt(index);
            }
            else if (discardPrefixes)
            {
                index = list.FindIndex(i => item.StartsWith(i));
                if (index >= 0)
                {
                    list.RemoveAt(index);
                }
            }

            if (list.Count >= maxCount)
            {
                list.RemoveAt(list.Count - 1);
            }

            list.Insert(0, item);
            return true;
        }

        public static string? GetMSBuildFile()
        {
            return GetRecentMSBuildLocations().FirstOrDefault();
        }

        private const string DefaultArguments = "/t:Rebuild";

        public static string GetCustomArguments(string filePath)
        {
            string[] lines;

            using (Store.Lock(Path.GetFileName(customArgumentsFilePath)))
            {
                if (!Store.Exists(customArgumentsFilePath))
                {
                    return DefaultArguments;
                }

                lines = Store.ReadAllLines(customArgumentsFilePath);
            }

            if (FindArguments(lines, filePath, out string? arguments, out int index))
            {
                return arguments;
            }

            var mostRecentArguments = TextUtilities.ParseNameValue(lines[0]);
            return mostRecentArguments.Value;
        }

        private const int MaximumProjectsInRecentArgumentsList = 100;

        /// <summary>
        /// Just an escape hatch in case some users might want it
        /// </summary>
        public static bool DisableUpdates => Store.Exists(disableUpdatesFilePath);

        private static bool FindArguments(IList<string> lines, string projectFilePath, [NotNullWhen(returnValue: true)] out string? existingArguments, out int index)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals == -1)
                {
                    continue;
                }

                string project = line.Substring(0, equals);
                string arguments = line.Substring(equals + 1, line.Length - equals - 1);
                if (string.Equals(projectFilePath, project, StringComparison.OrdinalIgnoreCase))
                {
                    existingArguments = arguments;
                    index = i;
                    return true;
                }
            }

            existingArguments = null;
            index = -1;
            return false;
        }

        public static void SaveCustomArguments(string projectFilePath, string newArguments)
        {
            using (Store.Lock(Path.GetFileName(customArgumentsFilePath)))
            {
                if (!Store.Exists(customArgumentsFilePath))
                {
                    if (newArguments == DefaultArguments)
                    {
                        return;
                    }
                    else
                    {
                        Store.WriteAllLines(customArgumentsFilePath, new[] {projectFilePath + "=" + newArguments});
                        return;
                    }
                }

                var list = Store.ReadAllLines(customArgumentsFilePath).ToList();

                if (FindArguments(list, projectFilePath, out string? arguments, out int index))
                {
                    list.RemoveAt(index);
                }

                list.Insert(0, projectFilePath + "=" + newArguments);
                if (list.Count >= MaximumProjectsInRecentArgumentsList)
                {
                    list.RemoveAt(list.Count - 1);
                }

                Store.WriteAllLines(customArgumentsFilePath, list);
            }
        }

        private static T Get<T>(ref T backingField)
        {
            EnsureSettingsRead();
            return backingField;
        }

        private static void Set<T>(ref T backingField, T value)
        {
            if (backingField == null && value == null
                || (backingField?.Equals(value) ?? false))
            {
                return;
            }

            backingField = value;
            SaveSettings();
        }

        private static bool enableTreeViewVirtualization = true;

        public static bool EnableTreeViewVirtualization
        {
            get => Get(ref enableTreeViewVirtualization);

            set => Set(ref enableTreeViewVirtualization, value);
        }

        private static bool virtualizedTree = false;

        /// <summary>Avalonia: show the main tree as a flat virtualized list instead of the TreeView.</summary>
        public static bool VirtualizedTree
        {
            get => Get(ref virtualizedTree);

            set => Set(ref virtualizedTree, value);
        }

        /// <summary>Default for the virtualized tree until a saved choice is read (the saved line always wins).</summary>
        public static void SetDefaultVirtualizedTree(bool on) => virtualizedTree = on;

        private static bool markResultsInTree = false;

        public static bool MarkResultsInTree
        {
            get => Get(ref markResultsInTree);

            set => Set(ref markResultsInTree, value);
        }

        public static bool ShowConfigurationAndPlatform
        {
            get => Get(ref ProjectOrEvaluationHelper.ShowConfigurationAndPlatform);

            set
            {
                if (ProjectOrEvaluationHelper.ShowConfigurationAndPlatform == value)
                {
                    return;
                }

                ProjectOrEvaluationHelper.ShowConfigurationAndPlatform = value;
                ProjectOrEvaluationHelper.ClearCache();
                SaveSettings();
            }
        }

        private static bool useDarkTheme = false;
        private static bool useDarkThemeChosen = false;

        /// <summary>True once the user has chosen a theme (or a saved choice was read); until then
        /// <see cref="SetDefaultUseDarkTheme"/> (e.g. the OS / browser preference) decides.</summary>
        public static bool UseDarkThemeChosen
        {
            get { EnsureSettingsRead(); return useDarkThemeChosen; }
        }

        public static bool UseDarkTheme
        {
            get => Get(ref useDarkTheme);

            set
            {
                EnsureSettingsRead();
                if (!useDarkThemeChosen)
                {
                    useDarkThemeChosen = true;
                    useDarkTheme = value;
                    SaveSettings();
                    return;
                }

                Set(ref useDarkTheme, value);
            }
        }

        /// <summary>Theme to use while the user has not chosen one; not persisted.</summary>
        public static void SetDefaultUseDarkTheme(bool dark)
        {
            EnsureSettingsRead();
            if (!useDarkThemeChosen)
            {
                useDarkTheme = dark;
            }
        }

        private static bool vsCodeHintDismissed = false;
        public static bool VSCodeHintDismissed
        {
            get => Get(ref vsCodeHintDismissed);

            set => Set(ref vsCodeHintDismissed, value);
        }

        private static string? preferredVSCodeVariant;
        public static string? PreferredVSCodeVariant
        {
            get => Get(ref preferredVSCodeVariant);

            set => Set(ref preferredVSCodeVariant, value);
        }

        private static string? windowPosition;
        public static string? WindowPosition
        {
            get => Get(ref windowPosition);

            set => Set(ref windowPosition, value);
        }

        // separate from WindowPosition so the WPF and Avalonia viewers don't
        // overwrite each other's saved position (the formats are incompatible)
        private static string? avaloniaWindowPosition;
        public static string? AvaloniaWindowPosition
        {
            get => Get(ref avaloniaWindowPosition);

            set => Set(ref avaloniaWindowPosition, value);
        }

        private static string? ignoreEmbeddedFiles;
        public static string? IgnoreEmbeddedFiles
        {
            get => Get(ref ignoreEmbeddedFiles);

            set => Set(ref ignoreEmbeddedFiles, value);
        }

        private static void EnsureSettingsRead()
        {
            if (!settingsRead)
            {
                ReadSettings();
                settingsRead = true;
            }
        }

        const string Virtualization = "Virtualization=";
        const string MarkResultsInTreeSetting = "MarkResultsInTree=";
        const string VirtualizedTreeSetting = "VirtualizedTree=";
        const string ShowConfigurationAndPlatformSetting = "ShowConfigurationAndPlatform=";
        const string UseDarkThemeSetting = "UseDarkTheme=";
        const string VSCodeHintDismissedSetting = "VSCodeHintDismissed=";
        const string PreferredVSCodeVariantSetting = "PreferredVSCodeVariant=";
        const string WindowPositionSetting = "WindowPosition=";
        const string AvaloniaWindowPositionSetting = "AvaloniaWindowPosition=";
        const string IgnoreEmbeddedFilesSetting = "IgnoreEmbeddedFiles=";

        private static void SaveSettings()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Virtualization + enableTreeViewVirtualization.ToString());
            //sb.AppendLine(ParentAllTargetsUnderProjectSetting + parentAllTargetsUnderProject.ToString());
            sb.AppendLine(MarkResultsInTreeSetting + markResultsInTree.ToString());
            sb.AppendLine(VirtualizedTreeSetting + virtualizedTree.ToString());
            sb.AppendLine(ShowConfigurationAndPlatformSetting + ShowConfigurationAndPlatform.ToString());
            if (useDarkThemeChosen)
            {
                sb.AppendLine(UseDarkThemeSetting + useDarkTheme.ToString());
            }

            sb.AppendLine(VSCodeHintDismissedSetting + vsCodeHintDismissed.ToString());
            sb.AppendLine(PreferredVSCodeVariantSetting + preferredVSCodeVariant);
            sb.AppendLine(WindowPositionSetting + windowPosition);
            sb.AppendLine(AvaloniaWindowPositionSetting + avaloniaWindowPosition);
            sb.AppendLine(IgnoreEmbeddedFilesSetting + IgnoreEmbeddedFiles);

            using (Store.Lock(Path.GetFileName(settingsFilePath)))
            {
                Store.WriteAllText(settingsFilePath, sb.ToString());
            }
        }

        private static void ReadSettings()
        {
            using (Store.Lock(Path.GetFileName(settingsFilePath)))
            {
                if (!Store.Exists(settingsFilePath))
                {
                    return;
                }

                var lines = Store.ReadAllLines(settingsFilePath);
                foreach (var line in lines)
                {
                    ProcessLine(Virtualization, line, ref enableTreeViewVirtualization);
                    //ProcessLine(ParentAllTargetsUnderProjectSetting, line, ref parentAllTargetsUnderProject);
                    ProcessLine(MarkResultsInTreeSetting, line, ref markResultsInTree);
                    ProcessLine(VirtualizedTreeSetting, line, ref virtualizedTree);
                    ProcessLine(ShowConfigurationAndPlatformSetting, line, ref ProjectOrEvaluationHelper.ShowConfigurationAndPlatform);
                    if (line.StartsWith(UseDarkThemeSetting))
                    {
                        ProcessLine(UseDarkThemeSetting, line, ref useDarkTheme);
                        useDarkThemeChosen = true;
                    }

                    ProcessLine(VSCodeHintDismissedSetting, line, ref vsCodeHintDismissed);
                    ProcessString(PreferredVSCodeVariantSetting, line, ref preferredVSCodeVariant);
                    ProcessString(WindowPositionSetting, line, ref windowPosition);
                    ProcessString(AvaloniaWindowPositionSetting, line, ref avaloniaWindowPosition);
                    ProcessString(IgnoreEmbeddedFilesSetting, line, ref ignoreEmbeddedFiles);

                    void ProcessString(string setting, string text, ref string? variable)
                    {
                        if (!text.StartsWith(setting))
                        {
                            return;
                        }

                        var value = text.Substring(setting.Length);
                        variable = value;
                    }

                    void ProcessLine(string setting, string text, ref bool variable)
                    {
                        if (!text.StartsWith(setting))
                        {
                            return;
                        }

                        var value = text.Substring(setting.Length);
                        if (bool.TryParse(value, out bool boolValue))
                        {
                            variable = boolValue;
                        }
                    }
                }
            }
        }

        private static bool cleanedUpTempFiles = false;

        public static string GetPreprocessedFilePath(string content, string fileExtension = ".xml")
        {
            var folder = tempFolder;
            var filePath = Path.Combine(folder, Utilities.GetMD5Hash(content, 16) + fileExtension);
            return filePath;
        }

        public static string WriteContentToTempFileAndGetPath(string content, string fileExtension)
        {
            var folder = tempFolder;
            var filePath = GetPreprocessedFilePath(content, fileExtension);

            using (Store.Lock(Path.GetFileName(filePath)))
            {
                if (File.Exists(filePath))
                {
                    return filePath;
                }

                Directory.CreateDirectory(folder);
                File.WriteAllText(filePath, content);
            }

            if (!cleanedUpTempFiles)
            {
                System.Threading.Tasks.Task.Run(() => CleanupTempFiles());
            }

            return filePath;
        }

        /// <summary>
        /// Delete temp files older than one month
        /// </summary>
        private static void CleanupTempFiles()
        {
            using (Store.Lock("StructuredLogViewerTempFileCleanup"))
            {
                if (cleanedUpTempFiles)
                {
                    return;
                }

                cleanedUpTempFiles = true;

                var folder = tempFolder;
                try
                {
                    foreach (var file in Directory.GetFiles(folder))
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            if (fileInfo.LastWriteTimeUtc < DateTime.UtcNow - TimeSpan.FromDays(30))
                            {
                                fileInfo.Delete();
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }
    }
}
