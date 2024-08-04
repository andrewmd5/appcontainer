namespace AppContainer;

/// <summary>
/// Represents the position and dimensions of a monitor.
/// </summary>
/// <param name="X">The X-coordinate of the monitor's top-left corner.</param>
/// <param name="Y">The Y-coordinate of the monitor's top-left corner.</param>
/// <param name="Width">The width of the monitor in pixels.</param>
/// <param name="Height">The height of the monitor in pixels.</param>
internal record Monitor(int X, int Y, int Width, int Height);