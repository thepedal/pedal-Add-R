using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BuzzGUI.Interfaces;

namespace PedalAddR
{
    // ─────────────────────────────────────────────────────────────────────────
    // About banner — appears at the top of the parameter window.
    //
    // Route A from the design discussion: not the machine-view right-click,
    // but the next-best documented surface. Accessed via right-click →
    // Parameters on the machine, then the banner sits above the sliders.
    //
    // ReBuzz discovers IMachineGUIFactory by exported-type assembly scan
    // (Core §26.1). PreferWindowedGUI = false → embedded in param window.
    // The single Machine setter is called by ParameterWindowVM after
    // CreateGUI(); we don't need the cast for a static banner, but stash
    // _iMachine for future use.
    //
    // Static content only — no DispatcherTimer, nothing to poll. Update
    // Version on PedalAddRMachine, rebuild, done.
    // ─────────────────────────────────────────────────────────────────────────

    [MachineGUIFactoryDecl(PreferWindowedGUI = false)]
    public class PedalAddRGuiFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new PedalAddRGui();
    }

    public class PedalAddRGui : UserControl, IMachineGUI
    {
        IMachine _iMachine;

        public IMachine Machine
        {
            get => _iMachine;
            set => _iMachine = value;
        }

        public PedalAddRGui()
        {
            // ── Colours — explicit dark theme; not relying on UseThemeStyles
            //    because we want a consistent look across ReBuzz themes. ─────
            var bgBrush      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x28));
            var borderBrush  = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x48));
            var titleBrush   = Brushes.WhiteSmoke;
            var versionBrush = new SolidColorBrush(Color.FromRgb(0x88, 0xCC, 0xFF));
            var authorBrush  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            var taglineBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            var featureBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x99, 0xAA));

            // ── Top row: name + version on the left, author on the right ────
            var nameText = new TextBlock
            {
                Text       = "Pedal Add-R",
                FontSize   = 14,
                FontWeight = FontWeights.Bold,
                Foreground = titleBrush,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var versionText = new TextBlock
            {
                Text       = "v" + PedalAddRMachine.Version,
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = versionBrush,
                Margin     = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var authorText = new TextBlock
            {
                Text       = "by thepedal",
                FontSize   = 10,
                Foreground = authorBrush,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            var topRow = new Grid();
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(nameText,    0);
            Grid.SetColumn(versionText, 1);
            Grid.SetColumn(authorText,  2);
            topRow.Children.Add(nameText);
            topRow.Children.Add(versionText);
            topRow.Children.Add(authorText);

            // ── Tagline (the elevator-pitch identity) ───────────────────────
            var tagline = new TextBlock
            {
                Text       = "8-voice polyphonic time-domain additive synth",
                FontSize   = 10,
                FontStyle  = FontStyles.Italic,
                Foreground = taglineBrush,
                Margin     = new Thickness(0, 3, 0, 0),
            };

            // ── Features — what the engine has, at a glance ─────────────────
            var features = new TextBlock
            {
                Text       = "Inharmonic morph · per-voice LFO · formant · click-protect retrigger · 19 presets",
                FontSize   = 9,
                Foreground = featureBrush,
                Margin     = new Thickness(0, 1, 0, 0),
            };

            // ── Layout ──────────────────────────────────────────────────────
            var stack = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };
            stack.Children.Add(topRow);
            stack.Children.Add(tagline);
            stack.Children.Add(features);

            var border = new Border
            {
                Background      = bgBrush,
                BorderBrush     = borderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child           = stack,
            };

            Content = border;
        }
    }
}
