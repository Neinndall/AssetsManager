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
}
