using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// This app's first browser-uploaded-file parsing (confirmed zero
/// precedent elsewhere -- the real ETL only ever does server-side-path
/// BULK INSERT). Uses CsvHelper rather than hand-rolled comma/quote
/// splitting, same "don't hand-roll something a vetted library already
/// solves correctly" reasoning as this app's own MailKit dependency.
/// </summary>
public static class CsvFileReader
{
    /// <summary>
    /// D-198: the most data rows one file may have. Every row is held in
    /// memory while it's imported, so an unbounded file (a browser upload or
    /// a feed) could exhaust the API's memory. Split a bigger file.
    /// </summary>
    public const int MaxRows = 250_000;

    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadRowsAsync(Stream fileStream)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];

        var rows = new List<IReadOnlyDictionary<string, string>>();
        while (await csv.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in headers)
            {
                row[header] = csv.GetField(header) ?? "";
            }
            rows.Add(row);
            if (rows.Count > MaxRows)
            {
                throw Errors.ApiProblemException.BadRequest(
                    $"The file has more than {MaxRows:N0} rows. Split it into smaller files and import each one.");
            }
        }

        return rows;
    }

    /// <summary>D-181: just the header row, for the Data Sources Test button (doesn't read the rest of the file).</summary>
    public static async Task<IReadOnlyList<string>> ReadHeaderAsync(Stream fileStream)
    {
        using var reader = new StreamReader(fileStream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture));
        if (!await csv.ReadAsync()) return [];
        csv.ReadHeader();
        return csv.HeaderRecord ?? [];
    }
}
