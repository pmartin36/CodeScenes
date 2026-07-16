using System;
using System.Collections.Generic;
using System.IO;
using SceneBuilder.Core.Parsing;

namespace SceneBuilder.Core.Validation
{
    // b4-t3: reads + parses a builder file, runs the ONE shared PlanningValidator walk (b2-t1)
    // through a DiskResolutionProvider (b4-t2) built from a ProjectLayout (b4-t1). Launches no
    // Unity. Library API the CLI (b4-t4) shells over and the b5 EditMode consistency test calls
    // directly.
    public static class HeadlessValidator
    {
        public static HeadlessValidationResult Validate(string builderFilePath, ProjectLayout layout)
        {
            var source = File.ReadAllText(builderFilePath);
            var parse = BuilderParser.Parse(source);
            var provider = new DiskResolutionProvider(layout);
            var result = PlanningValidator.Validate(parse, source, provider, builderFilePath);

            var skipped = layout.ManagedDllsAvailable
                ? Array.Empty<string>()
                : new[] { "type", "asset" };

            return new HeadlessValidationResult
            {
                File = builderFilePath,
                Result = result,
                Skipped = skipped,
            };
        }
    }

    // Result envelope: the planning ValidationResult plus the categories that were SKIPPED (not
    // passed) because the Unity managed DLL dir was unlocatable.
    public sealed record HeadlessValidationResult
    {
        public string File { get; init; } = string.Empty;
        public ValidationResult Result { get; init; } = new();
        public IReadOnlyList<string> Skipped { get; init; } = Array.Empty<string>();
        public bool Ok => Result.Ok;
    }
}
