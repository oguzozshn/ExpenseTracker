namespace ExpenseTracker.Services;

/// <summary>Where the ledger lives on disk. Registered once so the settings page can show it without recomputing it.</summary>
public record DataPaths(string Directory, string DatabaseFile)
{
    public long DatabaseBytes => File.Exists(DatabaseFile) ? new FileInfo(DatabaseFile).Length : 0;
}
