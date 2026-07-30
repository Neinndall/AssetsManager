using System.Collections;

namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Defines the shared state required for single selection.
    /// </summary>
    public interface ISelectable
    {
        bool IsSelected { get; set; }
    }

    /// <summary>
    /// Defines the shared state required for single and multiple selection.
    /// </summary>
    public interface IMultiSelectable : ISelectable
    {
        bool IsMultiSelected { get; set; }
    }

    /// <summary>
    /// Exposes the visible hierarchy required by shared tree selection.
    /// </summary>
    public interface ISelectableTreeNode : IMultiSelectable
    {
        IEnumerable SelectionChildren { get; }
        bool IsExpanded { get; set; }
        bool IsSelectionVisible { get; }
    }
}
