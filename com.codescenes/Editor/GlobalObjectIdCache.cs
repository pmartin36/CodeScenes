#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// Instance-owned cache of instanceId -&gt; <see cref="GlobalObjectId"/> string, centralizing every
    /// slow GlobalObjectId resolve behind a single counted seam so incremental snapshot assembly can be
    /// proven O(changed), not O(scene). Not static — a domain reload wipes it; the next cold assemble
    /// rewarms via <see cref="WarmBatch"/>.
    /// </summary>
    public sealed class GlobalObjectIdCache
    {
        private readonly Dictionary<UnityEngine.EntityId, string> _cache = new();

        /// <summary>Number of GlobalObjectId slow resolves actually performed (cache misses only).</summary>
        public int ResolutionCount { get; private set; }

        /// <summary>True when the last <see cref="WarmBatch"/> used the batch overload (not the per-object fallback).</summary>
        public bool LastWarmUsedBatch { get; private set; }

        /// <summary>Zeroes the resolution counter without clearing cached entries.</summary>
        public void ResetCount() => ResolutionCount = 0;

        /// <summary>Cache hit -&gt; cached string; miss -&gt; GetGlobalObjectIdSlow(obj), store, count++.</summary>
        public string Resolve(UnityEngine.Object obj)
        {
            var entityId = obj.GetEntityId();
            if (_cache.TryGetValue(entityId, out var cached))
            {
                return cached;
            }

            var resolved = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            _cache[entityId] = resolved;
            ResolutionCount++;
            return resolved;
        }

        /// <summary>Resolves every cache MISS among <paramref name="objects"/> in one batch call.</summary>
        public void WarmBatch(IReadOnlyList<UnityEngine.Object> objects)
        {
            var misses = new List<UnityEngine.Object>();
            foreach (var obj in objects)
            {
                if (obj == null)
                {
                    continue;
                }

                if (!_cache.ContainsKey(obj.GetEntityId()))
                {
                    misses.Add(obj);
                }
            }

            if (misses.Count == 0)
            {
                LastWarmUsedBatch = true;
                return;
            }

            try
            {
                var missArray = misses.ToArray();
                var results = new GlobalObjectId[missArray.Length];
                GlobalObjectId.GetGlobalObjectIdsSlow(missArray, results);

                for (var i = 0; i < missArray.Length; i++)
                {
                    _cache[missArray[i].GetEntityId()] = results[i].ToString();
                }

                ResolutionCount += missArray.Length;
                LastWarmUsedBatch = true;
            }
            catch (Exception)
            {
                foreach (var obj in misses)
                {
                    _cache[obj.GetEntityId()] = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
                    ResolutionCount++;
                }

                LastWarmUsedBatch = false;
            }
        }

        /// <summary>
        /// Forces the next <see cref="Resolve"/> for this entity id to re-resolve. Keyed on
        /// <see cref="UnityEngine.EntityId"/>, NOT <c>int</c> — <c>Object.GetInstanceID()</c> and the
        /// EntityId-&gt;int implicit cast are both compile ERRORS (unsuppressable, CS0619) on the
        /// installed 6000.5.3f1 editor; EntityId is the only viable identity type there.
        /// </summary>
        public void Invalidate(UnityEngine.EntityId entityId) => _cache.Remove(entityId);

        /// <summary>Batch form of <see cref="Invalidate(UnityEngine.EntityId)"/>.</summary>
        public void Invalidate(IEnumerable<UnityEngine.EntityId> entityIds)
        {
            foreach (var entityId in entityIds)
            {
                _cache.Remove(entityId);
            }
        }

        /// <summary>Drops every cached entry and resets the counter.</summary>
        public void Clear()
        {
            _cache.Clear();
            ResolutionCount = 0;
            LastWarmUsedBatch = false;
        }
    }
}
