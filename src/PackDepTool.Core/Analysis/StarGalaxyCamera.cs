namespace PackDepTool.Core.Analysis;

/// <summary>
/// Pure screen/world camera used by the native star-galaxy view.
/// No UI framework types: the view converts points at the boundary.
/// </summary>
public readonly record struct StarGalaxyCamera(double Zoom, double PanX, double PanY)
{
    public const double MinZoom = 0.12;
    public const double MaxZoom = 4.0;

    public LayoutPoint ScreenToWorld(double screenX, double screenY) =>
        new((screenX - PanX) / Zoom, (screenY - PanY) / Zoom);

    public LayoutPoint WorldToScreen(double worldX, double worldY) =>
        new(worldX * Zoom + PanX, worldY * Zoom + PanY);

    public StarGalaxyCamera Panned(double dx, double dy) =>
        this with { PanX = PanX + dx, PanY = PanY + dy };

    /// <summary>
    /// Zooms while keeping the world point under the cursor pinned to the cursor.
    /// </summary>
    public StarGalaxyCamera ZoomAt(
        double screenX,
        double screenY,
        double factor,
        double minZoom = MinZoom,
        double maxZoom = MaxZoom)
    {
        if (factor <= 0 || double.IsNaN(factor) || double.IsInfinity(factor)) return this;
        var world = ScreenToWorld(screenX, screenY);
        var zoom = Math.Clamp(Zoom * factor, minZoom, maxZoom);
        return new StarGalaxyCamera(zoom, screenX - world.X * zoom, screenY - world.Y * zoom);
    }

    /// <summary>Fits a world rectangle into a viewport with padding, slightly below the top-left chrome.</summary>
    public static StarGalaxyCamera Fit(
        LayoutRect world,
        double viewWidth,
        double viewHeight,
        double padding = 28,
        double maxZoom = 1.6)
    {
        if (viewWidth <= 1 || viewHeight <= 1 || world.Width <= 0 || world.Height <= 0)
            return new StarGalaxyCamera(1, 0, 0);

        var availableWidth = Math.Max(1, viewWidth - padding * 2);
        var availableHeight = Math.Max(1, viewHeight - padding * 2);
        var zoom = Math.Clamp(
            Math.Min(availableWidth / world.Width, availableHeight / world.Height),
            MinZoom,
            Math.Max(MinZoom, maxZoom));
        return new StarGalaxyCamera(
            zoom,
            viewWidth / 2 - (world.X + world.Width / 2) * zoom,
            viewHeight / 2 - (world.Y + world.Height / 2) * zoom);
    }
}
