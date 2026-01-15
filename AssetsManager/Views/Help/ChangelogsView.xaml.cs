using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Material.Icons;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Help;

namespace AssetsManager.Views.Help
{
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
                
                if (changelogData != null && changelogData.Count > 0)
                {
                    changelogData[0].IsLatest = true;
                    // IsExpanded is false by default
                }

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
            bool isCollectingDescription = false;
            string accumulatedDescription = "";

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // 1. Version Header detection
                if (line.StartsWith("AssetsManager - League of Legends |"))
                {
                    // Save description for previous version if exists
                    if (currentVersion != null && !string.IsNullOrWhiteSpace(accumulatedDescription))
                    {
                        currentVersion.UpdateDescription = accumulatedDescription.Trim();
                    }

                    string verStr = trimmedLine.Split('|').Last().Trim();
                    currentVersion = new ChangelogVersion { Version = verStr };
                    
                    // Auto-determine type based on version number
                    var typeInfo = DetermineUpdateType(verStr);
                    currentVersion.UpdateType = typeInfo.Type;
                    currentVersion.UpdateTypeColor = typeInfo.Color;

                    versions.Add(currentVersion);
                    currentGroup = null;
                    isCollectingDescription = true;
                    accumulatedDescription = "";
                    continue;
                }

                if (currentVersion == null) continue;
                
                // End of version block check
                if (trimmedLine.StartsWith(">>>>>"))
                {
                    isCollectingDescription = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(trimmedLine))
                {
                    if (isCollectingDescription && !string.IsNullOrEmpty(accumulatedDescription))
                    {
                        accumulatedDescription += "\n\n";
                    }
                    continue;
                }

                // Check for explicit UPDATE TYPE lines in text file to ignore them (since we auto-calculate)
                // OR check for Group Titles
                if (!trimmedLine.StartsWith("*") && !trimmedLine.StartsWith("-"))
                {
                    string titleLower = trimmedLine.ToLower();

                    // Check for explicit Update Type Override in text file
                    // Strict check: Must contain keywords AND be short (< 40 chars).
                    if (trimmedLine.Length < 40 && titleLower.Contains("update") && (titleLower.Contains("major") || titleLower.Contains("medium") || titleLower.Contains("hotfix")))
                    {
                        if (titleLower.Contains("major")) 
                        {
                            currentVersion.UpdateType = "MAJOR UPDATE";
                            currentVersion.UpdateTypeColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5252");
                        }
                        else if (titleLower.Contains("medium")) 
                        {
                            currentVersion.UpdateType = "MEDIUM UPDATE";
                            currentVersion.UpdateTypeColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#448AFF");
                        }
                        else if (titleLower.Contains("hotfix")) 
                        {
                            currentVersion.UpdateType = "HOTFIX UPDATE";
                            currentVersion.UpdateTypeColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#FFD740");
                        }
                        continue;
                    }

                    // Check for known Group Titles
                    // Ultra-strict check: Must be an EXACT match for the category titles.
                    bool isGroupTitle = titleLower.Equals("new features") || 
                                      titleLower.Equals("improvements") || 
                                      titleLower.Equals("bug fixes") || 
                                      titleLower.Equals("changes");

                    if (isGroupTitle)
                    {
                        // Stop collecting description as we hit the first group
                        if (isCollectingDescription)
                        {
                            currentVersion.UpdateDescription = accumulatedDescription.Trim();
                            isCollectingDescription = false;
                        }

                        currentGroup = new ChangeGroup { Title = trimmedLine };
                        
                        if (titleLower.Contains("new features")) { currentGroup.Icon = MaterialIconKind.PlusCircleOutline; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#69F0AE"); }
                        else if (titleLower.Contains("improvements")) { currentGroup.Icon = MaterialIconKind.TrendingUp; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#40C4FF"); }
                        else if (titleLower.Contains("bug fixes")) { currentGroup.Icon = MaterialIconKind.BugOutline; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5252"); }
                        else if (titleLower.Contains("changes")) { currentGroup.Icon = MaterialIconKind.Update; currentGroup.IconColor = (SolidColorBrush)new BrushConverter().ConvertFrom("#B2FF59"); }
                        else { currentGroup.Icon = MaterialIconKind.InformationOutline; currentGroup.IconColor = (SolidColorBrush)Application.Current.FindResource("TextSecondary"); }
                        
                        currentVersion.Groups.Add(currentGroup);
                        continue;
                    }
                    
                    // If it's not a group title and not an update type header, and we are collecting description
                    if (isCollectingDescription)
                    {
                        if (!string.IsNullOrEmpty(accumulatedDescription) && !accumulatedDescription.EndsWith("\n\n"))
                            accumulatedDescription += " ";
                        
                        accumulatedDescription += trimmedLine;
                        continue;
                    }
                }
                
                // Process Change Items
                if (currentGroup != null)
                {
                    int spaces = line.TakeWhile(c => c == ' ').Count();
                    int indentation = spaces / 2;

                    var item = new ChangeItem
                    {
                        IndentationLevel = indentation
                    };

                    if (trimmedLine.StartsWith("-"))
                    {
                        // Only treat as a subheading if it's a top-level section title
                        if (indentation <= 1)
                        {
                            item.IsSubheading = true;
                        }
                        else
                        {
                            // Otherwise, it's a nested list item that happens to use a dash
                            item.IsSubheading = false;
                        }
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
                        item.IndentationLevel += 1;
                    }
                    currentGroup.Changes.Add(item);
                }
            }

            // Capture description for the very last version if file ends
            if (currentVersion != null && isCollectingDescription && !string.IsNullOrWhiteSpace(accumulatedDescription))
            {
                currentVersion.UpdateDescription = accumulatedDescription.Trim();
            }

            return versions;
        }

        private (string Type, SolidColorBrush Color) DetermineUpdateType(string version)
        {
            // Remove 'v' prefix if exists
            version = version.ToLower().Replace("v", "");
            
            var parts = version.Split('.');
            if (parts.Length < 2) return ("UPDATE", Brushes.Gray);

            int major = int.Parse(parts[0]);
            int minor = int.Parse(parts[1]);
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            int revision = parts.Length > 3 ? int.Parse(parts[3]) : 0;

            if (revision > 0)
                return ("HOTFIX UPDATE", (SolidColorBrush)new BrushConverter().ConvertFrom("#FFD740")); // Amber
            
            if (build > 0)
                return ("MEDIUM UPDATE", (SolidColorBrush)new BrushConverter().ConvertFrom("#448AFF")); // Blue

            return ("MAJOR UPDATE", (SolidColorBrush)new BrushConverter().ConvertFrom("#FF5252")); // Red
        }

        private void QuickNavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is ChangeGroup targetGroup)
            {
                // Radar Scan: Walk up the tree, and at each step, look down for our target container.
                // This finds the nearest "GroupsContainer" (which is a sibling/cousin in the visual tree).
                var groupsControl = FindSiblingGroupsContainer(btn);

                if (groupsControl != null)
                {
                    // Use the generator to find the actual visual container for the data item
                    var container = groupsControl.ItemContainerGenerator.ContainerFromItem(targetGroup) as FrameworkElement;
                    
                    // Force layout update just in case it's not fully realized yet
                    if (container == null)
                    {
                        groupsControl.UpdateLayout();
                        container = groupsControl.ItemContainerGenerator.ContainerFromItem(targetGroup) as FrameworkElement;
                    }

                    if (container != null)
                    {
                        container.BringIntoView();
                    }
                }
            }
        }

        private ItemsControl FindSiblingGroupsContainer(DependencyObject startNode)
        {
            var current = startNode;
            // Go up up to 10 levels (safety limit)
            for (int i = 0; i < 10; i++)
            {
                current = VisualTreeHelper.GetParent(current);
                if (current == null) return null;

                // Look down from this height
                var found = FindChild<ItemsControl>(current, x => x.Tag is string tag && tag == "GroupsContainer");
                if (found != null) return found;
            }
            return null;
        }

        private static T FindChild<T>(DependencyObject parent, Func<T, bool> predicate) where T : FrameworkElement
        {
            if (parent == null) return null;

            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && predicate(typedChild))
                {
                    return typedChild;
                }

                var foundChild = FindChild<T>(child, predicate);
                if (foundChild != null) return foundChild;
            }

            return null;
        }
    }
}