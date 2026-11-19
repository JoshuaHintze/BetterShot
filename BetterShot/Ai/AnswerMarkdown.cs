using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace BetterShot.Ai;

/// <summary>
/// Light Markdown for readability while streaming: fenced code and tables in monospace, headings, bullets,
/// quotes, inline code, and bold. Anything else stays plain text, so partial Markdown never breaks rendering.
/// </summary>
internal static partial class AnswerMarkdown
{
    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New");
    private static readonly Brush CodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x14)));
    private static readonly Brush InlineCodeBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x33)));
    private static readonly Brush CodeForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE2)));
    private static readonly Brush QuoteForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xB4, 0xB6, 0xBC)));

    internal static List<Block> ToBlocks(string markdown)
    {
        var blocks = new List<Block>();
        var paragraphLines = new List<string>();
        var codeLines = new List<string>();
        var tableLines = new List<string>();
        bool inFence = false;

        void FlushParagraph()
        {
            if (paragraphLines.Count > 0) blocks.Add(TextParagraph(paragraphLines));
            paragraphLines.Clear();
        }
        void FlushTable()
        {
            if (tableLines.Count > 0) blocks.Add(CodeParagraph(tableLines, InlineCodeBackground));
            tableLines.Clear();
        }

        foreach (string rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inFence) { blocks.Add(CodeParagraph(codeLines, CodeBackground)); codeLines.Clear(); }
                else { FlushParagraph(); FlushTable(); }
                inFence = !inFence;
                continue;
            }
            if (inFence) { codeLines.Add(line); continue; }

            if (trimmed.StartsWith('|')) { FlushParagraph(); tableLines.Add(trimmed); continue; }
            FlushTable();

            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (HorizontalRule().IsMatch(trimmed)) { FlushParagraph(); continue; }

            Match heading = Heading().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                var paragraph = new Paragraph { FontWeight = FontWeights.SemiBold, FontSize = heading.Groups[1].Length <= 2 ? 17 : 15, Margin = new Thickness(0, 4, 0, 6) };
                AddInlines(paragraph.Inlines, heading.Groups[2].Value);
                blocks.Add(paragraph);
                continue;
            }

            Match bullet = Bullet().Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(ListParagraph("•", bullet.Groups[1].Value.Length, bullet.Groups[2].Value));
                continue;
            }

            Match numbered = Numbered().Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(ListParagraph(numbered.Groups[2].Value, numbered.Groups[1].Value.Length, numbered.Groups[3].Value));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var quote = new Paragraph { Foreground = QuoteForeground, FontStyle = FontStyles.Italic, Margin = new Thickness(12, 0, 0, 8) };
                AddInlines(quote.Inlines, trimmed.TrimStart('>').TrimStart());
                blocks.Add(quote);
                continue;
            }

            paragraphLines.Add(line);
        }

        FlushParagraph();
        FlushTable();
        if (inFence && codeLines.Count > 0) blocks.Add(CodeParagraph(codeLines, CodeBackground));
        return blocks;
    }

    private static Paragraph TextParagraph(List<string> lines)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) paragraph.Inlines.Add(new LineBreak());
            AddInlines(paragraph.Inlines, lines[index]);
        }
        return paragraph;
    }

    private static Paragraph ListParagraph(string marker, int indent, string text)
    {
        double left = 6 + Math.Min(indent, 12) * 6;
        var paragraph = new Paragraph { Margin = new Thickness(left + 16, 0, 0, 4), TextIndent = -16 };
        paragraph.Inlines.Add(new Run(marker + "  "));
        AddInlines(paragraph.Inlines, text);
        return paragraph;
    }

    private static Paragraph CodeParagraph(List<string> lines, Brush background)
    {
        var paragraph = new Paragraph
        {
            FontFamily = Monospace,
            FontSize = 13,
            Background = background,
            Foreground = CodeForeground,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 10)
        };
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[index]));
        }
        return paragraph;
    }

    /// <summary>Handles `inline code` and **bold**; unmatched markers are shown literally.</summary>
    private static void AddInlines(InlineCollection inlines, string text)
    {
        var plain = new StringBuilder();
        void FlushPlain()
        {
            if (plain.Length > 0) inlines.Add(new Run(plain.ToString()));
            plain.Clear();
        }

        int position = 0;
        while (position < text.Length)
        {
            if (text[position] == '`')
            {
                int end = text.IndexOf('`', position + 1);
                if (end > position + 1)
                {
                    FlushPlain();
                    inlines.Add(new Run(text[(position + 1)..end]) { FontFamily = Monospace, Background = InlineCodeBackground, Foreground = CodeForeground });
                    position = end + 1;
                    continue;
                }
            }
            if (text[position] == '*' && position + 1 < text.Length && text[position + 1] == '*')
            {
                int end = text.IndexOf("**", position + 2, StringComparison.Ordinal);
                if (end > position + 2)
                {
                    FlushPlain();
                    inlines.Add(new Bold(new Run(text[(position + 2)..end])));
                    position = end + 2;
                    continue;
                }
            }
            plain.Append(text[position]);
            position++;
        }
        FlushPlain();
    }

    private static Brush Freeze(SolidColorBrush brush) { brush.Freeze(); return brush; }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();
    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();
    [GeneratedRegex(@"^(\s*)(\d{1,3}[.)])\s+(.*)$")]
    private static partial Regex Numbered();
    [GeneratedRegex(@"^([-*_])(\s*\1){2,}$")]
    private static partial Regex HorizontalRule();
}
