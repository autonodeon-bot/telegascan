using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace TelegaScan.Controls;

public class HighlightTextBlock : TextBlock
{
    public static readonly DependencyProperty SourceTextProperty =
        DependencyProperty.Register(nameof(SourceText), typeof(string), typeof(HighlightTextBlock),
            new PropertyMetadata("", OnTextPropsChanged));

    public static readonly DependencyProperty HighlightQueryProperty =
        DependencyProperty.Register(nameof(HighlightQuery), typeof(string), typeof(HighlightTextBlock),
            new PropertyMetadata("", OnTextPropsChanged));

    public string SourceText
    {
        get => (string)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public string HighlightQuery
    {
        get => (string)GetValue(HighlightQueryProperty);
        set => SetValue(HighlightQueryProperty, value);
    }

    private static void OnTextPropsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HighlightTextBlock)d).RebuildInlines();

    private void RebuildInlines()
    {
        Inlines.Clear();
        var text = SourceText ?? "";
        var q = (HighlightQuery ?? "").Trim();
        if (string.IsNullOrEmpty(text))
        {
            Inlines.Add(new Run(""));
            return;
        }
        if (string.IsNullOrEmpty(q))
        {
            Inlines.Add(new Run(text));
            return;
        }

        var idx = 0;
        while (idx < text.Length)
        {
            var hit = text.IndexOf(q, idx, StringComparison.CurrentCultureIgnoreCase);
            if (hit < 0)
            {
                Inlines.Add(new Run(text[idx..]));
                break;
            }
            if (hit > idx)
                Inlines.Add(new Run(text[idx..hit]));
            Inlines.Add(new Run(text.Substring(hit, q.Length))
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3d, 0x6a, 0x9e)),
                Foreground = System.Windows.Media.Brushes.White
            });
            idx = hit + q.Length;
        }
    }
}
