using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SacredModManager.Core.Mods;

namespace SacredModManager.Features.Common;

/// <summary>
/// <c>common:MarkdownText.Source="{Binding ...}"</c> on a TextBlock / SelectableTextBlock shows light markdown (headings, bullets, **bold**, *italic*, `code`) as styled runs.
/// It only builds text runs from <see cref="MarkdownLite"/>: no links, no images, nothing is fetched or run.
/// </summary>
public static class MarkdownText
{
    public static readonly AttachedProperty<string?> SourceProperty = AvaloniaProperty.RegisterAttached<TextBlock, string?>("Source", typeof(MarkdownText));

    static MarkdownText() => SourceProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string));

    public static string? GetSource(TextBlock block) => block.GetValue(SourceProperty);

    public static void SetSource(TextBlock block, string? value) => block.SetValue(SourceProperty, value);

    private static void Render(TextBlock block, string? markdown)
    {
        block.Text = null;
        InlineCollection inlines = block.Inlines ??= [];
        inlines.Clear();
        IReadOnlyList<MdBlock> blocks = MarkdownLite.ParseBlocks(markdown);
        bool first = true;
        foreach (MdBlock b in blocks)
        {
            if (!first)
            {
                inlines.Add(new LineBreak());
            }

            first = false;
            if (b.Kind == MdBlockKind.Bullet)
            {
                inlines.Add(new Run("•  "));
            }

            foreach (MdSpan span in b.Spans)
            {
                var run = new Run(span.Text);
                if (span.Bold || b.Kind == MdBlockKind.Heading)
                {
                    run.FontWeight = FontWeight.SemiBold;
                }

                if (span.Italic)
                {
                    run.FontStyle = FontStyle.Italic;
                }

                if (span.Code)
                {
                    run.FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace");
                    run.Background = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
                }

                if (b.Kind == MdBlockKind.Heading)
                {
                    run.FontSize = block.FontSize * (b.Level <= 1 ? 1.25 : b.Level == 2 ? 1.1 : 1.0);
                }

                inlines.Add(run);
            }
        }
    }
}
