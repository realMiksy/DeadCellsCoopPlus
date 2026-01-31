using System;
using dc.hxd;

namespace DeadCellsMultiplayerMod.Tools
{
    public static class UiScale
    {
        private const double ReferenceWidth = 1920.0;
        private const double ReferenceHeight = 1080.0;

        public static double GetResolutionScale()
        {
            var win = Window.Class.getInstance();
            if (win == null)
                return 1.0;

            double width = win.get_width();
            double height = win.get_height();
            if (width <= 0 || height <= 0)
                return 1.0;

            double scaleW = width / ReferenceWidth;
            double scaleH = height / ReferenceHeight;
            if (scaleW <= 0 || scaleH <= 0)
                return 1.0;

            return System.Math.Min(scaleW, scaleH);
        }
    }
}
