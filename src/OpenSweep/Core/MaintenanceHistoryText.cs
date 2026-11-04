using System;
using System.Text;

namespace OpenSweep.Core
{
    public static class MaintenanceHistoryText
    {
        /// <summary>Returns XML 1.0 text bounded by UTF-16 length without splitting surrogate pairs.</summary>
        public static string Limit(string value, int max)
        {
            if (value == null) return null;
            if (max <= 0) return string.Empty;

            var result = new StringBuilder(Math.Min(value.Length, max));
            for (int i = 0; i < value.Length && result.Length < max; i++)
            {
                char current = value[i];
                if (char.IsHighSurrogate(current))
                {
                    if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1])) continue;
                    if (max - result.Length < 2) break;
                    result.Append(current);
                    result.Append(value[++i]);
                }
                else if (current == '\t' || current == '\n' || current == '\r' ||
                    (current >= '\u0020' && current <= '\uD7FF') ||
                    (current >= '\uE000' && current <= '\uFFFD'))
                {
                    result.Append(current);
                }
            }
            return result.ToString();
        }
    }
}
