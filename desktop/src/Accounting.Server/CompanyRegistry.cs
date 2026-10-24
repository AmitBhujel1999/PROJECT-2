using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Accounting.Core.Common;
using Accounting.Core.Data;

namespace Accounting.Server;

/// <summary>
/// Several companies, one SQLite file each. The first company ("main") is the program's own file;
/// companies added later live in a "companies" folder beside it as &lt;slug&gt;.db.
/// </summary>
public sealed partial class CompanyRegistry(string mainFile, Func<DateOnly>? today = null)
{
    public const string MainSlug = "main";
    private readonly ConcurrentDictionary<string, Database> _open = new();

    public string MainFile { get; } = mainFile;
    private string Folder => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(MainFile))!, "companies");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,49}$")]
    private static partial Regex SlugPattern();

    private string FileFor(string slug) => slug == MainSlug ? MainFile : Path.Combine(Folder, slug + ".db");

    public bool Exists(string slug) => SlugPattern().IsMatch(slug) && File.Exists(FileFor(slug));

    /// <summary>The company's database (opened once and kept), or null for an unknown slug.</summary>
    public Database? Get(string slug)
    {
        if (!Exists(slug) && slug != MainSlug)
            return null;
        return _open.GetOrAdd(slug, s =>
        {
            var db = Database.Open(FileFor(s));
            if (today is not null)
                db.Today = today;
            return db;
        });
    }

    public Database Main => Get(MainSlug)!;

    /// <summary>Every company with its business name, main first.</summary>
    public IReadOnlyList<(string Slug, string Name)> List()
    {
        var slugs = new List<string> { MainSlug };
        if (Directory.Exists(Folder))
            slugs.AddRange(Directory.GetFiles(Folder, "*.db").Select(f => Path.GetFileNameWithoutExtension(f)!).Where(s => SlugPattern().IsMatch(s)).Order());
        return slugs.Select(s => (s, Get(s)!.Read(BusinessSettings.Get).BusinessName)).ToList();
    }

    /// <summary>Remove a company that failed to set up (its file is closed and deleted).</summary>
    public void Discard(string slug)
    {
        if (slug == MainSlug || !_open.TryRemove(slug, out _))
            return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(FileFor(slug) + suffix))
                File.Delete(FileFor(slug) + suffix);
    }

    /// <summary>Create an empty company file and return its slug (made from the name, unique).</summary>
    public (string Slug, Database Db) Create(string name)
    {
        Directory.CreateDirectory(Folder);
        var baseSlug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        baseSlug = Regex.Replace(baseSlug, "-+", "-");
        if (baseSlug.Length == 0 || baseSlug == MainSlug)
            baseSlug = "company";
        baseSlug = baseSlug[..Math.Min(baseSlug.Length, 40)];
        var slug = baseSlug;
        for (var n = 2; File.Exists(FileFor(slug)); n++)
            slug = $"{baseSlug}-{n}";
        return (slug, Get(slug) ?? _open.GetOrAdd(slug, s => Database.Open(FileFor(s))));
    }
}
