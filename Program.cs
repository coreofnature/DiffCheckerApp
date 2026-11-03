using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Urls.Add("http://localhost:5050");
app.UseDefaultFiles();
app.UseStaticFiles();

var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "http://localhost:5050",
            UseShellExecute = true
        });
    }
    catch { }
});

app.MapPost("/api/diff", (DiffRequest req) =>
{
    var leftLines = (req.OriginalText ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
    var rightLines = (req.ModifiedText ?? "").Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

    var rawDiff = DiffEngine.Compare(leftLines, rightLines);
    var processed = DiffEngine.EnrichWithCharDiff(rawDiff);
    return Results.Ok(processed);
});

app.MapPost("/api/shutdown", (IHostApplicationLifetime appLifetime) =>
{
    appLifetime.StopApplication();
    return Results.Ok();
});

app.Run();

// Modeller
public record DiffRequest(string OriginalText, string ModifiedText);

public record CharChunk(string Text, bool IsChanged);

public record DiffLine(
    string Type, 
    string Text, 
    int? OldLineNumber, 
    int? NewLineNumber, 
    List<CharChunk>? Chunks = null
);

public static class DiffEngine
{
    public static List<DiffLine> Compare(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length;
        int m = newLines.Length;
        int[,] dp = new int[n + 1, m + 1];

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < m; j++)
            {
                if (oldLines[i] == newLines[j])
                    dp[i + 1, j + 1] = dp[i, j] + 1;
                else
                    dp[i + 1, j + 1] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<DiffLine>();
        int x = n, y = m;

        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && oldLines[x - 1] == newLines[y - 1])
            {
                result.Add(new DiffLine("unchanged", oldLines[x - 1], x, y));
                x--;
                y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                result.Add(new DiffLine("added", newLines[y - 1], null, y));
                y--;
            }
            else if (x > 0 && (y == 0 || dp[x, y - 1] < dp[x - 1, y]))
            {
                result.Add(new DiffLine("deleted", oldLines[x - 1], x, null));
                x--;
            }
        }

        result.Reverse();
        return result;
    }

    // Birbiri ardına gelen silinen ve eklenen satırları eşleyip karakter diff'i uygular
    public static List<DiffLine> EnrichWithCharDiff(List<DiffLine> lines)
    {
        var output = new List<DiffLine>();

        for (int i = 0; i < lines.Count; i++)
        {
            // Eğer peş peşe deleted ve added geldiyse (yani modifiye satır)
            if (i < lines.Count - 1 && lines[i].Type == "deleted" && lines[i + 1].Type == "added")
            {
                var del = lines[i];
                var add = lines[i + 1];

                var (delChunks, addChunks) = ComputeCharDiff(del.Text, add.Text);

                output.Add(del with { Chunks = delChunks });
                output.Add(add with { Chunks = addChunks });
                i++; // İki satırı birden tükettik
            }
            else
            {
                output.Add(lines[i]);
            }
        }

        return output;
    }

    private static (List<CharChunk> OldChunks, List<CharChunk> NewChunks) ComputeCharDiff(string s1, string s2)
    {
        int n = s1.Length;
        int m = s2.Length;
        int[,] dp = new int[n + 1, m + 1];

        for (int i = 0; i < n; i++)
            for (int j = 0; j < m; j++)
                dp[i + 1, j + 1] = s1[i] == s2[j] ? dp[i, j] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var oldChunks = new List<CharChunk>();
        var newChunks = new List<CharChunk>();

        int x = n, y = m;
        var s1Diff = new List<(char ch, bool changed)>();
        var s2Diff = new List<(char ch, bool changed)>();

        while (x > 0 || y > 0)
        {
            if (x > 0 && y > 0 && s1[x - 1] == s2[y - 1])
            {
                s1Diff.Add((s1[x - 1], false));
                s2Diff.Add((s2[y - 1], false));
                x--; y--;
            }
            else if (y > 0 && (x == 0 || dp[x, y - 1] >= dp[x - 1, y]))
            {
                s2Diff.Add((s2[y - 1], true));
                y--;
            }
            else if (x > 0 && (y == 0 || dp[x, y - 1] < dp[x - 1, y]))
            {
                s1Diff.Add((s1[x - 1], true));
                x--;
            }
        }

        s1Diff.Reverse();
        s2Diff.Reverse();

        return (CompressChunks(s1Diff), CompressChunks(s2Diff));
    }

    private static List<CharChunk> CompressChunks(List<(char ch, bool changed)> list)
    {
        var result = new List<CharChunk>();
        if (list.Count == 0) return result;

        var currentText = new System.Text.StringBuilder().Append(list[0].ch);
        bool currentStatus = list[0].changed;

        for (int i = 1; i < list.Count; i++)
        {
            if (list[i].changed == currentStatus)
            {
                currentText.Append(list[i].ch);
            }
            else
            {
                result.Add(new CharChunk(currentText.ToString(), currentStatus));
                currentText.Clear().Append(list[i].ch);
                currentStatus = list[i].changed;
            }
        }
        result.Add(new CharChunk(currentText.ToString(), currentStatus));
        return result;
    }
}