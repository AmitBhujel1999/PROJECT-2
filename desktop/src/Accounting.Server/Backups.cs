using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace Accounting.Server;

/// <summary>One backup file of a company, newest first in <see cref="Backups.List"/>.</summary>
public sealed record BackupFile(string Name, DateOnly Date, long Size, DateTime ModifiedUtc);

/// <summary>
/// Copies of every company file in a folder the user picks (a Google Drive or OneDrive folder, or a
/// USB drive), so the books survive a broken or lost computer. One file per company per day,
/// refreshed when the program closes; the last <see cref="Keep"/> days are kept.
///
/// A restore never swaps a file that is open: it leaves the chosen backup beside the company file as
/// &lt;file&gt;.restore, and the next start puts it in place (<see cref="ApplyPendingRestore"/>).
/// Before that, the current books are saved as a "before-restore" backup, so a restore can be undone.
/// </summary>
public sealed partial class Backups(CompanyRegistry companies) : BackgroundService
{
    public const int Keep = 30;
    public const string SubFolder = "Mero Lekhapal backups";
    private readonly Lock _lock = new();

    private sealed record Saved(string? Folder, DateTime? LastUtc, string? LastError);

    private string SettingsFile => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(companies.MainFile))!, "backup.json");

    private Saved Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(SettingsFile)) ?? new(null, null, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(null, null, null);
        }
    }

    private void Save(Saved s) => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s));

    /// <summary>The folder the user chose, or null when backups are off.</summary>
    public string? Folder => Load().Folder;

    /// <summary>Where the files go: a folder of their own inside the chosen one.</summary>
    public string? Target => Folder is { } f ? Path.Combine(f, SubFolder) : null;

    public DateTime? LastUtc => Load().LastUtc;
    public string? LastError => Load().LastError;

    /// <summary>Choose the folder (null switches backups off). The folder must exist and take new files.</summary>
    public void SetFolder(string? folder)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                Save(Load() with { Folder = null, LastError = null });
                return;
            }
            folder = folder.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                throw new ValidationError("folder", "This folder does not exist on this computer.");
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, SubFolder));
                var probe = Path.Combine(folder, SubFolder, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ValidationError("folder", "Mero Lekhapal cannot save files in this folder.");
            }
            Save(Load() with { Folder = Path.GetFullPath(folder), LastError = null });
        }
    }

    /// <summary>Folders worth offering: Google Drive for desktop ("My Drive" on its own drive letter) and OneDrive.</summary>
    public static IReadOnlyList<(string Label, string Path)> Suggestions()
    {
        var list = new List<(string, string)>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "My Drive")))
                    list.Add(("Google Drive", Path.Combine(drive.RootDirectory.FullName, "My Drive")));
            }
            catch (IOException)
            {
                // A drive that went away while looking.
            }
        }
        if (Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive && Directory.Exists(oneDrive))
            list.Add(("OneDrive", oneDrive));
        return list;
    }

    [GeneratedRegex(@"^MeroLekhapal-(?<slug>[a-z0-9][a-z0-9-]*?)-(?<date>\d{4}-\d{2}-\d{2})(?<before>-before-restore-\d{6})?\.db$")]
    private static partial Regex FileName();

    private static string DailyName(string slug, DateOnly date) => $"MeroLekhapal-{slug}-{date:yyyy-MM-dd}.db";

    /// <summary>The company's backups in the folder, newest first.</summary>
    public IReadOnlyList<BackupFile> List(string slug)
    {
        if (Target is not { } target || !Directory.Exists(target))
            return [];
        return Directory.GetFiles(target, $"MeroLekhapal-{slug}-*.db")
            .Select(f => new FileInfo(f))
            .Select(f => (f, m: FileName().Match(f.Name)))
            .Where(x => x.m.Success && x.m.Groups["slug"].Value == slug)
            .Select(x => new BackupFile(x.f.Name, DateOnly.ParseExact(x.m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture), x.f.Length, x.f.LastWriteTimeUtc))
            .OrderByDescending(b => b.ModifiedUtc)
            .ToList();
    }

    /// <summary>Back up every company now (today's file of each is replaced). False when backups are off.</summary>
    public bool RunAll() => Run(onlyMissing: false);

    private bool Run(bool onlyMissing)
    {
        lock (_lock)
        {
            var saved = Load();
            if (saved.Folder is null)
                return false;
            var target = Path.Combine(saved.Folder, SubFolder);
            try
            {
                Directory.CreateDirectory(target);
                var done = false;
                foreach (var (slug, _) in companies.List())
                {
                    var db = companies.Get(slug)!;
                    if (File.Exists(RestoreFile(db.Path)))
                        continue; // Its books are about to be replaced; keep the backups as they are.
                    var name = DailyName(slug, db.Today());
                    if (onlyMissing && File.Exists(Path.Combine(target, name)))
                        continue;
                    Snapshot(db, Path.Combine(target, name));
                    Prune(target, slug);
                    done = true;
                }
                if (done || !onlyMissing)
                    Save(saved with { LastUtc = DateTime.UtcNow, LastError = null });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
            {
                Save(saved with { LastError = ex.Message });
                throw new BusinessError($"The backup could not be saved: {ex.Message}", "BACKUP_FAILED", 500);
            }
        }
    }

    /// <summary>A consistent copy of the open company file, even while it is being used.</summary>
    private static void Snapshot(Database db, string file)
    {
        // Written under another name first, so a cloud folder never uploads half a file.
        var partial = file + ".partial";
        if (File.Exists(partial))
            File.Delete(partial);
        db.Read(tx => tx.Execute("VACUUM INTO @partial", new { partial }));
        File.Move(partial, file, overwrite: true);
    }

    private void Prune(string target, string slug)
    {
        var files = List(slug);
        foreach (var old in files.Where(f => !f.Name.Contains("-before-restore-")).Skip(Keep)
                     .Concat(files.Where(f => f.Name.Contains("-before-restore-")).Skip(5)))
            File.Delete(Path.Combine(target, old.Name));
    }

    private static string RestoreFile(string companyFile) => companyFile + ".restore";

    public bool RestorePending(string slug) => File.Exists(RestoreFile(companies.Get(slug)!.Path));

    /// <summary>
    /// Get <paramref name="name"/> ready to replace the company's books at the next start. The current
    /// books are backed up first as "before-restore". Returns the date of the chosen backup.
    /// </summary>
    public DateOnly Restore(string slug, string name)
    {
        lock (_lock)
        {
            var file = List(slug).FirstOrDefault(b => b.Name == name)
                       ?? throw new BusinessError("That backup was not found in the backup folder.", "NOT_FOUND", 404);
            var source = Path.Combine(Target!, file.Name);
            Check(source);
            var db = companies.Get(slug)!;
            // Copy first, then check the copy: a cloud folder may still be downloading the original.
            var staged = RestoreFile(db.Path) + ".partial";
            File.Copy(source, staged, overwrite: true);
            try
            {
                Check(staged);
                Snapshot(db, Path.Combine(Target!, $"MeroLekhapal-{slug}-{db.Today():yyyy-MM-dd}-before-restore-{DateTime.Now:HHmmss}.db"));
                Prune(Target!, slug);
                File.Move(staged, RestoreFile(db.Path), overwrite: true);
            }
            finally
            {
                if (File.Exists(staged))
                    File.Delete(staged);
            }
            return file.Date;
        }
    }

    /// <summary>The file must be a whole company file of this program, not from a newer version.</summary>
    private static void Check(string file)
    {
        try
        {
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            if (cmd.ExecuteScalar() as string != "ok")
                throw new BusinessError("This backup file is damaged and cannot be restored.", "BACKUP_DAMAGED", 400);
            cmd.CommandText = "PRAGMA user_version";
            if ((long)cmd.ExecuteScalar()! > Database.SchemaVersion)
                throw new BusinessError("This backup was made by a newer version of Mero Lekhapal. Update the program first.", "BACKUP_NEWER", 400);
            cmd.CommandText = "SELECT COUNT(*) FROM business_settings";
            cmd.ExecuteScalar();
        }
        catch (SqliteException)
        {
            throw new BusinessError("This file is not a Mero Lekhapal backup, or it is damaged.", "BACKUP_DAMAGED", 400);
        }
    }

    /// <summary>On start, before the company file is opened: put a restored backup in place.</summary>
    public static void ApplyPendingRestore(string companyFile)
    {
        var restore = RestoreFile(companyFile);
        if (!File.Exists(restore))
            return;
        foreach (var suffix in new[] { "-wal", "-shm", "" })
            if (File.Exists(companyFile + suffix))
                File.Delete(companyFile + suffix);
        File.Move(restore, companyFile);
    }

    // ------------------------------------------------------------------ when

    /// <summary>Once a day while the program is open (today's file, if there is none yet).</summary>
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Run(onlyMissing: true);
                }
                catch (BusinessError)
                {
                    // Saved as LastError; shown on the backup screen.
                }
                await Task.Delay(TimeSpan.FromMinutes(15), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
    }

    /// <summary>When the program closes: refresh today's backups with the day's last changes.</summary>
    public override async Task StopAsync(CancellationToken ct)
    {
        await base.StopAsync(ct);
        try
        {
            RunAll();
        }
        catch (BusinessError)
        {
            // Saved as LastError.
        }
    }
}
