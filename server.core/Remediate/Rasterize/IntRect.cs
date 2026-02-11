namespace server.core.Remediate.Rasterize;

public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

