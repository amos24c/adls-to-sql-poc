namespace EventHubSasTest;

public enum CheckResult
{
    Pass,
    Fail,
    Warn,
    Skip,
    Info,
}

/// <summary>Console output for the test run, plus a tally of what passed and failed.</summary>
public sealed class Report
{
    private readonly List<(string Name, CheckResult Result, string Detail)> _entries = [];

    public bool HasFailures => _entries.Any(e => e.Result == CheckResult.Fail);

    public void Section(string title)
    {
        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan, $"── {title} ".PadRight(78, '─'));
    }

    public void Record(string name, CheckResult result, string detail = "")
    {
        _entries.Add((name, result, detail));
        Write(result, name, detail);
    }

    /// <summary>Writes a line without adding it to the final tally.</summary>
    public void Detail(string text) => Console.WriteLine($"         {text}");

    public void Hint(string text) => WriteColored(ConsoleColor.DarkYellow, $"         → {text}");

    private static void Write(CheckResult result, string name, string detail)
    {
        var (label, color) = result switch
        {
            CheckResult.Pass => ("  OK  ", ConsoleColor.Green),
            CheckResult.Fail => (" FAIL ", ConsoleColor.Red),
            CheckResult.Warn => (" WARN ", ConsoleColor.Yellow),
            CheckResult.Skip => (" SKIP ", ConsoleColor.DarkGray),
            _ => (" INFO ", ConsoleColor.Gray),
        };

        var previous = Console.ForegroundColor;
        Console.Write("[");
        Console.ForegroundColor = color;
        Console.Write(label);
        Console.ForegroundColor = previous;
        Console.Write("] ");
        Console.WriteLine(detail.Length == 0 ? name : $"{name}: {detail}");
    }

    public void Summary()
    {
        Section("Summary");

        foreach (var group in _entries.GroupBy(e => e.Result).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  {group.Key,-5} {group.Count()}");
        }

        Console.WriteLine();

        if (HasFailures)
        {
            WriteColored(ConsoleColor.Red, "RESULT: FAILED — see the failures above.");
            foreach (var entry in _entries.Where(e => e.Result == CheckResult.Fail))
            {
                Console.WriteLine($"  - {entry.Name}: {entry.Detail}");
            }
        }
        else
        {
            WriteColored(ConsoleColor.Green, "RESULT: PASSED — the SAS key authenticated and the event hub accepted the message.");
        }
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }
}
