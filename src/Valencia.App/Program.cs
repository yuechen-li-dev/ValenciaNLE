using System.Runtime.Versioning;

namespace Valencia.App;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--render-probe")) return RenderProbe.Run(args);
        if (args.Contains("--help"))
        {
            Console.WriteLine("Valencia [media.mp4|project.valencia] (timeline editor)\nValencia --player video.mp4 (direct M0 player)\nValencia --smoke video.mp4 --output artifacts/local/smoke\nValencia --editor-smoke video.mp4 --output artifacts/local/m1b-smoke\nEditor: Ctrl+I import, Ctrl+O open project, Ctrl+S save, Ctrl+E export, Ctrl+K split, Ctrl+Z undo, Ctrl+Shift+Z redo.");
            return 0;
        }
        if (args.Contains("--prepare-shaders"))
        {
            try
            {
                Valencia.Rendering.NativeVideoPresenter.PrepareShaderArtifact(ValueAfter(args, "--prepare-shaders")
                    ?? throw new ArgumentException("--prepare-shaders requires an output file."));
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }
        bool smoke = args.Contains("--smoke") || args.Contains("--editor-smoke") || args.Contains("--analysis-smoke") || args.Contains("--transcript-smoke") || args.Contains("--creator-smoke") || args.Contains("--qualification-smoke") || args.Contains("--layout-smoke");
        try
        {
            bool analysisSmoke = args.Contains("--analysis-smoke");
            bool transcriptSmoke = args.Contains("--transcript-smoke");
            bool creatorSmoke = args.Contains("--creator-smoke");
            bool qualificationSmoke = args.Contains("--qualification-smoke");
            bool layoutSmoke = args.Contains("--layout-smoke");
            bool editorSmoke = args.Contains("--editor-smoke") || analysisSmoke || transcriptSmoke || creatorSmoke || qualificationSmoke || layoutSmoke;
            string? path = smoke ? ValueAfter(args, layoutSmoke ? "--layout-smoke" : qualificationSmoke ? "--qualification-smoke" : creatorSmoke ? "--creator-smoke" : transcriptSmoke ? "--transcript-smoke" : analysisSmoke ? "--analysis-smoke" : editorSmoke ? "--editor-smoke" : "--smoke") : args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal));
            if (smoke && path is null)
            {
                throw new ArgumentException("--smoke requires a video path.");
            }
            string output = Path.GetFullPath(ValueAfter(args, "--output") ?? "artifacts/local/smoke");
            if (!args.Contains("--player") && (!smoke || editorSmoke))
            {
                using var editor = new Editing.EditorWindow(path, editorSmoke ? output : null, analysisSmoke, transcriptSmoke, creatorSmoke, qualificationSmoke, layoutSmoke); editor.Run();
            }
            else { using var player = new PlayerWindow(path, smoke ? output : null); player.Run(); }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia", "Logs");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.txt"), error.ToString());
            if (!smoke)
            {
                MessageBox.Show(error.Message, "Valencia could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 1;
        }
    }

    private static string? ValueAfter(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
