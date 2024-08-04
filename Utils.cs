using System.Collections.Immutable;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace AppContainer;

public static partial class Utils
{
    /// <summary>
    /// Parses an array of command-line arguments and returns an immutable dictionary.
    /// </summary>
    /// <param name="args">An array of strings representing command-line arguments.</param>
    /// <returns>An ImmutableDictionary where keys are argument names (without "--") and values are the corresponding argument values.</returns>
    /// <remarks>
    /// Arguments should be in the format "--name value". 
    /// If an argument starts with "--" but has no following value, its value will be an empty string.
    /// </remarks>
    public static ImmutableDictionary<string, string> ParseArguments(string[] args)
    {
        var dictionary = new Dictionary<string, string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--"))
            {
                string key = args[i].Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    dictionary[key] = args[i + 1];
                    i++; // Skip the next argument as it's the value
                }
                else
                {
                    dictionary[key] = string.Empty; // Flag-only argument
                }
            }
        }
        return dictionary.ToImmutableDictionary();
    }

    /// <summary>
    /// Converts a string representation of a window handle to an nint and validates its existence.
    /// </summary>
    /// <param name="handleString">The string representation of the window handle in decimal or hexadecimal format (with '0x' prefix).</param>
    /// <returns>An nint representing the validated window handle.</returns>
    /// <exception cref="ArgumentException">Thrown when the input string is null, empty, or not a valid number.</exception>
    /// <exception cref="OverflowException">Thrown when the number is too large to fit in an nint.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the handle does not refer to an existing window.</exception>
    internal static Windows.Win32.Foundation.HWND ConvertAndValidateWindowHandle(string handleString)
    {
        if (string.IsNullOrWhiteSpace(handleString))
        {
            throw new ArgumentException("Handle string cannot be null or empty.", nameof(handleString));
        }

        long longHandle;
        if (handleString.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            // Hexadecimal input
            if (!long.TryParse(handleString.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out longHandle))
            {
                throw new ArgumentException("Invalid hexadecimal handle string. Must be a valid hexadecimal number with '0x' prefix.", nameof(handleString));
            }
        }
        else
        {
            // Decimal input
            if (!long.TryParse(handleString, out longHandle))
            {
                throw new ArgumentException("Invalid handle string. Must be a valid decimal number or hexadecimal number with '0x' prefix.", nameof(handleString));
            }
        }

        // Convert long to nint
        HWND windowHandle = new(new nint(longHandle));

        // Validate if the window handle refers to an existing window
        if (!PInvoke.IsWindow(windowHandle))
        {
            throw new InvalidOperationException("The specified window handle does not refer to an existing window.");
        }

        return windowHandle;
    }

    /// <summary>
    /// Creates a solid color bitmap.
    /// </summary>
    /// <param name="color">The color to fill the bitmap with.</param>
    /// <param name="width">The width of the bitmap.</param>
    /// <param name="height">The height of the bitmap.</param>
    /// <returns>A new Bitmap filled with the specified color.</returns>
    public static Bitmap CreateSolidColorBitmap(Color color, int width, int height)
    {
        Bitmap bmp = new(width, height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
        }
        return bmp;
    }

    /// <summary>
    /// Validates if the given string is a valid hex color code.
    /// </summary>
    /// <param name="hexColor">The hex color code string.</param>
    /// <returns>True if the string is a valid hex color code, otherwise false.</returns>
    public static bool IsValidHexColor(string hexColor)
    {
        return HexRegex().IsMatch(hexColor);
    }


    /// <summary>
    /// Creates a gradient bitmap from two colors.
    /// </summary>
    /// <param name="color1">The starting color of the gradient.</param>
    /// <param name="color2">The ending color of the gradient.</param>
    /// <param name="width">The width of the bitmap.</param>
    /// <param name="height">The height of the bitmap.</param>
    /// <returns>A new Bitmap with the specified gradient.</returns>
    public static Bitmap CreateGradientBitmap(Color color1, Color color2, int width, int height)
    {
        Bitmap bmp = new(width, height);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            using LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(0, 0, width, height), color1, color2, LinearGradientMode.Vertical);
            g.FillRectangle(brush, 0, 0, width, height);
        }
        return bmp;
    }

    [System.Text.RegularExpressions.GeneratedRegex("^#([0-9A-Fa-f]{6})$")]
    private static partial System.Text.RegularExpressions.Regex HexRegex();
}