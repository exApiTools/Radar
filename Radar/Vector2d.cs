using System;

namespace Radar;

public readonly record struct Vector2d(double X, double Y)
{
    public readonly double X = X;
    public readonly double Y = Y;

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static Vector2d operator -(Vector2d v1, Vector2d v2)
    {
        return new Vector2d(v1.X - v2.X, v1.Y - v2.Y);
    }

    public static Vector2d operator +(Vector2d v1, Vector2d v2)
    {
        return new Vector2d(v1.X + v2.X, v1.Y + v2.Y);
    }

    public static Vector2d operator /(Vector2d v, double d)
    {
        return new Vector2d(v.X / d, v.Y / d);
    }
}
