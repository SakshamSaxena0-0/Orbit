using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Orbit
{
    static class Glow
    {
        public static readonly Color Gold = Color.FromRgb(0xFF, 0xD9, 0x8A);
        public static readonly Color Peach = Color.FromRgb(0xF7, 0xC5, 0x9F);
        public static readonly Color Rose = Color.FromRgb(0xEB, 0xA6, 0xB3);
        public static readonly Color Ink = Color.FromRgb(0x24, 0x1A, 0x26);

        public static readonly Brush Fill = MakeFill();

        public static readonly Brush InkBrush = Frozen(new SolidColorBrush(Ink));
        public static readonly Brush InkSoft = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, Ink.R, Ink.G, Ink.B)));

        public static readonly Brush Grain = MakeGrain(96);

        static Brush MakeFill()
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            b.GradientStops.Add(new GradientStop(With(Gold, 0xF2), 0));
            b.GradientStops.Add(new GradientStop(With(Peach, 0xF2), 0.6));
            b.GradientStops.Add(new GradientStop(With(Rose, 0xF2), 1));
            return Frozen(b);
        }

        static Brush MakeGrain(int size)
        {
            var random = new Random(7);
            var pixels = new byte[size * size * 4];
            for (int i = 0; i < size * size; i++)
            {
                double v = random.NextDouble();
                double a = 0.16 * v * v * v;
                pixels[i * 4] = (byte)(Ink.B * a);
                pixels[i * 4 + 1] = (byte)(Ink.G * a);
                pixels[i * 4 + 2] = (byte)(Ink.R * a);
                pixels[i * 4 + 3] = (byte)(255 * a);
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, size * 4);
            bitmap.Freeze();
            var brush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size),
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            return Frozen(brush);
        }

        public static void DrawFill(DrawingContext dc, Geometry shape)
        {
            dc.DrawGeometry(Fill, null, shape);
            dc.DrawGeometry(Grain, null, shape);
        }

        static Color With(Color c, byte alpha) { return Color.FromArgb(alpha, c.R, c.G, c.B); }

        static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
