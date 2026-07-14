namespace AssetsManager.Views.Helpers
{
    /// <summary>
    /// Define una interfaz común para objetos que soportan selección múltiple.
    /// </summary>
    public interface IMultiSelectable
    {
        bool IsMultiSelected { get; set; }
    }
}
