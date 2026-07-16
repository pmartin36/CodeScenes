using SceneBuilder.Core.Validation;

namespace SceneBuilder.Validate
{
    // Thin console shell: parse args -> ProjectLayout.Infer -> HeadlessValidator.Validate ->
    // DiagnosticRenderer -> write + exit code. No resolution/format logic lives here; see
    // SceneBuilder.Core/Validation/DiagnosticRenderer.cs and HeadlessValidator.cs.
    public static class Program
    {
        public static int Main(string[] args)
        {
            int start = 0;
            if (args.Length > 0 && args[0] == "validate")
            {
                start = 1;
            }

            string? builderFile = null;
            string? projectOverride = null;
            string? managedOverride = null;
            bool json = false;

            for (int i = start; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--project":
                        if (i + 1 >= args.Length)
                        {
                            System.Console.Error.WriteLine("--project requires a value.");
                            return 2;
                        }
                        projectOverride = args[++i];
                        break;
                    case "--managed":
                        if (i + 1 >= args.Length)
                        {
                            System.Console.Error.WriteLine("--managed requires a value.");
                            return 2;
                        }
                        managedOverride = args[++i];
                        break;
                    case "--json":
                        json = true;
                        break;
                    default:
                        if (builderFile is null)
                        {
                            builderFile = args[i];
                        }
                        break;
                }
            }

            if (builderFile is null)
            {
                System.Console.Error.WriteLine(
                    "Usage: codescenes validate <builderFile> [--project <dir>] [--managed <dir>] [--json]");
                return 2;
            }

            try
            {
                var layout = ProjectLayout.Infer(builderFile, projectOverride, managedOverride);
                var result = HeadlessValidator.Validate(builderFile, layout);

                System.Console.Write(json ? DiagnosticRenderer.RenderJson(result) : DiagnosticRenderer.RenderText(result));
                System.Console.WriteLine();

                return DiagnosticRenderer.ExitCode(result);
            }
            catch (System.Exception ex) when (ex is System.IO.FileNotFoundException or System.InvalidOperationException)
            {
                System.Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }
    }
}
