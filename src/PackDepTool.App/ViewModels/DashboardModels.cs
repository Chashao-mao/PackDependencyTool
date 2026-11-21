using Avalonia.Media;

namespace PackDepTool.App.ViewModels;

public sealed record DashboardBarItem(string Label, int Count, double Percent, IBrush Brush, string Detail);

public sealed record DashboardProblemItem(string Name, string Detail, string Status, IBrush Brush);
