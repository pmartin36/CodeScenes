using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace SceneBuilder.Core.Reconcile
{
    /// <summary>
    /// Shared handle-name derivation + uniquification. The Reconciler predicts a handle name via
    /// <see cref="Derive"/> to forecast the LogicalId a rewritten parent will get; the SourcePatchApplier
    /// writes the SAME name verbatim as `var &lt;h&gt; = ...`. Deterministic and side-effect-free so both
    /// sides compute the identical result from the identical inputs — do not duplicate this logic.
    /// </summary>
    internal static class HandleNaming
    {
        /// <summary>
        /// Derives a valid, unique C# identifier from <paramref name="parentName"/> — camelCase, invalid
        /// chars stripped, leading-digit/keyword-safe — then uniquifies against
        /// <paramref name="existingIdentifiers"/> by numeric suffixing (starting at 2). Does not mutate
        /// <paramref name="existingIdentifiers"/>.
        /// </summary>
        internal static string Derive(string parentName, IEnumerable<string> existingIdentifiers)
        {
            var baseName = DeriveBase(parentName);

            if (baseName.Length == 0)
            {
                baseName = "go";
            }

            if (char.IsDigit(baseName[0]))
            {
                baseName = "_" + baseName;
            }

            if (SyntaxFacts.GetKeywordKind(baseName) != SyntaxKind.None)
            {
                baseName += "_";
            }

            return Uniquify(baseName, existingIdentifiers);
        }

        // Splits into word-runs on any non-alphanumeric char, lowercases the first char of the
        // first (non-empty) word, PascalCases the first char of subsequent words, and drops any
        // remaining non-[A-Za-z0-9] chars within each word.
        private static string DeriveBase(string parentName)
        {
            var sb = new StringBuilder();
            var word = new StringBuilder();
            var firstWordEmitted = false;

            void FlushWord()
            {
                if (word.Length == 0)
                {
                    return;
                }

                var filtered = FilterAscii(word.ToString());
                word.Clear();

                if (filtered.Length == 0)
                {
                    return;
                }

                if (!firstWordEmitted)
                {
                    sb.Append(char.ToLowerInvariant(filtered[0])).Append(filtered, 1, filtered.Length - 1);
                    firstWordEmitted = true;
                }
                else
                {
                    sb.Append(char.ToUpperInvariant(filtered[0])).Append(filtered, 1, filtered.Length - 1);
                }
            }

            foreach (var c in parentName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    word.Append(c);
                }
                else
                {
                    FlushWord();
                }
            }

            FlushWord();

            return sb.ToString();
        }

        private static string FilterAscii(string word)
        {
            var sb = new StringBuilder(word.Length);
            foreach (var c in word)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        private static string Uniquify(string baseName, IEnumerable<string> existingIdentifiers)
        {
            var taken = new HashSet<string>(existingIdentifiers, System.StringComparer.Ordinal);

            if (!taken.Contains(baseName))
            {
                return baseName;
            }

            var suffix = 2;
            while (true)
            {
                var candidate = baseName + suffix;
                if (!taken.Contains(candidate))
                {
                    return candidate;
                }

                suffix++;
            }
        }
    }
}
