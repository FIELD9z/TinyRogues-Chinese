using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using TinyRogues.Chinese.Core;

try
{
if (args.Length != 2) throw new ArgumentException("Usage: Audit <repository> <audit-directory>");
var root = Path.GetFullPath(args[0]);
var directory = Path.GetFullPath(args[1]);
var files = Directory.GetFiles(Path.Combine(root, "locales/zh-Hans"), "*.json").Order(StringComparer.Ordinal).ToArray();
var catalog = new Catalog(files.Select(p => (PackJson.Read(p), false, p)));
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var counts = new Dictionary<string, int>();
var total = 0;
var seen = new HashSet<string>(StringComparer.Ordinal);
using var output = new StreamWriter(Path.Combine(directory, "checked.jsonl"), false, new System.Text.UTF8Encoding(false));
foreach (var line in File.ReadLines(Path.Combine(directory, "inputs.jsonl")))
{
    var row = JsonSerializer.Deserialize<Input>(line, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    if (!seen.Add(row.Id)) throw new InvalidDataException("Duplicate input ID: " + row.Id);
    var target = row.Source;
    var matched = false;
    string? error = null;
    string status;
    if (row.Kind == "regex-rule")
    {
        try { _ = new Regex(row.Source, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(30)); status = "regex-requires-concrete-inputs"; }
        catch (ArgumentException e) { status = "invalid-regex"; error = e.Message; }
    }
    else if (row.Kind == "template-specification") status = "template-requires-concrete-inputs";
    else if (row.Source.Length > 12000) status = "oversize-input-unverified";
    else
    {
        try
        {
            var context = string.IsNullOrEmpty(row.Context) ? "*" : row.Context;
            // Exactly the route used by Plugin.Translate. No search over all contexts.
            matched = context.StartsWith("ui:", StringComparison.Ordinal) || context == "styled-ui"
                ? catalog.TryTranslateLabel(row.Source, context, out target)
                : catalog.TryTranslate(row.Source, context, out target);
            status = !row.ContextKnown ? "context-unresolved"
                : !matched ? "catalog-no-match"
                : Regex.IsMatch(Plain(target), "[A-Za-z]") ? "matched-with-latin-needs-review"
                : target == row.Source ? "unchanged-match-needs-review" : "static-match-only";
        }
        catch (Exception e) { status = "check-error"; error = e.GetType().Name + ": " + e.Message; }
    }
    var sourcePlain = Plain(row.Source);
    var targetPlain = Plain(target);
    // Numbers inside tags are not displayed values; sprite names/indices are checked separately.
    var numbersPreserved = Bag(Regex.Matches(sourcePlain, @"[+\-−]?\d+(?:[.,]\d+)?%?").Select(m => m.Value))
        .SequenceEqual(Bag(Regex.Matches(targetPlain, @"[+\-−]?\d+(?:[.,]\d+)?%?").Select(m => m.Value)));
    var originalSprites = Regex.Matches(row.Source, @"<sprite\b[^>]*>").Select(m => m.Value).ToArray();
    var outputSprites = Regex.Matches(target, @"<sprite\b[^>]*>").Select(m => m.Value).ToArray();
    var spritesPreserved = originalSprites.GroupBy(x => x).All(g => outputSprites.Count(x => x == g.Key) >= g.Count());
    if (matched && (!numbersPreserved || !spritesPreserved)) status = "preservation-needs-review";
    output.WriteLine(JsonSerializer.Serialize(new {
        row.Id, row.Source, row.Context, row.ContextKnown, row.Route, row.Kind, row.Origins,
        row.Flags, row.HistoricalNativeEvidence, status, matched, target, error,
        discoveryOnly = !row.ContextKnown, discoveryMatched = !row.ContextKnown && matched,
        verifiedContextMatch = row.ContextKnown && matched,
        sourceHasLatin = Regex.IsMatch(sourcePlain, "[A-Za-z]"),
        remainingLatin = Regex.Matches(targetPlain, "[A-Za-z]+").Select(m => m.Value).Distinct().ToArray(),
        numbersPreserved, originalSpritesPreserved = spritesPreserved,
        currentNativeVerified = false, meaningReviewed = false, layoutVerified = false,
        applicability = "unresolved", reason = Reason(status)
    }, json));
    counts[status] = counts.GetValueOrDefault(status) + 1;
    if (++total % 10000 == 0) Console.WriteLine($"Checked {total} rows");
}
output.Flush();
output.Dispose();
if (total == 0) throw new InvalidDataException("Empty audit input is not a successful check.");
var summary = new {
    completedUtc = DateTime.UtcNow.ToString("O"),
    checkedRows = total, catalogEntries = catalog.EntryCount, statusCounts = counts,
    inputSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "inputs.jsonl")))),
    checkedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, "checked.jsonl")))),
    checkerAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location))),
    packHashes = files.Select(p => new { file = Path.GetFileName(p), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) }),
    policy = "One result for every input. Unknown contexts are discovery only. Static matches never imply native, semantic or layout verification. Latin letters, including single letters, are retained for review."
};
File.WriteAllText(Path.Combine(directory, "check-summary.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(json) { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { total, counts, catalog.EntryCount }, json));
return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("Audit failed: " + error.Message);
    return 1;
}

static string Plain(string value) => Regex.Replace(value, "<[^>]*>", "");
static string[] Bag(IEnumerable<string> values) => values.Order(StringComparer.Ordinal).ToArray();
static string Reason(string status) => status switch {
    "context-unresolved" => "Source location is not a verified runtime translation context; wildcard result is discovery only.",
    "catalog-no-match" => "No catalog match at this context. Confirm input stage, completeness and actual render path before declaring a visible leak.",
    "matched-with-latin-needs-review" => "Output retains Latin letters. Bindings, codes and ordinary English must be reviewed individually.",
    "static-match-only" => "Catalog output contains no visible Latin letters; native invocation, meaning and layout remain unverified.",
    "regex-requires-concrete-inputs" => "Regex syntax checked; every relevant runtime variant still needs concrete input evidence.",
    "template-requires-concrete-inputs" => "Template is a specification, not an actual game input. Concrete variables and invocation remain unverified.",
    "oversize-input-unverified" => "Source exceeds 12000 characters. Preserved in full; not passed through the ordinary short-label audit path.",
    _ => "Manual investigation required; this entry is not counted as verified."
};
record Input(string Id, string Source, string Context, bool ContextKnown, string Route, string Kind,
    JsonElement[] Origins, string[] Flags, JsonElement[] HistoricalNativeEvidence);
