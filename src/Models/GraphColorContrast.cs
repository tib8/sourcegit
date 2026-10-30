using System;
using Avalonia.Media;

namespace SourceGit.Models
{
    internal static class GraphColorContrast
    {
        // Machado, Oliveira and Fernandes (2009), full dichromacy in linear sRGB.
        private static readonly double[][] s_transforms = [
            [1, 0, 0, 0, 1, 0, 0, 0, 1],
            [0.152286, 1.052583, -0.204868, 0.114503, 0.786281, 0.099216, -0.003882, -0.048116, 1.051998],
            [0.367322, 0.860646, -0.227968, 0.280085, 0.672501, 0.047413, -0.011820, 0.042940, 0.968881],
            [1.255528, -0.076749, -0.178779, -0.078411, 0.930809, 0.147602, 0.004733, 0.691367, 0.303900],
        ];

        public static double Distance(Color first, Color second)
        {
            var minimum = double.MaxValue;
            foreach (var transform in s_transforms)
            {
                var a = Perceptual(first, transform);
                var b = Perceptual(second, transform);
                var dl = a.L - b.L;
                var da = a.A - b.A;
                var db = a.B - b.B;
                minimum = Math.Min(minimum, dl * dl + da * da + db * db);
            }
            return minimum;
        }

        private static (double L, double A, double B) Perceptual(Color color, double[] matrix)
        {
            var r = Linear(color.R);
            var g = Linear(color.G);
            var b = Linear(color.B);
            var red = Math.Clamp(matrix[0] * r + matrix[1] * g + matrix[2] * b, 0, 1);
            var green = Math.Clamp(matrix[3] * r + matrix[4] * g + matrix[5] * b, 0, 1);
            var blue = Math.Clamp(matrix[6] * r + matrix[7] * g + matrix[8] * b, 0, 1);
            // OKLab distances include differences in lightness as well as hue.
            var l = Math.Cbrt(0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue);
            var m = Math.Cbrt(0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue);
            var s = Math.Cbrt(0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue);
            return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
        }

        private static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
    }
}
