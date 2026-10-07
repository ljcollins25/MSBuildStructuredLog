using System;
using System.Collections.Generic;

namespace Microsoft.Build.Logging.StructuredLogger;

public class SourceText
{
    public SourceText(string text)
    {
        this.text = text;
    }

    /// <summary>The text is produced by <paramref name="load"/> the first time it is needed.</summary>
    public SourceText(Func<string> load)
    {
        this.load = load;
    }

    private string text;
    private Func<string> load;

    public string Text
    {
        get
        {
            if (text == null && load != null)
            {
                lock (this)
                {
                    if (text == null)
                    {
                        text = load() ?? "";
                        load = null;
                    }
                }
            }

            return text;
        }
    }

    private IReadOnlyList<Span> lines;
    public IReadOnlyList<Span> Lines
    {
        get
        {
            if (lines == null)
            {
                lines = Text.GetLineSpans();
            }

            return lines;
        }
    }

    // Opaque slot for a lazily-computed parse tree (e.g. an XML root
    // produced by SourceTextXml.TryGetXml). Held as object so the core
    // SourceText type does not need to depend on a particular parser.
    public object SyntaxTree { get; set; }

    public IReadOnlyList<int> Find(string searchText)
    {
        var result = new List<int>();
        var searchTextLength = searchText.Length;
        for (int i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if (line.Length >= searchTextLength)
            {
                int foundOffset = Text.IndexOf(searchText, line.Start, line.Length, StringComparison.OrdinalIgnoreCase);
                if (foundOffset >= line.Start && foundOffset < line.End - searchTextLength)
                {
                    result.Add(i);
                }
            }
        }

        return result;
    }

    public string GetLineText(int lineNumber)
    {
        var line = Lines[lineNumber];
        if (line.Length == 0)
        {
            return "";
        }

        var end = line.End - 1;
        while (end >= line.Start && Text[end].IsLineBreakChar())
        {
            end--;
        }

        if (end < line.Start)
        {
            return "";
        }

        return Text.Substring(line.Start, end - line.Start + 1);
    }

    public string GetText(int start, int length)
    {
        return Text.Substring(start, length);
    }

    public int GetLineNumberFromPosition(int startPosition)
    {
        for (int i = 0; i < Lines.Count; i++)
        {
            if (startPosition >= Lines[i].Start && startPosition < Lines[i].End)
            {
                return i;
            }
        }

        return 0;
    }

    public (int Line, int Column) GetLineAndColumn1Based(int position)
    {
        var lineNumber = GetLineNumberFromPosition(position);
        var column = position - Lines[lineNumber].Start + 1;
        var line = lineNumber + 1;
        return (line, column);
    }

    public override string ToString()
    {
        return Text;
    }

    public int GetPosition(int lineNumber1Based, int columnNumber1Based)
    {
        if (lineNumber1Based >= 1 && lineNumber1Based <= Lines.Count)
        {
            var line = Lines[lineNumber1Based - 1];
            if (columnNumber1Based <= line.Length)
            {
                return line.Start + columnNumber1Based - 1;
            }
        }

        return -1;
    }
}
