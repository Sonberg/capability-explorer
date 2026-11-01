using System.Text;

namespace CapabilityExplorer.Explorer;

/// <summary>Identifier spelling only: no synonyms or assertions of domain equivalence.</summary>
public static class GlossaryNormalization
{
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = new List<string>();
        var word = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (!char.IsLetterOrDigit(current))
            {
                Flush();
                continue;
            }

            // HTTPServer -> HTTP / Server, averagePerWeek -> average / Per / Week.
            if (word.Length > 0 && char.IsUpper(current) &&
                (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) ||
                 (char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                Flush();
            }

            word.Append(char.ToLowerInvariant(current));
        }

        Flush();
        if (words.Count > 1 && words[^1] is "dto" or "entity" or "record")
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);

        void Flush()
        {
            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
    }
}
