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
        }

        return rows;
    }
}
