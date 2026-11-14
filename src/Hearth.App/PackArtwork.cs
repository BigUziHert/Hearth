using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hearth.App;

/// <summary>Small, original vector block emblems. Each call returns a fresh WPF element.</summary>
internal static class PackArtwork
{
    public static FrameworkElement Create(int variant, double size)
    {
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        variant = ((variant % 3) + 3) % 3;
        var canvas = new Canvas { Width = 64, Height = 64, IsHitTestVisible = false, SnapsToDevicePixels = true };
        var outline = Brush("#171C1B");
        void Face(Point[] points, string fill, bool edge = false)
        {
            canvas.Children.Add(new Polygon
            {
                Points = new PointCollection(points), Fill = Brush(fill),
                Stroke = edge ? outline : null, StrokeThickness = edge ? 1.2 : 0,
                StrokeLineJoin = PenLineJoin.Miter, SnapsToDevicePixels = true
            });
        }
        Point Top(double x, double y) => new(32 + 26 * x - 26 * y, 4 + 14 * x + 14 * y);
        Point Left(double x, double y) => new(6 + 26 * x, 18 + 14 * x + 28 * y);
        Point Right(double x, double y) => new(32 + 26 * x, 32 - 14 * x + 28 * y);
        void Tile(Func<double, double, Point> project, double x, double y, double width, double height, string color)
            => Face([project(x, y), project(x + width, y), project(x + width, y + height), project(x, y + height)], color);

        // A restrained ground shadow keeps the block legible on dark selected rows.
        Face([new(5, 49), new(31, 63), new(60, 48), new(32, 36)], "#111513");
        switch (variant)
        {
            case 0:
                Face([Left(0, 0), Left(1, 0), Left(1, 1), Left(0, 1)], "#795336", true);
                Face([Right(0, 0), Right(1, 0), Right(1, 1), Right(0, 1)], "#543C2A", true);
                foreach (var p in new[] { (.05, .36), (.36, .62), (.70, .40), (.13, .80), (.74, .84) })
                    Tile(Left, p.Item1, p.Item2, .19, .12, "#9A704A");
                foreach (var p in new[] { (.12, .46), (.50, .67), (.75, .30), (.18, .83) })
                    Tile(Right, p.Item1, p.Item2, .17, .12, "#725137");
                Tile(Left, 0, 0, 1, .19, "#539445");
                Tile(Left, 0, .18, .2, .13, "#539445");
                Tile(Left, .39, .18, .18, .09, "#539445");
                Tile(Left, .78, .18, .22, .15, "#539445");
                Tile(Right, 0, 0, 1, .17, "#39713A");
                Tile(Right, .12, .16, .18, .11, "#39713A");
                Tile(Right, .60, .16, .22, .13, "#39713A");
                Face([Top(0, 0), Top(1, 0), Top(1, 1), Top(0, 1)], "#78B853", true);
                Tile(Top, .06, .11, .27, .22, "#90CA62");
                Tile(Top, .57, .12, .29, .20, "#69A348");
                Tile(Top, .18, .61, .22, .24, "#8AC55C");
                Tile(Top, .63, .58, .28, .27, "#6EAA4A");
                break;
            case 1:
                Face([Left(0, 0), Left(1, 0), Left(1, 1), Left(0, 1)], "#967044", true);
                Face([Right(0, 0), Right(1, 0), Right(1, 1), Right(0, 1)], "#6D4D30", true);
                Tile(Left, .10, .15, .79, .63, "#BD945B");
                Tile(Right, .10, .15, .79, .63, "#9A7548");
                foreach (var x in new[] { .10, .40, .70 })
                {
                    Tile(Left, x, .16, .04, .61, "#7A5533");
                    Tile(Right, x, .16, .04, .61, "#5C402A");
                }
                Tile(Left, .03, .83, .94, .12, "#60422D");
                Tile(Right, .03, .83, .94, .12, "#4D3526");
                // Crossed tool marks on the side identify the crafting-table variant.
                Tile(Left, .24, .31, .48, .09, "#574734");
                Tile(Left, .43, .24, .09, .42, "#574734");
                Tile(Right, .26, .29, .42, .10, "#CDA86B");
                Tile(Right, .43, .29, .09, .40, "#D3B681");
                Face([Top(0, 0), Top(1, 0), Top(1, 1), Top(0, 1)], "#A77B44", true);
                Tile(Top, .10, .10, .80, .80, "#D2A66B");
                foreach (var n in new[] { .10, .36, .62, .87 })
                {
                    Tile(Top, n, .10, .035, .80, "#715035");
                    Tile(Top, .10, n, .80, .035, "#715035");
                }
                break;
            default:
                Face([Left(0, 0), Left(1, 0), Left(1, 1), Left(0, 1)], "#69716D", true);
                Face([Right(0, 0), Right(1, 0), Right(1, 1), Right(0, 1)], "#434D49", true);
                Face([Top(0, 0), Top(1, 0), Top(1, 1), Top(0, 1)], "#8A9286", true);
                Tile(Left, .05, .53, .32, .19, "#56605C");
                Tile(Left, .56, .13, .35, .16, "#78827A");
                Tile(Right, .07, .12, .28, .22, "#53605A");
                Tile(Right, .56, .63, .32, .22, "#36423D");
                Tile(Top, .03, .16, .37, .22, "#747F76");
                Tile(Top, .57, .57, .35, .26, "#768077");
                foreach (var p in new[] { (.16, .17), (.55, .47), (.27, .76) })
                {
                    Tile(Left, p.Item1, p.Item2, .23, .16, "#C49334");
                    Tile(Left, p.Item1, p.Item2, .13, .08, "#F0D06A");
                }
                foreach (var p in new[] { (.55, .16), (.15, .49), (.62, .76) })
                {
                    Tile(Right, p.Item1, p.Item2, .22, .15, "#A77E2F");
                    Tile(Right, p.Item1, p.Item2, .12, .07, "#D3AE4D");
                }
                Tile(Top, .49, .11, .25, .21, "#E5C25A");
                Tile(Top, .18, .52, .25, .23, "#F2D278");
                Tile(Top, .62, .57, .17, .14, "#B48C38");
                break;
        }
        var artwork = new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform, IsHitTestVisible = false, Focusable = false };
        AutomationProperties.SetName(artwork, variant switch { 0 => "Grass block pack icon", 1 => "Crafting table pack icon", _ => "Gold ore pack icon" });
        return artwork;
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
