using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using SftpExplorerWinUI.Helpers;
using System;
using Windows.UI.ViewManagement;

namespace SftpExplorerWinUI.Controls;

public sealed partial class ConnectionProtocolReveal : UserControl
{
    private readonly UISettings _uiSettings;
    private bool _animationsEnabled;
    private bool _isAnimationsChangedSubscribed;
    private string _currentState = "Normal";
    private Storyboard? _waveStoryboard;
    private readonly PointerEventHandler _rootPointerPressedHandler;
    private UIElement? _rootContent;
    private bool _touchChooserOpen;
    private bool _suppressNextTouchClick;

    public event EventHandler? SftpRequested;
    public event EventHandler? SshRequested;

    public ConnectionProtocolReveal()
    {
        InitializeComponent();

        _rootPointerPressedHandler = RootContent_PointerPressed;
        _uiSettings = new UISettings();
        _animationsEnabled = _uiSettings.AnimationsEnabled;
        AutomationProperties.SetName(
            SftpButton,
            LocalizationHelper.GetString("OpenSftpConnection"));
        AutomationProperties.SetName(
            SshButton,
            LocalizationHelper.GetString("OpenSshTerminal"));

        Loaded += ConnectionProtocolReveal_Loaded;
        Unloaded += ConnectionProtocolReveal_Unloaded;
    }

    private void ConnectionProtocolReveal_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isAnimationsChangedSubscribed)
        {
            _uiSettings.AnimationsEnabledChanged += UISettings_AnimationsEnabledChanged;
            _isAnimationsChangedSubscribed = true;
            ApplyAnimationsSetting(_uiSettings.AnimationsEnabled);
        }

        if (_rootContent is not null || XamlRoot?.Content is not UIElement rootContent)
            return;

        _rootContent = rootContent;
        _rootContent.AddHandler(
            UIElement.PointerPressedEvent,
            _rootPointerPressedHandler,
            handledEventsToo: true);
    }

    private void ConnectionProtocolReveal_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_isAnimationsChangedSubscribed)
        {
            _uiSettings.AnimationsEnabledChanged -= UISettings_AnimationsEnabledChanged;
            _isAnimationsChangedSubscribed = false;
        }

        if (_rootContent is not null)
        {
            _rootContent.RemoveHandler(UIElement.PointerPressedEvent, _rootPointerPressedHandler);
            _rootContent = null;
        }

        _currentState = "Normal";
        ApplyStateWithoutVisualStateManager(_currentState);

        _touchChooserOpen = false;
        _suppressNextTouchClick = false;
    }

    private void UISettings_AnimationsEnabledChanged(
        UISettings sender,
        UISettingsAnimationsEnabledChangedEventArgs args)
    {
        var animationsEnabled = sender.AnimationsEnabled;
        _ = DispatcherQueue.TryEnqueue(() => ApplyAnimationsSetting(animationsEnabled));
    }

    private void ApplyAnimationsSetting(bool animationsEnabled)
    {
        _animationsEnabled = animationsEnabled;
        if (animationsEnabled)
        {
            ClearDirectStateValues();
            VisualStateManager.GoToState(this, _currentState, useTransitions: true);
            return;
        }

        ApplyStateWithoutVisualStateManager(_currentState);
    }

    private void RevealRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (IsTouch(e))
            return;

        CloseTouchChooser();

        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null && source != RevealRoot)
        {
            if (source == SftpButton)
            {
                SetProtocolState("Sftp");
                return;
            }

            if (source == SshButton)
            {
                SetProtocolState("Ssh");
                return;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        var position = e.GetCurrentPoint(RevealRoot).Position;
        SetProtocolState(position.X < RevealRoot.ActualWidth / 2 ? "Sftp" : "Ssh");
    }

    private void SftpButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTouch(e))
            SetProtocolState("Sftp");
    }

    private void SftpButton_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        HandleProtocolPointerPressed(e, "Sftp");

    private void SshButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!IsTouch(e))
            SetProtocolState("Ssh");
    }

    private void SshButton_PointerPressed(object sender, PointerRoutedEventArgs e) =>
        HandleProtocolPointerPressed(e, "Ssh");

    private void HandleProtocolPointerPressed(PointerRoutedEventArgs e, string protocolState)
    {
        if (!IsTouch(e))
        {
            SetProtocolState(protocolState);
            return;
        }

        if (!_touchChooserOpen)
        {
            _touchChooserOpen = true;
            _suppressNextTouchClick = true;
            SetProtocolState("Touch");
            return;
        }

        _suppressNextTouchClick = false;
        SetProtocolState(protocolState);
    }

    private void RevealRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_touchChooserOpen)
            return;

        if (!SftpButton.FocusState.HasFlag(FocusState.Keyboard) &&
            !SshButton.FocusState.HasFlag(FocusState.Keyboard))
        {
            SetProtocolState("Normal");
        }
    }

    private void SftpButton_GotFocus(object sender, RoutedEventArgs e) => SetProtocolState("Sftp");

    private void SshButton_GotFocus(object sender, RoutedEventArgs e) => SetProtocolState("Ssh");

    private void ProtocolButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (SftpButton.FocusState == FocusState.Unfocused &&
            SshButton.FocusState == FocusState.Unfocused)
        {
            SetProtocolState("Normal");
        }
    }

    private void SftpButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedTouchClick())
            return;

        _touchChooserOpen = false;
        SftpRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SshButton_Click(object sender, RoutedEventArgs e)
    {
        if (ConsumeSuppressedTouchClick())
            return;

        _touchChooserOpen = false;
        SshRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool ConsumeSuppressedTouchClick()
    {
        if (!_suppressNextTouchClick)
            return false;

        _suppressNextTouchClick = false;
        return true;
    }

    private void RootContent_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_touchChooserOpen || !IsTouch(e) || IsDescendantOfThis(e.OriginalSource as DependencyObject))
            return;

        CloseTouchChooser();
    }

    private bool IsDescendantOfThis(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, this))
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void CloseTouchChooser()
    {
        if (!_touchChooserOpen)
            return;

        _touchChooserOpen = false;
        _suppressNextTouchClick = false;
        SetProtocolState("Normal");
    }

    private static bool IsTouch(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch;

    private void SetProtocolState(string state)
    {
        if (_currentState == state)
            return;

        var previousState = _currentState;
        _currentState = state;
        if (_animationsEnabled)
        {
            ClearDirectStateValues();
            VisualStateManager.GoToState(this, state, useTransitions: true);
        }
        else
        {
            ApplyStateWithoutVisualStateManager(state);
        }

        AnimateWave(previousState, state);
    }

    // Calling VisualStateManager.GoToState with transitions disabled can crash
    // WinUI when the user disables system animations. Keep this path entirely
    // outside VisualStateManager and assign the same values as the XAML states.
    private void ApplyStateWithoutVisualStateManager(string state)
    {
        _waveStoryboard?.Stop();
        _waveStoryboard = null;

        var protocolVisible = state != "Normal";
        BlurLayer.Opacity = protocolVisible ? 0.96d : 0d;
        BlurVeil.Opacity = protocolVisible ? 0.20d : 0d;
        AccentWash.Opacity = protocolVisible ? 0.035d : 0d;

        var sftpVisible = state is "Sftp" or "Touch";
        SftpLabel.Opacity = sftpVisible ? 1d : 0d;
        SftpTranslate.X = sftpVisible ? 0d : 40d;

        var sshVisible = state is "Ssh" or "Touch";
        SshLabel.Opacity = sshVisible ? 1d : 0d;
        SshTranslate.X = sshVisible ? 0d : -40d;

        WaveTranslate.X = GetWaveOffset(state);
        WaveLayer.Opacity = IsWaveVisible(state) ? 0.12d : 0d;
    }

    // Direct assignments above are local dependency-property values. Release
    // them before handing control back to XAML visual states after animations
    // have been re-enabled in Windows.
    private void ClearDirectStateValues()
    {
        BlurLayer.ClearValue(UIElement.OpacityProperty);
        BlurVeil.ClearValue(UIElement.OpacityProperty);
        AccentWash.ClearValue(UIElement.OpacityProperty);
        SftpLabel.ClearValue(UIElement.OpacityProperty);
        SftpTranslate.ClearValue(TranslateTransform.XProperty);
        SshLabel.ClearValue(UIElement.OpacityProperty);
        SshTranslate.ClearValue(TranslateTransform.XProperty);
    }

    private void AnimateWave(string previousState, string state)
    {
        var fromX = WaveTranslate.X;
        var targetX = GetWaveOffset(state);
        var fromOpacity = WaveLayer.Opacity;
        var targetOpacity = IsWaveVisible(state) ? 0.12d : 0d;

        _waveStoryboard?.Stop();
        _waveStoryboard = null;

        if (!_animationsEnabled)
        {
            WaveTranslate.X = targetX;
            WaveLayer.Opacity = targetOpacity;
            return;
        }

        var duration = state != "Normal" && previousState != "Normal"
            ? TimeSpan.FromMilliseconds(333)
            : TimeSpan.FromMilliseconds(167);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var translation = new DoubleAnimation
        {
            From = fromX,
            To = targetX,
            Duration = duration,
            EasingFunction = easing,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(translation, WaveTranslate);
        Storyboard.SetTargetProperty(translation, nameof(TranslateTransform.X));

        var opacity = new DoubleAnimation
        {
            From = fromOpacity,
            To = targetOpacity,
            Duration = duration,
            EasingFunction = easing
        };
        Storyboard.SetTarget(opacity, WaveLayer);
        Storyboard.SetTargetProperty(opacity, nameof(Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(translation);
        storyboard.Children.Add(opacity);
        _waveStoryboard = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_waveStoryboard, storyboard))
                return;

            WaveTranslate.X = targetX;
            WaveLayer.Opacity = targetOpacity;
            storyboard.Stop();
            _waveStoryboard = null;
        };
        storyboard.Begin();
    }

    private double GetWaveOffset(string state) => state switch
    {
        "Sftp" => -ActualWidth * 0.25,
        "Ssh" => ActualWidth * 0.25,
        _ => 0
    };

    private static bool IsWaveVisible(string state) => state is "Sftp" or "Ssh";
}
