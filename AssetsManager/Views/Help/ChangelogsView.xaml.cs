using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Material.Icons;
using AssetsManager.Services;
using AssetsManager.Services.Core;

namespace AssetsManager.Views.Help
{
    public class ChangeItem
    {
        public string Text { get; set; }
        public bool IsSubheading { get; set; }
        public bool IsDescription { get; set; }
        public int IndentationLevel { get; set; }
    }

    public class ChangelogVersion
    {
        public string Version { get; set; }
        public List<ChangeGroup> Groups { get; set; } = new List<ChangeGroup>();
    }

    public class ChangeGroup
    {
        public string Title { get; set; }
        public MaterialIconKind Icon { get; set; }
        public SolidColorBrush IconColor { get; set; }
        public List<ChangeItem> Changes { get; set; } = new List<ChangeItem>();
    }

    public partial class ChangelogsView : UserControl
    {
        private readonly LogService _logService;

        public ChangelogsView(LogService logService)
        {
            InitializeComponent();
            _logService = logService;
            LoadChangelogs();
        }

        private void LoadChangelogs()
        {
            try
            {
                var changelogText = LoadEmbeddedChangelog();
                var changelogData = ParseChangelog(changelogText);
                ChangelogItemsControl.ItemsSource = changelogData;
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to load or parse changelog.txt.");
            }
        }

        private string LoadEmbeddedChangelog()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "AssetsManager.changelogs.txt";
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return string.Empty;
                using (StreamReader reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private List<ChangelogVersion> ParseChangelog(string changelogText)
        {
            var versions = new List<ChangelogVersion>();
            if (string.IsNullOrWhiteSpace(changelogText)) return versions;

            var lines = changelogText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

            ChangelogVersion currentVersion = null;
            ChangeGroup currentGroup = null;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (line.StartsWith("AssetsManager - League of Legends |"))
                {
                    currentVersion = new ChangelogVersion { Version = trimmedLine };
                    versions.Add(currentVersion);
                    currentGroup = null;
                    continue;
                }

                if (currentVersion == null) continue;
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith(">>>>>")) continue;

                if (!line.StartsWith(" ") && !line.StartsWith("\t") && !trimmedLine.StartsWith("*") && !trimmedLine.StartsWith("-"))
                {
                    if (trimmedLine.Split(' ').Length <= 3)
                    {
                        currentGroup = new ChangeGroup { Title = trimmedLine };
                        string titleLower = trimmedLine.ToLower();
                        if (titleLower.Contains("major update")) { currentGroup.Icon = MaterialIconKind.AlertDecagram; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#F44336"); }
                        else if (titleLower.Contains("medium update")) { currentGroup.Icon = MaterialIconKind.ArrowUpCircleOutline; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#2196F3"); }
                        else if (titleLower.Contains("hotfix update")) { currentGroup.Icon = MaterialIconKind.Wrench; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#2196F3"); }
                        else if (titleLower.Contains("app name")) { currentGroup.Icon = MaterialIconKind.Label; currentGroup.IconColor = (SolidColorBrush)Application.Current.FindResource("TextMuted"); }
                        else if (titleLower.Contains("new features")) { currentGroup.Icon = MaterialIconKind.Star; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFC107"); }
                        else if (titleLower.Contains("improvements")) { currentGroup.Icon = MaterialIconKind.ArrowUpBoldCircle; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#4CAF50"); }
                        else if (titleLower.Contains("bug fixes")) { currentGroup.Icon = MaterialIconKind.Bug; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#F44336"); }
                        else if (titleLower.Contains("changes")) { currentGroup.Icon = MaterialIconKind.Pencil; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#2196F3"); }
                        else if (titleLower.Contains("notes")) { currentGroup.Icon = MaterialIconKind.Notebook; currentGroup.IconColor = (SolidColorBrush)Application.Current.FindResource("TextMuted"); }
                        else { currentGroup.Icon = MaterialIconKind.Info; currentGroup.IconColor = (SolidColorBrush)Application.Current.FindResource("TextMuted"); }
                        currentVersion.Groups.Add(currentGroup);
                    }
                    else
                    {
                        if (currentGroup != null)
                        {
                            var item = new ChangeItem { IsDescription = true, Text = trimmedLine, IndentationLevel = 1 };
                            currentGroup.Changes.Add(item);
                        }
                    }
                }
                else if (currentGroup != null)
                {
                    var indentation = line.Length - line.TrimStart().Length;
                    var item = new ChangeItem
                    {
                        IndentationLevel = indentation / 4
                    };

                    if (trimmedLine.StartsWith("-"))
                    {
                        item.IsSubheading = true;
                        item.Text = trimmedLine.Substring(1).Trim();
                    }
                    else if (trimmedLine.StartsWith("*"))
                    {
                        item.IsDescription = false;
                        item.Text = trimmedLine.Substring(1).Trim();
                    }
                    else
                    {
                        item.IsDescription = true;
                        item.Text = trimmedLine;
                        item.IndentationLevel = 1;
                    }
                    currentGroup.Changes.Add(item);
                }
            }
            return versions;
        }
    }

}
