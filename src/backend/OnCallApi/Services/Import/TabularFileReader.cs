using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace OnCallApi.Services.Import;

/// <summary>
/// Turns an uploaded file into a header row plus data rows, whether it arrived as a CSV
/// or as an Excel .xlsx workbook.
///
/// The format is decided by CONTENT, never by the file extension. A workbook saved or
/// renamed as "staff.csv" is extremely common — it used to be fed to the CSV line
/// splitter, whose "header" then came out of the zip's binary preamble, producing one
/// meaningless error per binary line ("Expected 2 columns, got 1") and importing nothing.
/// Sniffing the leading bytes turns that into either a successful import or a single
/// sentence the person uploading can act on.
/// </summary>
public static class TabularFileReader
{
    /// <summary>Upper bound on data rows accepted from one file.</summary>
    public const int MaxDataRows = 10_000;

    /// <summary>One data row, numbered as the person uploading would count it.</summary>
    /// <param name="Number">
    /// The CSV physical line number, or the worksheet row number shown in Excel, so an
    /// error message points at a row they can actually find.
    /// </param>
    public sealed record TabularRow(int Number, string[] Values);

    public sealed record TabularDocument(string[] Headers, IReadOnlyList<TabularRow> Rows);

    /// <summary>
    /// Reads <paramref name="stream"/> into headers + rows, or returns a single error
    /// describing why the whole file is unusable. Errors here are whole-file problems;
    /// per-row validation stays with the caller.
    /// </summary>
    public static async Task<(TabularDocument? Document, string? Error)> ReadAsync(Stream stream)
    {
        var (format, error) = await DetectFormatAsync(stream);
        if (error != null) return (null, error);

        return format == FileFormat.Xlsx
            ? ReadXlsx(stream)
            : await ReadCsvAsync(stream);
    }

    // ── Format detection ──

    private enum FileFormat { Csv, Xlsx }

    private static readonly byte[] ZipMagic = [0x50, 0x4B];              // "PK" — xlsx is a zip
    private static readonly byte[] Ole2Magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    private const string LegacyXlsMessage =
        "This file is a legacy Excel .xls workbook. Open it in Excel and choose "
        + "File > Save As > Excel Workbook (.xlsx), or CSV UTF-8, then upload that.";

    private const string BinaryMessage =
        "This file is not a CSV or an Excel workbook. Upload a .csv or .xlsx file "
        + "(in Excel: File > Save As > CSV UTF-8, or Excel Workbook).";

    private static async Task<(FileFormat Format, string? Error)> DetectFormatAsync(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;

        var probe = new byte[4096];
        var read = await ReadAtLeastAsync(stream, probe, probe.Length);
        if (stream.CanSeek) stream.Position = 0;

        if (read == 0) return (FileFormat.Csv, "The uploaded file is empty.");

        if (StartsWith(probe, read, Ole2Magic)) return (FileFormat.Csv, LegacyXlsMessage);
        if (StartsWith(probe, read, ZipMagic)) return (FileFormat.Xlsx, null);

        // A UTF-16/UTF-32 text file is full of NUL bytes but is perfectly readable — the
        // BOM says so, and StreamReader decodes it. Excel's "Unicode Text" export is
        // exactly this, so the NUL scan below must not condemn it.
        if (HasTextBom(probe, read)) return (FileFormat.Csv, null);

        if (Array.IndexOf(probe, (byte)0, 0, read) >= 0) return (FileFormat.Csv, BinaryMessage);

        return (FileFormat.Csv, null);
    }

    private static bool HasTextBom(byte[] buffer, int length) =>
        StartsWith(buffer, length, [0xEF, 0xBB, 0xBF])          // UTF-8
        || StartsWith(buffer, length, [0xFF, 0xFE])             // UTF-16 LE (and UTF-32 LE)
        || StartsWith(buffer, length, [0xFE, 0xFF]);            // UTF-16 BE

    private static bool StartsWith(byte[] buffer, int length, byte[] prefix)
    {
        if (length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (buffer[i] != prefix[i]) return false;
        return true;
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ── CSV ──

    private static async Task<(TabularDocument? Document, string? Error)> ReadCsvAsync(Stream stream)
    {
        // leaveOpen: the caller owns the stream (the controller's MemoryStream).
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024, leaveOpen: true);

        var headerLine = await reader.ReadLineAsync();
        if (headerLine == null) return (null, "CSV file is empty.");

        var headers = ParseCsvLine(headerLine).Select(CleanCell).ToArray();

        var rows = new List<TabularRow>();
        var lineNumber = 1;
        while (!reader.EndOfStream)
        {
            lineNumber++;
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (rows.Count >= MaxDataRows) return (null, TooManyRowsMessage);

            // Row width is left exactly as the file had it: a genuinely ragged CSV must
            // still be reported, so the caller compares it against the header width.
            rows.Add(new TabularRow(lineNumber, ParseCsvLine(line)));
        }

        return (new TabularDocument(headers, rows), null);
    }

    /// <summary>
    /// Parses a single CSV line, handling quoted fields and escaped double-quotes.
    /// </summary>
    private static string[] ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                // Handle escaped double-quotes inside quoted fields
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++; // skip the second quote of the pair
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (line[i] == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(line[i]);
            }
        }
        values.Add(current.ToString());

        return values.ToArray();
    }

    // ── XLSX ──

    private const string NotAWorkbookMessage =
        "This file looks like a zip archive rather than an Excel workbook. Upload a .xlsx "
        + "workbook or a .csv file.";

    private const string NoHeaderMessage =
        "The first sheet has no header row. Row 1 must name the columns "
        + "(firstName, lastName, email, and so on).";

    private static readonly string TooManyRowsMessage =
        $"This file has more than {MaxDataRows:N0} rows. Split it into smaller files and import them in turn.";

    private static (TabularDocument? Document, string? Error) ReadXlsx(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;

        SpreadsheetDocument doc;
        try
        {
            doc = SpreadsheetDocument.Open(stream, isEditable: false);
        }
        catch (Exception)
        {
            // Any zip that is not a workbook lands here (.docx, .zip, a renamed archive).
            return (null, NotAWorkbookMessage);
        }

        using (doc)
        {
            // An ordinary zip opens as a valid OPC package with no workbook inside it, so
            // this is where a .zip or a renamed archive is caught -- not in the catch above.
            var workbookPart = doc.WorkbookPart;
            if (workbookPart?.Workbook == null)
                return (null, NotAWorkbookMessage);

            var sheet = workbookPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault();
            if (sheet?.Id?.Value == null)
                return (null, "This workbook has no worksheets.");

            if (workbookPart.GetPartById(sheet.Id!.Value!) is not WorksheetPart worksheetPart)
                return (null, NotAWorkbookMessage);

            var strings = workbookPart.SharedStringTablePart?.SharedStringTable;
            var dateStyles = BuildDateStyleSet(workbookPart);
            var epochShift = workbookPart.Workbook?.WorkbookProperties?.Date1904?.Value == true ? 1462 : 0;

            string[]? headers = null;
            var rows = new List<TabularRow>();

            foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
            {
                var values = ExpandRow(row, strings, dateStyles, epochShift, headers?.Length);
                if (values == null) continue; // wholly empty row

                if (headers == null)
                {
                    headers = values.Select(CleanCell).ToArray();
                    continue;
                }

                if (rows.Count >= MaxDataRows) return (null, TooManyRowsMessage);

                rows.Add(new TabularRow((int)(row.RowIndex?.Value ?? (uint)(rows.Count + 2)), values));
            }

            if (headers == null || headers.All(string.IsNullOrWhiteSpace))
                return (null, NoHeaderMessage);

            return (new TabularDocument(headers, rows), null);
        }
    }

    /// <summary>
    /// Reads one worksheet row into a dense array, or null when the row holds nothing.
    ///
    /// Excel omits empty cells from the XML entirely, so cells must be placed by their
    /// column reference ("D7" is index 3). Reading them in document order instead shifts
    /// every value after a blank cell one column to the left, which in this directory
    /// means an employee silently acquiring someone else's phone number.
    /// </summary>
    /// <param name="width">
    /// Header width, when known. Data rows are padded up to it, because a trailing blank
    /// cell is a present-but-empty value rather than a ragged row.
    /// </param>
    private static string[]? ExpandRow(
        Row row, SharedStringTable? strings, HashSet<uint> dateStyles, int epochShift, int? width)
    {
        var cells = new SortedDictionary<int, string>();

        foreach (var cell in row.Elements<Cell>())
        {
            var value = ReadCell(cell, strings, dateStyles, epochShift);
            if (string.IsNullOrEmpty(value)) continue;

            var index = ColumnIndex(cell.CellReference?.Value);
            if (index < 0) index = cells.Count;
            cells[index] = value;
        }

        if (cells.Count == 0) return null;

        var size = Math.Max(cells.Keys.Max() + 1, width ?? 0);
        var values = new string[size];
        for (var i = 0; i < size; i++)
            values[i] = cells.TryGetValue(i, out var v) ? v : string.Empty;

        return values;
    }

    /// <summary>Turns a cell reference such as "AB12" into a zero-based column index.</summary>
    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return -1;

        var index = 0;
        var sawLetter = false;
        foreach (var c in cellReference)
        {
            if (!char.IsLetter(c)) break;
            sawLetter = true;
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return sawLetter ? index - 1 : -1;
    }

    private static string ReadCell(Cell cell, SharedStringTable? strings, HashSet<uint> dateStyles, int epochShift)
    {
        var raw = cell.CellValue?.InnerText;

        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (raw == null || strings == null) return string.Empty;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                   && i >= 0 && i < strings.ChildElements.Count
                ? strings.ChildElements[i].InnerText
                : string.Empty;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
            return cell.InlineString?.Text?.Text ?? cell.InnerText;

        if (cell.DataType?.Value == CellValues.Boolean)
            return raw == "1" ? "TRUE" : "FALSE";

        if (raw == null) return string.Empty;

        // A date-formatted cell holds a serial number, not text. Handing "45678.5" to the
        // shift importer fails as "startTime is required (ISO 8601 format)", which tells
        // nobody anything — so it is converted here, once, for every importer.
        if (cell.StyleIndex?.Value is uint styleIndex && dateStyles.Contains(styleIndex)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
        {
            try
            {
                return DateTime.FromOADate(serial + epochShift)
                    .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch (ArgumentException)
            {
                return raw; // out of DateTime range — pass it through and let validation speak
            }
        }

        return raw;
    }

    /// <summary>
    /// The style indexes whose number format renders as a date or a time.
    /// </summary>
    private static HashSet<uint> BuildDateStyleSet(WorkbookPart workbookPart)
    {
        var dateStyles = new HashSet<uint>();

        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        var cellFormats = stylesheet?.CellFormats;
        if (cellFormats == null) return dateStyles;

        // Custom formats (id >= 164) must be read from the workbook; built-ins are fixed.
        var customDateFormats = new HashSet<uint>();
        if (stylesheet?.NumberingFormats != null)
        {
            foreach (var format in stylesheet.NumberingFormats.Elements<NumberingFormat>())
            {
                var code = format.FormatCode?.Value;
                if (format.NumberFormatId?.Value is uint id && code != null && LooksLikeDateFormat(code))
                    customDateFormats.Add(id);
            }
        }

        uint styleIndex = 0;
        foreach (var format in cellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value;
            if (id != null && (IsBuiltInDateFormat(id.Value) || customDateFormats.Contains(id.Value)))
                dateStyles.Add(styleIndex);
            styleIndex++;
        }

        return dateStyles;
    }

    /// <summary>Built-in ECMA-376 number format ids that render as a date or a time.</summary>
    private static bool IsBuiltInDateFormat(uint id) =>
        (id >= 14 && id <= 22) || (id >= 27 && id <= 36) || (id >= 45 && id <= 47) || (id >= 50 && id <= 58);

    private static bool LooksLikeDateFormat(string formatCode)
    {
        // Strip colour and locale sections before looking for tokens, so a bracketed
        // directive such as [$-409] does not read as a date token.
        var stripped = Regex.Replace(formatCode, @"\[[^\]]*\]", "");
        return stripped.IndexOfAny(['y', 'm', 'd', 'h', 's', 'Y', 'M', 'D', 'H', 'S']) >= 0
               && !stripped.Contains('#') && !stripped.Contains('0');
    }

    // ── Shared ──

    /// <summary>Trims whitespace, surrounding quotes and a stray BOM from a header cell.</summary>
    private static string CleanCell(string value) =>
        value.Trim().Trim('"').Trim().TrimStart('﻿');
}
