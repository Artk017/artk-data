using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace WinUIDemo.Helpers;

/// <summary>
/// 把一组数值映射成屏幕坐标，并生成平滑曲线（Catmull-Rom 转三次贝塞尔）。
/// </summary>
public static class ChartGeometry
{
    public static Point[] Project(
        IReadOnlyList<double> series,
        double width,
        double height,
        double min,
        double max,
        double padTop = 12,
        double padBottom = 6,
        double padRight = 8)
    {
        int n = series.Count;
        var points = new Point[n];
        double plotHeight = height - padTop - padBottom;
        double plotWidth = Math.Max(width - padRight, 1);
        double span = Math.Max(max - min, 0.0001);

        for (int i = 0; i < n; i++)
        {
            double x = n == 1 ? 0 : plotWidth * i / (n - 1);
            double t = Math.Clamp((series[i] - min) / span, 0, 1);
            points[i] = new Point(x, padTop + (plotHeight * (1 - t)));
        }

        return points;
    }

    /// <summary>生成平滑折线；closeAtY 不为空时闭合到指定基线，形成面积图。</summary>
    public static PathGeometry BuildCurve(Point[] points, double? closeAtY = null)
    {
        var figure = new PathFigure
        {
            StartPoint = points[0],
            IsClosed = closeAtY.HasValue,
            IsFilled = closeAtY.HasValue,
        };

        int n = points.Length;
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = points[Math.Max(i - 1, 0)];
            Point p1 = points[i];
            Point p2 = points[i + 1];
            Point p3 = points[Math.Min(i + 2, n - 1)];

            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(p1.X + ((p2.X - p0.X) / 6.0), p1.Y + ((p2.Y - p0.Y) / 6.0)),
                Point2 = new Point(p2.X - ((p3.X - p1.X) / 6.0), p2.Y - ((p3.Y - p1.Y) / 6.0)),
                Point3 = p2,
            });
        }

        if (closeAtY is double bottom)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(points[^1].X, bottom) });
            figure.Segments.Add(new LineSegment { Point = new Point(points[0].X, bottom) });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
