using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AssetsManager.Views.Helpers
{
    public class VisualHost : FrameworkElement
    {
        private readonly List<Visual> _visuals = new List<Visual>();

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index)
        {
            return _visuals[index];
        }

        public void AddVisual(Visual visual)
        {
            _visuals.Add(visual);
            AddVisualChild(visual);
            AddLogicalChild(visual);
        }

        public void RemoveVisual(Visual visual)
        {
            _visuals.Remove(visual);
            RemoveVisualChild(visual);
            RemoveLogicalChild(visual);
        }

        public void ClearVisuals()
        {
            foreach (var visual in _visuals)
            {
                RemoveVisualChild(visual);
                RemoveLogicalChild(visual);
            }
            _visuals.Clear();
        }
    }
}
