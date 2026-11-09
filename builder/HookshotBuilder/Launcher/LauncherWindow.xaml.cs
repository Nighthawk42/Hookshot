using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace HookshotBuilder.Launcher;

/// The game folder's launcher (hookshot.exe there starts the builder with --launch <folder>, builder/setup/setup.c): Play, Update
/// when a newer release is out, and the in-game menu's settings and controls, written to the folder's hookshot_options.ini
/// before the game starts.
public partial class LauncherWindow : Window
{
    readonly string folder;
    readonly GameOptions opts;
    readonly List<string> gpus;
    readonly List<Action> refreshers = new();
    bool refreshing;
    string? latestTag, updateTarget;
    bool running;
    readonly DispatcherTimer runningTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public LauncherWindow(string folder)
    {
        this.folder = folder;
        InitializeComponent();
        Eyebrow.Text = Spaced("The Legend of Zelda");
        gpus = GameFolder.Gpus(folder);
        SdlPads.Start(folder);   // (early: SDL takes a moment to find the controllers)
        string? loadProblem = null;
        try { opts = GameOptions.Load(folder); }
        catch (Exception e)
        {
            opts = GameOptions.Defaults(folder);
            loadProblem = "hookshot_options.ini could not be read (" + e.Message + "). The defaults are shown; changing one writes a new file.";
        }

        BuildRows(GraphicsRows, "Graphics");
        BuildRows(GameplayRows, "Gameplay");
        BuildRows(ControlsRows, "Controls");
        BuildBindings();
        BuildSystem();
        BuildSummary();
        Refresh();

        var game = MainWindow.GameVersion(folder) ?? MainWindow.ReadVersion(folder);
        RailVersion.Text = game != null ? $"Hookshot {game}" : "Hookshot";
        if (!File.Exists(MainWindow.GameExe(folder)))
        {
            HomePlay.IsEnabled = FootPlay.IsEnabled = false;
            HomeStatus.Text = "The game was not found in this folder (bin\\hookshot.exe). Build it again with Hookshot Builder.";
        }
        if (loadProblem != null) ShowSaveState(loadProblem, problem: true);

        PreviewKeyDown += ListenKey;
        PreviewMouseDown += ListenMouse;
        Deactivated += (_, _) => CancelListen();
        runningTimer.Tick += (_, _) => CheckRunning();
        Loaded += (_, _) =>
        {
            Fade(HomePage);
            StartFireflies();
            CheckRunning();
            runningTimer.Start();
            CheckUpdates(game);
            HomePlay.Focus();
        };
        Closed += (_, _) => { runningTimer.Stop(); padTimer.Stop(); };
    }

    /// Small capitals with air between the letters (WPF has no letter spacing)
    static string Spaced(string s) => string.Join(" ", s.ToUpperInvariant().Select(c => c == ' ' ? " " : c.ToString()));

    // ================= updates (as the builder's launcher page did) =================

    async void CheckUpdates(string? game)
    {
        latestTag = Environment.GetEnvironmentVariable("HOOKSHOT_LATEST_TAG") is { Length: > 0 } fake
            ? (fake == "none" ? null : fake)
            : await Updater.LatestTag();
        updateTarget = latestTag ?? Tools.Find()?.Version;   // (GitHub's newest, else this builder's own)
        var line = game != null ? $"Hookshot {game}" : "Hookshot";
        if (updateTarget == null)
        {
            RailStatus.Text = "Could not check for updates";
            HomeStatus.Text = line + " · GitHub did not answer, so updates could not be checked";
        }
        else if (game != null && game != updateTarget && MainWindow.GameVersion(folder) != null)
        {
            RailStatus.Text = $"{updateTarget} is available";
            StatusDot.Fill = (Brush)FindResource("ZEmerald");
            HomeStatus.Text = $"{line} · {updateTarget} is available. Update builds it again, compiling only what changed. Saves and settings stay.";
            UpdateText.Text = $"Update to {updateTarget}";
            UpdateButton.Visibility = Visibility.Visible;
        }
        else if (game != null && game != updateTarget)
        {
            RailStatus.Text = $"{updateTarget} is available";
            HomeStatus.Text = $"{line} · {updateTarget} is out: build this folder again with Hookshot Builder to get it";
        }
        else
        {
            RailStatus.Text = latestTag == null ? "Up to date with this builder" : "Up to date";
            StatusDot.Fill = (Brush)FindResource("ZGold");
            HomeStatus.Text = line + " · " + (latestTag == null ? "up to date with this builder (GitHub did not answer)" : "up to date");
        }
        if (!File.Exists(MainWindow.GameExe(folder)))
            HomeStatus.Text = "The game was not found in this folder (bin\\hookshot.exe). Build it again with Hookshot Builder.";
    }

    /// The builder's window does the update (its progress, its problems), as it did from the old launcher page
    void UpdateClick(object sender, RoutedEventArgs e)
    {
        var w = new MainWindow { LauncherUpdate = (folder, updateTarget) };
        Application.Current.MainWindow = w;
        w.Show();
        Close();
    }

    // ================= Play =================

    void PlayClick(object sender, RoutedEventArgs e)
    {
        CancelListen();
        try
        {
            MainWindow.StartGame(folder);
            Application.Current.Shutdown();
        }
        catch (Exception x)
        {
            ShowSaveState("The game could not start: " + x.Message, problem: true);
        }
    }

    void CheckRunning()
    {
        var now = GameFolder.Running(folder);
        if (now == running) return;
        running = now;
        var exists = File.Exists(MainWindow.GameExe(folder));
        HomePlay.IsEnabled = FootPlay.IsEnabled = exists && !running;
        if (running)
            ShowSaveState("Hookshot is running. Changes made here apply when it starts again, and its in-game menu may write over them.", problem: true);
        else
            ShowSaveState("Settings are saved to hookshot_options.ini as you change them.", problem: false);
    }

    // ================= pages =================

    void NavChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || GraphicsPage == null) return;
        CancelListen();
        var (page, title) = sender == NavGraphics ? ((FrameworkElement)GraphicsPage, "Graphics")
                          : sender == NavGameplay ? (GameplayPage, "Gameplay")
                          : sender == NavControls ? (ControlsPage, "Controls")
                          : sender == NavSystem ? (SystemPage, "System")
                          : ((FrameworkElement)HomePage, "");
        foreach (var p in new FrameworkElement[] { HomePage, GraphicsPage, GameplayPage, ControlsPage, SystemPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        var home = page == HomePage;
        Header.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        FootPlay.Visibility = home ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = title;
        // (the forest stays in view on Adventure; behind rows of small text it is shaded down)
        var shade = home ? 0.35 : 0.85;
        if (SystemParameters.ClientAreaAnimation) PageShade.BeginAnimation(OpacityProperty, new DoubleAnimation(shade, TimeSpan.FromMilliseconds(350)));
        else PageShade.Opacity = shade;
        if (page is ScrollViewer sv) sv.ScrollToTop();
        Fade(page);
    }

    static void Fade(FrameworkElement page)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        page.Opacity = 0;
        page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        var lift = new TranslateTransform(0, 8);
        page.RenderTransform = lift;
        lift.BeginAnimation(TranslateTransform.YProperty,
                           new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // ================= the settings: one row each, as the in-game menu has them =================

    void BuildRows(Panel into, string tab)
    {
        foreach (var r in GameOptions.Rows.Where(r => r.Tab == tab))
            into.Children.Add(RowView(r.Name, r.Help, Editor(r)));
    }

    FrameworkElement RowView(string name, string help, FrameworkElement editor)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 28, 0) };
        text.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (help.Length > 0) text.Children.Add(new TextBlock { Text = help, Style = (Style)FindResource("ZFaint"), Margin = new Thickness(0, 4, 0, 0), MaxWidth = 470, HorizontalAlignment = HorizontalAlignment.Left });
        g.Children.Add(text);
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        g.Children.Add(editor);
        AutomationName(editor, name);
        return new Border { Child = g, Padding = new Thickness(0, 16, 0, 16), BorderBrush = (Brush)FindResource("ZGoldFaint"), BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    static void AutomationName(FrameworkElement e, string name)
    {
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(e)))
            System.Windows.Automation.AutomationProperties.SetName(e, name);
    }

    FrameworkElement Editor(OptionRow r)
    {
        var choices = r.Choices;
        if (r.Key == "gpu_device") return GpuEditor(r);
        if (r.Key == "sharpness") return SliderEditor(r, v => v + "%");
        if (r.Key == "mouse_sensitivity") return SliderEditor(r, v => (v / 100.0).ToString("0.00"));
        if (choices is ["Off", "On"]) return SwitchEditor(r);
        if (r.Key == "frame_generation" || choices!.Length > 5) return ComboEditor(r);
        return SegmentEditor(r);
    }

    void Changed(string key, int value)
    {
        if (refreshing || opts[key] == value && key != "quality_preset" && key != "layout_preset") return;
        opts.Set(key, value);
        Save();
    }

    void Save()
    {
        try
        {
            opts.Save();
            ShowSaveState(running ? "Saved. Hookshot is running: this applies when it starts again." : "Saved to hookshot_options.ini", problem: running, flash: true);
        }
        catch (Exception e)
        {
            ShowSaveState("Could not save hookshot_options.ini: " + e.Message, problem: true);
        }
        Refresh();
    }

    void ShowSaveState(string text, bool problem, bool flash = false)
    {
        SaveState.Text = text;
        SaveState.Foreground = (Brush)FindResource(problem ? "ZRuby" : "ZInkSoft");
        SaveMark.Fill = (Brush)FindResource(problem ? "ZRuby" : flash ? "ZGoldBright" : "ZGoldDim");
        if (flash && SystemParameters.ClientAreaAnimation)
            SaveMark.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(500)));
    }

    /// Every editor shown again from the options (a preset sets several rows at once)
    void Refresh()
    {
        refreshing = true;
        try { foreach (var r in refreshers) r(); }
        finally { refreshing = false; }
    }

    FrameworkElement SwitchEditor(OptionRow r)
    {
        var box = new CheckBox { Style = (Style)FindResource("ZSwitch") };
        box.Click += (_, _) => Changed(r.Key, box.IsChecked == true ? 1 : 0);
        refreshers.Add(() => box.IsChecked = opts[r.Key] != 0);
        return box;
    }

    FrameworkElement SegmentEditor(OptionRow r)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "seg_" + r.Key;
        var buttons = new List<RadioButton>();
        for (var i = 0; i < r.Choices!.Length; i++)
        {
            var v = i;
            var b = new RadioButton { Content = r.Choices[i], GroupName = group, Style = (Style)FindResource("ZSegment"), Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0) };
            // (Custom is what the layout becomes when a button is changed: shown, not chosen)
            if (r.Key == "layout_preset" && i == 3) b.IsEnabled = false;
            b.Checked += (_, _) => Changed(r.Key, v);
            buttons.Add(b);
            panel.Children.Add(b);
        }
        refreshers.Add(() =>
        {
            for (var i = 0; i < buttons.Count; i++) buttons[i].IsChecked = opts[r.Key] == i;
            if (r.Key == "layout_preset") buttons[3].Visibility = opts[r.Key] == 3 ? Visibility.Visible : Visibility.Collapsed;
        });
        return new Border { Child = panel, Background = (Brush)FindResource("ZWell"), BorderBrush = (Brush)FindResource("ZGoldLine"), BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(4), Padding = new Thickness(3) };
    }

    FrameworkElement ComboEditor(OptionRow r)
    {
        var values = Enumerable.Range(0, r.Choices!.Length).Where(i => r.Choices[i].Length > 0).ToList();   // (frame generation has no 1x)
        var box = new ComboBox { Style = (Style)FindResource("ZChoice"), ItemsSource = values.Select(i => r.Choices[i]).ToList() };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Changed(r.Key, values[box.SelectedIndex]); };
        refreshers.Add(() => box.SelectedIndex = values.IndexOf(opts[r.Key]));
        return box;
    }

    FrameworkElement GpuEditor(OptionRow r)
    {
        var names = new List<string> { "Auto (the best dedicated card)" };
        for (var i = 0; i < gpus.Count; i++) names.Add($"{i}: {gpus[i]}");
        // (a card chosen before that the last log does not name: kept, by number)
        for (var i = gpus.Count; i < opts[r.Key]; i++) names.Add($"{i}: Card {i}");
        var box = new ComboBox { Style = (Style)FindResource("ZChoice"), ItemsSource = names, MinWidth = 280, MaxWidth = 320 };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Changed(r.Key, box.SelectedIndex); };
        refreshers.Add(() => box.SelectedIndex = Math.Min(opts[r.Key], names.Count - 1));
        if (gpus.Count > 0) return box;
        var stack = new StackPanel();
        stack.Children.Add(box);
        stack.Children.Add(new TextBlock { Text = "Your cards are listed after the game's first start.", Style = (Style)FindResource("ZFaint"), Margin = new Thickness(2, 6, 0, 0) });
        return stack;
    }

    FrameworkElement SliderEditor(OptionRow r, Func<int, string> label)
    {
        var slider = new Slider { Style = (Style)FindResource("ZSlider"), Minimum = r.Lo, Maximum = r.Hi, TickFrequency = r.Step, SmallChange = r.Step, LargeChange = r.Step * 5 };
        var value = new TextBlock { Width = 52, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("ZGold"),
                                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = label((int)Math.Round(slider.Value));
            Changed(r.Key, (int)Math.Round(slider.Value));
        };
        refreshers.Add(() => { slider.Value = opts[r.Key]; value.Text = label(opts[r.Key]); });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(value);
        panel.Children.Add(slider);
        return panel;
    }

    // ================= Adventure: what is set, at a glance =================

    void BuildSummary()
    {
        Summary.Children.Add(SummaryTile("GRAPHICS", NavGraphics, () =>
        {
            var preset = GameOptions.Row("quality_preset").Choices![opts["quality_preset"]];
            var res = GameOptions.Row("resolution").Choices![opts["resolution"]];
            var fg = opts["frame_generation"] == 0 ? "no frame generation" : GameOptions.Row("frame_generation").Choices![opts["frame_generation"]] + " frames";
            return $"{(opts["quality_preset"] == 0 ? "Custom" : preset)} · {res} · {fg}";
        }));
        Summary.Children.Add(SummaryTile("CONTROLS", NavControls, () =>
        {
            var layout = GameOptions.Row("layout_preset").Choices![opts["layout_preset"]];
            var cam = opts["mouse_camera"] == 1 ? "native mouse camera" : "mouse as the stick";
            return $"{layout} layout · {cam}";
        }));
        Summary.Children.Add(SummaryTile("LANGUAGE", NavSystem, () =>
        {
            var (code, all) = GameFolder.Language(folder);
            return all.FirstOrDefault(l => l.Code == code)?.Name ?? code ?? "Not set";
        }));
    }

    FrameworkElement SummaryTile(string label, RadioButton page, Func<string> text)
    {
        var value = new TextBlock { FontSize = 13, Foreground = (Brush)FindResource("ZInk"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = Spaced(label), Style = (Style)FindResource("ZEyebrow"), FontSize = 11 });
        stack.Children.Add(value);
        var border = new Border { Style = (Style)FindResource("ZCard"), Padding = new Thickness(16, 12, 16, 14), Child = stack };
        var b = new Button { Content = border, Margin = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand, FocusVisualStyle = null };
        b.Template = TileTemplate();
        b.Click += (_, _) => page.IsChecked = true;
        AutomationName(b, label.ToLowerInvariant() + " settings");
        refreshers.Add(() => value.Text = text());
        border.MouseEnter += (_, _) => border.BorderBrush = (Brush)FindResource("ZGold");
        border.MouseLeave += (_, _) => border.BorderBrush = (Brush)FindResource("ZGoldLine");
        b.GotKeyboardFocus += (_, _) => border.BorderBrush = (Brush)FindResource("ZGoldBright");
        b.LostKeyboardFocus += (_, _) => border.BorderBrush = (Brush)FindResource("ZGoldLine");
        return b;
    }

    static ControlTemplate TileTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        t.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
        return t;
    }

    // ================= Controls: what presses each of the Switch's buttons =================

    // (the in-game Controls page's order: the face buttons, the shoulders, Plus and Minus, the sticks, the D-pad; then the keys-only
    // directions)
    static readonly (int Index, string Name, string Badge)[] BindOrder =
    {
        (0, "A", "A"), (1, "B", "B"), (2, "X", "X"), (3, "Y", "Y"), (6, "L", "L"), (7, "R", "R"), (8, "ZL", "ZL"), (9, "ZR", "ZR"),
        (10, "Plus", "+"), (11, "Minus", "−"), (4, "Left stick press", "LS"), (5, "Right stick press", "RS"),
        (13, "D-pad up", "↑"), (15, "D-pad down", "↓"), (12, "D-pad left", "←"), (14, "D-pad right", "→"),
        (16, "Move forward", "W"), (17, "Move back", "S"), (18, "Move left", "A"), (19, "Move right", "D"),
        (20, "Camera up", "↑"), (21, "Camera down", "↓"), (22, "Camera left", "←"), (23, "Camera right", "→"),
    };

    void BuildBindings()
    {
        foreach (var (index, name, badge) in BindOrder)
        {
            if (index == 0) BindRows.Children.Add(Group("Buttons"));
            if (index == 13) BindRows.Children.Add(Group("D-pad"));
            if (index == 16) BindRows.Children.Add(Group("Movement · keys and mouse only"));
            if (index == 20) BindRows.Children.Add(Group("Camera · keys and mouse only"));

            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var w in new[] { 0.0, 190, 12, 150, 12, 150 })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

            var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(Badge(badge, index >= 16));
            label.Children.Add(new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            g.Children.Add(label);

            if (index < GameOptions.SwitchButtons)
            {
                var pad = BindCell($"{name}, controller");
                var i = index;
                pad.Click += (_, _) => StartListen(pad, Listen.Pad, i, 0);
                pad.ContextMenu = PadMenu(i);
                refreshers.Add(() => pad.Content = PadText(opts.Pad[i]));
                Grid.SetColumn(pad, 1);
                g.Children.Add(pad);
            }
            else
            {
                var none = new TextBlock { Text = "—", Foreground = (Brush)FindResource("ZInkFaint"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0) };
                Grid.SetColumn(none, 1);
                g.Children.Add(none);
            }
            for (var slot = 0; slot < 2; slot++)
            {
                var key = BindCell($"{name}, {(slot == 0 ? "key" : "second key")}");
                int i = index, s = slot;
                key.Click += (_, _) => StartListen(key, Listen.Key, i, s);
                refreshers.Add(() => key.Content = KeyText(opts.Keys[i, s]));
                Grid.SetColumn(key, 3 + slot * 2);
                g.Children.Add(key);
            }
            BindRows.Children.Add(g);
        }
    }

    FrameworkElement Group(string text) =>
        new TextBlock { Text = text, Style = (Style)FindResource("ZFaint"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) };

    FrameworkElement Badge(string text, bool keyOnly)
    {
        var b = new Border
        {
            MinWidth = 30, Height = 24, CornerRadius = new CornerRadius(12), Padding = new Thickness(7, 0, 7, 0),
            BorderThickness = new Thickness(1), BorderBrush = (Brush)FindResource(keyOnly ? "ZGoldLine" : "ZGoldDim"),
            Background = (Brush)FindResource(keyOnly ? "ZWell" : "ZGoldFaint"),
        };
        b.Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("ZGold"),
                                  HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return b;
    }

    Button BindCell(string name)
    {
        var b = new Button { Style = (Style)FindResource("ZBind") };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        return b;
    }

    object PadText(int input)
    {
        if (input < 0) return new TextBlock { Text = "None", Foreground = (Brush)FindResource("ZInkFaint") };
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = Pad.Name(input) });
        p.Children.Add(new TextBlock { Text = "  " + Pad.Xbox[input], Foreground = (Brush)FindResource("ZGoldDim"), FontWeight = FontWeights.SemiBold });
        return p;
    }

    object KeyText(int vk) => vk == 0
        ? new TextBlock { Text = "None", Foreground = (Brush)FindResource("ZInkFaint") }
        : new TextBlock { Text = KeyCodes.Name(vk) };

    ContextMenu PadMenu(int button)
    {
        var menu = new ContextMenu { Style = (Style)FindResource("ZMenu") };
        for (var input = -1; input < GameOptions.PadInputs; input++)
        {
            var v = input;
            var item = new MenuItem { Header = v < 0 ? "None" : $"{Pad.Names[v]}  ({Pad.Xbox[v]})", Style = (Style)FindResource("ZMenuItem") };
            item.Click += (_, _) => { CancelListen(); opts.BindPad(button, v); Save(); };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem m in menu.Items) m.IsChecked = menu.Items.IndexOf(m) - 1 == opts.Pad[button];
        };
        return menu;
    }

    void ResetControlsClick(object sender, RoutedEventArgs e)
    {
        CancelListen();
        opts.ResetControls();
        Save();
    }

    // ---- listening for the new input ----
    enum Listen { None, Pad, Key }
    Listen listen;
    Button? listenCell;
    int listenButton, listenSlot;
    bool padArmed;
    DateTime listenEnd;
    readonly DispatcherTimer padTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    bool padTimerHooked;

    void StartListen(Button cell, Listen kind, int button, int slot)
    {
        var same = listen != Listen.None && cell == listenCell;
        CancelListen();
        if (same) return;   // (a second click on the box: stop listening)
        listen = kind;
        listenCell = cell;
        listenButton = button;
        listenSlot = slot;
        listenEnd = DateTime.UtcNow + TimeSpan.FromSeconds(kind == Listen.Pad ? 6 : 8);
        cell.Content = new TextBlock { Text = kind == Listen.Pad ? "Press a button…" : "Press a key…", Foreground = (Brush)FindResource("ZGoldBright"), FontWeight = FontWeights.SemiBold };
        cell.BorderBrush = (Brush)FindResource("ZGoldBright");
        if (SystemParameters.ClientAreaAnimation)
            cell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        if (kind == Listen.Pad && !Pad.Connected())
            BindHint.Text = "No controller found. Connect one (Xbox, PlayStation, Switch Pro and most others work), or right-click the box to pick the button from a list.";
        padArmed = false;
        if (!padTimerHooked) { padTimer.Tick += PadTick; padTimerHooked = true; }
        padTimer.Start();
    }

    void CancelListen()
    {
        if (listen == Listen.None) return;
        listen = Listen.None;
        padTimer.Stop();
        if (listenCell != null)
        {
            listenCell.BeginAnimation(OpacityProperty, null);
            listenCell.Opacity = 1;
            listenCell.ClearValue(Control.BorderBrushProperty);
        }
        listenCell = null;
        BindHint.Text = "Click a box, then press the button or key. Esc cancels, Delete clears it. Right-click a controller box to pick from a list.";
        Refresh();
    }

    void PadTick(object? sender, EventArgs e)
    {
        if (listen == Listen.None) { padTimer.Stop(); return; }
        if (DateTime.UtcNow > listenEnd) { CancelListen(); return; }
        if (listen != Listen.Pad) return;
        var held = Pad.Held();
        // (as the in-game page: once what was held when it started is let go)
        if (!padArmed) { padArmed = held == 0; return; }
        if (held == 0) return;
        var input = System.Numerics.BitOperations.TrailingZeroCount(held);
        var button = listenButton;
        CancelListen();
        opts.BindPad(button, input);
        Save();
    }

    void ListenKey(object sender, KeyEventArgs e)
    {
        if (listen == Listen.None) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { CancelListen(); return; }
        var (button, slot, kind) = (listenButton, listenSlot, listen);
        if (key == Key.Delete)
        {
            CancelListen();
            if (kind == Listen.Pad) opts.BindPad(button, -1); else opts.BindKey(button, slot, 0);
            Save();
            return;
        }
        if (kind != Listen.Key) return;
        var vk = KeyCodes.FromKey(key);
        if (vk == 0)
        {
            BindHint.Text = $"The game cannot use {key} for a control. Pick another key, or Esc to cancel.";
            return;
        }
        CancelListen();
        opts.BindKey(button, slot, vk);
        Save();
    }

    void ListenMouse(object sender, MouseButtonEventArgs e)
    {
        if (listen == Listen.None) return;
        e.Handled = true;
        if (listen == Listen.Pad) { CancelListen(); return; }   // (a click while waiting for the controller: stop waiting)
        var vk = KeyCodes.FromMouse(e.ChangedButton);
        var (button, slot) = (listenButton, listenSlot);
        CancelListen();
        if (vk == 0) return;
        opts.BindKey(button, slot, vk);
        Save();
    }

    // ================= System: language, cards, the folder, starting over =================

    void BuildSystem()
    {
        var (code, all) = GameFolder.Language(folder);
        if (all.Count > 0)
        {
            var box = new ComboBox { Style = (Style)FindResource("ZChoice"), ItemsSource = all };
            box.SelectedItem = all.FirstOrDefault(l => l.Code == code);
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedItem is not GameFolder.LanguageChoice l) return;
                try
                {
                    GameFolder.SetLanguage(folder, l.Code);
                    ShowSaveState($"Language set to {l.Name} in language.txt", problem: false, flash: true);
                }
                catch (Exception x) { ShowSaveState("Could not write language.txt: " + x.Message, problem: true); }
                Refresh();
            };
            SystemRows.Children.Add(RowView("Game language", "The language of the game's text. Kept in language.txt in the game folder; the next start uses it.", box));
        }

        var cards = new TextBlock { Style = (Style)FindResource("ZSoft"), MaxWidth = 320, TextAlignment = TextAlignment.Right,
                                    Text = gpus.Count == 0 ? "Listed after the game's first start" : string.Join("\n", gpus.Select((n, i) => $"{i}: {n}")) };
        SystemRows.Children.Add(RowView("Graphics cards", "The cards with Vulkan 1.3 the game found in its last run. Choose one under Graphics, GPU device.", cards));

        var open = new Button { Style = (Style)FindResource("ZOutline"), Content = "Open folder" };
        open.Click += OpenFolderClick;
        SystemRows.Children.Add(RowView("Game folder", folder, open));

        var saves = new Button { Style = (Style)FindResource("ZOutline"), Content = "Open saves" };
        saves.Click += OpenSavesClick;
        SystemRows.Children.Add(RowView("Saves", "The user folder in the game folder. Copy it somewhere to keep a backup.", saves));

        var log = new Button { Style = (Style)FindResource("ZOutline"), Content = "Open log" };
        log.Click += OpenLogClick;
        SystemRows.Children.Add(RowView("Last run's log", "hookshot_log.txt: attach it to a bug report with your graphics card and where you were in the game.", log));

        var reset = new Button { Style = (Style)FindResource("ZOutline"), Content = "Reset everything" };
        var armed = false;
        var disarm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        disarm.Tick += (_, _) => { disarm.Stop(); armed = false; reset.Content = "Reset everything"; reset.ClearValue(ForegroundProperty); reset.ClearValue(BorderBrushProperty); };
        reset.Click += (_, _) =>
        {
            if (!armed)
            {
                armed = true;
                reset.Content = "Press again to reset";
                reset.Foreground = reset.BorderBrush = (Brush)FindResource("ZRuby");
                disarm.Start();
                return;
            }
            disarm.Stop();
            armed = false;
            reset.Content = "Reset everything";
            reset.ClearValue(ForegroundProperty);
            reset.ClearValue(BorderBrushProperty);
            opts.ResetAll();
            Save();
        };
        SystemRows.Children.Add(RowView("Reset settings", "Every graphics, gameplay and control setting back to the game's defaults. Saves and language are kept.", reset));
    }

    void OpenFolderClick(object sender, RoutedEventArgs e) => Open(folder);
    void OpenSavesClick(object sender, RoutedEventArgs e) => Open(Directory.Exists(GameFolder.SaveFolder(folder)) ? GameFolder.SaveFolder(folder) : folder);
    void OpenLogClick(object sender, RoutedEventArgs e)
    {
        var log = System.IO.Path.Combine(folder, "hookshot_log.txt");
        if (File.Exists(log)) Open(log);
        else ShowSaveState("There is no log yet: the game writes hookshot_log.txt when it runs.", problem: false);
    }

    void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception x) { ShowSaveState("Could not open " + path + ": " + x.Message, problem: true); }
    }

    // ================= the window =================

    void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void CloseClick(object sender, RoutedEventArgs e) => Close();

    /// Fireflies: a few gold lights drifting over the forest (none when Windows asks for less motion)
    void StartFireflies()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        // (the forest drifts closer and back, slowly enough not to be seen moving)
        var slow = new DoubleAnimation(1, 1.06, TimeSpan.FromSeconds(40)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
        Drift.BeginAnimation(ScaleTransform.ScaleXProperty, slow);
        Drift.BeginAnimation(ScaleTransform.ScaleYProperty, slow);
        var rng = new Random(7);
        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        for (var i = 0; i < 22; i++)
        {
            var size = 2 + rng.NextDouble() * 3.5;
            var green = rng.NextDouble() < 0.35;
            var color = green ? Color.FromRgb(0xB8, 0xF0, 0x9A) : Color.FromRgb(0xF6, 0xDE, 0x97);
            var dot = new Ellipse
            {
                Width = size * 4, Height = size * 4,
                Fill = new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromArgb(0xFF, color.R, color.G, color.B), 0), new(Color.FromArgb(0x66, color.R, color.G, color.B), 0.22),
                    new(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
                }),
                Opacity = 0,
            };
            var x = 252 + rng.NextDouble() * (w - 252);   // (over the page, not the side)
            var y = rng.NextDouble() * h * 0.9;
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            Fireflies.Children.Add(dot);
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var drift = TimeSpan.FromSeconds(7 + rng.NextDouble() * 9);
            dot.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(x, x + (rng.NextDouble() - 0.5) * 160, drift) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            dot.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(y, y + (rng.NextDouble() - 0.5) * 120, drift * 0.8) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.25 + rng.NextDouble() * 0.6, TimeSpan.FromSeconds(1.5 + rng.NextDouble() * 2.5))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(rng.NextDouble() * 3), EasingFunction = ease,
            });
        }
    }
}
