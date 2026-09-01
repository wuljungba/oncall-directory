using System.ComponentModel.DataAnnotations;

namespace OnCallApi.Models;

/// <summary>
/// One upload, held between being read and being committed.
///
/// The importer used to be a single call: a file went in, and either everything was
/// written or the whole thing failed on the first bad row. That works for a clean
/// two-column CSV and not at all for what actually arrives -- a workbook of a dozen unit
/// rosters with inconsistent headers, where three rows out of four hundred are wrong.
///
/// Staging the rows makes the mapping correctable and the errors visible BEFORE anything
/// reaches the directory. The commit stays all-or-nothing over the rows the user chose to
/// include: a half-written directory is worse than a refused one, because nobody can tell
/// which half arrived.
/// </summary>
public class ImportJob
{
    public int Id { get; set; }

    /// <summary>The subscription the rows will be created in. Null for a super admin.</summary>
    public int? TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    [MaxLength(200)]
    public string? UploadedByName { get; set; }

    /// <summary>
    /// The uploader's object id or email, so a job can only be resumed by the person who
    /// started it. Staged rows are directory data belonging to a subscription.
    /// </summary>
    [MaxLength(200)]
    public string? UploadedByPrincipalId { get; set; }

    [MaxLength(400)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>draft | committed | abandoned</summary>
    [MaxLength(20)]
    public string Status { get; set; } = ImportJobStatus.Draft;

    /// <summary>
    /// Per-sheet column mapping as JSON: { "Sheet1": { "Work Email": "email" } }.
    /// Seeded from the header aliases and overridable by the user, which is the whole
    /// point -- a column the aliases do not know is otherwise silently ignored.
    /// </summary>
    public string? MappingJson { get; set; }

    /// <summary>Sheet names the user chose to exclude, as a JSON array.</summary>
    public string? ExcludedSheetsJson { get; set; }

    public int SheetCount { get; set; }
    public int TotalRows { get; set; }
    public int ImportedCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CommittedAt { get; set; }

    public ICollection<ImportJobRow> Rows { get; set; } = new List<ImportJobRow>();
}

public static class ImportJobStatus
{
    public const string Draft = "draft";
    public const string Committed = "committed";
    public const string Abandoned = "abandoned";
}

/// <summary>
/// One staged row, kept as the file wrote it.
///
/// The ORIGINAL cells are stored rather than the parsed result, so changing the mapping
/// re-reads them instead of asking for the file again -- and so the error report can hand
/// back exactly what was uploaded, with a reason beside it.
/// </summary>
public class ImportJobRow
{
    public int Id { get; set; }

    public int ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }

    [MaxLength(200)]
    public string SheetName { get; set; } = string.Empty;

    public int SheetIndex { get; set; }

    /// <summary>The row number as the person uploading would count it in Excel.</summary>
    public int SourceRow { get; set; }

    /// <summary>The row's cells as a JSON object keyed by the file's own header text.</summary>
    public string RawValuesJson { get; set; } = "{}";

    /// <summary>Whether this row is part of the commit. Excluded rows are kept, not deleted.</summary>
    public bool Included { get; set; } = true;

    /// <summary>Why this row cannot be imported as it stands, if it cannot.</summary>
    [MaxLength(1000)]
    public string? ErrorReason { get; set; }

    /// <summary>
    /// Why this row wants a human's eye even though it parsed -- an ambiguous name, most
    /// often. Distinct from <see cref="ErrorReason"/>: this one is importable, it is just
    /// not certain.
    /// </summary>
    [MaxLength(1000)]
    public string? ReviewReason { get; set; }

    /// <summary>create | merge | skip -- what to do when this row matches someone already here.</summary>
    [MaxLength(20)]
    public string Resolution { get; set; } = ImportRowResolution.Create;

    /// <summary>The existing directory entry this row was matched to, when it matched one.</summary>
    public Guid? MatchedEmployeeId { get; set; }

    /// <summary>How the match was made, for the preview to explain itself.</summary>
    [MaxLength(100)]
    public string? MatchedOn { get; set; }
}

public static class ImportRowResolution
{
    /// <summary>Add a new directory entry.</summary>
    public const string Create = "create";

    /// <summary>Update the matched entry with the columns this file supplied.</summary>
    public const string Merge = "merge";

    /// <summary>Leave the directory alone for this row.</summary>
    public const string Skip = "skip";

    public static bool IsKnown(string? value) => value is Create or Merge or Skip;
}
