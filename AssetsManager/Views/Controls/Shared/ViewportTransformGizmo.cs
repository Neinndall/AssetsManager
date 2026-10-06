using System.Windows;
using System.Windows.Media;

namespace AssetsManager.Views.Controls.Shared
{
    /// <summary>Draws the shared viewport gizmo without per-axis layout bounds.</summary>
    public sealed class ViewportTransformGizmo : FrameworkElement
    {
        private static readonly Pen XPen = AxisPen(0xff, 0x5a, 0x5f);
        private static readonly Pen YPen = AxisPen(0x35, 0xe5, 0x8a);
        private static readonly Pen ZPen = AxisPen(0x4d, 0xa3, 0xff);
        private static readonly Brush OriginFill = FrozenBrush(0xf4, 0xf4, 0xf5);
        private static readonly Pen OriginPen = CreateOriginPen();
        private readonly DrawingVisual _drawing = new();
        private Point _origin, _x, _y, _z;

        public ViewportTransformGizmo() => AddVisualChild(_drawing);

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => index == 0
            ? _drawing : throw new System.ArgumentOutOfRangeException(nameof(index));
        private bool _hasGizmo;

        internal void SetPoints(Point origin, Point x, Point y, Point z)
        {
            if (_hasGizmo && _origin == origin && _x == x && _y == y && _z == z) return;
            _origin = origin; _x = x; _y = y; _z = z;
            _hasGizmo = true;
            Redraw();
        }

        internal void Clear()
        {
            if (!_hasGizmo) return;
            _hasGizmo = false;
            Redraw();
        }

        private void Redraw()
        {
            // Update retained drawing content directly: actor focus can change after the layout pass.
            using DrawingContext drawing = _drawing.RenderOpen();
            if (!_hasGizmo) return;
            drawing.DrawLine(XPen, _origin, _x);
            drawing.DrawLine(YPen, _origin, _y);
            drawing.DrawLine(ZPen, _origin, _z);
            drawing.DrawEllipse(OriginFill, OriginPen, _origin, 4, 4);
        }

        private static Brush FrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private static Pen AxisPen(byte red, byte green, byte blue)
        {
            var pen = new Pen(FrozenBrush(red, green, blue), 4)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle
            };
            pen.Freeze();
            return pen;
        }

        private static Pen CreateOriginPen()
        {
            var pen = new Pen(FrozenBrush(0x18, 0x18, 0x1b), 2);
            pen.Freeze();
            return pen;
        }
    }
}
