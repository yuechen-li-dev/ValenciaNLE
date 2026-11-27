namespace Valencia.Core;

public static class ValenciaData
{
    public static string Root => Path.GetFullPath(Environment.GetEnvironmentVariable("VALENCIA_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Valencia"));
}
