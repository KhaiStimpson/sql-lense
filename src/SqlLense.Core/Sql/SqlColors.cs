using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace SqlLense.Sql
{
    /// <summary>An sRGB color, independent of any UI framework.</summary>
    public readonly struct SqlRgb : IEquatable<SqlRgb>
    {
        public SqlRgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
        public double Luminance => 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);

        public bool IsDark => Luminance < 0.4;

        public static SqlRgb FromHex(string hex)
        {
            var value = int.Parse(hex.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new SqlRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        }

        /// <summary>WCAG contrast ratio, 1 to 21.</summary>
        public static double Contrast(SqlRgb a, SqlRgb b)
        {
            var la = a.Luminance;
            var lb = b.Luminance;
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        /// <summary>Hue in degrees, saturation and lightness in 0..1.</summary>
        public static SqlRgb FromHsl(double hue, double saturation, double lightness)
        {
            hue = ((hue % 360) + 360) % 360;
            var c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
            var m = lightness - c / 2;
            var (r, g, b) = hue switch
            {
                < 60 => (c, x, 0d),
                < 120 => (x, c, 0d),
                < 180 => (0d, c, x),
                < 240 => (0d, x, c),
                < 300 => (x, 0d, c),
                _ => (c, 0d, x),
            };
            return new SqlRgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
        }

        public bool Equals(SqlRgb other) => R == other.R && G == other.G && B == other.B;

        public override bool Equals(object? obj) => obj is SqlRgb other && Equals(other);

        public override int GetHashCode() => (R << 16) | (G << 8) | B;

        public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

        private static double Linear(byte channel)
        {
            var c = channel / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static byte ToByte(double value) => (byte)Math.Round(Math.Max(0, Math.Min(1, value)) * 255);
    }

    /// <summary>
    /// Generates one color per alias. Hues advance by the golden angle, so colors never repeat and
    /// neighbouring aliases are far apart on the color wheel; lightness is picked for the editor
    /// background and nudged until the color meets WCAG AA contrast against it.
    /// </summary>
    public static class SqlAliasPalette
    {
        private const double GoldenAngle = 137.50776405003785;
        private const double StartHue = 200;
        private const double MinContrast = 4.5;

        public static SqlRgb GetColor(int index, SqlRgb background)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            var dark = background.IsDark;
            var hue = StartHue + index * GoldenAngle;

            // After each lap of the wheel vary saturation and lightness a little, so late aliases differ
            // in more than a few degrees of hue from early ones.
            var lap = index / 8 % 3;
            var saturation = lap == 1 ? 0.55 : 0.75;
            var lightness = (dark ? 0.66 : 0.38) + (lap == 2 ? (dark ? 0.08 : -0.08) : 0);
            var step = dark ? 0.02 : -0.02;

            var color = SqlRgb.FromHsl(hue, saturation, lightness);
            while (SqlRgb.Contrast(color, background) < MinContrast && lightness > 0.05 && lightness < 0.95)
            {
                lightness += step;
                color = SqlRgb.FromHsl(hue, saturation, lightness);
            }

            return color;
        }
    }

    public enum SqlColorPreset
    {
        [Description("Custom (keep current colors)")]
        Custom,

        [Description("SqlLense default")]
        SqlLense,

        [Description("SSMS-like")]
        Ssms,

        [Description("Azure Data Studio-like")]
        AzureDataStudio,

        [Description("DataGrip / JetBrains")]
        DataGrip,

        [Description("Dracula")]
        Dracula,

        [Description("Solarized")]
        Solarized,
    }

    public readonly struct SqlTokenStyle
    {
        public SqlTokenStyle(SqlRgb foreground, bool bold = false)
        {
            Foreground = foreground;
            Bold = bold;
        }

        public SqlRgb Foreground { get; }

        public bool Bold { get; }
    }

    /// <summary>
    /// Built-in color schemes for the highlighted token classes, each in a light and a dark variant.
    /// Presets only set token foregrounds (and bold); the editor background is left alone.
    /// </summary>
    public static class SqlColorPresets
    {
        /// <summary>The token classes SqlLense colors; strings and operators keep the C# string color.</summary>
        public static readonly IReadOnlyList<SqlTokenClass> StyledClasses = new[]
        {
            SqlTokenClass.Keyword, SqlTokenClass.Identifier, SqlTokenClass.Function,
            SqlTokenClass.Variable, SqlTokenClass.Number, SqlTokenClass.Comment,
        };

        // Order of each row: keyword, identifier, function, variable, number, comment.
        private static readonly Dictionary<SqlColorPreset, (Scheme Light, Scheme Dark)> s_presets = new Dictionary<SqlColorPreset, (Scheme, Scheme)>
        {
            // Mid-tones that read on light and dark backgrounds alike.
            [SqlColorPreset.SqlLense] = Both(new Scheme("3E8EDE", "1FA09A", "B06AC8", "C77C1E", "3A9A5C", "6A8F5A", boldKeywords: true)),
            [SqlColorPreset.Ssms] = (
                new Scheme("0000FF", "000000", "FF00FF", "000000", "000000", "008000"),
                new Scheme("569CD6", "DCDCDC", "DA70D6", "9CDCFE", "B5CEA8", "57A64A")),
            [SqlColorPreset.AzureDataStudio] = (
                new Scheme("0000FF", "000000", "795E26", "001080", "098658", "008000"),
                new Scheme("569CD6", "D4D4D4", "DCDCAA", "9CDCFE", "B5CEA8", "6A9955")),
            [SqlColorPreset.DataGrip] = (
                new Scheme("0033B3", "080808", "00627A", "871094", "1750EB", "8C8C8C", boldKeywords: true),
                new Scheme("CC7832", "A9B7C6", "FFC66D", "9876AA", "6897BB", "808080", boldKeywords: true)),
            [SqlColorPreset.Dracula] = (
                new Scheme("A3144D", "1F1F1F", "14710A", "A34D14", "644AC9", "6C664B"),
                new Scheme("FF79C6", "F8F8F2", "50FA7B", "FFB86C", "BD93F9", "6272A4")),
            [SqlColorPreset.Solarized] = (
                new Scheme("859900", "586E75", "268BD2", "B58900", "2AA198", "93A1A1"),
                new Scheme("859900", "93A1A1", "268BD2", "B58900", "2AA198", "586E75")),
        };

        /// <summary>The scheme for a preset, or null for <see cref="SqlColorPreset.Custom"/>.</summary>
        public static IReadOnlyDictionary<SqlTokenClass, SqlTokenStyle>? GetScheme(SqlColorPreset preset, bool darkBackground)
        {
            if (!s_presets.TryGetValue(preset, out var variants))
            {
                return null;
            }

            return (darkBackground ? variants.Dark : variants.Light).Styles;
        }

        private static (Scheme, Scheme) Both(Scheme scheme) => (scheme, scheme);

        private sealed class Scheme
        {
            public Scheme(string keyword, string identifier, string function, string variable, string number, string comment, bool boldKeywords = false)
            {
                Styles = new Dictionary<SqlTokenClass, SqlTokenStyle>
                {
                    [SqlTokenClass.Keyword] = new SqlTokenStyle(SqlRgb.FromHex(keyword), boldKeywords),
                    [SqlTokenClass.Identifier] = new SqlTokenStyle(SqlRgb.FromHex(identifier)),
                    [SqlTokenClass.Function] = new SqlTokenStyle(SqlRgb.FromHex(function)),
                    [SqlTokenClass.Variable] = new SqlTokenStyle(SqlRgb.FromHex(variable)),
                    [SqlTokenClass.Number] = new SqlTokenStyle(SqlRgb.FromHex(number)),
                    [SqlTokenClass.Comment] = new SqlTokenStyle(SqlRgb.FromHex(comment)),
                };
            }

            public IReadOnlyDictionary<SqlTokenClass, SqlTokenStyle> Styles { get; }
        }
    }
}
