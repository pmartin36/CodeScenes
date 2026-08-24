#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UnityEditor;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Validation;

namespace SceneBuilder.Editor
{
    /// <summary>
    /// THE one place a code-&gt;scene / code-&gt;prefab build OUTCOME is recorded, keyed on
    /// <c>BuilderName</c> (<see cref="System.IO.Path.GetFileNameWithoutExtension(string)"/> of the
    /// builder path). <see cref="SceneBuilderBuild.RunCore"/> and the prefab
    /// pre-<see cref="SceneBuilderBuild.RunCore"/> refusals call
    /// <see cref="RecordRefused(string, IReadOnlyList{Diagnostic})"/> /
    /// <see cref="RecordClean"/> on every outcome, so a query below reports "nothing recorded" for
    /// any builder whose last build was clean (or has never built). Persisted in
    /// <see cref="SessionState"/> (mirroring <c>Licensing.LicenseStore</c>): survives a domain
    /// reload, dies with the editor session. Recording itself is silent — no console line — the
    /// four logging channels render one through <see cref="FormatLocated"/> instead.
    /// </summary>
    public static class SceneBuilderBuildStatus
    {
        /// <summary>The standing located refusal for one builder.</summary>
        public sealed class Refusal
        {
            public string BuilderName { get; set; } = "";
            public string File { get; set; } = "";
            public int Line { get; set; }
            public int Col { get; set; }
            public string Code { get; set; } = "";
            public string Message { get; set; } = "";
        }

        private const string IndexKey = "CodeScenes.BuildStatus.Index";
        private const string RefusalKeyPrefix = "CodeScenes.BuildStatus.refusal.";
        private const string OverlayKeyPrefix = "CodeScenes.BuildStatus.overlay.";

        /// <summary>
        /// Records <paramref name="builderName"/>'s standing refusal from a collect-all diagnostics
        /// list (primary = <paramref name="diagnostics"/>[0]) and returns a <see cref="SceneBuilderBuild.BuildResult"/>
        /// carrying the FULL list unchanged, so collect-all callers keep seeing every diagnostic.
        /// </summary>
        public static SceneBuilderBuild.BuildResult RecordRefused(string builderName, IReadOnlyList<Diagnostic> diagnostics)
        {
            var primary = diagnostics[0];
            var refusal = new Refusal
            {
                BuilderName = builderName,
                File = primary.File,
                Line = primary.Line,
                Col = primary.Col,
                Code = primary.Code,
                Message = primary.Message,
            };

            Persist(builderName, refusal);
            RegisterOverlay(builderName, refusal);

            return new SceneBuilderBuild.BuildResult { Diagnostics = diagnostics };
        }

        /// <summary>
        /// Converges a raw-parse <see cref="ParseException"/> onto the same recorded shape as a
        /// collected diagnostic (single-element list, primary = the synthesized diagnostic).
        /// </summary>
        public static SceneBuilderBuild.BuildResult RecordRefused(string builderName, ParseException e, string builderPath)
        {
            var diagnostic = new Diagnostic
            {
                File = builderPath,
                Line = e.Line,
                Col = e.Column,
                Code = DiagnosticCodes.ParseError,
                Severity = DiagnosticSeverity.Error,
                Message = e.Message,
            };

            return RecordRefused(builderName, new[] { diagnostic });
        }

        /// <summary>
        /// Erases <paramref name="builderName"/>'s standing refusal (and its overlay entry) and
        /// returns <paramref name="result"/> unchanged — a pass-through so a success return can read
        /// <c>return SceneBuilderBuildStatus.RecordClean(builderName, new BuildResult { ... });</c>.
        /// </summary>
        public static SceneBuilderBuild.BuildResult RecordClean(string builderName, SceneBuilderBuild.BuildResult result)
        {
            ClearRefusal(builderName);
            return result;
        }

        /// <summary>True while at least one builder carries a standing refusal.</summary>
        public static bool AnyRefusing => ReadIndex().Count > 0;

        /// <summary>The standing refusal for <paramref name="builderName"/>, or null if it last built clean (or never refused).</summary>
        public static Refusal? GetRefusal(string builderName)
        {
            var json = SessionState.GetString(RefusalKey(builderName), "");
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<Refusal>(json);
        }

        /// <summary>Every builder with a standing refusal.</summary>
        public static IReadOnlyList<Refusal> CurrentRefusals =>
            ReadIndex().Select(GetRefusal).Where(r => r != null).Select(r => r!).ToArray();

        /// <summary>The ONE console-line format for a located diagnostic — every channel renders through this.</summary>
        public static string FormatLocated(Diagnostic d) => $"[CodeScenes] {d.Code} {d.File}({d.Line},{d.Col}): {d.Message}";

        /// <summary>Test seam: clears every recorded refusal (and its overlay entry).</summary>
        public static void ResetForTests()
        {
            foreach (var builderName in ReadIndex())
            {
                var overlayKey = SessionState.GetString(OverlayKey(builderName), "");
                if (!string.IsNullOrEmpty(overlayKey))
                {
                    ConflictSurfacing.RemoveOverlay(overlayKey);
                }

                SessionState.EraseString(RefusalKey(builderName));
                SessionState.EraseString(OverlayKey(builderName));
            }

            SessionState.EraseString(IndexKey);
        }

        private static List<string> ReadIndex()
        {
            var raw = SessionState.GetString(IndexKey, "");
            return string.IsNullOrEmpty(raw) ? new List<string>() : raw.Split('\n').ToList();
        }

        private static void WriteIndex(List<string> builderNames) =>
            SessionState.SetString(IndexKey, string.Join("\n", builderNames));

        private static string RefusalKey(string builderName) => RefusalKeyPrefix + builderName;

        private static string OverlayKey(string builderName) => OverlayKeyPrefix + builderName;

        private static void Persist(string builderName, Refusal refusal)
        {
            SessionState.SetString(RefusalKey(builderName), JsonSerializer.Serialize(refusal));

            var index = ReadIndex();
            if (!index.Contains(builderName))
            {
                index.Add(builderName);
                WriteIndex(index);
            }
        }

        private static void RegisterOverlay(string builderName, Refusal refusal)
        {
            var key = $"build error at {refusal.File}:{refusal.Line}";
            var storedKey = SessionState.GetString(OverlayKey(builderName), "");
            if (!string.IsNullOrEmpty(storedKey) && storedKey != key)
            {
                ConflictSurfacing.RemoveOverlay(storedKey);
            }

            new ConflictSurfacing().RegisterOverlay(key);
            SessionState.SetString(OverlayKey(builderName), key);
        }

        private static void ClearRefusal(string builderName)
        {
            var storedKey = SessionState.GetString(OverlayKey(builderName), "");
            if (!string.IsNullOrEmpty(storedKey))
            {
                ConflictSurfacing.RemoveOverlay(storedKey);
            }

            SessionState.EraseString(RefusalKey(builderName));
            SessionState.EraseString(OverlayKey(builderName));

            var index = ReadIndex();
            if (index.Remove(builderName))
            {
                WriteIndex(index);
            }
        }
    }
}
