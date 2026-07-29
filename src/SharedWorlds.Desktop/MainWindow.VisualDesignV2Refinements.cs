using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace SharedWorlds.Desktop;

public partial class MainWindow
{
    private const string SettingsCardV2Tag = "safe-world-v2-settings-card";
    private const string DangerCardV2Tag = "safe-world-v2-danger-card";

    private bool _visualDesignV2RefinementsInitialized;
    private Border? _worldEmptyStateCardV2;
    private TextBlock? _worldEmptyStateDescriptionV2;
    private bool _normalizingWorldEmptyStateV2;

    internal void InitializeVisualDesignV2Refinements()
    {
        if (_visualDesignV2RefinementsInitialized)
        {
            return;
        }

        _visualDesignV2RefinementsInitialized = true;
        ReplaceHeaderBrandMarkV2();
        ComposeWorldEmptyStateV2();
        RecomposeSettingsSurfaceV2();
        RehomeDangerZoneV2();
    }

    private void ReplaceHeaderBrandMarkV2()
    {
        var topBar = FindSafeWorldTopBar();
        var brandRow = topBar?.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Orientation == Orientation.Horizontal);
        var shell = brandRow?.Children.OfType<Border>().FirstOrDefault();
        if (shell is null)
        {
            return;
        }

        shell.Child = CreateWorldMarkV2(27);
    }

    private static Grid CreateWorldMarkV2(double size)
    {
        var mark = new Grid
        {
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var stroke = Brushes.White;
        var outerSize = size * 0.78;

        mark.Children.Add(new Ellipse
        {
            Width = outerSize,
            Height = outerSize,
            Stroke = stroke,
            StrokeThickness = 1.9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        mark.Children.Add(new Ellipse
        {
            Width = outerSize * 0.42,
            Height = outerSize,
            Stroke = stroke,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        mark.Children.Add(new Ellipse
        {
            Width = outerSize,
            Height = outerSize * 0.42,
            Stroke = stroke,
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        return mark;
    }

    private void ComposeWorldEmptyStateV2()
    {
        if (_worldEmptyStateCardV2 is not null || EmptyStateText.Parent is not StackPanel owner)
        {
            return;
        }

        var index = owner.Children.IndexOf(EmptyStateText);
        owner.Children.Remove(EmptyStateText);

        EmptyStateText.FontSize = 22;
        EmptyStateText.FontWeight = FontWeights.SemiBold;
        EmptyStateText.TextAlignment = TextAlignment.Center;
        EmptyStateText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        var description = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            MaxWidth = 430,
            FontSize = 13,
            LineHeight = 20,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

        var markShell = new Border
        {
            Width = 54,
            Height = 54,
            Margin = new Thickness(0, 0, 0, 18),
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        markShell.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var mark = CreateWorldMarkV2(31);
        foreach (var ellipse in mark.Children.OfType<Ellipse>())
        {
            ellipse.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        }
        markShell.Child = mark;

        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(markShell);
        content.Children.Add(EmptyStateText);
        content.Children.Add(description);

        var card = new Border
        {
            MaxWidth = 560,
            Margin = new Thickness(0, 72, 0, 0),
            Padding = new Thickness(34, 32, 34, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = content
        };
        if (TryFindResource("CardBorderStyle") is Style cardStyle)
        {
            card.Style = cardStyle;
        }
        card.SetBinding(
            UIElement.VisibilityProperty,
            new Binding(nameof(UIElement.Visibility)) { Source = EmptyStateText });

        owner.Children.Insert(index, card);
        _worldEmptyStateCardV2 = card;
        _worldEmptyStateDescriptionV2 = description;

        var textDescriptor = DependencyPropertyDescriptor.FromProperty(
            TextBlock.TextProperty,
            typeof(TextBlock));
        textDescriptor?.AddValueChanged(EmptyStateText, (_, _) => UpdateWorldEmptyStateCopyV2());
        UpdateWorldEmptyStateCopyV2();
    }

    private void UpdateWorldEmptyStateCopyV2()
    {
        if (_worldEmptyStateDescriptionV2 is null || _normalizingWorldEmptyStateV2)
        {
            return;
        }

        var text = EmptyStateText.Text?.Trim() ?? string.Empty;
        var game = string.IsNullOrWhiteSpace(SelectedGameNameText.Text)
            ? "this game"
            : SelectedGameNameText.Text.Trim();

        string? normalizedTitle = null;
        string description;
        if (text.Contains("match this search", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTitle = "No matching Worlds";
            description = "Try a different name or clear the search to show every World.";
        }
        else if (text.Contains("Shared Worlds are temporarily unavailable", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTitle = "Shared Worlds are unavailable";
            description = $"Safe World could not load shared {game} Worlds right now. Local Worlds remain available on this PC.";
        }
        else if (text.StartsWith("No managed ", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("No Worlds yet", StringComparison.OrdinalIgnoreCase))
        {
            normalizedTitle = $"No {game} Worlds yet";
            description = $"Create a new {game} World or add one already saved on this PC.";
        }
        else
        {
            normalizedTitle = text.Equals("Select a World.", StringComparison.OrdinalIgnoreCase)
                ? "Choose a World"
                : null;
            description = "Select a World from the list to continue, host, share, or manage it.";
        }

        if (normalizedTitle is not null && !string.Equals(text, normalizedTitle, StringComparison.Ordinal))
        {
            _normalizingWorldEmptyStateV2 = true;
            try
            {
                EmptyStateText.Text = normalizedTitle;
            }
            finally
            {
                _normalizingWorldEmptyStateV2 = false;
            }
        }

        _worldEmptyStateDescriptionV2.Text = description;
    }

    private void RecomposeSettingsSurfaceV2()
    {
        if (_globalSettingsPanel?.Child is not ScrollViewer scroll ||
            scroll.Content is not StackPanel content ||
            content.Children.OfType<Border>().Any(border => Equals(border.Tag, SettingsCardV2Tag)))
        {
            return;
        }

        var expander = content.Children.OfType<Expander>().FirstOrDefault();
        if (expander?.Content is not UIElement expanderContent)
        {
            return;
        }

        var index = content.Children.IndexOf(expander);
        expander.Content = null;
        content.Children.Remove(expander);

        UIElement settingsControls = expanderContent;
        if (expanderContent is Border existingShell && existingShell.Child is UIElement existingContent)
        {
            existingShell.Child = null;
            settingsControls = existingContent;
        }
        if (settingsControls is FrameworkElement settingsElement)
        {
            settingsElement.Margin = new Thickness(0, 18, 0, 0);
        }

        var cardContent = new StackPanel();
        cardContent.Children.Add(new TextBlock
        {
            Text = DesktopText.HostingOnThisDevice,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold
        });
        var explanation = new TextBlock
        {
            Text = "Choose whether this PC may host shared Worlds when a game supports hosting.",
            Margin = new Thickness(0, 6, 0, 0),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        explanation.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        cardContent.Children.Add(explanation);
        cardContent.Children.Add(settingsControls);

        var card = new Border
        {
            Tag = SettingsCardV2Tag,
            Padding = new Thickness(24, 22, 24, 22),
            Child = cardContent
        };
        if (TryFindResource("CardBorderStyle") is Style cardStyle)
        {
            card.Style = cardStyle;
        }
        content.Children.Insert(index, card);
    }

    private void RehomeDangerZoneV2()
    {
        if (_deleteWorldPanel is null ||
            WorldDetailsPanel.Children.OfType<Border>().Any(border => Equals(border.Tag, DangerCardV2Tag)))
        {
            return;
        }

        DetachSafeWorldSidebarElement(_deleteWorldPanel);
        var separator = _deleteWorldPanel.Children
            .OfType<Border>()
            .FirstOrDefault(border => border.Height == 1);
        if (separator is not null)
        {
            _deleteWorldPanel.Children.Remove(separator);
        }

        var heading = _deleteWorldPanel.Children.OfType<TextBlock>().FirstOrDefault();
        heading?.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        _deleteWorldPanel.Margin = new Thickness(0);

        var card = new Border
        {
            Tag = DangerCardV2Tag,
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(20),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = _deleteWorldPanel
        };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "DangerBrush");
        card.SetBinding(
            UIElement.VisibilityProperty,
            new Binding(nameof(UIElement.Visibility)) { Source = _deleteWorldPanel });

        var technicalDetails = WorldDetailsPanel.Children.OfType<Expander>().FirstOrDefault();
        var insertIndex = technicalDetails is null
            ? WorldDetailsPanel.Children.Count
            : WorldDetailsPanel.Children.IndexOf(technicalDetails);
        WorldDetailsPanel.Children.Insert(insertIndex, card);
    }
}
