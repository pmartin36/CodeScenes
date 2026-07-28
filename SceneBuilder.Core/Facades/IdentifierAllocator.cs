using System;
using System.Collections.Generic;
using System.Globalization;

namespace SceneBuilder.Core.Facades
{
    // Shared Core allocator: BARE-number suffix (Wheel, Wheel2, Wheel3, ...) — NOT
    // CatalogEmit's "_N" form. Loop-until-unique also guards the rare case where a
    // synthesized "Wheel2" collides with a literally-named "Wheel2" sibling. Extracted from
    // FacadeCatalogBuilder so FacadeCatalogBuilder and AssetCatalogBuilder share ONE
    // implementation of the loop-until-unique semantic (must not drift between the two).
    internal sealed class IdentifierAllocator
    {
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        public string Allocate(string sanitized)
        {
            var candidate = sanitized;
            var n = 1;
            while (_used.Contains(candidate))
            {
                n++;
                candidate = sanitized + n.ToString(CultureInfo.InvariantCulture);
            }

            _used.Add(candidate);
            return candidate;
        }

        // Non-mutating occupancy peek. Reuses the same `_used` truth as Allocate so a caller
        // (e.g. AssetCatalogBuilder's collapse-collision check) can ask "is this slot taken"
        // without allocating into it.
        public bool IsAllocated(string sanitized) => _used.Contains(sanitized);
    }
}
