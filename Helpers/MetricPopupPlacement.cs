using System;
using System.Windows;

namespace LenSa.Core.Helpers
{
    /// <summary>Right-aligns a metrics card to its chip and keeps it within the host viewport.</summary>
    public static class MetricPopupPlacement
    {
        public static Point Calculate(Rect target, Size popup, Rect bounds)
        {
            var x = Math.Clamp(target.Right - popup.Width, bounds.Left,
                Math.Max(bounds.Left, bounds.Right - popup.Width));
            var above = target.Top - popup.Height - 5;
            var below = target.Bottom + 5;
            var y = above >= bounds.Top ? above
                : below + popup.Height <= bounds.Bottom ? below
                : Math.Clamp(above, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - popup.Height));
            return new Point(x, y);
        }
    }
}
