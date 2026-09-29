using System.Globalization;
using System.Text;

namespace ShelfScan.Core;

internal static class Text
{
    /// <summary>
    /// Lowercase words with accents and punctuation removed, one-letter words dropped:
    /// <c>"Saint-Exupéry, J."</c> gives <c>{ "saint", "exupery" }</c>.
    /// </summary>
    public static HashSet<string> Words(string text)
    {
        HashSet<string> words = [];
        StringBuilder word = new();
        foreach (char c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                Flush();
            }
        }

        Flush();
        return words;

        void Flush()
        {
            if (word.Length > 1)
            {
                words.Add(word.ToString());
            }

            word.Clear();
        }
    }
}
